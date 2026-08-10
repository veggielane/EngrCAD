namespace EngrCAD.Ecad;

/// <summary>
/// The result of a <see cref="PinIdentity.Check(PartDefinition)"/> — whether a part's three
/// representations agree by pin NUMBER. A part loaded with a <see cref="Symbol"/> AND a
/// <see cref="Footprint"/> must have its symbol pins, its footprint pads and its
/// <see cref="Pin"/> list name the SAME numbers (symbol pin "1" == pad "1" == netlist pin "1"):
/// that is the whole point of loading symbol and footprint together.
/// <para>The four lists anchor on the <see cref="Pin"/> list as the authoritative terminal set,
/// so that all four being empty proves the three number-sets are equal. Each entry NAMES the
/// offending number (a check that only said "mismatch" would be useless — the
/// <see cref="SchematicCheckResult"/> culture).</para>
/// </summary>
/// <param name="PinsWithoutSymbolPin">Pin numbers the definition declares that the symbol does
/// not draw (only when a symbol is present).</param>
/// <param name="PinsWithoutPad">Pin numbers the definition declares that the footprint has no
/// pad for (only when a footprint is present).</param>
/// <param name="SymbolPinsNotAPin">Symbol pin numbers that are not a pin of the definition.</param>
/// <param name="PadsNotAPin">Pad numbers that are not a pin of the definition.</param>
public sealed record PinIdentityReport(
    IReadOnlyList<string> PinsWithoutSymbolPin,
    IReadOnlyList<string> PinsWithoutPad,
    IReadOnlyList<string> SymbolPinsNotAPin,
    IReadOnlyList<string> PadsNotAPin)
{
    /// <summary>True when the three representations agree by number: every declared pin has a
    /// symbol pin (if a symbol is present) and a pad (if a footprint is present), and every
    /// symbol pin and every pad is a declared pin.</summary>
    public bool Ok =>
        PinsWithoutSymbolPin.Count == 0 && PinsWithoutPad.Count == 0
        && SymbolPinsNotAPin.Count == 0 && PadsNotAPin.Count == 0;

    /// <summary>Symbol pin numbers with no footprint pad — the "a symbol pin with no matching
    /// pad" case, derived (a symbol pin lacking a pad is either not a pin, or a pin with no pad).
    /// Empty when either representation is absent.</summary>
    public IReadOnlyList<string> SymbolPinsWithoutPad { get; init; } = [];

    /// <summary>Footprint pad numbers with no symbol pin — the mirror of
    /// <see cref="SymbolPinsWithoutPad"/>. Empty when either representation is absent.</summary>
    public IReadOnlyList<string> PadsWithoutSymbolPin { get; init; } = [];

    /// <summary>One line per mismatch, each naming the offending number.</summary>
    public IEnumerable<string> Messages
    {
        get
        {
            foreach (var n in PinsWithoutSymbolPin)
                yield return $"pin '{n}' has no symbol pin";
            foreach (var n in PinsWithoutPad)
                yield return $"pin '{n}' has no footprint pad";
            foreach (var n in SymbolPinsNotAPin)
                yield return $"symbol pin '{n}' is not a pin of the definition";
            foreach (var n in PadsNotAPin)
                yield return $"footprint pad '{n}' is not a pin of the definition";
        }
    }

    /// <summary>A human-readable report.</summary>
    public override string ToString() =>
        Ok ? "pin identity holds (symbol == footprint == netlist)"
           : string.Join(Environment.NewLine, Messages);
}

/// <summary>
/// The one-declaration identity check for a <see cref="PartDefinition"/>: the symbol pins, the
/// footprint pads and the netlist pins are ONE set of numbers, verified. Representations that
/// are absent (no symbol, or no footprint — connectivity does not need either) are simply not
/// cross-checked; the check is only as strong as what the part carries.
/// </summary>
public static class PinIdentity
{
    /// <summary>Checks that a part's symbol pins, footprint pads and pin list agree by number.</summary>
    public static PinIdentityReport Check(PartDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var pins = definition.Pins.Select(p => p.Number).ToHashSet(StringComparer.Ordinal);
        var symbol = definition.Symbol;
        var footprint = definition.Footprint;

        var pinsWithoutSymbolPin = new List<string>();
        var pinsWithoutPad = new List<string>();
        var symbolPinsNotAPin = new List<string>();
        var padsNotAPin = new List<string>();
        var symbolPinsWithoutPad = new List<string>();
        var padsWithoutSymbolPin = new List<string>();

        HashSet<string>? symbolNumbers = null;
        if (symbol is not null)
        {
            symbolNumbers = symbol.PinNumbers.ToHashSet(StringComparer.Ordinal);
            // Walk the DEFINITION's pins so the offenders are reported in declaration order.
            foreach (var number in definition.Pins.Select(p => p.Number))
                if (!symbolNumbers.Contains(number))
                    pinsWithoutSymbolPin.Add(number);
            foreach (var number in symbol.PinNumbers)
                if (!pins.Contains(number))
                    symbolPinsNotAPin.Add(number);
        }

        HashSet<string>? padNumbers = null;
        if (footprint is not null)
        {
            padNumbers = footprint.Pads.Select(p => p.Number).ToHashSet(StringComparer.Ordinal);
            foreach (var number in definition.Pins.Select(p => p.Number))
                if (!padNumbers.Contains(number))
                    pinsWithoutPad.Add(number);
            foreach (var pad in footprint.Pads)
                if (!pins.Contains(pad.Number))
                    padsNotAPin.Add(pad.Number);
        }

        // The direct symbol <-> pad cross-check (only meaningful when both are present).
        if (symbol is not null && footprint is not null && padNumbers is not null && symbolNumbers is not null)
        {
            foreach (var number in symbol.PinNumbers)
                if (!padNumbers.Contains(number))
                    symbolPinsWithoutPad.Add(number);
            foreach (var pad in footprint.Pads)
                if (!symbolNumbers.Contains(pad.Number))
                    padsWithoutSymbolPin.Add(pad.Number);
        }

        return new PinIdentityReport(pinsWithoutSymbolPin, pinsWithoutPad, symbolPinsNotAPin, padsNotAPin)
        {
            SymbolPinsWithoutPad = symbolPinsWithoutPad,
            PadsWithoutSymbolPin = padsWithoutSymbolPin,
        };
    }
}
