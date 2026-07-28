using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// The animation timeline: a duration, an easing, and tracks mapping t in [0,1] to
// instance poses or a camera. It lives in EngrCAD.Viewer.Core by dependency direction:
// pose tracks speak Scene/Mechanism (EngrCAD.Modeling) and camera tracks speak
// CameraState/ViewCubeMath, and this assembly already sits above Modeling while
// Modeling cannot reference the camera types without a cycle. The cost of that
// placement is that a Scene cannot CARRY its animation as a typed property — hosts
// take it beside the scene (EngrCadOptions.WithAnimation, the export entry points).
//
// THE load-bearing rule, inherited from the exploded view: an animation must not touch
// geometry. Every track returns poses (matrices over the same instance list, count and
// order independent of t) or a camera — never a re-meshed part — which is what lets
// SetInstancePoses animate with matrices alone, keeps picking working (HitTest reads
// the per-instance model matrix), and makes scrubbing/reversing/export/headless
// rendering the same code path: Animation.At(t) is a pure function.

/// <summary>Easing applied to the whole timeline before tracks see t. Tracks may add
/// their own shaping (a keyframed camera eases per segment); a looping turntable wants
/// <see cref="Linear"/>, an explode presentation reads better with
/// <see cref="Smoothstep"/>.</summary>
public enum AnimationEasing
{
    /// <summary>t passes through unchanged — the right choice for seamless loops.</summary>
    Linear,

    /// <summary>The view cube's smoothstep (<see cref="ViewCubeMath.Ease"/>): C1 at both
    /// ends, so motion starts and stops without a velocity jump.</summary>
    Smoothstep,
}

/// <summary>One evaluated instant of an <see cref="Animation"/>: the posed instances
/// (null when the animation has no pose track — the scene's own instances stand) and
/// the camera (null when it has no camera track — the viewer's current camera stands).</summary>
public sealed record AnimationSample(IReadOnlyList<PartInstance>? Instances, CameraState? Camera);

/// <summary>
/// A track maps track-local t ∈ [0,1] to instance poses (<see cref="PoseTrack"/>) or a
/// camera (<see cref="CameraTrack"/>). Every track has a <b>window</b> on the shared
/// timeline (default the whole of it): before the window it holds its start value,
/// after it its end value (clamp semantics), and inside it runs its own 0→1. That is
/// what sequences a camera move after an explode without a second timeline.
/// </summary>
public abstract class AnimationTrack
{
    /// <summary>Window start on the shared timeline (fraction, 0 ≤ start &lt; end ≤ 1).</summary>
    public double WindowStart { get; private set; }

    /// <summary>Window end on the shared timeline.</summary>
    public double WindowEnd { get; private set; } = 1;

    /// <summary>Confines this track to a window of the shared timeline; outside it the
    /// track holds its boundary value (clamp, not deactivate — a finished explode stays
    /// exploded). Returns this track for chaining.</summary>
    public AnimationTrack Window(double start, double end)
    {
        if (!(start >= 0 && start < end && end <= 1))
            throw new ArgumentOutOfRangeException(nameof(start),
                $"A track window needs 0 <= start < end <= 1 (got [{start:g6}, {end:g6}]).");
        WindowStart = start;
        WindowEnd = end;
        return this;
    }

    /// <summary>Timeline t mapped into this track's window: 0 before it, 1 after it,
    /// linear inside.</summary>
    public double LocalT(double t) =>
        Math.Clamp((t - WindowStart) / (WindowEnd - WindowStart), 0, 1);
}

/// <summary>A track that poses the scene's instances. The contract every
/// implementation must keep: the instance COUNT and ORDER are the same for every t
/// (only the matrices move), and evaluation never lowers or meshes geometry.</summary>
public abstract class PoseTrack : AnimationTrack
{
    /// <summary>The posed instances at track-local <paramref name="t"/> ∈ [0,1].</summary>
    public abstract IReadOnlyList<PartInstance> PosesAt(double t);
}

/// <summary>A track that moves the camera.</summary>
public abstract class CameraTrack : AnimationTrack
{
    /// <summary>The camera pose at track-local <paramref name="t"/> ∈ [0,1].</summary>
    public abstract CameraState CameraAt(double t);
}

/// <summary>
/// A timeline over poses and the camera: a duration (seconds — playback and export
/// timing), an easing, at most one <see cref="PoseTrack"/> and at most one
/// <see cref="CameraTrack"/>. <see cref="At"/> is a PURE function of t, which is the
/// design's one commitment: scrubbing, reversing, looping, the window's playback and
/// every export format evaluate the same function rather than four re-implementations.
/// <para><b>Why one pose track</b>: two tracks that each produce the full instance
/// list cannot compose (whose matrices win?), so sequencing lives INSIDE the pose
/// track where it is well defined — <see cref="ExplodeTrack.Stagger"/> gives each
/// occurrence its own timing window, and a <see cref="MechanismTrack"/> carries a
/// whole swept study. Composing relative displacement tracks is filed as future
/// work, not half-supported here.</para>
/// </summary>
public sealed class Animation
{
    /// <summary>Playback length in seconds (also the export default: frames spread
    /// across this duration).</summary>
    public double Duration { get; }

    /// <summary>Easing applied to timeline t before the tracks see it.</summary>
    public AnimationEasing Easing { get; }

    /// <summary>The pose track, when the animation moves parts.</summary>
    public PoseTrack? PoseTrack { get; private set; }

    /// <summary>The camera track, when the animation moves the camera.</summary>
    public CameraTrack? CameraTrack { get; private set; }

    public Animation(double durationSeconds = 5, AnimationEasing easing = AnimationEasing.Linear)
    {
        if (!(durationSeconds > 0) || !double.IsFinite(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds),
                "An animation needs a positive finite duration.");
        Duration = durationSeconds;
        Easing = easing;
    }

    /// <summary>Adds the pose track (at most one — see the class remarks for why
    /// sequencing lives inside the track). Chainable.</summary>
    public Animation With(PoseTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (PoseTrack is not null)
            throw new InvalidOperationException(
                "This animation already has a pose track. Two full-instance-list tracks cannot " +
                "compose; sequence within one track instead (ExplodeTrack.Stagger gives " +
                "per-occurrence timing windows).");
        PoseTrack = track;
        return this;
    }

    /// <summary>Adds the camera track (at most one). Chainable.</summary>
    public Animation With(CameraTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (CameraTrack is not null)
            throw new InvalidOperationException(
                "This animation already has a camera track. Sequence camera moves inside one " +
                "KeyframedCameraTrack instead.");
        CameraTrack = track;
        return this;
    }

    /// <summary>
    /// The animation evaluated at timeline <paramref name="t"/> (clamped to [0,1]):
    /// eased, windowed, handed to the tracks. Pure — the same t always returns the same
    /// sample, and nothing in the document is touched.
    /// </summary>
    public AnimationSample At(double t)
    {
        double clamped = Math.Clamp(t, 0, 1);
        double eased = Easing == AnimationEasing.Smoothstep ? ViewCubeMath.Ease(clamped) : clamped;
        var poses = PoseTrack is { } pose ? pose.PosesAt(pose.LocalT(eased)) : null;
        var camera = CameraTrack is { } view ? view.CameraAt(view.LocalT(eased)) : null;
        return new AnimationSample(poses, camera);
    }

    /// <summary>The animation evaluated at <paramref name="seconds"/> into playback.</summary>
    public AnimationSample AtTime(double seconds) => At(seconds / Duration);
}
