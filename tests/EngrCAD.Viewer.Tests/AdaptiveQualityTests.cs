using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The camera-adaptive display-quality decision rule — pure state, no GL, no window.
/// <para>Its own backlog entry named the oracle: two camera distances must produce
/// segment counts differing the way the criterion predicts, and zooming back out must
/// never coarsen below the quality the session started at. Both are here, measured
/// through the production types (<see cref="AdaptiveQuality"/> feeding
/// <see cref="TessellationQuality"/>), plus the settle/hysteresis/ratchet behaviour that
/// keeps a drag from queueing a tessellation per frame.</para>
/// </summary>
public class AdaptiveQualityTests
{
    private const double ViewportHeight = 800;

    private static CameraState At(double distance) => new(0.7, 0.45, distance, Vector3d.Zero);

    // ---- the criterion ----

    [Fact]
    public void ChordDeviationIsHalfADevicePixelAtTheOrbitTarget()
    {
        // 2*d*tan(fov/2)/height is the world size of one pixel at the target plane; the
        // criterion asks for half of it.
        double expected = 0.5 * 2 * 300 * Math.Tan(CameraMath.FovY / 2) / ViewportHeight;
        Assert.Equal(expected, AdaptiveQuality.ChordDeviationFor(At(300), ViewportHeight), 15);
    }

    [Fact]
    public void ChordDeviationIsProportionalToDistance()
    {
        double far = AdaptiveQuality.ChordDeviationFor(At(400), ViewportHeight);
        double near = AdaptiveQuality.ChordDeviationFor(At(100), ViewportHeight);
        Assert.Equal(4.0, far / near, 12);
    }

    [Fact]
    public void ANonPositiveViewportHeightIsRefusedByName()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => AdaptiveQuality.ChordDeviationFor(At(100), 0));
        Assert.Contains("positive pixel count", error.Message);
    }

    // ---- THE ORACLE: two camera distances, segment counts differing as predicted ----

    [Fact]
    public void TwoCameraDistancesResolveSegmentCountsTheCriterionPredicts()
    {
        // A large-radius part: the chord criterion binds at the largest radius, so this
        // is where the on-screen size of a rim decides the count.
        var solid = Shape.Cylinder(radius: 200, height: 40).ToBrep();
        var rule = new AdaptiveQuality(baseline: null);

        var far = rule.QualityFor(AdaptiveQuality.ChordDeviationFor(At(1200), ViewportHeight));
        var near = rule.QualityFor(AdaptiveQuality.ChordDeviationFor(At(300), ViewportHeight));

        int farSegments = far.Tessellation!.ResolveFor(solid).SegmentsPerCircle;
        int nearSegments = near.Tessellation!.ResolveFor(solid).SegmentsPerCircle;

        // n ~ pi*sqrt(r/2d) and d scales with the distance, so a 4x closer camera asks
        // for 2x the segments. Assert the DIRECTION and the predicted RATIO, since the
        // ratio is what says the counts follow the criterion rather than merely differ.
        Assert.True(nearSegments > farSegments,
            $"zooming in must refine: far {farSegments}, near {nearSegments}");
        Assert.Equal(2.0, (double)nearSegments / farSegments, 1);
        Assert.Equal(40, farSegments);    // measured, and the arithmetic above predicts it
        Assert.Equal(80, nearSegments);

        // And each count is what the criterion asks of this radius, independently.
        Assert.Equal(far.Tessellation.SegmentsFor(200), farSegments);
        Assert.Equal(near.Tessellation.SegmentsFor(200), nearSegments);
    }

    // ---- THE PER-PART DEPTH: a part far from the orbit target is sized by ITSELF ----

    private static Vector3d EyeOf(CameraState c) =>
        CameraMath.Eye(c.Yaw, c.Pitch, c.Distance, c.Target);

    private static Vector3d AtDepth(CameraState c, double depth)
    {
        var eye = EyeOf(c);
        return eye + (c.Target - eye).Normalized() * depth;
    }

    [Fact]
    public void APointAtTheOrbitTargetAsksForExactlyTheSceneLevelDeviation()
    {
        // The generalisation must REDUCE to what it generalises: the target's own depth
        // IS the camera distance, so the per-part rule and the scene-level one agree
        // there. Anything else would mean the two spellings disagree about the case they
        // share, which is exactly how a "per-part" refinement quietly changes every part.
        var camera = At(300);
        Assert.Equal(
            AdaptiveQuality.ChordDeviationFor(camera, ViewportHeight),
            AdaptiveQuality.ChordDeviationFor(camera, ViewportHeight, camera.Target), 12);
    }

    [Fact]
    public void TheDeviationIsProportionalToThePartsOwnDepth()
    {
        // A pixel's world size grows linearly with depth, so this is an exact ratio, not
        // a trend: a part twice as far as the target may be half as finely meshed.
        var camera = At(300);
        double near = AdaptiveQuality.ChordDeviationFor(camera, ViewportHeight, AtDepth(camera, 150));
        double at = AdaptiveQuality.ChordDeviationFor(camera, ViewportHeight, camera.Target);
        double far = AdaptiveQuality.ChordDeviationFor(camera, ViewportHeight, AtDepth(camera, 600));
        Assert.Equal(0.5, near / at, 12);
        Assert.Equal(2.0, far / at, 12);
    }

    [Fact]
    public void DepthIsMeasuredALONGTheViewDirection_NotAsADistance()
    {
        // A part displaced SIDEWAYS is no further into the screen, so it must be sized
        // exactly as the target is — the discriminating case, since a naive
        // |worldPoint - eye| would make it coarser the further off-axis it sits.
        var camera = At(300);
        var eye = EyeOf(camera);
        var forward = (camera.Target - eye).Normalized();
        var sideways = forward.Cross(Vector3d.UnitZ).Normalized();
        var offAxis = camera.Target + sideways * 250;

        Assert.Equal(
            AdaptiveQuality.ChordDeviationFor(camera, ViewportHeight, camera.Target),
            AdaptiveQuality.ChordDeviationFor(camera, ViewportHeight, offAxis), 12);

        // and the naive reading really would differ, so the assertion above has teeth
        Assert.True((offAxis - eye).Length > 1.3 * camera.Distance);
    }

    [Fact]
    public void APartBehindTheEyeClampsRatherThanGoingNonPositive()
    {
        var camera = At(300);
        double clamped = AdaptiveQuality.ChordDeviationFor(
            camera, ViewportHeight, AtDepth(camera, -500));
        double floor = AdaptiveQuality.ChordDeviationFor(
            camera, ViewportHeight, AtDepth(camera, AdaptiveQuality.MinimumDepthFraction * 300));
        Assert.True(clamped > 0);
        Assert.Equal(floor, clamped, 12);
    }

    [Fact]
    public void TwoPartsAtDifferentDepthsResolveSegmentCountsTheCriterionPredicts()
    {
        // The oracle the residual asked for, in the same shape as the two-camera one:
        // one settled pose, two parts, segment counts following n ~ sqrt(1/depth).
        var solid = Shape.Cylinder(radius: 200, height: 40).ToBrep();
        var camera = At(300);
        var rule = new AdaptiveQuality(baseline: null);

        var near = rule.QualityFor(camera, ViewportHeight, AtDepth(camera, 300));
        var far = rule.QualityFor(camera, ViewportHeight, AtDepth(camera, 1200));

        int nearSegments = near.Tessellation!.ResolveFor(solid).SegmentsPerCircle;
        int farSegments = far.Tessellation!.ResolveFor(solid).SegmentsPerCircle;

        Assert.True(nearSegments > farSegments,
            $"the nearer part must be finer: near {nearSegments}, far {farSegments}");
        Assert.Equal(2.0, (double)nearSegments / farSegments, 1);
        Assert.Equal(80, nearSegments);
        Assert.Equal(40, farSegments);

        // And it is the SAME pair of numbers the two-camera oracle measures, which is the
        // point: moving the camera 4x closer and putting the part 4x nearer are one rule.
        Assert.Equal(
            rule.QualityFor(AdaptiveQuality.ChordDeviationFor(At(1200), ViewportHeight))
                .Tessellation!.ResolveFor(solid).SegmentsPerCircle,
            farSegments);
    }

    // ---- settle ----

    [Fact]
    public void AMovingCameraNeverAdopts()
    {
        var rule = new AdaptiveQuality(baseline: null);
        var clock = TimeSpan.Zero;
        for (int i = 0; i < 200; i++)
        {
            clock += TimeSpan.FromMilliseconds(16);   // ~60 fps of a wheel drag
            Assert.Null(rule.Observe(At(1000 - i), ViewportHeight, clock));
        }
    }

    [Fact]
    public void ASettledCameraAdoptsOnceAndOnlyOnce()
    {
        var rule = new AdaptiveQuality(baseline: null);
        var pose = At(500);

        Assert.Null(rule.Observe(pose, ViewportHeight, TimeSpan.Zero));
        Assert.Null(rule.Observe(pose, ViewportHeight, AdaptiveQuality.SettleDelay - TimeSpan.FromMilliseconds(1)));
        Assert.NotNull(rule.Observe(pose, ViewportHeight, AdaptiveQuality.SettleDelay));
        // Every later look at the SAME settled pose answers nothing: one evaluation per
        // settle, so a timer polling forever queues nothing.
        Assert.Null(rule.Observe(pose, ViewportHeight, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void AResizeRestartsTheSettleClock()
    {
        var rule = new AdaptiveQuality(baseline: null);
        var pose = At(500);
        Assert.Null(rule.Observe(pose, ViewportHeight, TimeSpan.Zero));
        // Same pose, different viewport: the criterion's answer changed, so this counts
        // as a move and the settle restarts.
        Assert.Null(rule.Observe(pose, ViewportHeight * 2, AdaptiveQuality.SettleDelay));
        Assert.NotNull(rule.Observe(pose, ViewportHeight * 2, AdaptiveQuality.SettleDelay * 2));
    }

    // ---- hysteresis ----

    [Fact]
    public void ASmallZoomInsideTheHysteresisBandAdoptsNothing()
    {
        var rule = new AdaptiveQuality(baseline: null);
        Assert.NotNull(Settle(rule, At(1000), TimeSpan.Zero));

        // 1.6x closer: finer, but not the factor of two the band demands.
        Assert.Null(Settle(rule, At(625), TimeSpan.FromSeconds(10)));

        // Past the band it adopts.
        Assert.NotNull(Settle(rule, At(400), TimeSpan.FromSeconds(20)));
    }

    // ---- the never-coarsen ratchet ----

    [Fact]
    public void ZoomingBackOutAdoptsNothing()
    {
        var rule = new AdaptiveQuality(baseline: null);
        Assert.NotNull(Settle(rule, At(200), TimeSpan.Zero));

        // Ten times further out — comfortably past the hysteresis band in the COARSE
        // direction, which the rule declines rather than obeys.
        Assert.Null(Settle(rule, At(2000), TimeSpan.FromSeconds(10)));
        Assert.Null(Settle(rule, At(20000), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void TheEmittedQualityCannotResolveBelowTheSessionFloor()
    {
        // A session that stated a fine fixed count: the adaptive layer's MinSegments is
        // that count, so a camera so far out that the chord criterion would ask for a
        // handful of segments still resolves to the session's own quality.
        var baseline = new MeshQuality { SegmentsPerCircle = 96, CurveSamples = 72 };
        var rule = new AdaptiveQuality(baseline);
        var quality = rule.QualityFor(AdaptiveQuality.ChordDeviationFor(At(1e6), ViewportHeight));

        var solid = Shape.Cylinder(radius: 5, height: 10).ToBrep();
        Assert.Equal(96, quality.Tessellation!.MinSegments);
        Assert.Equal(96, quality.Tessellation.ResolveFor(solid).SegmentsPerCircle);
    }

    [Fact]
    public void ASessionCriterionSurvivesAndIsOnlyEverTightened()
    {
        // A session that already stated an adaptive criterion keeps its angle rule and
        // clamps, and takes the FINER of the two deviations — so the adaptive layer can
        // refine what the session asked for and never loosen it.
        var session = new TessellationQuality
        {
            MaxAngleDegrees = 6,
            MaxChordDeviation = 0.05,
            MinSegments = 24,
            MaxSegments = 256,
        };
        var rule = new AdaptiveQuality(new MeshQuality { Tessellation = session });

        var tight = rule.QualityFor(0.001).Tessellation!;
        Assert.Equal(6, tight.MaxAngleDegrees);
        Assert.Equal(24, tight.MinSegments);
        Assert.Equal(256, tight.MaxSegments);
        Assert.Equal(0.001, tight.MaxChordDeviation);

        // A coarser camera deviation never loosens the session's stated one.
        Assert.Equal(0.05, rule.QualityFor(5.0).Tessellation!.MaxChordDeviation);
    }

    [Fact]
    public void ANonPositiveChordDeviationIsRefusedByName()
    {
        var rule = new AdaptiveQuality(baseline: null);
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.QualityFor(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.QualityFor(double.NaN));
    }

    [Fact]
    public void NoViewportYetAnswersNothingRatherThanThrowing()
    {
        // The window polls before it has been laid out; that is a wait, not a failure.
        var rule = new AdaptiveQuality(baseline: null);
        Assert.Null(rule.Observe(At(500), 0, TimeSpan.Zero));
        Assert.Null(rule.Observe(At(500), 0, AdaptiveQuality.SettleDelay * 4));
    }

    /// <summary>Observes a pose twice — once to start its settle clock, once a settle
    /// delay later — and returns whatever the second look answered.</summary>
    private static MeshQuality? Settle(AdaptiveQuality rule, CameraState pose, TimeSpan at)
    {
        rule.Observe(pose, ViewportHeight, at);
        return rule.Observe(pose, ViewportHeight, at + AdaptiveQuality.SettleDelay);
    }
}
