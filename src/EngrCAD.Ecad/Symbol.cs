using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// Where a <see cref="SymbolPin"/> points, measured FROM its anchor (the point where a wire
/// lands) INTO the symbol body — the same convention KiCad's pin angle uses (0° right, 90° up,
/// 180° left, 270° down). So a pin whose anchor is on the LEFT of the body has direction
/// <see cref="Right"/> (it extends rightward into the body), and a schematic sheet draws the
/// wire arriving at the anchor from the OPPOSITE side.
/// </summary>
public enum SymbolPinDirection
{
    /// <summary>Points +X (KiCad angle 0°): anchor on the left, body to the right.</summary>
    Right,

    /// <summary>Points +Y (KiCad angle 90°): anchor below, body above.</summary>
    Up,

    /// <summary>Points −X (KiCad angle 180°): anchor on the right, body to the left.</summary>
    Left,

    /// <summary>Points −Y (KiCad angle 270°): anchor above, body below.</summary>
    Down,
}

/// <summary>Direction helpers for <see cref="SymbolPinDirection"/>.</summary>
public static class SymbolPinDirectionExtensions
{
    /// <summary>The unit vector this direction points (from anchor into the body).</summary>
    public static Vector2d ToVector(this SymbolPinDirection direction) => direction switch
    {
        SymbolPinDirection.Right => new Vector2d(1, 0),
        SymbolPinDirection.Up => new Vector2d(0, 1),
        SymbolPinDirection.Left => new Vector2d(-1, 0),
        SymbolPinDirection.Down => new Vector2d(0, -1),
        _ => new Vector2d(1, 0),
    };

    /// <summary>The direction nearest a KiCad-style angle in degrees (snapped to the four
    /// orthogonal cases; a pin is always orthogonal in KiCad).</summary>
    public static SymbolPinDirection FromDegrees(double degrees)
    {
        // Normalize into [0, 360) then snap to the nearest of 0/90/180/270.
        double a = degrees % 360.0;
        if (a < 0) a += 360.0;
        int q = (int)Math.Round(a / 90.0) % 4;
        return q switch
        {
            0 => SymbolPinDirection.Right,
            1 => SymbolPinDirection.Up,
            2 => SymbolPinDirection.Left,
            _ => SymbolPinDirection.Down,
        };
    }
}

/// <summary>
/// One pin of a schematic <see cref="Symbol"/> — the drawn counterpart of a
/// <see cref="Pin"/>. It carries the pin NUMBER (its identity, shared with the footprint pad
/// and the netlist pin), an optional functional <see cref="Name"/>, the <see cref="Anchor"/>
/// where a wire lands, the <see cref="Direction"/> the pin points from that anchor into the
/// body, a <see cref="Length"/>, and the electrical <see cref="Type"/> (the SAME
/// <see cref="PinType"/> the netlist uses).
/// <para>The <see cref="Anchor"/> is the load-bearing property for a schematic sheet: it is the
/// electrical connection point, so a wire routed to a symbol lands exactly there.</para>
/// </summary>
/// <param name="Number">The pin number — its identity within the part (shared with the pad and
/// the netlist pin).</param>
/// <param name="Name">The functional name (<c>"VCC"</c>, <c>"OUT"</c>), or the empty string.</param>
/// <param name="Anchor">The connection point, in the symbol's own 2D frame (mm), where a wire
/// attaches.</param>
/// <param name="Direction">The direction the pin points from its anchor into the body.</param>
/// <param name="Length">The drawn pin length (mm).</param>
/// <param name="Type">The electrical character.</param>
public readonly record struct SymbolPin(
    string Number, string Name, Vector2d Anchor, SymbolPinDirection Direction, double Length,
    PinType Type)
{
    /// <summary>The inner endpoint, where the pin line meets the body:
    /// <c>Anchor + Length·Direction</c>.</summary>
    public Vector2d Inner => Anchor + Length * Direction.ToVector();

    /// <summary>A short label: <c>"1 (VCC)"</c>, or just <c>"1"</c> when it has no name.</summary>
    public override string ToString() => Name.Length == 0 ? Number : $"{Number} ({Name})";
}

/// <summary>A graphic primitive of a <see cref="Symbol"/>, in the symbol's own 2D frame (mm).
/// The concrete kinds are <see cref="SymbolPolyline"/> (a line is a two-point polyline),
/// <see cref="SymbolRectangle"/>, <see cref="SymbolCircle"/>, <see cref="SymbolArc"/> and
/// <see cref="SymbolText"/> — the common subset a schematic symbol draws.</summary>
public abstract record SymbolGraphic;

/// <summary>A connected run of straight segments (a KiCad <c>polyline</c>; a single-segment
/// polyline is a line).</summary>
/// <param name="Points">The polyline vertices, in order (at least two).</param>
public sealed record SymbolPolyline(IReadOnlyList<Vector2d> Points) : SymbolGraphic;

/// <summary>An axis-aligned rectangle (a KiCad <c>rectangle</c>), stored by its two extreme
/// corners.</summary>
/// <param name="Min">The lower-left corner (min x, min y).</param>
/// <param name="Max">The upper-right corner (max x, max y).</param>
public sealed record SymbolRectangle(Vector2d Min, Vector2d Max) : SymbolGraphic;

/// <summary>A circle (a KiCad <c>circle</c>).</summary>
/// <param name="Center">The centre.</param>
/// <param name="Radius">The radius (mm).</param>
public sealed record SymbolCircle(Vector2d Center, double Radius) : SymbolGraphic;

/// <summary>A circular arc through three points (KiCad's <c>arc</c> start/mid/end form).</summary>
/// <param name="Start">The start point.</param>
/// <param name="Mid">A point on the arc between start and end.</param>
/// <param name="End">The end point.</param>
public sealed record SymbolArc(Vector2d Start, Vector2d Mid, Vector2d End) : SymbolGraphic;

/// <summary>A text label drawn in the symbol (a KiCad graphic <c>text</c>).</summary>
/// <param name="Value">The text.</param>
/// <param name="At">The text position.</param>
/// <param name="Size">The text height (mm).</param>
public sealed record SymbolText(string Value, Vector2d At, double Size) : SymbolGraphic;

/// <summary>
/// The 2D SCHEMATIC symbol of a <see cref="PartDefinition"/> — the drawn shape a schematic
/// sheet places, wires to, and labels. It is graphic primitives (<see cref="Graphics"/>) plus
/// one <see cref="SymbolPin"/> per terminal (<see cref="Pins"/>), all in the symbol's own 2D
/// frame.
/// <para>The pin NUMBERS are the identity that ties the symbol to its footprint and its
/// netlist (symbol pin "1" == pad "1" == netlist pin "1"); a <see cref="PinIdentity"/> check
/// verifies the three representations agree. The symbol is the representation the filed
/// "schematic drawing output" needs — a pin's <see cref="SymbolPin.Anchor"/> is where a wire
/// lands.</para>
/// </summary>
public sealed class Symbol
{
    private readonly Dictionary<string, SymbolPin> _byNumber;

    /// <summary>The symbol's name (usually the part type name, e.g. <c>"R_0805"</c>).</summary>
    public string Name { get; }

    /// <summary>The graphic primitives, in draw order.</summary>
    public IReadOnlyList<SymbolGraphic> Graphics { get; }

    /// <summary>The pins, in declaration order.</summary>
    public IReadOnlyList<SymbolPin> Pins { get; }

    /// <summary>The De Morgan / ALTERNATE body of this unit, or <c>null</c> when it has none. A KiCad
    /// <c>unit_style</c> 2 sub-symbol (a NAND drawn as an OR with bubbles, etc.) is a different DRAWING of
    /// the same unit — same pin NUMBERS — so it is carried as its own <see cref="Symbol"/> here rather
    /// than replacing the default body. The alternate never nests (its own <see cref="Alternate"/> is
    /// null). A caller decides per instance which body to draw; the default is this (the primary) body.</summary>
    public Symbol? Alternate { get; }

    /// <summary>Builds a symbol from a name, its pins and its graphics, optionally with a De Morgan
    /// alternate body.</summary>
    /// <param name="name">The symbol name.</param>
    /// <param name="pins">The pins; numbers must be non-empty and unique.</param>
    /// <param name="graphics">The graphic primitives (may be empty).</param>
    /// <param name="alternate">The De Morgan / alternate body, or null. It must not itself carry an
    /// alternate (the style is a single toggle, not a chain).</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, a pin number is
    /// empty or repeated, or <paramref name="alternate"/> itself carries an alternate.</exception>
    public Symbol(
        string name, IEnumerable<SymbolPin> pins, IEnumerable<SymbolGraphic>? graphics = null,
        Symbol? alternate = null)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("A symbol needs a name.", nameof(name));
        if (alternate?.Alternate is not null)
            throw new ArgumentException(
                $"Symbol '{name}': an alternate body must not itself carry an alternate.", nameof(alternate));
        Name = name;
        Alternate = alternate;
        Graphics = graphics is null ? [] : [.. graphics];
        Pins = [.. pins];

        _byNumber = new Dictionary<string, SymbolPin>(Pins.Count);
        foreach (var pin in Pins)
        {
            if (string.IsNullOrEmpty(pin.Number))
                throw new ArgumentException(
                    $"Symbol '{name}' has a pin with an empty number.", nameof(pins));
            if (!_byNumber.TryAdd(pin.Number, pin))
                throw new ArgumentException(
                    $"Symbol '{name}' has two pins numbered '{pin.Number}'.", nameof(pins));
        }
    }

    /// <summary>The pin numbers this symbol draws.</summary>
    public IEnumerable<string> PinNumbers => Pins.Select(p => p.Number);

    /// <summary>Whether a pin with this number exists.</summary>
    public bool HasPin(string number) => _byNumber.ContainsKey(number);

    /// <summary>The pin with this number.</summary>
    /// <exception cref="ArgumentException">No pin has that number.</exception>
    public SymbolPin PinNumbered(string number)
    {
        if (_byNumber.TryGetValue(number, out var pin))
            return pin;
        throw new ArgumentException(
            $"Symbol '{Name}' has no pin '{number}'. Its pins are: "
            + string.Join(", ", Pins.Select(p => p.Number)) + ".", nameof(number));
    }

    /// <summary>The symbol's name and pin count.</summary>
    public override string ToString() => $"{Name} ({Pins.Count} pins)";
}
