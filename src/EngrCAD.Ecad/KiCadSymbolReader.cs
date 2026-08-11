using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// A KiCad schematic symbol read from a <c>.kicad_sym</c> file: the drawn <see cref="Symbol"/>,
/// the <see cref="Pin"/> list its pins imply (the netlist terminals), the reference-designator
/// <see cref="ReferencePrefix"/> and the referenced <see cref="FootprintName"/>, plus the
/// diagnostics naming anything the reader could not carry.
/// </summary>
/// <param name="Symbol">The FIRST unit's drawn symbol — graphics plus <see cref="SymbolPin"/>s. For
/// a single-unit part this is the whole drawn symbol; for a MULTI-UNIT part it is a representative and
/// <see cref="Units"/> carries them all.</param>
/// <param name="Pins">The netlist terminals implied by the symbol's pins (number, name, type) — the
/// UNION across every unit, deduplicated by number in unit order.</param>
/// <param name="Units">The per-unit drawn symbols (a dual op-amp has several — amp A, amp B, a power
/// unit), ordered by unit number ascending; a single-unit part has exactly one.</param>
/// <param name="ReferencePrefix">The reference-designator prefix from the <c>Reference</c>
/// property (<c>"R"</c>, <c>"U"</c>).</param>
/// <param name="FootprintName">The footprint the symbol references (<c>"Lib:Name"</c>), or null
/// when the <c>Footprint</c> property is empty.</param>
/// <param name="Diagnostics">What the reader ignored or could not carry — the
/// <c>StepReader.Diagnostics</c> convention.</param>
public sealed record KiCadSymbol(
    Symbol Symbol,
    IReadOnlyList<Pin> Pins,
    IReadOnlyList<Symbol> Units,
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
            parsed.Symbol, parsed.Pins, parsed.Units, parsed.ReferencePrefix, parsed.FootprintName,
            diagnostics);
    }

    /// <summary>Reads a <c>.kicad_sym</c> file from disk.</summary>
    public static KiCadSymbol ReadFile(string path, string? symbolName = null) =>
        Read(File.ReadAllText(path), symbolName);

    // ---- the shared symbol-parsing core --------------------------------------

    /// <summary>
    /// One <c>(symbol …)</c> list parsed into the pieces a caller needs — the per-unit
    /// <see cref="Ecad.Symbol"/>s (<see cref="Units"/>, one per schematic unit — a dual op-amp has
    /// three: amp A, amp B, a power unit), the netlist <see cref="Pin"/>s (their UNION), the
    /// reference prefix, the referenced footprint name, the <c>Value</c> property, and whether it is
    /// a POWER symbol (carries a <c>(power)</c> flag). This is the shared core the <c>.kicad_sym</c>
    /// reader (<see cref="Read"/>) and the <c>.kicad_sch</c> reader (<see cref="KiCadSchReader"/>)
    /// both use, so symbol parsing lives in ONE place — a schematic's embedded <c>lib_symbols</c>
    /// are the same grammar as a symbol library's symbols.
    /// </summary>
    /// <param name="Symbol">The FIRST unit's drawn symbol (a representative — <see cref="Units"/>
    /// carries them all). For a single-unit part it is the sole unit.</param>
    /// <param name="Units">The per-unit symbols, ordered by unit number ascending.</param>
    /// <param name="UnitNumbers">The KiCad unit number of each entry of <see cref="Units"/>, parallel
    /// to it — a placed instance's <c>(unit N)</c> selects the unit with number N.</param>
    internal sealed record ParsedSymbol(
        Symbol Symbol,
        IReadOnlyList<Symbol> Units,
        IReadOnlyList<int> UnitNumbers,
        IReadOnlyList<Pin> Pins,
        string ReferencePrefix,
        string? FootprintName,
        string? Value,
        bool IsPower);

    /// <summary>Parses one <c>(symbol "name" …)</c> list, appending any ignored/approximated
    /// features to <paramref name="diagnostics"/> (the <c>StepReader.Diagnostics</c> convention).
    /// <para>A KiCad symbol is drawn as one or more UNIT sub-symbols named
    /// <c>&lt;name&gt;_&lt;unit&gt;_&lt;style&gt;</c>: unit <c>0</c> is graphics/pins COMMON to every
    /// unit, unit <c>1</c>… are the distinct schematic units (a dual op-amp's amp A / amp B / power),
    /// and <c>style</c> <c>1</c> is the default body while <c>2</c> is the De Morgan alternate (out of
    /// scope — ignored with a named diagnostic). Each unit's <see cref="Ecad.Symbol"/> carries the
    /// common pins plus its own, so a schematic can place each unit at its own location while the
    /// part stays ONE component whose <see cref="ParsedSymbol.Pins"/> are the union.</para></summary>
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

        // Collect graphics and pins PER UNIT. Direct items under the top symbol, and any unit-0
        // sub-symbol, are COMMON (shared by every unit). Each numbered unit sub-symbol is its own.
        var common = new UnitCollector();
        CollectDirect(top, common, diagnostics, name);
        var units = new SortedDictionary<int, UnitCollector>();
        foreach (var sub in top.Lists("symbol"))
        {
            var (unit, style) = ParseUnitStyle(sub.Arg(0));
            if (style >= 2)
            {
                diagnostics.Add(
                    $"Symbol '{name}': a De Morgan / alternate body style (unit_style {style}) is not "
                    + "supported and was ignored.");
                continue;
            }
            var target = unit == 0
                ? common
                : units.TryGetValue(unit, out var c) ? c : units[unit] = new UnitCollector();
            CollectDirect(sub, target, diagnostics, name);
        }

        // Build one Symbol per unit (common graphics/pins + the unit's own, deduped by number — a
        // repeated number within a unit is reported and dropped, so a dirty file never throws from the
        // Symbol ctor). A part with no numbered unit (only common, or nothing) is a single unit — the
        // common alone.
        var unitSymbols = new List<Symbol>();
        var unitNumbers = new List<int>();
        if (units.Count == 0)
        {
            unitSymbols.Add(new Symbol(name, DedupPins(name, 1, common.Pins, diagnostics), common.Graphics));
            unitNumbers.Add(1);
        }
        else
        {
            foreach (var (unitNo, collector) in units)   // SortedDictionary iterates ascending
            {
                var graphics = new List<SymbolGraphic>(common.Graphics);
                graphics.AddRange(collector.Graphics);
                unitSymbols.Add(new Symbol(
                    name, DedupPins(name, unitNo, common.Pins.Concat(collector.Pins), diagnostics), graphics));
                unitNumbers.Add(unitNo);
            }
        }

        // The netlist terminals: the UNION of every unit's pins, deduped by number in unit order.
        // Two units claiming one number with DIFFERENT pin data is an inconsistency reported BY NAME
        // (a reader never throws on dirty input — reconcile to the first, so the part stays loadable).
        var pinList = new List<Pin>();
        var byNumber = new Dictionary<string, SymbolPin>(StringComparer.Ordinal);
        foreach (var unitSym in unitSymbols)
            foreach (var pin in unitSym.Pins)
            {
                if (byNumber.TryGetValue(pin.Number, out var first))
                {
                    if (!string.Equals(first.Name, pin.Name, StringComparison.Ordinal) || first.Type != pin.Type)
                        diagnostics.Add(
                            $"Symbol '{name}': its units disagree about pin '{pin.Number}' "
                            + $"(one is '{first.Name}'/{first.Type}, another '{pin.Name}'/{pin.Type}); "
                            + "the first was used.");
                    continue;
                }
                byNumber[pin.Number] = pin;
                pinList.Add(new Pin(pin.Number, pin.Name, pin.Type));
            }

        return new ParsedSymbol(
            unitSymbols[0], unitSymbols, unitNumbers, pinList, prefix, footprintName, value, isPower);
    }

    /// <summary>The unit and body-style of a unit sub-symbol name
    /// <c>&lt;parent&gt;_&lt;unit&gt;_&lt;style&gt;</c> — the LAST two underscore-separated integer
    /// tokens (the base name may itself contain underscores, e.g. <c>R_0805_0_1</c>). A name that does
    /// not end in two integers falls back to unit 1, style 1 (a plain single-unit symbol).</summary>
    private static (int Unit, int Style) ParseUnitStyle(string? subName)
    {
        if (subName is not null)
        {
            int last = subName.LastIndexOf('_');
            if (last > 0)
            {
                int prev = subName.LastIndexOf('_', last - 1);
                if (prev >= 0
                    && int.TryParse(subName.AsSpan(prev + 1, last - prev - 1), out int unit)
                    && int.TryParse(subName.AsSpan(last + 1), out int style))
                    return (unit, style);
            }
        }
        return (1, 1);
    }

    /// <summary>Deduplicates one unit's pins by number (keep the first, report the rest), so a dirty
    /// file with a repeated pin number is reconciled rather than throwing from the <see cref="Symbol"/>
    /// constructor.</summary>
    private static List<SymbolPin> DedupPins(
        string name, int unitNo, IEnumerable<SymbolPin> pins, List<string> diagnostics)
    {
        var result = new List<SymbolPin>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            if (seen.Add(pin.Number))
                result.Add(pin);
            else
                diagnostics.Add(
                    $"Symbol '{name}' unit {unitNo}: pin number '{pin.Number}' appears more than once; "
                    + "the extra was ignored.");
        }
        return result;
    }

    /// <summary>The graphics and pins collected from ONE unit's sub-symbol (or the common/top level),
    /// before the union dedup.</summary>
    private sealed class UnitCollector
    {
        public List<SymbolGraphic> Graphics { get; } = [];
        public List<SymbolPin> Pins { get; } = [];
    }

    // ---- collecting graphics and pins from one node's DIRECT items -----------

    /// <summary>Collects the graphic primitives and pins DIRECTLY under <paramref name="node"/> into
    /// <paramref name="into"/>. A nested <c>(symbol …)</c> is NOT recursed — units are gathered by the
    /// caller, keyed by the sub-symbol's own unit number.</summary>
    private static void CollectDirect(
        SList node, UnitCollector into, List<string> diagnostics, string symbolName)
    {
        foreach (var item in node.Items.OfType<SList>())
        {
            switch (item.Head)
            {
                case "rectangle":
                    if (TryRectangle(item, out var rect))
                        into.Graphics.Add(rect);
                    break;
                case "circle":
                    if (TryCircle(item, out var circle))
                        into.Graphics.Add(circle);
                    break;
                case "arc":
                    if (TryArc(item, out var arc))
                        into.Graphics.Add(arc);
                    break;
                case "polyline":
                    if (TryPolyline(item, out var poly))
                        into.Graphics.Add(poly);
                    break;
                case "text":
                    if (TryText(item, out var text))
                        into.Graphics.Add(text);
                    break;
                case "pin":
                    if (TryPin(item, diagnostics, out var pin))
                        into.Pins.Add(pin);
                    break;
                case "bezier":
                case "gr_curve":
                    diagnostics.Add(
                        $"Symbol '{symbolName}': graphic '{item.Head}' is not supported and was ignored.");
                    break;
                    // A nested (symbol …) is a unit — handled by the caller. property/pin_names/
                    // pin_numbers/in_bom/on_board/effects/stroke/fill carry no geometry we model.
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

    private static bool TryPin(SList list, List<string> diagnostics, out SymbolPin pin)
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
