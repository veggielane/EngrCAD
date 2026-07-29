using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// One feature type as the registry knows it: how a UI names it, whether it can be
/// constructed from data, why not when it cannot, and its <c>[Param]</c> metadata
/// (names, types, ranges, units — values are null; this is the shape of the type, not
/// the state of an instance) for building an insertion dialog before any instance
/// exists.
/// </summary>
public sealed record FeatureTypeInfo(
    string Name,
    Type Type,
    bool CanCreate,
    string? Reason,
    IReadOnlyList<ParamInfo> Parameters);

/// <summary>
/// The feature type registry: discovery for UI insertion ("which features can I add,
/// and what parameters do they take?") and the construction side of persistence —
/// <see cref="FeatureHistory.LoadHistory"/> builds each saved record through
/// <see cref="TryCreate"/>.
///
/// <para><b>What is data-constructible.</b> A feature is creatable when it has a public
/// parameterless constructor (the <c>[Param]</c>-only features: fillet/chamfer rims,
/// patterns) or a registered input factory paired with the feature's
/// <see cref="Feature.SaveInputs"/> (holes carry their <see cref="HoleSpec"/> and
/// points; sketch extrude/revolve carry their <see cref="Sketch"/>, serialized through
/// the exact public <see cref="Curve2d"/> vocabulary — <see cref="Sketch.ToCurves"/> /
/// <see cref="Sketch.FromCurves"/> — so nothing is flattened). What CANNOT round-trip,
/// and why: <see cref="BooleanFeature"/> (an arbitrary <see cref="Shape"/> graph has no
/// serialized form), <see cref="VariableChamferRimFeature"/> (its setback law is code),
/// <see cref="ComponentFeature"/> (a catalogue <see cref="HardwareComponent"/> is a
/// code object), and <see cref="Feature.FromFunc"/> lambdas. Those load through
/// <c>LoadHistory</c>'s resolve hook or not at all — a warning either way the hook
/// returns null.</para>
///
/// <para><see cref="Default"/> carries the built-in Modeling features; construct your
/// own (it copies the defaults) and <see cref="Register{T}"/> your feature types to
/// make them insertable and loadable.</para>
/// </summary>
public sealed class FeatureRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries;

    private sealed record Entry(Type Type, Func<JsonElement?, Feature>? Factory, string? Reason);

    /// <summary>The built-in registry (Modeling's own feature types).</summary>
    public static FeatureRegistry Default { get; } = new();

    /// <summary>A registry seeded with the built-in Modeling feature types.</summary>
    public FeatureRegistry()
    {
        _entries = [];
        foreach (var type in typeof(Feature).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !type.IsPublic
                || !typeof(Feature).IsAssignableFrom(type))
                continue;
            _entries[type.Name] = Describe(type);
        }

        // Input factories for the built-ins whose constructor data serializes exactly.
        RegisterFactory(typeof(HoleFeature), inputs =>
        {
            var element = Require(inputs, nameof(HoleFeature));
            return new HoleFeature(
                InputJson.LoadHoleSpec(element.GetProperty("hole")),
                InputJson.LoadPoints(element.GetProperty("points")));
        });
        RegisterFactory(typeof(ExtrudeSketchFeature), inputs =>
            new ExtrudeSketchFeature(
                InputJson.LoadSketch(Require(inputs, nameof(ExtrudeSketchFeature)).GetProperty("sketch"))));
        RegisterFactory(typeof(RevolveSketchFeature), inputs =>
            new RevolveSketchFeature(
                InputJson.LoadSketch(Require(inputs, nameof(RevolveSketchFeature)).GetProperty("sketch"))));
        RegisterFactory(typeof(BaseFlangeFeature), inputs =>
            new BaseFlangeFeature(
                InputJson.LoadSketch(Require(inputs, nameof(BaseFlangeFeature)).GetProperty("sketch"))));
    }

    private static JsonElement Require(JsonElement? inputs, string type) =>
        inputs ?? throw new FormatException($"{type} needs its saved 'inputs' to be reconstructed.");

    private static Entry Describe(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is not null)
            return new Entry(type, null, null);
        var constructor = type.GetConstructors().FirstOrDefault();
        string wants = constructor is null
            ? "no public constructor"
            : string.Join(", ", constructor.GetParameters()
                .Select(p => $"{p.ParameterType.Name} {p.Name}"));
        return new Entry(type, null,
            $"constructor requires ({wants}) — inputs with no serialized form; " +
            "supply the instance through LoadHistory's resolve hook.");
    }

    /// <summary>Registers (or replaces) a parameterless-constructible feature type —
    /// the way a design makes its own feature classes insertable and loadable.</summary>
    public void Register<T>() where T : Feature, new()
    {
        lock (_gate)
            _entries[typeof(T).Name] = new Entry(typeof(T), _ => new T(), null);
    }

    /// <summary>Registers (or replaces) a feature type with an input factory: the
    /// factory receives the record's saved <c>inputs</c> element (null when the file has
    /// none) and returns the constructed feature. Pair it with an override of
    /// <see cref="Feature.SaveInputs"/> so the inputs are written in the first place.</summary>
    public void Register(Type type, Func<JsonElement?, Feature> factory)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(factory);
        if (!typeof(Feature).IsAssignableFrom(type))
            throw new ArgumentException($"{type.Name} is not a Feature.", nameof(type));
        RegisterFactory(type, factory);
    }

    private void RegisterFactory(Type type, Func<JsonElement?, Feature> factory)
    {
        lock (_gate)
            _entries[type.Name] = new Entry(type, factory, null);
    }

    /// <summary>Every known feature type, creatable or not, ordered by name — the UI
    /// insertion catalogue (filter on <see cref="FeatureTypeInfo.CanCreate"/>).</summary>
    public IReadOnlyList<FeatureTypeInfo> All
    {
        get
        {
            lock (_gate)
                return [.. _entries
                    .OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => ToInfo(e.Key, e.Value))];
        }
    }

    /// <summary>Looks a feature type up by its registered name (the type's short name),
    /// or null.</summary>
    public FeatureTypeInfo? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
            return _entries.TryGetValue(name, out var entry) ? ToInfo(name, entry) : null;
    }

    private static FeatureTypeInfo ToInfo(string name, Entry entry) =>
        new(name, entry.Type,
            entry.Factory is not null || entry.Type.GetConstructor(Type.EmptyTypes) is not null,
            entry.Reason,
            DescribeParameters(entry.Type));

    private static IReadOnlyList<ParamInfo> DescribeParameters(Type type) =>
        [.. Feature.PropertiesOf(type).Select(p =>
        {
            var attribute = p.GetCustomAttribute<ParamAttribute>()!;
            return new ParamInfo(p.Name, p.PropertyType, null,
                attribute.Min, attribute.Max, attribute.Units, attribute.Description);
        })];

    /// <summary>
    /// Constructs a feature by registered name, from its saved inputs when the type
    /// takes any. False (with a reason naming what is missing) for unknown names,
    /// non-creatable types and factories that reject the inputs — never an exception
    /// for bad data, matching <see cref="FeatureHistory.LoadParameters"/>' contract.
    /// </summary>
    public bool TryCreate(string name, JsonElement? inputs, out Feature? feature, out string? error)
    {
        ArgumentNullException.ThrowIfNull(name);
        feature = null;
        Entry? entry;
        lock (_gate)
            _entries.TryGetValue(name, out entry);
        if (entry is null)
        {
            error = $"unknown feature type '{name}' — register it on the FeatureRegistry.";
            return false;
        }
        if (entry.Factory is { } factory)
        {
            try
            {
                feature = factory(inputs);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = $"could not construct {name} from its inputs: {exception.Message}";
                return false;
            }
        }
        if (entry.Type.GetConstructor(Type.EmptyTypes) is not null)
        {
            feature = (Feature)Activator.CreateInstance(entry.Type)!;
            error = null;
            return true;
        }
        error = entry.Reason ?? $"{name} is not data-constructible.";
        return false;
    }
}

/// <summary>
/// JSON forms for the constructor inputs the built-in factories understand. Sketches
/// serialize through the exact public <see cref="Curve2d"/> vocabulary
/// (<see cref="Sketch.ToCurves"/>/<see cref="Sketch.FromCurves"/>), so lines, circular and
/// elliptical arcs and Béziers round-trip with nothing flattened; hole specs write their
/// kind and the factory arguments <see cref="HoleSpec"/> validates on the way back in.
///
/// <para><b>The curve vocabulary here must cover everything <see cref="Sketch.ToCurves"/>
/// can emit, and that is a test rather than a convention</b>
/// (<c>FeatureRegistryTests.EverySketchSegmentKind_HasAJsonForm</c> enumerates the segment
/// types from the assembly). The reason it is worth pinning: this writer's default case
/// THROWS, and <see cref="Document.Save"/> has no catch around
/// <see cref="FeatureHistory.SaveHistory"/> — so a curve kind the reader learned and the
/// writer did not takes the whole document down rather than degrading one feature.</para>
/// </summary>
internal static class InputJson
{
    // ---- points ----

    internal static JsonArray SavePoints(IReadOnlyList<Vector2d> points) =>
        new([.. points.Select(p => (JsonNode)new JsonArray(p.X, p.Y))]);

    internal static IReadOnlyList<Vector2d> LoadPoints(JsonElement element) =>
        [.. element.EnumerateArray().Select(p => new Vector2d(p[0].GetDouble(), p[1].GetDouble()))];

    // ---- hole specs ----

    internal static JsonObject SaveHoleSpec(HoleSpec hole)
    {
        var json = new JsonObject();
        if (hole.IsCounterbore)
        {
            json["kind"] = "counterbore";
            json["diameter"] = hole.Diameter;
            json["counterboreDiameter"] = hole.FeatureDiameter;
            json["counterboreDepth"] = hole.CounterboreDepth;
        }
        else if (hole.IsCountersink)
        {
            json["kind"] = "countersink";
            json["diameter"] = hole.Diameter;
            json["countersinkDiameter"] = hole.FeatureDiameter;
            json["angleDegrees"] = hole.CountersinkAngleDegrees;
        }
        else
        {
            json["kind"] = "simple";
            json["diameter"] = hole.Diameter;
        }
        if (hole.TipAngleDegrees is { } tip)
            json["tipAngleDegrees"] = tip;
        return json;
    }

    internal static HoleSpec LoadHoleSpec(JsonElement element)
    {
        string kind = element.GetProperty("kind").GetString() ?? "simple";
        double diameter = element.GetProperty("diameter").GetDouble();
        var hole = kind switch
        {
            "counterbore" => HoleSpec.Counterbore(
                diameter,
                element.GetProperty("counterboreDiameter").GetDouble(),
                element.GetProperty("counterboreDepth").GetDouble()),
            "countersink" => HoleSpec.Countersink(
                diameter,
                element.GetProperty("countersinkDiameter").GetDouble(),
                element.GetProperty("angleDegrees").GetDouble()),
            "simple" => HoleSpec.Simple(diameter),
            _ => throw new FormatException($"unknown hole kind '{kind}'"),
        };
        if (element.TryGetProperty("tipAngleDegrees", out var tip))
            hole = hole.WithTipAngle(tip.GetDouble());
        return hole;
    }

    // ---- sketches ----

    internal static JsonObject SaveSketch(Sketch sketch)
    {
        var json = new JsonObject { ["outer"] = SaveCurves(sketch.ToCurves()) };
        if (sketch.Holes.Count > 0)
            json["holes"] = new JsonArray([.. sketch.Holes.Select(h => (JsonNode)SaveCurves(h.ToCurves()))]);
        return json;
    }

    internal static Sketch LoadSketch(JsonElement element)
    {
        var sketch = Sketch.FromCurves(LoadCurves(element.GetProperty("outer")));
        if (element.TryGetProperty("holes", out var holes))
        {
            foreach (var hole in holes.EnumerateArray())
                sketch = sketch.WithHole(Sketch.FromCurves(LoadCurves(hole)));
        }
        return sketch;
    }

    private static JsonArray SaveCurves(IReadOnlyList<Curve2d> curves) =>
        new([.. curves.Select(curve => (JsonNode)(curve switch
        {
            Line2d line => new JsonObject
            {
                ["line"] = new JsonArray(line.Start.X, line.Start.Y, line.End.X, line.End.Y),
            },
            Arc2d arc => new JsonObject
            {
                ["arc"] = new JsonArray(
                    arc.Center.X, arc.Center.Y, arc.Radius, arc.StartAngle, arc.SweepAngle),
            },
            // An elliptical arc stores BOTH semi-axis VECTORS, not a pair of lengths plus a
            // rotation: that is the representation EllipseSeg carries, so a rotated ellipse
            // round-trips without anyone re-deriving an angle from a vector and back.
            Ellipse2d ellipse => new JsonObject
            {
                ["ellipse"] = new JsonArray(
                    ellipse.Center.X, ellipse.Center.Y,
                    ellipse.SemiAxisX.X, ellipse.SemiAxisX.Y,
                    ellipse.SemiAxisY.X, ellipse.SemiAxisY.Y,
                    ellipse.StartAngle, ellipse.SweepAngle),
            },
            BezierCurve2d bezier => new JsonObject
            {
                ["bezier"] = new JsonArray(
                    [.. bezier.ControlPoints.SelectMany(p => new JsonNode[] { p.X, p.Y })]),
            },
            _ => throw new FormatException(
                $"sketch curve {curve.GetType().Name} has no JSON form"),
        }))]);

    private static IReadOnlyList<Curve2d> LoadCurves(JsonElement element)
    {
        var curves = new List<Curve2d>();
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.TryGetProperty("line", out var line))
            {
                curves.Add(new Line2d(
                    new Vector2d(line[0].GetDouble(), line[1].GetDouble()),
                    new Vector2d(line[2].GetDouble(), line[3].GetDouble())));
            }
            else if (entry.TryGetProperty("arc", out var arc))
            {
                curves.Add(new Arc2d(
                    new Vector2d(arc[0].GetDouble(), arc[1].GetDouble()),
                    arc[2].GetDouble(), arc[3].GetDouble(), arc[4].GetDouble()));
            }
            else if (entry.TryGetProperty("ellipse", out var ellipse))
            {
                curves.Add(new Ellipse2d(
                    new Vector2d(ellipse[0].GetDouble(), ellipse[1].GetDouble()),
                    new Vector2d(ellipse[2].GetDouble(), ellipse[3].GetDouble()),
                    new Vector2d(ellipse[4].GetDouble(), ellipse[5].GetDouble()),
                    ellipse[6].GetDouble(), ellipse[7].GetDouble()));
            }
            else if (entry.TryGetProperty("bezier", out var bezier))
            {
                var flat = bezier.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                var control = new Vector2d[flat.Length / 2];
                for (int i = 0; i < control.Length; i++)
                    control[i] = new Vector2d(flat[2 * i], flat[2 * i + 1]);
                curves.Add(new BezierCurve2d(control));
            }
            else
            {
                throw new FormatException($"unknown sketch curve record: {entry.GetRawText()}");
            }
        }
        return curves;
    }
}
