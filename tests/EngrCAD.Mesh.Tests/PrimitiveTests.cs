using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class PrimitiveTests
{
    [Fact]
    public void UvSphere_TopologyIsClosedGenusZero()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 16, rings: 8);
        sphere.Validate();
        Assert.True(sphere.IsClosed);
        Assert.Equal(2, sphere.EulerCharacteristic);
        Assert.Equal(16 * 7 + 2, sphere.VertexCount);
        // 2 pole fans of 16 triangles + 6 bands of 16 quads.
        Assert.Equal(32 + 96, sphere.FaceCount);
    }

    [Fact]
    public void UvSphere_PoleValenceEqualsSegments()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 12, rings: 6);
        Assert.Equal(12, sphere.GetVertex(0).Valence);
        Assert.Equal(12, sphere.GetVertex(sphere.VertexCount - 1).Valence);
    }

    [Fact]
    public void UvSphere_VolumeAndAreaConvergeToExact()
    {
        double r = 2.0;
        var sphere = MeshPrimitives.UvSphere(r, segments: 64, rings: 32);
        double exactVolume = 4.0 / 3.0 * Math.PI * r * r * r;
        double exactArea = 4.0 * Math.PI * r * r;

        Assert.True(sphere.Volume() > 0, "winding must be outward");
        Assert.True(Math.Abs(sphere.Volume() - exactVolume) / exactVolume < 0.01);
        Assert.True(Math.Abs(sphere.SurfaceArea() - exactArea) / exactArea < 0.01);
    }

    [Fact]
    public void UvSphere_VertexNormalsAreRadial()
    {
        var sphere = MeshPrimitives.UvSphere(3.0, segments: 32, rings: 16);
        var normals = sphere.ComputeVertexNormals();
        for (int v = 0; v < sphere.VertexCount; v++)
        {
            var radial = sphere.GetPosition(v).Normalized();
            Assert.True(normals[v].Dot(radial) > 0.99, $"vertex {v}: normal deviates from radial");
        }
    }

    [Fact]
    public void Cylinder_TopologyAndExactPrismVolume()
    {
        int n = 64;
        double r = 1.5, h = 4.0;
        var cylinder = MeshPrimitives.Cylinder(r, h, n);
        cylinder.Validate();
        Assert.True(cylinder.IsClosed);
        Assert.Equal(2, cylinder.EulerCharacteristic);
        Assert.Equal(2 * n, cylinder.VertexCount);
        Assert.Equal(n + 2, cylinder.FaceCount); // n side quads + 2 n-gon caps

        // The mesh is exactly an n-gonal prism.
        double prismVolume = 0.5 * n * r * r * Math.Sin(2 * Math.PI / n) * h;
        Assert.Equal(prismVolume, cylinder.Volume(), 9);
        Assert.True(Math.Abs(cylinder.Volume() - Math.PI * r * r * h) / (Math.PI * r * r * h) < 0.01);
    }

    [Fact]
    public void Cylinder_CapsAreNgons()
    {
        var cylinder = MeshPrimitives.Cylinder(1, 2, 10);
        int ngons = cylinder.Faces.Count(f => f.Degree == 10);
        Assert.Equal(2, ngons);

        var top = cylinder.Faces.Single(f => f.Degree == 10 && f.Normal().Z > 0.5);
        Assert.Equal(1.0, top.Normal().Dot(Vector3d.UnitZ), 12);
    }

    /// <summary>Exact volume of the n-gonal frustum between similar polygon rings.</summary>
    private static double FrustumVolume(int n, double r1, double r2, double h) =>
        0.5 * n * Math.Sin(2 * Math.PI / n) * h * (r1 * r1 + r1 * r2 + r2 * r2) / 3;

    [Fact]
    public void ConeFrustum_IsExactlyAPolygonalFrustum()
    {
        const int n = 32;
        double r1 = 2, r2 = 1, h = 3;
        var cone = MeshPrimitives.Cone(r1, r2, h, n);
        cone.Validate();
        Assert.True(cone.IsClosed);
        Assert.Equal(2, cone.EulerCharacteristic);
        Assert.Equal(2 * n, cone.VertexCount);
        Assert.Equal(n + 2, cone.FaceCount); // n side quads + 2 n-gon caps
        Assert.Equal(FrustumVolume(n, r1, r2, h), cone.Volume(), 12);

        double exact = Math.PI * h * (r1 * r1 + r1 * r2 + r2 * r2) / 3;
        Assert.True(Math.Abs(cone.Volume() - exact) / exact < 0.01);
    }

    [Fact]
    public void ApexCones_FanClosedWithExactPyramidVolume()
    {
        const int n = 24;
        var pointedUp = MeshPrimitives.Cone(1.5, 0, 2, n);
        pointedUp.Validate();
        Assert.True(pointedUp.IsClosed);
        Assert.Equal(2, pointedUp.EulerCharacteristic);
        Assert.Equal(n + 1, pointedUp.VertexCount);
        Assert.Equal(n + 1, pointedUp.FaceCount); // n triangles + 1 cap
        Assert.Equal(FrustumVolume(n, 1.5, 0, 2), pointedUp.Volume(), 12);
        Assert.True(pointedUp.Volume() > 0);

        var pointedDown = MeshPrimitives.Cone(0, 1.5, 2, n);
        pointedDown.Validate();
        Assert.True(pointedDown.IsClosed);
        Assert.Equal(pointedUp.Volume(), pointedDown.Volume(), 12);
    }

    [Fact]
    public void Cone_DegenerateInputsThrow()
    {
        Assert.Throws<ArgumentException>(() => MeshPrimitives.Cone(0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshPrimitives.Cone(-1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshPrimitives.Cone(1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshPrimitives.Cone(1, 1, 1, 2));
    }
}
