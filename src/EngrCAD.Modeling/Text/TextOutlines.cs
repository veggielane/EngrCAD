using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

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
/// end. <see cref="TextStyle.VerticalAlign"/> moves the whole block off the baseline
/// (to its top, middle or bottom) using the font's own ascender and descender, so
/// two differently-lettered labels centred on one point still line up.</para>
/// <para><b>Text on a path.</b> <see cref="SketchesOnPath"/> lays a single line along
/// any <see cref="Curve2d"/> — the engraved ring around a dial or a bezel. Each glyph is
/// placed RIGIDLY (rotated to the path's tangent, never bent to its curvature): only the
/// control points are mapped, which is exactly the curve because a Bézier is an affine
/// combination of them, whereas a curvature-following warp is not affine and has no exact
/// Bézier image. Pen positions are ARC LENGTHS, so letter spacing is the spacing the font
/// asked for however the curve happens to be parameterized.</para>
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
        Layout(text, font, size, style, (glyph, scale, pose) =>
            sketches.AddRange(GlyphOutlines.ToSketches(glyph, scale, pose)));
        return sketches;
    }

    /// <summary>
    /// One line of text laid along <paramref name="path"/> — the ring of lettering round a
    /// dial, a bezel or a nameplate's curved edge — as closed sketches, counters attached
    /// as holes.
    /// <para><b>Each glyph is placed rigidly, not bent.</b> A glyph is rotated so its
    /// baseline is tangent to the path at its own anchor and translated there; its outline
    /// keeps its shape exactly (see the note on <see cref="TextOutlines"/> for why an
    /// exact bent glyph does not exist in this vocabulary). The anchor is the MIDDLE of
    /// the glyph's advance, so a letter leans about its own centre rather than pivoting
    /// off its left edge.</para>
    /// <para><b>Distances along the path are arc lengths</b>, measured from the path's
    /// start; <paramref name="startOffset"/> moves the run along it and
    /// <see cref="TextStyle.Align"/> says whether the run starts at that offset, is
    /// centred on it, or ends there.</para>
    /// <para><b>A glyph's "up" is the path's LEFT normal</b> — its unit tangent turned a
    /// quarter turn counter-clockwise — which is the only choice that makes a straight
    /// left-to-right path reproduce ordinary layout exactly. Note what that means on a
    /// circle: a COUNTER-CLOCKWISE one has its left normal pointing at the centre, so the
    /// lettering hangs inward, right-way-up when read from inside the ring. For text
    /// standing on the outside of a dial, run the path CLOCKWISE —
    /// <c>new Arc2d(centre, radius, startAngle, -2 * Math.PI)</c>.
    /// <see cref="TextStyle.VerticalAlign"/> then lifts the run along that same normal,
    /// so <see cref="TextVerticalAlign.Bottom"/> moves it clear of the curve on whichever
    /// side the text is standing.</para>
    /// </summary>
    /// <param name="path">Any 2D curve. A CLOSED path wraps, so a run may cross the seam;
    /// an open one may not run off either end.</param>
    /// <param name="startOffset">Arc length from the path's start where the run is
    /// anchored (see <see cref="TextStyle.Align"/>).</param>
    /// <param name="upright">When true every glyph is TRANSLATED to its path point and left
    /// un-rotated (its x axis stays world +X), the way a gentle-arc banner or a row of
    /// labels reads; when false (the default) each glyph is rotated to the path's tangent,
    /// the engraved-dial case. Upright is a property of the PLACEMENT, not of the type, so
    /// it is an argument here rather than on <see cref="TextStyle"/>. Both modes anchor at
    /// the middle of the advance and space by arc length, so both reproduce ordinary layout
    /// on a straight horizontal path; they differ only in whether a glyph tilts with the
    /// curve, and in whether <see cref="TextStyle.VerticalAlign"/> lifts along the path's
    /// left normal (rotated) or world +Y (upright, the glyph's own up).</param>
    /// <exception cref="ArgumentException">The text has more than one line (a second line
    /// would need an offset of the path, which is a different curve and may
    /// self-intersect — build it deliberately and call this again); the font has no glyph
    /// for one of the characters; or the run does not fit on the path (the required and
    /// available lengths are named).</exception>
    public static IReadOnlyList<Sketch> SketchesOnPath(
        string text, TrueTypeFont font, double size, Curve2d path,
        TextStyle? style = null, double startOffset = 0, bool upright = false)
    {
        var sketches = new List<Sketch>();
        LayoutOnPath(text, font, size, path, style, startOffset, upright, (glyph, scale, pose) =>
            sketches.AddRange(GlyphOutlines.ToSketches(glyph, scale, pose)));
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
        return GlyphOutlines.ToSketches(
            Resolve(font, character), size / font.UnitsPerEm, GlyphPose.At(Vector2d.Zero));
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

    /// <summary>Walks the laid-out text, handing each glyph its scale and its pose
    /// (upright, at the pen position on its line's baseline).</summary>
    private static void Layout(
        string text, TrueTypeFont font, double size, TextStyle? style, Action<Glyph, double, GlyphPose> place)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        style ??= TextStyle.Default;

        double scale = size / font.UnitsPerEm;
        double tracking = size * style.LetterSpacing;
        var lines = SplitLines(text);
        double shift = VerticalShift(font, size, style, lines.Count);

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
            // Exact-zero semantic test (the epsilon ladder's scale-free tier): a
            // Baseline-aligned block must keep the arithmetic it always had, down to the
            // sign of a zero, so a zero shift is not added at all.
            if (shift != 0)
                baseline += shift;

            int previous = -1;
            foreach (char character in line)
            {
                var glyph = Resolve(font, character);
                if (previous >= 0 && style.Kerning)
                    pen += font.KerningBetween(previous, glyph.Index) * scale;
                if (!glyph.IsEmpty)
                    place(glyph, scale, GlyphPose.At(new Vector2d(pen, baseline)));
                pen += glyph.AdvanceWidth * scale + tracking;
                previous = glyph.Index;
            }
        }
    }

    /// <summary>
    /// Walks one line laid along a curve, handing each glyph a pose whose x axis is the
    /// path's unit tangent at that glyph's own anchor — or world +X when
    /// <paramref name="upright"/>.
    /// </summary>
    private static void LayoutOnPath(
        string text, TrueTypeFont font, double size, Curve2d path, TextStyle? style,
        double startOffset, bool upright, Action<Glyph, double, GlyphPose> place)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(path);
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        style ??= TextStyle.Default;

        var lines = SplitLines(text);
        if (lines.Count > 1)
            throw new ArgumentException(
                $"Text on a path must be a single line ({lines.Count} were given). A second line "
                + "would sit on an OFFSET of the path, which is a different curve and can "
                + "self-intersect — build that curve deliberately and lay its line on it.",
                nameof(text));

        double scale = size / font.UnitsPerEm;
        double tracking = size * style.LetterSpacing;
        string line = lines[0];
        double width = MeasureLine(line, font, size, style);

        var table = new ArcLengthTable2d(path);
        double total = table.TotalLength;
        if (width > total)
            throw new ArgumentException(
                $"\"{line}\" needs {width:G6} of path at em size {size:G6} but the path is only "
                + $"{total:G6} long. Shorten the text, reduce the size, or lengthen the path.",
                nameof(path));

        // The run's start in arc length. Align means the same thing it does on a straight
        // baseline: where x = 0 of the LINE sits, which here is startOffset.
        double begin = style.Align switch
        {
            TextAlign.Center => startOffset - width / 2,
            TextAlign.Right => startOffset - width,
            _ => startOffset,
        };
        // A closed path is a ring, so a run may cross the seam; an open one may not run
        // off either end (extrapolating a curve past its domain is inventing geometry).
        if (!path.IsClosed && (begin < -Weld || begin + width > total + Weld))
            throw new ArgumentException(
                $"\"{line}\" would run from {begin:G6} to {begin + width:G6} along a path that "
                + $"spans 0 to {total:G6}. An open path cannot be extrapolated — move the "
                + $"{nameof(startOffset)}, change the alignment, or close the path.",
                nameof(startOffset));

        double lift = VerticalShift(font, size, style, lineCount: 1);

        double pen = 0;
        int previous = -1;
        foreach (char character in line)
        {
            var glyph = Resolve(font, character);
            if (previous >= 0 && style.Kerning)
                pen += font.KerningBetween(previous, glyph.Index) * scale;
            double advance = glyph.AdvanceWidth * scale;
            if (!glyph.IsEmpty)
            {
                // Anchor at the MIDDLE of the advance (SVG's text-on-path rule): a letter
                // then leans about its own centre instead of pivoting off its left edge,
                // which is what keeps a wide glyph from tipping into its neighbour on a
                // tight curve.
                double at = begin + pen + advance / 2;
                if (path.IsClosed)
                    at = Wrap(at, total);
                double t = table.ParameterAtLength(at);
                var point = path.PointAt(t);
                GlyphPose pose;
                if (upright)
                {
                    // The glyph frame IS the world frame: shift left by half the advance so
                    // the mid-advance baseline point lands on the path, and lift along
                    // world +Y (the glyph's own up). On a straight horizontal path this
                    // coincides with the rotated branch, so upright reproduces ordinary
                    // layout there too.
                    var origin = point - new Vector2d(advance / 2, 0) + new Vector2d(0, lift);
                    pose = GlyphPose.At(origin);
                }
                else
                {
                    var tangent = path.DerivativeAt(t).Normalized();
                    var normal = new Vector2d(-tangent.Y, tangent.X);
                    var origin = point - tangent * (advance / 2) + normal * lift;
                    pose = GlyphPose.Along(origin, tangent);
                }
                place(glyph, scale, pose);
            }
            pen += advance + tracking;
            previous = glyph.Index;
        }
    }

    /// <summary>Arc length reduced into [0, total) — a closed path's seam is not a
    /// boundary, so a run may cross it.</summary>
    private static double Wrap(double length, double total)
    {
        double wrapped = length % total;
        return wrapped < 0 ? wrapped + total : wrapped;
    }

    /// <summary>
    /// How far the block moves off its first baseline for
    /// <see cref="TextStyle.VerticalAlign"/>. Measured from the FONT's ascender and
    /// descender (see <see cref="TextVerticalAlign"/> for why, and note that a font's
    /// descender is negative).
    /// </summary>
    private static double VerticalShift(TrueTypeFont font, double size, TextStyle style, int lineCount)
    {
        if (style.VerticalAlign == TextVerticalAlign.Baseline)
            return 0;
        double scale = size / font.UnitsPerEm;
        double top = font.Ascender * scale;
        double bottom = -(lineCount - 1) * LineHeight(size, style) + font.Descender * scale;
        return style.VerticalAlign switch
        {
            TextVerticalAlign.Top => -top,
            TextVerticalAlign.Bottom => -bottom,
            _ => -(top + bottom) / 2,
        };
    }

    /// <summary>The 1e-9 absolute weld tier: an offset computed as
    /// <c>startOffset - width</c> can land an ulp outside the path's own span, and
    /// refusing that would be refusing a run that exactly fits.</summary>
    private const double Weld = 1e-9;

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
