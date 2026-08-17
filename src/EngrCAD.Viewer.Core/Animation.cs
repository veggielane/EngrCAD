using EngrCAD.Core;
using EngrCAD.Mesh;
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
// order independent of t), a camera, or a scalar — never a re-meshed part — which is
// what lets SetInstancePoses animate with matrices alone, keeps picking working
// (HitTest reads the per-instance model matrix), and makes
// scrubbing/reversing/export/headless rendering the same code path: Animation.At(t) is
// a pure function.
//
// A DEFORMED RESULT looks like the exception and is not. New vertex positions per frame
// would break the rule outright, which is why the deformed-shape overlay was originally
// documented as off this path — but the displacement travels ONCE as a vertex attribute
// and the vertex shader applies `position + uDeformScale * displacement`, so animating
// it is one float uniform per frame. A DeformationTrack therefore carries a SCALAR, and
// the rule stands with nothing weakened.

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
/// (null when the animation has no pose track — the scene's own instances stand), the
/// camera (null when it has no camera track — the viewer's current camera stands), the
/// deformation factor (null when it has no deformation track — every part draws at
/// its own <c>FieldDisplay.DeformScale</c>, i.e. factor 1) and the section planes (null
/// when it has no section track — whatever sections the render call or the window
/// carries stand).</summary>
public sealed record AnimationSample(
    IReadOnlyList<PartInstance>? Instances, CameraState? Camera, double? DeformScale = null,
    IReadOnlyList<SectionPlane>? Sections = null, string? FieldName = null)
{
    /// <summary>The deformation factor a renderer should apply — the track's value, or 1
    /// when nothing is animating it. One property so no front end has to spell the
    /// "null means unchanged" convention itself.</summary>
    public double DeformFactor => DeformScale ?? 1;
}

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
/// A track that scales a simulation result's displayed deformation: it returns a factor
/// multiplying every part's own <c>FieldDisplay.DeformScale</c>, so 1 draws each part at
/// the exaggeration it states and 0 draws them all undeformed.
/// <para><b>It is a scalar, and that is the whole point.</b> The displacement field
/// travels once as a vertex attribute and the vertex shader applies it, so a whole
/// result animation changes ONE uniform per frame — no buffer touched, no re-upload, the
/// instance list untouched. A track producing displaced meshes would have broken the
/// rule this file is built on; a track producing a number does not.</para>
/// <para>For a LINEAR solve the intermediate frames are not a tween: a linear result
/// scales exactly, so the shape at factor 0.5 is the actual answer for half the load.
/// See <see cref="DeformationTracks"/> for the ramp and oscillation laws and for what
/// that honesty does and does not extend to.</para>
/// </summary>
public abstract class DeformationTrack : AnimationTrack
{
    /// <summary>The deformation factor at track-local <paramref name="t"/> ∈ [0,1].</summary>
    public abstract double ScaleAt(double t);
}

/// <summary>
/// A track that drives the SECTION PLANES over the timeline — the material-addition /
/// material-removal player. It obeys the file's load-bearing rule by construction: a
/// clip plane is shader state (the same per-fragment discard the section toolbar
/// drives), so a print-progress reveal or a machining cutaway animates with NO
/// re-meshing at all — the geometry is uploaded once and the plane sweeps through it.
/// </summary>
public abstract class SectionTrack : AnimationTrack
{
    /// <summary>The section planes at track-local <paramref name="t"/> ∈ [0,1].</summary>
    public abstract IReadOnlyList<SectionPlane> SectionsAt(double t);
}

/// <summary>
/// A track that drives WHICH RESULT a part displays over the timeline — the transient
/// playback player (a thermal run's stored temperature steps, a load sweep). It is the
/// one track whose sample is a result SELECTION rather than matrices, a camera or a
/// scalar, and the contract extension is stated with its cost model: applying a
/// selection re-uploads ONE colour buffer (measured 0.042 / 0.68 ms per frame at
/// 12k / 195k render vertices — <c>FieldPlaybackBenchmark</c>); nothing else the
/// animation contract protects moves — instance count and order, the meshes, the pick
/// BVH are all untouched.
/// <para><b>Hold-last-step semantics, deliberately</b>: the stored states ARE the
/// answers at their own instants, so a t between steps shows the latest step at or
/// before it — holding is honest where tweening colours between two solutions would
/// invent a state the solver never produced.</para>
/// <para><b>Participation</b> is the consumer's rule (the <c>PoseByPath</c> lesson): a
/// part shows a track's steps only when it carries ALL of them as results; a track
/// saying nothing about a part leaves it alone.</para>
/// </summary>
public sealed class FieldSequenceTrack : AnimationTrack
{
    private readonly string[] _names;
    private readonly double[] _seconds;

    /// <summary>The run's steps: each result NAME with its own REAL time (seconds,
    /// strictly increasing — the solver's own instants, which is what makes the
    /// playback honest about time rather than about step index).</summary>
    public FieldSequenceTrack(IReadOnlyList<(string FieldName, double Seconds)> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
            throw new ArgumentException("A field sequence needs at least one step.", nameof(steps));
        _names = new string[steps.Count];
        _seconds = new double[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            var (name, seconds) = steps[i];
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"Step {i} names no result.", nameof(steps));
            if (!double.IsFinite(seconds))
                throw new ArgumentOutOfRangeException(nameof(steps), $"Step {i}'s time is not finite.");
            if (i > 0 && !(seconds > _seconds[i - 1]))
                throw new ArgumentException(
                    $"Step times must strictly increase ({seconds:G6} after {_seconds[i - 1]:G6}).",
                    nameof(steps));
            _names[i] = name;
            _seconds[i] = seconds;
        }
    }

    /// <summary>
    /// The track for a part's saved time axis — the ONE rule connecting a
    /// <see cref="ResultSequence"/> (document data: the ordered result names and
    /// instants a transient run published through
    /// <see cref="Part.AddResultSequence"/>) to its playback, so the saved axis and
    /// the clip cannot disagree. Refuses by name when the part carries no sequence of
    /// that name, listing what it does carry.
    /// </summary>
    public static FieldSequenceTrack For(Part part, string sequenceName)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Sequence(sequenceName) is not { } sequence)
        {
            var carried = part.ResultSequences;
            throw new ArgumentException(
                $"Part '{part.Name}' has no result sequence '{sequenceName}'. " +
                (carried.Count == 0
                    ? "It carries none."
                    : $"It carries: {string.Join(", ", carried.Select(s => s.Name))}."),
                nameof(sequenceName));
        }
        return new FieldSequenceTrack(sequence.Steps);
    }

    /// <summary>Every step's result name, in run order — what a consumer checks a
    /// part's results against to decide participation.</summary>
    public IReadOnlyList<string> FieldNames => _names;

    /// <summary>The result selected at track-local <paramref name="t"/> ∈ [0,1]: t maps
    /// linearly over the run's REAL span, and the answer is the latest step at or
    /// before that instant (hold-last; before the first step, the first).</summary>
    public string FieldAt(double t)
    {
        double clamped = Math.Clamp(t, 0, 1);
        double time = _seconds[0] + clamped * (_seconds[^1] - _seconds[0]);
        int at = 0;
        for (int i = 1; i < _seconds.Length; i++)
        {
            if (_seconds[i] <= time)
                at = i;
            else
                break;
        }
        return _names[at];
    }

    /// <summary>The run's real time at track-local <paramref name="t"/> — what a legend
    /// or a caption states as the displayed instant.</summary>
    public double SecondsAt(double t) =>
        _seconds[0] + Math.Clamp(t, 0, 1) * (_seconds[^1] - _seconds[0]);

    /// <summary>
    /// The display a participating part shows for <paramref name="fieldName"/>'s step —
    /// the ONE application rule every consumer asks (window playback, stills, batched
    /// export), so a frame cannot mean two things. Returns false when the part does not
    /// participate: it must carry a display AND every one of the run's steps as results
    /// (a track saying nothing about a part leaves it alone — the PoseByPath lesson).
    /// The clip shows ONE range throughout: the display's own EXPLICIT range, else the
    /// UNION of the step fields' ranges — a legend that rescales per frame lies.
    /// </summary>
    public bool TryDisplayFor(Part part, string fieldName, out ResolvedFieldDisplay display)
    {
        ArgumentNullException.ThrowIfNull(part);
        display = default;
        if (!part.TryResolveFieldDisplay(out var resolved, out _))
            return false;
        foreach (var name in _names)
        {
            if (part.Result(name) is null)
                return false;
        }
        var step = part.Result(fieldName);
        if (step is null)
            return false;
        var range = part.FieldDisplay?.Range ?? RunRange(part);
        if (range.IsEmpty)
            return false;
        display = resolved with { Field = step, Range = range };
        return true;
    }

    /// <summary>The union of every step field's own range on <paramref name="part"/> —
    /// the clip's shared scale when the display states none explicitly.</summary>
    public FieldRange RunRange(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var name in _names)
        {
            if (part.Result(name) is not { } field || field.Range.IsEmpty)
                continue;
            min = Math.Min(min, field.Range.Min);
            max = Math.Max(max, field.Range.Max);
        }
        return min <= max ? new FieldRange(min, max) : new FieldRange(1, 0);
    }
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

    /// <summary>The deformation track, when the animation scales a displayed result's
    /// deformation.</summary>
    public DeformationTrack? DeformationTrack { get; private set; }

    /// <summary>The section track, when the animation drives the clip planes.</summary>
    public SectionTrack? SectionTrack { get; private set; }

    /// <summary>The field-sequence track, when the animation steps through stored
    /// results (a transient run's playback).</summary>
    public FieldSequenceTrack? FieldTrack { get; private set; }

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

    /// <summary>Adds the deformation track (at most one — several factors on one
    /// uniform have no defined composition, the pose track's argument in miniature).
    /// Chainable.</summary>
    public Animation With(DeformationTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (DeformationTrack is not null)
            throw new InvalidOperationException(
                "This animation already has a deformation track. Two factors on one scale have " +
                "no defined composition; sequence within one track instead (DeformationTracks.From " +
                "takes any law of t).");
        DeformationTrack = track;
        return this;
    }

    /// <summary>Adds the section track (at most one — several plane sets on one clip
    /// state have no defined composition; a multi-plane cut is ONE track returning the
    /// whole set). Chainable.</summary>
    public Animation With(SectionTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (SectionTrack is not null)
            throw new InvalidOperationException(
                "This animation already has a section track. Two plane sets on one clip state " +
                "have no defined composition; return the whole set from one track instead.");
        SectionTrack = track;
        return this;
    }

    /// <summary>Adds the field-sequence track (at most one — two selections of one
    /// part's displayed result have no defined composition; sequence within one track,
    /// whose steps are already an ordered run). Chainable.</summary>
    public Animation With(FieldSequenceTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (FieldTrack is not null)
            throw new InvalidOperationException(
                "This animation already has a field-sequence track. Two selections of one " +
                "displayed result have no defined composition; put the whole run in one track.");
        FieldTrack = track;
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
        double? deform = DeformationTrack is { } flex ? flex.ScaleAt(flex.LocalT(eased)) : null;
        var sections = SectionTrack is { } clip ? clip.SectionsAt(clip.LocalT(eased)) : null;
        string? fieldName = FieldTrack is { } run ? run.FieldAt(run.LocalT(eased)) : null;
        return new AnimationSample(poses, camera, deform, sections, fieldName);
    }

    /// <summary>The animation evaluated at <paramref name="seconds"/> into playback.</summary>
    public AnimationSample AtTime(double seconds) => At(seconds / Duration);
}
