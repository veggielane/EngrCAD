using EngrCAD.Core;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The section plane's clip rule as picking applies it. This must stay identical to
/// the fragment shaders' discard (<c>dot(world, uSectionAxis) &gt; uSectionOffset</c>)
/// — a click may not select a surface the cut removed.
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
}
