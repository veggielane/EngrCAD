using EngrCAD.Core;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// View-cube math: the pose table (must reproduce the toolbar's Front/Top/Right/Iso
/// yaw/pitch exactly), shortest-path yaw wrapping, the eased animation, and the
/// screen-space hit test (mini-projection ortho ray vs the unit cube with band
/// classification). Pure math — no GL, no Avalonia.
/// </summary>
public class ViewCubeTests
{
    private const double Tol = 1e-12;

    // ---- pose table (locked to the SceneHost toolbar values) ----

    [Fact]
    public void FrontFacePoseMatchesToolbarFront()
    {
        var (yaw, pitch) = ViewCubeMath.PoseFor((0, -1, 0), currentYaw: 0.7);
        Assert.Equal(-Math.PI / 2, yaw, Tol);
        Assert.Equal(0, pitch, Tol);
    }

    [Fact]
    public void RightFacePoseMatchesToolbarRight()
    {
        var (yaw, pitch) = ViewCubeMath.PoseFor((1, 0, 0), currentYaw: 0.7);
        Assert.Equal(0, yaw, Tol);
        Assert.Equal(0, pitch, Tol);
    }

    [Fact]
    public void IsoCornerPoseMatchesToolbarIso()
    {
        // The (+X, -Y, +Z) corner is exactly the toolbar's Iso view.
        var (yaw, pitch) = ViewCubeMath.PoseFor((1, -1, 1), currentYaw: 0.7);
        Assert.Equal(-Math.PI / 4, yaw, Tol);
        Assert.Equal(Math.Asin(1 / Math.Sqrt(3)), pitch, Tol);
    }

    [Fact]
    public void TopFaceKeepsCurrentYawAndClampsPitch()
    {
        // Yaw is unconstrained looking straight down +Z; pitch stops at the orbit
        // clamp (the toolbar's pi/2 is clamped identically by the Camera setter).
        var (yaw, pitch) = ViewCubeMath.PoseFor((0, 0, 1), currentYaw: 0.7);
        Assert.Equal(0.7, yaw, Tol);
        Assert.Equal(Math.PI / 2 - 0.01, pitch, Tol);

        var (_, bottomPitch) = ViewCubeMath.PoseFor((0, 0, -1), currentYaw: 0.7);
        Assert.Equal(-(Math.PI / 2 - 0.01), bottomPitch, Tol);
    }

    [Fact]
    public void FrontRightEdgePoseIsHalfwayBetweenFaces()
    {
        var (yaw, pitch) = ViewCubeMath.PoseFor((1, -1, 0), currentYaw: 0);
        Assert.Equal(-Math.PI / 4, yaw, Tol);   // 45 degrees between Front and Right
        Assert.Equal(0, pitch, Tol);
    }

    [Fact]
    public void BackLeftBottomCornerPose()
    {
        var (yaw, pitch) = ViewCubeMath.PoseFor((-1, 1, -1), currentYaw: 0);
        Assert.Equal(3 * Math.PI / 4, yaw, Tol);
        Assert.Equal(-Math.Asin(1 / Math.Sqrt(3)), pitch, Tol);
    }

    // ---- shortest-path yaw ----

    [Theory]
    [InlineData(3.0, -3.0)]     // near +pi to near -pi: must wrap, not go the long way
    [InlineData(0.0, Math.PI)]
    [InlineData(0.1, -0.1)]
    [InlineData(10.0, 0.0)]     // accumulated multi-turn yaw
    [InlineData(-7.5, 2.0)]
    public void ShortestYawTargetIsWithinHalfATurnAndEquivalent(double from, double to)
    {
        double target = ViewCubeMath.ShortestYawTarget(from, to);
        Assert.True(Math.Abs(target - from) <= Math.PI + Tol,
            $"|{target} - {from}| exceeds pi");
        // Equivalent angle: differs from the requested yaw by a whole number of turns.
        double turns = (target - to) / (2 * Math.PI);
        Assert.Equal(Math.Round(turns), turns, 9);
    }

    [Fact]
    public void ShortestYawWrapExample()
    {
        // 3.0 -> -3.0 rad: the short way is +0.283 rad forward through pi, not -6 back.
        double target = ViewCubeMath.ShortestYawTarget(3.0, -3.0);
        Assert.Equal(3.0 + (2 * Math.PI - 6.0), target, Tol);
    }

    // ---- animation ----

    [Fact]
    public void AnimationStartsAtStartAndEndsExactlyAtTarget()
    {
        var animation = new ViewCubeAnimation(0.4, 0.1, -1.2, 0.6, durationSeconds: 0.25);
        var (y0, p0, done0) = animation.Evaluate(0);
        Assert.Equal(0.4, y0, Tol);
        Assert.Equal(0.1, p0, Tol);
        Assert.False(done0);

        var (y1, p1, done1) = animation.Evaluate(0.25);
        Assert.Equal(-1.2, y1, Tol);
        Assert.Equal(0.6, p1, Tol);
        Assert.True(done1);

        var (y2, p2, done2) = animation.Evaluate(5.0);   // past the end: stays landed
        Assert.Equal(-1.2, y2, Tol);
        Assert.Equal(0.6, p2, Tol);
        Assert.True(done2);
    }

    [Fact]
    public void AnimationMidpointIsBetweenPoses()
    {
        var animation = new ViewCubeAnimation(0, 0, 1, 0.5, durationSeconds: 0.25);
        var (y, p, done) = animation.Evaluate(0.125);
        Assert.Equal(0.5, y, Tol);      // smoothstep(0.5) = 0.5 exactly
        Assert.Equal(0.25, p, Tol);
        Assert.False(done);
    }

    [Fact]
    public void AnimationTakesShortestYawPath()
    {
        var animation = new ViewCubeAnimation(3.0, 0, -3.0, 0);
        var (yaw, _, _) = animation.Evaluate(10);
        Assert.Equal(3.0 + (2 * Math.PI - 6.0), yaw, Tol);
    }

    [Fact]
    public void AnimationClampsTargetPitchToOrbitLimit()
    {
        var animation = new ViewCubeAnimation(0, 0, 0, Math.PI / 2);
        var (_, pitch, _) = animation.Evaluate(10);
        Assert.Equal(Math.PI / 2 - 0.01, pitch, Tol);
    }

    // ---- screen-space hit test ----

    private const double FrontYaw = -Math.PI / 2;   // camera on -Y looking at FRONT

    [Fact]
    public void CenterClickFromFrontViewHitsFrontFace()
    {
        Assert.True(ViewCubeMath.TryHit(FrontYaw, 0, 0, 0, out var direction));
        Assert.Equal(0, direction.X, Tol);
        Assert.Equal(-1, direction.Y, Tol);
        Assert.Equal(0, direction.Z, Tol);
    }

    [Fact]
    public void OffCenterClickInsideBandIsStillTheFace()
    {
        // u = 0.2 maps to x = 0.38 on the face — inside the 0.55 band.
        Assert.True(ViewCubeMath.TryHit(FrontYaw, 0, 0.2, 0.1, out var direction));
        Assert.Equal(new Vector3d(0, -1, 0), direction);
    }

    [Fact]
    public void RightBandClickFromFrontViewIsFrontRightEdge()
    {
        // u = 0.45 maps to x = 0.855 on the face: past the 0.55 band -> edge region.
        Assert.True(ViewCubeMath.TryHit(FrontYaw, 0, 0.45, 0, out var direction));
        Assert.Equal(new Vector3d(1, -1, 0), direction);
    }

    [Fact]
    public void TopBandClickFromFrontViewIsFrontTopEdge()
    {
        Assert.True(ViewCubeMath.TryHit(FrontYaw, 0, 0, 0.45, out var direction));
        Assert.Equal(new Vector3d(0, -1, 1), direction);
    }

    [Fact]
    public void CornerBandClickFromFrontViewIsIsoCorner()
    {
        Assert.True(ViewCubeMath.TryHit(FrontYaw, 0, 0.45, 0.45, out var direction));
        Assert.Equal(new Vector3d(1, -1, 1), direction);
    }

    [Fact]
    public void ClickOutsideCubeSilhouetteMisses()
    {
        Assert.False(ViewCubeMath.TryHit(FrontYaw, 0, 0.9, 0, out _));
        Assert.False(ViewCubeMath.TryHit(FrontYaw, 0, 0, -0.95, out _));
    }

    [Fact]
    public void CenterClickFromIsoViewIsTheFacingCorner()
    {
        // At the Iso pose the (1,-1,1) corner faces the camera dead-center.
        Assert.True(ViewCubeMath.TryHit(
            -Math.PI / 4, Math.Asin(1 / Math.Sqrt(3)), 0, 0, out var direction));
        Assert.Equal(new Vector3d(1, -1, 1), direction);
    }

    [Fact]
    public void ClickingTheVisibleFaceRoundTripsToItsOwnPose()
    {
        // From any face-on view, a center click must re-target the same pose.
        foreach (var (yaw, pitch) in new[] { (FrontYaw, 0.0), (0.0, 0.0), (Math.PI / 2, 0.0), (Math.PI, 0.0) })
        {
            Assert.True(ViewCubeMath.TryHit(yaw, pitch, 0, 0, out var direction));
            var (targetYaw, targetPitch) = ViewCubeMath.PoseFor(direction, yaw);
            Assert.Equal(0, Math.IEEERemainder(targetYaw - yaw, 2 * Math.PI), 1e-9);
            Assert.Equal(pitch, targetPitch, 1e-9);
        }
    }

    // ---- region mapping ----

    [Fact]
    public void RegionCenterMapsToNdcOrigin()
    {
        double width = 800, height = 600;
        double cx = width - ViewCubeMath.RegionMarginDip - ViewCubeMath.RegionSizeDip / 2;
        double cy = ViewCubeMath.RegionMarginDip + ViewCubeMath.RegionSizeDip / 2;
        Assert.True(ViewCubeMath.TryMapToRegion(cx, cy, width, height, out double u, out double v));
        Assert.Equal(0, u, Tol);
        Assert.Equal(0, v, Tol);
    }

    [Fact]
    public void RegionCornersMapToNdcExtremes()
    {
        double width = 800, height = 600;
        double left = width - ViewCubeMath.RegionMarginDip - ViewCubeMath.RegionSizeDip;
        double top = ViewCubeMath.RegionMarginDip;
        Assert.True(ViewCubeMath.TryMapToRegion(left, top, width, height, out double u, out double v));
        Assert.Equal(-1, u, Tol);
        Assert.Equal(1, v, Tol);
    }

    [Fact]
    public void PointsOutsideTheRegionAreRejected()
    {
        Assert.False(ViewCubeMath.TryMapToRegion(400, 300, 800, 600, out _, out _));
        Assert.False(ViewCubeMath.TryMapToRegion(795, 200, 800, 600, out _, out _)); // below region
        Assert.False(ViewCubeMath.TryMapToRegion(600, 50, 800, 600, out _, out _));  // left of region
    }

    // ---- labels ----

    [Theory]
    [InlineData(0, -1, 0, "front")]
    [InlineData(0, 1, 0, "back")]
    [InlineData(1, 0, 0, "right")]
    [InlineData(-1, 0, 0, "left")]
    [InlineData(0, 0, 1, "top")]
    [InlineData(0, 0, -1, "bottom")]
    [InlineData(1, -1, 0, "front-right")]
    [InlineData(1, -1, 1, "front-right-top")]
    [InlineData(-1, 1, -1, "back-left-bottom")]
    public void HitDirectionsNameThemselves(double x, double y, double z, string expected) =>
        Assert.Equal(expected, ViewCubeMath.Label(new Vector3d(x, y, z)));
}
