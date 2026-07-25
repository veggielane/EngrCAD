using EngrCAD.Core;
using EngrCAD.Core.Spatial;

namespace EngrCAD.Mesh;

/// <summary>How a face sits against the other solid's coincident surface.</summary>
internal enum Coincidence
{
    /// <summary>The face is not on the other solid's boundary; classify it by winding number.</summary>
    None,

    /// <summary>Flush, normals agreeing — both solids occupy the same side of this surface.</summary>
    SameNormal,

    /// <summary>Flush, normals opposing — the solids mate here, each on its own side.</summary>
    OppositeNormal,
}

/// <summary>
/// The part of one mesh's boundary that the other mesh's boundary lies on top of, as
/// reported by <see cref="MeshMeshCut"/> (<see cref="MeshImprint.CoincidentFacesA"/>).
/// Answers the one question the winding number cannot: a face lying exactly ON the other
/// solid gets winding ½, so <see cref="MeshBooleanExact"/> asks here first and only falls
/// back to the winding number for faces that are genuinely inside or outside.
/// <para>
/// Membership is decided at the face's centroid, which is safe because the coincident
/// region's <em>boundary</em> is imprinted into both meshes by the ordinary transversal
/// path (a coplanar patch ends where the other solid's surface leaves the shared plane —
/// a transverse face — and that pair does produce a segment). So after the cut, a face is
/// wholly coincident or wholly not, exactly as it is wholly inside or wholly outside.
/// </para>
/// </summary>
internal sealed class CoincidentSurface
{
    private readonly (Vector3d A, Vector3d B, Vector3d C)[] _triangles;
    private readonly Vector3d[] _normals;
    private readonly Bvh _bvh;
    private readonly double _epsilon;
    private readonly List<int> _hits = [];

    private CoincidentSurface(
        (Vector3d A, Vector3d B, Vector3d C)[] triangles, Vector3d[] normals, Bvh bvh, double epsilon)
    {
        _triangles = triangles;
        _normals = normals;
        _bvh = bvh;
        _epsilon = epsilon;
    }

    /// <summary>
    /// Indexes the coincident triangles for point queries, or returns null when the two
    /// operands share no boundary area — the overwhelmingly common case, which then costs
    /// nothing.
    /// </summary>
    public static CoincidentSurface? For(
        IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> triangles, double epsilon)
    {
        if (triangles.Count == 0)
            return null;

        var kept = new (Vector3d, Vector3d, Vector3d)[triangles.Count];
        var normals = new Vector3d[triangles.Count];
        var boxes = new Aabb[triangles.Count];
        int count = 0;
        foreach (var (a, b, c) in triangles)
        {
            // A sliver that cannot carry a normal cannot decide anything either; the cut
            // never pairs one (TryPlane rejects it), so this is belt and braces.
            if (!TryNormal(a, b, c, out var normal))
                continue;
            kept[count] = (a, b, c);
            normals[count] = normal;
            boxes[count] = Aabb.Empty.Union(a).Union(b).Union(c);
            count++;
        }
        if (count == 0)
            return null;
        Array.Resize(ref kept, count);
        Array.Resize(ref normals, count);
        Array.Resize(ref boxes, count);
        return new CoincidentSurface(kept, normals, Bvh.Build(boxes), epsilon);
    }

    /// <summary>
    /// Whether <paramref name="face"/> lies on this surface and, if it does, whether the
    /// two normals agree. A face qualifies only when ALL its corners sit within the cut's
    /// degeneracy guard of a coincident triangle's plane — the same criterion the cut used
    /// to call the pair coplanar — and its centroid falls inside that triangle.
    /// </summary>
    public Coincidence Classify(HalfEdgeMesh mesh, int face)
    {
        var handle = mesh.GetFace(face);
        var centroid = handle.Centroid();
        var raw = handle.NormalRaw; // area-weighted: never normalized, so never scale-limited

        _hits.Clear();
        _bvh.Query(new Aabb(
            centroid - (_epsilon, _epsilon, _epsilon), centroid + (_epsilon, _epsilon, _epsilon)), _hits);
        foreach (int index in _hits)
        {
            var (a, b, c) = _triangles[index];
            var triangleNormal = _normals[index];
            if (!Contains(a, b, c, triangleNormal, centroid))
                continue;
            if (!Flush(mesh, handle, a, triangleNormal))
                continue;
            // The face is already known to be flush, so its area vector is parallel to the
            // triangle's normal and only the SIGN of the dot product is being read — no
            // magnitude, hence no tolerance. Exactly zero means a zero-area face, which has
            // no side to be on and no geometry to contribute.
            double sense = raw.Dot(triangleNormal);
            if (sense == 0)
                continue;
            return sense > 0 ? Coincidence.SameNormal : Coincidence.OppositeNormal;
        }
        return Coincidence.None;
    }

    /// <summary>
    /// The triangle's unit normal, rejected by a guard relative to its own edge lengths (the
    /// scale-free tier). <c>Vector3d.TryNormalize</c> would apply <c>Tolerance.Default</c>'s
    /// ABSOLUTE 1e-9 to a cross product — an area threshold that fails quadratically with
    /// scale, which is precisely the BSP path's defect this algorithm exists to avoid.
    /// </summary>
    private static bool TryNormal(in Vector3d a, in Vector3d b, in Vector3d c, out Vector3d normal)
    {
        var ab = b - a;
        var ac = c - a;
        var cross = ab.Cross(ac);
        double reference = Math.Max(ab.LengthSquared, Math.Max(ac.LengthSquared, (ac - ab).LengthSquared));
        double length = cross.Length;
        if (length <= MeshMeshCut.RelativeEpsilon * reference)
        {
            normal = default;
            return false;
        }
        normal = cross / length;
        return true;
    }

    /// <summary>Every corner of the face is within the degeneracy guard of the triangle's plane.</summary>
    private bool Flush(HalfEdgeMesh mesh, Face face, in Vector3d origin, in Vector3d normal)
    {
        foreach (var vertex in face.Vertices())
        {
            if (Math.Abs(normal.Dot(vertex.Position - origin)) > _epsilon)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Point-in-triangle in the triangle's own plane, measured as a signed distance to each
    /// edge so the tolerance stays a length (a raw <c>Orient2d</c> determinant is an area,
    /// which would make the test scale with edge length).
    /// </summary>
    private bool Contains(
        in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d normal, in Vector3d p) =>
        EdgeDistance(a, b, normal, p) >= -_epsilon &&
        EdgeDistance(b, c, normal, p) >= -_epsilon &&
        EdgeDistance(c, a, normal, p) >= -_epsilon;

    private static double EdgeDistance(in Vector3d a, in Vector3d b, in Vector3d normal, in Vector3d p)
    {
        var edge = b - a;
        double length = edge.Length;
        return length > 0 ? edge.Cross(p - a).Dot(normal) / length : double.NegativeInfinity;
    }
}
