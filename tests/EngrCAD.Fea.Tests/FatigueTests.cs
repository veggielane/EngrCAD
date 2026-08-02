using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// <see cref="FatigueAnalysis"/>: the alternating/mean decomposition, the mean-stress
/// corrections and the published fields, over the bar whose stress state is EXACT.
///
/// <para>With nu = 0 a uniaxial bar's solution is a linear field IN the tet space (the
/// AnalysisBody lesson), so the FEA side of every check here is exact to round-off and
/// the tolerances test the fatigue arithmetic rather than a discretization. The oracles
/// are built independently: stresses re-derived from the TIP DISPLACEMENT rather than
/// read back through the stress recovery the decomposition itself consumes.</para>
/// </summary>
public class FatigueTests
{
    private const double E = 210_000;
    private const double Length = 40;

    /// <summary>nu = 0 so the uniaxial state is exact (no lateral contraction, so the
    /// fully fixed end is compatible with it).</summary>
    private static readonly Material Bar = new("fatigue bar", E, 0.0);

    private static AnalysisMesh Mesh() =>
        AnalysisMesh.Of(StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(Length, 10, 10), 4, 1, 1));

    /// <summary>A bar fixed at x = 0 pulled by a uniform axial traction (MPa) at x = L —
    /// the exact stress state is (sigma, 0, 0; 0, 0, 0) everywhere.</summary>
    private static StructuralModel Pull(AnalysisMesh mesh, double sigma)
    {
        var model = new StructuralModel(mesh, Bar);
        model.Fix(StructuredTetMesh.XMin);
        model.Traction(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(sigma, 0, 0));
        return model;
    }

    private static (FatigueResults Fatigue, StructuralResults A, StructuralResults B) Evaluate(
        double sigmaA, double sigmaB, SnCurve curve, FatigueOptions? options = null)
    {
        var mesh = Mesh();
        var solved = StructuralSolver.SolveAll([Pull(mesh, sigmaA), Pull(mesh, sigmaB)]);
        return (FatigueAnalysis.Evaluate(solved[0], solved[1], curve, options), solved[0], solved[1]);
    }

    /// <summary>The independent oracle: the axial stress re-derived from the mean tip
    /// displacement, sigma = E·u(L)/L — the displacement solution directly, not the
    /// stress recovery the decomposition reads.</summary>
    private static double StressFromTipDisplacement(StructuralResults results)
    {
        var mesh = results.Mesh;
        double sum = 0;
        int count = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            if (Math.Abs(mesh.Position(v).X - Length) < 1e-9)
            {
                sum += results.DisplacementAt(v).X;
                count++;
            }
        }
        Assert.True(count > 0);
        return E * (sum / count) / Length;
    }

    // ---------- the decomposition ----------

    /// <summary>
    /// The single-element-grade hand check: two load cases at +100 and +20 MPa give
    /// sigma_a = 40 and sigma_m = 60 at EVERY node, against an oracle built from the
    /// displacement solution rather than by re-running the decomposition's own path.
    /// </summary>
    [Fact]
    public void TwoLoadCases_DecomposeToTheHandArithmetic()
    {
        var (fatigue, a, b) = Evaluate(100, 20, FatigueMaterials.Steel1045);

        double s1 = StressFromTipDisplacement(a);
        double s2 = StressFromTipDisplacement(b);
        double expectedAmp = 0.5 * (s1 - s2);    // 40, from the displacements
        double expectedMean = 0.5 * (s1 + s2);   // 60

        Assert.Equal(40.0, expectedAmp, 9);       // the oracle itself is on the money
        Assert.Equal(60.0, expectedMean, 9);
        for (int v = 0; v < fatigue.Mesh.NodeCount; v++)
        {
            Assert.True(Math.Abs(fatigue.AlternatingStress[v] - expectedAmp) <= 1e-10 * expectedAmp);
            Assert.True(Math.Abs(fatigue.MeanStress[v] - expectedMean) <= 1e-10 * expectedMean);
        }
    }

    /// <summary>The order of the two cases cannot matter: max and min are taken per node.</summary>
    [Fact]
    public void TheCaseOrderDoesNotMatter()
    {
        var (forward, _, _) = Evaluate(100, 20, FatigueMaterials.Steel1045);
        var (reversed, _, _) = Evaluate(20, 100, FatigueMaterials.Steel1045);
        for (int v = 0; v < forward.Mesh.NodeCount; v++)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(forward.AlternatingStress[v]),
                BitConverter.DoubleToInt64Bits(reversed.AlternatingStress[v]));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(forward.MeanStress[v]),
                BitConverter.DoubleToInt64Bits(reversed.MeanStress[v]));
        }
    }

    /// <summary>
    /// A fully reversed load reads R = -1: equal and opposite cases give a mean of zero.
    /// The bar is relative to the amplitude (the total-cancellation rule), though in
    /// practice it measures EXACTLY zero — negating the load negates every solve and
    /// recovery output bit for bit (IEEE rounding commutes with negation), and the signed
    /// von Mises is odd in the stress, so the two halves cancel identically.
    /// </summary>
    [Fact]
    public void EqualAndOppositeLoads_ReadFullyReversed()
    {
        var (fatigue, a, _) = Evaluate(150, -150, FatigueMaterials.Steel1045);
        double amp = Math.Abs(StressFromTipDisplacement(a));
        for (int v = 0; v < fatigue.Mesh.NodeCount; v++)
        {
            Assert.True(Math.Abs(fatigue.MeanStress[v]) <= 1e-13 * amp);
            Assert.True(Math.Abs(fatigue.AlternatingStress[v] - amp) <= 1e-10 * amp);
        }
    }

    /// <summary>The signed part of the signed von Mises: a compressive uniaxial state
    /// reads negative (its trace is negative), which is what makes a compressive case
    /// the cycle's MINIMUM rather than a second maximum of the same magnitude.</summary>
    [Fact]
    public void SignedVonMises_CarriesTheSignOfTheTrace()
    {
        var tension = new SymmetricTensor3(100, 0, 0, 0, 0, 0);
        var compression = new SymmetricTensor3(-100, 0, 0, 0, 0, 0);
        Assert.Equal(100.0, FatigueAnalysis.SignedVonMises(tension), 12);
        Assert.Equal(-100.0, FatigueAnalysis.SignedVonMises(compression), 12);
        // The uniaxial case is the one the S-N data was measured in: the equivalent IS
        // the stress, sign included.
    }

    // ---------- the mean-stress corrections ----------

    /// <summary>At zero mean the correction is the identity EXACTLY — structural (the
    /// non-tensile branch returns the amplitude verbatim), asserted as bits.</summary>
    [Fact]
    public void AtZeroMean_TheCorrectionIsTheIdentityExactly()
    {
        var curve = FatigueMaterials.Steel1045;
        foreach (var correction in new[]
        {
            MeanStressCorrection.None, MeanStressCorrection.Goodman, MeanStressCorrection.Gerber,
        })
        {
            double corrected = FatigueAnalysis.EquivalentAlternating(curve, 123.456, 0.0, correction);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(123.456),
                BitConverter.DoubleToInt64Bits(corrected));
        }
    }

    /// <summary>At a mean of exactly the ultimate strength the allowable alternating
    /// stress is exactly ZERO — S_ut/S_ut is exactly 1, its complement exactly 0 — for
    /// both lines. The other structural anchor.</summary>
    [Fact]
    public void AtTheUltimateStrength_TheAllowableAmplitudeIsExactlyZero()
    {
        var curve = FatigueMaterials.Steel1045;
        Assert.Equal(0.0, FatigueAnalysis.AllowableAmplitude(
            curve, curve.UltimateStrength, MeanStressCorrection.Goodman));
        Assert.Equal(0.0, FatigueAnalysis.AllowableAmplitude(
            curve, curve.UltimateStrength, MeanStressCorrection.Gerber));
    }

    /// <summary>A compressive mean earns NO credit: amplitude unchanged, allowable at the
    /// design strength — the stated default, not an omission.</summary>
    [Fact]
    public void ACompressiveMeanTakesNoBenefit()
    {
        var curve = FatigueMaterials.Steel1045;
        Assert.Equal(80.0, FatigueAnalysis.EquivalentAlternating(
            curve, 80, -200, MeanStressCorrection.Goodman));
        Assert.Equal(
            curve.EnduranceLimit!.Value,
            FatigueAnalysis.AllowableAmplitude(curve, -200, MeanStressCorrection.Goodman));
    }

    /// <summary>A mean at or beyond the ultimate strength is static failure: the
    /// equivalent amplitude is +infinity, and through the pipeline the life lands on the
    /// one-cycle floor (log10 = 0) rather than at a -infinity that would poison a
    /// legend's range.</summary>
    [Fact]
    public void AMeanAtTheUltimateStrengthIsStaticFailure()
    {
        var curve = FatigueMaterials.Steel1045;
        Assert.Equal(double.PositiveInfinity, FatigueAnalysis.EquivalentAlternating(
            curve, 10, curve.UltimateStrength, MeanStressCorrection.Goodman));

        // Through the pipeline: mean 640 > S_ut 621.
        var (fatigue, _, _) = Evaluate(680, 600, curve);
        for (int v = 0; v < fatigue.Mesh.NodeCount; v++)
        {
            Assert.Equal(double.PositiveInfinity, fatigue.CorrectedAmplitudeAt(v));
            Assert.Equal(0.0, fatigue.Log10Life[v]);
        }
    }

    /// <summary>Goodman is the more conservative line everywhere strictly between the
    /// axes — the reason it is the default — and both corrections only ever reduce the
    /// allowable amplitude under a tensile mean.</summary>
    [Fact]
    public void GoodmanIsMoreConservativeThanGerberUnderATensileMean()
    {
        var curve = FatigueMaterials.Steel1045;
        foreach (double mean in new[] { 50.0, 150.0, 400.0 })
        {
            double goodman = FatigueAnalysis.AllowableAmplitude(
                curve, mean, MeanStressCorrection.Goodman);
            double gerber = FatigueAnalysis.AllowableAmplitude(
                curve, mean, MeanStressCorrection.Gerber);
            Assert.True(goodman < gerber);
            Assert.True(gerber < curve.EnduranceLimit!.Value);
        }
    }

    // ---------- the safety factor, verified by its own definition ----------

    /// <summary>
    /// The radial safety factor IS the load multiplier to the line, so the oracle is to
    /// APPLY it: re-solve both cases with the loads scaled by the measured factor and the
    /// scaled state must sit exactly ON the line — the minimum factor reads 1, and the
    /// allowable amplitude at the scaled mean equals the scaled amplitude. An
    /// independently restated formula would agree with a broken implementation that made
    /// the same mistake; the definition cannot.
    /// </summary>
    [Theory]
    [InlineData(MeanStressCorrection.Goodman)]
    [InlineData(MeanStressCorrection.Gerber)]
    public void ScalingTheLoadsByTheSafetyFactorLandsOnTheLine(MeanStressCorrection correction)
    {
        var curve = FatigueMaterials.Steel1045;
        var options = new FatigueOptions { Correction = correction };
        var (fatigue, _, _) = Evaluate(100, 20, curve, options);
        double n = fatigue.MinSafetyFactor;
        Assert.True(n > 1);

        var (scaled, _, _) = Evaluate(100 * n, 20 * n, curve, options);
        Assert.Equal(1.0, scaled.MinSafetyFactor, 9);

        int node = scaled.MinSafetyFactorNode;
        double allowable = FatigueAnalysis.AllowableAmplitude(
            curve, scaled.MeanStress[node], correction);
        Assert.True(
            Math.Abs(scaled.AlternatingStress[node] - allowable) <= 1e-9 * allowable,
            $"amplitude {scaled.AlternatingStress[node]} vs allowable {allowable}");
    }

    /// <summary>Under R = -1 the factor reduces to design strength over amplitude for
    /// every correction — the zero-mean identity read through the factor.</summary>
    [Fact]
    public void UnderFullReversalTheFactorIsStrengthOverAmplitude()
    {
        var curve = FatigueMaterials.Steel1045;
        var (fatigue, _, _) = Evaluate(100, -100, curve);
        double expected = curve.EnduranceLimit!.Value / 100.0;
        Assert.Equal(expected, fatigue.MinSafetyFactor, 8);
    }

    // ---------- the full pipeline against a hand-computed life ----------

    /// <summary>
    /// R = -1 at 400 MPa on SAE 1045, end to end: two solves, the decomposition, the
    /// (identity) correction, the life. The oracle is the Basquin inversion evaluated
    /// from the datasheet constants typed here, and a literal band besides — worked by
    /// hand, N = ½·(400/948)^(1/−0.092) ≈ 5.92e3 cycles — so the formula and its
    /// transcription cannot both be wrong the same way.
    /// </summary>
    [Fact]
    public void FullyReversed400MPa_MatchesTheHandComputedLife()
    {
        var (fatigue, _, _) = Evaluate(400, -400, FatigueMaterials.Steel1045);

        double expectedCycles = 0.5 * Math.Pow(400.0 / 948.0, 1.0 / -0.092);
        Assert.InRange(expectedCycles, 5.8e3, 6.1e3);   // the hand-worked figure

        double expectedLog = Math.Log10(expectedCycles);
        for (int v = 0; v < fatigue.Mesh.NodeCount; v++)
            Assert.True(
                Math.Abs(fatigue.Log10Life[v] - expectedLog) <= 1e-6,
                $"node {v}: log10 life {fatigue.Log10Life[v]} vs {expectedLog}");

        Assert.Equal(fatigue.Mesh.NodeCount, fatigue.FiniteLifeNodeCount);
    }

    // ---------- infinite life, and its spelling ----------

    /// <summary>
    /// Below the endurance limit every node lives forever: the life field is NaN — the
    /// VTU writer's "no value" convention, which <see cref="FieldRange.Of"/> skips — so
    /// the field ranges EMPTY rather than a legend being collapsed by a sentinel, while
    /// the safety factor stays a finite, plottable number everywhere.
    /// </summary>
    [Fact]
    public void BelowTheEnduranceLimit_LifeIsNaNAndTheSafetyFactorIsFinite()
    {
        var curve = FatigueMaterials.Steel1045;
        var (fatigue, _, _) = Evaluate(100, -100, curve);   // amplitude 100 < endurance 249.5

        Assert.Equal(0, fatigue.FiniteLifeNodeCount);
        Assert.True(double.IsNaN(fatigue.MinLog10Life));
        var life = fatigue.Fields().Single(f => f.Name == FatigueResults.FieldNames.Life);
        Assert.True(life.Range.IsEmpty);

        var safety = fatigue.Fields().Single(f => f.Name == FatigueResults.FieldNames.SafetyFactor);
        Assert.False(safety.Range.IsEmpty);
        Assert.True(fatigue.MinSafetyFactor > 1);
    }

    /// <summary>Two IDENTICAL tensile cases are a static load: no cycle, infinite life,
    /// and the safety factor degrades to the static margin S_ut/mean — the Goodman
    /// line's mean-axis edge.</summary>
    [Fact]
    public void IdenticalCases_AreAStaticLoad()
    {
        var curve = FatigueMaterials.Steel1045;
        var (fatigue, _, _) = Evaluate(200, 200, curve);
        Assert.Equal(0, fatigue.FiniteLifeNodeCount);
        Assert.Equal(curve.UltimateStrength / 200.0, fatigue.MinSafetyFactor, 8);
    }

    /// <summary>No load at all: no fatigue mechanism, so the factor is NaN (the no-value
    /// spelling) rather than an infinity that would poison a range.</summary>
    [Fact]
    public void NoLoadAtAll_HasNoSafetyFactor()
    {
        var mesh = Mesh();
        var solved = StructuralSolver.SolveAll([Pull(mesh, 0), Pull(mesh, 0)]);
        var fatigue = FatigueAnalysis.Evaluate(solved[0], solved[1], FatigueMaterials.Steel1045);
        Assert.True(double.IsNaN(fatigue.MinSafetyFactor));
        Assert.Equal(-1, fatigue.MinSafetyFactorNode);
        Assert.Equal(0, fatigue.FiniteLifeNodeCount);
    }

    // ---------- publishing ----------

    [Fact]
    public void PublishesFourFieldsWithTheStatedUnits()
    {
        var (fatigue, _, _) = Evaluate(100, 20, FatigueMaterials.Steel1045);
        var fields = fatigue.Fields();
        Assert.Equal(4, fields.Count);
        Assert.Equal("", fields.Single(f => f.Name == FatigueResults.FieldNames.SafetyFactor).Units);
        Assert.Equal(
            "log10(cycles)",
            fields.Single(f => f.Name == FatigueResults.FieldNames.Life).Units);
        Assert.Equal("MPa", fields.Single(f => f.Name == FatigueResults.FieldNames.Alternating).Units);
        Assert.Equal("MPa", fields.Single(f => f.Name == FatigueResults.FieldNames.Mean).Units);
        Assert.All(fields, f => Assert.Equal(fatigue.Mesh.NodeCount, f.Count));
    }

    // ---------- refusals ----------

    [Fact]
    public void RefusesResultsOnDifferentMeshes()
    {
        var a = StructuralSolver.Solve(Pull(Mesh(), 100));
        var b = StructuralSolver.Solve(Pull(Mesh(), 20));
        var ex = Assert.Throws<FeaException>(
            () => FatigueAnalysis.Evaluate(a, b, FatigueMaterials.Steel1045));
        Assert.Contains("different AnalysisMesh instance", ex.Message);
    }

    [Fact]
    public void RefusesResultsWithMismatchedRecoverySettings()
    {
        var mesh = Mesh();
        var solved = StructuralSolver.SolveAll([Pull(mesh, 100), Pull(mesh, 20)]);
        solved[0].Recovery = StressRecovery.Superconvergent;
        var ex = Assert.Throws<FeaException>(
            () => FatigueAnalysis.Evaluate(solved[0], solved[1], FatigueMaterials.Steel1045));
        Assert.Contains("different nodal stress settings", ex.Message);
    }

    /// <summary>A material with no endurance limit has no infinite-life strength, so a
    /// safety factor needs a stated design life — refused by name, and stated it works.</summary>
    [Fact]
    public void AluminiumNeedsADesignLife()
    {
        var mesh = Mesh();
        var solved = StructuralSolver.SolveAll([Pull(mesh, 100), Pull(mesh, 20)]);
        var ex = Assert.Throws<FeaException>(
            () => FatigueAnalysis.Evaluate(solved[0], solved[1], FatigueMaterials.Aluminium6061T6));
        Assert.Contains("no endurance limit", ex.Message);
        Assert.Contains("DesignLife", ex.Message);

        var fatigue = FatigueAnalysis.Evaluate(
            solved[0], solved[1], FatigueMaterials.Aluminium6061T6,
            new FatigueOptions { DesignLife = 1e7 });
        Assert.Equal(FatigueMaterials.Aluminium6061T6.StressAt(1e7), fatigue.DesignStrength);
    }

    [Fact]
    public void RefusesADesignLifeBelowOneCycle()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FatigueOptions { DesignLife = 0.2 });
    }
}
