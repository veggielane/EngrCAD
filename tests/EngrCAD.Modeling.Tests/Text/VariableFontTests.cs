using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// Variable fonts read from a synthetic <c>fvar</c>/<c>avar</c>/<c>gvar</c>/<c>HVAR</c>
/// font assembled byte by byte. Every expected coordinate here is hand-computed from the
/// stored deltas and the design's own exactly-representable scalars, so a decoding
/// mistake shows as a wrong NUMBER rather than as a shape that looks plausible.
/// </summary>
public class VariableFontTests
{
    private const double WeightScalar = SyntheticVariations.TestWeightNormalized;   // 0.75
    private const double WidthScalar = SyntheticVariations.TestWidthNormalized;     // 0.5

    private static TrueTypeFont Load(SyntheticVariableFontOptions? options = null) =>
        TrueTypeFont.Load(SyntheticVariableFont.Build(options));

    private static TrueTypeFont Instanced(SyntheticVariableFontOptions? options = null) =>
        Load(options).WithVariation(
            (SyntheticVariations.WeightTag, SyntheticVariations.TestWeight),
            (SyntheticVariations.WidthTag, SyntheticVariations.TestWidth));

    private static Vector2d[] Points(TrueTypeFont font, int glyph, int contour = 0) =>
        [.. font.GetGlyph(glyph).Contours[contour].Points.Select(p => p.Position)];

    // ---- fvar: the design space -----------------------------------------------

    [Fact]
    public void FvarAxesAreRead()
    {
        var font = Load();

        Assert.True(font.IsVariable);
        Assert.Equal(2, font.VariationAxes.Count);

        var weight = font.VariationAxes[0];
        Assert.Equal(SyntheticVariations.WeightTag, weight.Tag);
        Assert.Equal(SyntheticVariations.WeightMinimum, weight.Minimum);
        Assert.Equal(SyntheticVariations.WeightDefault, weight.Default);
        Assert.Equal(SyntheticVariations.WeightMaximum, weight.Maximum);
        Assert.Equal(SyntheticVariations.WeightName, weight.Name);
        Assert.False(weight.Hidden);

        var width = font.VariationAxes[1];
        Assert.Equal(SyntheticVariations.WidthTag, width.Tag);
        Assert.Equal(SyntheticVariations.WidthMinimum, width.Minimum);
        Assert.Equal(SyntheticVariations.WidthMaximum, width.Maximum);
        Assert.Equal(SyntheticVariations.WidthName, width.Name);
    }

    [Fact]
    public void AHiddenAxisIsReportedRatherThanDropped()
    {
        var font = Load(new SyntheticVariableFontOptions { HiddenWidthAxis = true });

        Assert.False(font.VariationAxes[0].Hidden);
        Assert.True(font.VariationAxes[1].Hidden);
        // A hidden axis is still a legal axis to set.
        Assert.Equal(SyntheticVariations.WidthMinimum,
            font.WithVariation((SyntheticVariations.WidthTag, SyntheticVariations.WidthMinimum))
                .Variation[SyntheticVariations.WidthTag]);
    }

    [Fact]
    public void NamedInstancesCarryTheirCoordinates()
    {
        var font = Load();
        var names = font.NamedInstances.Select(i => i.Name).ToArray();
        Assert.Equal([SyntheticVariations.SemiboldInstance, SyntheticVariations.CondensedInstance], names);

        var semibold = font.NamedInstances[0];
        Assert.Equal(SyntheticVariations.TestWeight, semibold.Coordinates[SyntheticVariations.WeightTag]);
        Assert.Equal(SyntheticVariations.WidthDefault, semibold.Coordinates[SyntheticVariations.WidthTag]);
        Assert.Equal(SyntheticVariations.SemiboldPostScriptName, semibold.PostScriptName);
    }

    /// <summary>A named instance is a coordinate a caller could have typed, so the two
    /// routes must produce the SAME font — asserted as bit-identical outlines, since the
    /// instance's weight is exactly representable.</summary>
    [Fact]
    public void ANamedInstanceIsExactlyTheCoordinateItNames()
    {
        var font = Load();
        var byName = font.WithNamedInstance(SyntheticVariations.SemiboldInstance);
        var byCoordinate = font.WithVariation((SyntheticVariations.WeightTag, SyntheticVariations.TestWeight));

        AssertBitIdentical(byName, byCoordinate, SyntheticVariableFont.RectGlyph);
        Assert.Equal(
            byCoordinate.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth,
            byName.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth);
    }

    // ---- normalization and avar ------------------------------------------------

    /// <summary>
    /// The specification's piecewise-linear normalization, split at the DEFAULT: every
    /// axis reads exactly 0 at its own default whatever its range, −1 at its minimum and
    /// +1 at its maximum, and a value outside the range is CLAMPED rather than refused.
    /// </summary>
    [Theory]
    [InlineData(400, 0.0)]
    [InlineData(900, 1.0)]
    [InlineData(100, -1.0)]
    [InlineData(650, 0.5)]
    [InlineData(250, -0.5)]
    [InlineData(5000, 1.0)]     // clamped
    [InlineData(-5000, -1.0)]   // clamped
    public void AxisNormalizationSplitsAtTheDefault(double value, double expected)
    {
        var axis = new VariationAxis(SyntheticVariations.WeightTag,
            SyntheticVariations.WeightMinimum, SyntheticVariations.WeightDefault,
            SyntheticVariations.WeightMaximum, "Weight", Hidden: false);

        Assert.Equal(expected, FontVariations.NormalizeAxis(axis, value), 12);
    }

    /// <summary>The <c>avar</c> map is applied and it MOVES the answer: the same weight
    /// lands at 0.5 without it and at 0.75 with it, so every delta below differs by half
    /// again.</summary>
    [Fact]
    public void AvarWarpsTheAxisAndTheDifferenceIsMeasurable()
    {
        var mapped = Instanced();
        var unmapped = Instanced(new SyntheticVariableFontOptions { Avar = false });

        double mappedX = Points(mapped, SyntheticVariableFont.RectGlyph)[0].X;
        double unmappedX = Points(unmapped, SyntheticVariableFont.RectGlyph)[0].X;

        // Left edge 100, delta −50 at the axis extreme.
        Assert.Equal(100 - 50 * SyntheticVariations.TestWeightNormalized, mappedX, 9);
        Assert.Equal(100 - 50 * SyntheticVariations.TestWeightUnmapped, unmappedX, 9);
        Assert.NotEqual(mappedX, unmappedX);
    }

    [Fact]
    public void AnAvarVersionThisReaderCannotHonourLoadsAndRefusesToVary()
    {
        var font = Load(new SyntheticVariableFontOptions { AvarVersion2 = true });

        // The font itself reads fine at its default.
        Assert.True(font.IsVariable);
        Assert.Equal(SyntheticVariableFont.Advances[SyntheticVariableFont.RectGlyph],
            font.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth);

        var error = Assert.Throws<FontFormatException>(
            () => font.WithVariation((SyntheticVariations.WeightTag, SyntheticVariations.TestWeight)));
        Assert.Contains("avar", error.Message);
    }

    // ---- gvar: outlines ---------------------------------------------------------

    /// <summary>'I' varies through a SHARED tuple over ALL points; every coordinate is
    /// the stored delta times the weight scalar, exactly.</summary>
    [Fact]
    public void OutlineDeltasApplyAtTheInstancedScalar()
    {
        var points = Points(Instanced(), SyntheticVariableFont.RectGlyph);
        var expected = SyntheticVariableFont.RectContours[0];

        Assert.Equal(expected.Length, points.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X + SyntheticVariableFont.RectDeltaX[i] * WeightScalar, points[i].X, 9);
            Assert.Equal(expected[i].Y, points[i].Y, 9);
        }
    }

    /// <summary>
    /// THE exact-zero clause: 'I' has a peak of exactly zero on the width axis, so that
    /// axis contributes a factor of ONE and the glyph must not move when only the width
    /// changes — while a reader treating a zero peak as "off" would zero the whole tuple
    /// and 'I' would never move at all (the mutation the previous test catches).
    /// </summary>
    [Fact]
    public void AnAxisWithAZeroPeakContributesAFactorOfOne()
    {
        var widthOnly = Load().WithVariation((SyntheticVariations.WidthTag, SyntheticVariations.WidthMaximum));
        var plain = Load();

        AssertBitIdentical(widthOnly, plain, SyntheticVariableFont.RectGlyph);
        Assert.Equal(plain.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth,
            widthOnly.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth);
    }

    /// <summary>
    /// IUP: only points 0 and 3 carry deltas, and the four inferred ones differ from
    /// BOTH the "no delta" and the "nearest neighbour's delta" answers — points 1 and 2
    /// interpolate along x, point 4 lies outside the anchors' range and is TRANSLATED by
    /// the nearer one, and in y the two anchors share a coordinate and a delta so the
    /// whole contour translates.
    /// </summary>
    [Fact]
    public void UnreferencedPointsAreInterpolatedFromTheirTouchedNeighbours()
    {
        var points = Points(Instanced(), SyntheticVariableFont.IupGlyph);
        var source = SyntheticVariableFont.IupContours[0];

        for (int i = 0; i < source.Length; i++)
        {
            Assert.Equal(source[i].X + SyntheticVariableFont.IupExpectedDeltaX[i] * WidthScalar, points[i].X, 9);
            Assert.Equal(source[i].Y + SyntheticVariableFont.IupExpectedDeltaY[i] * WidthScalar, points[i].Y, 9);
        }

        // The two answers a broken IUP would give, stated so the fixture cannot go quiet.
        double noInference = source[1].X;                                   // 100
        double nearestAnchor = source[1].X + SyntheticVariableFont.IupTouchedDeltaX[0] * WidthScalar; // also 100
        double farAnchor = source[1].X + SyntheticVariableFont.IupTouchedDeltaX[1] * WidthScalar;     // 130
        Assert.NotEqual(noInference, points[1].X);
        Assert.NotEqual(nearestAnchor, points[1].X);
        Assert.NotEqual(farAnchor, points[1].X);
        Assert.Equal(110, points[1].X, 9);
        Assert.Equal(220, points[2].X, 9);
    }

    /// <summary>
    /// The scalar of a tuple with peaks on TWO axes is their PRODUCT, and the intermediate
    /// tuple beside it is evaluated on its falling flank — 0.375 and 0.5 against the sum
    /// spelling's 1.25, which the assertion states so the mutation cannot pass.
    /// </summary>
    [Fact]
    public void ATuplesScalarIsTheProductOverItsAxes()
    {
        var points = Points(Instanced(), SyntheticVariableFont.ProductGlyph);
        var source = SyntheticVariableFont.ProductContours[0];

        const double product = WeightScalar * WidthScalar;                  // 0.375
        const double intermediate = 0.5;                                    // (1 − 0.75) / (1 − 0.5)
        for (int i = 0; i < source.Length; i++)
        {
            double expected = source[i].X
                + SyntheticVariableFont.ProductDeltaX[i] * product
                + SyntheticVariableFont.IntermediateDeltaX[i] * intermediate;
            Assert.Equal(expected, points[i].X, 9);
        }
        Assert.Equal(337.5, points[1].X, 9);

        double summed = source[1].X
            + SyntheticVariableFont.ProductDeltaX[1] * (WeightScalar + WidthScalar)
            + SyntheticVariableFont.IntermediateDeltaX[1] * intermediate;
        Assert.NotEqual(summed, points[1].X);                               // 225 against 337.5
    }

    /// <summary>
    /// A composite's points are its COMPONENT OFFSETS, each its own one-point contour —
    /// so the tuple moves the second component and leaves the first exactly where it was,
    /// where a translate-the-contour reading would move both.
    /// </summary>
    [Fact]
    public void ACompositesComponentOffsetsVaryIndependently()
    {
        var font = Instanced();
        var rect = Points(font, SyntheticVariableFont.RectGlyph);
        var composite = font.GetGlyph(SyntheticVariableFont.CompositeGlyph);

        Assert.Equal(2, composite.Contours.Count);
        double dx = SyntheticVariableFont.CompositeOffsetX + SyntheticVariableFont.CompositeComponentDeltaX * WeightScalar;
        double dy = SyntheticVariableFont.CompositeOffsetY + SyntheticVariableFont.CompositeComponentDeltaY * WeightScalar;
        Assert.Equal(522.5, dx, 9);
        Assert.Equal(130.0, dy, 9);

        for (int i = 0; i < rect.Length; i++)
        {
            // Component 0 is untouched: its own one-point contour carries no delta.
            Assert.Equal(rect[i].X, composite.Contours[0].Points[i].Position.X, 9);
            Assert.Equal(rect[i].Y, composite.Contours[0].Points[i].Position.Y, 9);
            Assert.Equal(rect[i].X + dx, composite.Contours[1].Points[i].Position.X, 9);
            Assert.Equal(rect[i].Y + dy, composite.Contours[1].Points[i].Position.Y, 9);
        }
    }

    [Fact]
    public void LongGvarOffsetsReadTheSameGeometry()
    {
        var shortForm = Instanced();
        var longForm = Instanced(new SyntheticVariableFontOptions { LongGvarOffsets = true });
        foreach (int glyph in new[] { SyntheticVariableFont.RectGlyph, SyntheticVariableFont.IupGlyph, SyntheticVariableFont.ProductGlyph })
            AssertBitIdentical(shortForm, longForm, glyph);
    }

    // ---- phantom points and HVAR ------------------------------------------------

    /// <summary>The advance-width phantom point: a glyph's advance moves by the
    /// difference of the two phantoms' deltas, so a bolder instance lays out at bolder
    /// spacing.</summary>
    [Fact]
    public void ThePhantomPointVariesTheAdvanceWidth()
    {
        var font = Instanced();
        Assert.Equal(
            SyntheticVariableFont.Advances[SyntheticVariableFont.RectGlyph]
                + SyntheticVariableFont.RectPhantomAdvanceDelta * WeightScalar,
            font.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth, 9);
        Assert.Equal(475, font.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth, 9);
    }

    /// <summary>A glyph that draws NOTHING still carries phantom points, so its advance
    /// varies although its outline cannot.</summary>
    [Fact]
    public void ABlankGlyphsAdvanceStillVaries()
    {
        var glyph = Instanced().GetGlyph(SyntheticVariableFont.SpaceGlyph);
        Assert.True(glyph.IsEmpty);
        Assert.Equal(
            SyntheticVariableFont.Advances[SyntheticVariableFont.SpaceGlyph]
                + SyntheticVariableFont.SpacePhantomAdvanceDelta * WeightScalar,
            glyph.AdvanceWidth, 9);
        Assert.Equal(650, glyph.AdvanceWidth, 9);
    }

    /// <summary>
    /// The varied advance reaches LAYOUT, which is the defect no outline test can see:
    /// two instances lay the same string out to widths that are closed forms of the
    /// font's own deltas.
    /// </summary>
    [Fact]
    public void LayoutWidthFollowsTheVariedAdvance()
    {
        var plain = Load();
        var bold = Instanced();
        double size = SyntheticVariableFont.UnitsPerEm;                     // scale exactly 1

        double plainWidth = TextOutlines.AdvanceWidth("II", plain, size);
        double boldWidth = TextOutlines.AdvanceWidth("II", bold, size);

        Assert.Equal(2.0 * SyntheticVariableFont.Advances[SyntheticVariableFont.RectGlyph], plainWidth, 9);
        Assert.Equal(2.0 * (SyntheticVariableFont.Advances[SyntheticVariableFont.RectGlyph]
            + SyntheticVariableFont.RectPhantomAdvanceDelta * WeightScalar), boldWidth, 9);
        Assert.Equal(950, boldWidth, 9);
    }

    /// <summary>
    /// <c>HVAR</c> SUPERSEDES the phantom points: its deltas deliberately disagree, so
    /// which route wins is observable — and it wins even where it says NOTHING, which is
    /// the assertion with teeth (a font carrying HVAR may omit phantom deltas entirely).
    /// </summary>
    [Fact]
    public void HvarSupersedesThePhantomPoints()
    {
        var font = Instanced(new SyntheticVariableFontOptions { Hvar = true });

        // 'I' maps to the delta-carrying row.
        Assert.Equal(
            SyntheticVariableFont.Advances[SyntheticVariableFont.RectGlyph]
                + SyntheticVariableFont.HvarAdvanceDelta * WeightScalar,
            font.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth, 9);
        Assert.Equal(625, font.GetGlyph(SyntheticVariableFont.RectGlyph).AdvanceWidth, 9);

        // 'space' maps to the zero row: HVAR says the advance does not move, and its
        // gvar phantom delta of +200 is ignored.
        Assert.Equal(SyntheticVariableFont.Advances[SyntheticVariableFont.SpaceGlyph],
            font.GetGlyph(SyntheticVariableFont.SpaceGlyph).AdvanceWidth, 9);

        // A glyph past the end of the delta-set index map takes the LAST entry.
        Assert.Equal(
            SyntheticVariableFont.Advances[SyntheticVariableFont.CompositeGlyph]
                + SyntheticVariableFont.HvarAdvanceDelta * WeightScalar,
            font.GetGlyph(SyntheticVariableFont.CompositeGlyph).AdvanceWidth, 9);

        // Outlines are untouched by HVAR.
        AssertBitIdentical(font, Instanced(), SyntheticVariableFont.RectGlyph);
    }

    // ---- the default instance is bit-identical ---------------------------------

    /// <summary>
    /// The strongest available check: at the DEFAULT coordinate every region's scalar is
    /// zero, so an instanced font must produce outlines BIT-IDENTICAL to the un-instanced
    /// read — a bit comparison, not a tolerance.
    /// </summary>
    [Fact]
    public void TheDefaultInstanceIsBitIdenticalToTheUninstancedRead()
    {
        var plain = Load();
        var defaulted = plain.WithVariation();

        foreach (int glyph in Enumerable.Range(0, SyntheticVariableFont.GlyphCount))
        {
            AssertBitIdentical(plain, defaulted, glyph);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(plain.GetGlyph(glyph).AdvanceWidth),
                BitConverter.DoubleToInt64Bits(defaulted.GetGlyph(glyph).AdvanceWidth));
        }
    }

    /// <summary>The presence of <c>gvar</c> changes nothing at the default, which is the
    /// same claim from the other side.</summary>
    [Fact]
    public void GvarChangesNothingAtTheDefault()
    {
        var withGvar = Load();
        var withoutGvar = Load(new SyntheticVariableFontOptions { Gvar = false });
        foreach (int glyph in Enumerable.Range(0, SyntheticVariableFont.GlyphCount))
            AssertBitIdentical(withGvar, withoutGvar, glyph);
    }

    /// <summary>A font with no <c>fvar</c> is not variable, so its <c>gvar</c> is data
    /// with no design space to read it in and must be ignored rather than applied.</summary>
    [Fact]
    public void GvarWithoutFvarIsIgnored()
    {
        var noAxes = Load(new SyntheticVariableFontOptions { OmitFvar = true });
        var noDeltas = Load(new SyntheticVariableFontOptions { OmitFvar = true, Gvar = false });

        Assert.False(noAxes.IsVariable);
        Assert.Empty(noAxes.VariationAxes);
        Assert.Empty(noAxes.Variation);
        foreach (int glyph in Enumerable.Range(0, SyntheticVariableFont.GlyphCount))
            AssertBitIdentical(noAxes, noDeltas, glyph);
    }

    /// <summary>A static font is unaffected by any of this — its outlines are exactly
    /// what they were.</summary>
    [Fact]
    public void AStaticFontStillReadsItsOwnExactOutlines()
    {
        var font = TrueTypeFont.Load(SyntheticFont.Build());

        Assert.False(font.IsVariable);
        Assert.Empty(font.VariationAxes);
        Assert.Empty(font.NamedInstances);

        var points = font.GetGlyph(SyntheticFont.RectGlyph).Contours[0].Points;
        var expected = SyntheticFont.RectContours[0];
        Assert.Equal(expected.Length, points.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal((double)expected[i].X, points[i].Position.X);
            Assert.Equal((double)expected[i].Y, points[i].Position.Y);
        }
        Assert.Equal(SyntheticFont.Advances[SyntheticFont.RectGlyph],
            font.GetGlyph(SyntheticFont.RectGlyph).AdvanceWidth);
    }

    // ---- the public surface -----------------------------------------------------

    [Fact]
    public void TheReportedVariationIsTheClampedValueAndIsAFixedPoint()
    {
        var font = Load();
        var clamped = font.WithVariation((SyntheticVariations.WeightTag, 5000));

        Assert.Equal(SyntheticVariations.WeightMaximum, clamped.Variation[SyntheticVariations.WeightTag]);
        Assert.Equal(SyntheticVariations.WidthDefault, clamped.Variation[SyntheticVariations.WidthTag]);

        var again = clamped.WithVariation(clamped.Variation);
        AssertBitIdentical(clamped, again, SyntheticVariableFont.RectGlyph);
    }

    [Fact]
    public void AnUnstatedAxisTakesItsDefault()
    {
        var both = Instanced();
        var weightOnly = Load().WithVariation((SyntheticVariations.WeightTag, SyntheticVariations.TestWeight));

        // 'I' varies on weight alone, so stating the width or not cannot move it.
        AssertBitIdentical(both, weightOnly, SyntheticVariableFont.RectGlyph);
        // 'B' varies on width alone, so it must NOT have moved in the weight-only font.
        var plain = Points(Load(), SyntheticVariableFont.IupGlyph);
        var unwidened = Points(weightOnly, SyntheticVariableFont.IupGlyph);
        for (int i = 0; i < plain.Length; i++)
            Assert.Equal(plain[i].X, unwidened[i].X, 12);
    }

    [Fact]
    public void RefusalsNameWhatTheFontCarries()
    {
        var variable = Load();
        var stat1c = TrueTypeFont.Load(SyntheticFont.Build());

        var noAxis = Assert.Throws<FontFormatException>(
            () => variable.WithVariation(("slnt", -10)));
        Assert.Contains("slnt", noAxis.Message);
        Assert.Contains(SyntheticVariations.WeightTag, noAxis.Message);

        var notVariable = Assert.Throws<FontFormatException>(
            () => stat1c.WithVariation((SyntheticVariations.WeightTag, 700)));
        Assert.Contains("fvar", notVariable.Message);

        var noInstance = Assert.Throws<FontFormatException>(() => variable.WithNamedInstance("Ultra"));
        Assert.Contains("Ultra", noInstance.Message);
        Assert.Contains(SyntheticVariations.SemiboldInstance, noInstance.Message);

        var notFinite = Assert.Throws<FontFormatException>(
            () => variable.WithVariation((SyntheticVariations.WeightTag, double.NaN)));
        Assert.Contains(SyntheticVariations.WeightTag, notFinite.Message);
    }

    /// <summary>A varied font IS a font, so <c>Shape.Text</c> takes it with no change —
    /// and the geometry follows the instance.</summary>
    [Fact]
    public void ShapeTextConsumesAVariedFontUnchanged()
    {
        var plain = Shape.Text("I", Load(), size: 100, height: 2);
        var bold = Shape.Text("I", Instanced(), size: 100, height: 2);

        double plainWidth = plain.Bounds().Size.X;
        double boldWidth = bold.Bounds().Size.X;
        // 'I' is 200 units wide by default and 200 + 2·50·0.75 = 275 instanced, at a
        // scale of 100 / 1000.
        Assert.Equal(20.0, plainWidth, 6);
        Assert.Equal(27.5, boldWidth, 6);
    }

    private static void AssertBitIdentical(TrueTypeFont a, TrueTypeFont b, int glyph)
    {
        var left = a.GetGlyph(glyph);
        var right = b.GetGlyph(glyph);
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
                Assert.Equal(lp[i].OnCurve, rp[i].OnCurve);
            }
        }
    }
}
