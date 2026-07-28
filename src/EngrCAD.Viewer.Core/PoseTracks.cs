using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// Plays a swept <see cref="MotionStudy"/> back as an animation track: t sweeps the
/// study's recorded frames in order, with rigid interpolation between adjacent frames.
/// <para><b>The frames are the truth; interpolation is screen smoothness.</b> At every
/// recorded sample the track returns the study's own matrices VERBATIM (bit-exact —
/// locked by test); between samples each instance takes the chordal rigid motion
/// between its two recorded poses (rotation via quaternion slerp of the rigid delta,
/// origin along the straight chord). That is a chord of the true mechanism path, the
/// same approximation a denser sweep shrinks — never a re-solve, because solving at an
/// arbitrary t seeded from an arbitrary pose is exactly the branch-flipping trap the
/// sweep's continuation exists to avoid.</para>
/// <para>A partial study (one that stalled at a dead centre or a joint stop) animates
/// what it recorded: t spans the recorded frames, honestly ending where the sweep did.</para>
/// </summary>
public sealed class MechanismTrack : PoseTrack
{
    private readonly IReadOnlyList<MotionFrame> _frames;

    // Scene grafting: the template is the scene's full instance list (captured once,
    // fixed order), and _frameIndexOf[i] is the study-frame instance whose pose drives
    // template instance i (-1 = not part of the mechanism, keeps its template matrix).
    private readonly IReadOnlyList<PartInstance>? _template;
    private readonly int[]? _frameIndexOf;

    /// <summary>A track over the study's own instances (the mechanism's assembly alone) —
    /// the headless/test form. Use the <see cref="MechanismTrack(MotionStudy, Scene)"/>
    /// overload to animate a whole scene.</summary>
    public MechanismTrack(MotionStudy study)
    {
        _frames = Validated(study);
    }

    /// <summary>
    /// A track over <paramref name="scene"/>'s instances, with the study's poses grafted
    /// on by occurrence path: instances outside the mechanism keep their scene poses, so
    /// the track's output matches the viewport's instance list index-for-index (fixtures
    /// and reference parts stay put while the linkage runs). Throws when no study path
    /// matches the scene — a study grafted onto the wrong scene should fail here, not
    /// render a motionless frame.
    /// </summary>
    public MechanismTrack(MotionStudy study, Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _frames = Validated(study);
        // The scene flattens the mechanism's assembly with the CURRENT occurrence
        // frames (wherever the sweep left them) — only the paths matter here.
        _template = scene.Instances().ToList();
        var indexByPath = new Dictionary<string, int>();
        var frame0 = _frames[0].Instances;
        for (int i = 0; i < frame0.Count; i++)
            indexByPath[frame0[i].Path] = i;

        _frameIndexOf = new int[_template.Count];
        int matched = 0;
        for (int i = 0; i < _template.Count; i++)
        {
            if (indexByPath.TryGetValue(_template[i].Path, out int frameIndex))
            {
                _frameIndexOf[i] = frameIndex;
                matched++;
            }
            else
            {
                _frameIndexOf[i] = -1;
            }
        }
        if (matched == 0)
            throw new ArgumentException(
                $"None of the study's {frame0.Count} instance paths (e.g. '{frame0[0].Path}') appear in " +
                "the scene — is the mechanism's assembly added to a tab of this scene?", nameof(scene));
    }

    /// <summary>The study this track plays.</summary>
    public IReadOnlyList<MotionFrame> Frames => _frames;

    private static IReadOnlyList<MotionFrame> Validated(MotionStudy study)
    {
        ArgumentNullException.ThrowIfNull(study);
        if (study.Frames.Count < 2)
            throw new ArgumentException(
                $"A mechanism track needs a study with at least two recorded frames; this one has " +
                $"{study.Frames.Count}." +
                (study.Completed ? "" : $" The sweep did not complete: {string.Join("; ", study.Diagnostics)}"),
                nameof(study));
        int count = study.Frames[0].Instances.Count;
        foreach (var frame in study.Frames)
        {
            if (frame.Instances.Count != count)
                throw new ArgumentException(
                    "The study's frames disagree about the instance count — the assembly changed " +
                    "shape mid-sweep, which no animation can pose.", nameof(study));
        }
        return study.Frames;
    }

    public override IReadOnlyList<PartInstance> PosesAt(double t)
    {
        // Continuous frame coordinate over the RECORDED samples (uniform in sweep order).
        double f = Math.Clamp(t, 0, 1) * (_frames.Count - 1);
        int i = Math.Min((int)f, _frames.Count - 2);
        double s = f - i;

        // Exact-zero / exact-one semantic tests: ON a recorded sample the study's own
        // matrices are returned verbatim — the frames are the truth, and a t that lands
        // on one must not pay (or smear) the interpolation.
        if (s == 0)
            return Grafted(_frames[i].Instances, null, 0);
        if (s == 1)
            return Grafted(_frames[i + 1].Instances, null, 0);
        return Grafted(_frames[i].Instances, _frames[i + 1].Instances, s);
    }

    private IReadOnlyList<PartInstance> Grafted(
        IReadOnlyList<PartInstance> a, IReadOnlyList<PartInstance>? b, double s)
    {
        if (_template is null)
        {
            if (b is null)
                return a;
            var blended = new PartInstance[a.Count];
            for (int k = 0; k < a.Count; k++)
                blended[k] = a[k] with { World = InterpolateRigid(a[k].World, b[k].World, s) };
            return blended;
        }

        var result = new PartInstance[_template.Count];
        for (int i = 0; i < _template.Count; i++)
        {
            int k = _frameIndexOf![i];
            result[i] = k < 0
                ? _template[i]
                : _template[i] with
                {
                    World = b is null ? a[k].World : InterpolateRigid(a[k].World, b[k].World, s),
                };
        }
        return result;
    }

    /// <summary>
    /// Chordal rigid interpolation between two poses of the SAME body. Both matrices
    /// are frame-chain (rigid) × part transform (arbitrary but constant), so the delta
    /// b·a⁻¹ is rigid whatever the part transform carries: its rotation R slerps from
    /// identity, and the body origin travels the straight chord between the two
    /// translation columns — M(s) = T(lerp(p_a, p_b, s)) · R_s · T(−p_a) · a, which is
    /// exactly a at s = 0 and exactly Δ·a = b at s = 1 (up to the quaternion round-trip;
    /// the callers return recorded frames verbatim at the endpoints anyway).
    /// </summary>
    internal static Matrix4d InterpolateRigid(in Matrix4d a, in Matrix4d b, double s)
    {
        // A body that did not move between the frames (the ground link, fixtures) has
        // bit-identical matrices — skip the inverse, and the answer is exact.
        if (a.Equals(b))
            return a;

        var delta = b * a.Inverse();
        var rotation = Quaterniond.FromRotationMatrix(delta);
        var eased = Quaterniond.Slerp(Quaterniond.Identity, rotation, s);

        var pa = new Vector3d(a.M14, a.M24, a.M34);
        var pb = new Vector3d(b.M14, b.M24, b.M34);
        var p = pa + (pb - pa) * s;

        return Matrix4d.CreateTranslation(p)
            * eased.ToMatrix()
            * Matrix4d.CreateTranslation(-pa)
            * a;
    }
}

/// <summary>
/// Animates the exploded view: t scales each occurrence's
/// <see cref="Occurrence.ExplodeOffset"/> from 0 (assembled) to 1 (exploded), through
/// the same <see cref="Scene.Instances(Func{Occurrence, double})"/> flattening viewers
/// and exporters already consume. <see cref="Stagger"/> gives an occurrence its own
/// timing window along the track — fasteners back out first, then the cover, then the
/// sub-assembly — which is the sequenced explode assembly instructions want.
/// <para>Construction derives missing offsets once via <see cref="Scene.AutoExplode"/>
/// (hand-set offsets survive, matching the toolbar toggle) — which reads the parts'
/// bounds, so construct off the render thread, like <c>Scene.PreMesh</c>. Evaluation
/// is then pure: matrices only.</para>
/// </summary>
public sealed class ExplodeTrack : PoseTrack
{
    private readonly Scene _scene;
    private readonly Dictionary<Occurrence, (double Start, double End)> _stagger = [];

    /// <param name="scene">The scene whose assemblies explode.</param>
    /// <param name="deriveOffsets">Derive missing <see cref="Occurrence.ExplodeOffset"/>s
    /// now via <see cref="Scene.AutoExplode"/> (explicit offsets are kept). Pass false
    /// when the design sets every offset itself.</param>
    public ExplodeTrack(Scene scene, bool deriveOffsets = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
        if (deriveOffsets)
            scene.AutoExplode();
    }

    /// <summary>
    /// Gives <paramref name="occurrence"/> its own window along the track: its explode
    /// factor is 0 until <paramref name="start"/>, runs 0→1 across the window, and holds
    /// 1 after <paramref name="end"/> (fractions of the track's own 0..1). Chainable.
    /// </summary>
    public ExplodeTrack Stagger(Occurrence occurrence, double start, double end)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!(start >= 0 && start < end && end <= 1))
            throw new ArgumentOutOfRangeException(nameof(start),
                $"A stagger window needs 0 <= start < end <= 1 (got [{start:g6}, {end:g6}]).");
        _stagger[occurrence] = (start, end);
        return this;
    }

    public override IReadOnlyList<PartInstance> PosesAt(double t) =>
        _scene.Instances(occurrence => FactorFor(occurrence, Math.Clamp(t, 0, 1))).ToList();

    private double FactorFor(Occurrence occurrence, double t)
    {
        if (!_stagger.TryGetValue(occurrence, out var window))
            return t;
        // Exact 0 before the window (Posed's exact-zero test then returns the original
        // frame bit-for-bit), exact 1 after it.
        if (t <= window.Start)
            return 0;
        if (t >= window.End)
            return 1;
        return (t - window.Start) / (window.End - window.Start);
    }
}
