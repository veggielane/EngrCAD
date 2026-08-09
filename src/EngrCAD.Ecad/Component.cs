namespace EngrCAD.Ecad;

/// <summary>
/// A placed instance of a <see cref="PartDefinition"/> in a <see cref="Schematic"/>: a
/// reference designator (<c>R1</c>, <c>U3</c>), the definition it instances, and an
/// optional <see cref="Value"/> (<c>"10k"</c>, <c>"100nF"</c>).
/// <para>A component is created through <see cref="Schematic.Add(string, PartDefinition, string)"/>,
/// which owns the reference-designator uniqueness rule; its <see cref="Pin(string)"/>
/// method names one of its terminals as a <see cref="PinRef"/> — the thing a
/// <see cref="Net"/> connects.</para>
/// </summary>
public sealed class Component
{
    internal Component(string referenceDesignator, PartDefinition definition, string value)
    {
        ReferenceDesignator = referenceDesignator;
        Definition = definition;
        Value = value;
    }

    /// <summary>The reference designator, unique within the schematic (<c>R1</c>, <c>U3</c>).</summary>
    public string ReferenceDesignator { get; }

    /// <summary>The part type this instances.</summary>
    public PartDefinition Definition { get; }

    /// <summary>The instance value, or the empty string (<c>"10k"</c>, <c>"100nF"</c>,
    /// <c>"BC547"</c>).</summary>
    public string Value { get; }

    /// <summary>Names one of this component's terminals.</summary>
    /// <param name="number">The pin number, from the definition.</param>
    /// <exception cref="ArgumentException">The definition has no pin with that number.</exception>
    public PinRef Pin(string number)
    {
        if (!Definition.HasPin(number))
            throw new ArgumentException(
                $"Component {ReferenceDesignator} ({Definition.Name}) has no pin '{number}'. "
                + "Its pins are: " + string.Join(", ", Definition.Pins.Select(p => p.Number)) + ".",
                nameof(number));
        return new PinRef(this, number);
    }

    /// <summary>Names one of this component's terminals (indexer form of
    /// <see cref="Pin(string)"/>).</summary>
    public PinRef this[string number] => Pin(number);

    /// <summary>Every terminal of this component, in definition pin order.</summary>
    public IEnumerable<PinRef> AllPins =>
        Definition.Pins.Select(p => new PinRef(this, p.Number));

    /// <summary>The reference designator, definition and value.</summary>
    public override string ToString() =>
        Value.Length == 0
            ? $"{ReferenceDesignator} ({Definition.Name})"
            : $"{ReferenceDesignator} ({Definition.Name}, {Value})";
}

/// <summary>
/// A reference to one terminal of one placed <see cref="Component"/> — a
/// (component, pin number) pair — which is what a <see cref="Net"/> connects. It is a
/// REFERENCE, not a copy: two <see cref="PinRef"/>s are equal iff they name the same
/// component object and the same pin number, so a net holds pointers into the component
/// graph and the persistence layer must reproduce that (which it does, by resolving a
/// saved (refdes, number) back to the same component).
/// </summary>
/// <param name="Component">The placed component.</param>
/// <param name="Number">The pin number on that component's definition.</param>
public readonly record struct PinRef(Component Component, string Number)
{
    /// <summary>The owning component's reference designator.</summary>
    public string ReferenceDesignator => Component.ReferenceDesignator;

    /// <summary>The resolved pin (its name and electrical type).</summary>
    public Pin Pin => Component.Definition.PinNumbered(Number);

    /// <summary>The canonical <c>"R1.2"</c> spelling — reference designator, dot, pin
    /// number.</summary>
    public override string ToString() => $"{ReferenceDesignator}.{Number}";
}
