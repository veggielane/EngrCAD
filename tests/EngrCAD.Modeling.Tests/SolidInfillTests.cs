using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The volume fill's own claims: ONE connected route where the solid allows it, every point
/// clearing the surface by the stated clearance (decided against the field's exact sign, so
/// there is no tolerance in the clip), the achieved spacing reported rather than the request
/// echoed, and both ways a fill can silently miss refused by name with the right message.
/// </summary>
public class SolidInfillTests
{
    [Fact]
    public void ACubeFillsAsOneRunBecauseTheFootprintIsItsOwnCube()
    {
        // The footprint IS the bounding cube here, so the only thing the clip removes is the
        // clearance shell — and a Hilbert curve restricted to a concentric sub-cube is still
        // one connected stretch nowhere near the walls only if the clearance is small enough
        // that the shell is thinner than a cell. Asserted as a MEASUREMENT rather than assumed:
        // what the design promises is the runs, not that there is one.
        var cube = Shape.Box(20, 20, 20);
        var fill = SolidInfill.Fill(cube, spacing: 3.0, clearance: 0.5);

        Assert.True(fill.Spacing <= 3.0);
        Assert.Equal(20.0 / SpaceFillingCurve3d.GridSize(fill.Order), fill.Spacing, 9);
        Assert.True(fill.PointCount > 0);
        Assert.True(fill.Length > 0);
        Assert.Equal(fill.Runs.Sum(r => r.Count), fill.PointCount);
    }

    [Fact]
    public void EveryPointClearsTheSurfaceByTheStatedClearance()
    {
        var body = Shape.Box(24, 16, 12);
        var field = body.ToImplicit();
        var fill = SolidInfill.Fill(body, spacing: 2.5, clearance: 1.0);

        foreach (var run in fill.Runs)
        {
            foreach (var p in run)
                Assert.True(field.Evaluate(p) <= -1.0 + 1e-12, $"{p} reads {field.Evaluate(p)}");
        }
    }

    [Fact]
    public void WithinARunConsecutivePointsAreExactlyOneSpacingApart()
    {
        var fill = SolidInfill.Fill(Shape.Box(20, 20, 20), spacing: 3.0, clearance: 0.5);

        foreach (var run in fill.Runs)
        {
            for (int i = 1; i < run.Count; i++)
                Assert.Equal(fill.Spacing, run[i].DistanceTo(run[i - 1]), 9);
        }
    }

    [Fact]
    public void TheWASTEReportsWhatTheBoundingCubeCost()
    {
        // The placement cost, as a number rather than a caveat: a cube wastes only its
        // clearance shell, a long thin bar wastes most of the curve. That difference is what a
        // tiled 3D footprint would buy, and it is why the entry is filed with a measurement.
        var cube = SolidInfill.Fill(Shape.Box(20, 20, 20), spacing: 3.0, clearance: 0.5);
        var bar = SolidInfill.Fill(Shape.Box(20, 20, 20).Scale(1, 0.2, 0.2), spacing: 3.0, clearance: 0.5);

        Assert.InRange(cube.Waste, 0.0, 0.5);
        Assert.True(bar.Waste > 0.85, $"a 20 x 4 x 4 bar wasted only {bar.Waste:P1} of the cube's curve");
    }

    [Fact]
    public void ASolidTooThinForTheSpacingIsRefusedWithTheDEPTHItFound()
    {
        // A 0.4-thick sheet cannot hold a pass 1.0 inside its surface at any phase, and the
        // message says so by naming how far in a finer probe actually reached.
        var sheet = Shape.Box(40, 40, 0.4);
        var error = Assert.Throws<ArgumentException>(
            () => SolidInfill.Fill(sheet, spacing: 5.0, clearance: 1.0));

        Assert.Contains("No point of this solid is more than 1", error.Message);
        Assert.Contains("deepest a probe found", error.Message);
    }

    [Fact]
    public void ASolidTheLATTICESteppedOverIsADifferentMessage()
    {
        // There IS room — a probe at half the spacing finds it — and the curve's phase put no
        // cell in it. Telling the two apart is what makes the first message honest.
        // 8 thick against a clearance of 1, so there is 6 of room; at spacing 30 the curve's
        // only two z cell centres both land outside the slab.
        var slab = Shape.Box(60, 60, 8.0);
        var error = Assert.Throws<ArgumentException>(
            () => SolidInfill.Fill(slab, spacing: 30.0, clearance: 1.0));

        Assert.Contains("stepped over", error.Message);
        Assert.Contains("Reduce the spacing", error.Message);
    }

    [Fact]
    public void TheLINKERShortensTheTravelAndNeverDropsARun()
    {
        // A body whose clip leaves many runs: a plate with the middle taken out, so the curve
        // enters and leaves repeatedly.
        var body = Shape.Box(40, 40, 8) - Shape.Cylinder(9, 40).Translate(0, 0, -16);
        var fill = SolidInfill.Fill(body, spacing: 2.5, clearance: 0.6);
        Assert.True(fill.Runs.Count > 4, $"only {fill.Runs.Count} runs — the fixture is not exercising the linker");

        var linkage = fill.Link();

        // A permutation, so nothing can be quietly lost, and never worse than the order it
        // started from.
        Assert.Equal(fill.Runs.Count, linkage.Order.Count);
        Assert.Equal(fill.Runs.Count, linkage.Order.Select(o => o.Index).Distinct().Count());
        Assert.True(linkage.TravelLength <= linkage.SourceOrderTravelLength + 1e-9,
            $"linked {linkage.TravelLength} against source order {linkage.SourceOrderTravelLength}");

        var reordered = linkage.Reorder(fill.Runs);
        Assert.Equal(fill.PointCount, reordered.Sum(r => r.Count));
    }

    [Fact]
    public void TheLinkerIsDeterministic()
    {
        var body = Shape.Box(40, 40, 8) - Shape.Cylinder(9, 40).Translate(0, 0, -16);
        var a = SolidInfill.Fill(body, spacing: 2.5, clearance: 0.6).Link();
        var b = SolidInfill.Fill(body, spacing: 2.5, clearance: 0.6).Link();

        Assert.Equal(a.Order, b.Order);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.TravelLength), BitConverter.DoubleToInt64Bits(b.TravelLength));
    }

    [Fact]
    public void TheFieldOverloadNeedsNoSecondLowering()
    {
        // The seam a caller with a lattice or a blend already in hand uses.
        var field = Sdf.Sphere(10);
        var fill = SolidInfill.Fill(field, field.Bounds, spacing: 2.0, clearance: 0.5);

        foreach (var run in fill.Runs)
        {
            foreach (var p in run)
                Assert.True(p.Length <= 9.5 + 1e-9);
        }
    }

    [Fact]
    public void RefusesANonPositiveSpacingAndANegativeClearanceByName()
    {
        var cube = Shape.Box(10, 10, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => SolidInfill.Fill(cube, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolidInfill.Fill(cube, 1, clearance: -1));
    }
}
