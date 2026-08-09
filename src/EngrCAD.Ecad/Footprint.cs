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
/// One copper land of a <see cref="Footprint"/>: where the board layout stage places the
/// pad for a given <see cref="PartDefinition"/> pin. It is DATA now — a placeholder the
/// layout stage will consume — not board geometry: nothing here lowers to a
/// <see cref="Modeling.Shape"/>, and the coordinates are in the footprint's own 2D frame
/// (millimetres, origin at the part's placement point).
/// </summary>
/// <param name="Number">The <see cref="Pin.Number"/> this land belongs to.</param>
/// <param name="Center">The pad centre in the footprint's own 2D frame (mm).</param>
/// <param name="Width">The pad width (mm).</param>
/// <param name="Height">The pad height (mm).</param>
/// <param name="Shape">The pad's copper shape.</param>
public readonly record struct Pad(
    string Number, Vector2d Center, double Width, double Height, PadShape Shape);

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
