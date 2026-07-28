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
    /// </summary>
    public Shape SweptVolume(string occurrencePath)
    {
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
        return source.SweptOver(poses);
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
