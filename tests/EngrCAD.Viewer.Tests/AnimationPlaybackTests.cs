using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The UI-free playback transport (<see cref="AnimationPlayback"/>) — what "play"
/// means lives here, not in the Avalonia layer, so the desktop toolbar, a future web
/// transport and these tests all drive the same state machine.
/// </summary>
public class AnimationPlaybackTests
{
    private static Animation TwoSecondTurntable() =>
        new Animation(durationSeconds: 2)
            .With(new TurntableTrack(new CameraState(0, 0.45, 10, Vector3d.Zero)));

    [Fact]
    public void AdvanceOnlyMovesWhilePlaying()
    {
        var playback = new AnimationPlayback(TwoSecondTurntable());
        Assert.False(playback.Playing);
        Assert.False(playback.Advance(0.5));   // paused: nothing to re-render
        Assert.Equal(0, playback.Time);

        playback.Play();
        Assert.True(playback.Advance(0.5));
        Assert.Equal(0.5, playback.Time, 12);
        Assert.Equal(0.25, playback.T, 12);

        playback.Pause();
        Assert.False(playback.Advance(0.5));
        Assert.Equal(0.5, playback.Time, 12);
    }

    [Fact]
    public void LoopWrapsTheOvershootSoSpeedIsTickIndependent()
    {
        var playback = new AnimationPlayback(TwoSecondTurntable());
        playback.Play();
        playback.Advance(1.9);
        // A 0.3 s tick past the 2 s end wraps to 0.2, not to 0 — playback speed must
        // not depend on where the timer's ticks happen to fall.
        Assert.True(playback.Advance(0.3));
        Assert.Equal(0.2, playback.Time, 12);
        Assert.True(playback.Playing);
    }

    [Fact]
    public void NonLoopClampsToTheEndAndPauses()
    {
        var playback = new AnimationPlayback(TwoSecondTurntable()) { Loop = false };
        playback.Play();
        Assert.True(playback.Advance(5));
        Assert.Equal(2, playback.Time);
        Assert.Equal(1, playback.T);
        Assert.False(playback.Playing);

        // Play at the end restarts from the beginning (the transport convention).
        playback.Play();
        Assert.Equal(0, playback.Time);
        Assert.True(playback.Playing);
    }

    [Fact]
    public void SeekIsClampedAndLegalWhilePlaying()
    {
        var playback = new AnimationPlayback(TwoSecondTurntable());
        playback.Seek(0.75);
        Assert.Equal(1.5, playback.Time, 12);
        playback.Seek(3);
        Assert.Equal(2, playback.Time);
        playback.Seek(-1);
        Assert.Equal(0, playback.Time);

        playback.Play();
        playback.Seek(0.5);
        Assert.True(playback.Playing);   // a scrub during playback just moves the clock
        Assert.Equal(1, playback.Time, 12);
    }

    [Fact]
    public void PlaybackPositionFeedsTheSamePureEvaluation()
    {
        // The transport owns WHERE; the animation owns WHAT — the sample at the
        // playback position is exactly Animation.At(T), the function exports evaluate.
        var animation = TwoSecondTurntable();
        var playback = new AnimationPlayback(animation);
        playback.Play();
        playback.Advance(0.6);
        Assert.Equal(animation.At(0.3).Camera, animation.At(playback.T).Camera);
    }

    [Fact]
    public void WithAnimationSetsTheOptionFactory()
    {
        var options = EngrCad.Configure()
            .WithAnimation(_ => TwoSecondTurntable())
            .Options;
        Assert.NotNull(options.Animation);
        Assert.NotNull(options.Animation!(new EngrCAD.Modeling.Scene()));
        Assert.Null(new EngrCadOptions().Animation);
    }
}
