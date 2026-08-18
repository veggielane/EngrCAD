namespace EngrCAD.Modeling;

/// <summary>
/// Which font a <see cref="PdfDrawing"/> letters with: the built-in Helvetica (the
/// default, and the only one that needs nothing embedded) or a real TrueType font
/// carried in the file as a subset.
///
/// <para><b>The two differ in what they can SAY, which is the whole reason the second
/// exists.</b> The standard-14 Helvetica is encoded as WinAnsi, so it cannot spell the
/// drafting depth (U+21A7), counterbore (U+2334) or countersink (U+2335) symbols a hole
/// callout emits, nor any non-Latin text — and the diameter sign U+2300 only survives as
/// its O-stroke stand-in. An embedded font carries whatever glyphs it has, addressed by
/// glyph index through <c>/Identity-H</c>, so those symbols reach the paper and the
/// substitution is not needed at all.</para>
///
/// <para>Embedding costs bytes (the subset font program) and needs a font file; naming
/// nothing keeps the incumbent file byte for byte.</para>
/// </summary>
public sealed class PdfFont
{
    private PdfFont(TrueTypeFont? source)
    {
        Source = source;
    }

    /// <summary>
    /// The built-in Helvetica over WinAnsi — no font program in the file, because every
    /// conforming reader carries the standard 14. The default.
    /// </summary>
    public static PdfFont Helvetica { get; } = new(null);

    /// <summary>
    /// A TrueType font embedded as a subset of the glyphs the drawing uses. Refused by
    /// name at write time for a PostScript (<c>CFF </c>/<c>.otf</c>) font — see
    /// <c>PdfFontSubset</c> for why that is a separate path rather than a gap.
    /// </summary>
    public static PdfFont Embed(TrueTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        return new PdfFont(font);
    }

    /// <summary>True when the file carries a font program.</summary>
    public bool IsEmbedded => Source is not null;

    /// <summary>The font being embedded, or null for the built-in Helvetica.</summary>
    public TrueTypeFont? Source { get; }

    /// <summary>A name for diagnostics.</summary>
    public override string ToString() =>
        Source is null ? "Helvetica (built-in)"
            : Source.FamilyName.Length > 0 ? $"{Source.FamilyName} (embedded subset)" : "an embedded subset";
}

/// <summary>
/// How a <see cref="PdfDrawing"/> carries a sketch's circular and elliptical arcs, which
/// PDF has no exact form for — its paths are lines and CUBIC Béziers only.
///
/// <para>Which pieces of a sketch survive exactly does not depend on this choice and is
/// worth stating plainly: <b>straight segments and cubic Béziers are exact in either
/// mode</b> (a Bézier IS a PDF path operator, and a quadratic elevates to a cubic
/// exactly, which is what a glyph outline arrives as). Only arcs are approximated, and
/// <see cref="PdfSketchReport"/> reports how many were and by how much.</para>
/// </summary>
public enum PdfCurveMode
{
    /// <summary>
    /// Arcs become polylines whose sagitta is at most the stated tolerance. Simple,
    /// and the deviation is exactly the stated one — but a printed arc is visibly
    /// faceted unless the tolerance is small, which costs points.
    /// </summary>
    Flatten,

    /// <summary>
    /// Arcs become cubic Béziers by the standard <c>k = (4/3)·tan(θ/4)</c> construction,
    /// split into spans of at most a quarter turn and further until the deviation meets
    /// the stated tolerance. Far fewer path elements for the same accuracy, and the
    /// error has an exact closed form (see <see cref="PdfDrawing.ArcCubicDeviation"/>).
    /// </summary>
    Kappa,
}

/// <summary>
/// What a <see cref="PdfDrawing.Add(Sketch, PdfCurveMode, double, SvgLineClass, string?, SvgDrawing.SvgPen?)"/>
/// call actually wrote — the honesty a lossy step owes its caller, in the
/// <c>BiArcFit.MaxDeviation</c> tradition: the counts split what survived exactly from
/// what did not, and the deviation is measured from the construction rather than quoted
/// from the request.
/// </summary>
/// <param name="Mode">The mode the arcs were written in.</param>
/// <param name="ExactSegments">Segments written exactly (lines and cubic Béziers).</param>
/// <param name="ApproximatedSegments">Segments approximated (circular and elliptical arcs).</param>
/// <param name="MaxDeviation">
/// The largest distance from the written path to the true curve, in model units; exactly
/// 0 when nothing was approximated. For a circular arc this is the construction's own
/// closed form; for an elliptical one it is a BOUND (the circular figure carried through
/// the ellipse's largest semi-axis), because an affine map stretches the error
/// anisotropically.
/// </param>
public readonly record struct PdfSketchReport(
    PdfCurveMode Mode, int ExactSegments, int ApproximatedSegments, double MaxDeviation)
{
    /// <summary>True when every segment was written exactly — a sketch of lines and
    /// Béziers, whatever the mode.</summary>
    public bool IsExact => ApproximatedSegments == 0;
}
