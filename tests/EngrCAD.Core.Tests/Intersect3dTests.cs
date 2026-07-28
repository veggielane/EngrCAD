using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// <see cref="Intersect3d.RayTriangle"/> — the one ray/triangle test the whole codebase
/// shares. The contract worth pinning is the SPLIT: the predicate answers "does the
/// ray's line cross this triangle, and where", and leaves every acceptance range to the
/// caller, which is exactly where its two former copies differed.
/// </summary>
public class Intersect3dTests
{
    private static readonly Vector3d A = new(0, 0, 0);
    private static readonly Vector3d B = new(1, 0, 0);
    private static readonly Vector3d C = new(0, 1, 0);

    [Fact]
    public void HitsThroughTheInterior_AtTheParameterAlongTheDirection()
    {
        var ray = new Ray3d((0.25, 0.25, -2), (0, 0, 1));
        Assert.True(Intersect3d.RayTriangle(ray, A, B, C, out double t));
        Assert.Equal(2, t, 12);
        Assert.Equal(new Vector3d(0.25, 0.25, 0), ray.PointAt(t));
    }

    /// <summary>t is in units of the direction's LENGTH, which is what lets an occlusion
    /// probe carry its search radius in the direction vector and accept t &lt;= 1.</summary>
    [Fact]
    public void ParameterScalesWithTheDirectionsLength()
    {
        var unit = new Ray3d((0.25, 0.25, -2), (0, 0, 1));
        var scaled = new Ray3d((0.25, 0.25, -2), (0, 0, 4));
        Assert.True(Intersect3d.RayTriangle(unit, A, B, C, out double tUnit));
        Assert.True(Intersect3d.RayTriangle(scaled, A, B, C, out double tScaled));
        Assert.Equal(tUnit / 4, tScaled, 12);
    }

    /// <summary>A hit BEHIND the origin is reported with a negative t rather than
    /// filtered — the caller decides whether that counts.</summary>
    [Fact]
    public void HitsBehindTheOrigin_ReportNegativeT()
    {
        var ray = new Ray3d((0.25, 0.25, 3), (0, 0, 1));
        Assert.True(Intersect3d.RayTriangle(ray, A, B, C, out double t));
        Assert.Equal(-3, t, 12);
    }

    [Fact]
    public void MissesOutsideTheTriangle()
    {
        var ray = new Ray3d((0.9, 0.9, -2), (0, 0, 1));   // beyond the hypotenuse
        Assert.False(Intersect3d.RayTriangle(ray, A, B, C, out _));
        Assert.False(Intersect3d.RayTriangle(
            new Ray3d((-0.1, 0.5, -2), (0, 0, 1)), A, B, C, out _));
    }

    [Fact]
    public void ParallelRayMisses()
    {
        var ray = new Ray3d((0.25, 0.25, 1), (1, 0, 0));
        Assert.False(Intersect3d.RayTriangle(ray, A, B, C, out _));
    }

    /// <summary>A collapsed triangle has no interior; the guard must return false rather
    /// than divide by a zero determinant.</summary>
    [Fact]
    public void DegenerateTriangleMisses()
    {
        var ray = new Ray3d((0.25, 0, -2), (0, 0, 1));
        Assert.False(Intersect3d.RayTriangle(ray, A, B, B, out _));
        Assert.False(Intersect3d.RayTriangle(ray, A, A, A, out _));
    }

    /// <summary>Winding does not matter: a back-facing triangle is still hit (the
    /// occlusion probe needs both, and the shading pass reads the normal itself).</summary>
    [Fact]
    public void BackFacingTriangleIsStillHit()
    {
        var ray = new Ray3d((0.25, 0.25, -2), (0, 0, 1));
        Assert.True(Intersect3d.RayTriangle(ray, A, C, B, out double t));
        Assert.Equal(2, t, 12);
    }
}
