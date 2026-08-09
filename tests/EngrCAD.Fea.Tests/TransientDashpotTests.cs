using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The transient solver integrating the model's OWN damping — a discrete dashpot and
/// per-region viscous coefficients — rather than the proportional run option.
///
/// <para><b>Why this needs its own verification and could not be bolted on.</b> A model-carried
/// C is non-proportional in general, so it enters the effective stiffness and every step's
/// right-hand side as an assembled matrix rather than as the <c>alpha·(M·x) + beta·(K·x)</c>
/// products the Rayleigh option takes. A sign slip in any of those places leaves a response
/// that still decays and is still plausible — which is exactly the failure the single-degree-of-
/// freedom fixture exists to catch, because its reduced system is <c>1 x 1</c> and its damped
/// closed form is exact (<see cref="TransientFixtures"/>). A grounded dashpot along the one free
/// axis puts <c>c</c> on that DOF alone, so the reduced damping is exactly <c>[c]</c> and the
/// modal damping ratio is <c>zeta = c / (2·m·omega)</c> with no fitting.</para>
///
/// <para>The energy-balance identity re-derived with the C term is the second oracle
/// (<see cref="TransientSolveReport.EnergyBalanceResidual"/>): the dissipation the report
/// accumulates as <c>integral(v'·C·v dt)</c> must equal the energy that left the system, to
/// round-off, whichever matrix C is.</para>
/// </summary>
public class TransientDashpotTests(ITestOutputHelper output)
{
    /// <summary>The trapezoidal rule's damped phase error, a maximum reached inside the run
    /// when the run is long enough — the same prediction the Rayleigh cases use.</summary>
    private static double DampedPhaseError(double omega, double dt, double zeta)
    {
        double w = omega * dt;
        return w * w / (12.0 * zeta * Math.E);
    }

    [Fact]
    public void AGroundedDashpot_DecaysByTheDampedClosedForm()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, mass, omega) = TransientFixtures.Properties(model, node);

        const double zeta = 0.02;
        // A grounded dashpot along the one free axis: its 3x3 block c·a·a' has c on the XX
        // entry alone, so the reduced 1x1 damping is exactly [c] and zeta = c/(2·m·omega).
        double c = 2.0 * mass * omega * zeta;
        model.Dashpot(node, new Vector3d(1, 0, 0), c);

        const double u0 = 0.01;
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(u0, 0, 0);

        double period = 2 * Math.PI / omega;
        double dt = period / 200;
        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, 200 * 10) { InitialDisplacement = initial });

        // The response is the model's own damping, so the report must say so.
        output.WriteLine(results.Report.ToText());
        Assert.Contains("dashpot", results.Report.Damping);

        double worst = TransientFixtures.WorstError(
            results, node, t => TransientFixtures.DampedFreeVibration(u0, omega, zeta, t));
        double predicted = u0 * DampedPhaseError(omega, dt, zeta);
        output.WriteLine(
            $"grounded dashpot (zeta {zeta:P1}): worst error {worst:E4} ({worst / u0:P4}), "
            + $"predicted {predicted:E4} ({predicted / u0:P4})");
        Assert.InRange(worst, 0.9 * predicted, 1.1 * predicted);

        // The energy the report says was dissipated is exactly the energy that left the
        // system — the balance identity, now with an assembled C in it rather than products.
        Assert.True(
            results.Report.EnergyBalanceResidual < 1e-12,
            $"energy balance {results.Report.EnergyBalanceResidual:E2}");
    }

    [Fact]
    public void AGroundedDashpotUnderAStepLoad_MatchesTheClosedForm()
    {
        // The load path with an assembled C: the damping enters both the effective stiffness
        // and the right-hand side, and the damped dynamic amplification factor is below 2 by a
        // known amount, so a sign error in either place is caught by a closed form and by the
        // exact DAF at once.
        var model = TransientFixtures.SingleDof(out int node);
        var (stiffness, mass, omega) = TransientFixtures.Properties(model, node);

        const double zeta = 0.05;
        double c = 2.0 * mass * omega * zeta;
        model.Dashpot(node, new Vector3d(1, 0, 0), c);

        const double force = 1000.0;
        double staticDeflection = force / stiffness;
        model.NodalForce(node, new Vector3d(force, 0, 0));

        double period = 2 * Math.PI / omega;
        double dt = period / 200;
        var results = TransientSolver.Solve(model, new TransientSolveOptions(dt, 200 * 8));

        double worst = TransientFixtures.WorstError(
            results, node,
            t => TransientFixtures.DampedStepResponse(staticDeflection, omega, zeta, t));
        double predicted = staticDeflection * DampedPhaseError(omega, dt, zeta);
        output.WriteLine(
            $"dashpot step (zeta {zeta:P1}): worst error {worst:E4} "
            + $"({worst / staticDeflection:P5}), predicted {predicted:E4}");
        Assert.InRange(worst, 0.9 * predicted, 1.1 * predicted);

        double daf = TransientFixtures.PeakX(results, node) / staticDeflection;
        double exactDaf = 1 + Math.Exp(-zeta * Math.PI / Math.Sqrt(1 - zeta * zeta));
        output.WriteLine($"damped DAF {daf:G8} against the exact {exactDaf:G8}");
        Assert.Equal(exactDaf, daf, 4);
    }

    [Fact]
    public void ModelCarriedRayleigh_AgreesWithTheOptionRayleigh()
    {
        // A uniform Rayleigh damping stated on the MODEL assembles C = alpha·M + beta·K as a
        // matrix; the same statement on the OPTIONS takes it as products dampA·(M·x) +
        // dampB·(K·x). Different arithmetic, same physics — so the two runs must agree to
        // round-off. This is the cross-check that the assembled-matrix path is the products
        // path by another route, which is what makes the non-proportional feature trustworthy:
        // the general path reproduces the special case it generalises.
        var optionModel = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(optionModel, node);
        const double force = 1000.0;
        optionModel.NodalForce(node, new Vector3d(force, 0, 0));

        var damping = RayleighDamping.MassProportional(omega / (2 * Math.PI), 0.05)
            with { Beta = 1e-6 };

        var modelModel = TransientFixtures.SingleDof(out _);
        modelModel.NodalForce(node, new Vector3d(force, 0, 0));
        modelModel.SetDamping(damping);

        double period = 2 * Math.PI / omega;
        double dt = period / 200;
        int steps = 200 * 8;

        var viaOption = TransientSolver.Solve(
            optionModel, new TransientSolveOptions(dt, steps) { Damping = damping });
        var viaModel = TransientSolver.Solve(
            modelModel, new TransientSolveOptions(dt, steps));

        Assert.Equal(viaOption.States.Count, viaModel.States.Count);
        double peak = 0, worst = 0;
        for (int s = 0; s < viaOption.States.Count; s++)
        {
            double a = viaOption.States[s].DisplacementAt(node).X;
            double b = viaModel.States[s].DisplacementAt(node).X;
            peak = Math.Max(peak, Math.Abs(a));
            worst = Math.Max(worst, Math.Abs(a - b));
        }
        output.WriteLine(
            $"model-carried vs option Rayleigh: worst |diff| {worst:E3}, peak {peak:E3}, "
            + $"relative {worst / peak:E3}");
        // The matrix path sums element entries then multiplies; the product path multiplies
        // then sums — so a few ulps, not the bit, is the honest bar.
        Assert.True(worst / peak < 1e-11, $"relative disagreement {worst / peak:E3}");
    }

    [Fact]
    public void UndampedRun_IsUnchanged_NoMatrixAssembled()
    {
        // A model that states no damping and no run damping must run exactly as it always did:
        // no C matrix is assembled, and the report says "undamped". This is the statement that
        // the common case stays on the bit-identical fast path.
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        const double u0 = 0.01;
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(u0, 0, 0);

        double dt = 2 * Math.PI / omega / 100;
        var results = TransientSolver.Solve(
            model, new TransientSolveOptions(dt, 400) { InitialDisplacement = initial });

        Assert.Equal("undamped", results.Report.Damping);
        // Neutrally damped: the amplitude is the initial displacement to round-off after
        // four periods.
        double peak = TransientFixtures.PeakX(results, node);
        Assert.InRange(peak / u0, 1 - 1e-10, 1 + 1e-10);
    }
}
