namespace EngrCAD.Modeling.Text;

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

    /// <summary>
    /// Apply the font's pair kerning (default true). Only the legacy <c>kern</c> table
    /// is read; fonts that ship kerning only in OpenType <c>GPOS</c> lay out on their
    /// advance widths alone, which is what <see cref="TrueTypeFont.HasKerning"/>
    /// reports.
    /// </summary>
    public bool Kerning { get; init; } = true;
}
