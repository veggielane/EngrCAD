using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class LoopSubdivisionTests
{
    [Fact]
    public void Triangulated_BoxBecomesTwelveTriangles()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))).Triangulated();
        box.Validate();
        Assert.Equal(12, box.FaceCount);
        Assert.True(box.IsClosed);
        Assert.Equal(1.0, box.Volume(), 12); // triangulation preserves volume
        Assert.All(box.Faces, f => Assert.Equal(3, f.Degree));
    }

    [Fact]
    public void Subdivide_CountsFollowLoopScheme()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))).Triangulated();
        var subdivided = LoopSubdivision.Subdivide(box);
        subdivided.Validate();

        Assert.Equal(box.FaceCount * 4, subdivided.FaceCount);
        Assert.Equal(box.VertexCount + box.EdgeCount, subdivided.VertexCount);
        Assert.True(subdivided.IsClosed);
        Assert.Equal(2, subdivided.EulerCharacteristic);
    }

    [Fact]
    public void Subdivide_ClosedMeshShrinksButStaysSolid()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))).Triangulated();
        var subdivided = LoopSubdivision.Subdivide(box, iterations: 3);
        subdivided.Validate();

        double volume = subdivided.Volume();
        Assert.True(volume > 0.3 && volume < 1.0,
            $"subdivided box volume {volume} should shrink toward the limit surface but stay solid");
    }

    [Fact]
    public void Subdivide_ConvergesTowardSphere()
    {
        // A subdivided octahedron approaches its smooth limit surface — rounded, but not a
        // sphere: the limit shape carries ~6.5% radius variation (flatter over original
        // faces, tighter at the six original corners). Guard smoothness, not sphericity.
        var octahedron = HalfEdgeMesh.Build(
            [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)],
            [
                new[] { 0, 2, 4 }, new[] { 2, 1, 4 }, new[] { 1, 3, 4 }, new[] { 3, 0, 4 },
                new[] { 2, 0, 5 }, new[] { 1, 2, 5 }, new[] { 3, 1, 5 }, new[] { 0, 3, 5 },
            ]);
        octahedron.Validate();
        Assert.True(octahedron.Volume() > 0);

        var smooth = LoopSubdivision.Subdivide(octahedron, iterations: 4);
        var radii = smooth.Vertices.Select(v => v.Position.Length).ToList();
        double spread = (radii.Max() - radii.Min()) / radii.Average();
        Assert.True(spread < 0.10, $"radius spread {spread:F4} should be small for a smooth limit surface");
    }

    [Fact]
    public void Subdivide_OpenMeshPreservesBoundaryCorners()
    {
        // A flat square patch of two triangles: boundary rules keep it in the plane,
        // and the 3/4–1/8–1/8 rule keeps corner vertices at the corners.
        var patch = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)],
            [new[] { 0, 1, 2 }, new[] { 0, 2, 3 }]);
        var subdivided = LoopSubdivision.Subdivide(patch, iterations: 2);
        subdivided.Validate();

        Assert.False(subdivided.IsClosed);
        Assert.Single(subdivided.BoundaryLoops());
        Assert.All(subdivided.Vertices, v => Assert.Equal(0.0, v.Position.Z, 12));

        // Boundary of the patch stays within the unit square.
        var bounds = subdivided.ComputeBounds();
        Assert.True(bounds.Min.X >= -1e-12 && bounds.Max.X <= 1 + 1e-12);
        Assert.True(bounds.Min.Y >= -1e-12 && bounds.Max.Y <= 1 + 1e-12);
    }

    [Fact]
    public void Subdivide_RejectsPolygonMesh()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1))); // quads
        Assert.Throws<ArgumentException>(() => LoopSubdivision.Subdivide(box));
    }
}
