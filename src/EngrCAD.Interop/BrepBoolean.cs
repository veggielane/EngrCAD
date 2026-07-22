using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// B-Rep boolean operations, orchestrating the whole pipeline: surface–surface
/// intersection per face pair, seam-aligned face splitting on both solids (each side
/// breaks its seam segments at the other side's crossings too, so tessellation welds),
/// fragment classification by probing the other solid's mesh SDF, and reassembly —
/// with subtracted-tool faces reversed. A hybrid-kernel operation by design: exact
/// B-Rep surfaces and curves, mesh-backed point classification.
///
/// v1 contract: input solids must intersect transversally (no coplanar/tangent face
/// pairs); inputs are consumed (their faces are split in place); the result is
/// geometrically sealed and tessellates closed with exact volumes, but seam edges are
/// not yet topologically unified between the two sides, so <see cref="BrepSolid.Validate"/>
/// does not apply to boolean output.
/// </summary>
public static class BrepBoolean
{
    public static BrepSolid Union(BrepSolid a, BrepSolid b) =>
        Execute(a, b, keepAOutside: true, keepBOutside: true, reverseB: false);

    public static BrepSolid Intersection(BrepSolid a, BrepSolid b) =>
        Execute(a, b, keepAOutside: false, keepBOutside: false, reverseB: false);

    public static BrepSolid Difference(BrepSolid a, BrepSolid b) =>
        Execute(a, b, keepAOutside: true, keepBOutside: false, reverseB: true);

    private static BrepSolid Execute(BrepSolid a, BrepSolid b, bool keepAOutside, bool keepBOutside, bool reverseB)
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
        foreach (var fa in curvesA.Keys)
        {
            foreach (var fb in curvesB.Keys)
            {
                foreach (var curve in SurfaceIntersection.Intersect(fa.Surface, fb.Surface, region))
                {
                    curvesA[fa].Add((curve, FaceSplitter.CrossingParameters(fb, curve)));
                    curvesB[fb].Add((curve, FaceSplitter.CrossingParameters(fa, curve)));
                }
            }
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
                kept.Add(reverseB
                    ? new BrepFace(fragment.Surface, fragment.Loops, isReversed: !fragment.IsReversed)
                    : fragment);
        }
        if (kept.Count == 0)
            throw new InvalidOperationException("Boolean result is empty.");
        return new BrepSolid([new BrepShell(kept)]);
    }

    private static IEnumerable<BrepFace> SplitAll(
        Dictionary<BrepFace, List<(Curve3d Curve, IReadOnlyList<double> Breaks)>> curvesPerFace)
    {
        foreach (var (face, curves) in curvesPerFace)
        {
            var fragments = new List<BrepFace> { face };
            foreach (var (curve, breaks) in curves)
                fragments = fragments.SelectMany(f => FaceSplitter.SplitByCurve(f, curve, breaks)).ToList();
            foreach (var fragment in fragments)
                yield return fragment;
        }
    }

    /// <summary>A point strictly interior to the face, for inside/outside classification.</summary>
    private static Vector3d ProbePoint(BrepFace face)
    {
        var loops = FaceGeometry.PullLoops(face);

        // Planar-style faces: centroids of the outer loop's triangles.
        var outer = loops[0];
        if (Math.Abs(FaceGeometry.LoopSignedArea(outer)) > 1e-12)
        {
            foreach (var (i0, i1, i2) in PolygonTriangulator.Triangulate(outer))
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
        var mid = face.Surface.PointAt(u, v);
        if (FaceGeometry.Contains(face, mid))
            return mid;
        throw new InvalidOperationException("Could not find a probe point on a face fragment.");
    }
}
