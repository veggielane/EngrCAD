using EngrCAD.Modeling.Text;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The TrueType reader against the hand-assembled <see cref="SyntheticFont"/>: every
/// expectation is a value we wrote into the bytes ourselves, so a decoding mistake
/// cannot hide behind "that is what the font says".
/// </summary>
public class TrueTypeFontTests
{
    private static TrueTypeFont Load(SyntheticFontOptions? options = null) =>
        TrueTypeFont.Load(SyntheticFont.Build(options));

    [Fact]
    public void Load_ReadsHeadMaxpAndHheaValues()
    {
        var font = Load();

        Assert.Equal(SyntheticFont.UnitsPerEm, font.UnitsPerEm);
        Assert.Equal(SyntheticFont.GlyphCount, font.GlyphCount);
        Assert.Equal(SyntheticFont.Ascender, font.Ascender);
        Assert.Equal(SyntheticFont.Descender, font.Descender);
        Assert.Equal(SyntheticFont.LineGap, font.LineGap);
        Assert.Equal(SyntheticFont.FamilyName, font.FamilyName);
    }

    [Fact]
    public void Hmtx_GivesEveryGlyphItsAdvanceAndBearing()
    {
        var font = Load();

        for (int i = 0; i < SyntheticFont.GlyphCount; i++)
        {
            var glyph = font.GetGlyph(i);
            Assert.Equal(SyntheticFont.Advances[i], glyph.AdvanceWidth);
            Assert.Equal(SyntheticFont.Bearings[i], glyph.LeftSideBearing);
        }
    }

    [Fact]
    public void Cmap_Format4_MapsCharactersToGlyphIndices()
    {
        var font = Load();

        Assert.True(font.TryGetGlyphIndex(' ', out int space));
        Assert.Equal(SyntheticFont.SpaceGlyph, space);
        Assert.True(font.TryGetGlyphIndex('I', out int rect));
        Assert.Equal(SyntheticFont.RectGlyph, rect);
        Assert.True(font.TryGetGlyphIndex('O', out int ring));
        Assert.Equal(SyntheticFont.RingGlyph, ring);
        Assert.True(font.TryGetGlyphIndex('C', out int curve));
        Assert.Equal(SyntheticFont.CurveGlyph, curve);
        Assert.True(font.TryGetGlyphIndex('A', out int composite));
        Assert.Equal(SyntheticFont.CompositeGlyph, composite);

        Assert.False(font.TryGetGlyphIndex('Z', out _));              // unmapped
        Assert.False(font.TryGetGlyphIndex(0x4E00, out _));           // outside every segment
    }

    [Fact]
    public void SimpleGlyph_DecodesCompressedCoordinatesExactly()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('I', out var glyph));

        var contour = Assert.Single(glyph.Contours);
        AssertContour(SyntheticFont.RectContours[0], contour);
        Assert.Equal(100, glyph.Bounds.Min.X);
        Assert.Equal(0, glyph.Bounds.Min.Y);
        Assert.Equal(300, glyph.Bounds.Max.X);
        Assert.Equal(700, glyph.Bounds.Max.Y);
    }

    [Fact]
    public void SimpleGlyph_WithTwoContours_KeepsThemSeparate()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('O', out var glyph));

        Assert.Equal(2, glyph.Contours.Count);
        AssertContour(SyntheticFont.RingContours[0], glyph.Contours[0]);
        AssertContour(SyntheticFont.RingContours[1], glyph.Contours[1]);
    }

    [Fact]
    public void SimpleGlyph_PreservesOnAndOffCurveFlags()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('C', out var glyph));

        var contour = Assert.Single(glyph.Contours);
        AssertContour(SyntheticFont.CurveContours[0], contour);
        Assert.Equal([true, false, false, true], contour.Points.Select(p => p.OnCurve));
    }

    [Fact]
    public void CompositeGlyph_PlacesComponentsWithOffsetAndScale()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('A', out var glyph));

        Assert.Equal(2, glyph.Contours.Count);
        AssertContour(SyntheticFont.RectContours[0], glyph.Contours[0]);       // identity component

        var scaled = SyntheticFont.RectContours[0]
            .Select(p => new SyntheticFont.Pt(
                (int)(p.X * SyntheticFont.CompositeScale) + SyntheticFont.CompositeOffsetX,
                (int)(p.Y * SyntheticFont.CompositeScale) + SyntheticFont.CompositeOffsetY,
                p.On))
            .ToArray();
        AssertContour(scaled, glyph.Contours[1]);
    }

    [Fact]
    public void BlankGlyph_HasNoContours()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph(' ', out var space));

        Assert.True(space.IsEmpty);
        Assert.Empty(space.Contours);
        Assert.Equal(SyntheticFont.Advances[SyntheticFont.SpaceGlyph], space.AdvanceWidth);
    }

    [Fact]
    public void LongLocaFormat_ParsesToTheSameOutlines()
    {
        var shortLoca = Load();
        var longLoca = Load(new SyntheticFontOptions { LongLoca = true });

        for (int i = 0; i < SyntheticFont.GlyphCount; i++)
        {
            var a = shortLoca.GetGlyph(i);
            var b = longLoca.GetGlyph(i);
            Assert.Equal(a.Contours.Count, b.Contours.Count);
            for (int c = 0; c < a.Contours.Count; c++)
                Assert.Equal(a.Contours[c].Points, b.Contours[c].Points);
        }
    }

    [Fact]
    public void Kern_Format0_ReturnsPairAdjustments()
    {
        var font = Load();

        Assert.True(font.HasKerning);
        Assert.Equal(SyntheticFont.KernPair.Value,
            font.KerningBetween(SyntheticFont.KernPair.Left, SyntheticFont.KernPair.Right));
        Assert.Equal(0, font.KerningBetween(SyntheticFont.KernPair.Right, SyntheticFont.KernPair.Left));
        Assert.Equal(0, font.KerningBetween(SyntheticFont.CurveGlyph, SyntheticFont.RectGlyph));
    }

    [Fact]
    public void CapHeight_PrefersOs2_AndFallsBackToTheAscender()
    {
        Assert.Equal(SyntheticFont.CapHeight, Load().CapHeight);

        // Without OS/2 (and with no 'H' or 'X' glyph to measure) the ascender is the
        // documented last resort.
        var withoutOs2 = Load(new SyntheticFontOptions { OptionalTables = false });
        Assert.Equal(SyntheticFont.Ascender, withoutOs2.CapHeight);
        Assert.False(withoutOs2.HasKerning);
        Assert.Equal("", withoutOs2.FamilyName);
    }

    [Fact]
    public void EmSizeForCapHeight_InvertsTheCapHeightRatio()
    {
        var font = Load();

        double size = font.EmSizeForCapHeight(3.5);
        Assert.Equal(3.5, size * font.CapHeight / font.UnitsPerEm, 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => font.EmSizeForCapHeight(0));
    }

    // ---- rejection paths -----------------------------------------------------

    [Fact]
    public void Load_RejectsPostScriptOutlines()
    {
        byte[] otto = [0x4F, 0x54, 0x54, 0x4F, 0, 1, 0, 0, 0, 0, 0, 0];

        var error = Assert.Throws<FontFormatException>(() => TrueTypeFont.Load(otto));
        Assert.Contains("CFF", error.Message);
        Assert.Contains("TrueType", error.Message);
    }

    [Fact]
    public void Load_RejectsFontCollections()
    {
        byte[] collection = [0x74, 0x74, 0x63, 0x66, 0, 1, 0, 0, 0, 0, 0, 2];

        var error = Assert.Throws<FontFormatException>(() => TrueTypeFont.Load(collection));
        Assert.Contains("Collection", error.Message);
    }

    [Fact]
    public void Load_RejectsUnknownContainers()
    {
        byte[] nonsense = [1, 2, 3, 4, 5, 6, 7, 8];

        var error = Assert.Throws<FontFormatException>(() => TrueTypeFont.Load(nonsense));
        Assert.Contains("Not a TrueType font", error.Message);
    }

    [Fact]
    public void Load_RejectsTruncatedData()
    {
        var full = SyntheticFont.Build();

        // Cut inside the table directory: the reader runs out of bytes mid-record.
        var inHeader = Assert.Throws<FontFormatException>(() => TrueTypeFont.Load(full.AsSpan(0, 20)));
        Assert.Contains("truncated", inHeader.Message, StringComparison.OrdinalIgnoreCase);

        // Cut after it: the directory is intact but its records point past the data.
        var inTables = Assert.Throws<FontFormatException>(() => TrueTypeFont.Load(full.AsSpan(0, full.Length / 2)));
        Assert.Contains("runs past", inTables.Message);
    }

    [Theory]
    [InlineData("glyf")]
    [InlineData("loca")]
    [InlineData("cmap")]
    [InlineData("hmtx")]
    public void Load_NamesTheMissingRequiredTable(string table)
    {
        var error = Assert.Throws<FontFormatException>(
            () => TrueTypeFont.Load(SyntheticFont.Build(new SyntheticFontOptions { OmitTable = table })));

        Assert.Contains(table, error.Message);
    }

    [Fact]
    public void GetGlyph_RejectsOutOfRangeIndices()
    {
        var font = Load();

        Assert.Throws<ArgumentOutOfRangeException>(() => font.GetGlyph(SyntheticFont.GlyphCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => font.GetGlyph(-1));
    }

    private static void AssertContour(SyntheticFont.Pt[] expected, GlyphContour actual)
    {
        Assert.Equal(expected.Length, actual.Points.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, actual.Points[i].Position.X);
            Assert.Equal(expected[i].Y, actual.Points[i].Position.Y);
            Assert.Equal(expected[i].On, actual.Points[i].OnCurve);
        }
    }
}
