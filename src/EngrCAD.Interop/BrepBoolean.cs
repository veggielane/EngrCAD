using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// A boolean whose exact B-Rep result could not be closed into a two-manifold solid.
/// Distinct from the generic failures so callers that own a fallback route (the
/// <c>Shape</c> API's implicit lowering) can recognize it and say so.
/// </summary>
public sealed class BrepBooleanException(string message) : Exception(message);

/// <summary>
/// B-Rep boolean operations, orchestrating the whole pipeline: surface–surface
/// intersection per face pair, seam-aligned face splitting on both solids (each side
/// breaks its seam segments at the other side's crossings too, so tessellation welds),
/// fragment classification by probing the other solid's mesh SDF, and reassembly —
/// with subtracted-tool faces reversed. A hybrid-kernel operation by design: exact
/// B-Rep surfaces and curves, mesh-backed point classification.
///
/// v1 contract: input solids must intersect transversally (no coplanar/tangent face
/// pairs); inputs are consumed (their faces are split in place). The result is sealed
/// both geometrically and topologically (<see cref="TopologyEditor.SealSeams"/>), and
/// every operation ENFORCES that before returning: a result that is not two-manifold
/// throws <see cref="BrepBooleanException"/> rather than handing back a solid that
/// tessellates open and exports an unprintable mesh.
/// </summary>
public static class BrepBoolean
{
    public static BrepSolid Union(BrepSolid a, BrepSolid b) =>
        Execute(a, b, "Union", keepAOutside: true, keepBOutside: true, reverseB: false);

    public static BrepSolid Intersection(BrepSolid a, BrepSolid b) =>
        Execute(a, b, "Intersection", keepAOutside: false, keepBOutside: false, reverseB: false);

    public static BrepSolid Difference(BrepSolid a, BrepSolid b) =>
        Execute(a, b, "Difference", keepAOutside: true, keepBOutside: false, reverseB: true);

    private static BrepSolid Execute(
        BrepSolid a, BrepSolid b, string operation, bool keepAOutside, bool keepBOutside, bool reverseB)
    {
        // Classification geometry is captured before any splitting mutates the inputs.
        var sdfA = new MeshSdf(BRepTessellator.Tessellate(a));
        var sdfB = new MeshSdf(BRepTessellator.Tessellate(b));
        var bounds = sdfA.Bounds.Union(sdfB.Bounds);
        var region = bounds.Expanded(bounds.Size[bounds.LongestAxis] * 0.1 + 0.1);

        // Intersection curves per original face pair; each side records the other side's
        // crossing parameters as mandatory seam breaks.
        var curvesA = a.Faces.ToDictionary(f => f, _ => new List<(Curve3d Curve, IReadOnlyList<double> Breaks)>());
        var curvesB = b.Faces.ToDictionary(f => f, _ => new List<(Curve3d Curve, IReadOnlyList<double> Breaks)>());
        // Conservative prefilter expansion at the inverse-evaluation scale; a too-small
        // value could only skip a genuinely-touching face pair, never corrupt geometry.
        var boundsA = curvesA.Keys.ToDictionary(f => f, f => f.Bounds().Expanded(1e-6));
        var boundsB = curvesB.Keys.ToDictionary(f => f, f => f.Bounds().Expanded(1e-6));
        foreach (var fa in curvesA.Keys)
        {
            foreach (var fb in curvesB.Keys)
            {
                // Carrier surfaces are unbounded (planes, cylinders): skip pairs whose
                // actual faces cannot meet, so spurious carrier intersections far from
                // either face never reach the splitter.
                if (!boundsA[fa].Intersects(boundsB[fb]))
                    continue;
                foreach (var curve in SurfaceIntersection.Intersect(fa.Surface, fb.Surface, region))
                {
                    // Identical carriers on repeated geometry (patterned faces sharing a
                    // plane) produce the same curve once per pair; splitting a face
                    // twice along the same curve breaks the arrangement tracer.
                    if (!curvesA[fa].Any(existing => SameCurve(existing.Curve, curve)))
                        curvesA[fa].Add((curve, SeamBreaks(curve, FaceSplitter.CrossingParameters(fb, curve))));
                    if (!curvesB[fb].Any(existing => SameCurve(existing.Curve, curve)))
                        curvesB[fb].Add((curve, SeamBreaks(curve, FaceSplitter.CrossingParameters(fa, curve))));
                }
            }
        }

        // Disjoint or fully nested operands: no intersection curves anywhere. Classify
        // whole bodies and combine shells — a multi-shell result for disjoint unions,
        // a cavity (reversed inner shell) for a fully swallowed Difference tool.
        if (curvesA.Values.All(list => list.Count == 0))
        {
            bool aInside = sdfB.Evaluate(ProbePoint(a.Faces.First())) < 0;
            bool bInside = sdfA.Evaluate(ProbePoint(b.Faces.First())) < 0;
            var shells = new List<BrepShell>();
            if (keepAOutside ? !aInside : aInside)
                shells.AddRange(a.Shells);
            if (keepBOutside ? !bInside : bInside)
                shells.AddRange(reverseB ? b.Shells.Select(CloneReversedShell) : b.Shells);
            if (shells.Count == 0)
                throw new InvalidOperationException("Boolean result is empty.");
            return Verified(new BrepSolid(shells), operation);
        }

        var kept = new List<BrepFace>();
        foreach (var fragment in SplitAll(curvesA))
        {
            bool inside = sdfB.Evaluate(ProbePoint(fragment)) < 0;
            if (keepAOutside ? !inside : inside)
                kept.Add(fragment);
        }
        foreach (var fragment in SplitAll(curvesB))
        {
            bool inside = sdfA.Evaluate(ProbePoint(fragment)) < 0;
            if (keepBOutside ? !inside : inside)
                kept.Add(reverseB ? ReverseFace(fragment) : fragment);
        }
        if (kept.Count == 0)
            throw new InvalidOperationException("Boolean result is empty.");
        TopologyEditor.SealSeams(kept);
        return Verified(new BrepSolid([new BrepShell(kept)]), operation);
    }

    /// <summary>
    /// Final acceptance test: the assembled result must be two-manifold — every edge used
    /// by exactly two coedges, every loop chaining end-to-start. An unclosed result is the
    /// project's worst failure mode: it tessellates into an open mesh with no complaint,
    /// exports an unprintable STL, and only surfaces when someone happens to call
    /// <see cref="BrepSolid.Validate"/>. Silence is not an option here — a boolean the
    /// exact kernel cannot close fails LOUDLY, naming the operation and where the crack is.
    /// </summary>
    private static BrepSolid Verified(BrepSolid result, string operation)
    {
        var uses = new Dictionary<BrepEdge, int>();
        foreach (var coedge in result.Coedges)
            uses[coedge.Edge] = uses.GetValueOrDefault(coedge.Edge) + 1;
        var unpaired = uses.Where(entry => entry.Value != 2).Select(entry => entry.Key).ToList();
        if (unpaired.Count > 0)
        {
            var sample = unpaired[0];
            var detail = string.Join("; ", unpaired.Take(8).Select(e =>
                $"{e.Curve.GetType().Name}[{uses[e]} use(s)] " +
                $"{e.Curve.PointAt(e.Domain.Start)}->{e.Curve.PointAt(e.Domain.End)}"));
            throw new BrepBooleanException(
                $"B-Rep {operation} produced an unclosed solid: {unpaired.Count} of {uses.Count} edges are " +
                $"used by {string.Join('/', unpaired.Select(e => uses[e]).Distinct().Order())} face(s) instead " +
                $"of 2, so the result has cracks (one runs through {sample.Curve.PointAt(sample.Domain.Mid)}). " +
                "The usual causes are coplanar or tangent face pairs — unsupported input for the v1 exact " +
                "boolean — or intersection curves that do not close into loops. Returning this solid would " +
                $"tessellate into an open mesh with no error, so it fails here instead. Unpaired: {detail}.");
        }

        foreach (var loop in result.Loops)
        {
            var coedges = loop.Coedges;
            for (int i = 0; i < coedges.Count; i++)
            {
                if (!ReferenceEquals(coedges[i].EndVertex, coedges[(i + 1) % coedges.Count].StartVertex))
                    throw new BrepBooleanException(
                        $"B-Rep {operation} produced a face loop whose coedges do not chain end-to-start " +
                        $"(near {coedges[i].EndVertex.Position}) — the assembled result is not a valid solid.");
            }
        }
        return result;
    }

    private static IEnumerable<BrepFace> SplitAll(
        Dictionary<BrepFace, List<(Curve3d Curve, IReadOnlyList<double> Breaks)>> curvesPerFace)
    {
        foreach (var (face, curves) in curvesPerFace)
        {
            var rest = ExtractInteriorChains(face, curves, out var chains);
            var fragments = new List<BrepFace> { face };
            foreach (var chain in chains)
            {
                fragments = fragments.SelectMany(f =>
                {
                    // With several chains (multiple threaded holes on one face), each
                    // chain splits only the fragment that contains it.
                    if (!FaceGeometry.Contains(f, chain[0].PointAt(chain[0].Domain.Mid)))
                        return (IEnumerable<BrepFace>)[f];
                    var split = FaceSplitter.SplitByClosedCurveChain(f, chain);
                    return [split.FaceWithHole, split.Disk!];
                }).ToList();
            }
            foreach (var (curve, breaks) in rest)
                fragments = fragments.SelectMany(f => FaceSplitter.SplitByCurve(f, curve, breaks)).ToList();
            foreach (var fragment in fragments)
                yield return fragment;
        }
    }

    /// <summary>
    /// Pulls out open curves whose endpoints all lie strictly INSIDE the face and pair
    /// end-to-end into closed cycles — a threaded tool's cap-cut chain on the drilled
    /// plane (one spiral arc per helical band, junctions on the shared rails). Such
    /// curves cannot go through <see cref="FaceSplitter.SplitByCurve"/> (open cuts must
    /// cross the boundary); each complete cycle splits as one hole+disk chain instead.
    /// Anything that fails to close stays in the returned list, where SplitByCurve
    /// reports it loudly. Curves not on this face's surface never qualify (containment
    /// projection fails), so all previously supported configurations are untouched.
    /// </summary>
    private static List<(Curve3d Curve, IReadOnlyList<double> Breaks)> ExtractInteriorChains(
        BrepFace face,
        List<(Curve3d Curve, IReadOnlyList<double> Breaks)> curves,
        out List<List<Curve3d>> chains)
    {
        chains = [];
        var rest = new List<(Curve3d Curve, IReadOnlyList<double> Breaks)>();
        var candidates = new List<(Curve3d Curve, IReadOnlyList<double> Breaks)>();
        foreach (var entry in curves)
        {
            var curve = entry.Curve;
            if (!curve.IsClosed
                && FaceGeometry.Contains(face, curve.PointAt(curve.Domain.Start))
                && FaceGeometry.Contains(face, curve.PointAt(curve.Domain.End)))
                candidates.Add(entry);
            else
                rest.Add(entry);
        }

        // Chain junctions match at the inverse-evaluation scale (1e-6): endpoints come
        // from independent tracer/analytic curves, not exactly-shared vertices.
        const double junctionTolerance = 1e-6;
        while (candidates.Count > 0)
        {
            var seed = candidates[0];
            candidates.RemoveAt(0);
            var chain = new List<(Curve3d Curve, IReadOnlyList<double> Breaks)> { seed };
            var start = seed.Curve.PointAt(seed.Curve.Domain.Start);
            var tail = seed.Curve.PointAt(seed.Curve.Domain.End);
            bool closed = false;
            while (!closed)
            {
                if (tail.DistanceTo(start) < junctionTolerance && chain.Count >= 2)
                {
                    closed = true;
                    break;
                }
                int next = candidates.FindIndex(c =>
                    c.Curve.PointAt(c.Curve.Domain.Start).DistanceTo(tail) < junctionTolerance ||
                    c.Curve.PointAt(c.Curve.Domain.End).DistanceTo(tail) < junctionTolerance);
                if (next < 0)
                    break; // dead end — hand the seed back for the loud path
                var link = candidates[next];
                candidates.RemoveAt(next);
                chain.Add(link);
                tail = link.Curve.PointAt(link.Curve.Domain.Start).DistanceTo(tail) < junctionTolerance
                    ? link.Curve.PointAt(link.Curve.Domain.End)
                    : link.Curve.PointAt(link.Curve.Domain.Start);
            }
            if (closed)
            {
                chains.Add([.. chain.Select(c => c.Curve)]);
            }
            else
            {
                rest.Add(seed);
                // Links consumed by the failed walk go back too.
                rest.AddRange(chain.Skip(1));
            }
        }
        return rest;
    }

    /// <summary>
    /// A properly reversed face: the outward normal flips (IsReversed) and every loop is
    /// re-wound (reversed coedge order and senses), so seam edges end up traversed
    /// oppositely by the two faces meeting there — keeping the result two-manifold.
    /// </summary>
    private static BrepFace ReverseFace(BrepFace face)
    {
        var loops = face.Loops
            .Select(l => new BrepLoop([.. l.Coedges.Reverse().Select(c => new BrepCoedge(c.Edge, !c.SameSense))]))
            .ToList();
        return new BrepFace(face.Surface, loops, isReversed: !face.IsReversed);
    }

    /// <summary>
    /// A reversed deep copy of a shell for the disjoint fast path. Unlike
    /// <see cref="ReverseFace"/> on split fragments (whose edges are freshly built and
    /// later cleaned by SealSeams), reversing intact faces in place would add duplicate
    /// coedge uses to the original edges — so edges are cloned.
    /// </summary>
    private static BrepShell CloneReversedShell(BrepShell shell)
    {
        var edgeClones = new Dictionary<BrepEdge, BrepEdge>();
        BrepEdge Clone(BrepEdge edge)
        {
            if (!edgeClones.TryGetValue(edge, out var clone))
                edgeClones[edge] = clone = new BrepEdge(edge.Curve, edge.Domain, edge.StartVertex, edge.EndVertex);
            return clone;
        }
        var faces = shell.Faces.Select(f => new BrepFace(
            f.Surface,
            [.. f.Loops.Select(l => new BrepLoop(
                [.. l.Coedges.Reverse().Select(c => new BrepCoedge(Clone(c.Edge), !c.SameSense))]))],
            !f.IsReversed)).ToList();
        return new BrepShell(faces);
    }

    /// <summary>
    /// Closed intersection curves get a mandatory break at their domain start on both
    /// sides: a side that wrap-splits a band along the curve anchors its closed seam
    /// edge's vertex there, so the side that instead cuts the curve into arc segments
    /// must subdivide at the same point or the seam edges cannot pair up.
    /// </summary>
    private static IReadOnlyList<double> SeamBreaks(Curve3d curve, IReadOnlyList<double> crossings) =>
        curve.IsClosed ? [.. crossings, curve.Domain.Start] : crossings;

    private static bool SameCurve(Curve3d a, Curve3d b)
    {
        // Weld-scale (1e-9 = Tolerance.Default.Linear) identity: duplicate split curves
        // from identical carriers are exact clones, so the tight tolerance is safe.
        const double tolerance = 1e-9;
        var a0 = a.PointAt(a.Domain.Start);
        var a1 = a.PointAt(a.Domain.End);
        var b0 = b.PointAt(b.Domain.Start);
        var b1 = b.PointAt(b.Domain.End);
        bool forward = a0.DistanceTo(b0) < tolerance && a1.DistanceTo(b1) < tolerance;
        bool backward = a0.DistanceTo(b1) < tolerance && a1.DistanceTo(b0) < tolerance;
        return (forward || backward)
            && a.PointAt(a.Domain.Mid).DistanceTo(b.PointAt(b.Domain.Mid)) < tolerance;
    }

    /// <summary>A point strictly interior to the face, for inside/outside classification.</summary>
    private static Vector3d ProbePoint(BrepFace face)
    {
        var loops = FaceGeometry.PullLoops(face);

        // Planar-style faces: centroids of the outer loop's triangles. A loop whose
        // unwrapped u-span covers the surface period wraps the band and cannot bound a
        // planar region — projection jitter gives such loops a tiny nonzero area, and
        // triangulating the sliver would put the probe on the fragment boundary.
        var outer = loops[0];
        double periodU = FaceGeometry.PeriodU(face.Surface);
        bool wrapsU = periodU > 0 && outer.Max(p => p.X) - outer.Min(p => p.X) > 0.75 * periodU;
        if (!wrapsU && Math.Abs(FaceGeometry.LoopSignedArea(outer)) > 1e-12)
        {
            // Largest triangles first: a sliver's centroid hugs the fragment boundary,
            // and near a boundary lying on the other solid's curved surface the
            // classification SDF is only sagitta-accurate — probing there flips signs.
            foreach (var (i0, i1, i2) in PolygonTriangulator.Triangulate(outer)
                .OrderByDescending(t => Math.Abs((outer[t.B] - outer[t.A]).Cross(outer[t.C] - outer[t.A]))))
            {
                var uv = (outer[i0] + outer[i1] + outer[i2]) / 3;
                var p = face.Surface.PointAt(uv.X, uv.Y);
                if (FaceGeometry.Contains(face, p))
                    return p;
            }
        }

        // Band-style faces (loops at constant v wrap the period, pulled area ~0).
        double u = loops.SelectMany(l => l).Average(p => p.X);
        double v = loops.Select(l => l.Average(p => p.Y)).Average();
        if (loops.Count == 1 && wrapsU)
        {
            // Pole-bounded faces (discs of axis-touching revolves) have only their rim
            // loop; averaging would probe ON the rim. Move halfway toward the pole —
            // and skip the parity check: the upward-v ray convention cannot see a
            // rim that lies below the probe, but everything between rim and pole
            // belongs to the face by construction.
            var domainV = face.Surface.DomainV;
            double far = Math.Abs(v - domainV.Start) > Math.Abs(domainV.End - v) ? domainV.Start : domainV.End;
            if (double.IsFinite(far))
                return face.Surface.PointAt(u, (v + far) / 2);
        }
        var mid = face.Surface.PointAt(u, v);
        if (FaceGeometry.Contains(face, mid))
            return mid;

        // Last resort — band fragments with bites (a hemisphere band minus several
        // bulges) have no useful uv centroid: search a coarse grid over the pulled
        // loops' uv extents and keep the containing sample farthest from the loops,
        // where the classification SDF is most trustworthy.
        double uMin = loops.SelectMany(l => l).Min(p => p.X), uMax = loops.SelectMany(l => l).Max(p => p.X);
        double vMin = loops.SelectMany(l => l).Min(p => p.Y), vMax = loops.SelectMany(l => l).Max(p => p.Y);
        var loopPoints = loops.SelectMany(l => l).Select(p => face.Surface.PointAt(p.X, p.Y)).ToList();
        Vector3d? best = null;
        double bestClearance = 0;
        const int grid = 12;
        for (int i = 1; i < grid; i++)
        {
            for (int j = 1; j < grid; j++)
            {
                double gu = uMin + (uMax - uMin) * i / grid;
                double gv = vMin + (vMax - vMin) * j / grid;
                var candidate = face.Surface.PointAt(gu, gv);
                if (!FaceGeometry.Contains(face, candidate))
                    continue;
                double clearance = loopPoints.Min(p => p.DistanceSquaredTo(candidate));
                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    best = candidate;
                }
            }
        }
        if (best is { } found)
            return found;
        throw new InvalidOperationException("Could not find a probe point on a face fragment.");
    }
}
