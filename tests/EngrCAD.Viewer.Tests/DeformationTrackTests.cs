using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The deformation track (DeformationTracks.cs in Viewer.Core) — headless, no GL, because
/// <c>Animation.At</c> is a pure function of t and a deformation track is a scalar law.
/// <para>What is pinned here is that a deformation is a NUMBER on the timeline rather
/// than geometry, and that the standard laws hit their endpoints exactly: a load ramp
/// that returns to 0.9999 instead of 0 would leave a clip's last frame subtly deformed,
/// which is the sort of thing an eye never catches and a loop always does.</para>
/// </summary>
public class DeformationTrackTests
{
    [Fact]
    public void AnimationWithNoDeformationTrack_ReportsFactorOne()
    {
        // The convention every front end reads: null means "leave every part at its own
        // stated exaggeration", which is factor 1 and NOT factor 0.
        var sample = new Animation().At(0.5);
        Assert.Null(sample.DeformScale);
        Assert.Equal(1, sample.DeformFactor);
    }

    [Fact]
    public void SecondDeformationTrackIsRefused()
    {
        var animation = new Animation().With(DeformationTracks.LoadRamp());
        Assert.Throws<InvalidOperationException>(
            () => animation.With(DeformationTracks.Ramp()));
    }

    [Fact]
    public void LoadRamp_RunsZeroToPeakToZeroAndHitsBothEndsExactly()
    {
        var animation = new Animation().With(DeformationTracks.LoadRamp());
        Assert.Equal(0, animation.At(0).DeformFactor);
        Assert.Equal(0.5, animation.At(0.25).DeformFactor, 12);
        Assert.Equal(1, animation.At(0.5).DeformFactor, 12);
        Assert.Equal(0.5, animation.At(0.75).DeformFactor, 12);
        // Exactly 0 at the far end, so a looping clip closes on the undeformed shape.
        Assert.Equal(0, animation.At(1).DeformFactor);
    }

    [Fact]
    public void LoadRamp_TakesItsPeak()
    {
        Assert.Equal(2.5, new Animation().With(DeformationTracks.LoadRamp(2.5)).At(0.5).DeformFactor, 12);
    }

    [Fact]
    public void Ramp_IsLinearBetweenItsEnds()
    {
        var animation = new Animation().With(DeformationTracks.Ramp(0.25, 1.25));
        Assert.Equal(0.25, animation.At(0).DeformFactor, 12);
        Assert.Equal(0.75, animation.At(0.5).DeformFactor, 12);
        Assert.Equal(1.25, animation.At(1).DeformFactor, 12);
    }

    [Fact]
    public void Oscillate_SwingsBothWaysAndClosesOnZero()
    {
        // The mode-shape law: a whole number of cycles starts and ends at 0 so the clip
        // loops seamlessly, and it genuinely goes NEGATIVE (a mode swings both ways).
        var animation = new Animation().With(DeformationTracks.Oscillate(3, cycles: 2));
        Assert.Equal(0, animation.At(0).DeformFactor, 12);
        Assert.Equal(3, animation.At(0.125).DeformFactor, 12);
        Assert.Equal(-3, animation.At(0.375).DeformFactor, 12);
        Assert.Equal(0, animation.At(1).DeformFactor, 12);
    }

    [Fact]
    public void Constant_HoldsOneFactor()
    {
        var animation = new Animation().With(DeformationTracks.Constant(12));
        Assert.Equal(12, animation.At(0).DeformFactor);
        Assert.Equal(12, animation.At(0.42).DeformFactor);
        Assert.Equal(12, animation.At(1).DeformFactor);
    }

    [Fact]
    public void AWindowedTrack_HoldsItsBoundaryValuesOutsideTheWindow()
    {
        // Clamp semantics, the same as every other track: a deformation that finishes
        // early stays finished while a camera move plays out.
        var track = DeformationTracks.Ramp();
        track.Window(0.25, 0.75);
        var animation = new Animation().With(track);
        Assert.Equal(0, animation.At(0).DeformFactor, 12);
        Assert.Equal(0, animation.At(0.25).DeformFactor, 12);
        Assert.Equal(0.5, animation.At(0.5).DeformFactor, 12);
        Assert.Equal(1, animation.At(0.75).DeformFactor, 12);
        Assert.Equal(1, animation.At(1).DeformFactor, 12);
    }

    [Fact]
    public void EasingReachesTheDeformationTrackToo()
    {
        // Timeline easing is applied before any track sees t, so a smoothstepped clip
        // eases its deformation exactly as it eases its poses.
        var animation = new Animation(easing: AnimationEasing.Smoothstep)
            .With(DeformationTracks.Ramp());
        Assert.Equal(ViewCubeMath.Ease(0.3), animation.At(0.3).DeformFactor, 12);
    }

    [Fact]
    public void ADeformationTrackComposesWithPoseAndCameraTracks()
    {
        // Three tracks of three different kinds is the point: they cannot conflict,
        // because each writes something else (matrices, a camera, one scalar).
        var animation = new Animation()
            .With(new TurntableTrack(new CameraState(0, 0.4, 10, EngrCAD.Core.Vector3d.Zero)))
            .With(DeformationTracks.LoadRamp());
        var sample = animation.At(0.5);
        Assert.NotNull(sample.Camera);
        Assert.Equal(1, sample.DeformFactor, 12);
        Assert.Null(sample.Instances);   // no pose track: the scene's own instances stand
    }

    [Fact]
    public void From_TakesAnyLawAndIsWhereSequencingLives()
    {
        // An animation takes at most one deformation track, so a hold-then-release is one
        // function rather than two tracks with no defined composition.
        var animation = new Animation().With(
            DeformationTracks.From(t => t < 0.5 ? 1 : 2 - 2 * t));
        Assert.Equal(1, animation.At(0.25).DeformFactor, 12);
        Assert.Equal(1, animation.At(0.5).DeformFactor, 12);
        Assert.Equal(0, animation.At(1).DeformFactor, 12);
    }
}
