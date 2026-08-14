using EngrCAD.Cam;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// The G-code writer and its twin decoder. The check with teeth is the EXTRUSION BOOKKEEPING
/// IDENTITY re-derived from the decoded values — <c>FilamentUsed == deposition length ×
/// BeadArea / FilamentArea</c> — which a structural look at the file cannot see and which any
/// unit slip, lost segment or E-mode bug breaks. Plus: the decoded layer grid, matched
/// retract/unretract pairs, write-only-when-stated temperatures, and the mode refusals by name
/// (inches, relative coordinates, relative extrusion, arcs).
/// </summary>
public sealed class GcodeTests
{
    private static PrinterProfile Profile() => new(
        LayerHeight: 0.25, InfillDensity: 0.3, HotendTemperature: 0, BedTemperature: 0);

    [Fact]
    public void TheExtrusionBookkeeping_IsAnIdentity_ThroughTheDecoder()
    {
        var part = FdmSlicer.Slice(Shape.Box(15, 10, 4), Profile());
        var decoded = GcodeReader.Read(GcodeWriter.Write(part));

        // The decoder re-derives both sides from the file alone: the deposition length from the
        // coordinates, the filament from the E deltas. They must agree with the slice's own
        // bookkeeping to formatting precision (3 dp coordinates, 5 dp cumulative E).
        Assert.Equal(part.DepositionLength, decoded.DepositionLength,
            part.DepositionLength * 1e-3);
        Assert.Equal(part.FilamentUsed, decoded.FilamentUsed, part.FilamentUsed * 1e-3);

        // And the identity itself, entirely on DECODED values.
        double p = part.Profile.BeadArea / part.Profile.FilamentArea;
        Assert.Equal(decoded.DepositionLength * p, decoded.FilamentUsed,
            decoded.FilamentUsed * 1e-3);

        // The decoded layer grid is the slice's.
        Assert.Equal(part.Layers.Count, decoded.LayerZs.Count);
        for (int i = 0; i < part.Layers.Count; i++)
            Assert.Equal(part.Layers[i].Z, decoded.LayerZs[i], 3);
    }

    [Fact]
    public void Retraction_FiresOnIslandHops_AndPairsUp()
    {
        // Two towers 8 mm apart: the hop between them exceeds MinTravelForRetraction, the hops
        // between a tower's own walls do not.
        var towers = Shape.Box(4, 4, 2).Translate(-6, 0, 0) | Shape.Box(4, 4, 2).Translate(6, 0, 0);
        var profile = Profile() with { InfillDensity = 0, WallCount = 1 };
        var decoded = GcodeReader.Read(GcodeWriter.Write(FdmSlicer.Slice(towers, profile)));
        Assert.True(decoded.RetractCount > 0);
        // The final end-of-print retract has no unretract; every travel retract pairs up.
        Assert.Equal(decoded.RetractCount - 1, decoded.UnretractCount);

        // Retraction off: no retracts at all.
        var off = GcodeReader.Read(GcodeWriter.Write(
            FdmSlicer.Slice(towers, profile with { RetractionLength = 0 })));
        Assert.Equal(0, off.RetractCount);
        Assert.Equal(0, off.UnretractCount);
    }

    [Fact]
    public void Temperatures_AreWriteOnlyWhenStated()
    {
        var part = FdmSlicer.Slice(Shape.Box(5, 5, 1), Profile());
        string silent = GcodeWriter.Write(part);
        Assert.DoesNotContain("M104", silent);
        Assert.DoesNotContain("M140", silent);

        var hot = FdmSlicer.Slice(Shape.Box(5, 5, 1),
            Profile() with { HotendTemperature = 205, BedTemperature = 60 });
        string heated = GcodeWriter.Write(hot);
        Assert.Contains("M104 S205", heated);
        Assert.Contains("M109 S205", heated);
        Assert.Contains("M140 S60", heated);
        Assert.Contains("M190 S60", heated);
        Assert.Contains("M104 S0", heated);                   // and cooled at the end
    }

    [Fact]
    public void TheDecoder_RefusesTheModesItMustNotGuessAbout()
    {
        Assert.Contains("INCHES",
            Assert.Throws<FormatException>(() => GcodeReader.Read("G20\nG1 X1 Y1")).Message);
        Assert.Contains("RELATIVE",
            Assert.Throws<FormatException>(() => GcodeReader.Read("G91\nG1 X1")).Message);
        Assert.Contains("M83",
            Assert.Throws<FormatException>(() => GcodeReader.Read("M83\nG1 E5")).Message);
        Assert.Contains("arc",
            Assert.Throws<FormatException>(() => GcodeReader.Read("G2 X1 Y1 I0.5 J0")).Message);
    }

    [Fact]
    public void TheDecoder_TreatsDirtAsNotes_NeverThrows()
    {
        var program = GcodeReader.Read("""
            ; a comment line
            M117 hello printer
            G21
            G90
            G4 P100
            G1 X5 Y0 E0.5 F1200
            banana
            G1 X5 Y5 E1.0
            """);
        Assert.Equal(2, program.Moves.Count(m => m.Kind == GcodeMoveKind.Deposition));
        Assert.Equal(1.0, program.FilamentUsed, 12);
        Assert.Equal(10.0, program.DepositionLength, 12);
        Assert.Contains(program.Notes, n => n.Contains('G') || n.Contains("ignored"));
    }

    [Fact]
    public void G92_ResetsTheEAxis_AndTheIdentityStillHolds()
    {
        var program = GcodeReader.Read("""
            G21
            G90
            M82
            G92 E0
            G1 X10 Y0 E1 F1200
            G92 E0
            G1 X10 Y10 E1
            """);
        Assert.Equal(2.0, program.FilamentUsed, 12);
        Assert.Equal(20.0, program.DepositionLength, 12);
    }
}
