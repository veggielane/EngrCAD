namespace EngrCAD.Viewer;

/// <summary>
/// Playback state over an <see cref="Animation"/>: play/pause, loop, seek, and a
/// clock-driven <see cref="Advance"/>. UI-free and pure so every front end (the
/// desktop toolbar, a future web viewport, tests) drives the same transport — the
/// front end owns only a timer and the widgets; what "play" means lives here.
/// <para>Evaluation stays with the animation: this class tracks WHERE on the timeline
/// playback is, and callers render <c>Animation.At(T)</c> — the same pure function a
/// scrub or an export evaluates, which is the whole point.</para>
/// </summary>
public sealed class AnimationPlayback
{
    public AnimationPlayback(Animation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        Animation = animation;
    }

    /// <summary>The animation being played.</summary>
    public Animation Animation { get; }

    /// <summary>Whether the clock is running.</summary>
    public bool Playing { get; private set; }

    /// <summary>Wrap at the end (on) or stop there (off). Defaults on — turntables and
    /// mechanism cycles are the common case and they loop.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Playback position in seconds, in [0, Duration].</summary>
    public double Time { get; private set; }

    /// <summary>Playback position as timeline t ∈ [0, 1] — feed to <c>Animation.At</c>.</summary>
    public double T => Math.Clamp(Time / Animation.Duration, 0, 1);

    /// <summary>Starts the clock. Pressing play at the end of a non-looping run
    /// restarts from the beginning (the universal transport convention).</summary>
    public void Play()
    {
        if (!Loop && Time >= Animation.Duration)
            Time = 0;
        Playing = true;
    }

    /// <summary>Stops the clock, keeping the position (scrubbing stays live).</summary>
    public void Pause() => Playing = false;

    /// <summary>Jumps to timeline <paramref name="t"/> ∈ [0, 1] (clamped). Legal while
    /// playing or paused — a scrub during playback just moves the clock.</summary>
    public void Seek(double t) => Time = Math.Clamp(t, 0, 1) * Animation.Duration;

    /// <summary>
    /// Advances the clock by <paramref name="deltaSeconds"/> of real time. Returns true
    /// when the position changed (the caller should re-render); false when paused. At
    /// the end: loops (wrapping the overshoot, so playback speed is independent of the
    /// timer's tick quantum) or clamps to the end and pauses.
    /// </summary>
    public bool Advance(double deltaSeconds)
    {
        if (!Playing || !(deltaSeconds > 0))
            return false;
        double next = Time + deltaSeconds;
        if (next >= Animation.Duration)
        {
            if (Loop)
            {
                next %= Animation.Duration;
            }
            else
            {
                next = Animation.Duration;
                Playing = false;
            }
        }
        Time = next;
        return true;
    }
}
