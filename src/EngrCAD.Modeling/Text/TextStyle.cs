namespace EngrCAD.Modeling;

/// <summary>Horizontal placement of each line relative to x = 0.</summary>
public enum TextAlign
{
    /// <summary>x = 0 is the start of the line (the default).</summary>
    Left,

    /// <summary>x = 0 is the middle of the line.</summary>
    Center,

    /// <summary>x = 0 is the end of the line.</summary>
    Right,
}

/// <summary>
/// Vertical placement of the whole text BLOCK relative to y = 0.
/// <para><b>Measured from the font's own metrics, never from the ink.</b> The block's top
/// is the first line's baseline plus <see cref="TrueTypeFont.Ascender"/> and its bottom is
/// the last line's baseline plus <see cref="TrueTypeFont.Descender"/> (which is negative),
/// so two labels centred on the same point line up whether or not either happens to
/// contain a descender or a capital. Aligning to the measured ink would make "AB" and
/// "ay" sit at different heights, which is the failure mode this convention exists to
/// avoid — <see cref="TextOutlines.Bounds"/> is still there for the caller who genuinely
/// wants the ink.</para>
/// </summary>
public enum TextVerticalAlign
{
    /// <summary>y = 0 is the first line's baseline — the typographic origin, and the
    /// default (a run placed this way is bit-for-bit what it always was).</summary>
    Baseline,

    /// <summary>y = 0 is the top of the block (first baseline + ascender).</summary>
    Top,

    /// <summary>y = 0 is halfway between the block's top and bottom.</summary>
    Middle,

    /// <summary>y = 0 is the bottom of the block (last baseline + descender).</summary>
    Bottom,
}

/// <summary>
/// How a run of text is laid out. All spacing is expressed as a multiple of the em
/// <c>size</c>, never in absolute units, so one style stays correct at every size.
/// <para>The defaults are the ordinary typographic ones: the font's own advance widths
/// and kerning, no extra tracking, and lines 1.2 em apart.</para>
/// </summary>
public sealed record TextStyle
{
    /// <summary>The default style — font metrics, kerning on, no extra tracking.</summary>
    public static readonly TextStyle Default = new();

    /// <summary>
    /// Extra space inserted after every glyph (tracking), as a multiple of the em size:
    /// 0.05 opens the text up by 5 % of an em per character, negative values tighten
    /// it. It is added <em>between</em> glyphs only — a line's measured width never
    /// includes a trailing gap.
    /// </summary>
    public double LetterSpacing { get; init; }

    /// <summary>Baseline-to-baseline distance as a multiple of the em size (default
    /// 1.2, the usual single-spaced value). Lines advance downward, so the second
    /// line's baseline sits at <c>-LineSpacing * size</c>.</summary>
    public double LineSpacing { get; init; } = 1.2;

    /// <summary>Where x = 0 sits on each line (default <see cref="TextAlign.Left"/>).</summary>
    public TextAlign Align { get; init; } = TextAlign.Left;

    /// <summary>Where y = 0 sits on the whole block (default
    /// <see cref="TextVerticalAlign.Baseline"/>, the typographic origin).</summary>
    public TextVerticalAlign VerticalAlign { get; init; } = TextVerticalAlign.Baseline;

    /// <summary>
    /// Apply the font's pair kerning (default true). Pairs come from the OpenType
    /// <c>GPOS</c> <c>kern</c> feature when the font has one (which makes the legacy
    /// <c>kern</c> table invisible, per the spec), else from the legacy table;
    /// <see cref="TrueTypeFont.HasKerning"/> reports whether either source exists.
    /// </summary>
    public bool Kerning { get; init; } = true;
}
