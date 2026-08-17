using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The CFF2 reader against <see cref="SyntheticCff2Font"/>: every expectation is a value
/// written into the bytes, so a decoding mistake shows as a wrong coordinate rather than
/// as a shape that looks plausible.
/// </summary>
public class Cff2FontTests
{
    private const double WeightScalar = SyntheticVariations.TestWeightNormalized;   // 0.75
    private const double WidthScalar = SyntheticVariations.TestWidthNormalized;     // 0.5

    private static TrueTypeFont Load() => TrueTypeFont.Load(SyntheticCff2Font.Build());

    private static TrueTypeFont Instanced() => Load().WithVariation(
        (SyntheticVariations.WeightTag, SyntheticVariations.TestWeight),
        (SyntheticVariations.WidthTag, SyntheticVariations.TestWidth));

    [Fact]
    public void ACff2FontLoadsAsAPostScriptVariableFont()
    {
        var font = Load();

        Assert.True(font.HasPostScriptOutlines);
        Assert.True(font.IsVariable);
        Assert.Equal(SyntheticCff2Font.GlyphCount, font.GlyphCount);
        Assert.Equal(SyntheticCff2Font.UnitsPerEm, font.UnitsPerEm);
        Assert.Equal(SyntheticCff2Font.FamilyName, font.FamilyName);
        Assert.Equal(2, font.VariationAxes.Count);
    }

    /// <summary>The subroutine, curve and hint paths decode to exact coordinates, so the
    /// shared <c>Type2Interpreter</c> is doing the same arithmetic through the CFF2
    /// dialect that it does through Type 2.</summary>
    [Fact]
    public void TheSharedCharstringMachineDecodesExactly()
    {
        var font = Load();

        AssertContour(SyntheticCff2Font.SubrPoints, font.GetGlyph(SyntheticCff2Font.SubrGlyph).Contours[0]);
        AssertContour(SyntheticCff2Font.CurvePoints, font.GetGlyph(SyntheticCff2Font.CurveGlyph).Contours[0]);

        var ring = font.GetGlyph(SyntheticCff2Font.RingGlyph);
        Assert.Equal(2, ring.Contours.Count);
        for (int c = 0; c < 2; c++)
            AssertContour(SyntheticCff2Font.RingPoints[c], ring.Contours[c]);
    }

    /// <summary>
    /// The hintmask stem count with NO width operand: nine stems, two mask bytes. Reading
    /// one byte would misread every operator after it, so the outline above is the
    /// assertion — 'O' comes back as two clean rectangles rather than as garbage.
    /// </summary>
    [Fact]
    public void HintmaskCountsStemsWithoutStrippingAWidth()
    {
        var ring = Load().GetGlyph(SyntheticCff2Font.RingGlyph);
        Assert.Equal(2, ring.Contours.Count);
        Assert.All(ring.Contours, c => Assert.Equal(4, c.Points.Count));
    }

    /// <summary>
    /// <c>blend</c> at an instance: the value is its default plus each region's scalar
    /// times that region's delta, so the rectangle widens by exactly the weight term.
    /// </summary>
    [Fact]
    public void BlendAppliesTheRegionScalarsToItsOwnDeltas()
    {
        var points = Load().GetGlyph(SyntheticCff2Font.RectGlyph).Contours[0].Points;
        Assert.Equal(100 + SyntheticCff2Font.RectHalfWidth, points[1].Position.X, 9);

        var instanced = Instanced().GetGlyph(SyntheticCff2Font.RectGlyph).Contours[0].Points;
        double width = SyntheticCff2Font.RectHalfWidth
            + SyntheticCff2Font.RectWeightDelta * WeightScalar
            + SyntheticCff2Font.RectWidthDelta * WidthScalar;
        Assert.Equal(245, width, 9);
        Assert.Equal(100, instanced[0].Position.X, 9);
        Assert.Equal(100 + width, instanced[1].Position.X, 9);
        Assert.Equal(100 + width, instanced[2].Position.X, 9);
        Assert.Equal(700, instanced[2].Position.Y, 9);
        Assert.Equal(100, instanced[3].Position.X, 9);
    }

    /// <summary>
    /// <c>vsindex</c> selects WHICH item variation data a blend reads, hence how many
    /// deltas each of its values carries — 'V' names the one-region data, so its blends
    /// take one delta and the width axis alone moves it.
    /// </summary>
    [Fact]
    public void VsIndexSelectsTheVariationDataAndHenceTheDeltaCount()
    {
        var plain = Load().GetGlyph(SyntheticCff2Font.VsIndexGlyph).Contours[0].Points;
        Assert.Equal(SyntheticCff2Font.VsIndexWidth, plain[1].Position.X, 9);

        var instanced = Instanced().GetGlyph(SyntheticCff2Font.VsIndexGlyph).Contours[0].Points;
        double width = SyntheticCff2Font.VsIndexWidth + SyntheticCff2Font.VsIndexDelta * WidthScalar;
        Assert.Equal(350, width, 9);
        Assert.Equal(width, instanced[1].Position.X, 9);
        Assert.Equal(500, instanced[2].Position.Y, 9);

        // The weight axis alone leaves it exactly where it was: its region is not in the
        // item data vsindex 1 names.
        var weightOnly = Load().WithVariation((SyntheticVariations.WeightTag, SyntheticVariations.WeightMaximum));
        Assert.Equal(SyntheticCff2Font.VsIndexWidth,
            weightOnly.GetGlyph(SyntheticCff2Font.VsIndexGlyph).Contours[0].Points[1].Position.X, 9);
    }

    /// <summary>
    /// A PostScript outline carries no phantom points, so <c>HVAR</c> is CFF2's only route
    /// to a varied advance — and it is read.
    /// </summary>
    [Fact]
    public void HvarVariesTheAdvanceOfACff2Glyph()
    {
        var plain = Load();
        var bold = Instanced();

        Assert.Equal(SyntheticCff2Font.Advances[SyntheticCff2Font.RectGlyph],
            plain.GetGlyph(SyntheticCff2Font.RectGlyph).AdvanceWidth, 9);
        Assert.Equal(
            SyntheticCff2Font.Advances[SyntheticCff2Font.RectGlyph]
                + SyntheticCff2Font.HvarAdvanceDelta * WeightScalar,
            bold.GetGlyph(SyntheticCff2Font.RectGlyph).AdvanceWidth, 9);
        Assert.Equal(625, bold.GetGlyph(SyntheticCff2Font.RectGlyph).AdvanceWidth, 9);

        // .notdef maps to the zero row.
        Assert.Equal(SyntheticCff2Font.Advances[SyntheticCff2Font.NotdefGlyph],
            bold.GetGlyph(SyntheticCff2Font.NotdefGlyph).AdvanceWidth, 9);
    }

    /// <summary>At the DEFAULT coordinate every region's scalar is zero, so a blend
    /// returns its own default values and the instanced outlines are BIT-IDENTICAL to the
    /// un-instanced read.</summary>
    [Fact]
    public void TheDefaultInstanceIsBitIdenticalToTheUninstancedRead()
    {
        var plain = Load();
        var defaulted = plain.WithVariation();

        for (int glyph = 0; glyph < SyntheticCff2Font.GlyphCount; glyph++)
        {
            var left = plain.GetGlyph(glyph);
            var right = defaulted.GetGlyph(glyph);
            Assert.Equal(left.Contours.Count, right.Contours.Count);
            for (int c = 0; c < left.Contours.Count; c++)
            {
                var lp = left.Contours[c].Points;
                var rp = right.Contours[c].Points;
                Assert.Equal(lp.Count, rp.Count);
                for (int i = 0; i < lp.Count; i++)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(lp[i].Position.X),
                        BitConverter.DoubleToInt64Bits(rp[i].Position.X));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(lp[i].Position.Y),
                        BitConverter.DoubleToInt64Bits(rp[i].Position.Y));
                }
            }
            Assert.Equal(BitConverter.DoubleToInt64Bits(left.AdvanceWidth),
                BitConverter.DoubleToInt64Bits(right.AdvanceWidth));
        }
    }

    /// <summary>A varied CFF2 font is a font: modelled text takes it unchanged and the
    /// geometry follows the instance.</summary>
    [Fact]
    public void ShapeTextConsumesACff2VariableFont()
    {
        double plainWidth = Shape.Text("I", Load(), size: 100, height: 2).Bounds().Size.X;
        double boldWidth = Shape.Text("I", Instanced(), size: 100, height: 2).Bounds().Size.X;

        Assert.Equal(SyntheticCff2Font.RectHalfWidth / 10.0, plainWidth, 6);
        Assert.Equal(
            (SyntheticCff2Font.RectHalfWidth + SyntheticCff2Font.RectWeightDelta * WeightScalar) / 10.0,
            boldWidth, 6);
        Assert.Equal(24.5, boldWidth, 6);
    }

    /// <summary>Landing CFF2 must not have moved the Type 2 reader: its own exact
    /// coordinates still decode, through the interpreter both dialects now share.</summary>
    [Fact]
    public void TheType2ReaderIsUnchanged()
    {
        var font = TrueTypeFont.Load(SyntheticCffFont.Build());
        AssertContour(SyntheticCffFont.WavePoints, font.GetGlyph(SyntheticCffFont.WaveGlyph).Contours[0]);
        AssertContour(SyntheticCffFont.SubrPoints, font.GetGlyph(SyntheticCffFont.SubrGlyph).Contours[0]);
        AssertContour(SyntheticCffFont.CurvePoints, font.GetGlyph(SyntheticCffFont.CurveGlyph).Contours[0]);
        Assert.Equal(SyntheticCffFont.Advances[SyntheticCffFont.RectGlyph],
            font.GetGlyph(SyntheticCffFont.RectGlyph).AdvanceWidth);
    }

    private static void AssertContour((double X, double Y, bool On)[] expected, GlyphContour actual)
    {
        Assert.True(actual.IsCubic, "CFF2 contours are cubic");
        Assert.Equal(expected.Length, actual.Points.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, actual.Points[i].Position.X, 9);
            Assert.Equal(expected[i].Y, actual.Points[i].Position.Y, 9);
            Assert.Equal(expected[i].On, actual.Points[i].OnCurve);
        }
    }
}
