using EngrCAD.Core;

namespace EngrCAD.Modeling.Text;

/// <summary>
/// Text as 2D geometry: glyph outlines converted to closed <see cref="Sketch"/>es —
/// counters (the holes in O, A, 8) already attached as holes — which then feed
/// <c>Shape.Extrude</c>, <c>Shape.Revolve</c>, <c>Shape.Sweep</c> or any other sketch
/// consumer. <see cref="Shape.Text(string, TrueTypeFont, double, double, SketchPlane?, TextStyle?)"/>
/// is the ready-made 3D version.
/// <para><b>Size convention.</b> <c>size</c> is the <b>em size</b>, the typographic
/// meaning of "12 point type": the em square scales to <c>size</c>, so glyph
/// coordinates are multiplied by <c>size / font.UnitsPerEm</c>. Capitals are shorter
/// than that (about 70 % of an em in a typical face) — when a drawing specifies letter
/// height, convert it with <see cref="TrueTypeFont.EmSizeForCapHeight"/>.</para>
/// <para><b>Origin convention.</b> Sketch coordinates run <c>x</c> along the writing
/// direction and <c>y</c> up, with the origin on the <b>baseline</b> of the first line
/// — the pen position, exactly where a font places a glyph. Descenders (g, y) reach
/// below y = 0, further lines sit below the first, and
/// <see cref="TextStyle.Align"/> decides whether x = 0 is a line's start, middle or
/// end.</para>
/// <para>Every glyph becomes its own sketch, so a word is a <em>list</em> of sketches
/// (disjoint in any sane font). Missing characters are an error, not a silent
/// omission: a part number engraved with a character quietly dropped is worse than a
/// failed build.</para>
/// </summary>
public static class TextOutlines
{
    /// <summary>
    /// The laid-out text as closed sketches — one per glyph outline, counters attached
    /// as holes. Blank glyphs (spaces) contribute nothing but still advance the pen.
    /// </summary>
    /// <param name="text">The text; <c>\n</c> starts a new line (a <c>\r</c> before it
    /// is ignored).</param>
    /// <param name="font">The font to read outlines and metrics from.</param>
    /// <param name="size">Em size — see the size convention on <see cref="TextOutlines"/>.</param>
    /// <param name="style">Spacing, alignment and kerning; <see cref="TextStyle.Default"/>
    /// when null.</param>
    /// <exception cref="ArgumentException">The font has no glyph for one of the
    /// characters (the character and font are named).</exception>
    public static IReadOnlyList<Sketch> Sketches(
        string text, TrueTypeFont font, double size, TextStyle? style = null)
    {
        var sketches = new List<Sketch>();
        Layout(text, font, size, style, (glyph, scale, origin) =>
            sketches.AddRange(GlyphOutlines.ToSketches(glyph, scale, origin)));
        return sketches;
    }

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
        return GlyphOutlines.ToSketches(Resolve(font, character), size / font.UnitsPerEm, Vector2d.Zero);
    }

    /// <summary>
    /// Bounds of the actual ink (z = 0), for sizing a plate around the text or
    /// centering it. Conservative for curved outlines in exactly the way
    /// <c>Sketch.Bounds</c> is (control-hull), and <see cref="Aabb.Empty"/> when the
    /// text draws nothing. Use <see cref="AdvanceWidth"/> for the typographic width,
    /// which is exact and includes side bearings.
    /// </summary>
    public static Aabb Bounds(string text, TrueTypeFont font, double size, TextStyle? style = null)
    {
        var bounds = Aabb.Empty;
        foreach (var sketch in Sketches(text, font, size, style))
            bounds = bounds.Union(sketch.Bounds);
        return bounds;
    }

    /// <summary>
    /// The typographic width of the text: the widest line's pen advance (advance
    /// widths, kerning and <see cref="TextStyle.LetterSpacing"/> between glyphs, with
    /// no trailing gap). Exact — it never touches an outline.
    /// </summary>
    public static double AdvanceWidth(string text, TrueTypeFont font, double size, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        double widest = 0;
        foreach (string line in SplitLines(text))
            widest = Math.Max(widest, MeasureLine(line, font, size, style ?? TextStyle.Default));
        return widest;
    }

    /// <summary>The baseline-to-baseline step used between lines (em size ×
    /// <see cref="TextStyle.LineSpacing"/>); lines advance downward by this much.</summary>
    public static double LineHeight(double size, TextStyle? style = null)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        return size * (style ?? TextStyle.Default).LineSpacing;
    }

    // ---- layout --------------------------------------------------------------

    /// <summary>Walks the laid-out text, handing each glyph its scale and its origin
    /// (the pen position on its line's baseline).</summary>
    private static void Layout(
        string text, TrueTypeFont font, double size, TextStyle? style, Action<Glyph, double, Vector2d> place)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        style ??= TextStyle.Default;

        double scale = size / font.UnitsPerEm;
        double tracking = size * style.LetterSpacing;
        var lines = SplitLines(text);

        for (int index = 0; index < lines.Count; index++)
        {
            string line = lines[index];
            double width = MeasureLine(line, font, size, style);
            double pen = style.Align switch
            {
                TextAlign.Center => -width / 2,
                TextAlign.Right => -width,
                _ => 0,
            };
            double baseline = -index * LineHeight(size, style);

            int previous = -1;
            foreach (char character in line)
            {
                var glyph = Resolve(font, character);
                if (previous >= 0 && style.Kerning)
                    pen += font.KerningBetween(previous, glyph.Index) * scale;
                if (!glyph.IsEmpty)
                    place(glyph, scale, new Vector2d(pen, baseline));
                pen += glyph.AdvanceWidth * scale + tracking;
                previous = glyph.Index;
            }
        }
    }

    private static double MeasureLine(string line, TrueTypeFont font, double size, TextStyle style)
    {
        double scale = size / font.UnitsPerEm;
        double tracking = size * style.LetterSpacing;
        double pen = 0;
        int previous = -1;
        foreach (char character in line)
        {
            var glyph = Resolve(font, character);
            if (previous >= 0 && style.Kerning)
                pen += font.KerningBetween(previous, glyph.Index) * scale;
            pen += glyph.AdvanceWidth * scale + tracking;
            previous = glyph.Index;
        }
        // Tracking is inserted BETWEEN glyphs: an empty line is zero wide, and a
        // measured line never carries a trailing gap.
        return previous < 0 ? 0 : pen - tracking;
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')];

    private static Glyph Resolve(TrueTypeFont font, char character)
    {
        if (font.TryGetGlyph(character, out var glyph))
            return glyph;
        string shown = char.IsControl(character) ? "" : $" '{character}'";
        throw new ArgumentException(
            $"The font{FontLabel(font)} has no glyph for{shown} U+{(int)character:X4}.", nameof(character));
    }

    private static string FontLabel(TrueTypeFont font) =>
        font.FamilyName.Length == 0 ? "" : $" '{font.FamilyName}'";
}
