using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Boolean operations on closed meshes via BSP clipping (csg.js approach). Inputs are
/// triangulated internally; both must be closed with outward winding. BSP clipping
/// tessellates the two sides of an intersection seam independently (T-junctions), so a
/// seam-zipping pass inserts the matching vertices on both sides before rebuilding —
/// results are topologically closed for well-conditioned inputs. Near-degenerate inputs
/// (coincident coplanar faces, tangent surfaces) remain fragile; an exact
/// intersection-based rewrite is on the roadmap.
/// </summary>
public static class MeshBoolean
{
    public static HalfEdgeMesh Union(HalfEdgeMesh a, HalfEdgeMesh b)
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

    public static HalfEdgeMesh Difference(HalfEdgeMesh a, HalfEdgeMesh b)
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

    public static HalfEdgeMesh Intersection(HalfEdgeMesh a, HalfEdgeMesh b)
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
