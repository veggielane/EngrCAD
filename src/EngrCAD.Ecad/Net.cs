namespace EngrCAD.Ecad;

/// <summary>
/// What a <see cref="Net"/> declares. Every terminal in a schematic is covered by exactly
/// one net, and the KIND says how: it is either wired to something (<see cref="Signal"/>),
/// a deliberate single-terminal net (<see cref="Stub"/>), or deliberately not connected at
/// all (<see cref="NoConnect"/>). The distinction is what makes the floating-net check
/// meaningful — a lone <see cref="Signal"/> pin is a mistake, a <see cref="Stub"/> or a
/// <see cref="NoConnect"/> pin is not.
/// </summary>
public enum NetKind
{
    /// <summary>An ordinary connection: two or more terminals wired together. A signal net
    /// with fewer than two pins is FLOATING and is reported by <see cref="Schematic.Check"/>.</summary>
    Signal,

    /// <summary>A deliberate single-terminal net (a test point, a single-ended terminal) —
    /// exempt from the floating-net check because being alone is the point. The "explicit,
    /// different thing" a lone signal net is not.</summary>
    Stub,

    /// <summary>Terminals that are deliberately left unconnected — the explicit, first-class
    /// state a null net would be. A no-connect terminal must be on no other net.</summary>
    NoConnect,
}

/// <summary>
/// A named connection of terminals across components — the edges of the netlist graph.
/// <para>A net owns an ordered list of the <see cref="PinRef"/>s it connects; the
/// <see cref="Schematic"/> creates and extends nets through its fluent methods, and the
/// <see cref="Kind"/> decides how <see cref="Schematic.Check"/> reads it.</para>
/// </summary>
public sealed class Net
{
    private readonly List<PinRef> _pins;

    internal Net(string name, NetKind kind, IEnumerable<PinRef>? pins = null)
    {
        Name = name;
        Kind = kind;
        _pins = pins is null ? [] : [.. pins];
    }

    /// <summary>The net's name (<c>"VCC"</c>, <c>"GND"</c>, an auto <c>"NC1"</c>).</summary>
    public string Name { get; }

    /// <summary>Whether this net wires terminals, is a deliberate stub, or marks
    /// no-connects.</summary>
    public NetKind Kind { get; }

    /// <summary>The connected terminals, in the order they were added.</summary>
    public IReadOnlyList<PinRef> Pins => _pins;

    /// <summary>Adds a terminal, unless it is already on this net (a pin is on a given net at
    /// most once; being on TWO nets is what <see cref="Schematic.Check"/> catches).</summary>
    /// <returns>True when the terminal was added, false when it was already present.</returns>
    internal bool Add(PinRef pin)
    {
        if (_pins.Contains(pin))
            return false;
        _pins.Add(pin);
        return true;
    }

    /// <summary>The net's kind, name and terminals.</summary>
    public override string ToString() =>
        $"{Name} [{Kind}]: {string.Join(", ", _pins)}";
}
