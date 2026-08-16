using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Cam.Tests;

/// <summary>
/// Adaptive stepover: the row spacing follows the surface so the scallop stays at the
/// stated height. The closed forms are the chord identity's own — on a FLAT plate every
/// spacing is exactly the uniform <c>StepoverForScallop</c>, and on a plane tilted by 45°
/// exactly cos 45° times it, because the 3D distance between corresponding CL points on a
/// tilted plane is the row spacing over cos θ and the chord identity is exact there.
/// </summary>
public class CncAdaptiveRasterTests
{
    private static IReadOnlyList<double> RowYs(MillOperation op) =>
        op.Passes.Select(p => p.Points[0].Y).ToList();

    [Fact]
    public void OnAFlatPlate_EverySpacing_IsExactlyTheUniformStepover()
    {
        var plate = Shape.Box(40, 30, 6).Translate(0, 0, -3);
        double h = 0.05;
        var op = CncSurfacing.AdaptiveRaster(plate, new MillTool(6), h, sampleStep: 2);

        double flat = CncSurfacing.StepoverForScallop(3, h);
        var ys = RowYs(op);
        Assert.True(ys.Count > 10);
        // Interior pairs (both rows over the part, away from the rim wrap) take the full
        // flat spacing — the fast path, no bisection, so the value is exact.
        for (int i = 1; i < ys.Count; i++)
            if (ys[i - 1] > -14 && ys[i] < 14)
                Assert.Equal(flat, ys[i] - ys[i - 1], 12);
        // Coverage: first a tool radius below the part, last exactly one above.
        Assert.Equal(-18.0, ys[0], 12);
        Assert.Equal(18.0, ys[^1], 9);
    }

    // A prism whose top is ONE 45° plane across the whole y-range: a right triangle in
    // the sketch plane, extruded along z and rotated so the hypotenuse's slope runs in y
    // (z_top = 15 − y over y ∈ [0, 30]) — no boolean, no ridge, no second slope.
    private static Shape Ramp() =>
        Shape.Extrude(
            Sketch.Polygon([new(-15, 0), new(15, 0), new(-15, 30)]), 40)
            .RotateY(-Math.PI / 2);

    [Fact]
    public void OnAFortyFiveDegreeSlope_TheSpacing_IsExactlyCosFortyFive()
    {
        double h = 0.05;
        var op = CncSurfacing.AdaptiveRaster(Ramp(), new MillTool(6), h, sampleStep: 2);

        double flat = CncSurfacing.StepoverForScallop(3, h);
        double expected = flat * Math.Cos(Math.PI / 4);
        var ys = RowYs(op);
        // Pairs wholly on the slope, clear of the top corner (y = 0), the toe (y = 30)
        // and the ball's own wrap of each: the CL distance there is dy·√2, so the
        // bisection lands on flat·cos45.
        int measured = 0;
        for (int i = 1; i < ys.Count; i++)
            if (ys[i - 1] > 5 && ys[i] < 20)
            {
                Assert.Equal(expected, ys[i] - ys[i - 1], 5);
                measured++;
            }
        Assert.True(measured >= 8, $"only {measured} interior slope pairs measured");
        // And the slope pairs are genuinely TIGHTER than the flat spacing — the feature.
        Assert.True(expected < flat * 0.72);
    }

    [Fact]
    public void ACliff_FloorsTheSpacing_AndTheMarchStillCompletes()
    {
        // A tall boss wall: no spacing meets the scallop target across the cliff, so the
        // march floors at flat/32 there and moves on rather than stalling.
        var part = Shape.Box(40, 30, 6).Translate(0, 0, -3)
            .Union(Shape.Box(40, 10, 12).Translate(0, 0, 6));
        double h = 0.05;
        var op = CncSurfacing.AdaptiveRaster(part, new MillTool(6), h, sampleStep: 2);

        double flat = CncSurfacing.StepoverForScallop(3, h);
        var ys = RowYs(op);
        // Completes the whole span…
        Assert.Equal(18.0, ys[^1], 9);
        // …with at least one floored pair at the wall and the count bounded (the floor
        // engages only across the cliffs, not everywhere).
        var spacings = ys.Zip(ys.Skip(1), (a, b) => b - a).ToList();
        Assert.Contains(spacings, s => s < flat / 16);
        Assert.True(ys.Count < 36 / (flat / 32),
            $"{ys.Count} rows — the floor must not govern the flat regions");
    }

    [Fact]
    public void ABullNose_UsesItsCornerRadius_AndAFlatCutterRefusesByName()
    {
        var plate = Shape.Box(40, 30, 6).Translate(0, 0, -3);
        double h = 0.02;
        var op = CncSurfacing.AdaptiveRaster(plate, new MillTool(6), h, sampleStep: 2,
            cutter: MillCutter.BullNose(6, 1));

        // The cusp between passes is cut by the CORNER torus, so the governing radius is
        // the corner's — the flat spacing from r = 1, not r = 3.
        double flat = CncSurfacing.StepoverForScallop(1, h);
        var ys = RowYs(op);
        for (int i = 1; i < ys.Count; i++)
            if (ys[i - 1] > -14 && ys[i] < 14)
                Assert.Equal(flat, ys[i] - ys[i - 1], 12);

        Assert.Contains("facets", Assert.Throws<ArgumentException>(() =>
            CncSurfacing.AdaptiveRaster(plate, new MillTool(6), h,
                cutter: MillCutter.FlatEnd(6))).Message);
    }

    [Fact]
    public void TheMarch_IsDeterministic()
    {
        var ramp = Ramp();
        var a = CncSurfacing.AdaptiveRaster(ramp, new MillTool(6), 0.05, sampleStep: 4);
        var b = CncSurfacing.AdaptiveRaster(ramp, new MillTool(6), 0.05, sampleStep: 4);
        Assert.Equal(a.Passes.Count, b.Passes.Count);
        for (int i = 0; i < a.Passes.Count; i++)
            Assert.Equal(a.Passes[i].Points, b.Passes[i].Points);
    }
}
