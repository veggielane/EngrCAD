using EngrCAD.Core;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The section planes' clip rule as picking applies it. This must stay identical to
/// the fragment shaders' discard — per plane <c>dot(world, normal) &gt; offset</c>,
/// combined by <see cref="SectionCombine"/> — because a click may not select a surface
/// the cut removed, and multi-plane cutaways would otherwise let picking and rendering
/// disagree about which corner is gone.
/// </summary>
public class SectionPickTests
{
    [Fact]
    public void NothingIsHiddenWhileSectionModeIsOff()
    {
        Assert.False(SectionClip.Hides(false, (0, 0, 100), SectionAxis.Z, 0));
        Assert.False(SectionClip.Hides(false, (100, 0, 0), SectionAxis.X, -50));
    }

    [Theory]
    [InlineData(SectionAxis.X)]
    [InlineData(SectionAxis.Y)]
    [InlineData(SectionAxis.Z)]
    public void PointsBeyondThePlaneAreHiddenOnEveryAxis(SectionAxis axis)
    {
        var beyond = axis switch
        {
            SectionAxis.X => new Vector3d(6, 0, 0),
            SectionAxis.Y => new Vector3d(0, 6, 0),
            _ => new Vector3d(0, 0, 6),
        };
        Assert.True(SectionClip.Hides(true, beyond, axis, 5));
        Assert.False(SectionClip.Hides(true, -beyond, axis, 5));
    }

    [Fact]
    public void TheCutPlaneItselfIsVisible()
    {
        // The shader discards strictly greater than the offset, so the exposed cut
        // face (which sits exactly on the plane) stays pickable.
        Assert.False(SectionClip.Hides(true, (0, 0, 5), SectionAxis.Z, 5));
        Assert.True(SectionClip.Hides(true, (0, 0, 5.0001), SectionAxis.Z, 5));
    }

    [Fact]
    public void OtherAxesDoNotAffectTheTest()
    {
        // Only the active axis' component matters — a far-out X does not hide a point
        // when the plane cuts Z.
        Assert.False(SectionClip.Hides(true, (999, -999, 1), SectionAxis.Z, 5));
    }

    // ---- several planes at once: picking must agree with the shader's combine rule ----

    private static readonly SectionPlane[] QuarterCut =
        [SectionPlane.On(SectionAxis.X, 0), SectionPlane.On(SectionAxis.Y, 0)];

    [Fact]
    public void IntersectionHidesOnlyTheCornerWhereEveryPlaneExcludes()
    {
        // The quarter cut removes the +x+y quadrant and nothing else: a point excluded
        // by one plane alone is still material, and still clickable.
        Assert.True(SectionClip.Hides(true, (5, 5, 0), QuarterCut, SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, (5, -5, 0), QuarterCut, SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, (-5, 5, 0), QuarterCut, SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, (-5, -5, 0), QuarterCut, SectionCombine.Intersection));
    }

    [Fact]
    public void UnionHidesWhereAnyPlaneExcludes()
    {
        // Each plane cuts independently, so only the quadrant every plane keeps survives.
        Assert.True(SectionClip.Hides(true, (5, 5, 0), QuarterCut, SectionCombine.Union));
        Assert.True(SectionClip.Hides(true, (5, -5, 0), QuarterCut, SectionCombine.Union));
        Assert.True(SectionClip.Hides(true, (-5, 5, 0), QuarterCut, SectionCombine.Union));
        Assert.False(SectionClip.Hides(true, (-5, -5, 0), QuarterCut, SectionCombine.Union));
    }

    [Fact]
    public void OnePlaneMakesBothCombineRulesCoincide()
    {
        SectionPlane[] single = [SectionPlane.On(SectionAxis.Z, 5)];
        foreach (var combine in new[] { SectionCombine.Intersection, SectionCombine.Union })
        {
            Assert.True(SectionClip.Hides(true, (0, 0, 6), single, combine));
            Assert.False(SectionClip.Hides(true, (0, 0, 4), single, combine));
        }
    }

    [Fact]
    public void AnOctantNeedsAllThreePlanesToExclude()
    {
        SectionPlane[] octant =
            [SectionPlane.On(SectionAxis.X, 0), SectionPlane.On(SectionAxis.Y, 0),
             SectionPlane.On(SectionAxis.Z, 4)];
        Assert.True(SectionClip.Hides(true, (5, 5, 9), octant, SectionCombine.Intersection));
        // Below the z plane: two of three exclude, so the material stays.
        Assert.False(SectionClip.Hides(true, (5, 5, 1), octant, SectionCombine.Intersection));
    }

    [Fact]
    public void NoPlanesHidesNothingEvenWhenEnabled()
    {
        Assert.False(SectionClip.Hides(true, (99, 99, 99), [], SectionCombine.Intersection));
    }

    [Fact]
    public void GeneralNonAxisAlignedPlanesWork()
    {
        // The shader has always taken a general normal; the CPU rule must too.
        SectionPlane[] diagonal = [new(new Vector3d(1, 1, 0).Normalized(), 0)];
        Assert.True(SectionClip.Hides(true, (1, 1, 0), diagonal, SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, (-1, -1, 0), diagonal, SectionCombine.Intersection));
    }
}
