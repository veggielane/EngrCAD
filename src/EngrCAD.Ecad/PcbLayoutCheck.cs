namespace EngrCAD.Ecad;

/// <summary>
/// The result of <see cref="PcbLayout.Check"/> — the geometric DRC of a layout, and the lift of
/// the schematic's pin-counting identity onto copper. Each list NAMES its offenders.
///
/// <para><b>The one-declaration identity.</b> Every pin of every placed component resolves to
/// exactly one placed pad at a known copper location. <see cref="PlacedPinCount"/> ==
/// <see cref="PlacedPadCount"/> with every pin covered once (<see cref="PinsCoveredByExactlyOnePad"/>)
/// is the identity — the lift of the schematic's <c>TotalPins == PinsCoveredOnce</c> — and the two
/// lists it splits into (<see cref="PadsWithoutPins"/>, <see cref="PinsWithoutPads"/>) name which
/// way the pin↔pad bijection failed.</para>
/// </summary>
/// <param name="MissingFootprints">Placed components whose definition carries no footprint
/// (<c>"R1 (R_0805)"</c>) — nothing to place on copper.</param>
/// <param name="PadsWithoutPins">Pads whose number matches no pin of the definition
/// (<c>"R1.3"</c>) — the over-covered half of the identity.</param>
/// <param name="PinsWithoutPads">Pins of a placed component with no pad of that number
/// (<c>"U1.7"</c>) — the under-covered half.</param>
/// <param name="PadsOffBoard">Placed pads whose centre lies outside the board outline
/// (<c>"R1.1 at (60, 5)"</c>).</param>
/// <param name="HolesInKeepOut">Vias / mounting holes / through-hole pads whose centre lies in a
/// via or route keep-out (<c>"via at (10, 10) in keep-out 'K1'"</c>).</param>
/// <param name="UnplacedComponents">Schematic components with no placement (<c>"C3"</c>) —
/// informational, a completeness note rather than a fault.</param>
/// <param name="PlacedPadCount">Pads over all placed components with a footprint.</param>
/// <param name="PlacedPinCount">Pins over all placed components with a footprint.</param>
/// <param name="PinsCoveredByExactlyOnePad">Pins with exactly one pad of their number.</param>
public sealed record PcbLayoutCheckResult(
    IReadOnlyList<string> MissingFootprints,
    IReadOnlyList<string> PadsWithoutPins,
    IReadOnlyList<string> PinsWithoutPads,
    IReadOnlyList<string> PadsOffBoard,
    IReadOnlyList<string> HolesInKeepOut,
    IReadOnlyList<string> UnplacedComponents,
    int PlacedPadCount,
    int PlacedPinCount,
    int PinsCoveredByExactlyOnePad)
{
    /// <summary>True when nothing is wrong. Unplaced components are informational (a partial
    /// layout is legal), so they do not fail the check.</summary>
    public bool Ok =>
        MissingFootprints.Count == 0 && PadsWithoutPins.Count == 0 && PinsWithoutPads.Count == 0
        && PadsOffBoard.Count == 0 && HolesInKeepOut.Count == 0;

    /// <summary>Whether the pin↔pad identity holds — pads and pins in bijection by number over
    /// the placed components: every pin covered by exactly one pad, and no pad without a pin.</summary>
    public bool IdentityHolds =>
        PlacedPadCount == PlacedPinCount
        && PinsCoveredByExactlyOnePad == PlacedPinCount
        && PadsWithoutPins.Count == 0;

    /// <summary>One line per problem, each naming its offender.</summary>
    public IEnumerable<string> Messages
    {
        get
        {
            foreach (var c in MissingFootprints)
                yield return $"missing footprint: {c} has no footprint to place";
            foreach (var p in PadsWithoutPins)
                yield return $"pad without a pin: {p} matches no pin of its definition";
            foreach (var p in PinsWithoutPads)
                yield return $"pin without a pad: {p} has no footprint pad";
            foreach (var p in PadsOffBoard)
                yield return $"pad off board: {p}";
            foreach (var h in HolesInKeepOut)
                yield return $"hole in keep-out: {h}";
        }
    }

    /// <summary>A human-readable report.</summary>
    public override string ToString() =>
        Ok ? $"Layout OK ({PlacedPadCount} pads over {PlacedPinCount} pins, identity holds)"
           : string.Join(Environment.NewLine, Messages);
}

/// <summary>The layout checks behind <see cref="PcbLayout.Check"/>.</summary>
internal static class PcbLayoutChecker
{
    internal static PcbLayoutCheckResult Check(PcbLayout layout)
    {
        var missingFootprints = new List<string>();
        var padsWithoutPins = new List<string>();
        var pinsWithoutPads = new List<string>();
        var padsOffBoard = new List<string>();
        var holesInKeepOut = new List<string>();

        int placedPadCount = 0, placedPinCount = 0, pinsCoveredOnce = 0;

        foreach (var placement in layout.Placements)
        {
            var component = layout.Schematic.Find(placement.Reference)!;
            var definition = component.Definition;
            var footprint = definition.Footprint;
            if (footprint is null)
            {
                missingFootprints.Add($"{placement.Reference} ({definition.Name})");
                continue;
            }

            placedPadCount += footprint.Pads.Count;
            placedPinCount += definition.Pins.Count;

            // pin↔pad bijection by number.
            foreach (var pin in definition.Pins)
            {
                int matches = footprint.Pads.Count(pad => pad.Number == pin.Number);
                if (matches == 0)
                    pinsWithoutPads.Add($"{placement.Reference}.{pin.Number}");
                else if (matches == 1)
                    pinsCoveredOnce++;
            }
            foreach (var pad in footprint.Pads)
                if (!definition.HasPin(pad.Number))
                    padsWithoutPins.Add($"{placement.Reference}.{pad.Number}");
        }

        // pads off the board outline.
        foreach (var pad in layout.PlacedPads())
            if (!layout.Board.ContainsInOutline(pad.World))
                padsOffBoard.Add($"{pad.Name} at ({pad.World.X:g6}, {pad.World.Y:g6})");

        // vias / mounting holes / through-hole pads inside a via or route keep-out.
        var viaKeepOuts = layout.Board.KeepOuts
            .Where(k => k.Kind is KeepOutKind.Via or KeepOutKind.Route).ToList();
        if (viaKeepOuts.Count > 0)
        {
            foreach (var hole in layout.Board.Holes)
                foreach (var keepOut in viaKeepOuts)
                    if (keepOut.Contains(hole.Center))
                        holesInKeepOut.Add(
                            $"{hole.Kind.ToString().ToLowerInvariant()} at ({hole.Center.X:g6}, "
                            + $"{hole.Center.Y:g6}) in keep-out '{keepOut.Name}'");
            foreach (var pad in layout.PlacedPads())
                if (pad.Kind == PadKind.ThroughHole)
                    foreach (var keepOut in viaKeepOuts)
                        if (keepOut.Contains(pad.World))
                            holesInKeepOut.Add(
                                $"through-hole pad {pad.Name} at ({pad.World.X:g6}, "
                                + $"{pad.World.Y:g6}) in keep-out '{keepOut.Name}'");
        }

        // components in the schematic that were never placed (informational).
        var placedRefs = layout.Placements.Select(p => p.Reference).ToHashSet(StringComparer.Ordinal);
        var unplaced = layout.Schematic.Components
            .Where(c => !placedRefs.Contains(c.ReferenceDesignator))
            .Select(c => c.ReferenceDesignator)
            .ToList();

        return new PcbLayoutCheckResult(
            missingFootprints, padsWithoutPins, pinsWithoutPads, padsOffBoard, holesInKeepOut,
            unplaced, placedPadCount, placedPinCount, pinsCoveredOnce);
    }
}
