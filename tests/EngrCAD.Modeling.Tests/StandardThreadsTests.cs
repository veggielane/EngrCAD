using Xunit;

namespace EngrCAD.Modeling.Tests;

public class StandardThreadsTests
{
    [Fact]
    public void M8_MatchesIso68Dimensions()
    {
        // ISO 724 basic dimensions for M8×1.25: d2 = 7.188, d1 = 6.647 (3 decimals).
        var m8 = StandardThreads.Metric(8);
        Assert.Equal(8.0, m8.MajorDiameter, 12);
        Assert.Equal(1.25, m8.Pitch, 12);
        Assert.Equal(1.25 * Math.Sqrt(3) / 2, m8.FundamentalHeight, 12);
        Assert.Equal(7.188, m8.PitchDiameter, 3);
        Assert.Equal(6.647, m8.MinorDiameter, 3);
        Assert.Equal(6.80, m8.TapDrillDiameter, 12);
        Assert.Equal("M8×1.25", m8.Designation);
    }

    [Theory]
    [InlineData(2.0, 0.40)]
    [InlineData(2.5, 0.45)]
    [InlineData(3.0, 0.50)]
    [InlineData(4.0, 0.70)]
    [InlineData(5.0, 0.80)]
    [InlineData(6.0, 1.00)]
    [InlineData(8.0, 1.25)]
    [InlineData(10.0, 1.50)]
    [InlineData(12.0, 1.75)]
    public void CoarseSeries_FollowsTheBasicProfileFormulas(double size, double pitch)
    {
        var spec = StandardThreads.Metric(size);
        Assert.Equal(pitch, spec.Pitch, 12);

        // H = (√3/2)P and the ISO 68-1 truncations: d2 = d − (3/4)H, d1 = d − (5/4)H,
        // depth 5H/8, crest flat P/8, root flat P/4.
        double h = Math.Sqrt(3) / 2 * pitch;
        Assert.Equal(size - 0.75 * h, spec.PitchDiameter, 12);
        Assert.Equal(size - 1.25 * h, spec.MinorDiameter, 12);
        Assert.Equal(0.625 * h, spec.ThreadDepth, 12);
        Assert.Equal(pitch / 8, spec.CrestFlatWidth, 12);
        Assert.Equal(pitch / 4, spec.RootFlatWidth, 12);

        // Sanity ordering: tap drill sits between the minor and major diameters
        // (it truncates the internal thread's crest, never the whole thread).
        Assert.InRange(spec.TapDrillDiameter, spec.MinorDiameter - 1e-9, spec.MajorDiameter);
        // Tap drill ≈ d − P (standard charts round to a stock drill within 0.06).
        Assert.True(Math.Abs(spec.TapDrillDiameter - (size - pitch)) < 0.06 + 1e-12);
    }

    [Fact]
    public void CustomSpec_DefaultsAndValidation()
    {
        var custom = new ThreadSpec(7, 1.0);
        Assert.Equal(6.0, custom.TapDrillDiameter, 12); // d − P default

        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadSpec(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadSpec(8, -1));
        // Minor diameter must stay positive: d ≤ (5/4)H rejected.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadSpec(1.0, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadSpec(8, 1.25, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadSpec(8, 1.25, 0));
    }

    [Fact]
    public void UnknownSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardThreads.Metric(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardThreads.Metric(16));
    }
}
