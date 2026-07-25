using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>Which algorithm <see cref="MeshBoolean"/> uses.</summary>
public enum BooleanMethod
{
    /// <summary>
    /// BSP clipping (csg.js) plus a seam-zipping pass — the original implementation, kept
    /// for comparison and as an escape hatch. Its degeneracy tests are absolute (1e-9): it
    /// discards every polygon of a model at ~1e-5 scale and leaves boundary edges where two
    /// surfaces come within 1e-9 of tangency.
    /// </summary>
    Bsp,

    /// <summary>
    /// Exact intersection imprint (<see cref="MeshMeshCut"/>) + winding-number
    /// classification, with coincident (coplanar-overlapping) surface classified by normal
    /// agreement. Scale-free — every guard is relative to the operands' extent — and the
    /// two halves weld by exact coordinate equality rather than by tolerance. The default.
    /// </summary>
    Exact,
}

/// <summary>Which combination of the two solids to keep.</summary>
internal enum BooleanOperation
{
    Union,
    Difference,
    Intersection,
}

/// <summary>
/// Boolean operations on closed meshes. Inputs are triangulated internally; both must be
/// closed with outward winding.
/// <para>
/// The default <see cref="BooleanMethod.Exact"/> path is the imprint boolean: cut both
/// meshes along their exact common curve, classify each surface patch by the other mesh's
/// generalized winding number — or, where the two solids share boundary instead of
/// crossing it, by normal agreement — keep the halves the operation calls for, and weld by
/// exact coordinate equality. Every guard is relative to the operands' extent, so it is
/// scale-free and survives near-tangency.
/// </para>
/// <para>
/// <see cref="BooleanMethod.Bsp"/> selects the older BSP-tree clipper (csg.js approach).
/// It tessellates the two sides of an intersection seam independently (T-junctions), so a
/// seam-zipping pass inserts the matching vertices on both sides before rebuilding —
/// results are topologically closed for well-conditioned inputs. Near-degenerate inputs
/// (tangent surfaces) and models far from unit scale remain fragile, because every
/// constant in it is absolute.
/// </para>
/// </summary>
public static class MeshBoolean
{
    /// <summary>
    /// The method the two-argument overloads use. It moved to
    /// <see cref="BooleanMethod.Exact"/> once coincident (coplanar-overlapping) surface was
    /// classified rather than refused — flush-mating operands, e.g. stacking two boxes face
    /// to face, were the last thing BSP did that the exact path could not. The flip was
    /// measured against the whole solution suite: every test in every project passes, bar
    /// the ones that pin BSP behaviour on purpose, which now name
    /// <see cref="BooleanMethod.Bsp"/> explicitly.
    /// </summary>
    private const BooleanMethod DefaultMethod = BooleanMethod.Exact;

    /// <summary>Everything in either solid.</summary>
    public static HalfEdgeMesh Union(HalfEdgeMesh a, HalfEdgeMesh b, BooleanMethod method) =>
        method == BooleanMethod.Exact
            ? MeshBooleanExact.Combine(a, b, BooleanOperation.Union)
            : UnionBsp(a, b);

    /// <summary>Everything in <paramref name="a"/> but not in <paramref name="b"/>.</summary>
    public static HalfEdgeMesh Difference(HalfEdgeMesh a, HalfEdgeMesh b, BooleanMethod method) =>
        method == BooleanMethod.Exact
            ? MeshBooleanExact.Combine(a, b, BooleanOperation.Difference)
            : DifferenceBsp(a, b);

    /// <summary>Everything in both solids.</summary>
    public static HalfEdgeMesh Intersection(HalfEdgeMesh a, HalfEdgeMesh b, BooleanMethod method) =>
        method == BooleanMethod.Exact
            ? MeshBooleanExact.Combine(a, b, BooleanOperation.Intersection)
            : IntersectionBsp(a, b);

    /// <summary>Everything in either solid, via <see cref="DefaultMethod"/>.</summary>
    public static HalfEdgeMesh Union(HalfEdgeMesh a, HalfEdgeMesh b) => Union(a, b, DefaultMethod);

    /// <summary>Everything in <paramref name="a"/> but not in <paramref name="b"/>, via <see cref="DefaultMethod"/>.</summary>
    public static HalfEdgeMesh Difference(HalfEdgeMesh a, HalfEdgeMesh b) => Difference(a, b, DefaultMethod);

    /// <summary>Everything in both solids, via <see cref="DefaultMethod"/>.</summary>
    public static HalfEdgeMesh Intersection(HalfEdgeMesh a, HalfEdgeMesh b) => Intersection(a, b, DefaultMethod);

    private static HalfEdgeMesh UnionBsp(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        var na = Node(a);
        var nb = Node(b);
        na.ClipTo(nb);
        nb.ClipTo(na);
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.Build(nb.AllPolygons());
        return FromPolygons(na.AllPolygons());
    }

    private static HalfEdgeMesh DifferenceBsp(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        var na = Node(a);
        var nb = Node(b);
        na.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.Build(nb.AllPolygons());
        na.Invert();
        return FromPolygons(na.AllPolygons());
    }

    private static HalfEdgeMesh IntersectionBsp(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        var na = Node(a);
        var nb = Node(b);
        na.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);
        na.Build(nb.AllPolygons());
        na.Invert();
        return FromPolygons(na.AllPolygons());
    }

    private static CsgNode Node(HalfEdgeMesh mesh)
    {
        if (!mesh.IsClosed)
            throw new ArgumentException("Boolean operations require closed meshes.");

        var polygons = new List<CsgPolygon>(mesh.FaceCount);
        foreach (var face in mesh.Triangulated().Faces)
        {
            var vertices = face.Vertices().Select(v => v.Position).ToList();
            if (CsgPlane.TryFromPoints(vertices[0], vertices[1], vertices[2], out var plane))
                polygons.Add(new CsgPolygon(vertices, plane)); // degenerate slivers are dropped
        }
        return new CsgNode(polygons);
    }

    private static HalfEdgeMesh FromPolygons(List<CsgPolygon> polygons) =>
        MeshWelder.WeldPolygons(
            polygons.Select(p => (IReadOnlyList<Vector3d>)p.Vertices),
            tolerance: 10 * CsgPlane.Epsilon,
            zipSeams: true);
}
