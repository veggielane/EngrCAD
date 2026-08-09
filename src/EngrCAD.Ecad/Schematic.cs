namespace EngrCAD.Ecad;

/// <summary>
/// A code-defined schematic: <see cref="Component"/>s declared from
/// <see cref="PartDefinition"/>s and their terminals connected into <see cref="Net"/>s.
///
/// <para><b>The object graph IS the netlist.</b> There is no separate netlist to keep in
/// step with the drawing and no capture/round-trip: the components and nets a schematic
/// holds ARE the connectivity, and every derived view — a <see cref="Netlist"/> index, a
/// future footprint placement, a 3D body — reads this one graph. That is the whole
/// discipline (the "one declaration produces both" rule the campaign turns on): a check or
/// a layout that disagrees with the schematic is a bug in a derivation, not a difference
/// between two hand-kept sources.</para>
///
/// <para><b>Declaring one.</b> The API is fluent and code-first, like <c>Sketch</c> and
/// <c>Scene</c>:</para>
/// <code>
/// var sch = new Schematic("LED indicator");
/// var r  = sch.Add("R1", resistor, value: "330");
/// var d  = sch.Add("D1", led);
/// var bt = sch.Add("BT1", battery);
/// sch.Connect("VCC",   bt.Pin("+"), r.Pin("1"));
/// sch.Connect("LED_A", r.Pin("2"),  d.Pin("A"));
/// sch.Connect("GND",   d.Pin("K"),  bt.Pin("-"));
/// var report = sch.Check();   // combinatorial, exact
/// </code>
/// </summary>
public sealed partial class Schematic
{
    private readonly List<Component> _components = [];
    private readonly Dictionary<string, Component> _byRef = [];
    private readonly List<Net> _nets = [];
    private readonly Dictionary<string, Net> _netsByName = [];   // ALL nets, by name (globally unique)
    private int _noConnectCounter;

    /// <summary>Creates an empty schematic.</summary>
    /// <param name="name">A human name for the sheet (may be empty).</param>
    public Schematic(string name = "") => Name = name ?? "";

    /// <summary>The schematic's name (may be empty).</summary>
    public string Name { get; }

    /// <summary>The placed components, in declaration order.</summary>
    public IReadOnlyList<Component> Components => _components;

    /// <summary>Every net — signal, stub and no-connect — in declaration order.</summary>
    public IReadOnlyList<Net> Nets => _nets;

    // ---- declaring components ------------------------------------------------

    /// <summary>Places a component.</summary>
    /// <param name="referenceDesignator">The reference designator (<c>R1</c>, <c>U3</c>),
    /// unique within this schematic.</param>
    /// <param name="definition">The part type it instances.</param>
    /// <param name="value">An optional instance value (<c>"10k"</c>, <c>"100nF"</c>).</param>
    /// <returns>The placed component (call <c>.Pin("1")</c> on it to wire it).</returns>
    /// <exception cref="ArgumentException">The reference designator is empty or already
    /// used.</exception>
    public Component Add(string referenceDesignator, PartDefinition definition, string value = "")
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrEmpty(referenceDesignator))
            throw new ArgumentException("A component needs a reference designator.",
                nameof(referenceDesignator));
        if (_byRef.ContainsKey(referenceDesignator))
            throw new ArgumentException(
                $"Reference designator '{referenceDesignator}' is already used in this schematic.",
                nameof(referenceDesignator));

        var component = new Component(referenceDesignator, definition, value ?? "");
        _components.Add(component);
        _byRef[referenceDesignator] = component;
        return component;
    }

    /// <summary>The component with this reference designator, or null.</summary>
    public Component? Find(string referenceDesignator) =>
        _byRef.GetValueOrDefault(referenceDesignator);

    // ---- declaring nets ------------------------------------------------------

    /// <summary>Wires terminals into a signal net, creating it or extending an existing net
    /// of the same name (so a rail can be declared incrementally: several
    /// <c>Connect("VCC", …)</c> calls build one <c>VCC</c> net).</summary>
    /// <param name="name">The net name.</param>
    /// <param name="pins">The terminals to add.</param>
    /// <returns>The signal net.</returns>
    /// <exception cref="ArgumentException">The name is empty, a terminal belongs to a
    /// component not in this schematic, or a net of that name already exists with a different
    /// <see cref="NetKind"/>.</exception>
    public Net Connect(string name, params PinRef[] pins) => Connect(name, (IEnumerable<PinRef>)pins);

    /// <summary>Wires terminals into a signal net (see
    /// <see cref="Connect(string, PinRef[])"/>).</summary>
    public Net Connect(string name, IEnumerable<PinRef> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("A net needs a name.", nameof(name));

        if (_netsByName.TryGetValue(name, out var existing))
        {
            if (existing.Kind != NetKind.Signal)
                throw new ArgumentException(
                    $"Net '{name}' already exists as a {existing.Kind} net; a signal net cannot "
                    + "share its name.", nameof(name));
        }
        else
        {
            existing = new Net(name, NetKind.Signal);
            _nets.Add(existing);
            _netsByName[name] = existing;
        }

        foreach (var pin in pins)
        {
            RequireLocal(pin);
            existing.Add(pin);
        }
        return existing;
    }

    /// <summary>Declares a deliberate single-terminal net (a test point, a single-ended
    /// terminal) — recorded as <see cref="NetKind.Stub"/> and therefore exempt from the
    /// floating-net check.</summary>
    /// <param name="name">The net name.</param>
    /// <param name="pin">The single terminal.</param>
    /// <returns>The stub net.</returns>
    /// <exception cref="ArgumentException">The name is empty or already used, or the terminal
    /// belongs to another schematic.</exception>
    public Net Stub(string name, PinRef pin)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("A stub net needs a name.", nameof(name));
        if (_netsByName.ContainsKey(name))
            throw new ArgumentException($"Net '{name}' already exists.", nameof(name));
        RequireLocal(pin);

        var net = new Net(name, NetKind.Stub);
        net.Add(pin);
        _nets.Add(net);
        _netsByName[name] = net;
        return net;
    }

    /// <summary>Marks terminals as deliberately unconnected — an explicit, first-class state
    /// (never a null net). The terminals are recorded in one auto-named
    /// <see cref="NetKind.NoConnect"/> net; a no-connect terminal that is ALSO on a signal
    /// net is a violation the check names.</summary>
    /// <param name="pins">The terminals to leave unconnected.</param>
    /// <returns>The no-connect net.</returns>
    /// <exception cref="ArgumentException">No terminals were given, or one belongs to another
    /// schematic.</exception>
    public Net NoConnect(params PinRef[] pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Length == 0)
            throw new ArgumentException("NoConnect needs at least one terminal.", nameof(pins));

        // Auto-name, skipping any name a user already took (net names are globally unique).
        string name;
        do { name = $"NC{++_noConnectCounter}"; } while (_netsByName.ContainsKey(name));

        var net = new Net(name, NetKind.NoConnect);
        foreach (var pin in pins)
        {
            RequireLocal(pin);
            net.Add(pin);
        }
        _nets.Add(net);
        _netsByName[name] = net;
        return net;
    }

    private void RequireLocal(PinRef pin)
    {
        if (pin.Component is null
            || !_byRef.TryGetValue(pin.ReferenceDesignator, out var owner)
            || !ReferenceEquals(owner, pin.Component))
            throw new ArgumentException(
                $"Terminal {pin} refers to a component that is not in this schematic. Add it "
                + "with Add(...) first, and wire the component Add returned.", nameof(pin));
    }

    // ---- verification and views ----------------------------------------------

    /// <summary>Runs the combinatorial connectivity checks — the DRC of connectivity. See
    /// <see cref="SchematicCheckResult"/> for what each list means and the counting identity
    /// it reports.</summary>
    public SchematicCheckResult Check() => SchematicChecker.Check(this);

    /// <summary>A derived, read-only index of this schematic's connectivity (pin → net and
    /// net → pins). Computed fresh each call — there is no cached netlist state to drift from
    /// the graph.</summary>
    public Netlist ToNetlist() => new(this);

    // ---- loader seams (used by SchematicReader; see SchematicFile.cs) ---------

    /// <summary>Reconstructs a net verbatim from a saved record (bypasses the create-or-extend
    /// logic so a loaded schematic re-saves byte-for-byte).</summary>
    internal Net RegisterLoadedNet(string name, NetKind kind, IEnumerable<PinRef> pins)
    {
        var net = new Net(name, kind);
        foreach (var pin in pins)
        {
            RequireLocal(pin);
            net.Add(pin);
        }
        if (!_netsByName.TryAdd(name, net))
            throw new FormatException($"Duplicate net name '{name}' in the saved schematic.");
        if (kind == NetKind.NoConnect && name.Length > 2 && name[0] == 'N' && name[1] == 'C'
            && int.TryParse(name.AsSpan(2), out int index) && index > _noConnectCounter)
        {
            // Keep the auto-name counter ahead of any loaded NoConnect net, so a schematic
            // edited after loading does not collide with a name the file already used.
            _noConnectCounter = index;
        }
        _nets.Add(net);
        return net;
    }
}
