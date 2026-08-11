using System.Text.Json;
using System.Text.Json.Nodes;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

public sealed partial class Schematic
{
    /// <summary>The file format's marker.</summary>
    public const string Format = "engrcad-schematic";

    /// <summary>The version this build writes (and the highest it reads).</summary>
    public const int Version = 1;

    /// <summary>
    /// Serializes the schematic to JSON. The format follows the document model's conventions
    /// (see <see cref="EngrCAD.Modeling.Document"/>): a JSON tree with two-space indent,
    /// <b>write-only-when-stated</b> optional fields, and no informational field that cannot
    /// round-trip. Part definitions are written ONCE and shared by identity (a definition
    /// used by many components is one entry that the components reference by id), exactly as
    /// the document model interns parts.
    ///
    /// <para><b>What does not travel.</b> A <see cref="PartDefinition.Body"/> is code, so it
    /// is not serialized; supply a <see cref="PartLibrary"/> to <see cref="Load"/> to
    /// re-attach bodies by definition name. Everything else — pins, footprints, values, nets,
    /// no-connects — round-trips, and <c>save → load → save</c> is a byte-identical fixed
    /// point.</para>
    /// </summary>
    public string Save() => SchematicWriter.Write(this);

    /// <summary>Rebuilds a schematic from <see cref="Save"/> JSON.</summary>
    /// <param name="json">The saved JSON.</param>
    /// <param name="library">An optional library re-attaching 3D bodies by definition name;
    /// bodies are code and do not travel in the file. Null leaves loaded definitions
    /// data-only, which is all connectivity needs.</param>
    /// <exception cref="FormatException">The JSON is not an EngrCAD schematic, is a version
    /// this build does not read, or is structurally inconsistent — a component referencing a
    /// definition the file does not contain, a net referencing a component or pin that does
    /// not exist. (The definition is the source: an instance without one is not loadable.)</exception>
    public static Schematic Load(string json, PartLibrary? library = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return SchematicReader.Read(json, library);
    }

    /// <summary>Writes this schematic to a file (see <see cref="Save"/>).</summary>
    public void SaveFile(string path) => File.WriteAllText(path, Save());

    /// <summary>Reads a schematic from a file (see <see cref="Load"/>).</summary>
    public static Schematic LoadFile(string path, PartLibrary? library = null) =>
        Load(File.ReadAllText(path), library);
}

/// <summary>
/// A library of 3D <see cref="PartDefinition.Body"/> builders keyed on definition name,
/// supplied to <see cref="Schematic.Load"/> so a loaded schematic can re-attach the bodies a
/// saved file cannot carry (a body is code — the same reason a lambda-backed feature is
/// re-attached through a hook rather than stored). Optional: without it, loaded definitions
/// are data-only, which is all a connectivity model needs.
/// </summary>
public sealed class PartLibrary
{
    private readonly Dictionary<string, Func<Shape>> _bodies = new(StringComparer.Ordinal);

    /// <summary>Registers a body builder for the definition of this name. Returns this
    /// library so calls chain.</summary>
    public PartLibrary Register(string definitionName, Func<Shape> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(definitionName);
        ArgumentNullException.ThrowIfNull(body);
        _bodies[definitionName] = body;
        return this;
    }

    internal Func<Shape>? BodyFor(string definitionName) =>
        _bodies.GetValueOrDefault(definitionName);
}

/// <summary>The JSON convention shared across the ECAD persistence — the document model's
/// seam (<c>WriteIndented</c>, a JSON tree serialized with <c>ToJsonString</c>). The
/// document model's own options object is internal to <c>EngrCAD.Modeling</c> and so cannot
/// be referenced here; this replicates the convention, not a second one.</summary>
internal static class EcadJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // ---- typed JSON accessors shared across the ECAD readers ----------------
    // (a missing or mistyped field is a structural error, refused by name)

    internal static string String(JsonObject o, string key) =>
        o[key]?.GetValue<string>()
        ?? throw new FormatException($"Missing string field '{key}'.");

    internal static int Int(JsonObject o, string key) =>
        o[key]?.GetValue<int>()
        ?? throw new FormatException($"Missing integer field '{key}'.");

    internal static double Double(JsonObject o, string key) =>
        o[key]?.GetValue<double>()
        ?? throw new FormatException($"Missing number field '{key}'.");

    internal static JsonArray Array(JsonObject o, string key) =>
        o[key] as JsonArray
        ?? throw new FormatException($"Missing array field '{key}'.");

    internal static JsonObject Object(JsonNode? node, string what) =>
        node as JsonObject
        ?? throw new FormatException($"Expected {what} to be a JSON object.");

    internal static T Enum<T>(string? value, string what) where T : struct, Enum
    {
        if (value is not null && System.Enum.TryParse<T>(value, ignoreCase: true, out var parsed))
            return parsed;
        throw new FormatException(
            $"'{value}' is not a valid {typeof(T).Name} for {what} "
            + $"(expected one of: {string.Join(", ", System.Enum.GetNames<T>())}).");
    }
}

internal static class SchematicWriter
{
    internal static string Write(Schematic schematic) =>
        BuildRoot(schematic).ToJsonString(EcadJson.Options);

    /// <summary>Builds the schematic's JSON object (with its own <c>format</c>/<c>version</c>
    /// markers) — the seam a containing document (a <c>PcbLayout</c>) embeds so the schematic
    /// travels as the board's single source of truth rather than being copied.</summary>
    internal static JsonObject BuildRoot(Schematic schematic)
    {
        // Definitions are SHARED (one type instanced by many components), so they are written
        // once under an id and referenced. The walk is deterministic — components in
        // declaration order, first use assigns the id — so a schematic's ids are a function of
        // its structure and nothing else, which is what makes save->load->save byte-identical.
        // PartDefinition is a class with no value equality, so the default comparer is
        // reference identity — the same dedupe rule the document model uses for parts.
        var defIds = new Dictionary<PartDefinition, string>();
        var defs = new List<PartDefinition>();
        foreach (var component in schematic.Components)
            if (defIds.TryAdd(component.Definition, $"d{defs.Count}"))
                defs.Add(component.Definition);

        var root = new JsonObject
        {
            ["format"] = Schematic.Format,
            ["version"] = Schematic.Version,
            ["name"] = schematic.Name,
        };

        var definitions = new JsonArray();
        foreach (var definition in defs)
            definitions.Add(SaveDefinition(definition, defIds[definition]));
        root["definitions"] = definitions;

        var components = new JsonArray();
        foreach (var component in schematic.Components)
        {
            var record = new JsonObject
            {
                ["ref"] = component.ReferenceDesignator,
                ["def"] = defIds[component.Definition],
            };
            if (component.Value.Length > 0)
                record["value"] = component.Value;
            components.Add(record);
        }
        root["components"] = components;

        var nets = new JsonArray();
        foreach (var net in schematic.Nets)
        {
            var record = new JsonObject { ["name"] = net.Name };
            if (net.Kind != NetKind.Signal)   // Signal is the default; write kind only otherwise
                record["kind"] = net.Kind.ToString();
            var pins = new JsonArray();
            foreach (var pin in net.Pins)
                pins.Add(new JsonObject { ["c"] = pin.ReferenceDesignator, ["p"] = pin.Number });
            record["pins"] = pins;
            nets.Add(record);
        }
        root["nets"] = nets;

        return root;
    }

    private static JsonObject SaveDefinition(PartDefinition definition, string id)
    {
        var record = new JsonObject
        {
            ["id"] = id,
            ["name"] = definition.Name,
            ["prefix"] = definition.ReferencePrefix,
        };

        var pins = new JsonArray();
        foreach (var pin in definition.Pins)
        {
            var pinRecord = new JsonObject { ["number"] = pin.Number };
            if (pin.Name.Length > 0)                 // functional name is optional
                pinRecord["name"] = pin.Name;
            pinRecord["type"] = pin.Type.ToString();
            pins.Add(pinRecord);
        }
        record["pins"] = pins;

        if (definition.Footprint is { } footprint)   // footprint is optional
            record["footprint"] = SaveFootprint(footprint);

        if (definition.Symbol is { } symbol)          // symbol is optional
            record["symbol"] = SaveSymbol(symbol);

        // The 3D model is optional AND only a FILE-referenced one travels as data (write-only-when-
        // stated). A code model (a Func<Shape>) is opaque — like the legacy Body, it is re-attached
        // from a PartLibrary — so SaveModel returns null and nothing is written for it.
        if (definition.Model is { } model && SaveModel(model) is { } modelRecord)
            record["model"] = modelRecord;

        return record;
    }

    // A file-referenced model as { path, offset?, rotate?, scale? } — the placement fields written
    // only when non-identity (a default placement omits them). A code model returns null (opaque).
    private static JsonObject? SaveModel(ComponentModel3D model)
    {
        if (!model.IsFileReferenced)
            return null;
        var record = new JsonObject { ["path"] = model.FilePath };
        var placement = model.Placement;
        if (placement.Offset != Vector3d.Zero)
            record["offset"] = Vec3(placement.Offset);
        if (placement.RotationDegrees != Vector3d.Zero)
            record["rotate"] = Vec3(placement.RotationDegrees);
        if (placement.EffectiveScale != new Vector3d(1, 1, 1))
            record["scale"] = Vec3(placement.EffectiveScale);
        return record;
    }

    private static JsonArray Vec3(Vector3d v) => [v.X, v.Y, v.Z];

    private static JsonObject SaveFootprint(Footprint footprint)
    {
        var pads = new JsonArray();
        foreach (var pad in footprint.Pads)
        {
            var record = new JsonObject
            {
                ["number"] = pad.Number,
                ["x"] = pad.Center.X,
                ["y"] = pad.Center.Y,
                ["w"] = pad.Width,
                ["h"] = pad.Height,
                ["shape"] = pad.Shape.ToString(),
            };
            // write-only-when-stated: an SMD pad with no drill (the stage-1 default) omits
            // both, so a stage-1 footprint saves byte-identically.
            if (pad.Kind != PadKind.Smd)
                record["kind"] = pad.Kind.ToString();
            if (pad.DrillDiameter != 0)
                record["drill"] = pad.DrillDiameter;
            pads.Add(record);
        }
        return new JsonObject { ["name"] = footprint.Name, ["pads"] = pads };
    }

    // ---- symbol (the drawn schematic symbol; write-only-when-stated) ---------

    private static JsonObject SaveSymbol(Symbol symbol)
    {
        var record = new JsonObject { ["name"] = symbol.Name };

        if (symbol.Graphics.Count > 0)   // write-only-when-stated: a graphics-less symbol omits it
        {
            var graphics = new JsonArray();
            foreach (var graphic in symbol.Graphics)
                graphics.Add(SaveGraphic(graphic));
            record["graphics"] = graphics;
        }

        var pins = new JsonArray();
        foreach (var pin in symbol.Pins)
        {
            var pinRecord = new JsonObject { ["number"] = pin.Number };
            if (pin.Name.Length > 0)                 // functional name is optional (the Pin rule)
                pinRecord["name"] = pin.Name;
            pinRecord["at"] = Vec(pin.Anchor);
            pinRecord["dir"] = pin.Direction.ToString();
            pinRecord["length"] = pin.Length;
            pinRecord["type"] = pin.Type.ToString();
            pins.Add(pinRecord);
        }
        record["pins"] = pins;
        return record;
    }

    // The writer's default arm THROWS (the Feature-persistence rule): a graphic kind the reader
    // learns and the writer does not takes the whole document down rather than degrading one
    // symbol. SymbolGraphic is a sealed hierarchy, so this switch is exhaustive.
    private static JsonObject SaveGraphic(SymbolGraphic graphic) => graphic switch
    {
        SymbolPolyline p => new JsonObject { ["kind"] = "polyline", ["points"] = Points(p.Points) },
        SymbolRectangle r => new JsonObject
        {
            ["kind"] = "rectangle", ["min"] = Vec(r.Min), ["max"] = Vec(r.Max),
        },
        SymbolCircle c => new JsonObject
        {
            ["kind"] = "circle", ["center"] = Vec(c.Center), ["radius"] = c.Radius,
        },
        SymbolArc a => new JsonObject
        {
            ["kind"] = "arc", ["start"] = Vec(a.Start), ["mid"] = Vec(a.Mid), ["end"] = Vec(a.End),
        },
        SymbolText t => new JsonObject
        {
            ["kind"] = "text", ["value"] = t.Value, ["at"] = Vec(t.At), ["size"] = t.Size,
        },
        _ => throw new NotSupportedException(
            $"Unknown symbol graphic '{graphic.GetType().Name}' has no JSON form."),
    };

    private static JsonArray Vec(Vector2d v) => [v.X, v.Y];

    private static JsonArray Points(IReadOnlyList<Vector2d> points)
    {
        var array = new JsonArray();
        foreach (var p in points)
            array.Add(Vec(p));
        return array;
    }
}

internal static class SchematicReader
{
    internal static Schematic Read(string json, PartLibrary? library)
    {
        JsonObject root = (JsonNode.Parse(json) as JsonObject)
            ?? throw new FormatException("A saved schematic is a JSON object.");
        return ReadObject(root, library);
    }

    /// <summary>Rebuilds a schematic from its JSON object — the seam a containing document
    /// (a <c>PcbLayout</c>) reads back from the embedded schematic.</summary>
    internal static Schematic ReadObject(JsonObject root, PartLibrary? library)
    {
        string format = String(root, "format");
        if (format != Schematic.Format)
            throw new FormatException(
                $"Not an EngrCAD schematic (format '{format}', expected '{Schematic.Format}').");
        int version = Int(root, "version");
        if (version != Schematic.Version)
            throw new FormatException(
                $"Unsupported schematic version {version} (this build reads {Schematic.Version}).");

        var schematic = new Schematic(root["name"]?.GetValue<string>() ?? "");

        var defsById = new Dictionary<string, PartDefinition>();
        foreach (var node in Array(root, "definitions"))
        {
            var record = Object(node, "a definition");
            string id = String(record, "id");
            string name = String(record, "name");
            string prefix = String(record, "prefix");
            var pins = ReadPins(record, name);
            var footprint = record.TryGetPropertyValue("footprint", out var footprintNode)
                ? ReadFootprint(Object(footprintNode, "a footprint"))
                : null;
            var symbol = record.TryGetPropertyValue("symbol", out var symbolNode)
                ? ReadSymbol(Object(symbolNode, "a symbol"))
                : null;
            var model = record.TryGetPropertyValue("model", out var modelNode)
                ? ReadModel(Object(modelNode, "a model"))
                : null;
            var definition = new PartDefinition(
                name, prefix, pins, footprint, library?.BodyFor(name), symbol, model);
            if (!defsById.TryAdd(id, definition))
                throw new FormatException($"Duplicate part-definition id '{id}' in the saved schematic.");
        }

        foreach (var node in Array(root, "components"))
        {
            var record = Object(node, "a component");
            string refdes = String(record, "ref");
            string defId = String(record, "def");
            string value = record["value"]?.GetValue<string>() ?? "";
            if (!defsById.TryGetValue(defId, out var definition))
                throw new FormatException(
                    $"Component '{refdes}' references part definition '{defId}', which the file "
                    + "does not contain — the definition is the source, so the component is not loadable.");
            schematic.Add(refdes, definition, value);
        }

        foreach (var node in Array(root, "nets"))
        {
            var record = Object(node, "a net");
            string name = String(record, "name");
            var kind = record.TryGetPropertyValue("kind", out var kindNode)
                ? ParseEnum<NetKind>(kindNode?.GetValue<string>(), $"net '{name}' kind")
                : NetKind.Signal;
            var pins = new List<PinRef>();
            foreach (var pinNode in Array(record, "pins"))
            {
                var pinRecord = Object(pinNode, $"a pin of net '{name}'");
                string cref = String(pinRecord, "c");
                string number = String(pinRecord, "p");
                var component = schematic.Find(cref)
                    ?? throw new FormatException(
                        $"Net '{name}' references component '{cref}', which the file does not contain.");
                if (!component.Definition.HasPin(number))
                    throw new FormatException(
                        $"Net '{name}' references pin '{number}' of {cref} ({component.Definition.Name}), "
                        + "which has no such pin.");
                pins.Add(new PinRef(component, number));
            }
            schematic.RegisterLoadedNet(name, kind, pins);
        }

        return schematic;
    }

    private static List<Pin> ReadPins(JsonObject definition, string definitionName)
    {
        var pins = new List<Pin>();
        foreach (var node in Array(definition, "pins"))
        {
            var record = Object(node, $"a pin of '{definitionName}'");
            string number = String(record, "number");
            string name = record["name"]?.GetValue<string>() ?? "";
            var type = ParseEnum<PinType>(
                String(record, "type"), $"pin '{number}' type of '{definitionName}'");
            pins.Add(new Pin(number, name, type));
        }
        return pins;
    }

    private static Footprint ReadFootprint(JsonObject footprint)
    {
        string name = String(footprint, "name");
        var pads = new List<Pad>();
        foreach (var node in Array(footprint, "pads"))
        {
            var record = Object(node, $"a pad of footprint '{name}'");
            var kind = record.TryGetPropertyValue("kind", out var kindNode)
                ? ParseEnum<PadKind>(kindNode?.GetValue<string>(), $"a pad kind of footprint '{name}'")
                : PadKind.Smd;
            double drill = record["drill"]?.GetValue<double>() ?? 0;
            pads.Add(new Pad(
                String(record, "number"),
                new Vector2d(Double(record, "x"), Double(record, "y")),
                Double(record, "w"),
                Double(record, "h"),
                ParseEnum<PadShape>(String(record, "shape"), $"a pad shape of footprint '{name}'"),
                kind, drill));
        }
        return new Footprint(name, pads);
    }

    // ---- symbol -------------------------------------------------------------

    private static Symbol ReadSymbol(JsonObject symbol)
    {
        string name = String(symbol, "name");

        var graphics = new List<SymbolGraphic>();
        if (symbol.TryGetPropertyValue("graphics", out var graphicsNode))
        {
            var array = graphicsNode as JsonArray
                ?? throw new FormatException($"Symbol '{name}' 'graphics' must be an array.");
            foreach (var node in array)
                graphics.Add(ReadGraphic(Object(node, $"a graphic of symbol '{name}'")));
        }

        var pins = new List<SymbolPin>();
        foreach (var node in Array(symbol, "pins"))
        {
            var record = Object(node, $"a pin of symbol '{name}'");
            string number = String(record, "number");
            string pinName = record["name"]?.GetValue<string>() ?? "";
            var at = ReadVec(record, "at", $"symbol '{name}' pin '{number}'");
            var dir = ParseEnum<SymbolPinDirection>(
                String(record, "dir"), $"symbol '{name}' pin '{number}' direction");
            double length = Double(record, "length");
            var type = ParseEnum<PinType>(String(record, "type"), $"symbol '{name}' pin '{number}' type");
            pins.Add(new SymbolPin(number, pinName, at, dir, length, type));
        }

        return new Symbol(name, pins, graphics);
    }

    private static SymbolGraphic ReadGraphic(JsonObject g)
    {
        string kind = String(g, "kind");
        return kind switch
        {
            "polyline" => new SymbolPolyline(ReadPoints(g, "points")),
            "rectangle" => new SymbolRectangle(ReadVec(g, "min", "rectangle"), ReadVec(g, "max", "rectangle")),
            "circle" => new SymbolCircle(ReadVec(g, "center", "circle"), Double(g, "radius")),
            "arc" => new SymbolArc(
                ReadVec(g, "start", "arc"), ReadVec(g, "mid", "arc"), ReadVec(g, "end", "arc")),
            "text" => new SymbolText(String(g, "value"), ReadVec(g, "at", "text"), Double(g, "size")),
            _ => throw new FormatException($"Unknown symbol graphic kind '{kind}'."),
        };
    }

    private static Vector2d ReadVec(JsonObject o, string key, string what)
    {
        var array = o[key] as JsonArray
            ?? throw new FormatException($"{what}: '{key}' must be an [x, y] array.");
        if (array.Count < 2)
            throw new FormatException($"{what}: '{key}' must have two numbers.");
        return new Vector2d(array[0]!.GetValue<double>(), array[1]!.GetValue<double>());
    }

    private static List<Vector2d> ReadPoints(JsonObject o, string key)
    {
        var array = o[key] as JsonArray
            ?? throw new FormatException($"A polyline's '{key}' must be an array of [x, y] points.");
        var points = new List<Vector2d>();
        foreach (var node in array)
        {
            var pair = node as JsonArray
                ?? throw new FormatException($"A point of '{key}' must be an [x, y] array.");
            if (pair.Count < 2)
                throw new FormatException($"A point of '{key}' must have two numbers.");
            points.Add(new Vector2d(pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
        }
        return points;
    }

    // ---- 3D model (a file-referenced ComponentModel3D; a code model does not travel) -------

    private static ComponentModel3D ReadModel(JsonObject model)
    {
        string path = String(model, "path");
        var offset = ReadVec3(model, "offset", Vector3d.Zero);
        var rotate = ReadVec3(model, "rotate", Vector3d.Zero);
        var scale = ReadVec3(model, "scale", new Vector3d(1, 1, 1));
        return ComponentModel3D.FromFile(path, new ModelPlacement(offset, rotate, scale));
    }

    private static Vector3d ReadVec3(JsonObject o, string key, Vector3d fallback)
    {
        if (!o.TryGetPropertyValue(key, out var node))
            return fallback;
        var array = node as JsonArray
            ?? throw new FormatException($"A model's '{key}' must be an [x, y, z] array.");
        if (array.Count < 3)
            throw new FormatException($"A model's '{key}' must have three numbers.");
        return new Vector3d(
            array[0]!.GetValue<double>(), array[1]!.GetValue<double>(), array[2]!.GetValue<double>());
    }

    // ---- typed JSON accessors (delegate to the shared EcadJson helpers) ----

    private static string String(JsonObject o, string key) => EcadJson.String(o, key);
    private static int Int(JsonObject o, string key) => EcadJson.Int(o, key);
    private static double Double(JsonObject o, string key) => EcadJson.Double(o, key);
    private static JsonArray Array(JsonObject o, string key) => EcadJson.Array(o, key);
    private static JsonObject Object(JsonNode? node, string what) => EcadJson.Object(node, what);
    private static T ParseEnum<T>(string? value, string what) where T : struct, Enum =>
        EcadJson.Enum<T>(value, what);
}
