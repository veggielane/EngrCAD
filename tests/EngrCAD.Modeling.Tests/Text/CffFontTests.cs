using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The CFF/OpenType-PostScript reader against the hand-assembled
/// <see cref="SyntheticCffFont"/>: every expectation is a value written into the bytes
/// by the test itself. Decoded outlines are pinned as exact coordinates — the operator
/// families reconstruct positions from relative deltas, so a single misread argument
/// (or a mis-skipped hintmask byte) shifts everything after it and cannot pass.
/// </summary>
public class CffFontTests
{
    private static TrueTypeFont Load(SyntheticCffFontOptions? options = null) =>
        TrueTypeFont.Load(SyntheticCffFont.Build(options));

    /// <summary>Em size equal to unitsPerEm, so sketch units ARE font units.</summary>
    private const double UnitScale = SyntheticCffFont.UnitsPerEm;

    // ---- container and metrics ----------------------------------------------

    [Fact]
    public void Load_AcceptsOttoContainers()
    {
        var font = Load();

        Assert.True(font.HasPostScriptOutlines);
        Assert.Equal(SyntheticCffFont.UnitsPerEm, font.UnitsPerEm);
        Assert.Equal(SyntheticCffFont.GlyphCount, font.GlyphCount);
        Assert.Equal(SyntheticCffFont.FamilyName, font.FamilyName);
        Assert.Equal(SyntheticCffFont.CapHeight, font.CapHeight);
        Assert.False(font.HasKerning);
    }

    [Fact]
    public void TrueTypeFonts_ReportQuadraticOutlines()
    {
        Assert.False(TrueTypeFont.Load(SyntheticFont.Build()).HasPostScriptOutlines);
    }

    [Fact]
    public void Hmtx_GivesEveryGlyphItsAdvance()
    {
        var font = Load();

        for (int i = 0; i < SyntheticCffFont.GlyphCount; i++)
            Assert.Equal(SyntheticCffFont.Advances[i], font.GetGlyph(i).AdvanceWidth);
    }

    [Fact]
    public void BlankGlyphs_HaveNoContours()
    {
        var font = Load();

        Assert.True(font.TryGetGlyph(' ', out var space));
        Assert.True(space.IsEmpty);
        Assert.Equal(SyntheticCffFont.Advances[SyntheticCffFont.SpaceGlyph], space.AdvanceWidth);
        Assert.True(font.GetGlyph(SyntheticCffFont.NotdefGlyph).IsEmpty);
    }

    // ---- charstring decoding, pinned to exact coordinates -------------------

    [Fact]
    public void Rect_DecodesMovetoAndAlternatingHlineto()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('I', out var glyph));

        var contour = Assert.Single(glyph.Contours);
        Assert.True(contour.IsCubic);
        AssertContour([(100, 0, true), (300, 0, true), (300, 700, true), (100, 700, true)], contour);
    }

    [Fact]
    public void Ring_KeepsBothContours()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('O', out var glyph));

        Assert.Equal(2, glyph.Contours.Count);
        AssertContour([(0, 0, true), (700, 0, true), (700, 700, true), (0, 700, true)], glyph.Contours[0]);
        AssertContour([(200, 200, true), (500, 200, true), (500, 500, true), (200, 500, true)], glyph.Contours[1]);
    }

    [Fact]
    public void Hintmask_SkipsOneDataBytePerEightStems_IncludingImplicitVstems()
    {
        // 'C' declares 5 hstems plus 4 implicit vstems on the hintmask: 9 stems, two
        // mask bytes. Skipping one byte would leave 0x80 to be read as an operator and
        // the outline would decode to garbage (or throw) — so exact points prove the
        // stem count.
        var font = Load();
        Assert.True(font.TryGetGlyph('C', out var glyph));

        var contour = Assert.Single(glyph.Contours);
        AssertContour(SyntheticCffFont.CurvePoints, contour);
    }

    [Fact]
    public void CurveOperatorFamilies_DecodeExactly()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('W', out var glyph));

        AssertContour(SyntheticCffFont.WavePoints, Assert.Single(glyph.Contours));
    }

    [Fact]
    public void LocalAndGlobalSubrs_AreCalledWithBias()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('S', out var glyph));

        AssertContour(SyntheticCffFont.SubrPoints, Assert.Single(glyph.Contours));
    }

    [Fact]
    public void FlexOperators_DecodeBothCurveHalves()
    {
        var font = Load();
        Assert.True(font.TryGetGlyph('F', out var glyph));

        Assert.Equal(2, glyph.Contours.Count);
        AssertContour(SyntheticCffFont.FlexPoints[0], glyph.Contours[0]);
        AssertContour(SyntheticCffFont.FlexPoints[1], glyph.Contours[1]);
    }

    // ---- CID-keyed fonts -----------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CidKeyedFonts_ResolveLocalSubrsThroughFdSelect(bool fdSelectFormat0)
    {
        var plain = Load();
        var cid = Load(new SyntheticCffFontOptions { CidKeyed = true, FdSelectFormat0 = fdSelectFormat0 });

        // 'S' (font DICT 1, the one with local subrs) and 'I' (font DICT 0) must both
        // decode identically to the non-CID build.
        foreach (char c in "ISWCF O")
        {
            Assert.True(plain.TryGetGlyph(c, out var expected));
            Assert.True(cid.TryGetGlyph(c, out var actual));
            Assert.Equal(expected.Contours.Count, actual.Contours.Count);
            for (int i = 0; i < expected.Contours.Count; i++)
                Assert.Equal(expected.Contours[i].Points, actual.Contours[i].Points);
        }
    }

    // ---- outlines to sketches ------------------------------------------------

    [Fact]
    public void Rect_BecomesOneSketchWithTheExactArea()
    {
        var font = Load();
        var sketch = Assert.Single(TextOutlines.GlyphSketches(font, 'I', UnitScale));

        Assert.Equal(200 * 700, sketch.Area(), 9);
    }

    [Fact]
    public void Counter_BecomesAHole_DespiteMatchingWinding()
    {
        // Both ring contours are wound the same way; only containment classification
        // can get this right (the same property the TrueType tests pin).
        var font = Load();
        var sketch = Assert.Single(TextOutlines.GlyphSketches(font, 'O', UnitScale));

        Assert.Equal(700 * 700 - 300 * 300, sketch.Area(), 9);
        var region = sketch.ToRegion();
        Assert.True(region.SignedDistance(new Vector2d(100, 350)) < 0, "the ring wall should be material");
        Assert.True(region.SignedDistance(new Vector2d(350, 350)) > 0, "the counter should be empty");
    }

    [Fact]
    public void CubicMidpoint_LiesExactlyOnTheOutline()
    {
        // B(1/2) = (P0 + 3P1 + 3P2 + P3)/8 = (200, 300), exactly representable: the
        // cubic analogue of the TrueType implied-midpoint pin. A quadratic
        // misinterpretation of the control pair moves the curve well away from it.
        var font = Load();
        var sketch = Assert.Single(TextOutlines.GlyphSketches(font, 'C', UnitScale));

        var region = sketch.ToRegion();
        Assert.Equal(0, region.SignedDistance(
            new Vector2d(SyntheticCffFont.CurveMidpoint.X, SyntheticCffFont.CurveMidpoint.Y)), 9);
        Assert.True(region.SignedDistance(new Vector2d(200, 100)) < 0, "the arch interior should be material");
        Assert.True(region.SignedDistance(new Vector2d(200, 350)) > 0, "above the arch should be empty");
    }

    [Fact]
    public void SubrGlyph_EnclosesTheExactRectangle()
    {
        var font = Load();
        var sketch = Assert.Single(TextOutlines.GlyphSketches(font, 'S', UnitScale));

        Assert.Equal(300 * 700, sketch.Area(), 9);
    }

    [Fact]
    public void Text_ExtrudesToAClosedSolidInAllRepresentations()
    {
        var font = Load();
        var shape = Shape.Text("I", font, size: 10, height: 2);

        // The 'I' is straight lines only, so the prism volume is exact: the glyph
        // rectangle is 200x700 font units at scale 10/1000.
        double expected = 200 * 700 * (10.0 / 1000) * (10.0 / 1000) * 2;
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(expected, mesh.Volume(), 9);

        var brep = shape.ToBrep();
        Assert.NotNull(brep);
    }

    [Fact]
    public void CubicText_RoundTripsThroughTheImplicitRepresentation()
    {
        var font = Load();
        var shape = Shape.Text("C", font, size: 10, height: 2);

        var sdf = shape.ToImplicit();
        // The arch midpoint maps to (2, 3) at size 10; on the extruded solid's wall.
        Assert.True(sdf.Evaluate((2, 3, 1)) <= 1e-9);
        Assert.True(sdf.Evaluate((2, 1, 1)) < 0, "inside the arch should be material");
    }

    // ---- rejection paths -----------------------------------------------------

    [Fact]
    public void SeacEndchar_IsRejectedByName()
    {
        var font = Load(new SyntheticCffFontOptions { SeacEndchar = true });

        var error = Assert.Throws<FontFormatException>(() => font.GetGlyph(SyntheticCffFont.RectGlyph));
        Assert.Contains("seac", error.Message);
    }

    [Fact]
    public void ArithmeticOperators_AreRejectedByNumber()
    {
        var font = Load(new SyntheticCffFontOptions { ArithmeticOp = true });

        var error = Assert.Throws<FontFormatException>(() => font.GetGlyph(SyntheticCffFont.RectGlyph));
        Assert.Contains("12 15", error.Message);
    }

    [Fact]
    public void Type1Charstrings_AreRejectedAtLoad()
    {
        var error = Assert.Throws<FontFormatException>(
            () => Load(new SyntheticCffFontOptions { CharstringType = 1 }));

        Assert.Contains("Type 2", error.Message);
    }

    [Fact]
    public void Cff2VariableFonts_AreRejectedByName()
    {
        var error = Assert.Throws<FontFormatException>(
            () => TrueTypeFont.Load(SyntheticCffFont.BuildCff2Stub()));

        Assert.Contains("CFF2", error.Message);
    }

    private static void AssertContour((double X, double Y, bool On)[] expected, GlyphContour actual)
    {
        Assert.True(actual.IsCubic, "CFF contours should be flagged cubic");
        Assert.Equal(expected.Length, actual.Points.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, actual.Points[i].Position.X);
            Assert.Equal(expected[i].Y, actual.Points[i].Position.Y);
            Assert.Equal(expected[i].On, actual.Points[i].OnCurve);
        }
    }
}
