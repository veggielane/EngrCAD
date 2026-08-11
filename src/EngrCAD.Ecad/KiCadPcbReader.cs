using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// A whole KiCad board read from a <c>.kicad_pcb</c> file: the placed <see cref="PcbLayout"/> the
/// reader reconstructed (schematic + board + placements + traces + vias + pours) and the diagnostics
/// naming anything it approximated or could not carry.
///
/// <para>Like an IDF import, a bare board carries no schematic, so the reader SYNTHESIZES one from the
/// pads' own net tags: a footprint becomes a data-only <see cref="PartDefinition"/>, and each pad's
/// <c>(net n name)</c> reconstructs the nets the design intended — which is exactly what
/// <see cref="PcbConnectivity"/> then checks against the imported copper (tracks / vias / zones). So
/// "the connectivity matches what KiCad intended" is a real, checkable claim: an import that connects
/// the wrong pads would show up as a net the copper does not join.</para>
/// </summary>
/// <param name="Layout">The reconstructed layout — schematic, board, placements, traces, vias, pours.</param>
/// <param name="BoardName">The board/design name (the title block's title, or <c>"board"</c>).</param>
/// <param name="Diagnostics">What the reader approximated or could not carry — the
/// <c>StepReader.Diagnostics</c> / <c>IdfReader</c> convention.</param>
public sealed record KiCadPcb(
    PcbLayout Layout, string BoardName, IReadOnlyList<string> Diagnostics)
{
    /// <summary>The number of footprints placed.</summary>
    public int FootprintCount => Layout.Placements.Count;

    /// <summary>The number of track segments imported.</summary>
    public int TrackCount => Layout.Traces.Count;

    /// <summary>The number of vias imported.</summary>
    public int ViaCount => Layout.Vias.Count;

    /// <summary>The number of copper zones (pours) imported.</summary>
    public int ZoneCount => Layout.Pours.Count;

    /// <summary>The number of nets the synthesized schematic carries.</summary>
    public int NetCount => Layout.Schematic.Nets.Count;

    /// <summary>The reconstructed layout (parity with <c>PcbImport.ToLayout</c>).</summary>
    public PcbLayout ToLayout() => Layout;
}

/// <summary>
/// Reads a whole KiCad board (<c>.kicad_pcb</c>) into a <see cref="KiCadPcb"/> — the board twin of the
/// KiCad component reader (<see cref="KiCadFootprintReader"/> / <see cref="KiCadSymbolReader"/>), reusing
/// the same hand-rolled dependency-free S-expression parser (<see cref="SExpr"/>) and the same
/// covered-subset / refuse-by-name discipline. The COMMON subset is mapped; the rest is ignored WITH a
/// named diagnostic (or refused by name), never mis-read silently (the <c>StepReader</c>/<c>IgesReader</c>
/// ethos).
///
/// <para><b>Covered:</b> the board <c>(general (thickness ...))</c>; the copper <c>(layers ...)</c> stack
/// (every <c>.Cu</c> layer, top-most first, mapped to a <see cref="PcbStackup"/>); the board OUTLINE from
/// the <c>Edge.Cuts</c> graphics (<c>gr_line</c> chained, <c>gr_rect</c>, <c>gr_poly</c>, <c>gr_arc</c>
/// flattened); each <c>(footprint ...)</c> as a placed data-only part (its <c>(at)</c>, side, reference,
/// and its <c>(pad ...)</c>s of the standard shapes on their nets); the <c>(net ...)</c> table; copper
/// <c>(segment ...)</c> / <c>(arc ...)</c> tracks; <c>(via ...)</c>; and copper <c>(zone ...)</c>s as
/// pours (their outline + net — EngrCAD RE-DERIVES the fill).</para>
///
/// <para><b>Coordinate convention.</b> KiCad stores Y downward. This reader imports coordinates
/// VERBATIM into the board frame (no Y-flip), so the board lives in the file's own coordinate frame —
/// which is what "pad centres exact from the file's mm coordinates" means, and it is INTERNALLY
/// CONSISTENT (pads, tracks, vias, zones and the outline share one frame), which is all connectivity,
/// the DRC and the Gerber round trip need. A footprint's rotation is taken as a CCW rotation in that
/// frame (the placement pose convention). A note records this.</para>
///
/// <para><b>Ignored / refused BY NAME:</b> keepout / rule-area zones, teardrops, dimension graphics,
/// 3D model references (<c>.wrl</c>/<c>.step</c>), a netless track / via / zone, an arc track
/// (flattened, noted), a KiCad zone's stored fill/hatch geometry (re-derived), a pad shape or type
/// outside the mapped set (best-effort mapped, noted). A non-<c>(kicad_pcb ...)</c> root — including a
/// <c>.kicad_sym</c> or <c>.kicad_mod</c> handed here — or a malformed S-expression is refused by name.</para>
/// </summary>
public static class KiCadPcbReader
{
    /// <summary>Reads a <c>.kicad_pcb</c> file's text into a <see cref="KiCadPcb"/>.</summary>
    /// <param name="text">The <c>.kicad_pcb</c> file text.</param>
    /// <exception cref="FormatException">The file is not a KiCad board, or its S-expression is
    /// malformed — refused by name.</exception>
    public static KiCadPcb Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = SExpr.Parse(text);
        if (!string.Equals(root.Head, "kicad_pcb", StringComparison.Ordinal))
            throw new FormatException(
                $"Not a KiCad board: the top S-expression is '{root.Head ?? "?"}', expected "
                + "'kicad_pcb'. (A '.kicad_sym' symbol library or a '.kicad_mod' footprint is read by "
                + "KiCadSymbolReader / KiCadFootprintReader.)");

        var diagnostics = new List<string>();
        var once = new HashSet<string>(StringComparer.Ordinal);
        void Note(string message) { if (once.Add(message)) diagnostics.Add(message); }

        // ---- the board frame ------------------------------------------------
        Note("KiCad stores Y downward; coordinates are imported verbatim into the board frame (no "
            + "Y-flip), so the board is in the file's own coordinate frame.");
        double thickness = ReadThickness(root, Note);
        var coppers = ReadCopperStackup(root, thickness, Note);
        var copperNames = new HashSet<string>(coppers.Select(c => c.Name), StringComparer.Ordinal);
        var netTable = ReadNetTable(root);
        var outline = ReadOutline(root, Note);
        string boardName = ReadBoardName(root);

        // ---- footprints -> data-only definitions + placements + pad nets ----
        var footprints = new List<ImportedFootprint>();
        var usedRefs = new HashSet<string>(StringComparer.Ordinal);
        int fpIndex = 0;
        foreach (var fp in root.Lists("footprint").Concat(root.Lists("module")))
            footprints.Add(ParseFootprint(fp, ref fpIndex, usedRefs, netTable, Note));

        // ---- the synthesized schematic (the pads' nets ARE the intent) -------
        var schematic = new Schematic(boardName);
        foreach (var f in footprints)
            schematic.Add(f.RefDes, new PartDefinition(f.DefName, f.Prefix, PinsOf(f.Footprint), f.Footprint));

        // Resolve every numbered pad to a net (first assignment wins; a conflict is named).
        var pinNet = new Dictionary<(string Ref, string Number), string>();
        var netOrder = new List<string>();
        var netToPins = new Dictionary<string, List<PinRef>>(StringComparer.Ordinal);
        foreach (var f in footprints)
        {
            var component = schematic.Find(f.RefDes)!;
            foreach (var (number, net) in f.PadNets)
            {
                if (number.Length == 0 || net.Length == 0 || !component.Definition.HasPin(number))
                    continue;
                var key = (f.RefDes, number);
                if (pinNet.TryGetValue(key, out var existing))
                {
                    if (existing != net)
                        Note($"Pad {f.RefDes}.{number} carries two nets ('{existing}' and '{net}'); "
                            + "the first was kept.");
                    continue;
                }
                pinNet[key] = net;
                if (!netToPins.TryGetValue(net, out var list))
                {
                    netToPins[net] = list = [];
                    netOrder.Add(net);
                }
                list.Add(component.Pin(number));
            }
        }
        // A multi-pad net is a signal; a single-pad net is a deliberate stub (both carry connectivity).
        foreach (var net in netOrder)
        {
            var pins = netToPins[net];
            if (pins.Count >= 2)
                schematic.Connect(net, pins);
            else
                schematic.Stub(net, pins[0]);
        }

        // ---- the board and the placed layout --------------------------------
        var board = new PcbBoard(outline, thickness, new PcbStackup(coppers));
        var layout = new PcbLayout(schematic, board);
        foreach (var f in footprints)
            layout.Place(f.RefDes, f.X, f.Y, f.Angle, f.Side);

        // ---- copper: tracks, vias, zones ------------------------------------
        foreach (var segment in root.Lists("segment"))
            if (TryTrack(segment, netTable, copperNames, isArc: false, Note, out var trace))
                layout.AddTrace(trace);
        foreach (var arc in root.Lists("arc"))
            if (TryTrack(arc, netTable, copperNames, isArc: true, Note, out var trace))
                layout.AddTrace(trace);

        foreach (var via in root.Lists("via"))
            ImportVia(via, netTable, copperNames, layout, Note);

        foreach (var zone in root.Lists("zone"))
            ImportZone(zone, netTable, copperNames, schematic, layout, Note);

        return new KiCadPcb(layout, boardName, diagnostics);
    }

    /// <summary>Reads a <c>.kicad_pcb</c> file from disk.</summary>
    public static KiCadPcb ReadFile(string path) => Read(File.ReadAllText(path));

    // ---- board scalars -------------------------------------------------------

    private static double ReadThickness(SList root, Action<string> note)
    {
        var t = root.List("general")?.List("thickness")?.Numbers();
        if (t is { Count: >= 1 } && t[0] > 0)
            return t[0];
        note("No (general (thickness ...)); the board thickness defaulted to 1.6 mm.");
        return 1.6;
    }

    private static string ReadBoardName(SList root)
    {
        string? title = root.List("title_block")?.List("title")?.Arg(0);
        return string.IsNullOrEmpty(title) ? "board" : title;
    }

    /// <summary>The copper layers as an ordered <see cref="CopperLayerSpec"/> list, top-most (F.Cu)
    /// first at z = thickness down to B.Cu at z = 0, inner layers evenly spread — the copper model is
    /// 2D, so the z's carry only the top/bottom ordering and via-span order. A copper layer is any
    /// whose canonical name ends in <c>.Cu</c> (F.Cu, In1.Cu, …, B.Cu).</summary>
    private static List<CopperLayerSpec> ReadCopperStackup(SList root, double thickness, Action<string> note)
    {
        var copper = new List<(int Ordinal, string Name)>();
        var layers = root.List("layers");
        if (layers is not null)
            foreach (var entry in layers.Items.OfType<SList>())
            {
                if (entry.Items.Count < 2 || entry.Items[0] is not SAtom ord || entry.Items[1] is not SAtom nm)
                    continue;
                if (!int.TryParse(ord.Text, out int ordinal))
                    continue;
                string name = nm.Text;
                if (name.EndsWith(".Cu", StringComparison.Ordinal))
                    copper.Add((ordinal, name));
            }

        if (copper.Count < 2)
        {
            note("The (layers ...) section carried fewer than two copper layers; defaulted to a "
                + "two-layer F.Cu / B.Cu stackup.");
            return [new CopperLayerSpec("F.Cu", thickness), new CopperLayerSpec("B.Cu", 0)];
        }

        copper.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));   // F.Cu (0) first, B.Cu (31) last
        int n = copper.Count;
        var specs = new List<CopperLayerSpec>(n);
        for (int i = 0; i < n; i++)
            specs.Add(new CopperLayerSpec(copper[i].Name, thickness * (n - 1 - i) / (n - 1)));
        return specs;
    }

    private static Dictionary<int, string> ReadNetTable(SList root)
    {
        var table = new Dictionary<int, string>();
        foreach (var net in root.Lists("net"))
            if (int.TryParse(net.Arg(0), out int id))
                table[id] = net.Arg(1) ?? "";
        return table;
    }

    // ---- outline (Edge.Cuts) -------------------------------------------------

    private static List<Vector2d> ReadOutline(SList root, Action<string> note)
    {
        var segments = new List<(Vector2d A, Vector2d B)>();
        List<Vector2d>? poly = null;
        Vector2d? circleCenter = null;
        double circleRadius = 0;

        foreach (var g in root.Items.OfType<SList>())
        {
            string head = g.Head ?? "";
            if (head is not ("gr_line" or "gr_rect" or "gr_arc" or "gr_poly" or "gr_circle"))
                continue;
            if (!string.Equals(g.List("layer")?.Arg(0), "Edge.Cuts", StringComparison.Ordinal))
                continue;

            switch (head)
            {
                case "gr_line":
                    if (TryEndpoints(g, out var l0, out var l1))
                        segments.Add((l0, l1));
                    break;
                case "gr_rect":
                    if (TryEndpoints(g, out var r0, out var r2))
                    {
                        var r1 = new Vector2d(r2.X, r0.Y);
                        var r3 = new Vector2d(r0.X, r2.Y);
                        segments.Add((r0, r1));
                        segments.Add((r1, r2));
                        segments.Add((r2, r3));
                        segments.Add((r3, r0));
                    }
                    break;
                case "gr_arc":
                {
                    var s = g.ChildNumbers("start");
                    var m = g.ChildNumbers("mid");
                    var e = g.ChildNumbers("end");
                    if (s.Count >= 2 && e.Count >= 2)
                    {
                        var pts = m.Count >= 2
                            ? FlattenArc(new(s[0], s[1]), new(m[0], m[1]), new(e[0], e[1]))
                            : [new Vector2d(s[0], s[1]), new Vector2d(e[0], e[1])];
                        for (int i = 1; i < pts.Count; i++)
                            segments.Add((pts[i - 1], pts[i]));
                        note("Edge.Cuts arcs were flattened to chords.");
                    }
                    break;
                }
                case "gr_poly":
                    poly ??= ReadPts(g.List("pts"));
                    break;
                case "gr_circle":
                {
                    var c = g.List("center")?.Numbers();
                    var e = g.ChildNumbers("end");
                    if (c is { Count: >= 2 } && e.Count >= 2)
                    {
                        circleCenter = new Vector2d(c[0], c[1]);
                        circleRadius = circleCenter.Value.DistanceTo(new Vector2d(e[0], e[1]));
                    }
                    break;
                }
            }
        }

        if (poly is { Count: >= 3 })
            return poly;

        if (segments.Count > 0)
        {
            var chained = ChainSegments(segments, note);
            if (chained.Count >= 3)
                return chained;
        }

        if (circleCenter is { } cc && circleRadius > 0)
        {
            note("A round Edge.Cuts outline was approximated by a 64-sided polygon.");
            var ring = new List<Vector2d>(64);
            for (int i = 0; i < 64; i++)
            {
                double a = 2 * Math.PI * i / 64;
                ring.Add(cc + new Vector2d(circleRadius * Math.Cos(a), circleRadius * Math.Sin(a)));
            }
            return ring;
        }

        // Last resort: the bounding box of whatever Edge.Cuts geometry we saw (or a default board).
        var points = segments.SelectMany(s => new[] { s.A, s.B }).ToList();
        if (points.Count == 0)
        {
            note("No Edge.Cuts outline was found; defaulted to a 100 × 100 mm board.");
            return [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        }
        note("The Edge.Cuts outline could not be chained into a closed loop; used its bounding box.");
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        return [new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)];
    }

    private static List<Vector2d> ChainSegments(List<(Vector2d A, Vector2d B)> segments, Action<string> note)
    {
        const double tol = 1e-4;
        var used = new bool[segments.Count];
        var loop = new List<Vector2d> { segments[0].A, segments[0].B };
        used[0] = true;
        var current = segments[0].B;
        while (true)
        {
            int found = -1;
            Vector2d next = default;
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i])
                    continue;
                if (segments[i].A.DistanceTo(current) < tol) { found = i; next = segments[i].B; break; }
                if (segments[i].B.DistanceTo(current) < tol) { found = i; next = segments[i].A; break; }
            }
            if (found < 0)
                break;
            used[found] = true;
            loop.Add(next);
            current = next;
        }

        bool closed = loop.Count >= 4 && loop[^1].DistanceTo(loop[0]) < tol;
        if (!closed || Array.Exists(used, u => !u))
            note("The board outline (Edge.Cuts) did not chain into a single closed loop; used the "
                + "segments that connected.");
        return loop;
    }

    // ---- footprints ----------------------------------------------------------

    private sealed record ImportedFootprint(
        string RefDes, string DefName, string Prefix, Footprint Footprint,
        CopperSide Side, double X, double Y, double Angle,
        IReadOnlyList<(string Number, string Net)> PadNets);

    private static ImportedFootprint ParseFootprint(
        SList fp, ref int index, HashSet<string> usedRefs, Dictionary<int, string> netTable, Action<string> note)
    {
        string fpId = fp.Arg(0) ?? "footprint";
        string defName = fpId.Contains(':') ? fpId[(fpId.IndexOf(':') + 1)..] : fpId;
        if (defName.Length == 0)
            defName = "footprint";

        var side = string.Equals(fp.List("layer")?.Arg(0), "B.Cu", StringComparison.Ordinal)
            ? CopperSide.Bottom : CopperSide.Top;

        var at = fp.ChildNumbers("at");
        double x = at.Count >= 1 ? at[0] : 0;
        double y = at.Count >= 2 ? at[1] : 0;
        double angle = at.Count >= 3 ? at[2] : 0;

        string refDes = ReadReference(fp);
        if (refDes.Length == 0 || refDes == "REF**" || !usedRefs.Add(refDes))
        {
            string generated;
            do { generated = $"FP{++index}"; } while (!usedRefs.Add(generated));
            if (refDes.Length != 0)
                note($"Footprint '{refDes}' was renamed to '{generated}' (missing or duplicate reference).");
            refDes = generated;
        }

        var pads = new List<Pad>();
        var padNets = new List<(string, string)>();
        foreach (var padList in fp.Lists("pad"))
        {
            var (pad, net) = ParsePad(padList, netTable, note);
            pads.Add(pad);
            if (pad.Number.Length > 0)
                padNets.Add((pad.Number, net));
        }

        string prefix = PrefixOf(refDes);
        return new ImportedFootprint(
            refDes, defName, prefix, new Footprint(defName, pads), side, x, y, angle, padNets);
    }

    private static string ReadReference(SList fp)
    {
        foreach (var property in fp.Lists("property"))
            if (string.Equals(property.Arg(0), "Reference", StringComparison.Ordinal))
                return property.Arg(1) ?? "";
        foreach (var text in fp.Lists("fp_text"))
            if (string.Equals(text.Arg(0), "reference", StringComparison.Ordinal))
                return text.Arg(1) ?? "";
        return "";
    }

    /// <summary>Parses a board <c>(pad ...)</c> — the same grammar the footprint reader maps, plus the
    /// pad's own <c>(net n name)</c>. Constructed via the record (not the validating factory) so a
    /// malformed pad IMPORTS and the DRC reports it (the "readers never throw on dirty geometry"
    /// culture).</summary>
    private static (Pad Pad, string Net) ParsePad(SList list, Dictionary<int, string> netTable, Action<string> note)
    {
        string number = list.Arg(0) ?? "";
        string typeToken = list.Arg(1) ?? "";
        string shapeToken = list.Arg(2) ?? "";
        var at = list.ChildNumbers("at");
        var size = list.ChildNumbers("size");
        double cx = at.Count >= 1 ? at[0] : 0;
        double cy = at.Count >= 2 ? at[1] : 0;
        double width = size.Count >= 1 ? size[0] : 0;
        double height = size.Count >= 2 ? size[1] : width;

        var (kind, kindKnown) = typeToken switch
        {
            "smd" => (PadKind.Smd, true),
            "thru_hole" => (PadKind.ThroughHole, true),
            "np_thru_hole" => (PadKind.ThroughHole, false),
            _ => (PadKind.Smd, false),
        };
        if (!kindKnown && number.Length > 0)
            note($"Pad '{number}' type '{typeToken}' mapped to {kind}.");

        var (shape, shapeKnown) = shapeToken switch
        {
            "circle" => (PadShape.Round, true),
            "rect" => (PadShape.Rectangular, true),
            "roundrect" => (PadShape.RoundedRectangle, true),
            "oval" => (PadShape.Oval, true),
            "trapezoid" => (PadShape.Rectangular, false),
            "custom" => (PadShape.Rectangular, false),
            _ => (PadShape.Rectangular, false),
        };
        if (!shapeKnown && number.Length > 0)
            note($"Pad '{number}' shape '{shapeToken}' mapped to {shape}.");

        double drill = 0;
        if (kind == PadKind.ThroughHole && list.List("drill") is { } drillList)
        {
            var nums = drillList.Numbers();
            if (nums.Count >= 1)
                drill = nums[0];
            else if (string.Equals(drillList.Arg(0), "oval", StringComparison.Ordinal)
                && drillList.Args.Count >= 2 && SList.TryNumber(drillList.Args[1].Text, out double ov))
            {
                drill = ov;
                note($"Pad '{number}' oval drill used its first dimension.");
            }
        }

        string net = ResolveNet(list.List("net"), netTable);
        var pad = new Pad(number, new Vector2d(cx, cy), width, height, shape, kind, drill);
        return (pad, net);
    }

    private static IEnumerable<Pin> PinsOf(Footprint footprint)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pad in footprint.Pads)
            if (pad.Number.Length > 0 && seen.Add(pad.Number))
                yield return new Pin(pad.Number, PinType.Passive);
    }

    // ---- tracks --------------------------------------------------------------

    private static bool TryTrack(
        SList list, Dictionary<int, string> netTable, HashSet<string> copperNames,
        bool isArc, Action<string> note, out PcbTrace trace)
    {
        trace = default;
        string layer = list.List("layer")?.Arg(0) ?? "";
        if (!copperNames.Contains(layer))
            return false;   // a track on a non-copper layer is not copper we model
        double width = list.ChildNumbers("width") is { Count: >= 1 } w ? w[0] : 0;
        if (!(width > 0))
            return false;
        string net = ResolveNet(list.List("net"), netTable);
        if (net.Length == 0)
        {
            note("A track with no net (net 0) was ignored.");
            return false;
        }

        List<Vector2d> points;
        if (isArc)
        {
            var s = list.ChildNumbers("start");
            var m = list.ChildNumbers("mid");
            var e = list.ChildNumbers("end");
            if (s.Count < 2 || e.Count < 2)
                return false;
            points = m.Count >= 2
                ? FlattenArc(new(s[0], s[1]), new(m[0], m[1]), new(e[0], e[1]))
                : [new Vector2d(s[0], s[1]), new Vector2d(e[0], e[1])];
            note("An arc track was flattened to a polyline.");
        }
        else
        {
            var s = list.ChildNumbers("start");
            var e = list.ChildNumbers("end");
            if (s.Count < 2 || e.Count < 2)
                return false;
            points = [new Vector2d(s[0], s[1]), new Vector2d(e[0], e[1])];
        }

        trace = new PcbTrace(net, layer, width, points);
        return true;
    }

    // ---- vias ----------------------------------------------------------------

    private static void ImportVia(
        SList via, Dictionary<int, string> netTable, HashSet<string> copperNames,
        PcbLayout layout, Action<string> note)
    {
        var at = via.ChildNumbers("at");
        if (at.Count < 2)
            return;
        double size = via.ChildNumbers("size") is { Count: >= 1 } sz ? sz[0] : 0;
        double drill = via.ChildNumbers("drill") is { Count: >= 1 } dr ? dr[0] : 0;
        var layerAtoms = via.List("layers")?.Args;
        string from = layerAtoms is { Count: >= 1 } ? layerAtoms[0].Text : "";
        string to = layerAtoms is { Count: >= 1 } ? layerAtoms[^1].Text : "";
        string net = ResolveNet(via.List("net"), netTable);

        if (net.Length == 0)
        {
            note("A via with no net was ignored.");
            return;
        }
        if (!copperNames.Contains(from) || !copperNames.Contains(to))
        {
            note($"A via spanning '{from}'..'{to}' names a non-copper layer and was ignored.");
            return;
        }

        try
        {
            layout.AddVia(net, at[0], at[1], from, to, drill, size);
        }
        catch (ArgumentException ex)
        {
            note($"A via at ({at[0]:g6}, {at[1]:g6}) could not be imported: {ex.Message}");
        }
    }

    // ---- zones (pours) -------------------------------------------------------

    private static void ImportZone(
        SList zone, Dictionary<int, string> netTable, HashSet<string> copperNames,
        Schematic schematic, PcbLayout layout, Action<string> note)
    {
        if (zone.List("keepout") is not null)
        {
            note("A keepout (rule-area) zone was ignored (rule areas are out of v1 scope).");
            return;
        }

        string net = zone.List("net_name")?.Arg(0) is { Length: > 0 } named
            ? named
            : ResolveNet(zone.List("net"), netTable);
        if (net.Length == 0)
        {
            note("A copper zone with no net was ignored.");
            return;
        }

        var layerAtoms = zone.List("layers")?.Args;
        var zoneLayers = layerAtoms is null
            ? []
            : layerAtoms.Select(a => a.Text).Where(copperNames.Contains).ToList();
        string layer = zoneLayers.Count > 0 ? zoneLayers[0] : (zone.List("layer")?.Arg(0) ?? "");
        if (zoneLayers.Count > 1)
            note($"A multi-layer zone ({string.Join(", ", zoneLayers)}) was imported on its first "
                + $"copper layer ('{layer}') only.");
        if (!copperNames.Contains(layer))
        {
            note($"A copper zone on '{layer}' is not a copper layer and was ignored.");
            return;
        }

        var outline = ReadPts(zone.List("polygon")?.List("pts"));
        if (outline.Count < 3)
        {
            note("A copper zone has no usable outline polygon and was ignored.");
            return;
        }
        if (!schematic.Nets.Any(n => n.Name == net))
        {
            note($"A copper zone on net '{net}' was ignored (the net carries no pads to attach to).");
            return;
        }

        note("Copper zones were imported as pours; EngrCAD RE-DERIVES the fill (KiCad's stored "
            + "filled_polygon / hatch geometry is not read).");
        try
        {
            layout.AddPour(new CopperPour(net, layer, outline));
        }
        catch (ArgumentException ex)
        {
            note($"A copper zone on net '{net}' could not be imported: {ex.Message}");
        }
    }

    // ---- shared parsing helpers ----------------------------------------------

    /// <summary>Resolves a <c>(net id [name])</c> to a net name: the stated name if present, else the
    /// board's net table by id. Empty (net 0, the "no net") comes back empty.</summary>
    private static string ResolveNet(SList? netList, Dictionary<int, string> netTable)
    {
        if (netList is null)
            return "";
        if (netList.Arg(1) is { Length: > 0 } name)
            return name;
        if (int.TryParse(netList.Arg(0), out int id) && netTable.TryGetValue(id, out var n))
            return n;
        return "";
    }

    private static bool TryEndpoints(SList list, out Vector2d start, out Vector2d end)
    {
        start = default;
        end = default;
        var s = list.ChildNumbers("start");
        var e = list.ChildNumbers("end");
        if (s.Count < 2 || e.Count < 2)
            return false;
        start = new Vector2d(s[0], s[1]);
        end = new Vector2d(e[0], e[1]);
        return true;
    }

    private static List<Vector2d> ReadPts(SList? pts)
    {
        var points = new List<Vector2d>();
        if (pts is null)
            return points;
        foreach (var xy in pts.Lists("xy"))
        {
            var n = xy.Numbers();
            if (n.Count >= 2)
                points.Add(new Vector2d(n[0], n[1]));
        }
        return points;
    }

    /// <summary>Flattens a KiCad arc — given by three points ON the arc (start, mid, end) — into a
    /// polyline sampled at ~22.5° steps, keeping the two endpoints exact. Falls back to a chord for
    /// three (near-)collinear points.</summary>
    private static List<Vector2d> FlattenArc(Vector2d s, Vector2d m, Vector2d e)
    {
        double ax = s.X, ay = s.Y, bx = m.X, by = m.Y, cx = e.X, cy = e.Y;
        double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(d) < 1e-12)
            return [s, m, e];

        double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
        double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
        double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
        double r = Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));

        double a1 = Math.Atan2(ay - uy, ax - ux);
        double amid = Mod2Pi(Math.Atan2(by - uy, bx - ux) - a1);
        double aend = Mod2Pi(Math.Atan2(cy - uy, cx - ux) - a1);
        double sweep = amid <= aend ? aend : aend - 2 * Math.PI;   // pass through the mid point

        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 8)));
        var points = new List<Vector2d>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = a1 + sweep * i / steps;
            points.Add(new Vector2d(ux + r * Math.Cos(t), uy + r * Math.Sin(t)));
        }
        return points;
    }

    private static double Mod2Pi(double a)
    {
        double m = a % (2 * Math.PI);
        return m < 0 ? m + 2 * Math.PI : m;
    }

    /// <summary>Strips trailing digits from a reference designator to leave the prefix
    /// (<c>"R1"</c> → <c>"R"</c>); <c>"U"</c> when there is no letter prefix.</summary>
    private static string PrefixOf(string reference)
    {
        int end = reference.Length;
        while (end > 0 && char.IsDigit(reference[end - 1]))
            end--;
        return end > 0 ? reference[..end] : "U";
    }
}
