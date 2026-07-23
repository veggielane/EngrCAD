using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshFeatureEdgesTests
{
    [Fact]
    public void Box_HasExactlyItsTwelveSharpEdges()
    {
        var edges = MeshFeatureEdges.Extract(MeshPrimitives.Box(2, 1, 1));
        Assert.Equal(12, edges.Count);
        double total = edges.Sum(e => e.A.DistanceTo(e.B));
        Assert.True(Math.Abs(total - (4 * 2 + 8 * 1)) < 1e-9, $"total edge length {total}");
    }

    [Fact]
    public void Sphere_IsSmooth_NoFeatureEdges()
    {
        var edges = MeshFeatureEdges.Extract(MeshPrimitives.UvSphere(1, segments: 48, rings: 24));
        Assert.Empty(edges);
    }

    [Fact]
    public void Cylinder_KeepsOnlyTheRims()
    {
        var edges = MeshFeatureEdges.Extract(MeshPrimitives.Cylinder(1, 2, segments: 64));
        // Two rim circles of 64 segments each; the smooth wall contributes nothing.
        Assert.Equal(128, edges.Count);
    }
}
