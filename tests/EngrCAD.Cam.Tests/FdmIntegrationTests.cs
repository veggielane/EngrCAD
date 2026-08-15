using EngrCAD.Cam;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// The integration wave: custom G-code snippets, the per-role filament split, fuzzy skin
/// and multi-part plating.
/// </summary>
public class FdmIntegrationTests
{
    private static Shape Plate2() => Shape.Box(20, 15, 4) - Shape.Cylinder(3, 10);

    [Fact]
    public void Snippets_SubstituteAndPlace_AndUnsetIsByteIdentical()
    {
        var profile = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, InfillDensity: 0,
            StartGcode: "M117 printing\nG29 ; level",
            LayerChangeGcode: "; layer {layer} at z {z}",
            EndGcode: "M117 done after {layer}");
        string gcode = GcodeWriter.Write(FdmSlicer.Slice(Plate2(), profile));

        Assert.Contains("M117 printing\nG29 ; level\n", gcode);
        int change = gcode.IndexOf("; layer 3 at z 0\n", StringComparison.Ordinal);
        Assert.True(change > gcode.IndexOf(";LAYER:3\n", StringComparison.Ordinal));
        Assert.Contains("M117 done after 7\n", gcode);
        Assert.True(gcode.IndexOf("G29", StringComparison.Ordinal)
            < gcode.IndexOf(";LAYER:0", StringComparison.Ordinal));

        var plain = new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, InfillDensity: 0);
        Assert.Equal(
            GcodeWriter.Write(FdmSlicer.Slice(Plate2(), plain)),
            GcodeWriter.Write(FdmSlicer.Slice(Plate2(),
                plain with { StartGcode = null, EndGcode = null, LayerChangeGcode = null })));
    }

    [Fact]
    public void FilamentByRole_SplitsTheTotalExactly()
    {
        var sliced = FdmSlicer.Slice(
            Shape.Box(4, 10, 8).Translate(0, 0, 4) | Shape.Box(20, 10, 2).Translate(0, 0, 9),
            new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, InfillDensity: 0.2,
                TopSolidLayers: 1, BottomSolidLayers: 1, SupportOverhangAngle: 45,
                IroningFlow: 0.2));
        var byRole = sliced.FilamentByRole;
        Assert.Contains(SlicePathRole.Wall, byRole.Keys);
        Assert.Contains(SlicePathRole.Support, byRole.Keys);
        Assert.Contains(SlicePathRole.SolidInfill, byRole.Keys);
        Assert.Contains(SlicePathRole.Ironing, byRole.Keys);
        Assert.Equal(sliced.FilamentUsed, byRole.Values.Sum(), sliced.FilamentUsed * 1e-9);

        // And the flow-aware total matches the decoder — the identity, generalised.
        var decoded = GcodeReader.Read(GcodeWriter.Write(sliced));
        Assert.Equal(sliced.FilamentUsed, decoded.FilamentUsed, sliced.FilamentUsed * 1e-3);
    }

    [Fact]
    public void FuzzySkin_JittersTheOuterWallOnly_Deterministically()
    {
        var plain = FdmSlicer.Slice(Plate2(),
            new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, WallCount: 2, InfillDensity: 0));
        var fuzzed = FdmSlicer.Slice(Plate2(),
            new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, WallCount: 2, InfillDensity: 0,
                FuzzySkinThickness: 0.3));

        // The fuzzed outer wall stays within half the thickness of the plain wall, and
        // genuinely moves; inner walls and layer 0 are untouched bit-for-bit.
        var plainOuter = plain.Layers[3].Paths.First(
            p => p.Role == SlicePathRole.Wall && p.WallIndex == 0);
        var fuzzedOuter = fuzzed.Layers[3].Paths.First(
            p => p.Role == SlicePathRole.Wall && p.WallIndex == 0);
        double worst = 0;
        foreach (var q in fuzzedOuter.Points)
        {
            double d = DistanceToLoop(plainOuter.Points, q);
            Assert.True(d <= 0.15 + 1e-9, $"fuzz escaped the thickness band ({d:0.####})");
            worst = Math.Max(worst, d);
        }
        Assert.True(worst > 0.03, "the skin must actually fuzz");

        var plainInner = plain.Layers[3].Paths.First(
            p => p.Role == SlicePathRole.Wall && p.WallIndex == 1);
        var fuzzedInner = fuzzed.Layers[3].Paths.First(
            p => p.Role == SlicePathRole.Wall && p.WallIndex == 1);
        Assert.Equal(plainInner.Points, fuzzedInner.Points);
        Assert.Equal(
            plain.Layers[0].Paths.First(p => p.WallIndex == 0).Points,
            fuzzed.Layers[0].Paths.First(p => p.WallIndex == 0).Points);

        Assert.Equal(
            GcodeWriter.Write(fuzzed),
            GcodeWriter.Write(FdmSlicer.Slice(Plate2(),
                new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5, WallCount: 2, InfillDensity: 0,
                    FuzzySkinThickness: 0.3))));
    }

    [Fact]
    public void APlate_SlicesAsIslands_AndRefusesWhenFull()
    {
        var plate = FdmPlating.Plate(
            [Shape.Box(20, 15, 4), Shape.Box(15, 15, 6), Shape.Cylinder(8, 5)],
            bedWidth: 120, bedDepth: 120, gap: 6);
        var sliced = FdmSlicer.Slice(plate, new PrinterProfile(NozzleDiameter: 0.8, LayerHeight: 0.5,
            WallCount: 1, InfillDensity: 0, BrimWidth: 2));

        // Three islands on the first layer, every part rested on the bed plane.
        Assert.Equal(3, sliced.Layers[0].Regions.Count);
        Assert.Equal(0, plate.Bounds().Min.Z, 9);
        // Different heights: the tallest island reaches the last layer alone.
        Assert.Single(sliced.Layers[^1].Regions);

        Assert.ThrowsAny<Exception>(() => FdmPlating.Plate(
            [Shape.Box(90, 90, 5), Shape.Box(90, 90, 5)], 100, 100, gap: 5));
    }

    private static double DistanceToLoop(IReadOnlyList<Vector2d> loop, in Vector2d p)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            var d = b - a;
            double len2 = d.Dot(d);
            double t = len2 > 0 ? Math.Clamp((p - a).Dot(d) / len2, 0, 1) : 0;
            best = Math.Min(best, (p - (a + d * t)).Length);
        }
        return best;
    }
}
