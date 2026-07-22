using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Ray3dTests
{
    private static readonly Aabb UnitBox = new((0, 0, 0), (1, 1, 1));

    [Fact]
    public void Hit_ThroughCenter_ReportsEntryAndExit()
    {
        var ray = new Ray3d(new Vector3d(-1, 0.5, 0.5), Vector3d.UnitX);
        Assert.True(ray.Intersects(UnitBox, out double tMin, out double tMax));
        Assert.Equal(1, tMin, 12);
        Assert.Equal(2, tMax, 12);
    }

    [Fact]
    public void Miss_ParallelOffsetRay()
    {
        var ray = new Ray3d(new Vector3d(-1, 2, 0.5), Vector3d.UnitX);
        Assert.False(ray.Intersects(UnitBox));
    }

    [Fact]
    public void Miss_BoxBehindRay()
    {
        var ray = new Ray3d(new Vector3d(5, 0.5, 0.5), Vector3d.UnitX);
        Assert.False(ray.Intersects(UnitBox));
    }

    [Fact]
    public void Hit_OriginInsideBox_TMinIsZero()
    {
        var ray = new Ray3d(new Vector3d(0.5, 0.5, 0.5), Vector3d.UnitZ);
        Assert.True(ray.Intersects(UnitBox, out double tMin, out double tMax));
        Assert.Equal(0, tMin, 12);
        Assert.Equal(0.5, tMax, 12);
    }

    [Fact]
    public void Hit_AxisParallelRayOnSlabBoundary()
    {
        // Ray along an edge of the box: origin lies exactly on two slab boundaries.
        var ray = new Ray3d(new Vector3d(0, 0, -1), Vector3d.UnitZ);
        Assert.True(ray.Intersects(UnitBox, out double tMin, out double tMax));
        Assert.Equal(1, tMin, 12);
        Assert.Equal(2, tMax, 12);
    }

    [Fact]
    public void Hit_DiagonalRay()
    {
        var ray = new Ray3d(new Vector3d(-1, -1, -1), new Vector3d(1, 1, 1));
        Assert.True(ray.Intersects(UnitBox, out double tMin, out double tMax));
        Assert.Equal(1, tMin, 12);
        Assert.Equal(2, tMax, 12);
    }

    [Fact]
    public void PointAt_EvaluatesAlongDirection()
    {
        var ray = new Ray3d(new Vector3d(1, 2, 3), new Vector3d(0, 0, 2));
        Assert.Equal(new Vector3d(1, 2, 7), ray.PointAt(2));
    }
}
