using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class BooleanTests
{
    private static HalfEdgeMesh Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        MeshPrimitives.Box(new Aabb((x0, y0, z0), (x1, y1, z1)));

    private static void AssertVolume(double expected, HalfEdgeMesh mesh, double relTol = 1e-6)
    {
        double actual = mesh.SignedVolume();
        Assert.True(Math.Abs(actual - expected) <= relTol * Math.Max(1, Math.Abs(expected)),
            $"volume {actual} should be ≈ {expected}");
    }

    [Fact]
    public void OverlappingBoxes_UnionDifferenceIntersection()
    {
        // A = [0,2]³, B = [1,3]³, overlap = [1,2]³ (volume 1).
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(1, 1, 1, 3, 3, 3);

        AssertVolume(8 + 8 - 1, MeshBoolean.Union(a, b));
        AssertVolume(8 - 1, MeshBoolean.Difference(a, b));
        AssertVolume(1, MeshBoolean.Intersection(a, b));
    }

    [Fact]
    public void Intersection_BoundsAreOverlapRegion()
    {
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(1, 1, 1, 3, 3, 3);
        var overlap = MeshBoolean.Intersection(a, b).ComputeBounds();

        Assert.True(overlap.Min.AreEqual((1, 1, 1), new Tolerance(1e-6, 1e-6)));
        Assert.True(overlap.Max.AreEqual((2, 2, 2), new Tolerance(1e-6, 1e-6)));
    }

    [Fact]
    public void DisjointBoxes_BooleansDegenerate()
    {
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = Box(5, 5, 5, 6, 6, 6);

        AssertVolume(8 + 1, MeshBoolean.Union(a, b));
        AssertVolume(8, MeshBoolean.Difference(a, b));

        var intersection = MeshBoolean.Intersection(a, b);
        Assert.True(intersection.FaceCount == 0 || Math.Abs(intersection.SignedVolume()) < 1e-9);
    }

    [Fact]
    public void ContainedBox_DifferenceHollowsVolume()
    {
        var outer = Box(0, 0, 0, 2, 2, 2);
        var inner = Box(0.5, 0.5, 0.5, 1.5, 1.5, 1.5);

        // Difference keeps the shell: outer volume minus the cavity.
        AssertVolume(8 - 1, MeshBoolean.Difference(outer, inner));
        AssertVolume(8, MeshBoolean.Union(outer, inner));
        AssertVolume(1, MeshBoolean.Intersection(outer, inner));
    }

    [Fact]
    public void BoxMinusSphere_VolumeMatchesAnalytic()
    {
        // Sphere fully inside the box: difference = boxVol − sphereVol (tessellated).
        var box = Box(-1.5, -1.5, -1.5, 1.5, 1.5, 1.5);
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);
        double sphereVolume = sphere.Volume(); // exact volume of the tessellated sphere

        var result = MeshBoolean.Difference(box, sphere);
        AssertVolume(27 - sphereVolume, result, relTol: 1e-4);
    }

    [Fact]
    public void BoxSphereIntersection_ClipsToHalfSphere()
    {
        // Sphere centered on a box face: intersection ≈ half the sphere.
        var box = Box(0, -2, -2, 2, 2, 2);
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);
        double halfSphere = sphere.Volume() / 2;

        var result = MeshBoolean.Intersection(box, sphere);
        AssertVolume(halfSphere, result, relTol: 0.01);
    }

    [Fact]
    public void Results_AreRenderable()
    {
        var a = Box(0, 0, 0, 2, 2, 2);
        var b = MeshPrimitives.UvSphere(1.2, segments: 24, rings: 12);
        var result = MeshBoolean.Difference(a, b);

        result.Validate();
        Assert.True(result.IsClosed, "seam zipping should close boolean results topologically");
        var render = RenderMesh.CreateFlat(result);
        Assert.True(render.TriangleCount > 0);
        Assert.Equal(render.Positions.Length, render.Normals.Length);
    }

    [Fact]
    public void Booleans_RejectOpenMeshes()
    {
        var open = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)],
            [new[] { 0, 1, 2 }]);
        var box = Box(0, 0, 0, 1, 1, 1);
        Assert.Throws<ArgumentException>(() => MeshBoolean.Union(open, box));
    }
}
