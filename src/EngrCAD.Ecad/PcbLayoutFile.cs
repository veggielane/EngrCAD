using System.Text.Json.Nodes;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

public sealed partial class PcbLayout
{
    /// <summary>The file format's marker.</summary>
    public const string Format = "engrcad-pcb";

    /// <summary>The version this build writes (and the highest it reads).</summary>
    public const int Version = 1;

    /// <summary>
    /// Serializes the layout to JSON, extending the schematic's seam (<see cref="Schematic.Save"/>):
    /// the schematic travels EMBEDDED (it is the source, not a copy), the board outline/stackup/
    /// holes/keep-outs and the placements ride alongside. Write-only-when-stated throughout (a
    /// default two-layer stackup, an empty hole or keep-out list, a top placement with no rotation,
    /// an identity board frame are all omitted), so <c>save → load → save</c> is a byte-identical
    /// fixed point.
    /// </summary>
    public string Save() => PcbLayoutWriter.Write(this).ToJsonString(EcadJson.Options);

    /// <summary>Rebuilds a layout from <see cref="Save"/> JSON.</summary>
    /// <param name="json">The saved JSON.</param>
    /// <param name="library">An optional library re-attaching 3D bodies by definition name on the
    /// embedded schematic (bodies are code and do not travel).</param>
    /// <exception cref="FormatException">The JSON is not an EngrCAD layout, is a version this build
    /// does not read, or is structurally inconsistent (a placement naming a component the schematic
    /// does not contain, a component placed more than once) — refused by name.</exception>
    public static PcbLayout Load(string json, PartLibrary? library = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return PcbLayoutReader.Read(json, library);
    }

    /// <summary>Writes this layout to a file (see <see cref="Save"/>).</summary>
    public void SaveFile(string path) => File.WriteAllText(path, Save());

    /// <summary>Reads a layout from a file (see <see cref="Load"/>).</summary>
    public static PcbLayout LoadFile(string path, PartLibrary? library = null) =>
        Load(File.ReadAllText(path), library);
}

internal static class PcbLayoutWriter
{
    internal static JsonObject Write(PcbLayout layout)
    {
        var root = new JsonObject
        {
            ["format"] = PcbLayout.Format,
            ["version"] = PcbLayout.Version,
            ["schematic"] = SchematicWriter.BuildRoot(layout.Schematic),
            ["board"] = SaveBoard(layout.Board),
        };

        if (!IsIdentity(layout.BoardFrame))
            root["boardFrame"] = SaveFrame(layout.BoardFrame);

        var placements = new JsonArray();
        foreach (var placement in layout.Placements)
        {
            var record = new JsonObject
            {
                ["ref"] = placement.Reference,
                ["x"] = placement.X,
                ["y"] = placement.Y,
            };
            if (placement.RotationDegrees != 0)              // top-with-no-rotation omits both
                record["rot"] = placement.RotationDegrees;
            if (placement.Side != CopperSide.Top)
                record["side"] = placement.Side.ToString();
            placements.Add(record);
        }
        root["placements"] = placements;

        return root;
    }

    private static JsonObject SaveBoard(PcbBoard board)
    {
        var record = new JsonObject { ["thickness"] = board.Thickness };

        var outline = new JsonArray();
        foreach (var p in board.OutlinePoints)
            outline.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
        record["outline"] = outline;

        if (!IsDefaultStackup(board.Stackup, board.Thickness))   // default 2-layer omitted
        {
            var stackup = new JsonArray();
            foreach (var c in board.Stackup.Coppers)
                stackup.Add(new JsonObject { ["name"] = c.Name, ["z"] = c.Z });
            record["stackup"] = stackup;
        }

        if (board.Holes.Count > 0)
        {
            var holes = new JsonArray();
            foreach (var h in board.Holes)
                holes.Add(new JsonObject
                {
                    ["x"] = h.Center.X, ["y"] = h.Center.Y, ["d"] = h.Diameter,
                    ["kind"] = h.Kind.ToString(),
                });
            record["holes"] = holes;
        }

        if (board.KeepOuts.Count > 0)
        {
            var keepOuts = new JsonArray();
            foreach (var k in board.KeepOuts)
            {
                var poly = new JsonArray();
                foreach (var p in k.Polygon)
                    poly.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
                keepOuts.Add(new JsonObject
                {
                    ["name"] = k.Name, ["kind"] = k.Kind.ToString(), ["poly"] = poly,
                });
            }
            record["keepouts"] = keepOuts;
        }

        return record;
    }

    private static JsonObject SaveFrame(Frame3d frame) => new()
    {
        ["ox"] = frame.Origin.X, ["oy"] = frame.Origin.Y, ["oz"] = frame.Origin.Z,
        ["xx"] = frame.X.X, ["xy"] = frame.X.Y, ["xz"] = frame.X.Z,
        ["yx"] = frame.Y.X, ["yy"] = frame.Y.Y, ["yz"] = frame.Y.Z,
    };

    private static bool IsIdentity(Frame3d f) =>
        f.Origin.X == 0 && f.Origin.Y == 0 && f.Origin.Z == 0
        && f.X.X == 1 && f.X.Y == 0 && f.X.Z == 0
        && f.Y.X == 0 && f.Y.Y == 1 && f.Y.Z == 0;

    private static bool IsDefaultStackup(PcbStackup s, double thickness) =>
        s.Coppers.Count == 2
        && s.Top.Name == "Top" && s.Top.Z.Equals(thickness)
        && s.Bottom.Name == "Bottom" && s.Bottom.Z == 0;
}

internal static class PcbLayoutReader
{
    internal static PcbLayout Read(string json, PartLibrary? library)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new FormatException("A saved layout is a JSON object.");

        string format = EcadJson.String(root, "format");
        if (format != PcbLayout.Format)
            throw new FormatException(
                $"Not an EngrCAD layout (format '{format}', expected '{PcbLayout.Format}').");
        int version = EcadJson.Int(root, "version");
        if (version != PcbLayout.Version)
            throw new FormatException(
                $"Unsupported layout version {version} (this build reads {PcbLayout.Version}).");

        var schematic = SchematicReader.ReadObject(
            EcadJson.Object(root["schematic"], "the embedded schematic"), library);
        var board = ReadBoard(EcadJson.Object(root["board"], "the board"));
        var boardFrame = root.TryGetPropertyValue("boardFrame", out var frameNode)
            ? ReadFrame(EcadJson.Object(frameNode, "the board frame"))
            : Frame3d.WorldXY;

        var layout = new PcbLayout(schematic, board, boardFrame);
        foreach (var node in EcadJson.Array(root, "placements"))
        {
            var record = EcadJson.Object(node, "a placement");
            var side = record.TryGetPropertyValue("side", out var sideNode)
                ? EcadJson.Enum<CopperSide>(sideNode?.GetValue<string>(), "a placement side")
                : CopperSide.Top;
            double rot = record["rot"]?.GetValue<double>() ?? 0;
            layout.AddLoadedPlacement(new PcbPlacement(
                EcadJson.String(record, "ref"),
                EcadJson.Double(record, "x"), EcadJson.Double(record, "y"), rot, side));
        }
        return layout;
    }

    private static PcbBoard ReadBoard(JsonObject record)
    {
        double thickness = EcadJson.Double(record, "thickness");

        var outline = new List<Vector2d>();
        foreach (var node in EcadJson.Array(record, "outline"))
        {
            var p = EcadJson.Object(node, "an outline point");
            outline.Add(new Vector2d(EcadJson.Double(p, "x"), EcadJson.Double(p, "y")));
        }

        PcbStackup? stackup = null;
        if (record.TryGetPropertyValue("stackup", out var stackupNode) && stackupNode is JsonArray stackupArray)
        {
            var coppers = new List<CopperLayerSpec>();
            foreach (var node in stackupArray)
            {
                var c = EcadJson.Object(node, "a copper layer");
                coppers.Add(new CopperLayerSpec(EcadJson.String(c, "name"), EcadJson.Double(c, "z")));
            }
            stackup = new PcbStackup(coppers);
        }

        var holes = new List<BoardHole>();
        if (record.TryGetPropertyValue("holes", out var holesNode) && holesNode is JsonArray holesArray)
            foreach (var node in holesArray)
            {
                var h = EcadJson.Object(node, "a board hole");
                holes.Add(new BoardHole(
                    new Vector2d(EcadJson.Double(h, "x"), EcadJson.Double(h, "y")),
                    EcadJson.Double(h, "d"),
                    EcadJson.Enum<BoardHoleKind>(EcadJson.String(h, "kind"), "a board hole kind")));
            }

        var keepOuts = new List<KeepOut>();
        if (record.TryGetPropertyValue("keepouts", out var keepOutsNode) && keepOutsNode is JsonArray keepOutsArray)
            foreach (var node in keepOutsArray)
            {
                var k = EcadJson.Object(node, "a keep-out");
                var poly = new List<Vector2d>();
                foreach (var pointNode in EcadJson.Array(k, "poly"))
                {
                    var p = EcadJson.Object(pointNode, "a keep-out point");
                    poly.Add(new Vector2d(EcadJson.Double(p, "x"), EcadJson.Double(p, "y")));
                }
                keepOuts.Add(new KeepOut(
                    EcadJson.String(k, "name"),
                    EcadJson.Enum<KeepOutKind>(EcadJson.String(k, "kind"), "a keep-out kind"),
                    poly));
            }

        return new PcbBoard(outline, thickness, stackup, holes, keepOuts);
    }

    private static Frame3d ReadFrame(JsonObject f) => Frame3d.FromOrthonormal(
        new Vector3d(EcadJson.Double(f, "ox"), EcadJson.Double(f, "oy"), EcadJson.Double(f, "oz")),
        new Vector3d(EcadJson.Double(f, "xx"), EcadJson.Double(f, "xy"), EcadJson.Double(f, "xz")),
        new Vector3d(EcadJson.Double(f, "yx"), EcadJson.Double(f, "yy"), EcadJson.Double(f, "yz")));
}
