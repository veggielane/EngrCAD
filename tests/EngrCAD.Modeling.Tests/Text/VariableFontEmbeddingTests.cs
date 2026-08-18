using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The seam between two features built independently: a font INSTANCED from a variable
/// one, EMBEDDED into a PDF.
///
/// <para><c>WithVariation</c> returns a clone that shares every parsed table with its
/// source — and it shared all of them except the table DIRECTORY, so <c>RawTable</c>
/// (whose only consumer is the PDF subsetter) read a null dictionary. Neither feature's
/// own tests could see it: the variable-font tests reach outlines and advances through
/// already-parsed structures and never go back through the directory, and the PDF tests
/// embed static fonts. The compiler saw it as the solution's only warning, which is what
/// a zero-warning bar buys.</para>
/// </summary>
public class VariableFontEmbeddingTests
{
    private static TrueTypeFont Font() => TrueTypeFont.Load(SyntheticVariableFont.Build());

    [Fact]
    public void AnInstancedFontCanStillReadItsOwnTableDirectory()
    {
        var bold = Font().WithVariation(("wght", 700));
        // the directory itself — the thing that was null
        Assert.NotNull(bold.RawTable("head"));
        Assert.NotNull(bold.RawTable("maxp"));
        Assert.Null(bold.RawTable("nope"));   // absent is null, not a throw
    }

    [Fact]
    public void TheClonesDirectoryIsTheSourcesOwn()
    {
        var font = Font();
        var bold = font.WithVariation(("wght", 700));
        // shared rather than re-parsed: byte-for-byte the same table
        Assert.Equal(font.RawTable("head"), bold.RawTable("head"));
    }

    [Fact]
    public void AnInstancedFontEmbedsIntoAPdf()
    {
        var font = Font();
        var bold = font.WithVariation(("wght", 700));

        var pdf = new PdfDrawing { Font = PdfFont.Embed(bold) };
        pdf.AddText(new Vector2d(0, 0), "AB", 5);
        string text = System.Text.Encoding.ASCII.GetString(pdf.ToPdf());
        Assert.Contains("/FontFile2", text, StringComparison.Ordinal);

        // and it is the INSTANCED font that travels, not its source: the two disagree
        // about the advance, so a subset built from the wrong object would lay the
        // string out at the wrong width.
        Assert.NotEqual(
            font.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth,
            bold.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth);
    }
}
