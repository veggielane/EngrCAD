using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// A reusable part TYPE built from its three views — a <see cref="Symbol"/> (the drawn
/// schematic shape), a <see cref="Footprint"/> (the 2D pad layout the board stage consumes)
/// and a 3D <see cref="Model"/> — plus a name/designation and an ordered list of
/// <see cref="Pin"/>s. A resistor type, an op-amp, a connector — declared once, instanced as
/// many <see cref="Component"/>s. Every view is OPTIONAL: connectivity needs only the pins,
/// and the three views (when present) share one pin-NUMBER identity a <see cref="PinIdentity"/>
/// check verifies (symbol pin "1" == pad "1" == netlist pin "1").
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
    /// verifies (symbol pin "1" == pad "1" == netlist pin "1").
    /// <para>For a MULTI-UNIT part (a dual op-amp drawn as several schematic symbols under one
    /// package and reference designator) this is the FIRST unit's symbol — a representative;
    /// <see cref="Units"/> carries all of them. For the ordinary single-unit part it is the sole
    /// unit's symbol.</para></summary>
    public Symbol? Symbol { get; }

    /// <summary>
    /// The part's schematic symbols, ONE PER UNIT. A single-unit part has exactly one (equal to
    /// <see cref="Symbol"/>); a symbol-less definition has none; a MULTI-UNIT part (e.g. a dual
    /// op-amp: amp A, amp B, a power unit) has several, each carrying only that unit's own pins at
    /// that unit's anchors, so a schematic can place each unit separately while the part stays ONE
    /// component with ONE set of pins (their UNION, which is what <see cref="Pins"/> holds).
    /// <para>Units are a SCHEMATIC-drawing/placement concern: connectivity, the board footprint and
    /// the BOM all read the whole pin set, so a multi-unit component is one component with all pads
    /// and all pins. The pin NUMBER identity spans the units (<see cref="PinIdentity"/> takes the
    /// union of every unit's symbol pins).</para>
    /// </summary>
    public IReadOnlyList<Symbol> Units { get; }

    /// <summary>Whether this definition carries more than one schematic unit (a multi-unit part).</summary>
    public bool IsMultiUnit => Units.Count > 1;

    /// <summary>
    /// The legacy 3D body builder, or null. OPTIONAL by design — stage 1 is connectivity, not
    /// placement. It is a lambda over the modelling API (e.g.
    /// <c>() => Shape.Box(2, 1.25, 0.5)</c>, or <c>() => hardwareComponent.Body</c>), so it
    /// is NOT serialized; a saved schematic re-attaches it from a
    /// <see cref="PartLibrary"/> on load if one is supplied.
    /// <para>This is the spelling of a code <see cref="Model"/> with the identity placement:
    /// when a definition carries no <see cref="Model"/>, the seating treats <see cref="Body"/> as
    /// exactly that. A definition may carry either or both; <see cref="Model"/> takes precedence.</para>
    /// </summary>
    public Func<Shape>? Body { get; }

    /// <summary>
    /// The 3D <see cref="ComponentModel3D"/> — a FIRST-CLASS peer of <see cref="Symbol"/> and
    /// <see cref="Footprint"/>, or null. Unlike the legacy <see cref="Body"/> (which is always
    /// code with the identity placement), a model unifies a body source — a FILE reference
    /// (<c>.stl</c>/<c>.obj</c>/<c>.off</c>/<c>.step</c>, which travels as DATA and loads on demand)
    /// or a <c>Func&lt;Shape&gt;</c> (code, opaque) — with a <see cref="ModelPlacement"/> relative to
    /// the footprint origin. When present it is what the board seats; a file-referenced model
    /// round-trips through the schematic/board file, while a code model (and the legacy
    /// <see cref="Body"/>) is re-attached from a <see cref="PartLibrary"/>.
    /// </summary>
    public ComponentModel3D? Model { get; }

    /// <summary>Builds a part definition.</summary>
    /// <param name="name">The type name.</param>
    /// <param name="referencePrefix">The reference-designator prefix.</param>
    /// <param name="pins">The pins; numbers must be non-empty and unique.</param>
    /// <param name="footprint">The optional pad layout.</param>
    /// <param name="body">The optional legacy 3D body builder (not serialized).</param>
    /// <param name="symbol">The optional 2D schematic symbol.</param>
    /// <param name="model">The optional 3D <see cref="ComponentModel3D"/> peer.</param>
    /// <param name="units">The optional MULTI-UNIT symbols — one <see cref="Ecad.Symbol"/> per unit
    /// (a dual op-amp's amp A, amp B, power unit). Added LAST so every existing positional
    /// construction is byte-for-byte unchanged; pass it INSTEAD of <paramref name="symbol"/> (not
    /// both). When it is null, the definition's <see cref="Units"/> is derived from
    /// <paramref name="symbol"/> (one unit, or none) so the single-unit case is unchanged.</param>
    /// <exception cref="ArgumentException">A name/prefix is empty, a pin number is empty
    /// or repeated, both <paramref name="symbol"/> and <paramref name="units"/> are given, or
    /// <paramref name="units"/> is an empty list.</exception>
    public PartDefinition(
        string name,
        string referencePrefix,
        IEnumerable<Pin> pins,
        Footprint? footprint = null,
        Func<Shape>? body = null,
        Symbol? symbol = null,
        ComponentModel3D? model = null,
        IReadOnlyList<Symbol>? units = null)
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
        Model = model;

        // The unit list is the source when given (a multi-unit part); otherwise the single symbol
        // derives one unit (or none), so a single-unit / symbol-less definition is byte-identical to
        // before. Symbol stays the first unit (or null) — the representative one-unit accessor.
        if (units is not null)
        {
            if (symbol is not null)
                throw new ArgumentException(
                    $"Part definition '{name}': pass either a single 'symbol' or a 'units' list, not "
                    + "both (a units list already carries the first unit as its symbol).", nameof(units));
            if (units.Count == 0)
                throw new ArgumentException(
                    $"Part definition '{name}': the 'units' list is empty. Pass symbol: null for a "
                    + "symbol-less definition, or the units the part draws.", nameof(units));
            Units = [.. units];
            Symbol = Units[0];
        }
        else
        {
            Symbol = symbol;
            Units = symbol is null ? [] : [symbol];
        }

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
