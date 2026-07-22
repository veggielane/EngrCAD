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
}
