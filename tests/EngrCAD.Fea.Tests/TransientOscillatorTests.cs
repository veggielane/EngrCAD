using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The single-degree-of-freedom verification set: a finite element model whose reduced
/// system is exactly <c>1 x 1</c>, checked against the closed-form solutions of
/// <c>m·q'' + c·q' + k·q = f(t)</c>.
///
/// <para><b>There is no discretization error in any of these.</b> The fixture is not a coarse
/// approximation of an oscillator, it IS one — see <see cref="TransientFixtures"/> for why
/// that is a legitimate use of a restraint pattern the modal work recorded as wrong for
/// MODELLING — so every difference measured here belongs to the time integrator and nothing
/// else. The stiffness and the frequency the closed forms are built from come from the static
/// and modal solvers respectively, so the reference is never the transient's own arithmetic.</para>
///
/// <para><b>The errors are PREDICTED and then measured, not bounded.</b> A threshold says
/// only that a run was not catastrophically wrong; the trapezoidal rule's error at a stated
/// step has a closed form of its own (see <see cref="PhaseError"/>), so every accuracy case
/// here asserts a narrow band around it. That is what separates "the integrator works" from
/// "the integrator is THIS integrator" — a scheme with the wrong beta would still produce a
/// decaying, plausible, wrong answer inside any loose threshold.</para>
/// </summary>
public class TransientOscillatorTests(ITestOutputHelper output)
{
    /// <summary>Steps per period, and periods, for the accuracy cases. 100 steps per period
    /// is an ordinary engineering choice and leaves the second-order error visible rather
    /// than at round-off.</summary>
    private const int StepsPerPeriod = 100;

    private const int Periods = 10;

    /// <summary>
    /// The trapezoidal rule's accumulated phase error after <paramref name="periods"/>
    /// periods, as a fraction of the amplitude.
    ///
    /// <para><b>The constant is 1/12 and it is derived rather than quoted</b>, because the
    /// two plausible values differ by exactly the factor a wrong prediction hides behind.
    /// Newmark's <c>(1/4, 1/2)</c> amplification matrix for <c>q'' + omega²·q = 0</c> has
    /// eigenvalues <c>(1 - W²/4 +/- iW)/(1 + W²/4)</c> with <c>W = omega·dt</c>; the modulus
    /// is exactly 1 (which is why the amplitude never moves), and the argument is
    /// <c>arctan(W/(1 - W²/4)) = W - W³/12 + O(W^5)</c>. So the ALGORITHMIC frequency is
    /// <c>omega·(1 - W²/12)</c>, the phase falls behind by <c>2·pi·N·W²/12</c> radians after
    /// N periods, and the displacement error is the amplitude times that.</para>
    /// </summary>
    private static double PhaseError(double omega, double dt, double periods)
    {
        double w = omega * dt;
        return 2 * Math.PI * periods * w * w / 12.0;
    }

    /// <summary>
    /// The same error for a DAMPED run, where the growing phase lag multiplies a decaying
    /// amplitude: <c>e^(-zeta·omega·t)·omega·t·W²/12</c> peaks at <c>t = 1/(zeta·omega)</c>,
    /// giving <c>W²/(12·zeta·e)</c> as a fraction of the initial amplitude. Whether that peak
    /// falls inside the run is the caller's to arrange.
    /// </summary>
    private static double DampedPhaseError(double omega, double dt, double zeta)
    {
        double w = omega * dt;
        return w * w / (12.0 * zeta * Math.E);
    }

    [Fact]
    public void FreeVibration_MatchesTheCosineToTheSchemesOwnOrder()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        double period = 2 * Math.PI / omega;
        double dt = period / StepsPerPeriod;
        const double u0 = 0.01;

        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(u0, 0, 0);

        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, StepsPerPeriod * Periods)
            {
                InitialDisplacement = initial,
            });

        double worst = TransientFixtures.WorstError(
            results, node, t => TransientFixtures.FreeVibration(u0, omega, t));
        double predicted = u0 * PhaseError(omega, dt, Periods);
        output.WriteLine(
            $"free vibration: {Periods} periods at {StepsPerPeriod} steps/period, "
            + $"worst |u - u0.cos(wt)| = {worst:E4} ({worst / u0:P3} of the amplitude), "
            + $"predicted {predicted:E4} ({predicted / u0:P3})");
        output.WriteLine(results.Report.ToText());

        // Below the prediction, and close to it: the error is the phase lag times the
        // amplitude only while the lag is small, and sin(x) < x is what puts the measurement
        // a few percent under rather than over.
        Assert.InRange(worst, 0.9 * predicted, 1.02 * predicted);

        // The amplitude never moves: this scheme is neutrally damped, so ten periods later
        // the peak is the initial displacement to round-off.
        double peak = TransientFixtures.PeakX(results, node);
        output.WriteLine($"peak amplitude {peak:G12} against u0 {u0:G12} ({peak / u0 - 1:E2})");
        Assert.InRange(peak / u0, 1 - 1e-10, 1 + 1e-10);
    }

    [Fact]
    public void AnInitialVelocity_GivesTheSineResponse()
    {
        // The impulse case, and the one that fails loudly if the initial velocity is dropped
        // or if a(0) is assumed zero: with u(0) = 0 the whole response is driven by v(0).
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        double period = 2 * Math.PI / omega;
        double dt = period / StepsPerPeriod;
        const double v0 = 100.0;

        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(v0, 0, 0);

        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, StepsPerPeriod * Periods) { InitialVelocity = initial });

        double amplitude = v0 / omega;
        double worst = TransientFixtures.WorstError(
            results, node, t => TransientFixtures.ImpulseResponse(v0, omega, t));
        double predicted = amplitude * PhaseError(omega, dt, Periods);
        output.WriteLine(
            $"impulse: amplitude v0/omega = {amplitude:G8}, worst error {worst:E4} "
            + $"({worst / amplitude:P3}), predicted {predicted:E4} ({predicted / amplitude:P3})");
        Assert.InRange(worst, 0.9 * predicted, 1.02 * predicted);
    }

    [Fact]
    public void AStepLoad_AmplifiesByExactlyTwo()
    {
        // The classic dynamic amplification factor. It is exactly 2 for an undamped
        // oscillator and no approximation enters, so this is an identity check on the load
        // path, the initial acceleration and the integrator at once.
        var model = TransientFixtures.SingleDof(out int node);
        var (stiffness, _, omega) = TransientFixtures.Properties(model, node);
        const double force = 1000.0;
        double staticDeflection = force / stiffness;

        model.NodalForce(node, new Vector3d(force, 0, 0));
        double period = 2 * Math.PI / omega;

        // Half a period lands the peak exactly on a stored step, so the measured peak is the
        // response's own maximum rather than the largest sample near it - which would report
        // a DAF short of 2 by the sampling alone and look like a solver error.
        const int stepsPerHalf = 500;
        double dt = period / (2 * stepsPerHalf);
        var results = TransientSolver.Solve(
            model, new TransientSolveOptions(dt, 2 * stepsPerHalf));

        double peak = TransientFixtures.PeakX(results, node);
        double daf = peak / staticDeflection;
        output.WriteLine($"static {staticDeflection:G8}, peak {peak:G8}, DAF {daf:G10}");
        Assert.Equal(2.0, daf, 4);

        double worst = TransientFixtures.WorstError(
            results, node, t => TransientFixtures.StepResponse(staticDeflection, omega, t));
        double predicted = staticDeflection * PhaseError(omega, dt, 1.0);
        output.WriteLine(
            $"worst |u - u_st(1 - cos wt)| = {worst:E4} ({worst / staticDeflection:P5}), "
            + $"predicted {predicted:E4}");
        Assert.InRange(worst, 0.5 * predicted, 1.02 * predicted);
    }

    [Fact]
    public void ADampedStepLoad_MatchesTheClosedForm()
    {
        // Damping enters both the effective stiffness and the right-hand side, and a sign
        // slip in either leaves a response that still decays and is still plausible. Only a
        // closed form catches it.
        var model = TransientFixtures.SingleDof(out int node);
        var (stiffness, _, omega) = TransientFixtures.Properties(model, node);
        const double force = 1000.0;
        double staticDeflection = force / stiffness;
        model.NodalForce(node, new Vector3d(force, 0, 0));

        const double zeta = 0.05;
        // Mass-proportional damping alone gives exactly this ratio at this frequency, so the
        // closed form's zeta is stated rather than fitted.
        var damping = RayleighDamping.MassProportional(omega / (2 * Math.PI), zeta);
        double period = 2 * Math.PI / omega;
        double dt = period / 200;

        var results = TransientSolver.Solve(
            model, new TransientSolveOptions(dt, 200 * 8) { Damping = damping });

        double worst = TransientFixtures.WorstError(
            results, node,
            t => TransientFixtures.DampedStepResponse(staticDeflection, omega, zeta, t));
        double predicted = staticDeflection * DampedPhaseError(omega, dt, zeta);
        output.WriteLine(
            $"damped step (zeta {zeta:P1}): worst error {worst:E4} "
            + $"({worst / staticDeflection:P5} of the static deflection), "
            + $"predicted {predicted:E4} ({predicted / staticDeflection:P5})");
        output.WriteLine(results.Report.ToText());
        Assert.InRange(worst, 0.9 * predicted, 1.1 * predicted);

        // And the peak amplification is the damped one, below 2 by a known amount.
        double daf = TransientFixtures.PeakX(results, node) / staticDeflection;
        double exactDaf = 1 + Math.Exp(-zeta * Math.PI / Math.Sqrt(1 - zeta * zeta));
        output.WriteLine($"damped DAF {daf:G8} against the exact {exactDaf:G8}");
        Assert.Equal(exactDaf, daf, 4);
    }

    [Fact]
    public void DampedFreeVibration_DecaysByTheLogarithmicDecrement()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        const double zeta = 0.02;
        const double u0 = 0.01;
        var damping = RayleighDamping.MassProportional(omega / (2 * Math.PI), zeta);
        double period = 2 * Math.PI / omega;
        double dt = period / 200;

        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(u0, 0, 0);

        // Ten periods, which contains the error's own peak at t = 1/(zeta.omega) = 7.96
        // periods - so the prediction below is a maximum reached inside the run rather than
        // an extrapolation past its end.
        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, 200 * 10)
            {
                Damping = damping,
                InitialDisplacement = initial,
            });

        double worst = TransientFixtures.WorstError(
            results, node, t => TransientFixtures.DampedFreeVibration(u0, omega, zeta, t));
        double predicted = u0 * DampedPhaseError(omega, dt, zeta);
        output.WriteLine(
            $"damped free vibration: worst error {worst:E4} ({worst / u0:P4}), "
            + $"predicted {predicted:E4} ({predicted / u0:P4})");
        Assert.InRange(worst, 0.9 * predicted, 1.1 * predicted);

        // The energy the report says was dissipated must be the energy that left the system,
        // to round-off - the balance identity with a damping term in it.
        output.WriteLine(results.Report.ToText());
        Assert.True(
            results.Report.EnergyBalanceResidual < 1e-12,
            $"energy balance {results.Report.EnergyBalanceResidual:E2}");
    }

    [Fact]
    public void AHarmonicDrive_SettlesOnTheSteadyStateAmplitude()
    {
        // Two references at once: the closed-form magnification, and HarmonicSolver's own
        // answer for the same drive. The two solvers share no code beyond assembly, so they
        // can only agree if both are right.
        var model = TransientFixtures.SingleDof(out int node);
        var (stiffness, _, omega) = TransientFixtures.Properties(model, node);
        const double force = 1000.0;
        double staticDeflection = force / stiffness;
        model.NodalForce(node, new Vector3d(force, 0, 0));

        const double zeta = 0.05;
        const double ratio = 0.8;
        double drive = ratio * omega;
        double driveHertz = drive / (2 * Math.PI);
        var damping = RayleighDamping.MassProportional(omega / (2 * Math.PI), zeta);

        double drivePeriod = 2 * Math.PI / drive;
        // Long enough for the free transient to decay: e^(-zeta.omega.t) after 40 drive
        // periods is about 4e-5.
        const int drivePeriods = 40;
        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(drivePeriod / 200, 200 * drivePeriods)
            {
                Damping = damping,
                LoadFactor = t => Math.Sin(drive * t),
            });

        // The amplitude over the LAST two drive periods, once the transient has gone.
        double amplitude = 0;
        foreach (var state in results.States)
        {
            if (state.Time >= (drivePeriods - 2) * drivePeriod)
                amplitude = Math.Max(amplitude, Math.Abs(state.DisplacementAt(node).X));
        }
        double exact = TransientFixtures.SteadyStateAmplitude(
            staticDeflection, omega, zeta, drive);
        output.WriteLine(
            $"harmonic drive r = {ratio}: integrated amplitude {amplitude:G8}, "
            + $"closed form {exact:G8} ({amplitude / exact - 1:P3})");
        Assert.Equal(exact, amplitude, 3 * exact / 100);

        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
        var harmonic = HarmonicSolver.Solve(
            modes,
            new HarmonicSolveOptions
            {
                Frequencies = [driveHertz],
                Damping = ModalDamping.Rayleigh(damping),
            });
        double fromHarmonic = harmonic.AmplitudeAt(0)[node].X;
        output.WriteLine(
            $"HarmonicSolver says {fromHarmonic:G8} ({amplitude / fromHarmonic - 1:P3} from the "
            + "time integration)");
        Assert.Equal(fromHarmonic, amplitude, 3 * fromHarmonic / 100);
    }
}
