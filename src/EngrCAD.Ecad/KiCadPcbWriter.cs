using System.Globalization;
using System.Text;

namespace EngrCAD.Ecad;

/// <summary>
/// Whole KiCad board EXPORT (<c>.kicad_pcb</c>) — the writer twin of <see cref="KiCadPcbReader"/>,
/// which is also its ORACLE: everything this writes, our own reader reads back, so the round trip
/// through the reader (connectivity partition, placements, pad centres, copper, DRC verdict) is a
/// checkable claim rather than a hope, and <c>write → read → write</c> is asserted as a byte fixed
/// point. The emitted subset is exactly the reader's covered subset: the board
/// <c>(general (thickness))</c>, the copper <c>(layers)</c> stack, the <c>(net)</c> table, each
/// placement as a <c>(footprint)</c> with its pads on their nets, <c>(segment)</c> tracks (one per
/// trace chord), <c>(via)</c>s, <c>(zone)</c>s from pours (outline + net + priority — KiCad
/// re-fills by its own rules, exactly as EngrCAD re-derives a fill on import), the
/// <c>Edge.Cuts</c> outline, and the title block.
///
/// <para><b>Layer names</b>: a layout whose copper layers already carry KiCad names (ending
/// <c>.Cu</c>) exports them VERBATIM — which is what makes import → export → import stable — while
/// EngrCAD-native names ("Top"/"Bottom"/inner) map positionally to <c>F.Cu</c>/<c>In1.Cu</c>…/
/// <c>B.Cu</c>. <b>Coordinates are written verbatim</b> (the reader's no-Y-flip convention run the
/// other way): a board imported from KiCad exports in the frame it arrived in.</para>
///
/// <para><b>Refused BY NAME</b> (geometry the file cannot spell without lying): an EMBEDDED or
/// inner-layer-seated placement (KiCad's board vocabulary has no cavity), and a board carrying
/// free <see cref="PcbBoard.Holes"/> (the KiCad idiom is an NPTH footprint pad, which this kernel
/// would re-import as a PLATED pad — a silent copper change; filed). <b>Reported, not refused</b>
/// (analysis/derived-output state the file does not carry): a fabrication spec, mask/silk/paste
/// settings, and teardrop settings — the <c>diagnostics</c> overload names each.</para>
/// </summary>
public static class KiCadPcbWriter
{
    /// <summary>Writes the layout as <c>.kicad_pcb</c> text.</summary>
    /// <param name="layout">The board layout to write.</param>
    /// <param name="boardName">The title-block title; defaults to the schematic's name (or
    /// <c>"board"</c>).</param>
    public static string Write(PcbLayout layout, string? boardName = null) =>
        Write(layout, out _, boardName);

    /// <summary>Writes the layout as <c>.kicad_pcb</c> text, reporting what the format does not
    /// carry (a stated fabrication spec, mask/silk/paste settings, teardrops).</summary>
    public static string Write(
        PcbLayout layout, out IReadOnlyList<string> diagnostics, string? boardName = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var notes = new List<string>();
        diagnostics = notes;

        foreach (var placement in layout.Placements)
        {
            if (placement.IsEmbedded)
                throw new ArgumentException(
                    $"Placement '{placement.Reference}' is embedded — KiCad's board vocabulary "
                    + "has no cavity, so writing it as a surface part would silently change the "
                    + "geometry. Refused.");
            if (placement.Layer is not null)
                throw new ArgumentException(
                    $"Placement '{placement.Reference}' seats on inner layer "
                    + $"'{placement.Layer}', which a .kicad_pcb cannot spell. Refused.");
        }
        if (layout.Board.Holes.Count > 0)
            throw new ArgumentException(
                $"The board carries {layout.Board.Holes.Count} free hole(s). A .kicad_pcb spells "
                + "a mounting hole as an NPTH footprint pad, which this kernel would re-import as "
                + "a PLATED pad — a silent copper change — so free holes are refused (filed).");

        if (layout.Fabrication is not null)
            notes.Add("The stated fabrication spec is not written (a (setup (stackup ...)) "
                + "export is filed).");
        if (layout.MaskSettings is not null || layout.SilkscreenSettings is not null
            || layout.PasteSettings is not null)
            notes.Add("Mask/silk/paste settings are EngrCAD derived-output configuration and are "
                + "not written; KiCad derives those layers by its own rules.");
        if (layout.Teardrops is not null)
            notes.Add("Teardrop settings are EngrCAD derived-copper configuration and are not "
                + "written.");

        if (layout.Schematic.Nets.Any(n => n.Kind == NetKind.NoConnect))
            notes.Add("NoConnect nets are not written — a KiCad pad with no net (net 0) is what a "
                + "no-connect pad means there; the deliberate-no-connect declaration does not travel.");

        var kicadName = LayerNames(layout.Board.Stackup);
        var netlist = layout.Schematic.ToNetlist();
        var netNumber = NetNumbers(layout, netlist);
        string title = boardName ?? (layout.Schematic.Name.Length > 0 ? layout.Schematic.Name : "board");

        var b = new StringBuilder();
        b.Append("(kicad_pcb (version 20221018) (generator \"engrcad\")\n");
        b.Append($"  (general (thickness {Num(layout.Board.Thickness)}))\n");
        b.Append($"  (title_block (title {Quote(title)}))\n");

        // ---- layers: the copper stack top-first, plus Edge.Cuts --------------
        b.Append("  (layers\n");
        var coppers = layout.Board.Stackup.Coppers.OrderByDescending(c => c.Z).ToList();
        for (int i = 0; i < coppers.Count; i++)
        {
            // KiCad's own ordinals: F.Cu = 0, inner 1.., B.Cu = 31; the reader only sorts by
            // ordinal, so the exact bottom number is a compatibility choice, not information.
            int ordinal = i == coppers.Count - 1 ? 31 : i;
            b.Append($"    ({ordinal} {Quote(kicadName[coppers[i].Name])} signal)\n");
        }
        b.Append("    (44 \"Edge.Cuts\" user)\n");
        b.Append("  )\n");

        // ---- the net table ----------------------------------------------------
        b.Append("  (net 0 \"\")\n");
        foreach (var (name, number) in netNumber.OrderBy(kv => kv.Value))
            b.Append($"  (net {number} {Quote(name)})\n");

        // ---- footprints -------------------------------------------------------
        foreach (var placement in layout.Placements)
        {
            var component = layout.Schematic.Find(placement.Reference)!;
            var footprint = component.Definition.Footprint;
            string side = placement.Side == CopperSide.Bottom
                ? kicadName[layout.Board.Stackup.Bottom.Name]
                : kicadName[layout.Board.Stackup.Top.Name];
            b.Append($"  (footprint {Quote(footprint?.Name ?? component.Definition.Name)} "
                + $"(layer {Quote(side)})\n");
            b.Append($"    (at {Num(placement.X)} {Num(placement.Y)}"
                + (placement.RotationDegrees != 0 ? $" {Num(placement.RotationDegrees)}" : "")
                + ")\n");
            b.Append($"    (property \"Reference\" {Quote(placement.Reference)} (at 0 0 0) "
                + "(layer \"F.SilkS\"))\n");
            b.Append($"    (property \"Value\" {Quote(component.Value)} (at 0 0 0) "
                + "(layer \"F.Fab\"))\n");
            if (footprint is not null)
                foreach (var pad in footprint.Pads)
                    WritePad(b, pad, component, netlist, netNumber);
            b.Append("  )\n");
        }

        // ---- copper: tracks, vias, zones --------------------------------------
        foreach (var trace in layout.Traces)
        {
            if (!netNumber.TryGetValue(trace.Net, out int net))
                continue;                                    // unreachable: a trace's net exists
            for (int i = 1; i < trace.Points.Count; i++)
                b.Append($"  (segment (start {Num(trace.Points[i - 1].X)} "
                    + $"{Num(trace.Points[i - 1].Y)}) (end {Num(trace.Points[i].X)} "
                    + $"{Num(trace.Points[i].Y)}) (width {Num(trace.Width)}) "
                    + $"(layer {Quote(kicadName[trace.Layer])}) (net {net}))\n");
        }

        foreach (var via in layout.Vias)
        {
            if (!netNumber.TryGetValue(via.Net, out int net))
                continue;
            b.Append($"  (via (at {Num(via.X)} {Num(via.Y)}) (size {Num(via.PadDiameter)}) "
                + $"(drill {Num(via.DrillDiameter)}) (layers "
                + $"{Quote(kicadName[via.FromLayer])} {Quote(kicadName[via.ToLayer])}) "
                + $"(net {net}))\n");
        }

        foreach (var pour in layout.Pours)
        {
            if (!netNumber.TryGetValue(pour.Net, out int net))
                continue;
            var outline = pour.Outline ?? layout.Board.OutlinePoints;
            b.Append($"  (zone (net {net}) (net_name {Quote(pour.Net)}) "
                + $"(layer {Quote(kicadName[pour.Layer])})");
            if (pour.Priority != 0)
                b.Append($" (priority {pour.Priority})");
            b.Append("\n    (connect_pads (clearance ").Append(Num(pour.Clearance))
                .Append("))\n    (polygon (pts");
            foreach (var p in outline)
                b.Append($" (xy {Num(p.X)} {Num(p.Y)})");
            b.Append("))\n  )\n");
        }
        if (layout.Pours.Count > 0)
            notes.Add("Pours were written as zone outlines; KiCad re-fills them by its own rules "
                + "(thermal relief and clearances beyond (connect_pads) are EngrCAD fill "
                + "parameters).");

        // ---- the outline ------------------------------------------------------
        var pts = layout.Board.OutlinePoints;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var c = pts[(i + 1) % pts.Count];
            b.Append($"  (gr_line (start {Num(a.X)} {Num(a.Y)}) (end {Num(c.X)} {Num(c.Y)}) "
                + "(layer \"Edge.Cuts\") (width 0.1))\n");
        }

        b.Append(")\n");
        return b.ToString();
    }

    /// <summary>Writes the layout to a <c>.kicad_pcb</c> file.</summary>
    public static void WriteFile(PcbLayout layout, string path, string? boardName = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, Write(layout, boardName));
    }

    // ---- pieces --------------------------------------------------------------

    private static void WritePad(
        StringBuilder b, Pad pad, Component component, Netlist netlist,
        Dictionary<string, int> netNumber)
    {
        string type = pad.Kind == PadKind.ThroughHole ? "thru_hole" : "smd";
        string shape = pad.Shape switch
        {
            PadShape.Round => "circle",
            PadShape.Rectangular => "rect",
            PadShape.RoundedRectangle => "roundrect",
            _ => "oval",
        };
        b.Append($"    (pad {Quote(pad.Number)} {type} {shape} "
            + $"(at {Num(pad.Center.X)} {Num(pad.Center.Y)}) "
            + $"(size {Num(pad.Width)} {Num(pad.Height)})");
        if (pad.Kind == PadKind.ThroughHole && pad.DrillDiameter > 0)
            b.Append($" (drill {Num(pad.DrillDiameter)})");
        if (pad.Shape == PadShape.RoundedRectangle)
            b.Append(" (roundrect_rratio 0.25)");
        b.Append(pad.Kind == PadKind.ThroughHole
            ? " (layers \"*.Cu\" \"*.Mask\")"
            : " (layers \"F.Cu\" \"F.Paste\" \"F.Mask\")");
        if (pad.Number.Length > 0 && component.Definition.HasPin(pad.Number)
            && netlist.NetOf(component.Pin(pad.Number)) is { } net
            && netNumber.TryGetValue(net.Name, out int number))
            b.Append($" (net {number} {Quote(net.Name)})");
        b.Append(")\n");
    }

    /// <summary>The KiCad name per copper layer: names already ending <c>.Cu</c> pass VERBATIM
    /// (import → export → import stability); EngrCAD-native names map positionally, F.Cu /
    /// In1.Cu… / B.Cu top-first.</summary>
    private static Dictionary<string, string> LayerNames(PcbStackup stackup)
    {
        var coppers = stackup.Coppers.OrderByDescending(c => c.Z).ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (coppers.All(c => c.Name.EndsWith(".Cu", StringComparison.Ordinal)))
        {
            foreach (var c in coppers)
                map[c.Name] = c.Name;
            return map;
        }
        for (int i = 0; i < coppers.Count; i++)
            map[coppers[i].Name] = i == 0 ? "F.Cu"
                : i == coppers.Count - 1 ? "B.Cu"
                : $"In{i}.Cu";
        return map;
    }

    /// <summary>The net table: 1-based numbers in PAD-ENCOUNTER order (placements in order, each
    /// footprint's pads in order) with any remaining signal/stub nets after — which is exactly the
    /// order <see cref="KiCadPcbReader"/> reconstructs nets in, so <c>write → read → write</c> is
    /// a byte fixed point. NoConnect nets are not numbered — a KiCad pad with no net carries
    /// net 0, which is what a no-connect pad means there.</summary>
    private static Dictionary<string, int> NetNumbers(PcbLayout layout, Netlist netlist)
    {
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        int next = 1;
        void Claim(string name)
        {
            if (!numbers.ContainsKey(name))
                numbers[name] = next++;
        }
        foreach (var placement in layout.Placements)
        {
            var component = layout.Schematic.Find(placement.Reference);
            if (component?.Definition.Footprint is not { } footprint)
                continue;
            foreach (var pad in footprint.Pads)
                if (pad.Number.Length > 0 && component.Definition.HasPin(pad.Number)
                    && netlist.NetOf(component.Pin(pad.Number)) is { } net
                    && net.Kind != NetKind.NoConnect)
                    Claim(net.Name);
        }
        foreach (var net in layout.Schematic.Nets)
            if (net.Kind != NetKind.NoConnect)
                Claim(net.Name);
        return numbers;
    }

    /// <summary>Round-trip number formatting, no exponent (KiCad's grammar is plain decimals).</summary>
    private static string Num(double value)
    {
        string s = value.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('E') ? value.ToString("0.###############", CultureInfo.InvariantCulture) : s;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
