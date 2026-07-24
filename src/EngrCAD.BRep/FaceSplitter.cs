using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>Result of splitting a face by a closed curve: the face keeps its loops and
/// gains the curve as a hole; the disk is the piece inside the curve (null when the
/// caller opted out to hand the edge's second use to another face, e.g. a drilled bore).</summary>
public sealed record ClosedSplitResult(BrepFace FaceWithHole, BrepFace? Disk, BrepEdge Edge);

/// <summary>Result of a closed-chain split: the hole-carrying face, the optional disk,
/// and the chain edges in traversal order (one per input curve).</summary>
public sealed record ChainSplitResult(BrepFace FaceWithHole, BrepFace? Disk, IReadOnlyList<BrepEdge> Edges);

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
        // Parameter-space interiority guard (1e-12, near round-off): a split at the very
        // end would create a zero-length edge; not a model-unit tolerance.
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
        // Boolean-critical absolute value: seam vertices built independently on the two
        // sides coincide only to tracer/projection error (~1e-7) — looser than the 1e-9
        // weld tolerance, tighter than the 1e-6 inverse-evaluation tolerance.
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
                // Midpoint identity at the inverse-evaluation scale (1e-6): the two sides
                // sample the shared intersection curve independently, so midpoints agree
                // only to chordal/tracer error, not to weld precision.
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

        // Hole loops wind opposite the outer loop; the disk's outer loop winds like it.
        // Non-reversed faces have CCW outer loops, reversed faces (boolean output) CW.
        bool holeSense = face.IsReversed ? curveCcw : !curveCcw;
        var holeCoedge = new BrepCoedge(edge, holeSense);
        var faceWithHole = new BrepFace(face.Surface, [.. face.Loops, new BrepLoop([holeCoedge])], face.IsReversed);

        BrepFace? disk = null;
        if (createDisk)
            disk = new BrepFace(face.Surface, [new BrepLoop([new BrepCoedge(edge, !holeSense)])], face.IsReversed);

        return new ClosedSplitResult(faceWithHole, disk, edge);
    }

    /// <summary>
    /// Splits a face along a CLOSED CHAIN of open curves lying entirely in its interior,
    /// whose endpoints pair end-to-start — e.g. the spiral-arc chain a threaded tool's
    /// helical bands cut into a drilled plane, one arc per band, junctions on the shared
    /// rails. Generalizes <see cref="SplitByClosedCurve"/>: the chain becomes an inner
    /// (hole) loop wound opposite the outer loop, and the disk carries the same edges as
    /// its outer loop (two-manifold). One vertex per junction and ONE edge per curve, so
    /// a boolean's other side — which splits each band by its own arc — pairs
    /// edge-for-edge in seam sealing.
    /// </summary>
    public static ChainSplitResult SplitByClosedCurveChain(
        BrepFace face, IReadOnlyList<Curve3d> chain, bool createDisk = true)
    {
        if (chain.Count < 2)
            throw new ArgumentException("A chain needs at least two curves (use SplitByClosedCurve for one).", nameof(chain));
        const double junctionTolerance = 1e-6;

        // Order and orient the curves end-to-start starting from chain[0] forward.
        var remaining = chain.Skip(1).ToList();
        var ordered = new List<(Curve3d Curve, bool Forward)> { (chain[0], true) };
        var start = chain[0].PointAt(chain[0].Domain.Start);
        var tail = chain[0].PointAt(chain[0].Domain.End);
        while (remaining.Count > 0)
        {
            int found = -1;
            bool forward = true;
            for (int i = 0; i < remaining.Count && found < 0; i++)
            {
                var candidate = remaining[i];
                if (candidate.PointAt(candidate.Domain.Start).DistanceTo(tail) < junctionTolerance)
                    (found, forward) = (i, true);
                else if (candidate.PointAt(candidate.Domain.End).DistanceTo(tail) < junctionTolerance)
                    (found, forward) = (i, false);
            }
            if (found < 0)
                throw new ArgumentException(
                    "Chain curves do not connect end-to-start into a single closed loop.", nameof(chain));
            var next = remaining[found];
            remaining.RemoveAt(found);
            ordered.Add((next, forward));
            tail = next.PointAt(forward ? next.Domain.End : next.Domain.Start);
        }
        if (tail.DistanceTo(start) >= junctionTolerance)
            throw new ArgumentException("The chain does not close.", nameof(chain));
        if (!FaceGeometry.Contains(face, chain[0].PointAt(chain[0].Domain.Mid)))
            throw new ArgumentException("The chain must lie inside the face.", nameof(chain));

        // One vertex per junction (vertex k = traversal start of curve k), one edge per
        // curve over its own domain; reversed traversal flips the coedge sense, not the
        // edge direction.
        var vertices = ordered
            .Select(o => new BrepVertex(o.Curve.PointAt(o.Forward ? o.Curve.Domain.Start : o.Curve.Domain.End)))
            .ToList();
        var edges = new List<BrepEdge>(ordered.Count);
        for (int k = 0; k < ordered.Count; k++)
        {
            var (curve, forward) = ordered[k];
            var traversalStart = vertices[k];
            var traversalEnd = vertices[(k + 1) % ordered.Count];
            edges.Add(forward
                ? new BrepEdge(curve, curve.Domain, traversalStart, traversalEnd)
                : new BrepEdge(curve, curve.Domain, traversalEnd, traversalStart));
        }

        // Winding from the pulled-back traversal.
        var pulled = new List<Vector2d>();
        foreach (var (curve, forward) in ordered)
        {
            var run = FaceGeometry.PullCurve(curve, face.Surface);
            if (!forward)
                run.Reverse();
            pulled.AddRange(run.Take(run.Count - 1)); // drop the duplicate junction sample
        }
        bool traversalCcw = FaceGeometry.LoopSignedArea(pulled) > 0;
        bool holeAlongTraversal = face.IsReversed ? traversalCcw : !traversalCcw;

        BrepLoop ChainLoop(bool alongTraversal) => new(alongTraversal
            ? [.. Enumerable.Range(0, edges.Count).Select(k => new BrepCoedge(edges[k], ordered[k].Forward))]
            : [.. Enumerable.Range(0, edges.Count).Reverse().Select(k => new BrepCoedge(edges[k], !ordered[k].Forward))]);

        var faceWithHole = new BrepFace(face.Surface, [.. face.Loops, ChainLoop(holeAlongTraversal)], face.IsReversed);
        var disk = createDisk
            ? new BrepFace(face.Surface, [ChainLoop(!holeAlongTraversal)], face.IsReversed)
            : null;
        return new ChainSplitResult(faceWithHole, disk, edges);
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
        double period = FaceGeometry.PeriodU(face.Surface);
        var runs = PullCurveRuns(curve, face.Surface, out bool fullyOnSurface);
        if (runs.Count == 0)
            return [];
        return FindCrossings(face, curve, runs, fullyOnSurface, period).Select(c => c.CurveParam).ToList();
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
        var runs = PullCurveRuns(curve, surface, out bool fullyOnSurface);
        if (runs.Count == 0)
            return [face]; // the curve does not lie on this face's surface
        var rawLoops = FaceGeometry.PullLoops(face);

        var crossings = FindCrossings(face, curve, runs, fullyOnSurface, period);
        if (crossings.Count == 0)
        {
            if (curve.IsClosed && fullyOnSurface)
            {
                var pulledCurve = runs[0];

                // A closed pulled curve that drifts a full period wraps the face's
                // periodic direction (e.g. a bore circle on a cylinder band): it is not
                // contractible and splits the band into two bands.
                var endUv = ProjectNear(surface, curve.PointAt(curve.Domain.End), pulledCurve[^1].Uv, period);
                double drift = endUv.X - pulledCurve[0].Uv.X;
                if (period > 0 && Math.Abs(drift) > period / 2)
                {
                    // A wrapping cut can only split a face whose region itself wraps
                    // the band: every loop must span the full period. A contractible
                    // fragment (a bite split off the band earlier) shares the same
                    // carrier surface, so the wrapping curve pulls back onto it — but
                    // with no boundary crossings it lies outside the fragment's region
                    // and must not split it (splitting would fabricate a phantom band).
                    if (rawLoops.Any(l => l.Max(p => p.X) - l.Min(p => p.X) < 0.75 * period))
                        return [face];
                    // Several wrapping cuts can hit the same band (a tool crossing a
                    // bore pierces its wall twice): each sub-band shares the full
                    // carrier surface, so every cut pulls back onto every fragment —
                    // parity against the fragment's own loops decides which one it
                    // actually lies in. Single-loop pole-bounded bands skip the check:
                    // the upward-v ray convention cannot see a rim below the point,
                    // but everything between rim and pole belongs to the face.
                    if (face.Loops.Count > 1 && !ParityContains(rawLoops, pulledCurve[0].Uv, period))
                        return [face];
                    return SplitBandByWrapCurve(face, curve, pulledCurve, drift > 0);
                }

                if (ParityContains(rawLoops, pulledCurve[0].Uv, period))
                    return SplitByInteriorClosedCurve(face, curve, pulledCurve, mandatoryBreaks);
            }
            return [face];
        }
        if (!curve.IsClosed)
        {
            double endEpsilon = Math.Max(1e-9, curve.Domain.Length * 1e-7);
            foreach (double endParam in (ReadOnlySpan<double>)[curve.Domain.Start, curve.Domain.End])
            {
                // An endpoint that IS a detected crossing terminates exactly on the
                // boundary — legal (a plane∩helical-band spiral arc ends on the band's
                // rails). Its parity is rounding noise, so it must not be tested.
                if (crossings.Any(c => Math.Abs(c.CurveParam - endParam) < endEpsilon))
                    continue;
                // Endpoints off the surface are trivially outside the face.
                if (surface.TryProjectPoint(curve.PointAt(endParam), out var uv, FaceGeometry.InverseEvaluationTolerance) &&
                    ParityContains(rawLoops, uv, period))
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
            if (!surface.TryProjectPoint(curve.PointAt(s), out var uv, FaceGeometry.InverseEvaluationTolerance))
                continue; // off this face's surface — the break belongs elsewhere
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
            if (!surface.TryProjectPoint(curve.PointAt(WrapParam(curve, mid)), out var midUv, FaceGeometry.InverseEvaluationTolerance))
                continue; // this stretch of the curve leaves the surface entirely
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
    /// Splits a face along a closed curve interior to it, honoring mandatory seam
    /// breaks: with two or more distinct break parameters the hole and disk loops are
    /// built from matching curve segments (a boolean's other solid crosses the curve
    /// with its own face boundaries there, so its seam edges are arcs — a single closed
    /// edge on this side could never pair with them in seam sealing). Without breaks it
    /// falls back to the single-edge <see cref="SplitByClosedCurve"/>.
    /// </summary>
    private static IReadOnlyList<BrepFace> SplitByInteriorClosedCurve(
        BrepFace face, Curve3d curve, List<(double S, Vector2d Uv)> pulledCurve, IReadOnlyList<double>? mandatoryBreaks)
    {
        var breaks = new List<double>();
        foreach (double raw in mandatoryBreaks ?? [])
        {
            double s = WrapParam(curve, curve.Domain.Clamp(raw));
            if (!breaks.Any(existing => Math.Abs(existing - s) < 1e-8))
                breaks.Add(s);
        }
        if (breaks.Count < 2)
        {
            var split = SplitByClosedCurve(face, curve);
            return [split.FaceWithHole, split.Disk!];
        }
        breaks.Sort();

        bool curveCcw = FaceGeometry.LoopSignedArea(pulledCurve.Select(p => p.Uv).ToList()) > 0;
        bool holeSense = face.IsReversed ? curveCcw : !curveCcw;

        var vertices = breaks.Select(s => new BrepVertex(curve.PointAt(s))).ToList();
        var edges = new List<BrepEdge>(breaks.Count);
        for (int i = 0; i < breaks.Count; i++)
        {
            double s0 = breaks[i];
            double s1 = breaks[(i + 1) % breaks.Count];
            if (s1 <= s0) // wrap segment past the domain end
                s1 += curve.Domain.Length;
            edges.Add(new BrepEdge(
                new CurveSegment(curve, s0, s1), Interval.Unit,
                vertices[i], vertices[(i + 1) % breaks.Count]));
        }

        BrepLoop Chain(bool alongCurve) => new(alongCurve
            ? [.. edges.Select(e => new BrepCoedge(e, true))]
            : [.. edges.AsEnumerable().Reverse().Select(e => new BrepCoedge(e, false))]);

        var faceWithHole = new BrepFace(face.Surface, [.. face.Loops, Chain(holeSense)], face.IsReversed);
        var disk = new BrepFace(face.Surface, [Chain(!holeSense)], face.IsReversed);
        return [faceWithHole, disk];
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
        if (face.Surface is not (ExtrudedSurface or RevolvedSurface { IsFullTurn: true }))
            throw new NotSupportedException(
                "Period-wrapping split curves are only supported on extruded and fully revolved band faces yet.");
        if (face.Loops.Count > 2)
            throw new NotSupportedException("Wrap-splitting expects a band face (at most two loops).");

        double vCut = pulledCurve.Average(p => p.Uv.Y);
        if (pulledCurve.Any(p => Math.Abs(p.Uv.Y - vCut) > 1e-6))
            return SplitBandByNonPlanarWrapCurve(face, curve, pulledCurve, traversesPlusU);

        // Projections carry ~1e-7 parameter error; on slanted generators (cones) that
        // shifts the cut ring radially past weld tolerance. Refine vCut so the
        // sub-band ring passes exactly through the cut curve.
        var curveStart = curve.PointAt(curve.Domain.Start);
        switch (face.Surface)
        {
            case RevolvedSurface revolvedBand:
            {
                var generator = revolvedBand.Generator;
                double target = (curveStart - revolvedBand.AxisOrigin).Dot(revolvedBand.AxisDirection);
                double Axial(double v) =>
                    (generator.PointAt(v) - revolvedBand.AxisOrigin).Dot(revolvedBand.AxisDirection) - target;
                double delta = Math.Max(generator.Domain.Length / 32, 1e-9);
                double lo = Math.Max(generator.Domain.Start, vCut - delta);
                double hi = Math.Min(generator.Domain.End, vCut + delta);
                double fLo = Axial(lo);
                if (fLo * Axial(hi) <= 0)
                {
                    for (int i = 0; i < 80; i++)
                    {
                        double mid = (lo + hi) / 2;
                        double fMid = Axial(mid);
                        if (fLo * fMid <= 0)
                            hi = mid;
                        else
                        {
                            lo = mid;
                            fLo = fMid;
                        }
                    }
                    vCut = (lo + hi) / 2;
                }
                break;
            }
            case ExtrudedSurface extrudedBand:
            {
                // v is the fraction along the direction; solve it exactly from the
                // curve start against the generator point at the same u.
                var generatorDomain = extrudedBand.Generator.Domain;
                double u0 = pulledCurve[0].Uv.X;
                double wrapped = generatorDomain.Start
                    + (((u0 - generatorDomain.Start) % generatorDomain.Length) + generatorDomain.Length) % generatorDomain.Length;
                var basePoint = extrudedBand.Generator.PointAt(wrapped);
                vCut = (curveStart - basePoint).Dot(extrudedBand.Direction) / extrudedBand.Direction.LengthSquared;
                break;
            }
        }

        var domainV = face.Surface.DomainV;
        double vTolerance = Math.Max(1e-9, domainV.Length * 1e-9);
        if (vCut <= domainV.Start + vTolerance || vCut >= domainV.End - vTolerance)
            return [face]; // the cut coincides with a boundary ring

        // Pole-bounded bands (axis-touching revolves) can have a single loop; missing
        // rings simply contribute no loop to the corresponding sub-band.
        BrepLoop? bottomLoop = null, topLoop = null;
        var pulledLoops = FaceGeometry.PullLoops(face);
        for (int i = 0; i < face.Loops.Count; i++)
        {
            if (pulledLoops[i].Average(p => p.Y) <= vCut)
                bottomLoop = face.Loops[i];
            else
                topLoop = face.Loops[i];
        }

        var seam = new BrepVertex(curve.PointAt(curve.Domain.Start));
        var edge = new BrepEdge(curve, curve.Domain, seam, seam);

        Surface lowerSurface, upperSurface;
        switch (face.Surface)
        {
            case ExtrudedSurface extruded:
                lowerSurface = new ExtrudedSurface(extruded.Generator, extruded.Direction * vCut);
                upperSurface = new ExtrudedSurface(
                    extruded.Generator.Transformed(Matrix4d.CreateTranslation(extruded.Direction * vCut)),
                    extruded.Direction * (1 - vCut));
                break;
            case RevolvedSurface revolved:
            {
                // v is the generator parameter directly: split the generator at the cut.
                var generatorDomain = revolved.Generator.Domain;
                lowerSurface = new RevolvedSurface(
                    new CurveSegment(revolved.Generator, generatorDomain.Start, vCut),
                    revolved.AxisOrigin, revolved.AxisDirection);
                upperSurface = new RevolvedSurface(
                    new CurveSegment(revolved.Generator, vCut, generatorDomain.End),
                    revolved.AxisOrigin, revolved.AxisDirection);
                break;
            }
            default:
                throw new NotSupportedException();
        }

        // Band conventions: the bottom loop follows the generator's +u direction, the top
        // loop opposes it — mirrored on reversed faces (boolean output re-winds loops).
        // The cut is the lower band's top and the upper band's bottom. A missing ring
        // (pole end of an axis-touching revolve) contributes no loop.
        bool lowerCutSense = face.IsReversed ? traversesPlusU : !traversesPlusU;
        var lowerLoops = new List<BrepLoop>(2);
        if (bottomLoop is not null)
            lowerLoops.Add(bottomLoop);
        lowerLoops.Add(new BrepLoop([new BrepCoedge(edge, lowerCutSense)]));
        var upperLoops = new List<BrepLoop>(2) { new([new BrepCoedge(edge, !lowerCutSense)]) };
        if (topLoop is not null)
            upperLoops.Add(topLoop);
        return
        [
            new BrepFace(lowerSurface, lowerLoops, face.IsReversed),
            new BrepFace(upperSurface, upperLoops, face.IsReversed),
        ];
    }

    /// <summary>
    /// Splits a band face along a closed wrapping curve whose v varies — e.g. the
    /// cylinder∩cylinder curve where a cross-drill tool pierces a bore wall. Unlike the
    /// constant-v case there is no parameter line to trim the surface at, so BOTH
    /// sub-bands keep the ORIGINAL surface and their loops no longer cover its grid
    /// domain — tessellation must route them through the trimmed-face path. Each
    /// boundary loop goes to the side of the cut its v-range lies on; a loop whose
    /// v-range overlaps the cut's is a tangent/intersecting configuration we reject.
    /// </summary>
    private static IReadOnlyList<BrepFace> SplitBandByNonPlanarWrapCurve(
        BrepFace face, Curve3d curve, List<(double S, Vector2d Uv)> pulledCurve, bool traversesPlusU)
    {
        double cutMin = pulledCurve.Min(p => p.Uv.Y);
        double cutMax = pulledCurve.Max(p => p.Uv.Y);
        var pulledLoops = FaceGeometry.PullLoops(face);
        BrepLoop? bottomLoop = null, topLoop = null;
        for (int i = 0; i < face.Loops.Count; i++)
        {
            if (pulledLoops[i].Max(p => p.Y) <= cutMin)
                bottomLoop = face.Loops[i];
            else if (pulledLoops[i].Min(p => p.Y) >= cutMax)
                topLoop = face.Loops[i];
            else
                throw new NotSupportedException(
                    "A non-planar wrapping cut overlaps a boundary loop's v-range " +
                    "(tangent or mutually intersecting cuts are not supported).");
        }

        var seam = new BrepVertex(curve.PointAt(curve.Domain.Start));
        var edge = new BrepEdge(curve, curve.Domain, seam, seam);

        // Same band conventions as the constant-v path: the bottom loop follows the
        // generator's +u direction, the top loop opposes it, mirrored on reversed faces;
        // the cut is the lower band's top and the upper band's bottom. A missing ring
        // (pole end of an axis-touching revolve) contributes no loop.
        bool lowerCutSense = face.IsReversed ? traversesPlusU : !traversesPlusU;
        var lowerLoops = new List<BrepLoop>(2);
        if (bottomLoop is not null)
            lowerLoops.Add(bottomLoop);
        lowerLoops.Add(new BrepLoop([new BrepCoedge(edge, lowerCutSense)]));
        var upperLoops = new List<BrepLoop>(2) { new([new BrepCoedge(edge, !lowerCutSense)]) };
        if (topLoop is not null)
            upperLoops.Add(topLoop);
        return
        [
            new BrepFace(face.Surface, lowerLoops, face.IsReversed),
            new BrepFace(face.Surface, upperLoops, face.IsReversed),
        ];
    }

    private static double WrapParam(Curve3d curve, double s)
    {
        var d = curve.Domain;
        if (!curve.IsClosed || (s >= d.Start && s <= d.End))
            return s;
        return d.Start + (((s - d.Start) % d.Length) + d.Length) % d.Length;
    }

    // ---- crossings ----

    /// <summary>
    /// Samples the curve and pulls the portions lying on the surface into parameter space
    /// (periodic u unwrapped within each run). Curves may leave a bounded surface — a
    /// region-clipped intersection line runs past a band's rings — so off-surface
    /// stretches separate contiguous runs. Each cut end of a partial run gains one
    /// linearly extrapolated pseudo-sample so crossings sitting exactly on the surface's
    /// domain edge (a cut line meeting a boundary ring) still produce a seed segment;
    /// the pseudo-samples are seeds only, refined in 3D afterwards.
    /// </summary>
    private static List<List<(double S, Vector2d Uv)>> PullCurveRuns(
        Curve3d curve, Surface surface, out bool fullyOnSurface, int samples = 96)
    {
        double period = FaceGeometry.PeriodU(surface);
        var parameters = FaceGeometry.ExactSampleParameters(
            curve, curve.Domain.Start, curve.Domain.End, samples);
        if (curve.IsClosed)
            parameters.RemoveAt(parameters.Count - 1); // duplicate of the start point
        var runs = new List<List<(double, Vector2d)>>();
        List<(double, Vector2d)>? current = null;
        foreach (double s in parameters)
        {
            if (!surface.TryProjectPoint(curve.PointAt(s), out var uv, FaceGeometry.InverseEvaluationTolerance))
            {
                current = null;
                continue;
            }
            if (current is null)
            {
                current = [];
                runs.Add(current);
            }
            else if (period > 0)
            {
                uv = new Vector2d(uv.X + period * Math.Round((current[^1].Item2.X - uv.X) / period), uv.Y);
            }
            current.Add((s, uv));
        }
        fullyOnSurface = runs.Count == 1 && runs[0].Count == parameters.Count;

        // A closed curve whose off-surface stretch straddles the parameter seam leaves
        // two runs that are really one: stitch them (later run first, s shifted a turn).
        if (!fullyOnSurface && curve.IsClosed && runs.Count >= 2 &&
            runs[0][0].Item1 <= parameters[0] + 1e-12 &&
            runs[^1][^1].Item1 >= parameters[^1] - 1e-12)
        {
            var tail = runs[^1];
            var head = runs[0];
            foreach (var (s, uv) in head)
            {
                var shifted = period > 0
                    ? new Vector2d(uv.X + period * Math.Round((tail[^1].Item2.X - uv.X) / period), uv.Y)
                    : uv;
                tail.Add((s + curve.Domain.Length, shifted));
            }
            runs.RemoveAt(0);
        }

        if (!fullyOnSurface)
        {
            foreach (var run in runs)
            {
                if (run.Count < 2)
                    continue;
                // Extrapolate by the local sample spacing (uniform for analytic curves,
                // the adjacent vertex gap for polylines).
                var frontDelta = run[1].Item2 - run[0].Item2;
                run.Insert(0, (run[0].Item1 - (run[1].Item1 - run[0].Item1), run[0].Item2 - frontDelta));
                var backDelta = run[^1].Item2 - run[^2].Item2;
                run.Add((run[^1].Item1 + (run[^1].Item1 - run[^2].Item1), run[^1].Item2 + backDelta));
            }
        }
        return runs;
    }

    private static Vector2d ProjectNear(Surface surface, in Vector3d point, Vector2d? near, double period)
    {
        if (!surface.TryProjectPoint(point, out var uv, FaceGeometry.InverseEvaluationTolerance))
            throw new ArgumentException($"Point {point} does not lie on the surface.");
        if (period > 0 && near is { } reference)
            uv = new Vector2d(uv.X + period * Math.Round((reference.X - uv.X) / period), uv.Y);
        return uv;
    }

    private static List<Crossing> FindCrossings(
        BrepFace face, Curve3d curve, List<List<(double S, Vector2d Uv)>> runs, bool fullyOnSurface, double period)
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
                    foreach (var pulledCurve in runs)
                    {
                        bool wrapSegment = curve.IsClosed && fullyOnSurface;
                        for (int j = 0; j < pulledCurve.Count - 1 + (wrapSegment ? 1 : 0); j++)
                        {
                            var (s0, b0) = pulledCurve[j];
                            var (s1, b1) = pulledCurve[(j + 1) % pulledCurve.Count];
                            if (wrapSegment && j == pulledCurve.Count - 1)
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
                            if (RefineCrossing(coedge.Edge, curve, ref tSeed, ref sSeed))
                            {
                                sSeed = WrapParam(curve, sSeed);
                                if (!crossings.Any(c => ReferenceEquals(c.Edge, coedge.Edge) && Math.Abs(c.EdgeParam - tSeed) < 1e-9))
                                    crossings.Add(new Crossing(coedge.Edge, tSeed, sSeed));
                            }
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
        var domain = coedge.Edge.Domain;
        var parameters = FaceGeometry.ExactSampleParameters(coedge.Edge.Curve, domain.Start, domain.End, samples);
        var result = new List<(double, Vector2d)>(parameters.Count);
        Vector2d? previous = null;
        for (int i = 0; i < parameters.Count; i++)
        {
            double t = coedge.SameSense ? parameters[i] : parameters[parameters.Count - 1 - i];
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
        // Near-parallel uv segments: these are only Newton seeds, so an absolute
        // round-off-scale cutoff suffices (refinement rejects false positives).
        if (Math.Abs(denominator) < 1e-15)
            return false;
        var d = q1 - p1;
        tp = d.Cross(s) / denominator;
        tq = d.Cross(r) / denominator;
        // Slightly inclusive: a cut passing exactly through a sampled vertex (a vertex
        // created by an earlier split) lands at tp = 0 or 1 up to rounding, and both
        // adjacent segments would otherwise miss it. These are only Newton seeds —
        // refinement rejects false positives and duplicate finds collapse downstream.
        const double slack = 1e-6;
        return tp is >= -slack and <= 1 + slack && tq is >= -slack and <= 1 + slack;
    }

    /// <summary>
    /// 2×2 Gauss–Newton in 3D: the boundary edge curve and the splitting curve both lie
    /// on the face's surface, so their uv crossing is an exact 3D intersection. Working
    /// on the curves directly (instead of projected uv) stays robust where inverse
    /// evaluation fails — a cut line meeting a bounded band exactly at its end ring —
    /// and converges to the same exact point from both solids' sides, which seam welding
    /// depends on.
    /// </summary>
    private static bool RefineCrossing(BrepEdge edge, Curve3d curve, ref double tEdge, ref double sCurve)
    {
        double t = tEdge, s = sCurve;
        var edgeDomain = edge.Domain;

        for (int iteration = 0; iteration < 20; iteration++)
        {
            var f = edge.Curve.PointAt(edgeDomain.Clamp(t)) - curve.PointAt(WrapParam(curve, s));
            // Gauss-Newton convergence: two decades below the 1e-9 weld tolerance so the
            // refined crossing cannot itself introduce a weld-scale error.
            if (f.Length < 1e-11)
            {
                tEdge = edgeDomain.Clamp(t);
                sCurve = s;
                return true;
            }

            double ht = Math.Max(1e-8, edgeDomain.Length * 1e-7);
            double hs = Math.Max(1e-8, curve.Domain.Length * 1e-7);
            double t0 = edgeDomain.Clamp(t - ht), t1 = edgeDomain.Clamp(t + ht);
            if (t1 <= t0)
                return false;
            var de = (edge.Curve.PointAt(t1) - edge.Curve.PointAt(t0)) / (t1 - t0);
            var dc = (curve.PointAt(WrapParam(curve, s + hs)) - curve.PointAt(WrapParam(curve, s - hs))) / (2 * hs);

            // Least squares for f(t, s) = E(t) − C(s): J = [de, −dc].
            double a11 = de.Dot(de), a12 = -de.Dot(dc), a22 = dc.Dot(dc);
            double b1 = -de.Dot(f), b2 = dc.Dot(f);
            double det = a11 * a22 - a12 * a12;
            if (det < 1e-12 * a11 * a22 || det <= 0)
                return false; // near-parallel (tangential) — not a transversal crossing
            t = edgeDomain.Clamp(t + (b1 * a22 - b2 * a12) / det);
            s += (b2 * a11 - b1 * a12) / det;
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
                    // Sub-face loops are traced by the tightest turn toward the face
                    // interior: clockwise for CCW-wound (normal) faces, counter-clockwise
                    // for CW-wound (reversed) ones — with the wrong handedness the walk
                    // wanders into cycles that never return to the start edge.
                    double delta = face.IsReversed ? angle - reverse : reverse - angle;
                    delta -= 2 * Math.PI * Math.Floor(delta / (2 * Math.PI)); // turn in (0, 2π]
                    // Round-off-scale angular guard: an exactly-zero turn is the back-along
                    // -the-same-edge case, which must count as a full 2π turn.
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

        // Classify traced loops. Contractible loops go by pulled signed area:
        // outer-wound (CCW on normal faces, CW on reversed ones) bound disk-like
        // sub-faces, the rest are holes. Loops that WRAP the periodic direction have
        // meaningless pulled area — they are band boundaries: traversal along +u puts
        // the material above (a band's bottom ring), along −u below (its top ring),
        // mirrored on reversed faces. Bands pair a bottom with the next top above it;
        // an unpaired boundary bounds a pole-capped band (region runs to the domain
        // edge, like a hemisphere above its equator).
        double orientation = face.IsReversed ? -1 : 1;
        var loopData = tracedLoops
            .Select(l =>
            {
                var polyline = LoopPolyline(l, surface, period);
                bool wraps = period > 0 &&
                    polyline.Max(p => p.X) - polyline.Min(p => p.X) > 0.75 * period;
                return (Coedges: l, Polyline: polyline,
                    Area: orientation * FaceGeometry.LoopSignedArea(polyline), Wraps: wraps);
            })
            .ToList();

        var outers = loopData.Where(d => !d.Wraps && d.Area > 0).ToList();
        var holes = loopData.Where(d => !d.Wraps && d.Area <= 0).ToList();

        // Pair wrapping loops into band regions by v: walking upward, a bottom
        // (material-above) boundary opens a band that the next top boundary closes.
        var wrapping = loopData.Where(d => d.Wraps)
            .Select(d =>
            {
                double drift = d.Polyline[^1].X - d.Polyline[0].X;
                return (d.Coedges, d.Polyline, IsBottom: orientation * drift > 0,
                    AverageV: d.Polyline.Average(p => p.Y));
            })
            .OrderBy(d => d.AverageV)
            .ToList();
        var bandRegions = new List<(List<List<BrepCoedge>> Loops, List<List<Vector2d>> Polylines)>();
        (List<BrepCoedge> Coedges, List<Vector2d> Polyline)? openBottom = null;
        foreach (var boundary in wrapping)
        {
            if (boundary.IsBottom)
            {
                if (openBottom is not null)
                    throw new InvalidOperationException(
                        "Arrangement tracing found two band-bottom boundaries with no top between them.");
                openBottom = (boundary.Coedges, boundary.Polyline);
            }
            else if (openBottom is { } bottom)
            {
                bandRegions.Add(([bottom.Coedges, boundary.Coedges], [bottom.Polyline, boundary.Polyline]));
                openBottom = null;
            }
            else
            {
                // Top with nothing below: the region runs down to the domain edge.
                bandRegions.Add(([boundary.Coedges], [boundary.Polyline]));
            }
        }
        if (openBottom is { } last)
            bandRegions.Add(([last.Coedges], [last.Polyline]));

        if (outers.Count == 0 && bandRegions.Count == 0)
            throw new InvalidOperationException("Arrangement tracing produced no outer-wound loops.");

        var faces = new List<BrepFace>();
        var assignedHoles = outers.ToDictionary(o => o.Coedges, _ => new List<List<BrepCoedge>>());
        var bandHoles = bandRegions.Select(_ => new List<List<BrepCoedge>>()).ToList();
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
            if (bestOuter is not null)
            {
                assignedHoles[bestOuter.Value.Coedges].Add(hole.Coedges);
                continue;
            }
            int band = bandRegions.FindIndex(r => r.Polylines.Count == 2 && ParityContains(r.Polylines, probe, period));
            if (band < 0)
                throw new InvalidOperationException("Hole loop is not contained in any sub-face.");
            bandHoles[band].Add(hole.Coedges);
        }

        foreach (var outer in outers)
        {
            var loops = new List<BrepLoop> { new(outer.Coedges) };
            loops.AddRange(assignedHoles[outer.Coedges].Select(h => new BrepLoop(h)));
            faces.Add(new BrepFace(surface, loops, face.IsReversed));
        }
        for (int i = 0; i < bandRegions.Count; i++)
        {
            var loops = bandRegions[i].Loops.Select(l => new BrepLoop(l)).ToList();
            loops.AddRange(bandHoles[i].Select(h => new BrepLoop(h)));
            faces.Add(new BrepFace(surface, loops, face.IsReversed));
        }
        return faces;
    }

    private static List<Vector2d> LoopPolyline(List<BrepCoedge> loop, Surface surface, double period, int samplesPerCoedge = 24)
    {
        var points = new List<Vector2d>();
        Vector2d? previous = null;
        foreach (var coedge in loop)
        {
            var domain = coedge.Edge.Domain;
            var parameters = FaceGeometry.ExactSampleParameters(coedge.Edge.Curve, domain.Start, domain.End, samplesPerCoedge);
            // Traversal-final sample skipped: it is the junction with the next coedge.
            for (int i = 0; i < parameters.Count - 1; i++)
            {
                double t = coedge.SameSense ? parameters[i] : parameters[parameters.Count - 1 - i];
                var p = coedge.Edge.Curve.PointAt(t);
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

    /// <summary>Base-curve parameter at t = 0.</summary>
    public double BaseStart => start;

    /// <summary>Base-curve parameter at t = 1 (may run past a closed base's domain end).</summary>
    public double BaseEnd => end;

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
