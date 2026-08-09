namespace EngrCAD.Ecad;

/// <summary>
/// The result of <see cref="Schematic.Check"/> — the DRC of connectivity, combinatorial and
/// exact. Each list NAMES its offenders (a check that only said "invalid" would be useless).
///
/// <para><b>The counting identity.</b> A well-formed schematic satisfies
/// <see cref="TotalPins"/> == <see cref="PinsCoveredOnce"/> with no over-assignments: every
/// terminal of every component is on exactly one net (signal, stub, or no-connect). The two
/// counts are exposed so that identity can be asserted numerically, and the two lists it
/// splits into (<see cref="UnassignedPins"/>, <see cref="MultiplyAssignedPins"/>) name which
/// way it failed.</para>
/// </summary>
/// <param name="UnassignedPins">Terminals on no net and not marked no-connect
/// (<c>"R1.2"</c>) — the under-covered half of the counting identity.</param>
/// <param name="MultiplyAssignedPins">Terminals on more than one net
/// (<c>"U3.7 on nets: VCC, GND"</c>) — the over-covered half, which includes a terminal
/// that is both wired and marked no-connect.</param>
/// <param name="FloatingNets">Signal nets with fewer than two terminals
/// (<c>"LED_A (1 pin: R1.2)"</c>) — a single-terminal wire that connects to nothing. Stub
/// and no-connect nets are exempt by their kind.</param>
/// <param name="EmptyNets">Nets with no terminals at all (<c>"SPARE"</c>).</param>
/// <param name="TotalPins">Every terminal of every component.</param>
/// <param name="PinsCoveredOnce">Terminals on exactly one net.</param>
public sealed record SchematicCheckResult(
    IReadOnlyList<string> UnassignedPins,
    IReadOnlyList<string> MultiplyAssignedPins,
    IReadOnlyList<string> FloatingNets,
    IReadOnlyList<string> EmptyNets,
    int TotalPins,
    int PinsCoveredOnce)
{
    /// <summary>True when nothing is wrong: every terminal is covered exactly once and no net
    /// is floating or empty.</summary>
    public bool Ok =>
        UnassignedPins.Count == 0 && MultiplyAssignedPins.Count == 0
        && FloatingNets.Count == 0 && EmptyNets.Count == 0;

    /// <summary>Whether the counting identity holds (every terminal covered exactly once).
    /// The assignment half of <see cref="Ok"/> — true iff both
    /// <see cref="UnassignedPins"/> and <see cref="MultiplyAssignedPins"/> are empty.</summary>
    public bool CountingIdentityHolds =>
        TotalPins == PinsCoveredOnce && MultiplyAssignedPins.Count == 0;

    /// <summary>One line per problem, each naming its offender.</summary>
    public IEnumerable<string> Messages
    {
        get
        {
            foreach (var p in UnassignedPins)
                yield return $"floating pin: {p} is on no net and is not marked no-connect";
            foreach (var p in MultiplyAssignedPins)
                yield return $"over-assigned pin: {p}";
            foreach (var n in FloatingNets)
                yield return $"floating net: {n}";
            foreach (var n in EmptyNets)
                yield return $"empty net: {n}";
        }
    }

    /// <summary>A human-readable report — <c>"Schematic OK"</c>, or the problems one per
    /// line.</summary>
    public override string ToString() =>
        Ok ? $"Schematic OK ({TotalPins} pins, all covered)"
           : string.Join(Environment.NewLine, Messages);
}

/// <summary>The connectivity checks behind <see cref="Schematic.Check"/>.</summary>
internal static class SchematicChecker
{
    internal static SchematicCheckResult Check(Schematic schematic)
    {
        // Which nets each terminal appears on (across ALL kinds), and the ordered set of
        // every terminal of every component. The net pin lists only ever reference real
        // component terminals (the fluent API and the loader both validate that), so the map
        // is a subset of the component terminals and the counting identity is well posed.
        var netsOfPin = new Dictionary<PinRef, List<Net>>();
        foreach (var net in schematic.Nets)
            foreach (var pin in net.Pins)
                (netsOfPin.TryGetValue(pin, out var list) ? list : netsOfPin[pin] = []).Add(net);

        var unassigned = new List<string>();
        var multiply = new List<string>();
        int total = 0;
        int coveredOnce = 0;

        foreach (var component in schematic.Components)
        {
            foreach (var pin in component.AllPins)
            {
                total++;
                if (!netsOfPin.TryGetValue(pin, out var nets) || nets.Count == 0)
                {
                    unassigned.Add(pin.ToString());
                }
                else if (nets.Count > 1)
                {
                    multiply.Add($"{pin} on nets: {string.Join(", ", nets.Select(n => n.Name))}");
                }
                else
                {
                    coveredOnce++;
                }
            }
        }

        var floating = new List<string>();
        var empty = new List<string>();
        foreach (var net in schematic.Nets)
        {
            if (net.Pins.Count == 0)
                empty.Add(net.Name);
            else if (net.Kind == NetKind.Signal && net.Pins.Count < 2)
                floating.Add($"{net.Name} (1 pin: {net.Pins[0]})");
        }

        return new SchematicCheckResult(unassigned, multiply, floating, empty, total, coveredOnce);
    }
}
