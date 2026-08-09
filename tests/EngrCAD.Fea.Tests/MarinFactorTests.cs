using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The Marin endurance-limit factors and the derived curve they produce.
///
/// <para><b>The transcription tests assert hand-worked textbook values</b>, not the
/// formula re-typed (which would agree with its own transcription mistake): machined at
/// S_ut = 690 MPa is the classic worked 0.798, the 32 mm size factor the classic 0.858,
/// 99% reliability the tabulated 0.814. The derivation tests are identities of the
/// construction: the 10³-cycle pivot is unchanged, the endurance limit is exactly the
/// factor times the pristine one, the ultimate strength and knee are untouched, and a
/// factor of exactly 1 returns the pristine curve verbatim.</para>
/// </summary>
public class MarinFactorTests(ITestOutputHelper output)
{
    // ---- transcription, against hand-worked values ------------------------------------

    [Theory]
    [InlineData(SurfaceFinish.Machined, 690.0, 0.798)]   // the classic worked example
    [InlineData(SurfaceFinish.Machined, 621.0, 0.820)]   // Steel1045's own S_ut
    [InlineData(SurfaceFinish.Ground, 1241.0, 0.862)]    // 1.58·1241^-0.085
    [InlineData(SurfaceFinish.HotRolled, 621.0, 0.570)]  // 57.7·621^-0.718
    [InlineData(SurfaceFinish.AsForged, 621.0, 0.452)]   // 272·621^-0.995
    [InlineData(SurfaceFinish.ColdDrawn, 690.0, 0.798)]  // same row as machined
    public void SurfaceFactorMatchesTheHandWorkedValues(
        SurfaceFinish finish, double ultimate, double expected)
    {
        double ka = MarinFactors.Surface(finish, ultimate);
        output.WriteLine($"{finish} at {ultimate} MPa: {ka:F4}");
        Assert.Equal(expected, ka, 3);
    }

    [Fact]
    public void GroundSurfaceClampsAtOneForSoftMaterials()
    {
        // 1.58·200^-0.085 = 1.007: the correlation crosses 1 below ~215 MPa, and the
        // polished specimen is the baseline, so the clamp is the honest reading.
        Assert.Equal(1.0, MarinFactors.Surface(SurfaceFinish.Ground, 200.0));
        Assert.True(MarinFactors.Surface(SurfaceFinish.Ground, 300.0) < 1.0);
    }

    [Theory]
    [InlineData(7.62, 1.000)]   // the correlation's own reference diameter
    [InlineData(32.0, 0.858)]   // the classic worked value
    [InlineData(51.0, 0.816)]   // the seam, first branch
    [InlineData(100.0, 0.733)]  // 1.51·100^-0.157
    [InlineData(1.0, 1.000)]    // below the correlation: the part IS the specimen scale
    public void SizeFactorMatchesTheHandWorkedValues(double diameter, double expected)
    {
        double kb = MarinFactors.Size(diameter);
        output.WriteLine($"d {diameter} mm: {kb:F4}");
        Assert.Equal(expected, kb, 3);
    }

    [Fact]
    public void TheTwoSizeBranchesAgreeAtTheirSeamToTheFitsOwnAccuracy()
    {
        // (51/7.62)^-0.107 vs 1.51·51^-0.157 — two independent fits meeting at 51 mm.
        double below = Math.Pow(51.0 / 7.62, -0.107);
        double above = 1.51 * Math.Pow(51.0, -0.157);
        output.WriteLine($"below {below:F4}, above {above:F4}");
        Assert.True(Math.Abs(below - above) < 0.005,
            $"the branches disagree by {Math.Abs(below - above):F4} at the seam");
    }

    [Theory]
    [InlineData(0.5, 1.000)]
    [InlineData(0.9, 0.897)]
    [InlineData(0.99, 0.814)]
    [InlineData(0.999, 0.753)]
    [InlineData(0.999999, 0.620)]
    public void ReliabilityFactorIsTheTabulatedValueExactly(double reliability, double expected)
    {
        Assert.Equal(expected, MarinFactors.Reliability(reliability));
    }

    // ---- the derived curve: identities of the construction ----------------------------

    [Fact]
    public void TheDerivedCurvePivotsAtTenCubedAndScalesTheEnduranceExactly()
    {
        var pristine = FatigueMaterials.Steel1045;
        const double factor = 0.62;
        var derived = pristine.WithEnduranceFactor(factor, "test");

        // The pivot: the 10³-cycle strength is unchanged (to round-off of the re-fit).
        AssertRelative(pristine.StressAt(1e3), derived.StressAt(1e3), 1e-12);
        // The endurance end: exactly the factor times the pristine limit.
        AssertRelative(
            factor * pristine.EnduranceLimit!.Value, derived.EnduranceLimit!.Value, 1e-12);
        // Untouched: the static anchor and the knee.
        Assert.Equal(pristine.UltimateStrength, derived.UltimateStrength);
        Assert.Equal(pristine.EnduranceLife, derived.EnduranceLife);
        // Between the anchors the corrected line sits BELOW the pristine one.
        foreach (double cycles in new[] { 3e3, 1e4, 1e5, 9e5 })
            Assert.True(derived.StressAt(cycles) < pristine.StressAt(cycles));
        // And lives shorten at any stress on the line.
        double stress = pristine.StressAt(1e5);
        Assert.True(derived.LifeAt(stress) < 1e5);

        output.WriteLine(pristine.ToString());
        output.WriteLine(derived.ToString());
    }

    [Fact]
    public void AFactorOfExactlyOneReturnsThePristineCurveVerbatim()
    {
        var pristine = FatigueMaterials.Steel1015;
        Assert.Same(pristine, pristine.WithEnduranceFactor(1.0));
    }

    [Fact]
    public void TheFullStackMatchesAHandComputedEndurance()
    {
        // Steel1045, machined, 25 mm, 99% reliability:
        // ka = 4.51·621^-0.265 = 0.8205, kb = (25/7.62)^-0.107 = 0.8806, ke = 0.814
        // => k = 0.5880, endurance = 0.5880·249.53 = 146.7 MPa.
        var derived = FatigueMaterials.Steel1045.WithFactors(
            SurfaceFinish.Machined, diameterMm: 25, reliability: 0.99);
        output.WriteLine(derived.ToString());
        Assert.Equal(146.7, derived.EnduranceLimit!.Value, 0);
        Assert.Contains("Machined", derived.Name);

        // Omitting the diameter omits the size effect (axial loading's own rule).
        var axial = FatigueMaterials.Steel1045.WithFactors(
            SurfaceFinish.Machined, reliability: 0.99);
        AssertRelative(
            MarinFactors.Surface(SurfaceFinish.Machined, 621) * 0.814
                * FatigueMaterials.Steel1045.EnduranceLimit!.Value,
            axial.EnduranceLimit!.Value, 1e-12);
    }

    [Fact]
    public void TheDerivedCurveFeedsTheFatigueMachineryUnchanged()
    {
        // A corrected curve is an ordinary SnCurve, so the safety-factor and life
        // arithmetic consume it with nothing special-cased — and the design strength it
        // answers with is the corrected endurance, which is the whole point.
        var derived = FatigueMaterials.Steel1045.WithFactors(SurfaceFinish.Machined);
        double strength = FatigueAnalysis.AllowableAmplitude(
            derived, 0.0, MeanStressCorrection.Goodman);
        AssertRelative(derived.EnduranceLimit!.Value, strength, 1e-12);
    }

    // ---- the knee-less (aluminium) correction at a stated reference life --------------

    [Fact]
    public void TheAluminiumCurvePivotsAtTenCubedAndScalesTheReferenceStrengthExactly()
    {
        // 6061-T6 has no endurance limit, so the factor is applied at a stated reference life
        // (5e8 is the rotating-beam convention) and the line stays knee-less.
        var pristine = FatigueMaterials.Aluminium6061T6;
        const double factor = 0.7;
        const double referenceLife = 5e8;
        var derived = pristine.WithEnduranceFactorAt(factor, referenceLife, "test");

        // The pivot is unchanged (to round-off of the re-fit).
        AssertRelative(pristine.StressAt(1e3), derived.StressAt(1e3), 1e-12);
        // At the reference life the strength is EXACTLY the factor times the pristine one —
        // the defining property of the construction.
        AssertRelative(
            factor * pristine.StressAt(referenceLife), derived.StressAt(referenceLife), 1e-12);
        // The line stays knee-less: no endurance limit was invented.
        Assert.False(derived.HasEnduranceLimit);
        Assert.Null(derived.EnduranceLife);
        // The static anchor is untouched.
        Assert.Equal(pristine.UltimateStrength, derived.UltimateStrength);
        // Between the anchors the corrected line sits below the pristine one, and lives shorten.
        foreach (double cycles in new[] { 1e5, 1e7, 1e8 })
            Assert.True(derived.StressAt(cycles) < pristine.StressAt(cycles));
        output.WriteLine(pristine.ToString());
        output.WriteLine(derived.ToString());
    }

    [Fact]
    public void TheAluminiumFullStackMatchesTheFactorAtTheReferenceLife()
    {
        var pristine = FatigueMaterials.Aluminium6061T6;
        const double referenceLife = 5e8;
        var derived = pristine.WithFactorsAt(
            SurfaceFinish.Machined, referenceLife, diameterMm: 20, reliability: 0.99);
        double factor = MarinFactors.Surface(SurfaceFinish.Machined, pristine.UltimateStrength)
            * MarinFactors.Size(20) * MarinFactors.Reliability(0.99);
        output.WriteLine($"combined factor {factor:F4}; {derived}");
        AssertRelative(
            factor * pristine.StressAt(referenceLife), derived.StressAt(referenceLife), 1e-12);
        Assert.Contains("Machined", derived.Name);
        Assert.False(derived.HasEnduranceLimit);
    }

    [Fact]
    public void AnAluminiumFactorOfExactlyOneReturnsThePristineCurveVerbatim()
    {
        var pristine = FatigueMaterials.Aluminium2024T351;
        Assert.Same(pristine, pristine.WithEnduranceFactorAt(1.0, 5e8));
    }

    [Fact]
    public void TheKneeLessCorrectionRefusesTheContradictions()
    {
        var aluminium = FatigueMaterials.Aluminium6061T6;

        // A knee'd curve has its own reference (the knee) — use WithEnduranceFactor.
        var kneed = Assert.Throws<FeaException>(() =>
            FatigueMaterials.Steel1045.WithEnduranceFactorAt(0.8, 5e8));
        Assert.Contains("endurance knee", kneed.Message);

        // A reference life at or below the pivot tilts the low-cycle regime.
        var lowRef = Assert.Throws<FeaException>(() => aluminium.WithEnduranceFactorAt(0.8, 500));
        Assert.Contains("pivot", lowRef.Message);

        // Factors outside (0, 1].
        Assert.Throws<FeaException>(() => aluminium.WithEnduranceFactorAt(0.0, 5e8));
        Assert.Throws<FeaException>(() => aluminium.WithEnduranceFactorAt(1.2, 5e8));
    }

    // ---- refusals ---------------------------------------------------------------------

    [Fact]
    public void RefusalsFireByName()
    {
        // Aluminium has no endurance limit to knock down.
        var ex1 = Assert.Throws<FeaException>(() =>
            FatigueMaterials.Aluminium6061T6.WithEnduranceFactor(0.8));
        Assert.Contains("no endurance limit", ex1.Message);

        // Factors outside (0, 1].
        Assert.Throws<FeaException>(() => FatigueMaterials.Steel1045.WithEnduranceFactor(0.0));
        Assert.Throws<FeaException>(() => FatigueMaterials.Steel1045.WithEnduranceFactor(1.2));

        // A knee at or below the pivot is outside the construction's regime.
        var lowKnee = new SnCurve("low knee", 900, -0.09, 600, enduranceLife: 500);
        Assert.Throws<FeaException>(() => lowKnee.WithEnduranceFactor(0.8));

        // The size correlation refuses beyond its data.
        var ex2 = Assert.Throws<FeaException>(() => MarinFactors.Size(300));
        Assert.Contains("254", ex2.Message);

        // A non-tabulated reliability names the levels rather than interpolating.
        var ex3 = Assert.Throws<FeaException>(() => MarinFactors.Reliability(0.98));
        Assert.Contains("0.999", ex3.Message);
    }

    private static void AssertRelative(double expected, double actual, double tolerance)
    {
        double scale = Math.Max(Math.Abs(expected), Math.Abs(actual));
        if (scale == 0)
        {
            Assert.Equal(expected, actual);
            return;
        }
        double relative = Math.Abs(expected - actual) / scale;
        Assert.True(relative <= tolerance,
            $"expected {expected:G17}, got {actual:G17} (relative {relative:E2} > {tolerance:E1})");
    }
}
