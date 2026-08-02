using System.Text.Json;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// Mechanism persistence, following <see cref="MateSet.SaveMates"/>'s conventions
/// (JSON out, warnings — never exceptions — back in for anything the model no longer
/// matches; only a bad envelope or an unknown version throws). One envelope carries
/// the whole layer a <see cref="MateSet"/> file loses: the grounds, the raw mates
/// OUTSIDE the joints, the joints, and the couplings.
///
/// <para><b>Joint mates are derived, not restated</b>: a joint's mates are a
/// deterministic function of its two ends, so the file stores the ends (path + pinned
/// coordinates + query descriptor, exactly as mates do) and loading re-runs the
/// constructor — a second copy of the mates could drift from the joints they spell.
/// What CANNOT be re-derived rounds trip as data: an axis joint's <b>perpendicular
/// reference directions</b> (derived once at construction — re-deriving them at load
/// would move the angle's zero) and its <b>sweep state</b> (the unwrapped accumulated
/// angle is a HISTORY — how many turns the crank has taken — that no pose can
/// recover).</para>
///
/// <para><b>Loading re-ADDS each joint</b>, which re-asserts its nominal DOF against
/// the solver's measured rank (<see cref="Joint.VerifyDegreesOfFreedom"/>) — so a load
/// can legitimately FAIL on a file that was valid when written (the model changed
/// under it), reported as a warning naming the joint, with the joint skipped and any
/// coupling referencing it skipped by name.</para>
///
/// <para><b>Cam laws follow the <c>Feature.SaveInputs</c> precedent</b>: the catalogue
/// laws save their factory kind + arguments, a <c>FromSketch</c> law saves its sampled
/// lifts (the law IS the samples; rebuilding the spline from them is deterministic),
/// and a <see cref="CamLaw.FromFunction"/> lambda saves an <c>opaque</c> marker that
/// loads as a warning unless the caller's <c>resolveOpaqueLaw</c> hook supplies the
/// instance. Coupling ZEROS are deliberately not saved: each coupling constrains the
/// CHANGE since its construction, and for any pose that satisfies it — which a saved
/// converged pose does — re-zeroing at load is exactly the same constraint.</para>
/// </summary>
public sealed partial class Mechanism
{
    /// <summary>This mechanism as JSON: grounds, raw mates (the ones not belonging to
    /// a joint), joints and couplings. Save→load→save is a byte-identical fixed point
    /// for everything short of opaque lambda laws, which are written as markers and
    /// reported (not silently dropped) on load.</summary>
    public string SaveMechanism()
    {
        var jointMates = new HashSet<Mate>(ReferenceEqualityComparer.Instance);
        foreach (var joint in _joints)
        {
            foreach (var mate in joint.Mates)
                jointMates.Add(mate);
        }
        var document = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["grounded"] = Mates.GroundedPaths(),
            ["mates"] = Mates.Mates.Where(m => !jointMates.Contains(m)).Select(Mates.SaveMate).ToArray(),
            ["joints"] = _joints.Select(SaveJoint).ToArray(),
            ["couplings"] = _pairCouplings.Select(SaveCoupling).ToArray(),
        };
        return JsonSerializer.Serialize(document, MateSet.JsonOptions);
    }

    /// <summary>
    /// Adds the grounds, mates, joints and couplings a <see cref="SaveMechanism"/> file
    /// describes to this mechanism (built over the same assembly), returning warnings
    /// for anything that no longer matches — missing occurrences, failed queries,
    /// joints whose DOF re-assertion fails, opaque cam laws — instead of throwing.
    /// <paramref name="resolveOpaqueLaw"/> may supply a <see cref="CamLaw"/> for a cam
    /// coupling whose law saved as an opaque marker, keyed by the coupling's name.
    /// </summary>
    public IReadOnlyList<string> LoadMechanism(string json, Func<string, CamLaw?>? resolveOpaqueLaw = null)
    {
        var warnings = new List<string>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("version", out var versionElement) || versionElement.GetInt32() != 1)
            throw new FormatException(
                "Unknown mechanism file version — this reader understands version 1.");

        if (root.TryGetProperty("grounded", out var grounded))
        {
            foreach (var entry in grounded.EnumerateArray())
                Mates.LoadGround(entry.GetString() ?? "", warnings);
        }

        if (root.TryGetProperty("mates", out var rawMates))
        {
            foreach (var entry in rawMates.EnumerateArray())
                Mates.LoadMateEntry(entry, warnings);
        }

        // Joints keep their SAVED indices in this list (null = skipped), because the
        // couplings reference joints by index and a skipped joint must not shift its
        // neighbours under them.
        var loaded = new List<Joint?>();
        if (root.TryGetProperty("joints", out var joints))
        {
            foreach (var entry in joints.EnumerateArray())
                loaded.Add(LoadJoint(entry, warnings));
        }

        if (root.TryGetProperty("couplings", out var couplings))
        {
            foreach (var entry in couplings.EnumerateArray())
                LoadCoupling(entry, loaded, resolveOpaqueLaw, warnings);
        }

        return warnings;
    }

    // ------------------------------------------------------------------ joints

    private Dictionary<string, object?> SaveJoint(Joint joint)
    {
        var entry = new Dictionary<string, object?>
        {
            ["kind"] = joint switch
            {
                RevoluteJoint => "revolute",
                PrismaticJoint => "prismatic",
                CylindricalJoint => "cylindrical",
                ScrewJoint => "screw",
                FixedJoint => "fixed",
                SphericalJoint => "spherical",
                PlanarJoint => "planar",
                _ => throw new NotSupportedException($"Unknown joint type {joint.GetType().Name}."),
            },
            ["name"] = joint.Name,
        };
        if (joint is ScrewJoint screw)
            entry["pitch"] = screw.Pitch;
        // Exact-zero semantic test: 0 is a planar joint's default (flush) gap.
        if (joint is PlanarJoint { Gap: not 0 } planar)
            entry["gap"] = planar.Gap;
        entry["a"] = MateSet.SaveEnd(joint.A);
        entry["b"] = MateSet.SaveEnd(joint.B);
        if (joint is AxisJoint axis)
        {
            entry["referenceA"] = Components(axis.ReferenceA.Direction);
            entry["referenceB"] = Components(axis.ReferenceB.Direction);
            entry["state"] = new Dictionary<string, object?>
            {
                ["accumulatedAngle"] = axis.State.AccumulatedAngle,
                ["lastMeasuredAngle"] = axis.State.LastMeasuredAngle,
                ["referenceSlide"] = axis.State.ReferenceSlide,
            };
            if (axis.AngleLimits is { } angleLimits)
                entry["angleLimits"] = new[] { angleLimits.Min, angleLimits.Max };
            if (axis.SlideLimits is { } slideLimits)
                entry["slideLimits"] = new[] { slideLimits.Min, slideLimits.Max };
        }
        return entry;
    }

    private Joint? LoadJoint(JsonElement entry, List<string> warnings)
    {
        string name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "joint" : "joint";
        try
        {
            string kind = entry.GetProperty("kind").GetString()
                ?? throw new FormatException("joint kinds are strings");
            var a = Mates.LoadEnd(entry.GetProperty("a"), $"joint '{name}'", "A", warnings);
            var b = Mates.LoadEnd(entry.GetProperty("b"), $"joint '{name}'", "B", warnings);
            if (a is not { } endA || b is not { } endB)
                return null;   // the end's warning already says why

            Joint joint = kind switch
            {
                "spherical" => new SphericalJoint(endA, endB, name),
                "planar" => new PlanarJoint(endA, endB, ReadDouble(entry, "gap"), name),
                _ => LoadAxisJoint(kind, entry, endA, endB, name),
            };
            if (joint is AxisJoint axis)
            {
                if (entry.TryGetProperty("state", out var state))
                {
                    axis.RestoreState(
                        state.GetProperty("accumulatedAngle").GetDouble(),
                        state.GetProperty("lastMeasuredAngle").GetDouble(),
                        state.GetProperty("referenceSlide").GetDouble());
                }
                if (TryReadPair(entry, "angleLimits", out var angleLimits))
                    axis.AngleLimits = angleLimits;
                if (TryReadPair(entry, "slideLimits", out var slideLimits))
                    axis.SlideLimits = slideLimits;
            }

            // Re-assert the joint's nominal DOF against the solver's measured rank —
            // the same gate a hand-built joint passes through Add. A file valid when
            // written can legitimately fail here (the model changed under it), which
            // is a warning, not an exception; the joint is skipped so the loaded
            // mechanism stays sound.
            Add(joint);
            return joint;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or KeyNotFoundException
                      or InvalidOperationException)
        {
            warnings.Add($"joint '{name}': {exception.Message}");
            return null;
        }
    }

    private static Joint LoadAxisJoint(
        string kind, JsonElement entry, in MateRef a, in MateRef b, string name)
    {
        // The SAVED perpendicular references, verbatim — re-deriving them from the
        // loaded pose would move the angle coordinate's zero. The MateRef constructor
        // keeps an already-unit direction untouched, so these round-trip bit-for-bit.
        if (!entry.TryGetProperty("referenceA", out var referenceA) ||
            !entry.TryGetProperty("referenceB", out var referenceB))
            throw new FormatException("an axis joint needs its saved reference directions");
        var references = (
            Joint.Redirect(a, ReadVector(referenceA)),
            Joint.Redirect(b, ReadVector(referenceB)));
        return kind switch
        {
            "revolute" => new RevoluteJoint(a, b, name, references),
            "prismatic" => new PrismaticJoint(a, b, name, references),
            "cylindrical" => new CylindricalJoint(a, b, name, references),
            "screw" => new ScrewJoint(a, b, entry.GetProperty("pitch").GetDouble(), name, references),
            "fixed" => new FixedJoint(a, b, name, references),
            _ => throw new FormatException($"unknown joint kind '{kind}'"),
        };
    }

    // ---------------------------------------------------------------- couplings

    private Dictionary<string, object?> SaveCoupling(Coupling coupling)
    {
        var entry = new Dictionary<string, object?>();
        if (coupling.SaveData is not { } data)
        {
            // A factory this writer does not know is written as a marker (the
            // FeatureRegistry rule: present in the file, reported on load) rather
            // than guessed at.
            entry["kind"] = "opaque";
            entry["name"] = coupling.Name;
            entry["joints"] = coupling.Joints.Select(j => _joints.IndexOf(j)).ToArray();
            return entry;
        }
        entry["kind"] = data.Kind;
        entry["name"] = coupling.Name;
        entry["joints"] = coupling.Joints.Select(j => _joints.IndexOf(j)).ToArray();
        if (data.Args.Length > 0)
            entry["args"] = data.Args;
        if (data.Law is { } law)
            entry["law"] = SaveLaw(law);
        return entry;
    }

    private void LoadCoupling(
        JsonElement entry, List<Joint?> joints, Func<string, CamLaw?>? resolveOpaqueLaw,
        List<string> warnings)
    {
        string name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "coupling" : "coupling";
        try
        {
            string kind = entry.GetProperty("kind").GetString()
                ?? throw new FormatException("coupling kinds are strings");
            var indices = entry.GetProperty("joints").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (indices.Length != 2)
                throw new FormatException("a coupling references exactly two joints");
            var ends = new AxisJoint[2];
            for (int i = 0; i < 2; i++)
            {
                if (indices[i] < 0 || indices[i] >= joints.Count || joints[indices[i]] is not { } joint)
                {
                    warnings.Add($"coupling '{name}': skipped — joint {indices[i]} did not load");
                    return;
                }
                ends[i] = joint as AxisJoint
                    ?? throw new FormatException($"joint '{joint.Name}' carries no axis coordinates");
            }
            double[] args = entry.TryGetProperty("args", out var argsElement)
                ? [.. argsElement.EnumerateArray().Select(e => e.GetDouble())]
                : [];
            Add(kind switch
            {
                "gear" => Coupling.Gear(ends[0], ends[1], args[0], args[1], args[2] != 0, name),
                "belt" => Coupling.Belt(ends[0], ends[1], args[0], args[1], args[2] != 0, name),
                "ratio" => Coupling.Ratio(ends[0], ends[1], args[0], name),
                "rackAndPinion" => Coupling.RackAndPinion(ends[0], ends[1], args[0], name),
                "cam" => Coupling.Cam(ends[0], ends[1],
                    LoadLaw(entry.GetProperty("law")) ?? resolveOpaqueLaw?.Invoke(name)
                        ?? throw new FormatException(
                            "its cam law is code (CamLaw.FromFunction), which only its author can " +
                            "rebuild — supply it via the resolveOpaqueLaw hook"),
                    name),
                "opaque" => throw new FormatException(
                    "the coupling was built by code this file cannot re-run"),
                _ => throw new FormatException($"unknown coupling kind '{kind}'"),
            });
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArgumentOutOfRangeException or FormatException
                      or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            warnings.Add($"coupling '{name}': {exception.Message}");
        }
    }

    // ----------------------------------------------------------------- cam laws

    private static Dictionary<string, object?> SaveLaw(CamLaw law) => law switch
    {
        FunctionCamLaw { Identity: { } identity } => new Dictionary<string, object?>
        {
            ["kind"] = identity.Kind,
            ["args"] = identity.Args,
        },
        SegmentedCamLaw segmented => new Dictionary<string, object?>
        {
            ["kind"] = "segments",
            ["segments"] = segmented.SavedSegments
                .Select(s => new Dictionary<string, object?> { ["span"] = s.Span, ["law"] = SaveLaw(s.Law) })
                .ToArray(),
        },
        SplineCamLaw spline => new Dictionary<string, object?>
        {
            ["kind"] = "spline",
            ["values"] = spline.Values,
        },
        _ => new Dictionary<string, object?> { ["kind"] = "opaque" },
    };

    /// <summary>Rebuilds a saved law by re-running the same factory with the same
    /// arguments (deterministic, so the second save is byte-identical). Null = the law
    /// saved as an opaque marker; an opaque member makes a whole Segments chain opaque.</summary>
    private static CamLaw? LoadLaw(JsonElement element)
    {
        string kind = element.GetProperty("kind").GetString()
            ?? throw new FormatException("cam-law kinds are strings");
        double[] Args() => element.TryGetProperty("args", out var a)
            ? [.. a.EnumerateArray().Select(e => e.GetDouble())]
            : throw new FormatException($"cam law '{kind}' needs its arguments");
        switch (kind)
        {
            case "harmonic":
            {
                var args = Args();
                return CamLaw.Harmonic(args[0], (int)args[1]);
            }
            case "dwell":
                return CamLaw.Dwell(Args()[0]);
            case "linear":
                return CamLaw.Linear(Args()[0]);
            case "cycloidal":
            {
                var args = Args();
                return CamLaw.Cycloidal(args[0], args[1]);
            }
            case "harmonicRise":
            {
                var args = Args();
                return CamLaw.HarmonicRise(args[0], args[1]);
            }
            case "modifiedTrapezoid":
            {
                var args = Args();
                return CamLaw.ModifiedTrapezoid(args[0], args[1]);
            }
            case "segments":
            {
                var segments = new List<(double Span, CamLaw Law)>();
                foreach (var segment in element.GetProperty("segments").EnumerateArray())
                {
                    if (LoadLaw(segment.GetProperty("law")) is not { } law)
                        return null;
                    segments.Add((segment.GetProperty("span").GetDouble(), law));
                }
                return CamLaw.Segments([.. segments]);
            }
            case "spline":
                return new SplineCamLaw(
                    [.. element.GetProperty("values").EnumerateArray().Select(e => e.GetDouble())]);
            case "opaque":
                return null;
            default:
                throw new FormatException($"unknown cam-law kind '{kind}'");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static double[] Components(in Vector3d vector) => [vector.X, vector.Y, vector.Z];

    private static Vector3d ReadVector(JsonElement element) =>
        new(element[0].GetDouble(), element[1].GetDouble(), element[2].GetDouble());

    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetDouble() : 0;

    private static bool TryReadPair(JsonElement element, string property, out (double Min, double Max) pair)
    {
        if (element.TryGetProperty(property, out var value))
        {
            pair = (value[0].GetDouble(), value[1].GetDouble());
            return true;
        }
        pair = default;
        return false;
    }
}
