using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// A KiCad footprint read from a <c>.kicad_mod</c> file: the <see cref="Footprint"/> (pads) and
/// the diagnostics naming anything the reader could not carry.
/// </summary>
/// <param name="Footprint">The pad layout, mapped onto <see cref="Ecad.Footprint"/>/<see cref="Pad"/>.</param>
/// <param name="Diagnostics">What the reader ignored or approximated.</param>
public sealed record KiCadFootprint(Footprint Footprint, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Reads a KiCad footprint (<c>.kicad_mod</c>) into a <see cref="KiCadFootprint"/>. It maps the
/// COMMON pad subset onto the existing <see cref="Pad"/> vocabulary — SMD and plated
/// through-hole pads of the standard shapes — and NAMES anything it approximates. A pad's
/// coordinates are stated in the file, so pad centres and sizes are carried EXACTLY (not a
/// tolerance); a pad shape or type outside the mapped set is best-effort mapped WITH a
/// diagnostic rather than mis-read silently (the <c>StepReader</c>/<c>IgesReader</c> rule).
///
/// <para><b>Covered:</b> pad number, type (<c>smd</c> → <see cref="PadKind.Smd"/>,
/// <c>thru_hole</c>/<c>np_thru_hole</c> → <see cref="PadKind.ThroughHole"/>), shape
/// (<c>circle</c>/<c>rect</c>/<c>roundrect</c>/<c>oval</c> → <see cref="PadShape"/>),
/// <c>at</c> (centre), <c>size</c> and <c>drill</c>.</para>
///
/// <para><b>Named/approximated:</b> a pad rotation (KiCad's <c>at x y rot</c>) is not carried by
/// <see cref="Pad"/> and a nonzero one is noted; <c>trapezoid</c>/<c>custom</c> shapes map to
/// <see cref="PadShape.Rectangular"/> with a note; an oval drill uses its first dimension with a
/// note; a pad type outside the set maps to <see cref="PadKind.Smd"/> with a note. Silkscreen and
/// courtyard graphics (<c>fp_line</c>/<c>fp_text</c>/…) are not pads and are skipped.</para>
/// </summary>
public static class KiCadFootprintReader
{
    /// <summary>Reads a <c>.kicad_mod</c> file's text.</summary>
    /// <param name="text">The <c>.kicad_mod</c> file text.</param>
    /// <exception cref="FormatException">The file is not a KiCad footprint, or its S-expression
    /// is malformed — refused by name.</exception>
    public static KiCadFootprint Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = SExpr.Parse(text);
        // KiCad 6+ uses (footprint ...); the legacy modules used (module ...).
        if (!string.Equals(root.Head, "footprint", StringComparison.Ordinal)
            && !string.Equals(root.Head, "module", StringComparison.Ordinal))
            throw new FormatException(
                $"Not a KiCad footprint: the top S-expression is '{root.Head}', "
                + "expected 'footprint' or 'module'.");

        string name = root.Arg(0) ?? throw new FormatException("A KiCad footprint has no name.");
        var diagnostics = new List<string>();
        var pads = new List<Pad>();
        foreach (var padList in root.Lists("pad"))
            if (TryPad(padList, name, diagnostics, out var pad))
                pads.Add(pad);

        return new KiCadFootprint(new Footprint(name, pads), diagnostics);
    }

    /// <summary>Reads a <c>.kicad_mod</c> file from disk.</summary>
    public static KiCadFootprint ReadFile(string path) => Read(File.ReadAllText(path));

    private static bool TryPad(SList list, string footprintName, List<string> diagnostics, out Pad pad)
    {
        pad = default;
        // Positional atoms: <number> <type> <shape>.
        string number = list.Arg(0) ?? "";
        string typeToken = list.Arg(1) ?? "";
        string shapeToken = list.Arg(2) ?? "";
        if (number.Length == 0)
        {
            diagnostics.Add($"Footprint '{footprintName}': a pad has no number and was ignored.");
            return false;
        }

        var at = list.ChildNumbers("at");
        var size = list.ChildNumbers("size");
        if (at.Count < 2 || size.Count < 2)
        {
            diagnostics.Add(
                $"Footprint '{footprintName}': pad '{number}' is missing its position or size "
                + "and was ignored.");
            return false;
        }
        var center = new Vector2d(at[0], at[1]);
        if (at.Count >= 3 && at[2] != 0)
            diagnostics.Add(
                $"Footprint '{footprintName}': pad '{number}' rotation {at[2]:g6}° is not carried "
                + "by a footprint pad and was ignored.");
        double width = size[0], height = size[1];

        var (kind, kindKnown) = typeToken switch
        {
            "smd" => (PadKind.Smd, true),
            "thru_hole" => (PadKind.ThroughHole, true),
            "np_thru_hole" => (PadKind.ThroughHole, false),   // non-plated hole — noted
            _ => (PadKind.Smd, false),
        };
        if (!kindKnown)
            diagnostics.Add(
                $"Footprint '{footprintName}': pad '{number}' type '{typeToken}' mapped to {kind}.");

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
        if (!shapeKnown)
            diagnostics.Add(
                $"Footprint '{footprintName}': pad '{number}' shape '{shapeToken}' mapped to {shape}.");

        double drill = 0;
        if (kind == PadKind.ThroughHole)
        {
            var drillList = list.List("drill");
            if (drillList is not null)
            {
                // (drill d) or (drill oval d1 d2) — take the first dimension.
                var nums = drillList.Numbers();
                if (nums.Count == 0 && string.Equals(drillList.Arg(0), "oval", StringComparison.Ordinal))
                {
                    // Skip the 'oval' token: the first positional atom was non-numeric.
                    var args = drillList.Args;
                    if (args.Count >= 2 && SList.TryNumber(args[1].Text, out double ov))
                    {
                        drill = ov;
                        diagnostics.Add(
                            $"Footprint '{footprintName}': pad '{number}' oval drill used its first "
                            + "dimension.");
                    }
                }
                else if (nums.Count >= 1)
                {
                    drill = nums[0];
                }
            }
            if (drill <= 0)
                diagnostics.Add(
                    $"Footprint '{footprintName}': through-hole pad '{number}' has no positive drill.");
            else if (drill >= Math.Min(width, height))
                diagnostics.Add(
                    $"Footprint '{footprintName}': through-hole pad '{number}' drill {drill:g6} leaves "
                    + "no annular ring (drill ≥ pad).");
        }

        // Construct the record directly (not the validating Pad.ThroughHole factory) so a
        // malformed pad IMPORTS and the DRC reports it, rather than the reader throwing on
        // dirty interchange (the "readers never throw on dirty geometry" culture).
        pad = new Pad(number, center, width, height, shape, kind, drill);
        return true;
    }
}
