using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The 2D opposing-edge thickness, verified against closed forms — and against the ONE reading
/// that separates it from a raw ray cast, which is the perpendicular correction on a slanted
/// wall. Every guard in it is relative, so the scale sweep is part of the verification rather
/// than a nicety (the recorded absolute-epsilon-on-an-area trap).
/// </summary>
public class Region2dThicknessTests
{
    private static Region2d Rectangle(double x0, double y0, double x1, double y1) =>
        new([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)]);

    [Fact]
    public void APlateReadsItsOwnShortSide()
    {
        var plate = Rectangle(0, 0, 40, 6);
        var report = Region2dThickness.Measure([plate]);

        Assert.Equal(6.0, report.Minimum, 9);
        Assert.Equal(0, report.Unmeasurable);
        Assert.Equal(4, report.Samples);          // four edges, one sample each
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void EveryGuardIsRelativeSoTheReadingIsScaleFree(double scale)
    {
        var plate = Rectangle(0, 0, 40 * scale, 6 * scale);
        var report = Region2dThickness.Measure([plate]);

        Assert.Equal(6.0 * scale, report.Minimum, 6.0 * scale * 1e-9);
    }

    [Fact]
    public void ASlantedOppositeReadsThePERPENDICULARWidthRatherThanTheRayLength()
    {
        // A right triangle with legs a and b: probing inward from a leg, the ray to the
        // hypotenuse is longer than the true wall by 1/cos, and the perpendicular distance from
        // the right-angle corner to the hypotenuse is the closed form a*b/hypot(a, b) — the
        // same identity the 3D twin is pinned by.
        const double a = 20, b = 20;
        var triangle = new Region2d([new(0, 0), new(a, 0), new(0, b)]);
        var report = Region2dThickness.Measure([triangle], samplesPerEdge: 8);

        double perpendicular = a * b / Math.Sqrt(a * a + b * b);   // 14.142135623730951
        // The minimum over the boundary is at the two acute corners, where the walls converge —
        // so what this asserts is the CORRECTION rather than the minimum: no probe from a leg
        // may report more than the perpendicular width, which a raw ray length would.
        Assert.True(report.Minimum <= perpendicular + 1e-9,
            $"minimum {report.Minimum} exceeds the perpendicular width {perpendicular}");
        Assert.True(report.Mean <= perpendicular + 1e-9,
            $"mean {report.Mean} exceeds the perpendicular width {perpendicular}, so the ray length "
            + "is being reported instead of the perpendicular distance");
    }

    [Fact]
    public void ItSeesANECKThatAWholePieceConnectivityTestCannot()
    {
        // A dumbbell: two fat 20 x 20 pads joined by a 2-wide bar. The region is ONE connected
        // piece, so any connectivity test calls it fine; the neck is the whole finding.
        var dumbbell = new Region2d(
        [
            new(0, 0), new(20, 0), new(20, 9), new(40, 9), new(40, 0), new(60, 0),
            new(60, 20), new(40, 20), new(40, 11), new(20, 11), new(20, 20), new(0, 20),
        ]);
        var report = Region2dThickness.Measure([dumbbell], samplesPerEdge: 4);

        Assert.Equal(2.0, report.Minimum, 9);
        // And it LOCATES it — the thin place is in the bar, not in either pad.
        Assert.InRange(report.ThinnestAt.X, 20, 40);
    }

    [Fact]
    public void AHoleIsPartOfTheBoundarySoTheWEBBetweenTwoBoresIsMeasured()
    {
        // The web between a bore and the outer wall is a neck like any other, and it is only
        // visible because holes contribute their segments to the same probe set.
        var square = new[] { new Vector2d(0, 0), new(30, 0), new(30, 30), new(0, 30) };
        var bore = new[] { new Vector2d(4, 14), new(4, 16), new(26, 16), new(26, 14) };  // CW
        var plate = new Region2d(square, [bore]);

        var report = Region2dThickness.Measure([plate], samplesPerEdge: 4);

        // The thinnest place is the WEB at the slot's end — 4 wide, between x = 0 and x = 4 —
        // not the 14 of material above and below it. A probe set that omitted the hole's own
        // segments would report 30 and call the plate uniform.
        Assert.Equal(4.0, report.Minimum, 9);
        Assert.InRange(report.ThinnestAt.Y, 14, 16);
    }

    [Fact]
    public void AMeanIsReportedBESIDETheMinimumAndNeverInsteadOfIt()
    {
        var dumbbell = new Region2d(
        [
            new(0, 0), new(20, 0), new(20, 9), new(40, 9), new(40, 0), new(60, 0),
            new(60, 20), new(40, 20), new(40, 11), new(20, 11), new(20, 20), new(0, 20),
        ]);
        var report = Region2dThickness.Measure([dumbbell], samplesPerEdge: 4);

        // The mean is comfortable and the minimum is the defect: reading the mean alone would
        // call this part fine.
        Assert.True(report.Mean > 8, $"mean {report.Mean}");
        Assert.Equal(2.0, report.Minimum, 9);
    }

    [Fact]
    public void AnEmptyInputIsAnInfiniteMinimumRatherThanZero()
    {
        // "Nothing to measure" must not read as "infinitely thin", which is the direction a
        // consumer comparing against a spacing would act on.
        var report = Region2dThickness.Measure([]);
        Assert.Equal(double.PositiveInfinity, report.Minimum);
        Assert.Equal(0, report.Samples);
    }

    [Fact]
    public void RefusesAZeroSampleCountByName()
    {
        var plate = Rectangle(0, 0, 10, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => Region2dThickness.Measure([plate], 0));
    }
}
