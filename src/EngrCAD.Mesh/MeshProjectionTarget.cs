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
        return ClosestPointOnTriangle(
            point,
            _positions[_triangles[3 * triangle]],
            _positions[_triangles[3 * triangle + 1]],
            _positions[_triangles[3 * triangle + 2]]);
    }

    private readonly struct TriangleDistance(Vector3d[] positions, int[] triangles, Vector3d point) : IBvhDistance
    {
        public double DistanceTo(int item)
        {
            var closest = ClosestPointOnTriangle(
                point,
                positions[triangles[3 * item]],
                positions[triangles[3 * item + 1]],
                positions[triangles[3 * item + 2]]);
            return (closest - point).Length;
        }
    }

    /// <summary>
    /// Closest point of triangle <c>abc</c> to <paramref name="p"/> — Ericson's Voronoi-region
    /// form (Real-Time Collision Detection §5.1.5): six barycentric sign tests locate the
    /// feature (vertex, edge or interior) and the answer is exact for that feature, with no
    /// tolerance anywhere. Degenerate (zero-area) triangles fall out through the edge cases.
    /// </summary>
    internal static Vector3d ClosestPointOnTriangle(in Vector3d p, in Vector3d a, in Vector3d b, in Vector3d c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        double d1 = ab.Dot(ap), d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0)
            return a;

        var bp = p - b;
        double d3 = ab.Dot(bp), d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3)
            return b;

        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            double denom = d1 - d3;
            // Exact-zero division guard (epsilon ladder: algorithmic tier) — a zero
            // denominator means a and b coincide, so a is the answer.
            return denom == 0 ? a : a + ab * (d1 / denom);
        }

        var cp = p - c;
        double d5 = ab.Dot(cp), d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6)
            return c;

        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            double denom = d2 - d6;
            return denom == 0 ? a : a + ac * (d2 / denom);
        }

        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            double denom = d4 - d3 + (d5 - d6);
            return denom == 0 ? b : b + (c - b) * ((d4 - d3) / denom);
        }

        double sum = va + vb + vc;
        if (sum == 0)
            return a; // fully degenerate triangle
        double inv = 1.0 / sum;
        return a + ab * (vb * inv) + ac * (vc * inv);
    }
}
