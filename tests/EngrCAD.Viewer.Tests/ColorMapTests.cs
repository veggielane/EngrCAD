using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

public class ColorMapTests
{
    // Golden endpoints. These ARE the tables' contract: a map whose ends moved would
    // change every legend and every plot ever rendered against it, so they are pinned
    // rather than described.
    [Fact]
    public void Viridis_EndsAtItsPublishedExtremes()
    {
        var low = ColorMaps.Sample(FieldColorMap.Viridis, 0);
        var high = ColorMaps.Sample(FieldColorMap.Viridis, 1);

        Assert.Equal((0.267f, 0.005f, 0.329f), low);      // dark blue-purple
        Assert.Equal((0.993f, 0.906f, 0.144f), high);     // yellow
    }

    [Fact]
    public void Diverging_HasANeutralGreyMidpoint()
    {
        var mid = ColorMaps.Sample(FieldColorMap.Diverging, 0.5);
        Assert.Equal((0.865f, 0.865f, 0.865f), mid);
        // Blue at the bottom, red at the top — a signed quantity's two directions.
        var low = ColorMaps.Sample(FieldColorMap.Diverging, 0);
        var high = ColorMaps.Sample(FieldColorMap.Diverging, 1);
        Assert.True(low.B > low.R, "the diverging map's low end must be blue");
        Assert.True(high.R > high.B, "the diverging map's high end must be red");
    }

    [Fact]
    public void Sample_ClampsOutsideTheUnitInterval()
    {
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 0),
                     ColorMaps.Sample(FieldColorMap.Viridis, -5));
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 1),
                     ColorMaps.Sample(FieldColorMap.Viridis, 12));
        // NaN takes the low end rather than producing a NaN colour (the !(t > 0) test).
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 0),
                     ColorMaps.Sample(FieldColorMap.Viridis, double.NaN));
    }

    [Fact]
    public void Sample_LandsExactlyOnEveryStop()
    {
        foreach (var map in (FieldColorMap[])[FieldColorMap.Viridis, FieldColorMap.Diverging])
        {
            var stops = ColorMaps.Stops(map);
            int count = ColorMaps.StopCount(map);
            for (int i = 0; i < count; i++)
            {
                var sampled = ColorMaps.Sample(map, (double)i / (count - 1));
                Assert.Equal(stops[i * 3], sampled.R, 6);
                Assert.Equal(stops[i * 3 + 1], sampled.G, 6);
                Assert.Equal(stops[i * 3 + 2], sampled.B, 6);
            }
        }
    }

    [Fact]
    public void Sample_InterpolatesBetweenStops()
    {
        var stops = ColorMaps.Stops(FieldColorMap.Viridis);
        int count = ColorMaps.StopCount(FieldColorMap.Viridis);
        double half = 0.5 / (count - 1);              // halfway between stop 0 and stop 1
        var mid = ColorMaps.Sample(FieldColorMap.Viridis, half);
        Assert.Equal((stops[0] + stops[3]) / 2, mid.R, 5);
        Assert.Equal((stops[1] + stops[4]) / 2, mid.G, 5);
        Assert.Equal((stops[2] + stops[5]) / 2, mid.B, 5);
    }

    [Fact]
    public void Viridis_IsMonotoneInLightness()
    {
        // The property viridis is CHOSEN for: it reads in greyscale and under
        // colour-vision deficiency because perceived lightness only ever increases.
        // Sampling the table asserts the transcription kept it, which a table typo
        // would break without changing either endpoint.
        double previous = double.NegativeInfinity;
        for (int i = 0; i <= 64; i++)
        {
            var (r, g, b) = ColorMaps.Sample(FieldColorMap.Viridis, i / 64.0);
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            Assert.True(luma > previous - 1e-6,
                $"viridis lightness fell at t = {i / 64.0:F3} ({luma:F4} after {previous:F4})");
            previous = luma;
        }
    }

    [Fact]
    public void Sample_ThroughARange_AgreesWithNormalizeThenSample()
    {
        // One call so the fills, the legend and any probe readout cannot disagree about
        // a value's colour: it must be exactly the two-step composition.
        var range = new FieldRange(10, 50);
        foreach (double value in (double[])[5, 10, 22.5, 30, 50, 90])
        {
            Assert.Equal(
                ColorMaps.Sample(FieldColorMap.Viridis, range.Normalize(value)),
                ColorMaps.Sample(FieldColorMap.Viridis, range, value));
        }
    }
}
