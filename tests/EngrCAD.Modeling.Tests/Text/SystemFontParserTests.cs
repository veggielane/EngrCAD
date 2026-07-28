using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The reader against a real, production font (Arial and friends) — the synthetic font
/// proves exact decoding, this proves the reader survives the messy reality of shipped
/// fonts: thousands of glyphs, real composites, real coordinate compression, a real
/// cmap. Assertions are structural only (see <see cref="SystemFonts"/>).
/// </summary>
public class SystemFontParserTests
{
    [SkippableFact]
    public void RealFont_ExposesItsMetricsAndGlyphRepertoire()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        Assert.InRange(font.UnitsPerEm, 16, 16384);
        Assert.True(font.GlyphCount > 100, $"expected a full repertoire, got {font.GlyphCount} glyphs");
        Assert.NotEqual("", font.FamilyName);
        Assert.True(font.Ascender > 0);
        Assert.True(font.Descender < 0);
        Assert.InRange(font.CapHeight, 0.4 * font.UnitsPerEm, font.UnitsPerEm);

        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
            Assert.True(font.TryGetGlyphIndex(c, out _), $"font has no glyph for '{c}'");
    }

    [SkippableFact]
    public void RealFont_LettersHaveOutlinesAndAdvances_SpaceHasNeither()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        foreach (char c in "ABEHIOQ8")
        {
            Assert.True(font.TryGetGlyph(c, out var glyph));
            Assert.False(glyph.IsEmpty, $"'{c}' has no contours");
            Assert.True(glyph.AdvanceWidth > 0, $"'{c}' has a zero advance");
            Assert.True(glyph.Bounds.Max.Y > 0);
        }

        Assert.True(font.TryGetGlyph(' ', out var space));
        Assert.True(space.IsEmpty);
        Assert.True(space.AdvanceWidth > 0);        // blank, but it still moves the pen
    }

    [SkippableFact]
    public void RealFont_CountersAreSeparateContours()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        Assert.True(font.TryGetGlyph('O', out var o));
        Assert.Equal(2, o.Contours.Count);          // outline + counter

        Assert.True(font.TryGetGlyph('8', out var eight));
        Assert.Equal(3, eight.Contours.Count);      // outline + two counters
    }

    [SkippableFact]
    public void RealFont_CompositeGlyphsFlattenToTheirComponents()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        // Accented capitals are the canonical composite: a base letter plus a mark,
        // each contributing its own contours.
        // U+00C4 as an escape, not a literal: source files here stay ASCII.
        Skip.IfNot(font.TryGetGlyph(0x00C4, out var aDiaeresis), "font has no U+00C4 (A with diaeresis)");
        Assert.True(font.TryGetGlyph('A', out var a));
        Assert.True(aDiaeresis.Contours.Count > a.Contours.Count,
            $"'A' has {a.Contours.Count} contours, U+00C4 only {aDiaeresis.Contours.Count} - the mark is missing");
        Assert.True(aDiaeresis.Bounds.Max.Y > a.Bounds.Max.Y, "the diaeresis should sit above the 'A'");
    }

    [SkippableFact]
    public void RealFont_OffCurvePointsAppearInCurvedGlyphs()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        Assert.True(font.TryGetGlyph('O', out var o));
        var points = o.Contours.SelectMany(c => c.Points).ToList();
        Assert.Contains(points, p => p.OnCurve);
        Assert.Contains(points, p => !p.OnCurve);

        // 'I' in a sans-serif face is a straight bar: no control points at all.
        Assert.True(font.TryGetGlyph('I', out var i));
        Assert.All(i.Contours.SelectMany(c => c.Points), p => Assert.True(p.OnCurve));
    }

    // ---- OpenType/CFF (.otf) — commonly skips: Windows ships no .otf ---------

    [SkippableFact]
    public void RealCffFont_ParsesAndProducesClosedGeometry()
    {
        Skip.If(SystemFonts.CffSkipReason is not null, SystemFonts.CffSkipReason);
        var font = SystemFonts.CffFont;

        Assert.True(font.HasPostScriptOutlines);
        Assert.True(font.GlyphCount > 0);

        Assert.True(font.TryGetGlyph('O', out var o));
        Assert.False(o.IsEmpty);
        Assert.All(o.Contours, c => Assert.True(c.IsCubic));

        // Structural facts only (the exact outlines are the font's, not ours): the
        // ring encloses area, its counter classifies as a hole, and text extrudes to
        // a closed solid.
        var sketches = TextOutlines.GlyphSketches(font, 'O', 10);
        Assert.True(sketches.Count >= 1);
        Assert.True(sketches.Sum(s => s.Area()) > 0);

        var mesh = Shape.Text("OK", font, size: 10, height: 2).ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.Volume() > 0);
    }
}
