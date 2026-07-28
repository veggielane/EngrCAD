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

    // ---- rotate-snap (drag on the cube settles onto a standard orientation) ----

    [Fact]
    public void ViewDirectionInvertsPoseFor()
    {
        // The two are exact inverses away from the poles, which is what makes
        // "snap to the nearest standard view" well defined.
        foreach (var direction in new Vector3d[] { (0, -1, 0), (1, 0, 0), (1, -1, 1), (-1, 1, -1) })
        {
            var unit = direction.Normalized();
            var (yaw, pitch) = ViewCubeMath.PoseFor(direction, currentYaw: 0.3);
            var back = ViewCubeMath.ViewDirection(yaw, pitch);
            Assert.Equal(unit.X, back.X, 9);
            Assert.Equal(unit.Y, back.Y, 9);
            Assert.Equal(unit.Z, back.Z, 9);
        }
    }

    [Fact]
    public void SnapPicksTheNearestFace()
    {
        // A pose a few degrees off Front snaps back to Front, not to an edge.
        var (yaw, pitch) = ViewCubeMath.PoseFor((0, -1, 0), currentYaw: 0);
        var snapped = ViewCubeMath.NearestStandardDirection(yaw + 0.12, pitch + 0.09);
        Assert.Equal(new Vector3d(0, -1, 0), snapped);
    }

    [Fact]
    public void SnapPicksEdgesAndCorners()
    {
        var (edgeYaw, edgePitch) = ViewCubeMath.PoseFor((1, -1, 0), currentYaw: 0);
        Assert.Equal(new Vector3d(1, -1, 0), ViewCubeMath.NearestStandardDirection(edgeYaw + 0.05, edgePitch));

        var (isoYaw, isoPitch) = ViewCubeMath.PoseFor((1, -1, 1), currentYaw: 0);
        Assert.Equal(new Vector3d(1, -1, 1), ViewCubeMath.NearestStandardDirection(isoYaw - 0.05, isoPitch + 0.04));
    }

    [Fact]
    public void SnapIsIdempotent()
    {
        // Snapping an already-snapped pose must not drift to a neighbour.
        foreach (var direction in new Vector3d[]
                 { (0, -1, 0), (1, 0, 0), (0, 1, 0), (-1, 0, 0), (1, -1, 0), (1, -1, 1), (-1, 1, -1) })
        {
            var (yaw, pitch) = ViewCubeMath.PoseFor(direction, currentYaw: 0);
            var once = ViewCubeMath.NearestStandardDirection(yaw, pitch);
            Assert.Equal(direction, once);
            var (yaw2, pitch2) = ViewCubeMath.PoseFor(once, yaw);
            Assert.Equal(once, ViewCubeMath.NearestStandardDirection(yaw2, pitch2));
        }
    }

    [Fact]
    public void SnapNeverReturnsTheZeroDirection()
    {
        // Sweep the orbit space: every pose must land on one of the 26 cube directions.
        for (double yaw = -Math.PI; yaw <= Math.PI; yaw += 0.37)
        {
            for (double pitch = -1.5; pitch <= 1.5; pitch += 0.31)
            {
                var snapped = ViewCubeMath.NearestStandardDirection(yaw, pitch);
                Assert.True(snapped.LengthSquared > 0);
                foreach (double component in new[] { snapped.X, snapped.Y, snapped.Z })
                    Assert.Contains(component, new[] { -1.0, 0.0, 1.0 });
            }
        }
    }

    [Fact]
    public void SnapNearThePoleReachesTopOrBottom()
    {
        Assert.Equal(new Vector3d(0, 0, 1), ViewCubeMath.NearestStandardDirection(0.7, ViewCubeMath.PitchLimit));
        Assert.Equal(new Vector3d(0, 0, -1), ViewCubeMath.NearestStandardDirection(0.7, -ViewCubeMath.PitchLimit));
    }

    [Fact]
    public void CubeRegionTestMatchesTheClickRegion()
    {
        // The press check that arms rotate-snap must agree with the click router.
        const double w = 800, h = 600;
        double inX = w - ViewCubeMath.RegionMarginDip - ViewCubeMath.RegionSizeDip / 2;
        double inY = ViewCubeMath.RegionMarginDip + ViewCubeMath.RegionSizeDip / 2;
        Assert.True(ViewCube.InRegion(inX, inY, w, h));
        Assert.False(ViewCube.InRegion(w / 2, h / 2, w, h));
        Assert.False(ViewCube.InRegion(inX, h - 10, w, h));
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
    public void AnimationZeroDurationLandsImmediately()
    {
        // durationSeconds: 0 must not divide by zero: the very first evaluation (even
        // at elapsed 0) returns the exact target pose and reports Done, so the render
        // loop's single Step lands the camera and clears the animation.
        var animation = new ViewCubeAnimation(0.4, 0.1, -1.2, 0.6, durationSeconds: 0);
        var (yaw, pitch, done) = animation.Evaluate(0);
        Assert.Equal(-1.2, yaw, Tol);
        Assert.Equal(0.6, pitch, Tol);
        Assert.True(done);
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

    // ---- hover ----

    [Fact]
    public void HoverThrottleAcceptsFirstSampleAndGatesByDistance()
    {
        var throttle = new HoverThrottle(4.0);
        Assert.True(throttle.ShouldSample(100, 100));    // first sample always accepted
        Assert.False(throttle.ShouldSample(101, 102));   // sqrt(5) < 4: gated
        Assert.False(throttle.ShouldSample(103, 100));   // still within 4 of (100,100)
        Assert.True(throttle.ShouldSample(104, 100));    // exactly 4: accepted
        Assert.False(throttle.ShouldSample(105, 100));   // now gated against (104,100)
    }

    [Fact]
    public void HoverThrottleResetForcesResample()
    {
        var throttle = new HoverThrottle(4.0);
        Assert.True(throttle.ShouldSample(50, 50));
        Assert.False(throttle.ShouldSample(51, 51));
        throttle.Reset();
        Assert.True(throttle.ShouldSample(51, 51));      // same spot, but reset re-picks
    }

    [Fact]
    public void CubeHoverTracksRegionAndReportsChangesOnly()
    {
        var cube = new ViewCube();
        double width = 800, height = 600;
        double cx = width - ViewCubeMath.RegionMarginDip - ViewCubeMath.RegionSizeDip / 2;
        double cy = ViewCubeMath.RegionMarginDip + ViewCubeMath.RegionSizeDip / 2;

        // Center of the region at the front view: hovering the FRONT face.
        Assert.True(cube.UpdateHover(cx, cy, width, height, FrontYaw, 0, out bool changed));
        Assert.True(changed);
        Assert.Equal(new Vector3d(0, -1, 0), cube.Hover);

        // Same spot again: still inside, no change.
        Assert.True(cube.UpdateHover(cx, cy, width, height, FrontYaw, 0, out changed));
        Assert.False(changed);

        // Slide toward the right band: the front-right edge, one change.
        double edgeX = cx + 0.45 * ViewCubeMath.RegionSizeDip / 2;
        Assert.True(cube.UpdateHover(edgeX, cy, width, height, FrontYaw, 0, out changed));
        Assert.True(changed);
        Assert.Equal(new Vector3d(1, -1, 0), cube.Hover);

        // Outside the region: not inside, hover cleared (one change).
        Assert.False(cube.UpdateHover(400, 300, width, height, FrontYaw, 0, out changed));
        Assert.True(changed);
        Assert.Null(cube.Hover);
        Assert.False(cube.UpdateHover(400, 300, width, height, FrontYaw, 0, out changed));
        Assert.False(changed);
    }

    [Fact]
    public void ClearHoverReportsChangeOnlyWhenSomethingWasHovered()
    {
        var cube = new ViewCube();
        Assert.False(cube.ClearHover());                 // nothing hovered yet
        double width = 800, height = 600;
        double cx = width - ViewCubeMath.RegionMarginDip - ViewCubeMath.RegionSizeDip / 2;
        double cy = ViewCubeMath.RegionMarginDip + ViewCubeMath.RegionSizeDip / 2;
        cube.UpdateHover(cx, cy, width, height, FrontYaw, 0, out _);
        Assert.True(cube.ClearHover());
        Assert.Null(cube.Hover);
        Assert.False(cube.ClearHover());
    }

    [Fact]
    public void CubeHoverInRegionButOffTheCubeIsNull()
    {
        var cube = new ViewCube();
        double width = 800, height = 600;
        // Region corner: inside the square but outside the cube's silhouette.
        double x = width - ViewCubeMath.RegionMarginDip - 1;
        double y = ViewCubeMath.RegionMarginDip + 1;
        Assert.True(cube.UpdateHover(x, y, width, height, FrontYaw, 0, out _));
        Assert.Null(cube.Hover);
    }

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

    /// <summary>
    /// The widget and a DRAWING SHEET must mean the same thing by "front". They can only
    /// be sure of that by reading one table, so the vectors live in
    /// <c>EngrCAD.Modeling.StandardViews</c> (beneath both) and this is the assertion
    /// that the delegation is real — read FROM the source, never re-typed here, because
    /// a copied table agrees with a broken implementation as happily as a correct one.
    /// </summary>
    [Fact]
    public void StandardViewTableIsTheModellingLayers()
    {
        Assert.Equal(EngrCAD.Modeling.StandardViews.Names, ViewCubeMath.StandardViewNames);
        foreach (string name in ViewCubeMath.StandardViewNames)
        {
            Assert.Equal(
                EngrCAD.Modeling.StandardViews.DirectionFor(name),
                ViewCubeMath.DirectionFor(name));
        }
        Assert.Null(ViewCubeMath.DirectionFor("sideways"));
    }

    /// <summary>
    /// A drawing view's sheet frame looks along the cube's direction EXACTLY; the orbit
    /// camera's own pose for the same name is that direction clamped
    /// <see cref="ViewCubeMath.PitchLimit"/> shy of the pole, which is a property of a
    /// LookAt up vector and not a disagreement about what "top" means. Pinning both
    /// halves is what makes the difference legible instead of a mystery 5e-5.
    /// </summary>
    [Theory]
    [InlineData("front")]
    [InlineData("back")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("bottom")]
    public void SheetFrameLooksAlongTheCubesDirection(string name)
    {
        var direction = ViewCubeMath.DirectionFor(name)!.Value;
        var sheet = EngrCAD.Modeling.StandardViews.SheetFrame(direction);
        Assert.Equal(1, direction.Normalized().Dot(sheet.Z), 12);

        var (yaw, pitch) = ViewCubeMath.PoseFor(direction, 0);
        double clamped = ViewCubeMath.ViewDirection(yaw, pitch).Dot(sheet.Z);
        // The whole gap is the pitch clamp: at most (pi/2 - PitchLimit) of angle, and
        // exactly zero for the four horizontal views.
        Assert.True(clamped >= Math.Cos(Math.PI / 2 - ViewCubeMath.PitchLimit) - 1e-12,
            $"'{name}' camera direction is {Math.Acos(clamped):G3} rad off the sheet frame");
    }
}
