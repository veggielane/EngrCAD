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
}

internal static class SchematicWriter
{
    internal static string Write(Schematic schematic)
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

        return root.ToJsonString(EcadJson.Options);
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

        return record;
    }

    private static JsonObject SaveFootprint(Footprint footprint)
    {
        var pads = new JsonArray();
        foreach (var pad in footprint.Pads)
            pads.Add(new JsonObject
            {
                ["number"] = pad.Number,
                ["x"] = pad.Center.X,
                ["y"] = pad.Center.Y,
                ["w"] = pad.Width,
                ["h"] = pad.Height,
                ["shape"] = pad.Shape.ToString(),
            });
        return new JsonObject { ["name"] = footprint.Name, ["pads"] = pads };
    }
}

internal static class SchematicReader
{
    internal static Schematic Read(string json, PartLibrary? library)
    {
        JsonObject root = (JsonNode.Parse(json) as JsonObject)
            ?? throw new FormatException("A saved schematic is a JSON object.");

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
            var definition = new PartDefinition(name, prefix, pins, footprint, library?.BodyFor(name));
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
            pads.Add(new Pad(
                String(record, "number"),
                new Vector2d(Double(record, "x"), Double(record, "y")),
                Double(record, "w"),
                Double(record, "h"),
                ParseEnum<PadShape>(String(record, "shape"), $"a pad shape of footprint '{name}'")));
        }
        return new Footprint(name, pads);
    }

    // ---- typed JSON accessors (a missing/mistyped field is a structural error) ----

    private static string String(JsonObject o, string key) =>
        o[key]?.GetValue<string>()
        ?? throw new FormatException($"Missing string field '{key}' in the saved schematic.");

    private static int Int(JsonObject o, string key) =>
        o[key]?.GetValue<int>()
        ?? throw new FormatException($"Missing integer field '{key}' in the saved schematic.");

    private static double Double(JsonObject o, string key) =>
        o[key]?.GetValue<double>()
        ?? throw new FormatException($"Missing number field '{key}' in the saved schematic.");

    private static JsonArray Array(JsonObject o, string key) =>
        o[key] as JsonArray
        ?? throw new FormatException($"Missing array field '{key}' in the saved schematic.");

    private static JsonObject Object(JsonNode? node, string what) =>
        node as JsonObject
        ?? throw new FormatException($"Expected {what} to be a JSON object.");

    private static T ParseEnum<T>(string? value, string what) where T : struct, Enum
    {
        if (value is not null && Enum.TryParse<T>(value, ignoreCase: true, out var parsed))
            return parsed;
        throw new FormatException(
            $"'{value}' is not a valid {typeof(T).Name} for {what} "
            + $"(expected one of: {string.Join(", ", Enum.GetNames<T>())}).");
    }
}
