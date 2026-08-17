using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Verification for the directional failure criteria — the answer to "MaxVonMises on a
/// composite is a number with no engineering meaning".
///
/// <para><b>Every criterion is checked against a CLOSED FORM, never against another
/// criterion.</b> Three families of them: the uniaxial reductions (a pure fibre-direction
/// tension must reach failure at exactly Xt, whatever the criterion, which for Tsai–Wu is an
/// algebraic identity rather than an arrangement), the classical off-axis strength curves
/// (max-stress's three-branch minimum and Tsai–Hill's
/// <c>1/sigma² = c⁴/X² + s⁴/Y² + s²c²(1/S² − 1/X²)</c>), and the definition of the strength
/// ratio itself — scale the load by R, re-solve, and the index must land on exactly 1.</para>
///
/// <para><b>The material-frame rotation has its own independent oracle</b>, for §3h's
/// reason: production evaluates <c>e_i · (sigma · e_j)</c> while the oracle here multiplies
/// explicit 3x3 matrices, so a transposed rotation cannot pass both.</para>
///
/// <para>Strengths are the ⚠ transcribed T300/5208 graphite/epoxy row quoted by R. M. Jones,
/// <i>Mechanics of Composite Materials</i> (2nd ed.) and Tsai &amp; Hahn — verify against a
/// real data sheet before designing anything.</para>
/// </summary>
public class CompositeFailureTests(ITestOutputHelper output)
{
    // ⚠ Transcribed, verify against a data sheet: T300/5208 graphite/epoxy.
    private const double E1 = 181_000.0;   // MPa
    private const double E2 = 10_300.0;
    private const double Nu12 = 0.28;
    private const double G12 = 7_170.0;
    private const double Xt = 1_500.0;     // MPa
    private const double Xc = 1_500.0;
    private const double Yt = 40.0;
    private const double Yc = 246.0;
    private const double Sc = 68.0;

    private static LaminaStrength Strength() => new(Xt, Xc, Yt, Yc, Sc, "T300/5208");

    private static Material Carrier => new("lamina carrier", E1, 0.3, 1.6e-9);

    private static Frame3d RotatedAboutZ(double degrees)
    {
        double a = degrees * Math.PI / 180.0;
        return Frame3d.FromOrthonormal(
            Vector3d.Zero,
            new Vector3d(Math.Cos(a), Math.Sin(a), 0),
            new Vector3d(-Math.Sin(a), Math.Cos(a), 0));
    }

    private static ElasticLaw Lamina(Frame3d frame) =>
        ElasticLaw.TransverselyIsotropic(frame, E1, E2, Nu12, 0.4, G12, "T300/5208");

    // ---- the uniaxial reductions, which are identities ----------------------------------

    [Theory]
    [InlineData(FailureCriterion.MaxStress)]
    [InlineData(FailureCriterion.TsaiHill)]
    [InlineData(FailureCriterion.TsaiWu)]
    public void EveryCriterionReachesFailureAtExactlyTheUniaxialAllowable(FailureCriterion criterion)
    {
        // All three criteria are calibrated on the same five uniaxial tests, so all three
        // must reproduce them exactly. For Tsai-Wu that is algebra rather than arrangement:
        // with F1 = 1/Xt - 1/Xc and F11 = 1/(Xt Xc), the discriminant F1^2 + 4 F11 collapses
        // to (1/Xt + 1/Xc)^2 and R comes out as exactly Xt/sigma.
        var s = Strength();
        const double sigma = 100.0;

        Assert.Equal(Xt / sigma, s.Evaluate(criterion, sigma, 0, 0).StrengthRatio, Xt / sigma * 1e-12);
        Assert.Equal(Xc / sigma, s.Evaluate(criterion, -sigma, 0, 0).StrengthRatio, Xc / sigma * 1e-12);
        Assert.Equal(Yt / sigma, s.Evaluate(criterion, 0, sigma, 0).StrengthRatio, Yt / sigma * 1e-12);
        Assert.Equal(Yc / sigma, s.Evaluate(criterion, 0, -sigma, 0).StrengthRatio, Yc / sigma * 1e-12);
        Assert.Equal(Sc / sigma, s.Evaluate(criterion, 0, 0, sigma).StrengthRatio, Sc / sigma * 1e-12);
        // Shear has no sign: a lamina is as strong in one shear direction as the other.
        Assert.Equal(Sc / sigma, s.Evaluate(criterion, 0, 0, -sigma).StrengthRatio, Sc / sigma * 1e-12);

        // And the index is the load fraction, so at the allowable itself it is exactly 1.
        Assert.Equal(1.0, s.Evaluate(criterion, Xt, 0, 0).Index, 1e-12);
        Assert.Equal(1.0, s.Evaluate(criterion, 0, -Yc, 0).Index, 1e-12);
    }

    [Fact]
    public void MaxStressNamesTheModeThatDroveIt()
    {
        // The non-interactive criterion's one advantage over the quadratic ones, and a real
        // engineering distinction: matrix cracking at Yt is a different event from fibre
        // failure at Xt, and a designer treats them differently.
        var s = Strength();
        Assert.Equal(FailureMode.FibreTension, s.Evaluate(FailureCriterion.MaxStress, 100, 0, 0).Mode);
        Assert.Equal(FailureMode.FibreCompression, s.Evaluate(FailureCriterion.MaxStress, -100, 0, 0).Mode);
        Assert.Equal(FailureMode.MatrixTension, s.Evaluate(FailureCriterion.MaxStress, 0, 10, 0).Mode);
        Assert.Equal(FailureMode.MatrixCompression, s.Evaluate(FailureCriterion.MaxStress, 0, -10, 0).Mode);
        Assert.Equal(FailureMode.Shear, s.Evaluate(FailureCriterion.MaxStress, 0, 0, 10).Mode);
        Assert.Equal(FailureMode.None, s.Evaluate(FailureCriterion.MaxStress, 0, 0, 0).Mode);
        Assert.Equal(
            double.PositiveInfinity, s.Evaluate(FailureCriterion.MaxStress, 0, 0, 0).StrengthRatio);

        // A modest fibre stress beside a transverse stress a twentieth its size still fails
        // TRANSVERSELY, which is the whole reason a scalar equivalent stress is useless here.
        var mixed = s.Evaluate(FailureCriterion.MaxStress, 600, 30, 0);
        Assert.Equal(FailureMode.MatrixTension, mixed.Mode);
        output.WriteLine($"sigma1 600, sigma2 30: index {mixed.Index:F3} in {mixed.Mode}");
    }

    [Fact]
    public void TheQuadraticCriteriaInteractWhereMaxStressDoesNot()
    {
        // The measurement that says the three criteria are not spellings of each other: at a
        // state safely inside every individual allowable, the interactive criteria are
        // measurably closer to failure than max-stress is.
        var s = Strength();
        const double s1 = 900, s2 = 25, t = 40;
        double max = s.Evaluate(FailureCriterion.MaxStress, s1, s2, t).Index;
        double hill = s.Evaluate(FailureCriterion.TsaiHill, s1, s2, t).Index;
        double wu = s.Evaluate(FailureCriterion.TsaiWu, s1, s2, t).Index;
        output.WriteLine($"max-stress {max:F4}, Tsai-Hill {hill:F4}, Tsai-Wu {wu:F4}");
        Assert.True(max < 1.0);
        Assert.True(hill > max, "an interactive criterion must be at least as severe here");
        Assert.True(wu > max);
    }

    // ---- the off-axis strength curves ---------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(60.0)]
    [InlineData(75.0)]
    [InlineData(90.0)]
    public void TheOffAxisStrengthMatchesItsClassicalClosedForm(double degrees)
    {
        // A unidirectional lamina pulled at `degrees` to its fibres. The material-frame state
        // is sigma1 = sigma c^2, sigma2 = sigma s^2, tau12 = -sigma s c, and both criteria
        // then have closed-form off-axis strengths:
        //   max-stress: sigma_f = min(Xt/c^2, Yt/s^2, S/|sc|)
        //   Tsai-Hill:  1/sigma_f^2 = c^4/Xt^2 + s^4/Yt^2 + s^2 c^2 (1/S^2 - 1/Xt^2)
        // Both are textbook results independent of anything this file computes.
        var s = Strength();
        const double sigma = 30.0;
        double a = degrees * Math.PI / 180.0;
        double c = Math.Cos(a), sn = Math.Sin(a);
        double c2 = c * c, s2 = sn * sn;

        double sigma1 = sigma * c2;
        double sigma2 = sigma * s2;
        double tau = -sigma * sn * c;

        double maxStressFailure = Math.Min(
            c2 > 0 ? Xt / c2 : double.PositiveInfinity,
            Math.Min(
                s2 > 0 ? Yt / s2 : double.PositiveInfinity,
                Math.Abs(sn * c) > 0 ? Sc / Math.Abs(sn * c) : double.PositiveInfinity));
        double hillFailure = 1.0 / Math.Sqrt(
            c2 * c2 / (Xt * Xt) + s2 * s2 / (Yt * Yt) + s2 * c2 * (1.0 / (Sc * Sc) - 1.0 / (Xt * Xt)));

        double maxStressRatio = s.Evaluate(FailureCriterion.MaxStress, sigma1, sigma2, tau).StrengthRatio;
        double hillRatio = s.Evaluate(FailureCriterion.TsaiHill, sigma1, sigma2, tau).StrengthRatio;

        output.WriteLine(
            $"{degrees:F0} deg: off-axis strength max-stress {maxStressRatio * sigma:F2} MPa "
            + $"(closed form {maxStressFailure:F2}), Tsai-Hill {hillRatio * sigma:F2} "
            + $"(closed form {hillFailure:F2})");

        Assert.Equal(maxStressFailure, maxStressRatio * sigma, maxStressFailure * 1e-12);
        Assert.Equal(hillFailure, hillRatio * sigma, hillFailure * 1e-12);
    }

    [Fact]
    public void TheOffAxisStrengthCollapsesWithinTenDegreesOfTheFibre()
    {
        // The engineering content of that curve, worth asserting because it is the reason
        // the criteria exist: rotating a unidirectional lamina 10 degrees off its fibre
        // throws away most of its strength.
        var s = Strength();
        double At(double degrees)
        {
            double a = degrees * Math.PI / 180.0;
            double c = Math.Cos(a), sn = Math.Sin(a);
            return s.Evaluate(
                FailureCriterion.TsaiHill,
                c * c, sn * sn, -sn * c).StrengthRatio;
        }
        double onAxis = At(0), ten = At(10), forty5 = At(45), ninety = At(90);
        output.WriteLine(
            $"off-axis strength: 0 deg {onAxis:F0}, 10 deg {ten:F0}, 45 deg {forty5:F0}, "
            + $"90 deg {ninety:F0} MPa");
        Assert.Equal(Xt, onAxis, Xt * 1e-9);
        Assert.Equal(Yt, ninety, Yt * 1e-9);
        Assert.True(ten < 0.25 * onAxis, $"10 degrees off axis measured {ten:F0} of {onAxis:F0}");
    }

    // ---- the frame, with an independent oracle -------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(-45.0)]
    public void TheMaterialFrameRotationMatchesAnIndependentMatrixOracle(double degrees)
    {
        // Production evaluates e_i . (sigma . e_j); the oracle multiplies R' sigma R as
        // explicit 3x3 matrices. Two spellings of one rotation, so a transposed frame cannot
        // pass both - the §3h rule applied to the post-processing side.
        var frame = RotatedAboutZ(degrees);
        var law = Lamina(frame);
        var global = new SymmetricTensor3(25, -7, 3, 11, -2, 5);

        var actual = law.ToMaterialFrame(global);
        var expected = RotateByMatrices(frame, global);

        double scale = 25.0;
        Assert.Equal(expected.Xx, actual.Xx, scale * 1e-12);
        Assert.Equal(expected.Yy, actual.Yy, scale * 1e-12);
        Assert.Equal(expected.Zz, actual.Zz, scale * 1e-12);
        Assert.Equal(expected.Xy, actual.Xy, scale * 1e-12);
        Assert.Equal(expected.Xz, actual.Xz, scale * 1e-12);
        Assert.Equal(expected.Yz, actual.Yz, scale * 1e-12);

        // The closed form for a uniaxial global state, as a third reading: sigma1 = sigma c^2.
        double a = degrees * Math.PI / 180.0;
        var uniaxial = law.ToMaterialFrame(new SymmetricTensor3(50, 0, 0, 0, 0, 0));
        Assert.Equal(50 * Math.Cos(a) * Math.Cos(a), uniaxial.Xx, 1e-10);
        Assert.Equal(50 * Math.Sin(a) * Math.Sin(a), uniaxial.Yy, 1e-10);
        Assert.Equal(-50 * Math.Sin(a) * Math.Cos(a), uniaxial.Xy, 1e-10);
    }

    private static SymmetricTensor3 RotateByMatrices(Frame3d frame, in SymmetricTensor3 global)
    {
        var x = frame.X;
        var y = frame.Y;
        var z = frame.Z;
        double[] r = [x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z];   // columns = material axes
        double[] sg =
        [
            global.Xx, global.Xy, global.Xz,
            global.Xy, global.Yy, global.Yz,
            global.Xz, global.Yz, global.Zz,
        ];
        var tmp = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += r[k * 3 + i] * sg[k * 3 + j];   // R' sigma
                tmp[i * 3 + j] = sum;
            }
        var sm = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                    sum += tmp[i * 3 + k] * r[k * 3 + j];  // (R' sigma) R
                sm[i * 3 + j] = sum;
            }
        return new SymmetricTensor3(sm[0], sm[4], sm[8], sm[1], sm[2], sm[5]);
    }

    // ---- through the solver --------------------------------------------------------------

    /// <summary>
    /// A prismatic bar of one off-axis lamina under a uniform traction, restrained
    /// statically. The exact solution is a uniform uniaxial STRESS state whatever the
    /// anisotropy, so the strain is constant, the displacement field linear and the solve
    /// exact — leaving the criterion as the only thing under test.
    /// </summary>
    private static (StructuralResults Results, AnalysisMesh Mesh) SolveOffAxisBar(
        double degrees, double sigma, LaminaStrength? strength)
    {
        const double length = 2.0, width = 1.5, height = 1.0;
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, width, height), 4, 3, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Carrier);
        model.SetElasticity(0, Lamina(RotatedAboutZ(degrees)));
        if (strength is not null)
            model.SetStrength(0, strength);

        model.Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX), Dof.X);
        int origin = FeaPatchTests.FindNode(mesh, Vector3d.Zero);
        int alongY = FeaPatchTests.FindNode(mesh, new Vector3d(0, width, 0));
        model.FixNode(origin, Dof.Y | Dof.Z);
        model.FixNode(alongY, Dof.Z);
        model.Traction(
            Facets.OnPlane(new Vector3d(length, 0, 0), Vector3d.UnitX),
            new Vector3d(sigma, 0, 0));

        return (StructuralSolver.Solve(model), mesh);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(90.0)]
    public void ASolvedOffAxisBarReportsTheClosedFormStrengthRatio(double degrees)
    {
        // End to end: the criterion evaluated on the RECOVERED stress, rotated through the
        // region's own law frame, against the same closed form the analytic tests use.
        const double sigma = 30.0;
        var (results, _) = SolveOffAxisBar(degrees, sigma, Strength());
        var failure = FailureAnalysis.Evaluate(results, FailureCriterion.TsaiHill);

        double a = degrees * Math.PI / 180.0;
        double c2 = Math.Cos(a) * Math.Cos(a), s2 = Math.Sin(a) * Math.Sin(a);
        double expected = 1.0 / Math.Sqrt(
            c2 * c2 / (Xt * Xt) + s2 * s2 / (Yt * Yt)
            + s2 * c2 * (1.0 / (Sc * Sc) - 1.0 / (Xt * Xt))) / sigma;

        output.WriteLine(
            $"{degrees:F0} deg: solved R = {failure.MinStrengthRatio:F4} against closed form "
            + $"{expected:F4}; out-of-plane fraction {failure.MaxOutOfPlaneFraction:E2}");

        Assert.Equal(expected, failure.MinStrengthRatio, expected * 1e-9);
        Assert.Equal(expected, 1.0 / failure.MaxFailureIndex, expected * 1e-9);
        Assert.Equal(failure.Mesh.NodeCount, failure.CoveredNodes);
        Assert.True(failure.IsSafe);
        // A bar loaded in its own plane with free faces: the plane-stress idealisation the
        // criterion rests on is exact here, and the diagnostic says so.
        Assert.True(failure.MaxOutOfPlaneFraction < 1e-9,
            $"out-of-plane fraction {failure.MaxOutOfPlaneFraction:E3}");
    }

    [Theory]
    [InlineData(FailureCriterion.MaxStress)]
    [InlineData(FailureCriterion.TsaiHill)]
    [InlineData(FailureCriterion.TsaiWu)]
    public void ScalingTheLoadByTheStrengthRatioLandsExactlyOnTheFailureSurface(
        FailureCriterion criterion)
    {
        // The strength ratio verified by its own DEFINITION, through a real re-solve rather
        // than by restating the formula - the oracle FatigueAnalysis.SafetyFactor already
        // uses, and the one an independently rewritten formula could not provide (it would
        // agree with a broken implementation making the same mistake).
        const double sigma = 30.0;
        var (results, _) = SolveOffAxisBar(35.0, sigma, Strength());
        double ratio = FailureAnalysis.Evaluate(results, criterion).MinStrengthRatio;

        var (scaled, _) = SolveOffAxisBar(35.0, sigma * ratio, Strength());
        var atFailure = FailureAnalysis.Evaluate(scaled, criterion);

        output.WriteLine(
            $"{criterion}: R = {ratio:F4}, index at R*load = {atFailure.MaxFailureIndex:F12}");
        Assert.Equal(1.0, atFailure.MaxFailureIndex, 1e-9);
        Assert.Equal(1.0, atFailure.MinStrengthRatio, 1e-9);

        // IsSafe is deliberately NOT asserted at exactly R: sitting ON the failure surface is
        // failure by the criterion's own definition, but a re-solve lands on 1 to round-off
        // and the verdict there is decided by the last bit. The claim with content is that
        // the predicate brackets R - a per cent under is safe, a per cent over is not.
        var (under, _) = SolveOffAxisBar(35.0, sigma * ratio * 0.99, Strength());
        var (over, _) = SolveOffAxisBar(35.0, sigma * ratio * 1.01, Strength());
        Assert.True(FailureAnalysis.Evaluate(under, criterion).IsSafe);
        Assert.False(FailureAnalysis.Evaluate(over, criterion).IsSafe);
    }

    [Fact]
    public void TheElementIndexAgreesWithTheNodalOneOnAUniformState()
    {
        // A uniform stress state has nothing for the averaging to smooth, so the per-element
        // index and the per-node one must agree exactly - which is what makes them
        // comparable on a state that is NOT uniform.
        var (results, mesh) = SolveOffAxisBar(30.0, 30.0, Strength());
        var failure = FailureAnalysis.Evaluate(results, FailureCriterion.TsaiWu);
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            Assert.Equal(
                failure.MaxFailureIndex, failure.ElementFailureIndex(e),
                failure.MaxFailureIndex * 1e-9);
        }
    }

    // ---- the no-value convention and the refusals -----------------------------------------

    [Fact]
    public void ARegionWithNoStrengthPublishesNaNRatherThanZero()
    {
        // The split the design turns on: NO strengths at all is a refusal (there is nothing
        // to measure and an all-NaN field would look like a solve that ran), while SOME
        // regions stating one leaves the others at NaN - the "no value" spelling ranging and
        // the colour map already skip. A zero there would paint the safest possible colour
        // on a part nobody has checked.
        IReadOnlyList<AnalysisBody> bodies =
        [
            new AnalysisBody(
                EngrCAD.Mesh.MeshPrimitives.Box(Aabb.FromPoints([Vector3d.Zero, new(1, 1, 1)])),
                Carrier, "composite"),
            new AnalysisBody(
                EngrCAD.Mesh.MeshPrimitives.Box(
                    Aabb.FromPoints([new(2, 0, 0), new(3, 1, 1)])),
                Carrier, "metal"),
        ];
        var tets = TetMesher.Mesh(bodies);
        var mesh = AnalysisMesh.Of(tets);
        var model = StructuralModel.For(mesh, bodies);
        model.SetElasticity(0, Lamina(Frame3d.WorldXY));
        model.SetStrength(0, Strength());
        model.Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX));
        model.Fix(Facets.OnPlane(new Vector3d(2, 0, 0), Vector3d.UnitX));
        model.Traction(
            Facets.OnPlane(new Vector3d(1, 0, 0), Vector3d.UnitX), new Vector3d(20, 0, 0));
        model.Traction(
            Facets.OnPlane(new Vector3d(3, 0, 0), Vector3d.UnitX), new Vector3d(20, 0, 0));

        var results = StructuralSolver.Solve(model);
        var failure = FailureAnalysis.Evaluate(results, FailureCriterion.TsaiWu);

        int nan = failure.FailureIndex.Count(double.IsNaN);
        output.WriteLine(
            $"{failure.CoveredNodes} of {mesh.NodeCount} nodes covered, {nan} NaN");
        Assert.True(nan > 0, "the strengthless body's nodes must read NaN");
        Assert.Equal(mesh.NodeCount - nan, failure.CoveredNodes);
        Assert.True(failure.CoveredNodes > 0);
        // MaxFailureIndex skips the NaNs rather than returning one.
        Assert.False(double.IsNaN(failure.MaxFailureIndex));
    }

    [Fact]
    public void RefusalsAreByName()
    {
        // No strength anywhere: refused, with the way out named.
        var (results, _) = SolveOffAxisBar(30.0, 30.0, strength: null);
        var noStrength = Assert.Throws<FeaException>(
            () => FailureAnalysis.Evaluate(results, FailureCriterion.TsaiWu));
        Assert.Contains("SetStrength", noStrength.Message, StringComparison.Ordinal);

        // A compressive allowable transcribed with its sign - the trap the convention has.
        var negative = Assert.Throws<FeaException>(() => new LaminaStrength(Xt, -Xc, Yt, Yc, Sc));
        Assert.Contains("positive MAGNITUDES", negative.Message, StringComparison.Ordinal);
        Assert.Throws<FeaException>(() => new LaminaStrength(Xt, Xc, 0, Yc, Sc));

        // An interaction coefficient that opens the failure surface.
        var wide = Assert.Throws<FeaException>(() => Strength() with { F12Star = -1.0 });
        Assert.Contains("positive definite", wide.Message, StringComparison.Ordinal);
        Assert.Throws<FeaException>(() => Strength() with { F12Star = 1.5 });
    }

    [Fact]
    public void TheInteractionCoefficientIsAStatedChoiceThatMovesTheAnswer()
    {
        // F12* is the one Tsai-Wu number no uniaxial test determines, so it is a parameter
        // rather than a constant - and this measures that the choice matters, which is why
        // burying it would have been wrong. It cannot move the uniaxial reductions (those
        // are calibration points), only the biaxial interior.
        var nominal = Strength();
        var alternative = nominal with { F12Star = 0.0 };

        Assert.Equal(
            nominal.Evaluate(FailureCriterion.TsaiWu, 100, 0, 0).StrengthRatio,
            alternative.Evaluate(FailureCriterion.TsaiWu, 100, 0, 0).StrengthRatio,
            1e-12);

        double a = nominal.Evaluate(FailureCriterion.TsaiWu, 600, 20, 0).StrengthRatio;
        double b = alternative.Evaluate(FailureCriterion.TsaiWu, 600, 20, 0).StrengthRatio;
        output.WriteLine($"biaxial R at F12* = -0.5: {a:F4}; at 0: {b:F4}");
        Assert.True(Math.Abs(a - b) > 1e-3 * a, "the interaction term must reach a biaxial state");
    }
}
