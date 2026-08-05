using EngrCAD.Core.Spatial;

namespace EngrCAD.Core.Geometry2;

/// <summary>
/// EXACT boolean set operations on <see cref="CurvedRegion2d"/>s — union, intersection,
/// difference — built on <see cref="CurvedArrangement2d"/>. Arcs go in and arcs come out:
/// nothing is flattened at any point, so a disc minus a chord measures its closed-form area
/// and the result's boundary is still made of circles.
///
/// <para><b>How it works</b> — the same machine as <see cref="Region2dBoolean"/>, with the
/// two changes curves force. Both operands' chains go into one arrangement, which splits
/// every contact and dedupes coincident carriers; <see cref="CurvedArrangement2d.ExtractCells"/>
/// carves the plane into interior-disjoint cells; each cell is classified once at a point
/// provably strictly inside it; the kept cells' DIRECTED EDGE IDS are collected (the
/// polygonal version keys on vertex pairs, which curves cannot — two vertices are joined by
/// two different arcs of the same circle, and by a chord as well); an edge with kept cells
/// on both sides is interior and disappears; and what remains is re-traced with the
/// arrangement's own counter-clockwise fan order so kept material always stays on the
/// left.</para>
///
/// <para><b>The interior sample</b> takes the boundary edge whose midpoint has the greatest
/// clearance and pushes half that clearance along the inward normal — with one addition the
/// straight version did not need: the push is also capped by the edge's own CURVATURE
/// RADIUS. Without it, a small circular hole inside a large cell would send the sample
/// straight past the circle's centre and out the far side; with it, the pushed point is at
/// distance |r ∓ s| from the centre with 0 &lt; s &lt; r, so it is off the carrier circle by
/// exactly s and off every other edge by more, which is the same proof the straight case
/// gives with an infinite radius.</para>
///
/// <para><b>Classification is parity, not <c>Contains</c>.</b> The sample is provably off
/// every boundary, so the operand test is the epsilon-free
/// <see cref="CurvedRegion2d.ParityInside(in Vector2d)"/> rather than the closed-set
/// <see cref="CurvedRegion2d.Contains"/>, whose on-boundary band would answer "inside both"
/// for a sample in a cell thinner than the weld tolerance.</para>
///
/// <para><b>Traced loops are re-merged.</b> Consecutive result edges sharing a carrier and a
/// travel direction fuse back into one (<see cref="MergeChain"/>) — the curved counterpart
/// of <c>Region2d.WithoutCollinearVertices</c>, and what makes a disc come back as one
/// full-circle edge rather than as the two half-arcs the arrangement had to split it into.</para>
/// </summary>
public static class CurvedRegion2dBoolean
{
    /// <summary>Default vertex merge distance — the 1e-9 absolute weld tier, matching
    /// <see cref="CurvedArrangement2d"/>'s own default.</summary>
    public const double DefaultSnapTolerance = 1e-9;

    private enum Op
    {
        Union,
        Intersection,
        Difference,
    }

    /// <summary>Everything covered by <paramref name="a"/> or <paramref name="b"/>.</summary>
    public static IReadOnlyList<CurvedRegion2d> Union(
        CurvedRegion2d a, CurvedRegion2d b, double snapTolerance = DefaultSnapTolerance) =>
        Combine([a], [b], Op.Union, snapTolerance);

    /// <summary>Everything covered by both <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static IReadOnlyList<CurvedRegion2d> Intersection(
        CurvedRegion2d a, CurvedRegion2d b, double snapTolerance = DefaultSnapTolerance) =>
        Combine([a], [b], Op.Intersection, snapTolerance);

    /// <summary><paramref name="a"/> with <paramref name="b"/> cut away.</summary>
    public static IReadOnlyList<CurvedRegion2d> Difference(
        CurvedRegion2d a, CurvedRegion2d b, double snapTolerance = DefaultSnapTolerance) =>
        Combine([a], [b], Op.Difference, snapTolerance);

    /// <summary>Everything covered by any region of either set.</summary>
    public static IReadOnlyList<CurvedRegion2d> Union(
        IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b,
        double snapTolerance = DefaultSnapTolerance) =>
        Combine(a, b, Op.Union, snapTolerance);

    /// <summary>Everything covered by both region sets.</summary>
    public static IReadOnlyList<CurvedRegion2d> Intersection(
        IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b,
        double snapTolerance = DefaultSnapTolerance) =>
        Combine(a, b, Op.Intersection, snapTolerance);

    /// <summary>The region set <paramref name="a"/> with everything in <paramref name="b"/> cut away.</summary>
    public static IReadOnlyList<CurvedRegion2d> Difference(
        IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b,
        double snapTolerance = DefaultSnapTolerance) =>
        Combine(a, b, Op.Difference, snapTolerance);

    /// <summary>
    /// Union of MANY regions, folded as a BALANCED TREE rather than a linear accumulate —
    /// the same reasoning as <see cref="Region2dBoolean.UnionAll"/>: one arrangement is
    /// quadratic in its total input, so halving recursively keeps most arrangements small
    /// and lets each merge discard its children's interior edges before climbing.
    /// </summary>
    public static IReadOnlyList<CurvedRegion2d> UnionAll(
        IReadOnlyList<CurvedRegion2d> regions, double snapTolerance = DefaultSnapTolerance)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return regions.Count == 0 ? [] : UnionRange(regions, 0, regions.Count, snapTolerance);
    }

    private static IReadOnlyList<CurvedRegion2d> UnionRange(
        IReadOnlyList<CurvedRegion2d> regions, int start, int end, double snapTolerance)
    {
        if (end - start == 1)
            return [regions[start]];
        int middle = start + (end - start) / 2;
        return Combine(
            UnionRange(regions, start, middle, snapTolerance),
            UnionRange(regions, middle, end, snapTolerance),
            Op.Union, snapTolerance);
    }

    // ---- the machine ----

    private static IReadOnlyList<CurvedRegion2d> Combine(
        IReadOnlyList<CurvedRegion2d> a, IReadOnlyList<CurvedRegion2d> b, Op op, double snapTolerance)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var arrangement = new CurvedArrangement2d(snapTolerance);
        foreach (var region in a.Concat(b))
            arrangement.Insert(region);
        if (arrangement.EdgeCount == 0)
            return [];

        var index = new EdgeIndex(arrangement);
        var parityA = ParityIndex.For(a);
        var parityB = ParityIndex.For(b);
        var kept = new HashSet<int>();
        foreach (var cell in arrangement.ExtractCells())
        {
            var sample = InteriorSample(arrangement, index, cell);
            bool inA = parityA.Inside(sample);
            bool inB = parityB.Inside(sample);
            bool keep = op switch
            {
                Op.Union => inA || inB,
                Op.Intersection => inA && inB,
                _ => inA && !inB,
            };
            if (!keep)
                continue;
            foreach (int directed in cell.OuterDirected)
                kept.Add(directed);
            foreach (var hole in cell.HolesDirected)
            {
                foreach (int directed in hole)
                    kept.Add(directed);
            }
        }

        // A directed edge whose reverse is also kept has kept cells on both sides: interior
        // to the result, so it disappears. Sorting keeps the trace start deterministic.
        var boundaryOrder = kept.Where(d => !kept.Contains(d ^ 1)).ToList();
        boundaryOrder.Sort();
        var boundary = new HashSet<int>(boundaryOrder);
        if (boundary.Count == 0)
            return [];

        var fans = arrangement.BuildCcwEdgeFans();
        var remaining = new HashSet<int>(boundary);
        var loops = new List<IReadOnlyList<CurvedEdge2d>>();
        foreach (int start in boundaryOrder)
        {
            if (!remaining.Contains(start))
                continue;
            var loop = new List<CurvedEdge2d>();
            int current = start;
            int guard = boundary.Count + 1;
            do
            {
                remaining.Remove(current);
                loop.Add(arrangement.DirectedEdgeAt(current));
                current = NextBoundaryEdge(arrangement, fans, boundary, current);
                if (--guard < 0)
                    throw new InvalidOperationException("Curved region boolean boundary tracing did not close.");
            }
            while (current != start);
            loops.Add(MergeChain(loop, snapTolerance));
        }
        return CurvedRegion2d.FromLoops(loops);
    }

    private static bool ParityInside(IReadOnlyList<CurvedRegion2d> regions, in Vector2d point)
    {
        foreach (var region in regions)
        {
            if (region.ParityInside(point))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Set to false to make cell classification walk every operand edge per cell — the
    /// unindexed form, kept as an A/B seam so <c>CurvedRegion2dBooleanBenchmark</c> can
    /// measure the index rather than assert it. Output is identical either way (see
    /// <see cref="ParityIndex"/>).
    /// </summary>
    internal static bool UseParityIndex { get; set; } = true;

    /// <summary>
    /// A <see cref="Bvh"/> over ONE operand set's boundary edges, built once per boolean, so
    /// classifying a cell costs O(log n + crossing candidates) rather than a walk over every
    /// edge of every region.
    ///
    /// <para><b>It is result-identical by construction, not by tolerance.</b> Parity is a
    /// COUNT of +x ray crossings, and an edge whose tight box does not reach the query ray
    /// contributes exactly zero to it — so restricting the sum to the box query's candidates
    /// removes only terms that are zero. The count is also order-free (integers), so the
    /// summation order the tree visits in cannot move the answer by an ulp the way a float
    /// sum could.</para>
    ///
    /// <para><b>Why it exists here when the polygonal twin's was declined.</b> The polygonal
    /// index measured 1.0× on a bulk union, because an overlap-heavy union's balanced fold
    /// keeps the CELL count tiny exactly where the operand edge counts grow. The workload
    /// that carries the product is one whose result KEEPS many cells against many-edge
    /// operands — see <c>CurvedRegion2dBooleanBenchmark</c>, which builds it deliberately
    /// (N horizontal stadiums intersected with N vertical ones, so cells grow as N² against
    /// O(N) edges) and reports the ratio.</para>
    /// </summary>
    private sealed class ParityIndex
    {
        private readonly IReadOnlyList<CurvedRegion2d> _regions;
        private readonly CurvedEdge2d[] _edges;
        private readonly int[] _regionOf;
        private readonly Bvh? _bvh;
        private readonly double _maxX;
        private readonly int[] _crossings;
        private readonly int[] _stamp;
        private readonly List<int> _candidates = [];
        private int _query;

        /// <summary>Edge count below which the walk beats building a tree — the same
        /// threshold, and the same reason, as <c>Region2dValidation.BruteForceLimit</c>.</summary>
        private const int MinIndexedEdges = 24;

        private ParityIndex(
            IReadOnlyList<CurvedRegion2d> regions, CurvedEdge2d[] edges, int[] regionOf,
            Bvh? bvh, double maxX)
        {
            _regions = regions;
            _edges = edges;
            _regionOf = regionOf;
            _bvh = bvh;
            _maxX = maxX;
            _crossings = new int[regions.Count];
            _stamp = new int[regions.Count];
        }

        public static ParityIndex For(IReadOnlyList<CurvedRegion2d> regions)
        {
            int count = 0;
            foreach (var region in regions)
            {
                foreach (var loop in region.AllLoops())
                    count += loop.Count;
            }
            if (!UseParityIndex || count < MinIndexedEdges)
                return new ParityIndex(regions, [], [], null, 0);

            var edges = new CurvedEdge2d[count];
            var regionOf = new int[count];
            var boxes = new Aabb[count];
            double maxX = double.NegativeInfinity;
            int next = 0;
            for (int r = 0; r < regions.Count; r++)
            {
                foreach (var loop in regions[r].AllLoops())
                {
                    foreach (var edge in loop)
                    {
                        edges[next] = edge;
                        regionOf[next] = r;
                        boxes[next] = edge.Bounds();
                        maxX = Math.Max(maxX, boxes[next].Max.X);
                        next++;
                    }
                }
            }
            return new ParityIndex(regions, edges, regionOf, Bvh.Build(boxes), maxX);
        }

        /// <summary>
        /// Is the point inside ANY member region? Parity is accumulated PER REGION, never
        /// across the whole set: two members of one operand list may legitimately overlap,
        /// and a combined count would XOR their interiors where the set union does not.
        /// </summary>
        public bool Inside(in Vector2d point)
        {
            if (_bvh is null)
                return ParityInside(_regions, point);
            // The ray is +x from the point, so only edges reaching past it in x and
            // straddling its y can cross it; everything else contributes exactly 0 — which
            // is why restricting the sum cannot change it.
            var query = new Aabb(
                new Vector3d(point.X, point.Y, 0),
                new Vector3d(Math.Max(_maxX, point.X), point.Y, 0));
            _candidates.Clear();
            _bvh.Query(query, _candidates);

            int stamp = ++_query;
            bool inside = false;
            foreach (int index in _candidates)
            {
                int region = _regionOf[index];
                if (_stamp[region] != stamp)
                {
                    _stamp[region] = stamp;
                    _crossings[region] = 0;
                }
                _crossings[region] += _edges[index].RayCrossings(point);
            }
            foreach (int index in _candidates)
            {
                int region = _regionOf[index];
                if ((_crossings[region] & 1) != 0)
                {
                    inside = true;
                    break;
                }
            }
            return inside;
        }
    }

    /// <summary>
    /// The next boundary edge after <paramref name="current"/>: at its head vertex, scan the
    /// counter-clockwise fan CLOCKWISE from the reverse direction and take the first edge
    /// that is still a boundary edge. Every edge skipped on the way has kept cells on both
    /// sides, so the edge found has kept material on its left.
    /// </summary>
    private static int NextBoundaryEdge(
        CurvedArrangement2d arrangement, int[][] fans, HashSet<int> boundary, int current)
    {
        int edge = current >> 1;
        var (a, b) = arrangement.EdgeVerticesAt(edge);
        int vertex = (current & 1) == 0 ? b : a;
        var fan = fans[vertex];
        int reverseIndex = Array.IndexOf(fan, edge);
        if (reverseIndex < 0)
            throw new InvalidOperationException("Curved region boolean boundary tracing lost its reverse edge.");

        for (int step = 1; step <= fan.Length; step++)
        {
            int index = ((reverseIndex - step) % fan.Length + fan.Length) % fan.Length;
            int next = arrangement.DirectedFrom(fan[index], vertex);
            if (boundary.Contains(next))
                return next;
        }
        throw new InvalidOperationException("Curved region boolean boundary tracing hit a dead end.");
    }

    /// <summary>
    /// Fuses consecutive edges of a closed chain that share a carrier and a travel direction
    /// — collinear segments become one segment, cocircular arcs become one arc, and the two
    /// halves an arrangement had to split a full circle into become the circle again.
    /// The point set is unchanged, exactly as for
    /// <see cref="Region2d.WithoutCollinearVertices"/>; only the intermediate joint (which
    /// may still be a T-junction for geometry OUTSIDE the result) stops being a chain break.
    /// </summary>
    public static IReadOnlyList<CurvedEdge2d> MergeChain(
        IReadOnlyList<CurvedEdge2d> chain, double tolerance = DefaultSnapTolerance)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var edges = new List<CurvedEdge2d>(chain);
        bool changed = true;
        while (changed && edges.Count > 1)
        {
            changed = false;
            for (int i = 0; i < edges.Count && edges.Count > 1;)
            {
                int j = (i + 1) % edges.Count;
                if (i == j || !TryFuse(edges[i], edges[j], tolerance, out var fused))
                {
                    i++;
                    continue;
                }
                edges[i] = fused;
                edges.RemoveAt(j);
                changed = true;
                if (j < i)
                    break;   // the wrap-around merge shifted everything; restart the sweep
            }
        }
        return edges;
    }

    private static bool TryFuse(
        in CurvedEdge2d first, in CurvedEdge2d second, double tolerance, out CurvedEdge2d fused)
    {
        fused = default;
        if (first.Kind != second.Kind)
            return false;
        // Two consecutive pieces of one cubic are NOT restrictions of one another, so
        // SameCarrier cannot answer for them; TryFuseCubics carries its own verification and
        // it is the stronger one (a full control-polygon comparison).
        if (first.IsBezier)
            return TryFuseCubics(first, second, tolerance, out fused);
        if (!CurveIntersection2d.SameCarrier(first, second, tolerance))
            return false;
        // Same direction of travel, so a 180-degree reversal (a slit) is never fused away.
        if (first.TangentAt(1).Dot(second.TangentAt(0)) <= 0)
            return false;

        if (!first.IsArc)
        {
            fused = CurvedEdge2d.Line(first.Start, second.End);
            return true;
        }
        if (Math.Sign(first.SweepAngle) != Math.Sign(second.SweepAngle))
            return false;
        double sweep = first.SweepAngle + second.SweepAngle;
        // Angular round-off slack on a dimensionless quantity: the fused arc may reach a
        // full turn (a circle) but never wrap past it.
        if (Math.Abs(sweep) > 2 * Math.PI + 1e-12)
            return false;
        fused = CurvedEdge2d
            .Arc(first.Center, first.Radius, first.StartAngle, Math.Clamp(sweep, -2 * Math.PI, 2 * Math.PI))
            .WithEndpoints(first.Start, second.End);
        return true;
    }

    /// <summary>
    /// Fuses two consecutive cubic pieces of ONE cubic back into it.
    ///
    /// <para>The split point's parameter on the fused curve is recoverable in closed form:
    /// a restriction to [0, u] scales the end derivative by u and one to [u, 1] scales it by
    /// (1 − u), so <c>u = |first′(1)| / (|first′(1)| + |second′(0)|)</c> — no search and no
    /// second tolerance. Extrapolating <paramref name="first"/> from [0, u] back to [0, 1] is
    /// then the ordinary Hermite construction, and the candidate is ACCEPTED only if its own
    /// restriction to [u, 1] reproduces <paramref name="second"/>'s control polygon within
    /// <paramref name="tolerance"/>. So a fuse can never quietly change the curve: the
    /// verification is a comparison of POINTS, and a pair that is merely nearly collinear in
    /// parameter simply fails it and stays two edges.</para>
    /// </summary>
    private static bool TryFuseCubics(
        in CurvedEdge2d first, in CurvedEdge2d second, double tolerance, out CurvedEdge2d fused)
    {
        fused = default;
        double d1 = first.DerivativeAt(1).Length;
        double d2 = second.DerivativeAt(0).Length;
        double total = d1 + d2;
        // Exact-zero guard: a cusp at the joint carries no parameter ratio.
        if (!(total > 0) || !(d1 > 0) || !(d2 > 0))
            return false;
        double u = d1 / total;

        // first = F|[0, u], so F is first re-parameterized by tau -> tau/u: same start, same
        // start derivative scaled by 1/u, and the far end read off second.
        var candidate = CurvedEdge2d.Bezier(
            first.Start,
            first.Start + first.DerivativeAt(0) / (3 * u),
            second.End - second.DerivativeAt(1) / (3 * (1 - u)),
            second.End);

        var (q0, q1, q2, q3) = candidate.Sub(0, u).ControlPoints;
        var (p0, p1, p2, p3) = first.ControlPoints;
        if (q0.DistanceTo(p0) > tolerance || q1.DistanceTo(p1) > tolerance
            || q2.DistanceTo(p2) > tolerance || q3.DistanceTo(p3) > tolerance)
        {
            return false;
        }
        var (r0, r1, r2, r3) = candidate.Sub(u, 1).ControlPoints;
        var (s0, s1, s2, s3) = second.ControlPoints;
        if (r0.DistanceTo(s0) > tolerance || r1.DistanceTo(s1) > tolerance
            || r2.DistanceTo(s2) > tolerance || r3.DistanceTo(s3) > tolerance)
        {
            return false;
        }
        fused = candidate;
        return true;
    }

    /// <summary>
    /// A point strictly inside a cell: the outer-chain edge midpoint with the greatest
    /// usable push, moved half that push along the inward (left) normal. The push is
    /// <c>min(clearance to any other edge, the edge's own curvature radius)</c> — see the
    /// class remarks for why the curvature cap is what makes the straight version's proof
    /// survive the move to arcs.
    /// </summary>
    private static Vector2d InteriorSample(
        CurvedArrangement2d arrangement, EdgeIndex index, CurvedCell2d cell)
    {
        var sample = default(Vector2d);
        double bestStep = 0;

        for (int i = 0; i < cell.OuterDirected.Count; i++)
        {
            int directed = cell.OuterDirected[i];
            var edge = cell.Outer[i];
            var midpoint = edge.MidPoint;
            double clearance = index.NearestDistance(midpoint, directed >> 1);
            if (double.IsInfinity(clearance))
                continue;
            // The curvature cap is asked as ONE rule: it is +infinity for a straight edge (so
            // the push is the straight case's, bit for bit), the arc's own radius, and 1/|κ|
            // at the midpoint of a cubic.
            double reach = Math.Min(clearance, edge.CurvatureRadiusAt(0.5));
            double step = 0.5 * reach;
            if (!(step > bestStep))
                continue;
            bestStep = step;
            // The left normal of the direction of travel; a counter-clockwise outer chain
            // keeps the cell on its left.
            sample = midpoint + edge.TangentAt(0.5).Perpendicular * step;
        }

        if (bestStep > 0)
            return sample;

        // Unreachable for a real cell (a bounded face always has other edges around it);
        // the chain's start-point centroid keeps the classifier answering rather than throwing.
        var centroid = Vector2d.Zero;
        foreach (var edge in cell.Outer)
            centroid += edge.Start;
        return centroid / cell.Outer.Count;
    }

    /// <summary>
    /// A <see cref="Bvh"/> over the arrangement's edges, built ONCE per boolean. Edges are
    /// embedded at z = 0 so the box distance the branch-and-bound prunes with is exactly the
    /// 2D one, and an arc's box is its TIGHT box (cardinal extremes inside the sweep), which
    /// keeps the bound valid and sharp.
    /// </summary>
    private sealed class EdgeIndex
    {
        private readonly CurvedEdge2d[] _edges;
        private readonly Bvh _bvh;

        public EdgeIndex(CurvedArrangement2d arrangement)
        {
            int count = arrangement.EdgeCount;
            _edges = new CurvedEdge2d[count];
            var boxes = new Aabb[count];
            for (int e = 0; e < count; e++)
            {
                _edges[e] = arrangement.EdgeAt(e);
                boxes[e] = _edges[e].Bounds();
            }
            _bvh = Bvh.Build(boxes);
        }

        /// <summary>Distance to the nearest edge other than <paramref name="skip"/>;
        /// positive infinity when there is no other edge.</summary>
        public double NearestDistance(in Vector2d point, int skip)
        {
            var metric = new EdgeDistance(_edges, point, skip);
            _bvh.Nearest(new Vector3d(point.X, point.Y, 0), ref metric, out _, out double distance);
            return distance;
        }

        private readonly struct EdgeDistance(CurvedEdge2d[] edges, Vector2d point, int skip) : IBvhDistance
        {
            public double DistanceTo(int item) =>
                item == skip ? double.PositiveInfinity : edges[item].DistanceTo(point);
        }
    }
}
