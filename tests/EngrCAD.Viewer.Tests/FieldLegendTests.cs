using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

public class FieldLegendTests
{
    private static ResolvedFieldDisplay Display(
        FieldColorMap map = FieldColorMap.Viridis, MeshField? deform = null, double scale = 1) =>
        new(MeshField.Scalar("von Mises", "MPa", [0, 50, 120]),
            new FieldRange(0, 120), map, deform, scale, true);

    [Fact]
    public void Build_LaysOutTheBandsOutlineTicksAndLabels()
    {
        var legend = FieldLegend.Build(Display(), 800, 600);

        Assert.True(legend.HasContent);
        Assert.Equal(FieldLegend.Bands, legend.BandCount);
        Assert.Equal(FieldLegend.Bands * FieldLegend.VerticesPerBand * 3, legend.BandVertices.Length);
        // Four outline segments plus one tick per label = 4 + Ticks segments, 2 vertices each.
        Assert.Equal((4 + FieldLegend.Ticks) * 2, legend.FrameVertexCount);
        Assert.True(legend.LabelVertexCount > 0, "the tick numbers and title must produce strokes");
    }

    [Fact]
    public void Build_BandColorsRunTheMapBottomToTop()
    {
        var legend = FieldLegend.Build(Display(), 800, 600);

        // The bar's bottom is the range minimum; each band shows its own MIDPOINT's
        // colour, so the ramp is symmetric about the bar's ends.
        for (int b = 0; b < legend.BandCount; b++)
        {
            Assert.Equal(
                ColorMaps.Sample(FieldColorMap.Viridis, (b + 0.5) / FieldLegend.Bands),
                legend.BandColors[b]);
        }
        Assert.NotEqual(legend.BandColors[0], legend.BandColors[^1]);
    }

    [Fact]
    public void Build_BandsStackUpwardsWithoutGapsOrOverlaps()
    {
        var legend = FieldLegend.Build(Display(), 800, 600);
        float Bottom(int b) => legend.BandVertices[b * FieldLegend.VerticesPerBand * 3 + 1];
        float Top(int b) => legend.BandVertices[b * FieldLegend.VerticesPerBand * 3 + 3 * 2 + 1];

        for (int b = 0; b < legend.BandCount; b++)
            Assert.True(Top(b) > Bottom(b), $"band {b} has no height");
        for (int b = 1; b < legend.BandCount; b++)
            Assert.Equal(Top(b - 1), Bottom(b), 3);
    }

    [Fact]
    public void Build_ScalesWithThePixelScale()
    {
        var one = FieldLegend.Build(Display(), 1600, 1200);
        var two = FieldLegend.Build(Display(), 1600, 1200, pixelScale: 2);

        // The bar is twice as tall in device pixels at twice the scale — the same
        // correction point sprites and annotation text make, so the widget keeps its
        // apparent size on a high-DPI display or in a supersampled offscreen pass.
        float Height(FieldLegendGeometry g) =>
            g.BandVertices[(g.BandCount - 1) * FieldLegend.VerticesPerBand * 3 + 7] - g.BandVertices[1];
        Assert.Equal(Height(one) * 2, Height(two), 2);
    }

    [Fact]
    public void Build_ReturnsEmptyWhenTheViewportIsTooSmall()
    {
        Assert.False(FieldLegend.Fits(80, 400, 1));
        Assert.False(FieldLegend.Fits(400, 60, 1));
        Assert.False(FieldLegend.Build(Display(), 80, 400).HasContent);
        Assert.Empty(FieldLegend.Build(Display(), 80, 400).BandColors);
    }

    [Fact]
    public void Projection_MapsPixelCornersToClipCorners()
    {
        var projection = FieldLegend.Projection(800, 600);
        var bottomLeft = projection.TransformPoint(new Vector3d(0, 0, 0));
        var topRight = projection.TransformPoint(new Vector3d(800, 600, 0));

        Assert.Equal(-1, bottomLeft.X, 9);
        Assert.Equal(-1, bottomLeft.Y, 9);
        Assert.Equal(1, topRight.X, 9);
        Assert.Equal(1, topRight.Y, 9);
    }

    [Fact]
    public void Title_IsUppercaseAndCarriesUnits()
    {
        Assert.Equal("VON MISES [MPA]", FieldLegend.Title(Display()));
    }

    [Fact]
    public void Title_StatesTheDeformationScale()
    {
        // A deformed plot whose exaggeration is not stated is a picture of a shape that
        // does not exist.
        var deform = MeshField.Vector("u", "mm", [Vector3d.UnitZ]);
        Assert.Contains("60X DEFORMED", FieldLegend.Title(Display(deform: deform, scale: 60)));
    }

    [Fact]
    public void EveryLabelCharacterIsInTheStrokeFont()
    {
        // The font is uppercase-only and an unmapped character advances as a BLANK, so a
        // label the font cannot draw comes out as a silent gap rather than an error.
        var display = Display(deform: MeshField.Vector("u", "mm", [Vector3d.UnitZ]), scale: 1500);
        var texts = new List<string> { FieldLegend.Title(display) };
        for (int t = 0; t < FieldLegend.Ticks; t++)
        {
            double f = (double)t / (FieldLegend.Ticks - 1);
            texts.Add(FieldLegend.Format(display.Range.Min + display.Range.Span * f));
        }
        texts.Add(FieldLegend.Format(-1.2345e-7));
        texts.Add(FieldLegend.Format(9.87e12));

        foreach (string text in texts)
        {
            foreach (char c in text)
                Assert.True(StrokeFont.TryGetStrokes(c, out _), $"'{c}' in \"{text}\" has no glyph");
        }
    }

    [Fact]
    public void Format_IsInvariantAndFourSignificantDigits()
    {
        Assert.Equal("1234", FieldLegend.Format(1234.0));
        Assert.Equal("0.1235", FieldLegend.Format(0.12345));
        Assert.Equal("-12.35", FieldLegend.Format(-12.345));
    }

    // ---- Log-scale displays (a field whose units declare log10 values) ----

    private static ResolvedFieldDisplay LogDisplay(double min, double max) =>
        new(MeshField.Scalar("Fatigue life", "log10(cycles)", [min, max]),
            new FieldRange(min, max), FieldColorMap.Viridis, null, 1, true);

    [Fact]
    public void TryLogUnits_ReadsTheProducersDeclaration()
    {
        // The convention FatigueResults established: the units string IS the flag.
        Assert.True(FieldLegend.TryLogUnits("log10(cycles)", out var units));
        Assert.Equal("cycles", units);
        Assert.True(FieldLegend.TryLogUnits("LOG10(Cycles)", out units));
        Assert.Equal("Cycles", units);

        Assert.False(FieldLegend.TryLogUnits("MPa", out _));
        Assert.False(FieldLegend.TryLogUnits("", out _));
        Assert.False(FieldLegend.TryLogUnits(null, out _));
        Assert.False(FieldLegend.TryLogUnits("log10()", out _));   // declares log of nothing
        Assert.False(FieldLegend.TryLogUnits("log10(", out _));
        Assert.False(FieldLegend.TryLogUnits("log10 cycles", out _));
    }

    [Fact]
    public void TickMarks_LinearDisplay_KeepsTheIncumbentFiveEvenTicks()
    {
        // The linear path must be bit-identical to what Build always did: same
        // fractions, same Format of min + span*f — the docs PNGs hang off it.
        var display = Display();
        var ticks = FieldLegend.TickMarks(display);

        Assert.Equal(FieldLegend.Ticks, ticks.Length);
        for (int t = 0; t < ticks.Length; t++)
        {
            double f = (double)t / (FieldLegend.Ticks - 1);
            Assert.Equal(f, ticks[t].Fraction);
            Assert.Equal(
                FieldLegend.Format(display.Range.Min + display.Range.Span * f),
                ticks[t].Label);
        }
    }

    [Fact]
    public void TickMarks_LogDisplay_PlacesDecadeTicksWithAntiloggedLabels()
    {
        // Eight decades: 9 integer decades step by ceil(9/6) = 2, the ends print the
        // (anti-logged) range bounds, and the decades AT the ends are dropped so the
        // labels cannot overlap.
        var ticks = FieldLegend.TickMarks(LogDisplay(0, 8));

        Assert.Equal(
            new[] { (0.0, "1"), (0.25, "100"), (0.5, "1E+04"), (0.75, "1E+06"), (1.0, "1E+08") },
            ticks);
    }

    [Fact]
    public void TickMarks_LogDisplay_EndsCarryTheTrueRange()
    {
        // A legend that hides its endpoints lies about its range: the ends are the
        // anti-logged min/max even when they are not round decades.
        var ticks = FieldLegend.TickMarks(LogDisplay(0.3, 4.7));

        Assert.Equal((0.0, FieldLegend.Format(Math.Pow(10, 0.3))), ticks[0]);
        Assert.Equal((1.0, FieldLegend.Format(Math.Pow(10, 4.7))), ticks[^1]);
        Assert.Equal(
            new[] { "10", "100", "1000", "1E+04" },
            ticks[1..^1].Select(t => t.Label).ToArray());
    }

    [Fact]
    public void TickMarks_LogDisplay_DropsADecadeUnderAnEndLabel()
    {
        // Decade 1 sits 0.016 of the bar above the min tick — inside the clearance —
        // and decade 4 IS the max tick; both yield to the end labels.
        var ticks = FieldLegend.TickMarks(LogDisplay(0.95, 4));

        Assert.Equal(4, ticks.Length);
        Assert.DoesNotContain("10", ticks.Select(t => t.Label));
        Assert.Equal(
            new[] { "100", "1000" },
            ticks[1..^1].Select(t => t.Label).ToArray());
    }

    [Fact]
    public void TickMarks_LogDisplay_UnderTwoDecades_FallsBackToEvenSpacing()
    {
        // An interval under two decades may hold as few as no interior decades — too few
        // ticks to describe a range — so the even five-tick layout stays, anti-logged.
        var ticks = FieldLegend.TickMarks(LogDisplay(4.2, 5.8));

        Assert.Equal(FieldLegend.Ticks, ticks.Length);
        for (int t = 0; t < ticks.Length; t++)
            Assert.Equal((double)t / (FieldLegend.Ticks - 1), ticks[t].Fraction);
        Assert.Equal("1E+05", ticks[2].Label);   // 10^5.0, not "5"
    }

    [Fact]
    public void Title_LogDisplay_StatesTheBaseUnitsAndTheScale()
    {
        // The ticks print anti-logged values in the base units, so a title still saying
        // LOG10(CYCLES) would make a "1E+05" tick read as ten to the 100000th.
        Assert.Equal("FATIGUE LIFE [CYCLES, LOG SCALE]", FieldLegend.Title(LogDisplay(0, 8)));
    }

    [Fact]
    public void Build_LogDisplay_DrawsOneTickSegmentPerTickMark()
    {
        var display = LogDisplay(0.3, 4.7);
        var legend = FieldLegend.Build(display, 800, 600);

        int tickCount = FieldLegend.TickMarks(display).Length;
        Assert.Equal((4 + tickCount) * 2, legend.FrameVertexCount);
        Assert.True(legend.LabelVertexCount > 0);
    }

    [Fact]
    public void TickMarks_LogScaleFlag_PrintsTheSameDecadeTicksAsTheUnitsDeclaration()
    {
        // The flag carries RAW values where the units convention carries logged ones;
        // the same data spelled both ways must print the same ticks, or the two
        // spellings drift apart.
        var flag = new ResolvedFieldDisplay(
            MeshField.Scalar("Fatigue life", "cycles", [1, 1e8]),
            new FieldRange(1, 1e8), FieldColorMap.Viridis, null, 1, true, LogScale: true);

        Assert.Equal(FieldLegend.TickMarks(LogDisplay(0, 8)), FieldLegend.TickMarks(flag));
    }

    [Fact]
    public void Title_LogScaleFlag_TagsTheScaleInTheFieldsOwnUnits()
    {
        var flag = new ResolvedFieldDisplay(
            MeshField.Scalar("Fatigue life", "cycles", [1, 1e8]),
            new FieldRange(1, 1e8), FieldColorMap.Viridis, null, 1, true, LogScale: true);
        Assert.Equal("FATIGUE LIFE [CYCLES, LOG SCALE]", FieldLegend.Title(flag));

        var unitless = new ResolvedFieldDisplay(
            MeshField.Scalar("count", "", [1, 100]),
            new FieldRange(1, 100), FieldColorMap.Viridis, null, 1, true, LogScale: true);
        Assert.Equal("COUNT [LOG SCALE]", FieldLegend.Title(unitless));
    }

    [Fact]
    public void Build_AFieldWithNaN_GainsTheNoValueSwatch()
    {
        var display = new ResolvedFieldDisplay(
            MeshField.Scalar("life", "cycles", [double.NaN, 50, 120]),
            new FieldRange(50, 120), FieldColorMap.Viridis, null, 1, true);
        var legend = FieldLegend.Build(display, 800, 600);

        // One extra band (the swatch), grey, with four more outline segments and a
        // NO VALUE label; the colour ramp itself is untouched.
        Assert.Equal(FieldLegend.Bands + 1, legend.BandCount);
        Assert.Equal(ColorMaps.NoValueColor, legend.BandColors[^1]);
        Assert.Equal((4 + 4 + FieldLegend.Ticks) * 2, legend.FrameVertexCount);
        Assert.Equal(
            FieldLegend.Build(Display(), 800, 600).BandColors[..FieldLegend.Bands],
            legend.BandColors[..FieldLegend.Bands]);
    }

    [Fact]
    public void Build_AFiniteField_CarriesNoSwatchAndIsUnchanged()
    {
        // The swatch appears exactly when a no-value node exists; a finite field's
        // legend is bit-identical to what it always was.
        var legend = FieldLegend.Build(Display(), 800, 600);
        Assert.Equal(FieldLegend.Bands, legend.BandCount);
        Assert.Equal((4 + FieldLegend.Ticks) * 2, legend.FrameVertexCount);
    }

    [Fact]
    public void HasNoValue_ReadsNaNAndLogScaleNonPositives()
    {
        var finite = new ResolvedFieldDisplay(
            MeshField.Scalar("s", "", [1, 2, 3]),
            new FieldRange(1, 3), FieldColorMap.Viridis, null, 1, true);
        Assert.False(FieldLegend.HasNoValue(finite));

        var nan = finite with { Field = MeshField.Scalar("s", "", [1, double.NaN, 3]) };
        Assert.True(FieldLegend.HasNoValue(nan));

        // A zero is an ordinary value on a linear display and a no-value one under
        // LogScale — the association is with the DISPLAY, not the field.
        var zero = finite with { Field = MeshField.Scalar("s", "", [0, 2, 3]) };
        Assert.False(FieldLegend.HasNoValue(zero));
        Assert.True(FieldLegend.HasNoValue(zero with { LogScale = true }));
    }

    [Fact]
    public void Build_TwoDistinctDisplays_StacksTwoBars_FirstOnTop()
    {
        var second = new ResolvedFieldDisplay(
            MeshField.Scalar("temperature", "K", [300, 400]),
            new FieldRange(300, 400), FieldColorMap.Diverging, null, 1, true);
        var legend = FieldLegend.Build([Display(), second], 800, 700);

        // Two full ramps, two outlines-with-ticks, and the FIRST display reads on top
        // (list order is draw order, and a stack reads top to bottom).
        Assert.Equal(2 * FieldLegend.Bands, legend.BandCount);
        Assert.Equal(2 * (4 + FieldLegend.Ticks) * 2, legend.FrameVertexCount);
        float firstBottom = legend.BandVertices[1];
        float secondBottom = legend.BandVertices[(FieldLegend.Bands * FieldLegend.VerticesPerBand + 0) * 3 + 1];
        Assert.True(firstBottom > secondBottom,
            $"first display's bar ({firstBottom}) must sit above the second's ({secondBottom})");
        // The upper bar's colours are the first display's map, the lower the second's.
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 0.5 / FieldLegend.Bands),
            legend.BandColors[0]);
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Diverging, 0.5 / FieldLegend.Bands),
            legend.BandColors[FieldLegend.Bands]);
    }

    [Fact]
    public void Build_ASingleDisplay_KeepsTheIncumbentCentredLayout()
    {
        // The centring expression is pinned so the delegating single overload cannot
        // drift: one bar sits at (height - barHeight) / 2 exactly.
        var legend = FieldLegend.Build(Display(), 800, 600);
        Assert.Equal((float)((600 - FieldLegend.BarHeightDip) / 2), legend.BandVertices[1]);
    }

    [Fact]
    public void Build_MoreDisplaysThanFit_KeepsWhatFits()
    {
        // Height 600 fits at most floor((600 - 40 + 34)/(170 + 34)) = 2 bars.
        var displays = new List<ResolvedFieldDisplay>();
        for (int i = 0; i < 3; i++)
            displays.Add(new ResolvedFieldDisplay(
                MeshField.Scalar($"f{i}", "", [0, i + 1]),
                new FieldRange(0, i + 1), FieldColorMap.Viridis, null, 1, true));

        var legend = FieldLegend.Build(displays, 800, 600);
        Assert.Equal(2 * FieldLegend.Bands, legend.BandCount);
        // A taller viewport takes all three.
        Assert.Equal(3 * FieldLegend.Bands,
            FieldLegend.Build(displays, 800, 900).BandCount);
    }

    [Fact]
    public void EveryLogLabelCharacterIsInTheStrokeFont()
    {
        var display = LogDisplay(-3.2, 8.6);
        var texts = new List<string> { FieldLegend.Title(display) };
        texts.AddRange(FieldLegend.TickMarks(display).Select(t => t.Label));

        foreach (string text in texts)
        {
            foreach (char c in text.ToUpperInvariant())
                Assert.True(StrokeFont.TryGetStrokes(c, out _), $"'{c}' in \"{text}\" has no glyph");
        }
    }
}
