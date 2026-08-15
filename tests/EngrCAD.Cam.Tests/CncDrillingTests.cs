using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// The drill program derived from the model's own hole declarations (the one-declaration
/// rule at the CAM boundary), and the rotated raster grid (quarter turns exact).
/// </summary>
public class CncDrillingTests
{
    private static Shape Plate() => Shape.Box(60, 40, 8)
        .Drill(StandardHoles.Clearance(5), [(-20, 10), (-20, -10), (20, 10), (20, -10)],
            depth: 10)
        .ThreadedHole(StandardThreads.Metric(6), [(0, 10), (0, -10)], depth: 6);

    [Fact]
    public void TheProgram_GroupsByDrillDiameter_ReadingTheModelsOwnRows()
    {
        var ops = CncDrilling.FromShape(Plate());

        // Two distinct drills, ascending: the M6 tap pilot (Ø5) and the M5 clearance
        // bore (Ø5.5) — read from the SAME rows the drawing's hole table letters.
        Assert.Equal(2, ops.Count);
        Assert.Equal(5.0, ops[0].Tool.Diameter, 12);
        Assert.Equal(5.5, ops[1].Tool.Diameter, 12);
        Assert.Equal(2, ops[0].Passes.Count);
        Assert.Equal(4, ops[1].Passes.Count);
        Assert.Equal(
            HoleTable.For(Plate()).HoleCount,
            ops.Sum(o => o.Passes.Count));

        // Depths verbatim (to the shoulder, the drill-cycle convention): the bed frame is
        // the placement plane, so tips reach exactly −depth.
        Assert.Equal(-6, ops[0].Passes.SelectMany(p => p.Points).Min(p => p.Z), 12);
        Assert.Equal(-10, ops[1].Passes.SelectMany(p => p.Points).Min(p => p.Z), 12);

        // And the program rides the canned-cycle writer: one G83 per hole.
        string canned = CncGcodeWriter.Write(ops, cannedDrilling: true);
        Assert.Equal(6, canned.Split('\n').Count(l => l.StartsWith("G83")));
    }

    [Fact]
    public void AddingAHole_AddsExactlyOnePassToItsDiametersOperation()
    {
        var more = Shape.Box(60, 40, 8)
            .Drill(StandardHoles.Clearance(5),
                [(-20, 10), (-20, -10), (20, 10), (20, -10), (0, 0)], depth: 10)
            .ThreadedHole(StandardThreads.Metric(6), [(0, 10), (0, -10)], depth: 6);
        var baseline = CncDrilling.FromShape(Plate());
        var extended = CncDrilling.FromShape(more);
        Assert.Equal(baseline[1].Passes.Count + 1, extended[1].Passes.Count);
        Assert.Equal(baseline[0].Passes.Count, extended[0].Passes.Count);
    }

    [Fact]
    public void ExtraDepthAndPeck_AreTheCallers_AndAShapeWithNoHolesIsAnEmptyProgram()
    {
        var ops = CncDrilling.FromShape(Plate(), peck: 0, extraDepth: 1.5);
        Assert.Equal(-7.5, ops[0].Passes.SelectMany(p => p.Points).Min(p => p.Z), 12);
        Assert.All(ops.SelectMany(o => o.Passes), p => Assert.Single(p.Points));

        Assert.Empty(CncDrilling.FromShape(Shape.Box(10, 10, 10)));
    }

    [Fact]
    public void ATiltedPlacementPlane_RefusesNamingTheRow()
    {
        var side = Shape.Box(30, 30, 30)
            .Drill(StandardHoles.Clearance(5), [(0, 0)], depth: 5,
                plane: new SketchPlane(Frame3d.FromNormal(
                    new Vector3d(15, 0, 0), new Vector3d(1, 0, 0))));
        Assert.Contains("tilted plane", Assert.Throws<ArgumentException>(() =>
            CncDrilling.FromShape(side)).Message);
        Assert.Contains("A", Assert.Throws<ArgumentException>(() =>
            CncDrilling.FromShape(side)).Message);
    }

    [Fact]
    public void ANinetyDegreeRaster_IsTheTransposedGrid_ToTheLastBit()
    {
        // A quarter turn is a sign swap, never a cos: on a rotationally symmetric dome the
        // 90° raster's points are the 0° raster's with (x, y) swapped, bit for bit.
        var dome = Shape.Sphere(10);
        var tool = new MillTool(8, StepDown: 2);
        var at0 = CncSurfacing.Raster(dome, tool);
        var at90 = CncSurfacing.Raster(dome, tool, rasterAngleDegrees: 90);

        Assert.Equal(at0.Passes.Count, at90.Passes.Count);
        var swapped = at0.Passes.SelectMany(p => p.Points)
            .Select(p => (p.Y, p.X, p.Z)).OrderBy(p => p).ToList();
        var rotated = at90.Passes.SelectMany(p => p.Points)
            .Select(p => (p.X, p.Y, p.Z)).OrderBy(p => p).ToList();
        Assert.Equal(swapped, rotated);

        // Every 90° pass runs along Y: constant X within a pass.
        Assert.All(at90.Passes, pass =>
            Assert.All(pass.Points, p => Assert.Equal(pass.Points[0].X, p.X)));

        // An oblique angle still covers the part: same sample-count order of magnitude and
        // rows along the stated direction.
        var at30 = CncSurfacing.Raster(dome, tool, rasterAngleDegrees: 30);
        var first = at30.Passes[0].Points;
        var direction = (first[^1] - first[0]);
        Assert.Equal(Math.Tan(30 * Math.PI / 180), direction.Y / direction.X, 9);
    }
}
