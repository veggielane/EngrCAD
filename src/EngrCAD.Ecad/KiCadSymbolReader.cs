using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// A KiCad schematic symbol read from a <c>.kicad_sym</c> file: the drawn <see cref="Symbol"/>,
/// the <see cref="Pin"/> list its pins imply (the netlist terminals), the reference-designator
/// <see cref="ReferencePrefix"/> and the referenced <see cref="FootprintName"/>, plus the
/// diagnostics naming anything the reader could not carry.
/// </summary>
/// <param name="Symbol">The drawn symbol — graphics plus <see cref="SymbolPin"/>s.</param>
/// <param name="Pins">The netlist terminals implied by the symbol's pins (number, name, type),
/// deduplicated by number in symbol order.</param>
/// <param name="ReferencePrefix">The reference-designator prefix from the <c>Reference</c>
/// property (<c>"R"</c>, <c>"U"</c>).</param>
/// <param name="FootprintName">The footprint the symbol references (<c>"Lib:Name"</c>), or null
/// when the <c>Footprint</c> property is empty.</param>
/// <param name="Diagnostics">What the reader ignored or could not carry — the
/// <c>StepReader.Diagnostics</c> convention.</param>
public sealed record KiCadSymbol(
    Symbol Symbol,
    IReadOnlyList<Pin> Pins,
    string ReferencePrefix,
    string? FootprintName,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Reads a KiCad symbol library (<c>.kicad_sym</c>) into a <see cref="KiCadSymbol"/> — the
/// primary, open, ubiquitous schematic-symbol interchange. It parses the S-expression with the
/// dependency-free <see cref="SExpr"/> reader (structure validated up front, malformed input
/// refused BY NAME) and maps the COMMON subset, refusing or ignoring the rest WITH A NAMED
/// diagnostic rather than mis-reading it silently.
///
/// <para><b>Covered:</b> the top symbol's <c>Reference</c>/<c>Value</c>/<c>Footprint</c>
/// properties; nested unit sub-symbols recursed for graphics and pins; graphic
/// <c>rectangle</c>/<c>circle</c>/<c>arc</c>/<c>polyline</c>/<c>text</c>; and
/// <c>pin</c>s (electrical type, name, number, position, angle, length). A pin's
/// <c>at x y angle</c> is its connection point (where a wire lands) and the angle points from
/// there into the body, exactly as <see cref="SymbolPinDirection"/> records it.</para>
///
/// <para><b>Refused/ignored BY NAME:</b> a symbol graphic kind outside the covered set (e.g.
/// <c>bezier</c>), a pin's alternate pin functions (<c>alternate</c>), and a pin electrical
/// type outside the mapped set — each recorded in <see cref="KiCadSymbol.Diagnostics"/>.</para>
/// </summary>
public static class KiCadSymbolReader
{
    /// <summary>Reads a <c>.kicad_sym</c> file's text.</summary>
    /// <param name="text">The <c>.kicad_sym</c> file text.</param>
    /// <param name="symbolName">The library symbol to read; null reads the FIRST symbol.</param>
    /// <exception cref="FormatException">The file is not a KiCad symbol library, its
    /// S-expression is malformed, it has no symbol, or the named symbol is absent — refused by
    /// name.</exception>
    public static KiCadSymbol Read(string text, string? symbolName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = SExpr.Parse(text);
        if (!string.Equals(root.Head, "kicad_symbol_lib", StringComparison.Ordinal))
            throw new FormatException(
                $"Not a KiCad symbol library: the top S-expression is '{root.Head}', "
                + "expected 'kicad_symbol_lib'.");

        var symbols = root.Lists("symbol").ToList();
        if (symbols.Count == 0)
            throw new FormatException("The KiCad symbol library contains no (symbol ...).");

        SList top;
        if (symbolName is null)
        {
            top = symbols[0];
        }
        else
        {
            top = symbols.FirstOrDefault(s => string.Equals(s.Arg(0), symbolName, StringComparison.Ordinal))
                ?? throw new FormatException(
                    $"The KiCad symbol library has no symbol '{symbolName}'. It has: "
                    + string.Join(", ", symbols.Select(s => s.Arg(0) ?? "?")) + ".");
        }

        var diagnostics = new List<string>();
        var parsed = ParseSymbolList(top, diagnostics);
        return new KiCadSymbol(
            parsed.Symbol, parsed.Pins, parsed.ReferencePrefix, parsed.FootprintName, diagnostics);
    }

    /// <summary>Reads a <c>.kicad_sym</c> file from disk.</summary>
    public static KiCadSymbol ReadFile(string path, string? symbolName = null) =>
        Read(File.ReadAllText(path), symbolName);

    // ---- the shared symbol-parsing core --------------------------------------

    /// <summary>
    /// One <c>(symbol …)</c> list parsed into the pieces a caller needs — the drawn
    /// <see cref="Ecad.Symbol"/>, its netlist <see cref="Pin"/>s, the reference prefix, the
    /// referenced footprint name, the <c>Value</c> property, and whether it is a POWER symbol
    /// (carries a <c>(power)</c> flag). This is the shared core the <c>.kicad_sym</c> reader
    /// (<see cref="Read"/>) and the <c>.kicad_sch</c> reader (<see cref="KiCadSchReader"/>) both
    /// use, so symbol parsing lives in ONE place — a schematic's embedded <c>lib_symbols</c> are
    /// the same grammar as a symbol library's symbols.
    /// </summary>
    internal sealed record ParsedSymbol(
        Symbol Symbol,
        IReadOnlyList<Pin> Pins,
        string ReferencePrefix,
        string? FootprintName,
        string? Value,
        bool IsPower);

    /// <summary>Parses one <c>(symbol "name" …)</c> list, appending any ignored/approximated
    /// features to <paramref name="diagnostics"/> (the <c>StepReader.Diagnostics</c> convention).</summary>
    internal static ParsedSymbol ParseSymbolList(SList top, List<string> diagnostics)
    {
        string name = top.Arg(0) ?? throw new FormatException("A (symbol ...) has no name.");

        // Properties (top level only): Reference -> prefix, Footprint -> referenced name,
        // Value -> the value (a power symbol's Value is its net name).
        string prefix = "U";
        string? footprintName = null;
        string? value = null;
        foreach (var property in top.Lists("property"))
        {
            string? key = property.Arg(0);
            string? propertyValue = property.Arg(1);
            if (string.Equals(key, "Reference", StringComparison.Ordinal) && !string.IsNullOrEmpty(propertyValue))
                prefix = PrefixOf(propertyValue);
            else if (string.Equals(key, "Footprint", StringComparison.Ordinal) && !string.IsNullOrEmpty(propertyValue))
                footprintName = propertyValue;
            else if (string.Equals(key, "Value", StringComparison.Ordinal) && !string.IsNullOrEmpty(propertyValue))
                value = propertyValue;
        }

        // A power symbol carries a bare (power) flag — its pin names the net rather than being a
        // netlist terminal of a placed component.
        bool isPower = top.List("power") is not null;

        // Graphics and pins: walk the symbol subtree (nested unit symbols hold them).
        var graphics = new List<SymbolGraphic>();
        var symbolPins = new List<SymbolPin>();
        var seenPinNumbers = new HashSet<string>(StringComparer.Ordinal);
        Collect(top, graphics, symbolPins, seenPinNumbers, diagnostics, name);

        var symbol = new Symbol(name, symbolPins, graphics);
        // The netlist terminals: one Pin per symbol pin, deduped by number in symbol order.
        var pins = symbolPins.Select(p => new Pin(p.Number, p.Name, p.Type)).ToList();

        return new ParsedSymbol(symbol, pins, prefix, footprintName, value, isPower);
    }

    // ---- collecting graphics and pins ----------------------------------------

    private static void Collect(
        SList node, List<SymbolGraphic> graphics, List<SymbolPin> pins,
        HashSet<string> seenPinNumbers, List<string> diagnostics, string symbolName)
    {
        foreach (var item in node.Items.OfType<SList>())
        {
            switch (item.Head)
            {
                case "rectangle":
                    if (TryRectangle(item, out var rect))
                        graphics.Add(rect);
                    break;
                case "circle":
                    if (TryCircle(item, out var circle))
                        graphics.Add(circle);
                    break;
                case "arc":
                    if (TryArc(item, out var arc))
                        graphics.Add(arc);
                    break;
                case "polyline":
                    if (TryPolyline(item, out var poly))
                        graphics.Add(poly);
                    break;
                case "text":
                    if (TryText(item, out var text))
                        graphics.Add(text);
                    break;
                case "pin":
                    if (TryPin(item, seenPinNumbers, diagnostics, out var pin))
                        pins.Add(pin);
                    break;
                case "symbol":
                    // A nested unit sub-symbol: recurse for its graphics and pins.
                    Collect(item, graphics, pins, seenPinNumbers, diagnostics, symbolName);
                    break;
                case "bezier":
                case "gr_curve":
                    diagnostics.Add(
                        $"Symbol '{symbolName}': graphic '{item.Head}' is not supported and was ignored.");
                    break;
                    // property/pin_names/pin_numbers/in_bom/on_board/effects/stroke/fill etc. carry
                    // no drawn geometry we model — silently skipped.
            }
        }
    }

    private static bool TryRectangle(SList list, out SymbolRectangle rect)
    {
        rect = default!;
        var start = list.ChildNumbers("start");
        var end = list.ChildNumbers("end");
        if (start.Count < 2 || end.Count < 2)
            return false;
        var min = new Vector2d(Math.Min(start[0], end[0]), Math.Min(start[1], end[1]));
        var max = new Vector2d(Math.Max(start[0], end[0]), Math.Max(start[1], end[1]));
        rect = new SymbolRectangle(min, max);
        return true;
    }

    private static bool TryCircle(SList list, out SymbolCircle circle)
    {
        circle = default!;
        var center = list.ChildNumbers("center");
        var radius = list.ChildNumbers("radius");
        if (center.Count < 2 || radius.Count < 1)
            return false;
        circle = new SymbolCircle(new Vector2d(center[0], center[1]), radius[0]);
        return true;
    }

    private static bool TryArc(SList list, out SymbolArc arc)
    {
        arc = default!;
        var start = list.ChildNumbers("start");
        var mid = list.ChildNumbers("mid");
        var end = list.ChildNumbers("end");
        if (start.Count < 2 || mid.Count < 2 || end.Count < 2)
            return false;
        arc = new SymbolArc(
            new Vector2d(start[0], start[1]),
            new Vector2d(mid[0], mid[1]),
            new Vector2d(end[0], end[1]));
        return true;
    }

    private static bool TryPolyline(SList list, out SymbolPolyline poly)
    {
        poly = default!;
        var pts = list.List("pts");
        if (pts is null)
            return false;
        var points = new List<Vector2d>();
        foreach (var xy in pts.Lists("xy"))
        {
            var n = xy.Numbers();
            if (n.Count >= 2)
                points.Add(new Vector2d(n[0], n[1]));
        }
        if (points.Count < 2)
            return false;
        poly = new SymbolPolyline(points);
        return true;
    }

    private static bool TryText(SList list, out SymbolText text)
    {
        text = default!;
        string? value = list.Arg(0);
        var at = list.ChildNumbers("at");
        if (value is null || at.Count < 2)
            return false;
        // font size lives under (effects (font (size w h)))
        double size = 1.27;
        var font = list.List("effects")?.List("font");
        var sizeNums = font?.ChildNumbers("size");
        if (sizeNums is { Count: >= 2 })
            size = sizeNums[1];
        text = new SymbolText(value, new Vector2d(at[0], at[1]), size);
        return true;
    }

    private static bool TryPin(
        SList list, HashSet<string> seenPinNumbers, List<string> diagnostics, out SymbolPin pin)
    {
        pin = default;
        // Positional atoms: <electrical_type> <graphic_style>.
        string electrical = list.Arg(0) ?? "unspecified";
        var (type, known) = MapPinType(electrical);
        var at = list.ChildNumbers("at");
        if (at.Count < 2)
            return false;
        double angle = at.Count >= 3 ? at[2] : 0;
        double length = list.ChildNumbers("length") is { Count: >= 1 } lens ? lens[0] : 0;
        string number = list.List("number")?.Arg(0) ?? "";
        string rawName = list.List("name")?.Arg(0) ?? "";
        string name = rawName == "~" ? "" : rawName;   // KiCad's "~" means no name

        if (number.Length == 0)
        {
            diagnostics.Add("A symbol pin has no number and was ignored.");
            return false;
        }
        if (!seenPinNumbers.Add(number))
        {
            // A stacked/duplicate pin number (e.g. multi-unit): keep the first, note the rest.
            diagnostics.Add($"Symbol pin number '{number}' appears more than once; the extra was ignored.");
            return false;
        }
        if (!known)
            diagnostics.Add(
                $"Symbol pin '{number}' electrical type '{electrical}' has no exact PinType; "
                + $"mapped to {type}.");
        if (list.List("alternate") is not null)
            diagnostics.Add($"Symbol pin '{number}' alternate functions were ignored.");

        pin = new SymbolPin(
            number, name, new Vector2d(at[0], at[1]),
            SymbolPinDirectionExtensions.FromDegrees(angle), length, type);
        return true;
    }

    private static (PinType Type, bool Known) MapPinType(string electrical) => electrical switch
    {
        "input" => (PinType.Input, true),
        "output" => (PinType.Output, true),
        "bidirectional" => (PinType.Bidirectional, true),
        "tri_state" => (PinType.TriState, true),
        "passive" => (PinType.Passive, true),
        "power_in" => (PinType.Power, true),
        "power_out" => (PinType.Power, true),
        "open_collector" => (PinType.OpenCollector, true),
        "open_emitter" => (PinType.OpenCollector, false),   // closest — noted
        "unspecified" => (PinType.Unspecified, true),
        "free" => (PinType.Unspecified, false),             // noted
        "no_connect" => (PinType.Unspecified, false),       // NC is a NET state, not a pin type — noted
        _ => (PinType.Unspecified, false),
    };

    /// <summary>Strips trailing digits from a reference designator to leave the prefix
    /// (<c>"R1"</c> → <c>"R"</c>); a library <c>Reference</c> is usually already the prefix.</summary>
    private static string PrefixOf(string reference)
    {
        int end = reference.Length;
        while (end > 0 && char.IsDigit(reference[end - 1]))
            end--;
        return end > 0 ? reference[..end] : reference;
    }
}
