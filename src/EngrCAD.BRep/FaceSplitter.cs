using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>Result of splitting a face by a closed curve: the face keeps its loops and
/// gains the curve as a hole; the disk is the piece inside the curve (null when the
/// caller opted out to hand the edge's second use to another face, e.g. a drilled bore).</summary>
public sealed record ClosedSplitResult(BrepFace FaceWithHole, BrepFace? Disk, BrepEdge Edge);

/// <summary>Topology surgery shared by splitting operations.</summary>
public static class TopologyEditor
{
    /// <summary>
    /// Splits an edge at a curve parameter into two edges joined by a new vertex,
    /// patching every loop that uses the edge (both neighboring faces stay consistent).
    /// </summary>
    public static (BrepEdge First, BrepEdge Second, BrepVertex Vertex) SplitEdge(BrepEdge edge, double t)
    {
        var domain = edge.Domain;
        if (t <= domain.Start + 1e-12 || t >= domain.End - 1e-12)
            throw new ArgumentOutOfRangeException(nameof(t), "Split parameter must be interior to the edge domain.");

        var vertex = new BrepVertex(edge.Curve.PointAt(t));
        var first = new BrepEdge(edge.Curve, new Interval(domain.Start, t), edge.StartVertex, vertex);
        var second = new BrepEdge(edge.Curve, new Interval(t, domain.End), vertex, edge.EndVertex);

        foreach (var use in edge.UsesInternal.ToList())
        {
            IReadOnlyList<BrepCoedge> replacement = use.SameSense
                ? [new BrepCoedge(first, true), new BrepCoedge(second, true)]
                : [new BrepCoedge(second, false), new BrepCoedge(first, false)];
            use.Loop.ReplaceCoedge(use, replacement);
        }
        return (first, second, vertex);
    }

    /// <summary>
    /// Topologically seals a face set assembled from two split solids (boolean output):
    /// prunes edge uses left by discarded fragments, unifies coincident vertices, and
    /// merges each seam edge with its geometrically identical twin from the other side
    /// (they match exactly when both sides were split with the same mandatory break
    /// parameters). After sealing, the assembled solid passes <see cref="BrepSolid.Validate"/>.
    /// </summary>
    public static void SealSeams(IReadOnlyList<BrepFace> keptFaces)
    {
        const double tolerance = 1e-7;
        var keptSet = keptFaces.ToHashSet();
        var edges = keptFaces.SelectMany(f => f.Loops).SelectMany(l => l.Coedges)
            .Select(c => c.Edge).Distinct().ToList();

        // 1. Uses contributed by discarded fragments no longer count.
        foreach (var edge in edges)
            edge.UsesInternal.RemoveAll(c => c.Loop is null || !keptSet.Contains(c.Loop.Face));

        // 2. Vertex unification by position.
        var pool = new List<BrepVertex>();
        BrepVertex Canonical(BrepVertex v)
        {
            foreach (var candidate in pool)
            {
                if (candidate.Position.AreEqual(v.Position, new Tolerance(tolerance, tolerance)))
                    return candidate;
            }
            pool.Add(v);
            return v;
        }
        foreach (var edge in edges)
        {
            edge.StartVertex = Canonical(edge.StartVertex);
            edge.EndVertex = Canonical(edge.EndVertex);
        }

        // 3. Merge coincident seam edge pairs (each currently used once, from opposite sides).
        static Vector3d Mid(BrepEdge e) => e.Curve.PointAt(e.Domain.Mid);
        var seamEdges = edges.Where(e => e.UsesInternal.Count == 1).ToList();
        var merged = new HashSet<BrepEdge>();
        for (int i = 0; i < seamEdges.Count; i++)
        {
            var keep = seamEdges[i];
            if (merged.Contains(keep))
                continue;
            for (int j = i + 1; j < seamEdges.Count; j++)
            {
                var duplicate = seamEdges[j];
                if (merged.Contains(duplicate))
                    continue;
                bool endpointsMatch =
                    (ReferenceEquals(keep.StartVertex, duplicate.StartVertex) && ReferenceEquals(keep.EndVertex, duplicate.EndVertex)) ||
                    (ReferenceEquals(keep.StartVertex, duplicate.EndVertex) && ReferenceEquals(keep.EndVertex, duplicate.StartVertex));
                if (!endpointsMatch || !Mid(keep).AreEqual(Mid(duplicate), new Tolerance(1e-6, 1e-6)))
                    continue;

                // Redirect the duplicate's single use onto the kept edge, preserving the
                // traversal direction (compared at the quarter point for closed edges).
                var use = duplicate.UsesInternal.Single();
                var quarterAlongUse = duplicate.Curve.PointAt(
                    duplicate.Domain.ParameterAt(use.SameSense ? 0.25 : 0.75));
                bool sense =
                    quarterAlongUse.DistanceSquaredTo(keep.Curve.PointAt(keep.Domain.ParameterAt(0.25))) <=
                    quarterAlongUse.DistanceSquaredTo(keep.Curve.PointAt(keep.Domain.ParameterAt(0.75)));
                use.Loop.ReplaceCoedge(use, [new BrepCoedge(keep, sense)]);
                merged.Add(duplicate);
                break;
            }
        }
    }
}

/// <summary>
/// Face splitting along intersection curves: closed curves interior to a face
/// (<see cref="SplitByClosedCurve"/>) and curves crossing the face boundary
/// (<see cref="SplitByCurve"/> — a full parameter-space arrangement: boundary edges are
/// split at the crossings, interior curve segments become shared edges, and sub-faces
/// are traced from the resulting planar graph). Crossings must be transversal.
/// </summary>
public static class FaceSplitter
{
    /// <summary>
    /// Splits a face along a closed curve lying in its interior. The original face's
    /// loops are kept and the curve becomes an inner (hole) loop wound opposite the outer
    /// loop; the disk face carries the curve as its outer loop. Both share one new edge,
    /// keeping the result two-manifold.
    /// </summary>
    public static ClosedSplitResult SplitByClosedCurve(BrepFace face, Curve3d closedCurve, bool createDisk = true)
    {
        if (!closedCurve.IsClosed)
            throw new ArgumentException("The splitting curve must be closed.", nameof(closedCurve));

        var pulled = FaceGeometry.PullCurve(closedCurve, face.Surface);
        var probe = closedCurve.PointAt(closedCurve.Domain.Start);
        if (!FaceGeometry.Contains(face, probe))
            throw new ArgumentException("The splitting curve must lie inside the face.", nameof(closedCurve));

        bool curveCcw = FaceGeometry.LoopSignedArea(pulled) > 0;

        var seam = new BrepVertex(probe);
        var edge = new BrepEdge(closedCurve, closedCurve.Domain, seam, seam);

        // Hole loops wind opposite the (CCW) outer loop; the disk's outer loop winds CCW.
        var holeCoedge = new BrepCoedge(edge, sameSense: !curveCcw);
        var faceWithHole = new BrepFace(face.Surface, [.. face.Loops, new BrepLoop([holeCoedge])]);

        BrepFace? disk = null;
        if (createDisk)
            disk = new BrepFace(face.Surface, [new BrepLoop([new BrepCoedge(edge, sameSense: curveCcw)])]);

        return new ClosedSplitResult(faceWithHole, disk, edge);
    }

    private sealed record Crossing(BrepEdge? Edge, double EdgeParam, double CurveParam)
    {
        public BrepVertex Vertex { get; set; } = null!;
    }

    /// <summary>
    /// Curve parameters where the curve crosses the face's boundary. Used by booleans to
    /// force matching seam subdivision on the other solid's faces. Empty when the curve
    /// does not lie on the face's surface.
    /// </summary>
    public static IReadOnlyList<double> CrossingParameters(BrepFace face, Curve3d curve)
    {
        try
        {
            double period = FaceGeometry.PeriodU(face.Surface);
            var pulled = PullCurveWithParams(curve, face.Surface);
            return FindCrossings(face, curve, pulled, period).Select(c => c.CurveParam).ToList();
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// Splits a face along a curve that crosses its boundary transversally. Boundary
    /// edges are split at the crossings (patching neighboring faces through their shared
    /// edges), the curve's inside portions become new edges used by the sub-faces on both
    /// sides, and the resulting sub-faces are returned (the input face is superseded).
    /// A closed curve with no crossings that lies inside the face falls back to
    /// <see cref="SplitByClosedCurve"/>; a curve entirely outside returns the face as-is.
    /// </summary>
    public static IReadOnlyList<BrepFace> SplitByCurve(
        BrepFace face, Curve3d curve, IReadOnlyList<double>? mandatoryBreaks = null)
    {
        var surface = face.Surface;
        double period = FaceGeometry.PeriodU(surface);
        List<(double S, Vector2d Uv)> pulledCurve;
        try
        {
            pulledCurve = PullCurveWithParams(curve, surface);
        }
        catch (ArgumentException)
        {
            return [face]; // the curve does not lie on this face's surface
        }
        var rawLoops = FaceGeometry.PullLoops(face);

        var crossings = FindCrossings(face, curve, pulledCurve, period);
        if (crossings.Count == 0)
        {
            if (curve.IsClosed)
            {
                // A closed pulled curve that drifts a full period wraps the face's
                // periodic direction (e.g. a bore circle on a cylinder band): it is not
                // contractible and splits the band into two bands.
                var endUv = ProjectNear(surface, curve.PointAt(curve.Domain.End), pulledCurve[^1].Uv, period);
                double drift = endUv.X - pulledCurve[0].Uv.X;
                if (period > 0 && Math.Abs(drift) > period / 2)
                    return SplitBandByWrapCurve(face, curve, pulledCurve, drift > 0);

                if (ParityContains(rawLoops, pulledCurve[0].Uv, period))
                {
                    var split = SplitByClosedCurve(face, curve);
                    return [split.FaceWithHole, split.Disk!];
                }
            }
            return [face];
        }
        if (!curve.IsClosed)
        {
            foreach (double endParam in (ReadOnlySpan<double>)[curve.Domain.Start, curve.Domain.End])
            {
                var uv = ProjectNear(surface, curve.PointAt(endParam), null, period);
                if (ParityContains(rawLoops, uv, period))
                    throw new NotSupportedException("Open splitting curves must start and end outside the face.");
            }
        }

        // Mandatory breaks (the other solid's crossings, in booleans) subdivide the seam
        // identically on both sides so tessellation welds; they become interior vertices.
        foreach (double breakParam in mandatoryBreaks ?? [])
        {
            double s = WrapParam(curve, curve.Domain.Clamp(breakParam));
            if (crossings.Any(c => Math.Abs(c.CurveParam - s) < 1e-8))
                continue;
            var uv = ProjectNear(surface, curve.PointAt(s), null, period);
            if (!ParityContains(rawLoops, uv, period))
                continue;
            crossings.Add(new Crossing(null, 0, s) { Vertex = new BrepVertex(curve.PointAt(s)) });
        }

        // 1. Split the boundary edges at the crossings (multiple crossings per edge are
        //    processed outward-in so earlier splits don't invalidate later parameters).
        //    A crossing at an edge endpoint — e.g. a vertex created by a previous split —
        //    reuses that vertex instead of splitting.
        foreach (var group in crossings.Where(c => c.Edge is not null).GroupBy(c => c.Edge!))
        {
            var edge = group.Key;
            double endEpsilon = Math.Max(1e-9, edge.Domain.Length * 1e-7);
            foreach (var crossing in group.OrderByDescending(c => c.EdgeParam))
            {
                if (crossing.EdgeParam >= edge.Domain.End - endEpsilon)
                {
                    crossing.Vertex = edge.EndVertex;
                }
                else if (crossing.EdgeParam <= edge.Domain.Start + endEpsilon)
                {
                    crossing.Vertex = edge.StartVertex;
                }
                else
                {
                    var (first, _, vertex) = TopologyEditor.SplitEdge(edge, crossing.EdgeParam);
                    crossing.Vertex = vertex;
                    edge = first; // remaining (smaller) parameters live in the first piece
                }
            }
        }

        // 2. Interior curve segments between consecutive crossings become new edges
        //    (crossings sharing a curve parameter — endpoint hits reported by two
        //    adjacent boundary edges — collapse to one).
        var ordered = crossings
            .OrderBy(c => c.CurveParam)
            .Aggregate(new List<Crossing>(), (list, c) =>
            {
                if (list.Count == 0 || Math.Abs(list[^1].CurveParam - c.CurveParam) > 1e-8)
                    list.Add(c);
                return list;
            });
        var segmentCoedges = new List<BrepCoedge>();
        int segmentCount = curve.IsClosed ? ordered.Count : ordered.Count - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            var from = ordered[i];
            var to = ordered[(i + 1) % ordered.Count];
            double s0 = from.CurveParam;
            double s1 = to.CurveParam;
            if (s1 <= s0) // closed-curve wrap segment
                s1 += curve.Domain.Length;
            double mid = (s0 + s1) / 2;
            var midUv = ProjectNear(surface, curve.PointAt(WrapParam(curve, mid)), null, period);
            if (!ParityContains(rawLoops, midUv, period))
                continue; // this stretch of the curve lies outside the face

            // CurveSegment wraps s past the domain end for closed curves.
            var edge = new BrepEdge(new CurveSegment(curve, s0, s1), Interval.Unit, from.Vertex, to.Vertex);
            segmentCoedges.Add(new BrepCoedge(edge, true));
            segmentCoedges.Add(new BrepCoedge(edge, false));
        }
        if (segmentCoedges.Count == 0)
            return [face];

        // 3. Trace sub-faces from the planar graph.
        return TraceFaces(face, segmentCoedges, period);
    }

    /// <summary>
    /// Splits a two-loop band face (extruded closed generator) along a closed curve that
    /// wraps the periodic direction at constant v — e.g. a bore wall cut by a plane. The
    /// band becomes two bands with exactly reconstructed sub-surfaces, sharing one new
    /// closed edge.
    /// </summary>
    private static IReadOnlyList<BrepFace> SplitBandByWrapCurve(
        BrepFace face, Curve3d curve, List<(double S, Vector2d Uv)> pulledCurve, bool traversesPlusU)
    {
        if (face.Surface is not ExtrudedSurface extruded)
            throw new NotSupportedException(
                "Period-wrapping split curves are only supported on extruded band faces yet.");
        if (face.Loops.Count != 2)
            throw new NotSupportedException("Wrap-splitting expects a two-loop band face.");

        double vCut = pulledCurve.Average(p => p.Uv.Y);
        if (pulledCurve.Any(p => Math.Abs(p.Uv.Y - vCut) > 1e-6))
            throw new NotSupportedException("Wrap-splitting supports constant-parameter cuts only.");
        if (vCut <= 1e-9 || vCut >= 1 - 1e-9)
            return [face]; // the cut coincides with a boundary ring

        var pulledLoops = FaceGeometry.PullLoops(face);
        int bottomIndex = pulledLoops[0].Average(p => p.Y) <= pulledLoops[1].Average(p => p.Y) ? 0 : 1;
        var bottomLoop = face.Loops[bottomIndex];
        var topLoop = face.Loops[1 - bottomIndex];

        var seam = new BrepVertex(curve.PointAt(curve.Domain.Start));
        var edge = new BrepEdge(curve, curve.Domain, seam, seam);

        var lowerSurface = new ExtrudedSurface(extruded.Generator, extruded.Direction * vCut);
        var upperSurface = new ExtrudedSurface(
            extruded.Generator.Transformed(Matrix4d.CreateTranslation(extruded.Direction * vCut)),
            extruded.Direction * (1 - vCut));

        // Band conventions: the bottom loop follows the generator's +u direction, the top
        // loop opposes it. The cut is the lower band's top and the upper band's bottom.
        var lower = new BrepFace(lowerSurface,
            [bottomLoop, new BrepLoop([new BrepCoedge(edge, sameSense: !traversesPlusU)])]);
        var upper = new BrepFace(upperSurface,
            [new BrepLoop([new BrepCoedge(edge, sameSense: traversesPlusU)]), topLoop]);
        return [lower, upper];
    }

    private static double WrapParam(Curve3d curve, double s)
    {
        var d = curve.Domain;
        if (s <= d.End)
            return s;
        return d.Start + (s - d.Start) % d.Length;
    }

    // ---- crossings ----

    private static List<(double S, Vector2d Uv)> PullCurveWithParams(Curve3d curve, Surface surface, int samples = 96)
    {
        var result = new List<(double, Vector2d)>(samples + 1);
        double period = FaceGeometry.PeriodU(surface);
        Vector2d? previous = null;
        int count = curve.IsClosed ? samples : samples + 1;
        for (int i = 0; i < count; i++)
        {
            double s = curve.Domain.ParameterAt((double)i / samples);
            var uv = ProjectNear(surface, curve.PointAt(s), previous, period);
            result.Add((s, uv));
            previous = uv;
        }
        return result;
    }

    private static Vector2d ProjectNear(Surface surface, in Vector3d point, Vector2d? near, double period)
    {
        if (!surface.TryProjectPoint(point, out var uv, 1e-6))
            throw new ArgumentException($"Point {point} does not lie on the surface.");
        if (period > 0 && near is { } reference)
            uv = new Vector2d(uv.X + period * Math.Round((reference.X - uv.X) / period), uv.Y);
        return uv;
    }

    private static List<Crossing> FindCrossings(
        BrepFace face, Curve3d curve, List<(double S, Vector2d Uv)> pulledCurve, double period)
    {
        var crossings = new List<Crossing>();
        foreach (var loop in face.Loops)
        {
            foreach (var coedge in loop.Coedges)
            {
                var boundary = SampleCoedge(coedge, face.Surface, period);
                for (int i = 0; i < boundary.Count - 1; i++)
                {
                    var (t0, a0) = boundary[i];
                    var (t1, a1) = boundary[i + 1];
                    for (int j = 0; j < pulledCurve.Count - 1 + (curve.IsClosed ? 1 : 0); j++)
                    {
                        var (s0, b0) = pulledCurve[j];
                        var (s1, b1) = pulledCurve[(j + 1) % pulledCurve.Count];
                        if (curve.IsClosed && j == pulledCurve.Count - 1)
                        {
                            s1 = curve.Domain.End;
                            b1 = pulledCurve[0].Uv;
                            if (period > 0)
                                b1 = new Vector2d(b1.X + period * Math.Round((b0.X - b1.X) / period), b1.Y);
                        }
                        var (c0, c1) = (b0, b1);
                        if (period > 0)
                        {
                            // Bring the curve segment into the boundary segment's period.
                            c1 = new Vector2d(c1.X + period * Math.Round((c0.X - c1.X) / period), c1.Y);
                            double shift = period * Math.Round(((a0.X + a1.X) / 2 - (c0.X + c1.X) / 2) / period);
                            c0 = new Vector2d(c0.X + shift, c0.Y);
                            c1 = new Vector2d(c1.X + shift, c1.Y);
                        }
                        if (!SegmentsCross(a0, a1, c0, c1, out double tf, out double sf))
                            continue;

                        double tSeed = t0 + tf * (t1 - t0);
                        double sSeed = s0 + sf * (s1 - s0);
                        if (RefineCrossing(coedge.Edge, curve, face.Surface, period, ref tSeed, ref sSeed))
                        {
                            sSeed = WrapParam(curve, sSeed);
                            if (!crossings.Any(c => ReferenceEquals(c.Edge, coedge.Edge) && Math.Abs(c.EdgeParam - tSeed) < 1e-9))
                                crossings.Add(new Crossing(coedge.Edge, tSeed, sSeed));
                        }
                    }
                }
            }
        }
        return crossings;
    }

    /// <summary>Samples a coedge in edge-curve parameters + unwrapped uv (in traversal order).</summary>
    private static List<(double T, Vector2d Uv)> SampleCoedge(BrepCoedge coedge, Surface surface, double period, int samples = 48)
    {
        var result = new List<(double, Vector2d)>(samples + 1);
        var domain = coedge.Edge.Domain;
        Vector2d? previous = null;
        for (int i = 0; i <= samples; i++)
        {
            double f = coedge.SameSense ? (double)i / samples : 1 - (double)i / samples;
            double t = domain.ParameterAt(f);
            var uv = ProjectNear(surface, coedge.Edge.Curve.PointAt(t), previous, period);
            result.Add((t, uv));
            previous = uv;
        }
        return result;
    }

    private static bool SegmentsCross(
        in Vector2d p1, in Vector2d p2, in Vector2d q1, in Vector2d q2, out double tp, out double tq)
    {
        tp = tq = 0;
        var r = p2 - p1;
        var s = q2 - q1;
        double denominator = r.Cross(s);
        if (Math.Abs(denominator) < 1e-15)
            return false;
        var d = q1 - p1;
        tp = d.Cross(s) / denominator;
        tq = d.Cross(r) / denominator;
        return tp is >= 0 and <= 1 && tq is >= 0 and <= 1;
    }

    /// <summary>2×2 Newton in parameter space: edge curve and splitting curve meet exactly.</summary>
    private static bool RefineCrossing(
        BrepEdge edge, Curve3d curve, Surface surface, double period, ref double tEdge, ref double sCurve)
    {
        double t = tEdge, s = sCurve;
        var reference = ProjectNear(surface, edge.Curve.PointAt(t), null, period);

        for (int iteration = 0; iteration < 12; iteration++)
        {
            var a = ProjectNear(surface, edge.Curve.PointAt(t), reference, period);
            var b = ProjectNear(surface, curve.PointAt(WrapParam(curve, s)), reference, period);
            var f = a - b;
            if (f.Length < 1e-11)
            {
                tEdge = edge.Domain.Clamp(t);
                sCurve = s;
                return true;
            }

            double ht = Math.Max(1e-8, edge.Domain.Length * 1e-7);
            double hs = Math.Max(1e-8, curve.Domain.Length * 1e-7);
            var dt = (ProjectNear(surface, edge.Curve.PointAt(edge.Domain.Clamp(t + ht)), reference, period)
                    - ProjectNear(surface, edge.Curve.PointAt(edge.Domain.Clamp(t - ht)), reference, period)) / (2 * ht);
            var ds = (ProjectNear(surface, curve.PointAt(WrapParam(curve, s + hs)), reference, period)
                    - ProjectNear(surface, curve.PointAt(WrapParam(curve, s - hs)), reference, period)) / (2 * hs);

            double det = dt.X * -ds.Y - -ds.X * dt.Y;
            if (Math.Abs(det) < 1e-18)
                return false;
            double deltaT = (-f.X * -ds.Y - -ds.X * -f.Y) / det;
            double deltaS = (dt.X * -f.Y - -f.X * dt.Y) / det;
            t = edge.Domain.Clamp(t + deltaT);
            s += deltaS;
        }
        return false;
    }

    private static bool ParityContains(List<List<Vector2d>> loops, Vector2d uv, double period)
    {
        int crossings = 0;
        foreach (var loop in loops)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                if (period > 0)
                {
                    b = new Vector2d(b.X + period * Math.Round((a.X - b.X) / period), b.Y);
                    double shift = period * Math.Round((uv.X - (a.X + b.X) / 2) / period);
                    a = new Vector2d(a.X + shift, a.Y);
                    b = new Vector2d(b.X + shift, b.Y);
                }
                if (a.X <= uv.X == b.X <= uv.X)
                    continue;
                double t = (uv.X - a.X) / (b.X - a.X);
                if (a.Y + t * (b.Y - a.Y) > uv.Y)
                    crossings++;
            }
        }
        return (crossings & 1) == 1;
    }

    // ---- sub-face tracing ----

    private static IReadOnlyList<BrepFace> TraceFaces(BrepFace face, List<BrepCoedge> segmentCoedges, double period)
    {
        var surface = face.Surface;

        // All directed half-edges of the arrangement: boundary coedges once (their loop
        // direction), interior segments in both directions.
        var halfEdges = new List<BrepCoedge>();
        foreach (var loop in face.Loops)
            halfEdges.AddRange(loop.Coedges);
        halfEdges.AddRange(segmentCoedges);

        Vector2d NodeUv(BrepVertex v) => ProjectNear(surface, v.Position, null, period);

        double DepartureAngle(BrepCoedge h)
        {
            var domain = h.Edge.Domain;
            double t0 = h.SameSense ? domain.Start : domain.End;
            double t1 = h.SameSense
                ? domain.ParameterAt(0.02)
                : domain.ParameterAt(0.98);
            var origin = NodeUv(h.StartVertex);
            var next = ProjectNear(surface, h.Edge.Curve.PointAt(t1), origin, period);
            var d = next - origin;
            return Math.Atan2(d.Y, d.X);
        }

        double ArrivalAngle(BrepCoedge h)
        {
            var domain = h.Edge.Domain;
            double t1 = h.SameSense
                ? domain.ParameterAt(0.98)
                : domain.ParameterAt(0.02);
            var node = NodeUv(h.EndVertex);
            var before = ProjectNear(surface, h.Edge.Curve.PointAt(t1), node, period);
            var d = node - before;
            return Math.Atan2(d.Y, d.X);
        }

        var outgoing = new Dictionary<BrepVertex, List<(BrepCoedge H, double Angle)>>();
        foreach (var h in halfEdges)
        {
            if (!outgoing.TryGetValue(h.StartVertex, out var list))
                outgoing[h.StartVertex] = list = [];
            list.Add((h, DepartureAngle(h)));
        }

        var pending = new HashSet<BrepCoedge>(halfEdges);
        var tracedLoops = new List<List<BrepCoedge>>();
        while (pending.Count > 0)
        {
            var startEdge = pending.First();
            var loop = new List<BrepCoedge>();
            var current = startEdge;
            int guard = halfEdges.Count + 4;
            while (true)
            {
                loop.Add(current);
                pending.Remove(current);

                var node = current.EndVertex;
                double reverse = ArrivalAngle(current) + Math.PI;
                var candidates = outgoing[node];

                BrepCoedge? best = null;
                double bestDelta = double.PositiveInfinity;
                foreach (var (h, angle) in candidates)
                {
                    bool isPartner = ReferenceEquals(h.Edge, current.Edge) && h.SameSense != current.SameSense;
                    if (isPartner && candidates.Count > 1)
                        continue;
                    double delta = reverse - angle;
                    delta -= 2 * Math.PI * Math.Floor(delta / (2 * Math.PI)); // clockwise turn in (0, 2π]
                    if (delta < 1e-12)
                        delta += 2 * Math.PI;
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        best = h;
                    }
                }
                current = best ?? throw new InvalidOperationException("Arrangement tracing dead-ended.");
                if (ReferenceEquals(current, startEdge))
                    break;
                if (--guard < 0)
                    throw new InvalidOperationException("Arrangement tracing did not close.");
            }
            tracedLoops.Add(loop);
        }

        // Classify traced loops by pulled signed area; CCW loops bound sub-faces, CW
        // loops are holes assigned to the smallest containing CCW loop.
        var loopData = tracedLoops
            .Select(l =>
            {
                var polyline = LoopPolyline(l, surface, period);
                return (Coedges: l, Polyline: polyline, Area: FaceGeometry.LoopSignedArea(polyline));
            })
            .ToList();

        var outers = loopData.Where(d => d.Area > 0).ToList();
        var holes = loopData.Where(d => d.Area <= 0).ToList();
        if (outers.Count == 0)
            throw new InvalidOperationException("Arrangement tracing produced no counter-clockwise loops.");

        var faces = new List<BrepFace>();
        var assignedHoles = outers.ToDictionary(o => o.Coedges, _ => new List<List<BrepCoedge>>());
        foreach (var hole in holes)
        {
            var probe = hole.Polyline[0];
            (List<BrepCoedge> Coedges, double Area)? bestOuter = null;
            foreach (var outer in outers)
            {
                if (ParityContains([outer.Polyline], probe, period) &&
                    (bestOuter is null || outer.Area < bestOuter.Value.Area))
                    bestOuter = (outer.Coedges, outer.Area);
            }
            if (bestOuter is null)
                throw new InvalidOperationException("Hole loop is not contained in any sub-face.");
            assignedHoles[bestOuter.Value.Coedges].Add(hole.Coedges);
        }

        foreach (var outer in outers)
        {
            var loops = new List<BrepLoop> { new(outer.Coedges) };
            loops.AddRange(assignedHoles[outer.Coedges].Select(h => new BrepLoop(h)));
            faces.Add(new BrepFace(surface, loops));
        }
        return faces;
    }

    private static List<Vector2d> LoopPolyline(List<BrepCoedge> loop, Surface surface, double period, int samplesPerCoedge = 24)
    {
        var points = new List<Vector2d>();
        Vector2d? previous = null;
        foreach (var coedge in loop)
        {
            for (int i = 0; i < samplesPerCoedge; i++)
            {
                double f = coedge.SameSense
                    ? (double)i / samplesPerCoedge
                    : 1 - (double)i / samplesPerCoedge;
                var p = coedge.Edge.Curve.PointAt(coedge.Edge.Domain.ParameterAt(f));
                var uv = ProjectNear(surface, p, previous, period);
                points.Add(uv);
                previous = uv;
            }
        }
        return points;
    }
}

/// <summary>A bounded piece of another curve, reparameterized to [0, 1].</summary>
public sealed class CurveSegment(Curve3d baseCurve, double start, double end) : Curve3d
{
    public Curve3d Base => baseCurve;

    public override Interval Domain => Interval.Unit;
    public override bool IsClosed => false;
    public override Curve3d Underlying => baseCurve.Underlying;

    private double Map(double t) => start + (end - start) * t;

    public override Vector3d PointAt(double t)
    {
        double s = Map(t);
        var d = baseCurve.Domain;
        if (s > d.End)
            s = d.Start + (s - d.Start) % d.Length;
        return baseCurve.PointAt(s);
    }
}
