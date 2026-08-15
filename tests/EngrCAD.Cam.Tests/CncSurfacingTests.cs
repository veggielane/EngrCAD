using EngrCAD.Cam;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// 3-axis surfacing. The oracles are the implicit engine's own: a flat top's raster is EXACT
/// (a box field is exact, so every interior tip lands on the face), the dome apex is touched at
/// its own height, a vertical cylinder's waterline is a circle of radius R + r to round-off
/// (the in-plane Newton polish), the gouge claim is the SDF inequality point-by-point, and the
/// scallop arithmetic is a chord identity its approximation is measured against.
/// </summary>
public class CncSurfacingTests
{
    private static readonly MillTool Ball = new(Diameter: 4);

    [Fact]
    public void FlatTop_RasterIsExact_AndSerpentine()
    {
        var plate = Shape.Box(20, 15, 4);
        var op = CncSurfacing.Raster(plate, Ball, sampleStep: 1);

        int interior = 0;
        foreach (var pass in op.Passes)
        {
            foreach (var p in pass.Points)
            {
                if (Math.Abs(p.X) <= 9.9 && Math.Abs(p.Y) <= 7.4)
                {
                    Assert.Equal(2, p.Z, 6);
                    interior++;
                }
            }
        }
        Assert.True(interior > 100, "the plate must actually be sampled");

        // Serpentine: each row starts where its neighbour ended (columns are grid-anchored,
        // so alternate rows run the same x range in opposite directions).
        for (int i = 1; i < op.Passes.Count; i++)
            Assert.Equal(op.Passes[i - 1].Points[^1].X, op.Passes[i].Points[0].X, 12);
    }

    [Fact]
    public void DomeApex_IsTouchedAtItsOwnHeight()
    {
        // The global grid anchors a sample at exactly (0, 0), over the apex of Sphere(8):
        // the ball touches the apex, so the tip z there is the apex's own height.
        var dome = Shape.Sphere(8);
        var op = CncSurfacing.Raster(dome, Ball, sampleStep: 1);
        var apex = op.Passes.SelectMany(p => p.Points)
            .Single(p => Math.Abs(p.X) < 1e-9 && Math.Abs(p.Y) < 1e-9);
        Assert.Equal(8, apex.Z, 6);
    }

    [Fact]
    public void Raster_IsGougeFreeByTheFieldsOwnInequality()
    {
        // A dome on a plate — every ball CENTRE must read at least r from the part's own
        // field: the no-gouge claim as an SDF inequality, point by point.
        var part = Shape.Box(24, 24, 6) | Shape.Sphere(6).Translate(0, 0, 3);
        var op = CncSurfacing.Raster(part, Ball, sampleStep: 0.8);
        var sdf = part.ToImplicit();
        double r = Ball.Radius;
        int count = 0;
        foreach (var p in op.Passes.SelectMany(pass => pass.Points))
        {
            Assert.True(sdf.Evaluate(new Vector3d(p.X, p.Y, p.Z + r)) >= r - 1e-6,
                $"ball centre over ({p.X:0.##}, {p.Y:0.##}) reads "
                + $"{sdf.Evaluate(new Vector3d(p.X, p.Y, p.Z + r)):0.######} < r = {r}");
            count++;
        }
        Assert.True(count > 500, "the fixture must actually exercise the inequality");
    }

    [Fact]
    public void CylinderWaterline_IsACircleAtRadiusPlusToolRadius()
    {
        // A vertical wall is what waterline exists for, and there the in-plane Newton polish
        // makes the contour exact: every CL point at hypot(x, y) = R + r to round-off.
        var cylinder = Shape.Cylinder(5, 12);
        var op = CncSurfacing.Waterline(cylinder, Ball, sampleStep: 0.5);

        var closed = op.Passes.Where(p => p.IsClosed).ToList();
        Assert.Equal(6, closed.Count); // height 12 at StepDown 2, the last at the bottom
        Assert.Equal(op.Passes.Count, closed.Count); // no open waterline on a closed wall

        var tips = closed.Select(p => p.Points[0].Z).OrderByDescending(z => z).ToList();
        Assert.Equal(4, tips[0], 9);
        Assert.Equal(-6, tips[^1], 9);

        foreach (var pass in closed)
        {
            foreach (var p in pass.Points)
                Assert.Equal(7, Math.Sqrt(p.X * p.X + p.Y * p.Y), 6);
            // A waterline pass is CONSTANT z by definition.
            Assert.All(pass.Points, p => Assert.Equal(pass.Points[0].Z, p.Z));
        }
    }

    [Fact]
    public void ScallopArithmetic_IsTheChordIdentity()
    {
        // Exact: h = r − √(r² − (s/2)²); the classic s²/8r is its small-stepover expansion.
        double exact = CncSurfacing.ScallopHeight(3, 1);
        Assert.Equal(3 - Math.Sqrt(9 - 0.25), exact, 15);
        double approx = 1.0 / (8 * 3);
        Assert.True(Math.Abs(exact - approx) / exact < 0.01,
            $"the s²/8r approximation should sit within 1% at s = r/3 (exact {exact}, approx {approx})");

        // The inverse round-trips through the same chord.
        Assert.Equal(1, CncSurfacing.StepoverForScallop(3, exact), 12);

        Assert.Contains("stepover", Assert.Throws<ArgumentException>(
            () => CncSurfacing.ScallopHeight(3, 6)).Message);
        Assert.Contains("scallopHeight", Assert.Throws<ArgumentException>(
            () => CncSurfacing.StepoverForScallop(3, 4)).Message);
    }

    [Fact]
    public void Waterline_IsGougeFreeToo()
    {
        var part = Shape.Box(24, 24, 6) | Shape.Sphere(6).Translate(0, 0, 3);
        var op = CncSurfacing.Waterline(part, Ball, sampleStep: 0.6);
        var sdf = part.ToImplicit();
        double r = Ball.Radius;
        int count = 0;
        foreach (var p in op.Passes.SelectMany(pass => pass.Points))
        {
            // Marching squares can land a point the crossing error inside the isolevel where
            // the polish declines (a near-horizontal crossing); the tolerance is that grade.
            Assert.True(sdf.Evaluate(new Vector3d(p.X, p.Y, p.Z + r)) >= r - 2e-3,
                $"waterline centre at ({p.X:0.##}, {p.Y:0.##}, {p.Z + r:0.##}) gouges");
            count++;
        }
        Assert.True(count > 200);
    }

    [Fact]
    public void Surfacing_IsDeterministic_ThroughTheWriter()
    {
        var part = Shape.Box(24, 24, 6) | Shape.Sphere(6).Translate(0, 0, 3);
        string First() => CncGcodeWriter.Write(
            [CncSurfacing.Raster(part, Ball), CncSurfacing.Waterline(part, Ball)], safeZ: 15);
        Assert.Equal(First(), First());
    }

    [Fact]
    public void UnusableSampling_RefusesByName()
    {
        var plate = Shape.Box(10, 10, 4);
        Assert.Contains("sampleStep", Assert.Throws<ArgumentException>(
            () => CncSurfacing.Raster(plate, Ball, sampleStep: 0)).Message);
        Assert.Contains("sampleStep", Assert.Throws<ArgumentException>(
            () => CncSurfacing.Waterline(plate, Ball, sampleStep: -1)).Message);
    }
}
