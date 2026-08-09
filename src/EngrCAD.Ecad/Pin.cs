namespace EngrCAD.Ecad;

/// <summary>
/// The electrical character of a <see cref="Pin"/> — enough for a floating-net and a
/// no-connect check (and, later, a signal-direction ERC), deliberately NOT a SPICE model.
/// <para><see cref="Unspecified"/> is the <c>0</c> value so the struct default carries no
/// meaning it was not given; a two-terminal passive part (a resistor, a capacitor) states
/// <see cref="Passive"/> explicitly.</para>
/// </summary>
public enum PinType
{
    /// <summary>Not stated. The struct default — an honest "unknown", not a silent Passive.</summary>
    Unspecified,

    /// <summary>No direction: a resistor, a capacitor, a connector terminal.</summary>
    Passive,

    /// <summary>A logic input.</summary>
    Input,

    /// <summary>A logic output.</summary>
    Output,

    /// <summary>A bidirectional (I/O) pin.</summary>
    Bidirectional,

    /// <summary>A power rail (VCC/VDD), whether a source or a sink.</summary>
    Power,

    /// <summary>A ground reference.</summary>
    Ground,

    /// <summary>A three-state output (may be high-impedance).</summary>
    TriState,

    /// <summary>An open-collector / open-drain output.</summary>
    OpenCollector,
}

/// <summary>
/// One terminal of a <see cref="PartDefinition"/>: a <see cref="Number"/> (the pad number,
/// e.g. <c>"1"</c>, <c>"A5"</c> — the pin's identity within its part), an optional
/// functional <see cref="Name"/> (<c>"VCC"</c>, <c>"GND"</c>, <c>"OUT"</c>), and an
/// electrical <see cref="Type"/>.
/// <para>A <see cref="Pin"/> is a value: it describes a terminal of a part TYPE, not a
/// terminal on a placed part. A terminal on a placed part is a <see cref="PinRef"/>
/// (component + pin number), which is what a <see cref="Net"/> connects.</para>
/// </summary>
/// <param name="Number">The pad number, unique within the owning <see cref="PartDefinition"/>.</param>
/// <param name="Name">The functional name, or the empty string.</param>
/// <param name="Type">The electrical character.</param>
public readonly record struct Pin(string Number, string Name, PinType Type)
{
    /// <summary>A pin with a number and a type but no functional name.</summary>
    public Pin(string number, PinType type = PinType.Passive) : this(number, "", type) { }

    /// <summary>A short label: <c>"1 (VCC)"</c>, or just <c>"1"</c> when it has no name.</summary>
    public override string ToString() =>
        Name.Length == 0 ? Number : $"{Number} ({Name})";
}
