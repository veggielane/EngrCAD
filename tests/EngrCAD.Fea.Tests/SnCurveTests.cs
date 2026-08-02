using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// <see cref="SnCurve"/> and the <see cref="FatigueMaterials"/> catalogue.
///
/// <para>A transcribed table's only honest claim is the transcription itself, so the
/// coefficient assertions re-type the datasheet values in datasheet form — which here is
/// MPa, the model unit as well, so unlike the density lesson there is no conversion for a
/// self-comparison to hide. The assertions with independent teeth are the structural
/// ones: the line reproduced at its own anchor points exactly, the inverse round-trip,
/// and the physics-flavoured bands a mistyped exponent cannot survive.</para>
/// </summary>
public class SnCurveTests
{
    // ---------- transcription (datasheet form: MPa, dimensionless b) ----------

    /// <summary>The stored coefficients, re-typed from the SAE J1099 / Dowling
    /// compilations the catalogue transcribes. Catches a typo in the table; the physics
    /// checks below catch a value that is self-consistently wrong.</summary>
    [Fact]
    public void CatalogueMatchesTheDatasheetFigures()
    {
        AssertRow(FatigueMaterials.Steel1015, 827, -0.11, 415, 1e6);
        AssertRow(FatigueMaterials.Steel1045, 948, -0.092, 621, 1e6);
        AssertRow(FatigueMaterials.Steel4340, 1758, -0.0977, 1241, 1e6);
        AssertRow(FatigueMaterials.Aluminium2024T351, 927, -0.113, 469, null);
        AssertRow(FatigueMaterials.Aluminium6061T6, 535, -0.102, 310, null);
        AssertRow(FatigueMaterials.Aluminium7075T6, 1466, -0.143, 578, null);
        Assert.Equal(6, FatigueMaterials.All.Count);

        static void AssertRow(SnCurve curve, double sigmaF, double b, double uts, double? knee)
        {
            Assert.Equal(sigmaF, curve.FatigueStrengthCoefficient);
            Assert.Equal(b, curve.FatigueStrengthExponent);
            Assert.Equal(uts, curve.UltimateStrength);
            Assert.Equal(knee, curve.EnduranceLife);
        }
    }

    // ---------- the line at its own anchor points, exactly ----------

    /// <summary>
    /// sigma'_f is DEFINED as the stress at one reversal (2N = 1), so the line evaluated
    /// there must return the coefficient EXACTLY — Math.Pow(1, b) is 1.0 for every b, so
    /// this is a bit assertion, not a tolerance.
    /// </summary>
    [Fact]
    public void TheLineAtOneReversalIsTheCoefficientExactly()
    {
        foreach (var curve in FatigueMaterials.All)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(curve.FatigueStrengthCoefficient),
                BitConverter.DoubleToInt64Bits(curve.StressAt(0.5)));
    }

    /// <summary>The endurance limit IS the line at its own knee — derived, so this pins
    /// the stitching: the curve at the knee, and everywhere beyond it, is that one
    /// value.</summary>
    [Fact]
    public void TheKneeIsOnTheLineAndTheCurveIsFlatBeyondIt()
    {
        foreach (var curve in FatigueMaterials.All.Where(c => c.HasEnduranceLimit))
        {
            double limit = curve.EnduranceLimit!.Value;
            double knee = curve.EnduranceLife!.Value;
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(limit),
                BitConverter.DoubleToInt64Bits(curve.StressAt(knee)));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(limit),
                BitConverter.DoubleToInt64Bits(curve.StressAt(1e9)));
        }
    }

    /// <summary>Life and stress are inverses on the sloped part of the line — a
    /// round-trip through two Math.Pow calls, so the bar is relative round-off rather
    /// than bits.</summary>
    [Fact]
    public void LifeAndStressRoundTripBelowTheKnee()
    {
        foreach (var curve in FatigueMaterials.All)
        {
            foreach (double cycles in new[] { 1e3, 1e4, 1e5 })
            {
                double back = curve.LifeAt(curve.StressAt(cycles));
                Assert.True(
                    Math.Abs(back - cycles) <= 1e-12 * cycles,
                    $"{curve.Name}: {cycles} cycles round-tripped to {back}.");
            }
        }
    }

    // ---------- physics-flavoured checks a self-comparison cannot pass ----------

    /// <summary>
    /// A steel's endurance limit classically sits near half its ultimate strength
    /// (0.35–0.5 across the common correlations). The catalogue's three steels DERIVE
    /// their limits from the Basquin line at the 10^6 knee, so landing in the band is a
    /// real cross-check between four independent transcriptions (sigma'_f, b, S_ut and
    /// the knee) — a mistyped exponent moves the ratio far outside it.
    /// </summary>
    [Fact]
    public void SteelEnduranceLimitsSitInTheClassicalBand()
    {
        foreach (var curve in FatigueMaterials.All.Where(c => c.HasEnduranceLimit))
        {
            double ratio = curve.EnduranceLimit!.Value / curve.UltimateStrength;
            Assert.InRange(ratio, 0.30, 0.55);
        }
    }

    /// <summary>SAE 1045's derived endurance limit against a hand-evaluated figure
    /// (948·(2·10⁶)^(−0.092) worked by hand, typed as a literal): the one row checked to
    /// a number rather than a band, so the formula and the derivation cannot both be
    /// wrong the same way.</summary>
    [Fact]
    public void Steel1045EnduranceLimitMatchesTheHandComputation()
    {
        Assert.Equal(249.53, FatigueMaterials.Steel1045.EnduranceLimit!.Value, 1);
    }

    /// <summary>At 10³ cycles — the near end of the high-cycle regime — every row's
    /// alternating strength is below its static strength. A transcription with sigma'_f
    /// and b swapped, or b's sign lost to the constructor's guard being bypassed, fails
    /// this on every row.</summary>
    [Fact]
    public void TheHighCycleLineEntersBelowTheStaticStrength()
    {
        foreach (var curve in FatigueMaterials.All)
            Assert.True(
                curve.StressAt(1e3) < curve.UltimateStrength,
                $"{curve.Name}: {curve.StressAt(1e3)} MPa at 1e3 cycles vs S_ut {curve.UltimateStrength}.");
    }

    // ---------- life semantics ----------

    [Fact]
    public void AtOrBelowTheEnduranceLimitLifeIsInfinite()
    {
        var curve = FatigueMaterials.Steel1045;
        double limit = curve.EnduranceLimit!.Value;
        Assert.Equal(double.PositiveInfinity, curve.LifeAt(limit));
        Assert.Equal(double.PositiveInfinity, curve.LifeAt(0.5 * limit));
        // Just above the limit the life is finite and lands near the knee.
        double justAbove = curve.LifeAt(limit * 1.001);
        Assert.True(double.IsFinite(justAbove));
        Assert.InRange(justAbove, 0.9e6, 1e6);
    }

    [Fact]
    public void ZeroAmplitudeIsInfiniteLifeWhateverTheMaterial()
    {
        // Aluminium has no endurance limit, and still nothing alternating never fails.
        Assert.Equal(double.PositiveInfinity, FatigueMaterials.Aluminium6061T6.LifeAt(0));
    }

    /// <summary>An amplitude at the coefficient itself is half a cycle — the line's
    /// definition read backwards — and above it the "life" keeps falling: gross overload
    /// reported as arithmetic, with the validity caveat living in the docs.</summary>
    [Fact]
    public void AtTheCoefficientLifeIsHalfACycle()
    {
        var curve = FatigueMaterials.Aluminium7075T6;
        Assert.Equal(0.5, curve.LifeAt(curve.FatigueStrengthCoefficient), 12);
    }

    // ---------- refusals ----------

    [Fact]
    public void RefusesANonNegativeExponentByName()
    {
        var ex = Assert.Throws<FeaException>(() => new SnCurve("bad", 900, 0.1, 500));
        Assert.Contains("negative by definition", ex.Message);
        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public void RefusesNonPositiveStrengths()
    {
        Assert.Throws<FeaException>(() => new SnCurve("bad", 0, -0.1, 500));
        Assert.Throws<FeaException>(() => new SnCurve("bad", 900, -0.1, 0));
        Assert.Throws<FeaException>(() => new SnCurve("bad", 900, -0.1, 500, 0.25));
    }

    [Fact]
    public void RefusesNonsenseQueries()
    {
        var curve = FatigueMaterials.Steel1045;
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.StressAt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.LifeAt(-1));
    }
}
