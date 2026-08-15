using System.Text.RegularExpressions;
using EngrCAD.Cam;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Per-feature speeds and the print-time estimator. The speeds claim is structural — a
/// profile stating one speed differs from the baseline ONLY in its F words — and the
/// estimator's trapezoid is closed-form arithmetic asserted directly, with the
/// infinite-acceleration limit collapsing the bracket.
/// </summary>
public class PrintTimeTests
{
    private static Shape Plate() =>
        Shape.Box(8, 8, 3) | Shape.Box(20, 15, 3).Translate(0, 0, 3); // a slab overhanging its column

    private static PrinterProfile Base => new(
        NozzleDiameter: 0.8, LayerHeight: 0.5, WallCount: 1, InfillDensity: 0.2,
        TopSolidLayers: 1, BottomSolidLayers: 1, SupportOverhangAngle: 45);

    [Fact]
    public void StatedSpeeds_ChangeOnlyTheFWords()
    {
        string baseline = GcodeWriter.Write(FdmSlicer.Slice(Plate(), Base));
        string tuned = GcodeWriter.Write(FdmSlicer.Slice(Plate(), Base with
        {
            WallSpeed = 30,
            InfillSpeed = 60,
            SolidInfillSpeed = 45,
            FirstLayerSpeed = 15,
        }));
        Assert.NotEqual(baseline, tuned);
        static string StripF(string gcode) => Regex.Replace(gcode, @" F\d+", "");
        Assert.Equal(StripF(baseline), StripF(tuned));

        // Unstated speeds are the write-only-when-stated path: byte-identical.
        Assert.Equal(baseline, GcodeWriter.Write(FdmSlicer.Slice(Plate(), Base with
        {
            WallSpeed = null,
            InfillSpeed = null,
        })));
    }

    [Fact]
    public void DecodedFeeds_MatchTheRoleRule()
    {
        var profile = Base with
        {
            WallSpeed = 30,
            InfillSpeed = 60,
            SolidInfillSpeed = 45,
            SupportSpeed = 80,
            FirstLayerSpeed = 15,
        };
        var decoded = GcodeReader.Read(GcodeWriter.Write(FdmSlicer.Slice(Plate(), profile)));
        var feeds = decoded.Moves
            .Where(m => !m.Rapid && m.DeltaE > 0 && m.XyLength > 0)
            .Select(m => m.Feed).Distinct().OrderBy(f => f).ToList();
        // 15 (first layer), 30 (walls), 45 (skins), 60 (sparse), 80 (supports) mm/s.
        Assert.Equal([900, 1800, 2700, 3600, 4800], feeds);

        // A solid skin with no stated skin speed falls back through the infill family.
        var fallback = GcodeReader.Read(GcodeWriter.Write(FdmSlicer.Slice(
            Plate(), Base with { InfillSpeed = 60 })));
        Assert.DoesNotContain(fallback.Moves, m => m.Feed == 2700);
        Assert.Contains(fallback.Moves, m => m.Feed == 3600);
    }

    [Fact]
    public void TheTrapezoid_IsClosedFormArithmetic()
    {
        // One 10 mm move at 60 mm/s: at a = 100 the move stays TRIANGULAR
        // (v²/a = 36 > 10), so max = 2·√(10/100); min is the feed's own 10/60.
        var program = GcodeReader.Read("G21\nG90\nM82\nG1 X10 Y0 F3600 E0.1\n");
        var estimate = PrintTime.Estimate(program, acceleration: 100);
        Assert.Equal(10.0 / 60, estimate.MinSeconds, 12);
        Assert.Equal(2 * Math.Sqrt(10.0 / 100), estimate.MaxSeconds, 12);

        // A 100 mm move reaches full speed (v²/a = 36 ≤ 100): max = d/v + v/a.
        var reaches = GcodeReader.Read("G21\nG90\nM82\nG1 X100 Y0 F3600 E1\n");
        var full = PrintTime.Estimate(reaches, acceleration: 100);
        Assert.Equal(100.0 / 60 + 60.0 / 100, full.MaxSeconds, 12);

        // The infinite-acceleration limit collapses the bracket onto the lower bound.
        var instant = PrintTime.Estimate(reaches, acceleration: 1e12);
        Assert.Equal(instant.MinSeconds, instant.MaxSeconds, 6);

        // A retract is an E-axis move: distance |ΔE| at the retract feed.
        var retract = GcodeReader.Read("G21\nG90\nM82\nG1 X10 F3600 E0.1\nG1 E-0.9 F600\n");
        var withRetract = PrintTime.Estimate(retract, acceleration: 1e12);
        Assert.Equal(10.0 / 60 + 1.0 / 10, withRetract.MinSeconds, 6);
    }

    [Fact]
    public void ARealSlice_BracketsSensibly()
    {
        var decoded = GcodeReader.Read(GcodeWriter.Write(FdmSlicer.Slice(Plate(), Base)));
        var brisk = PrintTime.Estimate(decoded, acceleration: 3000);
        var sluggish = PrintTime.Estimate(decoded, acceleration: 300);
        Assert.True(brisk.MinSeconds > 0);
        Assert.True(brisk.MinSeconds <= brisk.MaxSeconds);
        Assert.Equal(brisk.MinSeconds, sluggish.MinSeconds, 12); // the lower bound is accel-free
        Assert.True(sluggish.MaxSeconds > brisk.MaxSeconds,
            "a slower machine must take longer at the upper bound");

        Assert.Contains("acceleration", Assert.Throws<ArgumentException>(
            () => PrintTime.Estimate(decoded, acceleration: 0)).Message);
    }
}
