using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>The copper-pad geometry a <see cref="Pad"/> takes on the board.</summary>
public enum PadShape
{
    /// <summary>A circular pad (a plated through-hole, a via land).</summary>
    Round,

    /// <summary>A rectangular pad (an SMD terminal, pin 1 by convention).</summary>
    Rectangular,

    /// <summary>An oval / stadium pad.</summary>
    Oval,

    /// <summary>A rectangle with rounded corners (IPC's preferred SMD land).</summary>
    RoundedRectangle,
}

/// <summary>
/// How a <see cref="Pad"/> sits on the board's copper layers. <see cref="Smd"/> is the
/// <c>0</c> value so the struct default carries no meaning it was not given (the
/// <see cref="PinType.Unspecified"/> convention).
/// </summary>
public enum PadKind
{
    /// <summary>A surface-mount land: it drills nothing and lives on the copper of the side
    /// the component is placed on (top-side placement → top copper, bottom → bottom).</summary>
    Smd,

    /// <summary>A plated through-hole: it drills the board (a hole of
    /// <see cref="Pad.DrillDiameter"/>) and appears on EVERY copper layer, so the same hole
    /// serves the pad on both faces — which is why a bottom placement leaves its
    /// through-hole pads at the same world (x, y) as a top one.</summary>
    ThroughHole,
}

/// <summary>
/// One copper land of a <see cref="Footprint"/>: where the board layout stage places the
/// pad for a given <see cref="PartDefinition"/> pin. The coordinates are in the footprint's
/// own 2D frame (millimetres, origin at the part's placement point, z = 0 the seating
/// plane). A <see cref="Kind"/> of <see cref="PadKind.ThroughHole"/> carries a
/// <see cref="DrillDiameter"/>, from which the annular ring derives.
/// <para><b>Backward-compatible.</b> The stage-1 5-argument construction
/// <c>new Pad(number, center, width, height, shape)</c> still builds an SMD pad with no
/// drill, and persistence writes <see cref="Kind"/>/<see cref="DrillDiameter"/> only when
/// stated (write-only-when-stated), so a stage-1 footprint saves byte-identically.</para>
/// </summary>
/// <param name="Number">The <see cref="Pin.Number"/> this land belongs to.</param>
/// <param name="Center">The pad centre in the footprint's own 2D frame (mm).</param>
/// <param name="Width">The pad width (mm).</param>
/// <param name="Height">The pad height (mm).</param>
/// <param name="Shape">The pad's copper shape.</param>
/// <param name="Kind">Surface-mount or plated through-hole.</param>
/// <param name="DrillDiameter">The finished hole diameter for a through-hole pad (mm); 0 for
/// an SMD pad.</param>
public readonly record struct Pad(
    string Number, Vector2d Center, double Width, double Height, PadShape Shape,
    PadKind Kind = PadKind.Smd, double DrillDiameter = 0)
{
    /// <summary>A surface-mount land (drills nothing).</summary>
    public static Pad Smd(
        string number, Vector2d center, double width, double height,
        PadShape shape = PadShape.RoundedRectangle) =>
        new(number, center, width, height, shape, PadKind.Smd, 0);

    /// <summary>A plated through-hole land: a round pad of diameter <paramref name="pad"/>
    /// with a finished hole of <paramref name="drill"/> through it.</summary>
    /// <exception cref="ArgumentException">The drill is non-positive or leaves no annular
    /// ring (drill ≥ pad diameter).</exception>
    public static Pad ThroughHole(
        string number, Vector2d center, double pad, double drill, PadShape shape = PadShape.Round)
    {
        if (drill <= 0)
            throw new ArgumentException(
                $"A through-hole pad '{number}' needs a positive drill diameter (got {drill:g6}).",
                nameof(drill));
        if (drill >= pad)
            throw new ArgumentException(
                $"Through-hole pad '{number}' has no annular ring: drill {drill:g6} is not smaller "
                + $"than the pad {pad:g6}.", nameof(drill));
        return new(number, center, pad, pad, shape, PadKind.ThroughHole, drill);
    }

    /// <summary>Whether this pad drills the board (a through-hole pad with a positive
    /// drill).</summary>
    public bool IsDrilled => Kind == PadKind.ThroughHole && DrillDiameter > 0;

    /// <summary>The copper ring left around a through-hole pad's drill (mm); 0 for an SMD
    /// pad. Half the difference between the smaller pad dimension and the drill.</summary>
    public double AnnularRing =>
        IsDrilled ? (Math.Min(Width, Height) - DrillDiameter) / 2 : 0;
}

/// <summary>
/// A 2D pad layout for a <see cref="PartDefinition"/> — the land pattern the board layout
/// stage places. Stage 1 (connectivity) carries it as DATA so a definition can already
/// name its footprint; nothing here builds board geometry.
/// <para>The <see cref="Pad.Number"/>s are expected to name pins of the owning definition,
/// but that correspondence is NOT enforced here — a footprint is a reusable value that may
/// be attached to several definitions, and the layout stage is where a pad-without-a-pin
/// (or a pin-without-a-pad) becomes a checkable relationship.</para>
/// </summary>
public sealed class Footprint
{
    /// <summary>The footprint's name (e.g. <c>"R0805"</c>, <c>"SOIC-8"</c>).</summary>
    public string Name { get; }

    /// <summary>The copper lands, in declaration order.</summary>
    public IReadOnlyList<Pad> Pads { get; }

    /// <summary>Builds a footprint from a name and its pads.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public Footprint(string name, IEnumerable<Pad> pads)
    {
        ArgumentNullException.ThrowIfNull(pads);
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("A footprint needs a name.", nameof(name));
        Name = name;
        Pads = [.. pads];
    }

    /// <summary>The footprint's name and pad count.</summary>
    public override string ToString() => $"{Name} ({Pads.Count} pads)";
}
