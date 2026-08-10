using System.Text;

namespace EngrCAD.Ecad;

/// <summary>
/// A derived, read-only index of a <see cref="Schematic"/>'s connectivity — the two views a
/// netlist is usually asked for: net → its terminals, and terminal → its net. It is a
/// PROJECTION of the schematic graph (built fresh by <see cref="Schematic.ToNetlist"/>),
/// never a second editable copy, so it cannot drift: change the schematic and ask for the
/// netlist again.
/// </summary>
public sealed class Netlist
{
    private readonly Dictionary<PinRef, Net> _netOfPin = new();

    internal Netlist(Schematic schematic)
    {
        Schematic = schematic;
        Nets = schematic.Nets;
        // A pin on more than one net is a Check() violation, not a Netlist concern; this view
        // records the FIRST net a pin appears on so it stays usable on a malformed schematic.
        foreach (var net in schematic.Nets)
            foreach (var pin in net.Pins)
                _netOfPin.TryAdd(pin, net);
    }

    /// <summary>The schematic this indexes.</summary>
    public Schematic Schematic { get; }

    /// <summary>Every net — signal, stub and no-connect — in declaration order.</summary>
    public IReadOnlyList<Net> Nets { get; }

    /// <summary>The nets that actually wire terminals together (signal and stub), in
    /// declaration order.</summary>
    public IEnumerable<Net> SignalNets =>
        Nets.Where(n => n.Kind is NetKind.Signal or NetKind.Stub);

    /// <summary>The no-connect nets, in declaration order.</summary>
    public IEnumerable<Net> NoConnects => Nets.Where(n => n.Kind == NetKind.NoConnect);

    /// <summary>The net a terminal is on, or null when it is on none.</summary>
    public Net? NetOf(PinRef pin) => _netOfPin.GetValueOrDefault(pin);

    /// <summary>The terminals a net connects (shorthand for <c>net.Pins</c>).</summary>
    public IReadOnlyList<PinRef> PinsOf(Net net) => net.Pins;

    /// <summary>
    /// A human-readable netlist listing — the components then the nets, textually. Reflects
    /// the graph's declaration order (it is a view of the graph, not a re-sorted report).
    /// This is the textual view; a drawn SHEET (placed symbols, wires and labels to SVG/DXF/PDF)
    /// is <see cref="SchematicSheet"/>, a derived view over the same graph.
    /// </summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append("Schematic");
        if (Schematic.Name.Length > 0)
            text.Append(": ").Append(Schematic.Name);
        text.AppendLine();

        text.Append("Components (").Append(Schematic.Components.Count).AppendLine("):");
        foreach (var c in Schematic.Components)
        {
            text.Append("  ").Append(c.ReferenceDesignator.PadRight(6)).Append(' ')
                .Append(c.Definition.Name.PadRight(16));
            if (c.Value.Length > 0)
                text.Append("  = ").Append(c.Value);
            text.AppendLine();
        }

        var wired = SignalNets.ToList();
        text.Append("Nets (").Append(wired.Count).AppendLine("):");
        foreach (var net in wired)
        {
            text.Append("  ").Append(net.Name.PadRight(12));
            if (net.Kind == NetKind.Stub)
                text.Append("(stub) ");
            text.AppendLine(string.Join(", ", net.Pins));
        }

        var noConnects = NoConnects.SelectMany(n => n.Pins).ToList();
        if (noConnects.Count > 0)
        {
            text.Append("No-connect (").Append(noConnects.Count).AppendLine("):");
            text.Append("  ").AppendLine(string.Join(", ", noConnects));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The same listing as <see cref="ToText"/>.</summary>
    public override string ToString() => ToText();
}
