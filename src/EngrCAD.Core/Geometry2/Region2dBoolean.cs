using EngrCAD.Core.Spatial;

namespace EngrCAD.Core.Geometry2;

/// <summary>
/// Boolean set operations on <see cref="Region2d"/>s — union, intersection, difference —
/// built on <see cref="Arrangement2d"/>.
///
/// <para><b>How it works.</b> Both operands' loops go into one arrangement, which splits
/// every crossing and dedupes coincident edges, then <see cref="Arrangement2d.ExtractCells"/>
/// carves the plane into interior-disjoint cells. Each cell is classified once — is a point
/// strictly inside it in A? in B? — and kept or dropped per the operation. The kept cells'
/// directed loop edges are collected; an edge with kept cells on BOTH sides is internal and
/// disappears, leaving exactly the boundary of the result, which is re-traced with the
/// arrangement's own counter-clockwise fan order (<see cref="Arrangement2d.BuildCcwEdgeFans"/>)
/// so kept material always stays on the left. The resulting loops go through
/// <see cref="Region2d.FromLoops"/>, which re-derives the nesting — so a hole CREATED by the
/// operation (square minus a centred square) is detected exactly like any other.</para>
///
/// <para><b>Interior sample points.</b> Classification needs a point provably strictly
/// inside a cell, never on any boundary. For each edge of the cell's outer loop we take the
/// midpoint m and the clearance d = the distance from m to the nearest OTHER arrangement
/// edge; the open disk of radius d around m meets no edge but the one m sits on, so the
/// inward half-disk lies wholly in the cell, and m + d/2 along the inward (left) normal is
/// strictly interior. The edge with the LARGEST clearance is used, which keeps the sample as
/// far from every boundary as this construction can put it. The choice is scale-free (no
/// epsilon, no shrink factor) and needs no polygon triangulation; since the cell boundary
/// contains every edge of both operands, the sample can never land on A's or B's boundary,
/// so the closed/open convention of <see cref="Region2d.Contains"/> never matters here.
/// The clearance itself comes from a <see cref="Bvh"/> over the arrangement's edges, built
/// once per boolean — it replaced a per-loop-edge linear scan that made classification
/// O(E²), and it is bit-identical because only the minimum DISTANCE is ever used, never
/// which edge attained it.</para>
///
/// <para><b>Inputs</b> may be single regions or lists (a list means the union of its
/// members); results are always canonical (CCW outer, CW holes) and an empty result is an
/// empty list. Curved input must be flattened before it arrives (see
/// <c>Sketch.ToRegions</c>): the arrangement is polygonal.</para>
/// </summary>
public static class Region2dBoolean
{
    /// <summary>Default vertex merge distance — the 1e-9 absolute weld tier, matching
    /// <see cref="Arrangement2d"/>'s own default (a default parameter must be a constant,
    /// so it cannot reference <c>Tolerance.Default.Linear</c> directly).</summary>
    public const double DefaultSnapTolerance = 1e-9;

    private enum Op
    {
        Union,
        Intersection,
        Difference,
    }

    /// <summary>Everything covered by <paramref name="a"/> or <paramref name="b"/>.</summary>
    public static IReadOnlyList<Region2d> Union(
        Region2d a, Region2d b, double snapTolerance = DefaultSnapTolerance) =>
        Combine([a], [b], Op.Union, snapTolerance);

    /// <summary>Everything covered by both <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static IReadOnlyList<Region2d> Intersection(
        Region2d a, Region2d b, double snapTolerance = DefaultSnapTolerance) =>
        Combine([a], [b], Op.Intersection, snapTolerance);

    /// <summary><paramref name="a"/> with <paramref name="b"/> cut away.</summary>
    public static IReadOnlyList<Region2d> Difference(
        Region2d a, Region2d b, double snapTolerance = DefaultSnapTolerance) =>
        Combine([a], [b], Op.Difference, snapTolerance);

    /// <summary>Everything covered by any region of <paramref name="a"/> or <paramref name="b"/>
    /// (each list is read as the union of its members, so overlapping members are merged too).</summary>
    public static IReadOnlyList<Region2d> Union(
        IReadOnlyList<Region2d> a, IReadOnlyList<Region2d> b, double snapTolerance = DefaultSnapTolerance) =>
        Combine(a, b, Op.Union, snapTolerance);

    /// <summary>Everything covered by both region sets.</summary>
    public static IReadOnlyList<Region2d> Intersection(
        IReadOnlyList<Region2d> a, IReadOnlyList<Region2d> b, double snapTolerance = DefaultSnapTolerance) =>
        Combine(a, b, Op.Intersection, snapTolerance);

    /// <summary>The region set <paramref name="a"/> with everything in <paramref name="b"/> cut away.</summary>
    public static IReadOnlyList<Region2d> Difference(
        IReadOnlyList<Region2d> a, IReadOnlyList<Region2d> b, double snapTolerance = DefaultSnapTolerance) =>
        Combine(a, b, Op.Difference, snapTolerance);

    /// <summary>
    /// Union of MANY regions, folded as a <b>balanced tree</b> rather than a linear
    /// accumulate — the shape of the fold is what makes bulk unions affordable.
    ///
    /// <para>One <see cref="Arrangement2d"/> costs O(E²) (every inserted segment is scanned
    /// against every existing edge), so a single arrangement holding all N operands is
    /// quadratic in the TOTAL input size. A linear fold is no better: the accumulator keeps
    /// growing, so operand k is arranged against the whole union of the first k. Halving
    /// recursively instead means most arrangements are small, and each merge first discards
    /// the interior edges of its two children — so overlapping input (projected triangles,
    /// offset primitives) collapses as it climbs. Union is associative, so the result is
    /// identical either way; only the cost changes.</para>
    ///
    /// <para>Feed spatially sorted input when you have it (see <c>PlanarSection</c>'s
    /// silhouette): neighbours merged first is what keeps the intermediate boundaries
    /// simple.</para>
    /// </summary>
    public static IReadOnlyList<Region2d> UnionAll(
        IReadOnlyList<Region2d> regions, double snapTolerance = DefaultSnapTolerance)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return regions.Count == 0 ? [] : UnionRange(regions, 0, regions.Count, snapTolerance);
    }

    private static IReadOnlyList<Region2d> UnionRange(
        IReadOnlyList<Region2d> regions, int start, int end, double snapTolerance)
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

    private static IReadOnlyList<Region2d> Combine(
        IReadOnlyList<Region2d> a, IReadOnlyList<Region2d> b, Op op, double snapTolerance)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var arrangement = new Arrangement2d(snapTolerance);
        foreach (var region in a.Concat(b))
        {
            foreach (var loop in region.AllLoops())
                arrangement.InsertPolyline([.. loop], closed: true);
        }
        if (arrangement.EdgeCount == 0)
            return [];

        // Classify every cell once, and record the directed loop edges of the kept ones.
        var edges = new EdgeIndex(arrangement);
        var kept = new HashSet<(int From, int To)>();
        foreach (var cell in arrangement.ExtractCells())
        {
            var sample = InteriorSample(arrangement, edges, cell);
            bool inA = ContainedIn(a, sample);
            bool inB = ContainedIn(b, sample);
            bool keep = op switch
            {
                Op.Union => inA || inB,
                Op.Intersection => inA && inB,
                _ => inA && !inB,
            };
            if (!keep)
                continue;
            AddDirected(kept, cell.OuterVertices);
            foreach (var hole in cell.HoleVertices)
                AddDirected(kept, hole);
        }

        // An edge with kept cells on both sides is interior to the result: drop it. What
        // remains is the result's boundary, each directed edge with kept material on its
        // left (so outer loops come out CCW and holes CW).
        var boundaryOrder = kept.Where(e => !kept.Contains((e.To, e.From))).ToList();
        boundaryOrder.Sort();
        var boundary = new HashSet<(int From, int To)>(boundaryOrder);
        if (boundary.Count == 0)
            return [];

        var fans = arrangement.BuildCcwEdgeFans();
        var remaining = new HashSet<(int From, int To)>(boundary);
        var loops = new List<IReadOnlyList<Vector2d>>();
        foreach (var start in boundaryOrder)
        {
            if (!remaining.Contains(start))
                continue;
            var loop = new List<Vector2d>();
            var current = start;
            int guard = boundary.Count + 1;
            do
            {
                remaining.Remove(current);
                loop.Add(arrangement.VertexAt(current.From));
                current = NextBoundaryEdge(arrangement, fans, boundary, current);
                if (--guard < 0)
                    throw new InvalidOperationException("Region boolean boundary tracing did not close.");
            }
            while (current != start);
            // Coincident and touching operand edges leave exactly-collinear T-junction
            // vertices behind; dropping them is exact (see
            // Region2d.WithoutCollinearVertices) and keeps a plate a 4-sided plate.
            loops.Add(Region2d.WithoutCollinearVertices(loop));
        }
        return Region2d.FromLoops(loops);
    }

    private static bool ContainedIn(IReadOnlyList<Region2d> regions, in Vector2d point)
    {
        foreach (var region in regions)
        {
            if (region.Contains(point))
                return true;
        }
        return false;
    }

    private static void AddDirected(HashSet<(int From, int To)> set, IReadOnlyList<int> loop)
    {
        for (int i = 0; i < loop.Count; i++)
            set.Add((loop[i], loop[(i + 1) % loop.Count]));
    }

    /// <summary>
    /// The next boundary edge after <paramref name="current"/>: at its head vertex, scan the
    /// CCW fan CLOCKWISE from the reverse direction and take the first edge that is still a
    /// boundary edge. Scanning clockwise from the reverse stays inside kept material — every
    /// edge skipped on the way has kept cells on both sides — so the edge found has kept
    /// material on its left, exactly as <see cref="Arrangement2d.ExtractCells"/>'s
    /// tightest-turn walk does for a full arrangement face.
    /// </summary>
    private static (int From, int To) NextBoundaryEdge(
        Arrangement2d arrangement, int[][] fans, HashSet<(int From, int To)> boundary,
        (int From, int To) current)
    {
        int vertex = current.To;
        var fan = fans[vertex];
        int reverseIndex = -1;
        for (int i = 0; i < fan.Length; i++)
        {
            if (arrangement.OtherVertex(fan[i], vertex) == current.From)
            {
                reverseIndex = i;
                break;
            }
        }
        if (reverseIndex < 0)
            throw new InvalidOperationException("Region boolean boundary tracing lost its reverse edge.");

        for (int step = 1; step <= fan.Length; step++)
        {
            int index = ((reverseIndex - step) % fan.Length + fan.Length) % fan.Length;
            int next = arrangement.OtherVertex(fan[index], vertex);
            if (boundary.Contains((vertex, next)))
                return (vertex, next);
        }
        throw new InvalidOperationException("Region boolean boundary tracing hit a dead end.");
    }

    /// <summary>
    /// A point strictly inside a cell (see the class remarks): the outer-loop edge midpoint
    /// with the greatest clearance to any other arrangement edge, pushed half that clearance
    /// along the inward (left) normal.
    /// </summary>
    private static Vector2d InteriorSample(
        Arrangement2d arrangement, EdgeIndex edges, ArrangementCell2d cell)
    {
        var loop = cell.OuterVertices;
        var sample = default(Vector2d);
        double bestClearance = 0;

        for (int i = 0; i < loop.Count; i++)
        {
            int va = loop[i];
            int vb = loop[(i + 1) % loop.Count];
            var a = arrangement.VertexAt(va);
            var b = arrangement.VertexAt(vb);
            var direction = b - a;
            double length = direction.Length;
            if (!(length > 0))
                continue; // exact-zero division guard (scale-free, not a tolerance)
            var midpoint = (a + b) * 0.5;

            double clearance = edges.NearestDistance(midpoint, arrangement.FindEdge(va, vb));
            if (double.IsInfinity(clearance) || clearance <= bestClearance)
                continue;
            bestClearance = clearance;
            // Perpendicular is the +90 degree (left) normal, and a CCW outer loop keeps the
            // cell on its left.
            sample = midpoint + (direction / length).Perpendicular * (clearance * 0.5);
        }

        if (bestClearance > 0)
            return sample;

        // Unreachable for a real cell (a bounded face always has other edges around it);
        // the loop's vertex centroid keeps the classifier answering rather than throwing.
        var centroid = Vector2d.Zero;
        foreach (int vertex in loop)
            centroid += arrangement.VertexAt(vertex);
        return centroid / loop.Count;
    }

    /// <summary>
    /// A <see cref="Bvh"/> over the arrangement's edges, built ONCE per boolean and shared
    /// by every cell's interior sample.
    ///
    /// <para>The clearance search used to be a linear scan of every arrangement edge per
    /// loop edge, i.e. O(E²) over the whole boolean — and unlike insertion's scan it is not
    /// hidden by anything else: a union of 60 overlapping 32-gons spends most of its
    /// classification time here. The result is UNCHANGED bit for bit, because only the
    /// minimum DISTANCE is used (never which edge attained it) and a minimum over doubles
    /// does not depend on the order they are visited in.</para>
    ///
    /// <para>Edges are embedded at z = 0 in the 3D <see cref="Bvh"/> rather than growing a
    /// second, 2D hierarchy: the box distance the branch-and-bound prunes with is then
    /// exactly the 2D one.</para>
    /// </summary>
    private sealed class EdgeIndex
    {
        private readonly Vector2d[] _starts;
        private readonly Vector2d[] _ends;
        private readonly Bvh _bvh;

        public EdgeIndex(Arrangement2d arrangement)
        {
            int count = arrangement.EdgeCount;
            _starts = new Vector2d[count];
            _ends = new Vector2d[count];
            var boxes = new Aabb[count];
            for (int e = 0; e < count; e++)
            {
                var (x, y) = arrangement.EdgeAt(e);
                var p = arrangement.VertexAt(x);
                var q = arrangement.VertexAt(y);
                _starts[e] = p;
                _ends[e] = q;
                boxes[e] = Aabb.Empty
                    .Union(new Vector3d(p.X, p.Y, 0))
                    .Union(new Vector3d(q.X, q.Y, 0));
            }
            _bvh = Bvh.Build(boxes);
        }

        /// <summary>
        /// Distance from <paramref name="point"/> to the nearest edge other than
        /// <paramref name="skip"/>; positive infinity when there is no other edge.
        /// </summary>
        public double NearestDistance(in Vector2d point, int skip)
        {
            var metric = new SegmentDistance(_starts, _ends, point, skip);
            _bvh.Nearest(new Vector3d(point.X, point.Y, 0), ref metric, out _, out double distance);
            return distance;
        }

        private readonly struct SegmentDistance(
            Vector2d[] starts, Vector2d[] ends, Vector2d point, int skip) : IBvhDistance
        {
            public double DistanceTo(int item) => item == skip
                ? double.PositiveInfinity
                : PointSegmentDistance(point, starts[item], ends[item]);
        }
    }

    private static double PointSegmentDistance(in Vector2d point, in Vector2d a, in Vector2d b)
    {
        var direction = b - a;
        double lengthSquared = direction.LengthSquared;
        if (!(lengthSquared > 0))
            return point.DistanceTo(a);
        double t = Math.Clamp((point - a).Dot(direction) / lengthSquared, 0, 1);
        return point.DistanceTo(a + direction * t);
    }
}
