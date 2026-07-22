using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshDecimatorTests
{
    [Fact]
    public void Sphere_DecimatesToBudgetKeepingShape()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 48, rings: 24).Triangulated();
        int original = sphere.FaceCount;
        var decimated = MeshDecimator.Decimate(sphere, targetFaceCount: 500);
        decimated.Validate();

        Assert.True(decimated.FaceCount <= 500);
        Assert.True(decimated.FaceCount > 300, "should get close to the budget, not collapse to nothing");
        Assert.True(decimated.FaceCount < original / 4);
        Assert.True(decimated.IsClosed);
        Assert.Equal(2, decimated.EulerCharacteristic);
        Assert.All(decimated.Faces, f => Assert.Equal(3, f.Degree));

        // Shape preserved: volume close, vertices near the unit sphere.
        Assert.True(Math.Abs(decimated.Volume() - sphere.Volume()) / sphere.Volume() < 0.1,
            $"volume {decimated.Volume()} vs {sphere.Volume()}");
        foreach (var v in decimated.Vertices)
            Assert.True(Math.Abs(v.Position.Length - 1.0) < 0.1, $"vertex drifted to radius {v.Position.Length}");
    }

    [Fact]
    public void PolygonMesh_IsTriangulatedAutomatically()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16); // quads + tris
        var decimated = MeshDecimator.Decimate(sphere, targetFaceCount: 200);
        decimated.Validate();
        Assert.True(decimated.IsClosed);
        Assert.True(decimated.FaceCount <= 200);
    }

    [Fact]
    public void MeshUnderBudget_ReturnedUnchanged()
    {
        var octahedron = HalfEdgeMesh.Build(
            [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)],
            [
                new[] { 0, 2, 4 }, new[] { 2, 1, 4 }, new[] { 1, 3, 4 }, new[] { 3, 0, 4 },
                new[] { 2, 0, 5 }, new[] { 1, 2, 5 }, new[] { 3, 1, 5 }, new[] { 0, 3, 5 },
            ]);
        var result = MeshDecimator.Decimate(octahedron, targetFaceCount: 8);
        Assert.Equal(8, result.FaceCount);
    }

    [Fact]
    public void OpenPatch_BoundaryIsPreservedExactly()
    {
        // 6×6 vertex grid of quads in the plane, triangulated: only interior vertices may
        // collapse, so the outline (and hence the bounds) must survive exactly.
        var positions = new List<Vector3d>();
        for (int y = 0; y < 6; y++)
        {
            for (int x = 0; x < 6; x++)
                positions.Add((x, y, 0));
        }
        var faces = new List<int[]>();
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                int i = y * 6 + x;
                faces.Add([i, i + 1, i + 7]);
                faces.Add([i, i + 7, i + 6]);
            }
        }
        var patch = HalfEdgeMesh.Build(positions, faces);
        var decimated = MeshDecimator.Decimate(patch, targetFaceCount: 30);
        decimated.Validate();

        Assert.True(decimated.FaceCount <= 34, $"got {decimated.FaceCount}");
        Assert.False(decimated.IsClosed);
        Assert.Equal(new Aabb((0, 0, 0), (5, 5, 0)), decimated.ComputeBounds());
        Assert.All(decimated.Vertices, v => Assert.Equal(0.0, v.Position.Z, 12));

        // All 20 original boundary vertices survive untouched.
        var boundaryPositions = decimated.Vertices.Where(v => v.IsBoundary).Select(v => v.Position).ToList();
        Assert.Equal(20, boundaryPositions.Count);
    }

    [Fact]
    public void DecimatedMeshes_RemainUsableDownstream()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16).Triangulated();
        var decimated = MeshDecimator.Decimate(sphere, targetFaceCount: 300);

        // Feeds the rest of the kernel without complaint.
        var render = RenderMesh.CreateFlat(decimated);
        Assert.True(render.TriangleCount > 0);
        var boolResult = MeshBoolean.Difference(
            MeshPrimitives.Box(new Aabb((-2, -2, 0), (2, 2, 2))), decimated);
        Assert.True(boolResult.SignedVolume() > 0);
    }
}
