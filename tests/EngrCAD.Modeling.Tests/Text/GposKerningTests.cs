using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// GPOS pair kerning against the synthetic font's hand-assembled <c>GPOS</c> table:
/// PairPos format 1 (with an XPlacement in front of the XAdvance, so the value-record
/// slot arithmetic is under test), PairPos format 2 through an Extension lookup with
/// coverage format 2 and both class-definition formats, lookup accumulation, and the
/// spec's precedence rule — a GPOS <c>kern</c> feature makes the legacy <c>kern</c>
/// table invisible.
/// </summary>
public class GposKerningTests
{
    private static TrueTypeFont Load(bool gpos = true) =>
        TrueTypeFont.Load(SyntheticFont.Build(new SyntheticFontOptions { Gpos = gpos }));

    [Fact]
    public void PairPosFormat1_ReadsTheXAdvancePastOtherValueRecordFields()
    {
        var font = Load();

        // -60 from lookup 0 (the XAdvance sits AFTER an XPlacement in the record)
        // plus -20 from lookup 2: lookups accumulate.
        Assert.Equal(SyntheticFont.GposKernIO,
            font.KerningBetween(SyntheticFont.RectGlyph, SyntheticFont.RingGlyph));
    }

    [Fact]
    public void PairPosFormat2_ResolvesClassesThroughAnExtensionLookup()
    {
        var font = Load();

        Assert.Equal(SyntheticFont.GposKernCI,
            font.KerningBetween(SyntheticFont.CurveGlyph, SyntheticFont.RectGlyph));
        Assert.Equal(SyntheticFont.GposKernAI,
            font.KerningBetween(SyntheticFont.CompositeGlyph, SyntheticFont.RectGlyph));
    }

    [Fact]
    public void UnkernedPairs_ReturnZero()
    {
        var font = Load();

        // O|I is not covered by any subtable; C|O is covered (format 2) but its class
        // pair kerns by zero.
        Assert.Equal(0, font.KerningBetween(SyntheticFont.RingGlyph, SyntheticFont.RectGlyph));
        Assert.Equal(0, font.KerningBetween(SyntheticFont.CurveGlyph, SyntheticFont.RingGlyph));
    }

    [Fact]
    public void GposKernFeature_TakesPrecedenceOverTheLegacyKernTable()
    {
        // Both tables are present and disagree about I|O (kern says -50, GPOS -80):
        // per the OpenType spec the legacy table is ignored, not merged.
        var withGpos = Load();
        var kernOnly = Load(gpos: false);

        Assert.Equal(SyntheticFont.GposKernIO,
            withGpos.KerningBetween(SyntheticFont.RectGlyph, SyntheticFont.RingGlyph));
        Assert.Equal(SyntheticFont.KernPair.Value,
            kernOnly.KerningBetween(SyntheticFont.KernPair.Left, SyntheticFont.KernPair.Right));
        Assert.True(withGpos.HasKerning);
    }

    [Fact]
    public void Layout_AppliesGposKerningToAdvances()
    {
        var font = Load();

        // At size 10 (em 1000) a font unit is 1/100: 'I' advances 4, 'O' 8, kerning
        // I|O adds GposKernIO/100.
        const double size = 10;
        double expected = (SyntheticFont.Advances[SyntheticFont.RectGlyph]
                           + SyntheticFont.Advances[SyntheticFont.RingGlyph]
                           + SyntheticFont.GposKernIO) * size / SyntheticFont.UnitsPerEm;
        Assert.Equal(expected, TextOutlines.AdvanceWidth("IO", font, size), 9);
        Assert.Equal((SyntheticFont.Advances[SyntheticFont.RectGlyph]
                      + SyntheticFont.Advances[SyntheticFont.RingGlyph]) * size / SyntheticFont.UnitsPerEm,
            TextOutlines.AdvanceWidth("IO", font, size, new TextStyle { Kerning = false }), 9);
    }

    // ---- a real font (Arial and friends ship GPOS kerning) -------------------

    [SkippableFact]
    public void RealFont_KernsClassicPairsThroughGpos()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;
        Skip.IfNot(font.HasKerning, "the system font supplies no kerning at all");

        // Structural, not exact (the values are the font's): at least one classic
        // tight pair kerns negative, and layout narrows accordingly.
        (char Left, char Right)[] candidates = [('A', 'V'), ('T', 'o'), ('A', 'W'), ('Y', 'o'), ('L', 'T')];
        var kerned = candidates.Where(pair =>
        {
            Assert.True(font.TryGetGlyphIndex(pair.Left, out int left));
            Assert.True(font.TryGetGlyphIndex(pair.Right, out int right));
            return font.KerningBetween(left, right) < 0;
        }).ToList();
        Skip.If(kerned.Count == 0, "the system font kerns none of the classic pairs");

        var (l, r) = kerned[0];
        string pairText = $"{l}{r}";
        double with = TextOutlines.AdvanceWidth(pairText, font, 10);
        double without = TextOutlines.AdvanceWidth(pairText, font, 10, new TextStyle { Kerning = false });
        Assert.True(with < without, $"kerning should narrow '{pairText}' ({with} vs {without})");
    }
}
