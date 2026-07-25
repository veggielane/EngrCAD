using EngrCAD.Core;

namespace EngrCAD.Modeling.Text;

/// <summary>
/// Text as 2D geometry: glyph outlines converted to closed <see cref="Sketch"/>es —
/// counters (the holes in O, A, 8) already attached as holes — which then feed
/// <c>Shape.Extrude</c>, <c>Shape.Revolve</c>, <c>Shape.Sweep</c> or any sketch
/// consumer. <see cref="Shape.Text(string, TrueTypeFont, double, double, SketchPlane?, TextStyle?)"/>
/// is the ready-made 3D version.
/// <para><b>Size convention.</b> <c>size</c> is the <b>em size</b>, the typographic
/// meaning of "12 point type": the em square scales to <c>size</c>, so a glyph's
/// coordinates are multiplied by <c>size / font.UnitsPerEm</c>. Capitals are shorter
/// than that (about 70 % in a typical face) — when a drawing specifies letter height,
/// convert it with <see cref="TrueTypeFont.EmSizeForCapHeight"/>.</para>
/// <para><b>Origin convention.</b> Sketch coordinates are <c>x</c> along the writing
/// direction and <c>y</c> up, with the origin on the <b>baseline</b> at the start of
/// the first line — the pen position, exactly where a font places a glyph. Descenders
/// (g, y) therefore reach below y = 0 and further lines sit below the first.</para>
/// </summary>
public static class TextOutlines
{
    /// <summary>
    /// The outline of a single character as closed sketches at the given em
    /// <paramref name="size"/>, positioned with the glyph origin at (0, 0). Blank
    /// glyphs (space) yield an empty list.
    /// </summary>
    /// <exception cref="ArgumentException">The font has no glyph for the character.</exception>
    public static IReadOnlyList<Sketch> GlyphSketches(TrueTypeFont font, char character, double size)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (!font.TryGetGlyph(character, out var glyph))
            throw new ArgumentException(
                $"The font{FontLabel(font)} has no glyph for '{character}' (U+{(int)character:X4}).", nameof(character));
        return GlyphOutlines.ToSketches(glyph, size / font.UnitsPerEm, Vector2d.Zero);
    }

    internal static string FontLabel(TrueTypeFont font) =>
        font.FamilyName.Length == 0 ? "" : $" '{font.FamilyName}'";
}
