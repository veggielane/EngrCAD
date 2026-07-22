using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class AabbTests
{
    [Fact]
    public void Empty_IsEmptyAndUnionRecovers()
    {
        Assert.True(Aabb.Empty.IsEmpty);

        var p = new Vector3d(1, 2, 3);
        var box = Aabb.Empty.Union(p);
        Assert.False(box.IsEmpty);
        Assert.Equal(p, box.Min);
        Assert.Equal(p, box.Max);
    }

    [Fact]
    public void FromPoints_BoundsAllPoints()
    {
        ReadOnlySpan<Vector3d> points =
        [
            new(1, 5, -2),
            new(-3, 2, 7),
            new(0, 0, 0),
        ];
        var box = Aabb.FromPoints(points);
        Assert.Equal(new Vector3d(-3, 0, -2), box.Min);
        Assert.Equal(new Vector3d(1, 5, 7), box.Max);
        foreach (var p in points)
            Assert.True(box.Contains(p));
    }

    [Fact]
    public void Union_CoversBothBoxes()
    {
        var a = new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1));
        var b = new Aabb(new Vector3d(2, -1, 0.5), new Vector3d(3, 0.5, 2));
        var u = a.Union(b);
        Assert.True(u.Contains(a));
        Assert.True(u.Contains(b));
        Assert.Equal(new Vector3d(0, -1, 0), u.Min);
        Assert.Equal(new Vector3d(3, 1, 2), u.Max);
    }

    [Fact]
    public void Intersects_OverlappingTouchingAndDisjoint()
    {
        var a = new Aabb(new Vector3d(0, 0, 0), new Vector3d(2, 2, 2));
        Assert.True(a.Intersects(new Aabb(new Vector3d(1, 1, 1), new Vector3d(3, 3, 3))));
        // Sharing a face counts as intersecting (closed intervals).
        Assert.True(a.Intersects(new Aabb(new Vector3d(2, 0, 0), new Vector3d(3, 2, 2))));
        Assert.False(a.Intersects(new Aabb(new Vector3d(2.1, 0, 0), new Vector3d(3, 2, 2))));

        // With tolerance, a small gap still intersects.
        var tol = new Tolerance(0.2, 0.2);
        Assert.True(a.Intersects(new Aabb(new Vector3d(2.1, 0, 0), new Vector3d(3, 2, 2)), tol));
    }

    [Fact]
    public void Intersection_OfOverlappingBoxes()
    {
        var a = new Aabb(new Vector3d(0, 0, 0), new Vector3d(2, 2, 2));
        var b = new Aabb(new Vector3d(1, 1, 1), new Vector3d(3, 3, 3));
        var i = a.Intersection(b);
        Assert.Equal(new Vector3d(1, 1, 1), i.Min);
        Assert.Equal(new Vector3d(2, 2, 2), i.Max);

        var disjoint = new Aabb(new Vector3d(5, 5, 5), new Vector3d(6, 6, 6));
        Assert.True(a.Intersection(disjoint).IsEmpty);
    }

    [Fact]
    public void CenterSizeVolumeSurfaceArea()
    {
        var box = new Aabb(new Vector3d(0, 0, 0), new Vector3d(2, 4, 6));
        Assert.Equal(new Vector3d(1, 2, 3), box.Center);
        Assert.Equal(new Vector3d(2, 4, 6), box.Size);
        Assert.Equal(48, box.Volume, 12);
        Assert.Equal(2 * (8 + 24 + 12), box.SurfaceArea, 12);
        Assert.Equal(2, box.LongestAxis);
    }

    [Fact]
    public void ClosestPointAndDistance()
    {
        var box = new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1));
        Assert.Equal(new Vector3d(0.5, 0.5, 0.5), box.ClosestPoint(new Vector3d(0.5, 0.5, 0.5)));
        Assert.Equal(new Vector3d(1, 1, 0.5), box.ClosestPoint(new Vector3d(4, 2, 0.5)));
        Assert.Equal(3, box.DistanceTo(new Vector3d(4, 1, 1)), 12);
        Assert.Equal(0, box.DistanceTo(new Vector3d(0.2, 0.3, 0.4)), 12);
    }

    [Fact]
    public void Expanded_GrowsAllSides()
    {
        var box = new Aabb(new Vector3d(0, 0, 0), new Vector3d(1, 1, 1)).Expanded(0.5);
        Assert.Equal(new Vector3d(-0.5, -0.5, -0.5), box.Min);
        Assert.Equal(new Vector3d(1.5, 1.5, 1.5), box.Max);
    }
}
