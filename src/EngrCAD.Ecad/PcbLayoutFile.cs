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
            if (placement.Layer is not null)                 // embedded/inner-layer fields
                record["layer"] = placement.Layer;
            if (placement.Embedding != Embedding.Surface)
                record["embedding"] = placement.Embedding.ToString();
            if (placement.CavityClearance != 0)
                record["cavityClearance"] = placement.CavityClearance;
            placements.Add(record);
        }
        root["placements"] = placements;

        // Vias are layout truth (a placed signal / stitching via is part of the design), so they ride
        // in the file — write-only-when-stated, so a via-free layout stays byte-identical.
        if (layout.Vias.Count > 0)
        {
            var vias = new JsonArray();
            foreach (var via in layout.Vias)
                vias.Add(new JsonObject
                {
                    ["net"] = via.Net,
                    ["x"] = via.X, ["y"] = via.Y,
                    ["from"] = via.FromLayer, ["to"] = via.ToLayer,
                    ["drill"] = via.DrillDiameter, ["pad"] = via.PadDiameter,
                });
            root["vias"] = vias;
        }

        // Traces are layout truth (routed copper is part of the design), so they ride in the file —
        // write-only-when-stated, so an un-routed layout stays byte-identical to a pre-routing one.
        if (layout.Traces.Count > 0)
        {
            var traces = new JsonArray();
            foreach (var trace in layout.Traces)
            {
                var points = new JsonArray();
                foreach (var p in trace.Points)
                    points.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
                traces.Add(new JsonObject
                {
                    ["net"] = trace.Net,
                    ["layer"] = trace.Layer,
                    ["width"] = trace.Width,
                    ["points"] = points,
                });
            }
            root["traces"] = traces;
        }

        // Pours are layout truth (a ground / power plane is part of the design), so they ride in the
        // file — write-only-when-stated (every field omitted when it equals the pour's own default),
        // so a pour-free layout stays byte-identical and a poured one is a save → load → save fixed
        // point.
        if (layout.Pours.Count > 0)
        {
            var pours = new JsonArray();
            foreach (var pour in layout.Pours)
                pours.Add(SavePour(pour));
            root["pours"] = pours;
        }

        // Solder-mask / silkscreen settings are LAYOUT TRUTH like a pour — write ONLY when the caller
        // set them (non-null), and within, write only non-default fields, so a layout that states none
        // saves byte-identically to a pre-fabrication one and a set one is a save->load->save fixed point.
        if (layout.MaskSettings is { } mask)
            root["mask"] = SaveMask(mask);
        if (layout.SilkscreenSettings is { } silk)
            root["silk"] = SaveSilk(silk);

        return root;
    }

    private static JsonObject SaveMask(PcbMaskSettings mask)
    {
        var def = PcbMaskSettings.Default;
        var record = new JsonObject();
        if (mask.Expansion != def.Expansion)
            record["expansion"] = mask.Expansion;
        if (mask.Vias != def.Vias)
            record["vias"] = mask.Vias.ToString();
        return record;
    }

    private static JsonObject SaveSilk(PcbSilkscreenSettings silk)
    {
        var def = PcbSilkscreenSettings.Default;
        var record = new JsonObject();
        if (silk.TextHeight != def.TextHeight)
            record["textHeight"] = silk.TextHeight;
        if (silk.LineWidth != def.LineWidth)
            record["lineWidth"] = silk.LineWidth;
        if (silk.CourtyardMargin != def.CourtyardMargin)
            record["courtyardMargin"] = silk.CourtyardMargin;
        if (silk.ShowReferences != def.ShowReferences)
            record["showReferences"] = silk.ShowReferences;
        if (silk.ShowValues != def.ShowValues)
            record["showValues"] = silk.ShowValues;
        if (silk.ShowCourtyards != def.ShowCourtyards)
            record["showCourtyards"] = silk.ShowCourtyards;
        if (silk.BoardMarks.Count > 0)
        {
            var marks = new JsonArray();
            foreach (var m in silk.BoardMarks)
            {
                var mr = new JsonObject { ["text"] = m.Text, ["x"] = m.Position.X, ["y"] = m.Position.Y };
                if (m.Side != CopperSide.Top)
                    mr["side"] = m.Side.ToString();
                if (m.Height is { } h)
                    mr["height"] = h;
                marks.Add(mr);
            }
            record["marks"] = marks;
        }
        return record;
    }

    private static JsonObject SavePour(CopperPour pour)
    {
        var def = new CopperPour(pour.Net, pour.Layer);   // the defaults, drift-proof
        var record = new JsonObject { ["net"] = pour.Net, ["layer"] = pour.Layer };
        if (pour.Fill != def.Fill)
            record["fill"] = pour.Fill.ToString();
        if (pour.Outline is not null)
        {
            var outline = new JsonArray();
            foreach (var p in pour.Outline)
                outline.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
            record["outline"] = outline;
        }
        if (pour.Clearance != def.Clearance)
            record["clearance"] = pour.Clearance;
        if (pour.DrillClearance != def.DrillClearance)
            record["drillClearance"] = pour.DrillClearance;
        if (pour.EdgeClearance != def.EdgeClearance)
            record["edgeClearance"] = pour.EdgeClearance;
        if (pour.Relief is { } relief)
            record["relief"] = new JsonObject
            {
                ["spokes"] = relief.Spokes,
                ["spokeWidth"] = relief.SpokeWidth,
                ["gap"] = relief.Gap,
                ["startAngle"] = relief.StartAngleDegrees,
            };
        if (pour.Hatch is { } hatch)
            record["hatch"] = new JsonObject
            {
                ["spacing"] = hatch.Spacing,
                ["lineWidth"] = hatch.LineWidth,
                ["angle"] = hatch.AngleDegrees,
                ["crossHatch"] = hatch.CrossHatch,
            };
        if (pour.DeadCopper != def.DeadCopper)
            record["deadCopper"] = pour.DeadCopper.ToString();
        return record;
    }

    private static JsonObject SaveBoard(PcbBoard board)
    {
        var record = new JsonObject { ["thickness"] = board.Thickness };

        var outline = new JsonArray();
        foreach (var p in board.OutlinePoints)
            outline.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
        record["outline"] = outline;

        if (board.LayerStackup is not null)                      // the full physical build-up
        {
            var layers = new JsonArray();
            foreach (var l in board.LayerStackup.Layers)
                layers.Add(new JsonObject
                {
                    ["kind"] = l.Kind.ToString(), ["name"] = l.Name, ["thickness"] = l.Thickness,
                });
            record["layerStackup"] = layers;
        }
        else if (!IsDefaultStackup(board.Stackup, board.Thickness))   // default 2-layer omitted
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
            string? layer = record.TryGetPropertyValue("layer", out var layerNode)
                ? layerNode?.GetValue<string>() : null;
            var embedding = record.TryGetPropertyValue("embedding", out var embNode)
                ? EcadJson.Enum<Embedding>(embNode?.GetValue<string>(), "a placement embedding")
                : Embedding.Surface;
            double cavityClearance = record["cavityClearance"]?.GetValue<double>() ?? 0;
            layout.AddLoadedPlacement(new PcbPlacement(
                EcadJson.String(record, "ref"),
                EcadJson.Double(record, "x"), EcadJson.Double(record, "y"), rot, side,
                layer, embedding, cavityClearance));
        }

        if (root.TryGetPropertyValue("vias", out var viasNode) && viasNode is JsonArray viasArray)
            foreach (var node in viasArray)
            {
                var record = EcadJson.Object(node, "a via");
                layout.AddLoadedVia(new Via(
                    EcadJson.String(record, "net"),
                    EcadJson.Double(record, "x"), EcadJson.Double(record, "y"),
                    EcadJson.String(record, "from"), EcadJson.String(record, "to"),
                    EcadJson.Double(record, "drill"), EcadJson.Double(record, "pad")));
            }

        if (root.TryGetPropertyValue("traces", out var tracesNode) && tracesNode is JsonArray tracesArray)
            foreach (var node in tracesArray)
            {
                var record = EcadJson.Object(node, "a trace");
                var points = new List<Vector2d>();
                foreach (var pointNode in EcadJson.Array(record, "points"))
                {
                    var p = EcadJson.Object(pointNode, "a trace point");
                    points.Add(new Vector2d(EcadJson.Double(p, "x"), EcadJson.Double(p, "y")));
                }
                layout.AddLoadedTrace(new PcbTrace(
                    EcadJson.String(record, "net"), EcadJson.String(record, "layer"),
                    EcadJson.Double(record, "width"), points));
            }

        if (root.TryGetPropertyValue("pours", out var poursNode) && poursNode is JsonArray poursArray)
            foreach (var node in poursArray)
                layout.AddLoadedPour(ReadPour(EcadJson.Object(node, "a pour")));

        if (root.TryGetPropertyValue("mask", out var maskNode) && maskNode is JsonObject maskObj)
            layout.SetLoadedMaskSettings(ReadMask(maskObj));
        if (root.TryGetPropertyValue("silk", out var silkNode) && silkNode is JsonObject silkObj)
            layout.SetLoadedSilkscreenSettings(ReadSilk(silkObj));

        return layout;
    }

    private static PcbMaskSettings ReadMask(JsonObject r)
    {
        var def = PcbMaskSettings.Default;
        var vias = r.TryGetPropertyValue("vias", out var viasNode)
            ? EcadJson.Enum<ViaMaskPolicy>(viasNode?.GetValue<string>(), "a via mask policy")
            : def.Vias;
        return def with
        {
            Expansion = r["expansion"]?.GetValue<double>() ?? def.Expansion,
            Vias = vias,
        };
    }

    private static PcbSilkscreenSettings ReadSilk(JsonObject r)
    {
        var def = PcbSilkscreenSettings.Default;

        IReadOnlyList<SilkMark>? marks = null;
        if (r.TryGetPropertyValue("marks", out var marksNode) && marksNode is JsonArray marksArray)
        {
            var list = new List<SilkMark>();
            foreach (var node in marksArray)
            {
                var m = EcadJson.Object(node, "a silk mark");
                var side = m.TryGetPropertyValue("side", out var sideNode)
                    ? EcadJson.Enum<CopperSide>(sideNode?.GetValue<string>(), "a silk mark side")
                    : CopperSide.Top;
                double? height = m["height"]?.GetValue<double>();
                list.Add(new SilkMark(
                    EcadJson.String(m, "text"),
                    new Vector2d(EcadJson.Double(m, "x"), EcadJson.Double(m, "y")), side, height));
            }
            marks = list;
        }

        return def with
        {
            TextHeight = r["textHeight"]?.GetValue<double>() ?? def.TextHeight,
            LineWidth = r["lineWidth"]?.GetValue<double>() ?? def.LineWidth,
            CourtyardMargin = r["courtyardMargin"]?.GetValue<double>() ?? def.CourtyardMargin,
            ShowReferences = r["showReferences"]?.GetValue<bool>() ?? def.ShowReferences,
            ShowValues = r["showValues"]?.GetValue<bool>() ?? def.ShowValues,
            ShowCourtyards = r["showCourtyards"]?.GetValue<bool>() ?? def.ShowCourtyards,
            Marks = marks,
        };
    }

    private static CopperPour ReadPour(JsonObject record)
    {
        var def = new CopperPour(EcadJson.String(record, "net"), EcadJson.String(record, "layer"));

        var fill = record.TryGetPropertyValue("fill", out var fillNode)
            ? EcadJson.Enum<PourFill>(fillNode?.GetValue<string>(), "a pour fill")
            : def.Fill;

        IReadOnlyList<Vector2d>? outline = null;
        if (record.TryGetPropertyValue("outline", out var outlineNode) && outlineNode is JsonArray outlineArray)
        {
            var points = new List<Vector2d>();
            foreach (var n in outlineArray)
            {
                var p = EcadJson.Object(n, "a pour outline point");
                points.Add(new Vector2d(EcadJson.Double(p, "x"), EcadJson.Double(p, "y")));
            }
            outline = points;
        }

        ThermalRelief? relief = null;
        if (record.TryGetPropertyValue("relief", out var reliefNode) && reliefNode is JsonObject r)
            relief = new ThermalRelief(
                EcadJson.Int(r, "spokes"), EcadJson.Double(r, "spokeWidth"),
                EcadJson.Double(r, "gap"), EcadJson.Double(r, "startAngle"));

        HatchStyle? hatch = null;
        if (record.TryGetPropertyValue("hatch", out var hatchNode) && hatchNode is JsonObject h)
            hatch = new HatchStyle(
                EcadJson.Double(h, "spacing"), EcadJson.Double(h, "lineWidth"),
                EcadJson.Double(h, "angle"), h["crossHatch"]?.GetValue<bool>() ?? true);

        var deadCopper = record.TryGetPropertyValue("deadCopper", out var deadNode)
            ? EcadJson.Enum<DeadCopperPolicy>(deadNode?.GetValue<string>(), "a pour dead-copper policy")
            : def.DeadCopper;

        return def with
        {
            Fill = fill,
            Outline = outline,
            Clearance = record["clearance"]?.GetValue<double>() ?? def.Clearance,
            DrillClearance = record["drillClearance"]?.GetValue<double>() ?? def.DrillClearance,
            EdgeClearance = record["edgeClearance"]?.GetValue<double>() ?? def.EdgeClearance,
            Relief = relief,
            Hatch = hatch,
            DeadCopper = deadCopper,
        };
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

        // A full physical stackup takes precedence over the copper-only one (it derives the copper).
        LayerStackup? layerStackup = null;
        if (record.TryGetPropertyValue("layerStackup", out var layerNode) && layerNode is JsonArray layerArray)
        {
            var stackLayers = new List<StackLayer>();
            foreach (var node in layerArray)
            {
                var l = EcadJson.Object(node, "a stackup layer");
                stackLayers.Add(new StackLayer(
                    EcadJson.Enum<StackLayerKind>(EcadJson.String(l, "kind"), "a stackup layer kind"),
                    EcadJson.String(l, "name"), EcadJson.Double(l, "thickness")));
            }
            layerStackup = new LayerStackup(stackLayers);
        }

        PcbStackup? stackup = null;
        if (layerStackup is null
            && record.TryGetPropertyValue("stackup", out var stackupNode) && stackupNode is JsonArray stackupArray)
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

        return layerStackup is not null
            ? new PcbBoard(outline, layerStackup, holes, keepOuts)
            : new PcbBoard(outline, thickness, stackup, holes, keepOuts);
    }

    private static Frame3d ReadFrame(JsonObject f) => Frame3d.FromOrthonormal(
        new Vector3d(EcadJson.Double(f, "ox"), EcadJson.Double(f, "oy"), EcadJson.Double(f, "oz")),
        new Vector3d(EcadJson.Double(f, "xx"), EcadJson.Double(f, "xy"), EcadJson.Double(f, "xz")),
        new Vector3d(EcadJson.Double(f, "yx"), EcadJson.Double(f, "yy"), EcadJson.Double(f, "yz")));
}
