using EngrCAD.Cam;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// The print-process features: cooling slowdown, fan, the volumetric cap, z-hop, seam
/// placement, wall order and spiral vase — each verified through the decoder or on the
/// slice's own paths, with the unset profile byte-identical.
/// </summary>
public class FdmProcessTests
{
    private static Shape Plate() => Shape.Box(20, 15, 4) - Shape.Cylinder(3, 10);

    [Fact]
    public void Cooling_SlowsShortLayers_AndLeavesLongOnesAlone()
    {
        var tiny = Shape.Box(5, 5, 3);
        var cooled = GcodeReader.Read(GcodeWriter.Write(FdmSlicer.Slice(tiny,
            new PrinterProfile(LayerHeight: 0.25, PrintSpeed: 60, MinLayerTime: 30))));
        var feeds = cooled.Moves.Where(m => !m.Rapid && m.DeltaE > 0 && m.XyLength > 0)
            .Select(m => m.Feed).Distinct().ToList();
        Assert.All(feeds, f => Assert.True(f < 3600,
            $"a layer far quicker than MinLayerTime must slow down (saw F{f})"));
        Assert.All(feeds, f => Assert.True(f >= 10 * 60 - 1, "the MinPrintSpeed floor holds"));

        // A generous minimum leaves an already-slow-enough layer untouched.
        var untouched = GcodeReader.Read(GcodeWriter.Write(FdmSlicer.Slice(tiny,
            new PrinterProfile(LayerHeight: 0.25, PrintSpeed: 60, MinLayerTime: 0.001))));
        Assert.Contains(untouched.Moves, m => m.Feed == 3600);
    }

    [Fact]
    public void Fan_TurnsOnAfterTheStatedLayers_AndOnlyWhenStated()
    {
        string gcode = GcodeWriter.Write(FdmSlicer.Slice(Plate(),
            new PrinterProfile(LayerHeight: 0.25, FanSpeed: 0.8, FanOffLayers: 2)));
        int fanOn = gcode.IndexOf("M106 S204\n", StringComparison.Ordinal);
        Assert.True(fanOn > gcode.IndexOf(";LAYER:2\n", StringComparison.Ordinal));
        Assert.True(fanOn < gcode.IndexOf(";LAYER:3\n", StringComparison.Ordinal));
        Assert.Contains("M107", gcode);

        string silent = GcodeWriter.Write(FdmSlicer.Slice(Plate(),
            new PrinterProfile(LayerHeight: 0.25)));
        Assert.DoesNotContain("M106", silent);
        Assert.DoesNotContain("M107", silent);
    }

    [Fact]
    public void TheVolumetricCap_IsAHardCeilingOnEveryDeposition()
    {
        var profile = new PrinterProfile(LayerHeight: 0.25, PrintSpeed: 100,
            MaxVolumetricFlow: 2);
        double capSpeed = 2 / profile.BeadArea;
        Assert.True(capSpeed < 100, "the fixture must actually engage the cap");

        var decoded = GcodeReader.Read(GcodeWriter.Write(FdmSlicer.Slice(Plate(), profile)));
        int expected = (int)Math.Round(capSpeed * 60);
        Assert.All(decoded.Moves.Where(m => !m.Rapid && m.DeltaE > 0 && m.XyLength > 0),
            m => Assert.Equal(expected, m.Feed));
    }

    [Fact]
    public void ZHop_LiftsRetractedTravels_AndTheIdentityHolds()
    {
        var profile = new PrinterProfile(LayerHeight: 0.25, InfillDensity: 0.3, ZHop: 0.4);
        string gcode = GcodeWriter.Write(FdmSlicer.Slice(Plate(), profile));
        var decoded = GcodeReader.Read(gcode);

        // Hops appear as paired up/down z travels around retracted island hops.
        Assert.Contains(decoded.Moves,
            m => m.Rapid && m.XyLength == 0 && Math.Abs(m.To.Z - m.From.Z - 0.4) < 1e-9);
        Assert.Contains(decoded.Moves,
            m => m.Rapid && m.XyLength == 0 && Math.Abs(m.From.Z - m.To.Z - 0.4) < 1e-9);
        Assert.Equal(decoded.RetractCount - 1, decoded.UnretractCount);

        double identity = decoded.DepositionLength * profile.BeadArea / profile.FilamentArea;
        Assert.Equal(identity, decoded.FilamentUsed, identity * 1e-3);
    }

    [Fact]
    public void SeamPlacement_RotatesClosedWalls()
    {
        var rear = FdmSlicer.Slice(Plate(),
            new PrinterProfile(LayerHeight: 0.25, Seam: SeamPosition.Rear));
        int checkedLoops = 0;
        foreach (var path in rear.Layers[3].Paths.Where(p => p.Role == SlicePathRole.Wall))
        {
            double maxY = path.Points.Max(q => q.Y);
            Assert.Equal(maxY, path.Points[0].Y, 12);
            checkedLoops++;
        }
        Assert.True(checkedLoops >= 3); // outer shells + the bore's wall

        var aligned = FdmSlicer.Slice(Plate(),
            new PrinterProfile(LayerHeight: 0.25, Seam: SeamPosition.Aligned));
        var anchor = new Vector2d(10, 0); // bounds.Max.X, mid Y
        foreach (var path in aligned.Layers[3].Paths.Where(p => p.Role == SlicePathRole.Wall))
        {
            double nearest = path.Points.Min(q => (q - anchor).Length);
            Assert.Equal(nearest, (path.Points[0] - anchor).Length, 12);
        }

        // Seams line up vertically: the outer wall starts at the same XY on every layer.
        var starts = aligned.Layers.Select(l =>
            l.Paths.First(p => p.Role == SlicePathRole.Wall && p.WallIndex == 0).Points[0]).ToList();
        Assert.All(starts, s => Assert.Equal(0, (s - starts[0]).Length, 9));
    }

    [Fact]
    public void ExternalPerimetersFirst_InvertsTheWallOrder()
    {
        var inside = FdmSlicer.Slice(Plate(), new PrinterProfile(LayerHeight: 0.25, WallCount: 2));
        Assert.Equal(1, inside.Layers[2].Paths.First(p => p.Role == SlicePathRole.Wall).WallIndex);

        var outside = FdmSlicer.Slice(Plate(), new PrinterProfile(LayerHeight: 0.25, WallCount: 2,
            ExternalPerimetersFirst: true));
        Assert.Equal(0, outside.Layers[2].Paths.First(p => p.Role == SlicePathRole.Wall).WallIndex);
    }

    [Fact]
    public void SpiralVase_RampsContinuously_AndRefusesWhatItMust()
    {
        var vaseProfile = new PrinterProfile(LayerHeight: 0.3, WallCount: 1, InfillDensity: 0,
            SpiralVase: true, BottomSolidLayers: 2, RetractionLength: 0);
        var sliced = FdmSlicer.Slice(Shape.Cylinder(8, 12), vaseProfile);
        var decoded = GcodeReader.Read(GcodeWriter.Write(sliced));

        // Above the base the deposition z never steps back: one continuous spiral.
        // (The cylinder is origin-centred: z runs -6..6, the base ends at -6 + 2 layers.)
        double previous = -6;
        int climbing = 0;
        foreach (var move in decoded.Moves.Where(
            m => m.DeltaE > 0 && m.XyLength > 0 && m.To.Z > -6 + 2 * 0.3 + 1e-9))
        {
            Assert.True(move.To.Z >= previous - 1e-9,
                $"vase z stepped back from {previous:0.####} to {move.To.Z:0.####}");
            previous = move.To.Z;
            climbing++;
        }
        Assert.True(climbing > 100, "the spiral must actually be exercised");
        Assert.Equal(6, previous, 6); // the spiral ends exactly at the part's top

        double identity = decoded.DepositionLength
            * vaseProfile.BeadArea / vaseProfile.FilamentArea;
        Assert.Equal(identity, decoded.FilamentUsed, identity * 1e-3);

        // Refusals by name: contradictory settings, and a layer the spiral cannot be.
        Assert.Contains("one wall", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Cylinder(8, 12), vaseProfile with { WallCount = 2 })).Message);
        Assert.Contains("infill", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Cylinder(8, 12),
                vaseProfile with { InfillDensity = 0.2 })).Message);
        Assert.Contains("single island", Assert.Throws<ArgumentException>(() =>
            FdmSlicer.Slice(Shape.Box(6, 6, 12).Translate(-8, 0, 0)
                | Shape.Box(6, 6, 12).Translate(8, 0, 0), vaseProfile)).Message);
    }
}
