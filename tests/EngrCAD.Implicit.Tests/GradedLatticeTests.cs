using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// Graded lattices: a thickness, a strut diameter or a volume fraction that varies over space.
/// <para>
/// The claims are the ungraded family's, restated for a field that is no longer periodic — the
/// SIGN is exact (checked against the parameter the grading states at each point), the reported
/// <see cref="Sdf.LipschitzBound"/> covers the field's own secants (which is what stops the
/// polygonizer's cull dropping geometry now that the bound is above 1), and a constant grading
/// reproduces the uniform field BIT FOR BIT, which is the identity that says the graded path is
/// the same geometry rather than a second opinion about it.
/// </para>
/// </summary>
public class GradedLatticeTests(ITestOutputHelper output)
{
    [Fact]
    public void AConstantGrading_ReproducesTheUniformSheetBitForBit()
    {
        const double Cell = 5, Thickness = 1.1;
        var uniform = Sdf.TpmsSheet(TpmsKind.Gyroid, Cell, Thickness);
        var graded = Sdf.TpmsSheet(TpmsKind.Gyroid, Cell, LatticeGrading.Constant(Thickness));

        foreach (var p in DomainOperatorTests.Probes(seed: 4004, count: 20000, extent: 18))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(uniform.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(graded.Evaluate(p)));
        Assert.Equal(1.0, graded.LipschitzBound(new Aabb((-20, -20, -20), (20, 20, 20))));
    }

    [Fact]
    public void AConstantGrading_ReproducesTheUniformStrutLatticeBitForBit()
    {
        const double Cell = 5, Diameter = 1.2;
        var uniform = Sdf.StrutLattice(StrutLatticeKind.Octet, Cell, Diameter);
        var graded = Sdf.StrutLattice(StrutLatticeKind.Octet, Cell, LatticeGrading.Constant(Diameter));

        foreach (var p in DomainOperatorTests.Probes(seed: 4005, count: 20000, extent: 18))
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(uniform.Evaluate(p)),
                BitConverter.DoubleToInt64Bits(graded.Evaluate(p)));
        Assert.Equal(1.0, graded.LipschitzBound(new Aabb((-20, -20, -20), (20, 20, 20))));
    }

    /// <summary>
    /// The sign, which is the property the whole engine reads: a graded sheet's solid is exactly
    /// <c>|F| / (bound·omega) ≤ t(p)/2</c>, so the field's sign must agree with the UNIFORM
    /// sheet of the local thickness at every point. That is a stronger check than "it looks
    /// graded" — it catches a grading read at the wrong point, or off by a factor of two.
    /// </summary>
    [Fact]
    public void AGradedSheet_AgreesWithTheUniformSheetOfItsOwnLocalThickness()
    {
        const double Cell = 5;
        var grading = LatticeGrading.Along((0, 0, 1), -12, 12, 0.4, 1.8);
        var graded = Sdf.TpmsSheet(TpmsKind.Gyroid, Cell, grading);

        double worst = 0;
        foreach (var p in DomainOperatorTests.Probes(seed: 77, count: 20000, extent: 14))
        {
            var local = Sdf.TpmsSheet(TpmsKind.Gyroid, Cell, grading.At(p));
            worst = Math.Max(worst, Math.Abs(local.Evaluate(p) - graded.Evaluate(p)));
        }
        output.WriteLine($"graded sheet vs the local uniform sheet: worst difference {worst:E3}");
        Assert.Equal(0, worst);
    }

    /// <summary>
    /// The grading really varies, or the tests above are comparing a lattice with itself: a
    /// slice at the thin end and one at the thick end must measure different walls. Measured by
    /// polygonizing and reading the solid's own volume fraction over a slab at each end.
    /// </summary>
    [Fact]
    public void AGradedSheet_IsMeasurablyThickerAtTheThickEnd()
    {
        const double Cell = 5;
        var graded = Sdf.TpmsSheet(
            TpmsKind.Gyroid, Cell, LatticeGrading.Along((0, 0, 1), -10, 10, 0.3, 1.2));

        double thin = OccupiedFraction(graded, new Aabb((-5, -5, -10), (5, 5, -5)));
        double thick = OccupiedFraction(graded, new Aabb((-5, -5, 5), (5, 5, 10)));
        output.WriteLine($"graded gyroid sheet: {thin:0.###} of the slab at the thin end, " +
                         $"{thick:0.###} at the thick end");
        Assert.True(thin > 0.02 && thick < 0.95,
            $"neither end may saturate, or the comparison says nothing: {thin:R} against {thick:R}");
        Assert.True(thick > 2 * thin,
            $"the grading did not take: {thin:R} against {thick:R}");
    }

    [Fact]
    public void AGradedStrutLattice_IsMeasurablyThickerWhereTheGradingSaysSo()
    {
        const double Cell = 5;
        var graded = Sdf.StrutLattice(
            StrutLatticeKind.BodyCentredCubic, Cell,
            LatticeGrading.Radial((0, 0, 0), 0, 14, 0.4, 2.0));

        double inner = OccupiedFraction(graded, new Aabb((-2.5, -2.5, -2.5), (2.5, 2.5, 2.5)));
        double outer = OccupiedFraction(graded, new Aabb((10, -2.5, -2.5), (15, 2.5, 2.5)));
        output.WriteLine($"radially graded BCC: {inner:0.###} at the centre, {outer:0.###} at r ~ 12");
        Assert.True(outer > 2 * inner, $"the grading did not take: {inner:R} against {outer:R}");
    }

    /// <summary>
    /// The volume-fraction grading, which is the engineering spelling: a lattice asked for 12%
    /// at one end and 40% at the other must MEASURE about that.
    /// <para>
    /// The slabs are read where the ramp has CLAMPED, so the grading is exactly constant across
    /// each one — otherwise the measurement is the grading's AVERAGE over the slab and the
    /// comparison is against the wrong number. That is not hypothetical: the first version of
    /// this fixture read 0.162 for a requested 0.12 and was right to, its slab spanning 0.12 to
    /// 0.19 of the ramp.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.12, 0.40)]
    [InlineData(0.30, 0.15)]
    public void AVolumeFractionGrading_LandsOnItsRequestAtBothEnds(double atStart, double atEnd)
    {
        const double Cell = 4;
        var graded = Tpms.GradedSheetForVolumeFraction(
            TpmsKind.Gyroid, Cell, LatticeGrading.Along((0, 0, 1), -4, 4, atStart, atEnd));

        double start = OccupiedFraction(graded, new Aabb((-8, -8, -12), (8, 8, -4)));
        double end = OccupiedFraction(graded, new Aabb((-8, -8, 4), (8, 8, 12)));
        output.WriteLine($"asked {atStart:0.##} -> {atEnd:0.##}; measured {start:0.###} -> {end:0.###}");

        // The band the uniform solve itself carries: the parameter is a quantile of a sampled
        // cell and this is a different sampling over a different region.
        Assert.True(Math.Abs(start - atStart) < 0.03, $"start: asked {atStart}, measured {start:R}");
        Assert.True(Math.Abs(end - atEnd) < 0.03, $"end: asked {atEnd}, measured {end:R}");
    }

    [Fact]
    public void AGradedStrutLatticeByVolumeFraction_LandsOnItsRequest()
    {
        const double Cell = 4;
        var graded = StrutLattices.GradedForVolumeFraction(
            StrutLatticeKind.Octet, Cell, LatticeGrading.Along((0, 0, 1), -4, 4, 0.10, 0.35));

        // ONE cell across rather than four, at a finer sampling: a 10%-dense octet's struts are
        // a fraction of a millimetre against a cell of 4, and the first version of this fixture
        // read 0.04 for a requested 0.10 purely because its 0.4 sample spacing stepped over
        // them. The region is still a whole number of periods, so the measure is exact.
        double start = OccupiedFraction(graded, new Aabb((-2, -2, -12), (2, 2, -4)), 64);
        double end = OccupiedFraction(graded, new Aabb((-2, -2, 4), (2, 2, 12)), 64);
        output.WriteLine($"octet asked 0.10 -> 0.35; measured {start:0.###} -> {end:0.###}");
        Assert.True(Math.Abs(start - 0.10) < 0.03, $"start measured {start:R}");
        Assert.True(Math.Abs(end - 0.35) < 0.03, $"end measured {end:R}");
    }

    /// <summary>
    /// The reported bound is the derivation, and the derivation is checked against the field:
    /// <c>1 + L/2</c> for a thickness or diameter grading. Both halves matter — a bound below
    /// the measured slope drops geometry, and a bound far above it costs the cull.
    /// </summary>
    [Fact]
    public void AGradedFieldsLipschitzBound_IsOnePlusTheGradingsOwn()
    {
        const double L = 1.4 / 20;   // (1.8 - 0.4) over the ramp's 20 units
        var grading = LatticeGrading.Along((0, 0, 1), -10, 10, 0.4, 1.8);
        Assert.Equal(L, grading.LipschitzConstant, 12);

        var sheet = Sdf.TpmsSheet(TpmsKind.Gyroid, 5, grading);
        var region = new Aabb((-15, -15, -15), (15, 15, 15));
        Assert.Equal(1 + L / 2, sheet.LipschitzBound(region), 12);

        var struts = Sdf.StrutLattice(StrutLatticeKind.Octet, 5, grading);
        Assert.Equal(1 + L / 2, struts.LipschitzBound(region), 12);

        // ... and it covers the field.
        var rng = new Random(2718);
        double worst = 0;
        for (int i = 0; i < 40000; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 14,
                (rng.NextDouble() * 2 - 1) * 14,
                (rng.NextDouble() * 2 - 1) * 14);
            var d = new Vector3d(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)
                .Normalized() * 1e-5;
            worst = Math.Max(worst, Math.Abs(sheet.Evaluate(p + d) - sheet.Evaluate(p)) / d.Length);
        }
        output.WriteLine($"graded sheet: reported {1 + L / 2:0.#####}, measured {worst:0.#####}");
        Assert.True(worst <= 1 + L / 2, $"measured slope {worst:R} exceeds the reported bound");
    }

    [Fact]
    public void Gradings_DeriveTheirConstantWhereTheyCan_AndRefuseNonsense()
    {
        Assert.Equal(0, LatticeGrading.Constant(1.5).LipschitzConstant);
        Assert.Equal(0.25, LatticeGrading.Along((1, 0, 0), 0, 8, 1, 3).LipschitzConstant, 12);
        Assert.Equal(0.5, LatticeGrading.Radial((1, 2, 3), 2, 6, 4, 2).LipschitzConstant, 12);

        // The range is CLAMPED, so it is a guarantee rather than a promise — which is also what
        // keeps a ramp's own constant valid outside its two stations.
        var ramp = LatticeGrading.Along((0, 0, 1), 0, 10, 1, 2);
        Assert.Equal(1, ramp.At((0, 0, -50)), 12);
        Assert.Equal(2, ramp.At((0, 0, 50)), 12);
        Assert.Equal(1, ramp.Minimum, 12);
        Assert.Equal(2, ramp.Maximum, 12);

        Assert.Throws<ArgumentException>(() => LatticeGrading.Along((0, 0, 0), 0, 8, 1, 3));
        Assert.Throws<ArgumentException>(() => LatticeGrading.Along((1, 0, 0), 4, 4, 1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LatticeGrading.FromFunction(_ => 1, -1, 0, 2));
        Assert.Throws<ArgumentException>(
            () => LatticeGrading.FromFunction(_ => 1, 1, 5, 2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Sdf.StrutLattice(StrutLatticeKind.Octet, 5, LatticeGrading.Constant(0)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Sdf.TpmsSheet(TpmsKind.Gyroid, 5, LatticeGrading.Along((0, 0, 1), 0, 5, -1, 1)));
    }

    /// <summary>The fraction of a box the solid occupies, by sampling — the same estimator the
    /// uniform solves use, applied to a region rather than to a cell. The resolution has to be
    /// fine against the FEATURE, not against the box: a thin strut is stepped over by a coarse
    /// grid and the fraction comes back low.</summary>
    private static double OccupiedFraction(Sdf field, in Aabb box, int N = 40)
    {
        var size = box.Max - box.Min;
        int inside = 0;
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
                for (int k = 0; k < N; k++)
                {
                    var p = box.Min + new Vector3d(
                        size.X * (i + 0.5) / N, size.Y * (j + 0.5) / N, size.Z * (k + 0.5) / N);
                    if (field.Evaluate(p) < 0)
                        inside++;
                }
        return (double)inside / (N * N * N);
    }
}
