using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// A reusable part TYPE: a name/designation, an ordered list of <see cref="Pin"/>s, an
/// optional <see cref="Footprint"/> (the 2D pad layout the board stage consumes), and an
/// optional 3D <see cref="Body"/> hook. A resistor type, an op-amp, a connector — declared
/// once, instanced as many <see cref="Component"/>s.
///
/// <para><b>The definition is the source (the catalogue rule).</b> A <see cref="Component"/>
/// is meaningless without its definition — the definition is where its pins come from — so
/// a component always references one, and the definition travels WITH a saved schematic
/// (embedded and interned by identity, exactly as the document model interns parts). What
/// does NOT travel is the <see cref="Body"/>: it is code (a lambda over the modelling API),
/// so it is re-attached on load from an optional part library keyed on
/// <see cref="Name"/> — the <c>ResolveOpaqueFeature</c> pattern — and a definition with no
/// re-attached body is simply data-only, which is all connectivity needs.</para>
/// </summary>
public sealed class PartDefinition
{
    private readonly Dictionary<string, Pin> _byNumber;

    /// <summary>The type/designation name (e.g. <c>"R0805"</c>, <c>"ATmega328P"</c>).</summary>
    public string Name { get; }

    /// <summary>The reference-designator prefix a placed instance is named with
    /// (<c>"R"</c>, <c>"U"</c>, <c>"C"</c>, <c>"D"</c>, …).</summary>
    public string ReferencePrefix { get; }

    /// <summary>The pins, in declaration order.</summary>
    public IReadOnlyList<Pin> Pins { get; }

    /// <summary>The 2D pad layout, or null when the definition carries no footprint yet
    /// (connectivity does not need one).</summary>
    public Footprint? Footprint { get; }

    /// <summary>The 2D schematic <see cref="Ecad.Symbol"/> — graphic primitives plus a
    /// <see cref="SymbolPin"/> per terminal (where a wire lands) — or null when the definition
    /// carries no drawn symbol. OPTIONAL by design: stage 1 is connectivity, and a symbol is
    /// what a drawn schematic SHEET consumes. When a symbol AND a footprint are both present,
    /// their pins and pads share the pin NUMBER identity a <see cref="PinIdentity"/> check
    /// verifies (symbol pin "1" == pad "1" == netlist pin "1").</summary>
    public Symbol? Symbol { get; }

    /// <summary>
    /// The 3D body builder, or null. OPTIONAL by design — stage 1 is connectivity, not
    /// placement. It is a lambda over the modelling API (e.g.
    /// <c>() => Shape.Box(2, 1.25, 0.5)</c>, or <c>() => hardwareComponent.Body</c>), so it
    /// is NOT serialized; a saved schematic re-attaches it from a
    /// <see cref="PartLibrary"/> on load if one is supplied.
    /// </summary>
    public Func<Shape>? Body { get; }

    /// <summary>Builds a part definition.</summary>
    /// <param name="name">The type name.</param>
    /// <param name="referencePrefix">The reference-designator prefix.</param>
    /// <param name="pins">The pins; numbers must be non-empty and unique.</param>
    /// <param name="footprint">The optional pad layout.</param>
    /// <param name="body">The optional 3D body builder (not serialized).</param>
    /// <param name="symbol">The optional 2D schematic symbol. Added LAST so every existing
    /// positional construction is unchanged.</param>
    /// <exception cref="ArgumentException">A name/prefix is empty, or a pin number is empty
    /// or repeated.</exception>
    public PartDefinition(
        string name,
        string referencePrefix,
        IEnumerable<Pin> pins,
        Footprint? footprint = null,
        Func<Shape>? body = null,
        Symbol? symbol = null)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("A part definition needs a name.", nameof(name));
        if (string.IsNullOrEmpty(referencePrefix))
            throw new ArgumentException(
                $"Part definition '{name}' needs a reference-designator prefix (e.g. \"R\", \"U\").",
                nameof(referencePrefix));

        Name = name;
        ReferencePrefix = referencePrefix;
        Pins = [.. pins];
        Footprint = footprint;
        Body = body;
        Symbol = symbol;

        _byNumber = new Dictionary<string, Pin>(Pins.Count);
        foreach (var pin in Pins)
        {
            if (string.IsNullOrEmpty(pin.Number))
                throw new ArgumentException(
                    $"Part definition '{name}' has a pin with an empty number.", nameof(pins));
            if (!_byNumber.TryAdd(pin.Number, pin))
                throw new ArgumentException(
                    $"Part definition '{name}' has two pins numbered '{pin.Number}'.", nameof(pins));
        }
    }

    /// <summary>Whether a pin with this number exists.</summary>
    public bool HasPin(string number) => _byNumber.ContainsKey(number);

    /// <summary>The pin with this number.</summary>
    /// <exception cref="ArgumentException">No pin has that number (naming the definition and
    /// the numbers it does carry).</exception>
    public Pin PinNumbered(string number)
    {
        if (_byNumber.TryGetValue(number, out var pin))
            return pin;
        throw new ArgumentException(
            $"Part definition '{Name}' has no pin '{number}'. Its pins are: "
            + string.Join(", ", Pins.Select(p => p.Number)) + ".",
            nameof(number));
    }

    /// <summary>The definition's name and pin count.</summary>
    public override string ToString() => $"{Name} ({Pins.Count} pins)";
}
