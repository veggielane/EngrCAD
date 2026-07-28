using EngrCAD.Core;
using EngrCAD.Core.Spatial;

namespace EngrCAD.Mesh;

/// <summary>
/// A surface a remeshing pass can pull vertices back onto (geometry3Sharp's
/// <c>IProjectionTarget</c>). Smoothing shrinks a model — Laplacian flow moves every vertex
/// toward the local centroid, so a sphere loses radius every pass — and projection is what
/// undoes that: after each pass every free vertex is moved back to its closest point on the
/// target, so the remesh changes the tessellation without changing the shape.
/// </summary>
/// <remarks>
/// Implementations must be safe to call from the remesher's inner loop and must not depend
/// on the mesh being remeshed (it is mutating underneath). <see cref="MeshProjectionTarget"/>
/// snapshots the target's geometry for exactly that reason. Consumers that already have a
/// signed distance field (EngrCAD.Interop's <c>MeshSdf</c>, any <c>Sdf</c>) can implement
/// this in a few lines as <c>p − d(p)·∇d(p)</c>; the interface lives here so
/// <c>EngrCAD.Mesh</c> need not depend on the implicit engine.
/// </remarks>
public interface IProjectionTarget
{
    /// <summary>Returns the point on the target closest to (or otherwise standing in for) <paramref name="point"/>.</summary>
    Vector3d Project(in Vector3d point);

    /// <summary>
    /// Projects and additionally reports the target's outward unit normal there. Needed by
    /// face-aligned reprojection (<see cref="RemeshProjection.FaceAligned"/>), which moves a
    /// whole triangle rigidly onto the target and therefore needs to know which way the
    /// target faces, not just where it is.
    /// </summary>
    /// <remarks>
    /// The default implementation projects and reports <see cref="Vector3d.Zero"/>, which is
    /// how an <b>unoriented</b> target says so — a semantic exact-zero test, not a tolerance
    /// (no unit normal is ever zero). Callers must have a plan for that answer; the remesher
    /// falls back to plain closest-point projection for the triangles it affects. Overriding
    /// this is worth it wherever the orientation is already computed: both targets in this
    /// repository get it for free from work the projection does anyway.
    /// </remarks>
    Vector3d Project(in Vector3d point, out Vector3d normal)
    {
        normal = Vector3d.Zero;
        return Project(point);
    }
}

/// <summary>
/// Projects onto a fixed triangle mesh: BVH over the triangles, exact closest point on the
/// winning triangle (g3's <c>MeshProjectionTarget</c>). Unsigned — the closest surface point,
/// with no inside/outside notion — which is what remeshing wants, since a remeshed vertex is
/// already within a fraction of an edge length of the surface.
/// <para>
/// The target's positions and indices are <b>snapshotted at construction</b>: the mesh being
/// remeshed is mutating, and projecting it against itself would chase its own smoothing.
/// Queries are allocation-free (struct <see cref="IBvhDistance"/> metric).
/// </para>
/// </summary>
public sealed class MeshProjectionTarget : IProjectionTarget
{
    private readonly Vector3d[] _positions;
    private readonly int[] _triangles; // 3 indices per triangle
    private readonly Bvh _bvh;

    /// <summary>Builds a projection target over <paramref name="mesh"/> (triangulated if needed).</summary>
    public MeshProjectionTarget(HalfEdgeMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.FaceCount == 0)
            throw new ArgumentException("A projection target needs at least one face.", nameof(mesh));

        var source = mesh.Faces.Any(f => f.Degree != 3) ? mesh.Triangulated() : mesh;
        var (positions, faces) = source.ToIndexed();
        _positions = positions;
        _triangles = new int[faces.Count * 3];
        var boxes = new Aabb[faces.Count];
        for (int f = 0; f < faces.Count; f++)
        {
            var loop = faces[f];
            _triangles[3 * f] = loop[0];
            _triangles[3 * f + 1] = loop[1];
            _triangles[3 * f + 2] = loop[2];
            boxes[f] = Aabb.Empty
                .Union(positions[loop[0]])
                .Union(positions[loop[1]])
                .Union(positions[loop[2]]);
        }
        _bvh = Bvh.Build(boxes);
    }

    /// <summary>Number of triangles in the snapshot.</summary>
    public int TriangleCount => _triangles.Length / 3;

    /// <inheritdoc/>
    public Vector3d Project(in Vector3d point)
    {
        var metric = new TriangleDistance(_positions, _triangles, point);
        if (!_bvh.Nearest(point, ref metric, out int triangle, out _))
            return point;
        return Distance3d.ClosestPointOnTriangle(
            point,
            _positions[_triangles[3 * triangle]],
            _positions[_triangles[3 * triangle + 1]],
            _positions[_triangles[3 * triangle + 2]]);
    }

    /// <summary>
    /// Projects and reports the winning triangle's own normal — the snapshot's orientation, so
    /// it costs nothing beyond the search that already ran. A degenerate winning triangle
    /// reports a zero normal, which the interface's contract already defines as "unoriented".
    /// </summary>
    public Vector3d Project(in Vector3d point, out Vector3d normal)
    {
        normal = Vector3d.Zero;
        var metric = new TriangleDistance(_positions, _triangles, point);
        if (!_bvh.Nearest(point, ref metric, out int triangle, out _))
            return point;

        var a = _positions[_triangles[3 * triangle]];
        var b = _positions[_triangles[3 * triangle + 1]];
        var c = _positions[_triangles[3 * triangle + 2]];
        var area = (b - a).Cross(c - a);
        double length = area.Length;
        // Exact-zero test on a length, not an epsilon on an area: a triangle with no normal
        // has none at any scale, and the interface has a spelling for that.
        if (length > 0)
            normal = area / length;
        return Distance3d.ClosestPointOnTriangle(point, a, b, c);
    }

    private readonly struct TriangleDistance(Vector3d[] positions, int[] triangles, Vector3d point) : IBvhDistance
    {
        public double DistanceTo(int item)
        {
            var closest = Distance3d.ClosestPointOnTriangle(
                point,
                positions[triangles[3 * item]],
                positions[triangles[3 * item + 1]],
                positions[triangles[3 * item + 2]]);
            return (closest - point).Length;
        }
    }
}
