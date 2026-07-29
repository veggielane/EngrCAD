using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>Knobs for <see cref="MotionStudy.CheckInterference"/>.</summary>
public sealed class InterferenceOptions
{
    /// <summary>Include pairs directly connected by a joint (default false). A pin
    /// modeled at its bore's diameter INTERPENETRATES once both are tessellated —
    /// polygon chords cross even when the exact surfaces only touch — so jointed
    /// pairs report constant false clashes unless the models carry real clearance.</summary>
    public bool IncludeJointedPairs { get; init; }

    /// <summary>Compute the exact mesh-boolean intersection volume at the middle of
    /// each clash range (default false — it is orders of magnitude dearer than the
    /// crossing test, so it runs only for pairs the sweep already confirmed).</summary>
    public bool ExactVolumes { get; init; }

    /// <summary>Mesh quality for the crossing tests (defaults to the parts' cached
    /// display meshes).</summary>
    public MeshQuality? Quality { get; init; }
}

/// <summary>One contiguous run of sampled frames over which a pair interpenetrates.</summary>
/// <param name="Start">Driver value of the first clashing frame.</param>
/// <param name="End">Driver value of the last clashing frame.</param>
/// <param name="Volume">The mesh-boolean intersection volume at the middle of the
/// range, when <see cref="InterferenceOptions.ExactVolumes"/> asked for it.</param>
public sealed record InterferenceRange(double Start, double End, double? Volume);

/// <summary>Every clash range of one instance pair over the sweep.</summary>
public sealed record InterferencePair(string PathA, string PathB, IReadOnlyList<InterferenceRange> Ranges);

/// <summary>What a swept interference check found. Contact without interpenetration
/// (parts resting on each other, a seated pin's rim) is deliberately NOT a clash —
/// the <see cref="MeshIntersection"/> transversality rule.</summary>
public sealed class InterferenceReport
{
    internal InterferenceReport(IReadOnlyList<InterferencePair> pairs) => Pairs = pairs;

    /// <summary>The clashing pairs, each with its parameter ranges.</summary>
    public IReadOnlyList<InterferencePair> Pairs { get; }

    /// <summary>True when nothing interpenetrates anywhere in the sweep.</summary>
    public bool Clear => Pairs.Count == 0;

    public override string ToString() => Clear
        ? "no interference"
        : string.Join("\n", Pairs.Select(p =>
            $"{p.PathA} × {p.PathB}: " + string.Join(", ", p.Ranges.Select(r =>
                r.Start == r.End
                    ? $"at {r.Start:g6}" + (r.Volume is { } v0 ? $" (volume {v0:g4})" : "")
                    : $"[{r.Start:g6}, {r.End:g6}]" + (r.Volume is { } v ? $" (volume {v:g4})" : "")))));
}

public sealed partial class MotionStudy
{
    /// <summary>
    /// Per-frame clash detection over the sweep's sampled poses: instance-bounds
    /// overlap is the broad phase, <see cref="MeshIntersection.Crosses"/> the narrow
    /// phase (interpenetration only — a contact rim or flush seat is not a clash),
    /// reported as parameter ranges per offending pair. Pairs joined by a joint are
    /// skipped by default (see <see cref="InterferenceOptions.IncludeJointedPairs"/>);
    /// exact intersection volumes are opt-in per range.
    /// </summary>
    public InterferenceReport CheckInterference(InterferenceOptions? options = null)
    {
        options ??= new InterferenceOptions();
        var jointed = options.IncludeJointedPairs ? null : JointedPairs();
        var crossings = new Dictionary<(string A, string B), List<int>>();

        for (int f = 0; f < Frames.Count; f++)
        {
            var instances = Frames[f].Instances;
            var bounds = new Aabb[instances.Count];
            for (int i = 0; i < instances.Count; i++)
                bounds[i] = instances[i].Bounds(options.Quality);

            for (int i = 0; i < instances.Count; i++)
            {
                for (int j = i + 1; j < instances.Count; j++)
                {
                    if (!bounds[i].Intersects(bounds[j]))
                        continue;
                    var key = PairKey(instances[i].Path, instances[j].Path);
                    if (jointed is not null && jointed.Contains(key))
                        continue;
                    if (!MeshIntersection.Crosses(WorldMesh(instances[i], options), WorldMesh(instances[j], options)))
                        continue;
                    if (!crossings.TryGetValue(key, out var frames))
                        crossings[key] = frames = [];
                    frames.Add(f);
                }
            }
        }

        var pairs = new List<InterferencePair>();
        foreach (var (key, frameIndices) in crossings.OrderBy(c => c.Key.A, StringComparer.Ordinal)
                     .ThenBy(c => c.Key.B, StringComparer.Ordinal))
        {
            var ranges = new List<InterferenceRange>();
            int start = 0;
            for (int i = 1; i <= frameIndices.Count; i++)
            {
                if (i < frameIndices.Count && frameIndices[i] == frameIndices[i - 1] + 1)
                    continue;
                ranges.Add(Range(key, frameIndices[start], frameIndices[i - 1], options));
                start = i;
            }
            pairs.Add(new InterferencePair(key.A, key.B, ranges));
        }
        return new InterferenceReport(pairs);
    }

    /// <summary>
    /// The volume the named occurrence sweeps through the study's frames, as a
    /// <see cref="Shape"/>: implicit-native (the part's field lowered once, placed at
    /// every sampled pose, unioned — fidelity is the sweep's own sampling density),
    /// mesh via Surface Nets, B-Rep honestly impossible.
    /// <para><paramref name="maxTravel"/> makes the sampling <b>adaptive</b>: extra
    /// poses are rigidly interpolated between recorded frames until no point of the part
    /// moves further than that between consecutive placements, so the scallop between
    /// placements is bounded by a number in MODEL UNITS rather than inherited from
    /// whatever frame count the sweep happened to use. Travel is measured exactly, as
    /// the largest displacement of the part's own bounding-box corners — no rotation
    /// angle times an assumed radius — so a body that mostly spins about its centre
    /// costs few extra poses and one on the end of a long arm costs many, which is the
    /// right way round. Null keeps the recorded frames verbatim (bit-identical to
    /// before).</para>
    /// </summary>
    public Shape SweptVolume(string occurrencePath, double? maxTravel = null)
    {
        if (maxTravel is { } bound && (!(bound > 0) || !double.IsFinite(bound)))
            throw new ArgumentOutOfRangeException(nameof(maxTravel),
                "The travel bound between swept placements must be a positive length.");

        var poses = new List<Matrix4d>(Frames.Count);
        Part? part = null;
        foreach (var frame in Frames)
        {
            foreach (var instance in frame.Instances)
            {
                if (instance.Path != occurrencePath && RelativePath(instance.Path) != occurrencePath)
                    continue;
                part ??= instance.Part;
                poses.Add(instance.World);
                break;
            }
        }
        if (part is null)
            throw new ArgumentException(
                $"No instance '{occurrencePath}' in the study. It contains: " +
                $"{string.Join(", ", Frames[0].Instances.Select(i => i.Path))}.", nameof(occurrencePath));

        var source = part.Geometry switch
        {
            Shape shape => shape,
            BRep.BrepSolid solid => Shape.From(solid),
            HalfEdgeMesh mesh => Shape.From(mesh),
            Implicit.Sdf sdf => Shape.From(sdf),
            _ => throw new NotSupportedException(
                $"Part '{part.Name}' carries geometry of type {part.Geometry.GetType().Name}, which " +
                "cannot re-enter the Shape graph."),
        };
        if (maxTravel is { } limit)
            poses = Refined(poses, part, limit);
        return source.SweptOver(poses);
    }

    /// <summary>
    /// Inserts rigidly interpolated poses between recorded ones until no corner of the
    /// part's local bounding box travels further than <paramref name="maxTravel"/>
    /// between consecutive placements. The recorded poses are all KEPT and are hit
    /// exactly — the study's frames stay the truth, as they are for
    /// <c>MechanismTrack</c>; what is added is the intermediate coverage a union of
    /// discrete placements otherwise leaves to luck.
    /// </summary>
    private static List<Matrix4d> Refined(List<Matrix4d> poses, Part part, double maxTravel)
    {
        var box = part.GetMesh().ComputeBounds();
        if (box.IsEmpty || poses.Count < 2)
            return poses;
        var corners = new Vector3d[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vector3d(
                (i & 1) == 0 ? box.Min.X : box.Max.X,
                (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                (i & 4) == 0 ? box.Min.Z : box.Max.Z);
        }

        var refined = new List<Matrix4d> { poses[0] };
        for (int i = 1; i < poses.Count; i++)
        {
            var a = poses[i - 1];
            var b = poses[i];
            double travel = 0;
            foreach (var corner in corners)
                travel = Math.Max(travel, (b.TransformPoint(corner) - a.TransformPoint(corner)).Length);
            int steps = (int)Math.Ceiling(travel / maxTravel);
            for (int k = 1; k < steps; k++)
                refined.Add(InterpolatePose(a, b, k / (double)steps));
            refined.Add(b);
        }
        return refined;
    }

    /// <summary>
    /// Chordal rigid interpolation between two poses of the SAME body. Both matrices
    /// are frame-chain (rigid) × part transform (arbitrary but constant), so the delta
    /// b·a⁻¹ is rigid whatever the part transform carries: its rotation slerps from
    /// identity and the body origin travels the straight chord between the two
    /// translation columns — exactly <paramref name="a"/> at s = 0 and exactly
    /// <paramref name="b"/> at s = 1 (up to the quaternion round trip).
    /// <para>Public and here, in the document model, because two consumers need it and
    /// they sit on opposite sides of an assembly boundary: the animation layer's
    /// <c>MechanismTrack</c> (in EngrCAD.Viewer.Core) plays a study back, and
    /// <see cref="SweptVolume"/> subdivides one. Two copies would be two answers to
    /// "where was the body halfway between these frames".</para>
    /// </summary>
    public static Matrix4d InterpolatePose(in Matrix4d a, in Matrix4d b, double s)
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

    private InterferenceRange Range((string A, string B) key, int firstFrame, int lastFrame, InterferenceOptions options)
    {
        double? volume = null;
        if (options.ExactVolumes)
        {
            var middle = Frames[(firstFrame + lastFrame) / 2].Instances;
            var a = middle.First(i => i.Path == key.A);
            var b = middle.First(i => i.Path == key.B);
            volume = MeshBoolean.Intersection(WorldMesh(a, options), WorldMesh(b, options)).Volume();
        }
        return new InterferenceRange(Frames[firstFrame].Value, Frames[lastFrame].Value, volume);
    }

    private static HalfEdgeMesh WorldMesh(in PartInstance instance, InterferenceOptions options) =>
        instance.Part.GetMesh(options.Quality).Transformed(instance.World);

    private static (string A, string B) PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>Instance-path pairs directly connected by a joint (both orders folded
    /// into the canonical key).</summary>
    private HashSet<(string A, string B)> JointedPairs()
    {
        var pairs = new HashSet<(string, string)>();
        string prefix = Mechanism.Assembly.Name + "/";
        foreach (var joint in Mechanism.Joints)
        {
            if (joint.A.Path is not { } a || joint.B.Path is not { } b)
                continue;
            pairs.Add(PairKey(prefix + a, prefix + b));
        }
        return pairs;
    }

    private string RelativePath(string instancePath)
    {
        string prefix = Mechanism.Assembly.Name + "/";
        return instancePath.StartsWith(prefix, StringComparison.Ordinal)
            ? instancePath[prefix.Length..]
            : instancePath;
    }
}
