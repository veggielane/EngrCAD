using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Laser cutting: one outward offset gives every beam path with the kerf compensation
/// already right (outer loops out into the waste, hole loops into the holes), holes cut
/// first, the G-code GRBL's M4 flavour with no Z anywhere, verified through the decoder.
/// </summary>
public class CncLaserTests
{
    private static Region2d PlateWithHole()
    {
        var hole = new[]
        {
            new Vector2d(15, 5), new Vector2d(15, 15),
            new Vector2d(25, 15), new Vector2d(25, 5),
        };
        return new Region2d(
            [new Vector2d(0, 0), new Vector2d(40, 0), new Vector2d(40, 20), new Vector2d(0, 20)],
            [hole]);
    }

    [Fact]
    public void TheKerfCompensation_IsOneOutwardOffset_WithTheClosedFormPerimeter()
    {
        // A 40×20 part at kerf 0.2: the outer beam path is the rectangle offset +0.1 with
        // round corners — perimeter 2(40+20) + 2π·0.1 — and the hole path is the hole
        // shrunk by 0.1: 2(10+10) − 8·0.1 (an inward rectangle offset keeps sharp corners).
        var cut = CncLaser.Cut(PlateWithHole(), new LaserTool(KerfWidth: 0.2));

        Assert.Equal(2, cut.Passes.Count);
        double holeLength = cut.Passes[0].CutLength;
        double outerLength = cut.Passes[1].CutLength;
        Assert.Equal(2 * (10 + 10) - 8 * 0.1, holeLength, 0.001);
        Assert.Equal(2 * (40 + 20) + 2 * Math.PI * 0.1, outerLength, 0.01);

        // Holes FIRST — the release rule: the first pass sits inside the hole's box.
        Assert.All(cut.Passes[0].Points, p =>
        {
            Assert.InRange(p.X, 15, 25);
            Assert.InRange(p.Y, 5, 15);
        });
    }

    [Fact]
    public void TheGcode_IsGrblLaserFlavour_ReadByTheDecoder()
    {
        var cut = CncLaser.Cut(PlateWithHole(), new LaserTool(KerfWidth: 0.2, Power: 750));
        string gcode = CncLaser.WriteGcode(cut);

        Assert.Contains("M4 S750", gcode);
        Assert.Contains("M5", gcode);
        // A laser has no depth axis: no Z word anywhere in the program.
        Assert.DoesNotContain("Z", gcode.Split('\n').Where(l => !l.StartsWith(';'))
            .Aggregate("", (a, l) => a + l));

        // The decoder reads it (M4/S are modes): the non-rapid XY length IS the cut length.
        var decoded = GcodeReader.Read(gcode);
        double cutLength = decoded.Moves.Where(m => !m.Rapid).Sum(m => m.XyLength);
        // The writer quantizes coordinates at 3 decimals (a micron), so the decoded
        // length agrees to that grade, not to round-off.
        Assert.Equal(cut.CutLength, cutLength, cut.CutLength * 1e-4);
        // And travels between passes are rapids — beam off under M4.
        Assert.Contains(decoded.Moves, m => m.Rapid && m.XyLength > 1);
    }

    [Fact]
    public void MultiPass_RepeatsEachLoop_AndTheRefusalsName()
    {
        var one = CncLaser.Cut(PlateWithHole(), new LaserTool(KerfWidth: 0.2, Passes: 1));
        var three = CncLaser.Cut(PlateWithHole(), new LaserTool(KerfWidth: 0.2, Passes: 3));
        double L1 = GcodeReader.Read(CncLaser.WriteGcode(one))
            .Moves.Where(m => !m.Rapid).Sum(m => m.XyLength);
        double L3 = GcodeReader.Read(CncLaser.WriteGcode(three))
            .Moves.Where(m => !m.Rapid).Sum(m => m.XyLength);
        Assert.Equal(3 * L1, L3, L3 * 1e-9);

        Assert.Contains("KerfWidth", Assert.Throws<ArgumentException>(() =>
            new LaserTool(KerfWidth: 0).Validate()).Message);
        Assert.Contains("S word", Assert.Throws<ArgumentException>(() =>
            new LaserTool(Power: 1500).Validate()).Message);
        Assert.Contains("Passes", Assert.Throws<ArgumentException>(() =>
            new LaserTool(Passes: 0).Validate()).Message);
    }
}
