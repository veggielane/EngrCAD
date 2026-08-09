using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Base (support) excitation for the modal harmonic sweep — a shaker or seismic input driving
/// the structure through its supports rather than by a nodal force.
///
/// <para><b>It needs no new mathematics, and the verification says so three ways.</b> In relative
/// coordinates the modal force is exactly <c>-Gamma_d·a_g</c>, the participation factor the modal
/// results already carry — so a base acceleration <c>a_g</c> produces the SAME relative response
/// as a nodal force <c>m·a_g</c> (the inertial force), which is exact on the single-degree-of-
/// freedom fixture; the resonant relative displacement matches the closed form
/// <c>a_g/(2·zeta·omega_n²)</c>; and a velocity- or displacement-stated input scales the
/// acceleration by <c>omega</c> or <c>omega²</c>, which the sweep carries per frequency.</para>
/// </summary>
public sealed class BaseExcitationTests(ITestOutputHelper output)
{
    private static ModalResults Modes(StructuralModel model) =>
        ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });

    [Fact]
    public void TheModalForceIsMinusTheParticipationFactorTimesTheAcceleration()
    {
        var model = TransientFixtures.SingleDof(out int free);
        var modes = Modes(model);
        const double ag = 1000.0;

        var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = [modes.Mode(1).Frequency],
            Damping = ModalDamping.Uniform(0.02),
            BaseExcitation = new BaseExcitation(new Vector3d(1, 0, 0), BaseMotionKind.Acceleration, ag),
        });

        Assert.True(response.IsRelativeToBase);
        double gamma = modes.Mode(1).ParticipationFactor.X;
        output.WriteLine($"Gamma_x {gamma:G8}, modal force {response.ModalForces[0]:G8}, -Gamma·a_g {-gamma * ag:G8}");
        Assert.Equal(-gamma * ag, response.ModalForces[0], 1e-9 * Math.Abs(gamma * ag));
        _ = free;
    }

    [Fact]
    public void ResonantRelativeDisplacementMatchesTheClosedForm()
    {
        var model = TransientFixtures.SingleDof(out int free);
        var (_, _, omega) = TransientFixtures.Properties(model, free);
        var modes = Modes(model);
        const double zeta = 0.02, ag = 1000.0;

        var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = [omega / (2 * Math.PI)],
            Damping = ModalDamping.Uniform(zeta),
            BaseExcitation = new BaseExcitation(new Vector3d(1, 0, 0), BaseMotionKind.Acceleration, ag),
        });

        // |z(resonance)| = a_g / (2·zeta·omega_n²).
        double measured = response.AmplitudeAt(0)[free].X;
        double closedForm = ag / (2 * zeta * omega * omega);
        output.WriteLine($"relative displacement {measured:G8}, closed form {closedForm:G8}");
        Assert.Equal(closedForm, measured, 1e-6 * closedForm);
    }

    [Fact]
    public void ABaseAccelerationEqualsAnInertialNodalForce()
    {
        // The exact equivalence: a base acceleration a_g gives the same RELATIVE response as a
        // nodal force m·a_g (magnitude), because both project to the same modal force. Two runs
        // sharing no code beyond assembly must then agree across the whole sweep.
        var baseModel = TransientFixtures.SingleDof(out int free);
        var (_, mass, omega) = TransientFixtures.Properties(baseModel, free);
        const double ag = 500.0;
        double fn = omega / (2 * Math.PI);
        double[] sweep = HarmonicSweep.Around(fn, 0.3, 9);

        var baseResponse = HarmonicSolver.Solve(Modes(baseModel), new HarmonicSolveOptions
        {
            Frequencies = sweep,
            Damping = ModalDamping.Uniform(0.03),
            BaseExcitation = new BaseExcitation(new Vector3d(1, 0, 0), BaseMotionKind.Acceleration, ag),
        });

        var nodalModel = TransientFixtures.SingleDof(out _);
        nodalModel.NodalForce(free, new Vector3d(mass * ag, 0, 0));
        var nodalResponse = HarmonicSolver.Solve(Modes(nodalModel), new HarmonicSolveOptions
        {
            Frequencies = sweep,
            Damping = ModalDamping.Uniform(0.03),
        });

        double worst = 0;
        for (int k = 0; k < sweep.Length; k++)
        {
            double b = baseResponse.AmplitudeAt(k)[free].X;
            double n = nodalResponse.AmplitudeAt(k)[free].X;
            worst = Math.Max(worst, Math.Abs(b - n) / Math.Max(b, n));
        }
        output.WriteLine($"worst relative disagreement base vs inertial nodal force: {worst:E3}");
        Assert.True(worst < 1e-9, $"base excitation not equal to the inertial nodal force: {worst:E3}");
    }

    [Fact]
    public void AVelocityInputScalesTheAccelerationByOmega()
    {
        // A base VELOCITY amplitude v implies acceleration omega·v, so at one frequency the
        // velocity-input response equals an acceleration input of omega·v.
        var model = TransientFixtures.SingleDof(out int free);
        var modes = Modes(model);
        double f = 1.3 * modes.Mode(1).Frequency;   // off resonance, so no cancellation
        double omega = 2 * Math.PI * f;
        const double vg = 50.0;

        var velocity = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = [f],
            Damping = ModalDamping.Uniform(0.02),
            BaseExcitation = new BaseExcitation(new Vector3d(1, 0, 0), BaseMotionKind.Velocity, vg),
        });
        var acceleration = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = [f],
            Damping = ModalDamping.Uniform(0.02),
            BaseExcitation = new BaseExcitation(new Vector3d(1, 0, 0), BaseMotionKind.Acceleration, omega * vg),
        });
        double v = velocity.AmplitudeAt(0)[free].X;
        double a = acceleration.AmplitudeAt(0)[free].X;
        output.WriteLine($"velocity-input {v:G8}, acceleration(omega·v) {a:G8}");
        Assert.Equal(a, v, 1e-9 * a);
    }

    [Fact]
    public void TheRefusalsFireByName()
    {
        // A model with nodal forces AND a base excitation is two excitations.
        var loaded = TransientFixtures.SingleDof(out int free);
        loaded.NodalForce(free, new Vector3d(1, 0, 0));
        var both = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(Modes(loaded),
            new HarmonicSolveOptions
            {
                Frequencies = [100.0],
                Damping = ModalDamping.Uniform(0.02),
                BaseExcitation = new BaseExcitation(Vector3d.UnitX, BaseMotionKind.Acceleration, 1),
            }));
        Assert.Contains("two competing excitations", both.Message);

        // A base excitation cannot be combined with a static correction.
        var clean = TransientFixtures.SingleDof(out _);
        var stat = StructuralSolver.Solve(clean.ClearLoads().NodalForce(free, new Vector3d(1, 0, 0)));
        clean.ClearLoads();
        var withStatic = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(Modes(clean),
            new HarmonicSolveOptions
            {
                Frequencies = [100.0],
                Damping = ModalDamping.Uniform(0.02),
                BaseExcitation = new BaseExcitation(Vector3d.UnitX, BaseMotionKind.Acceleration, 1),
                StaticCorrection = stat,
            }));
        Assert.Contains("static correction", withStatic.Message);

        // A zero base direction has nothing to oscillate along.
        var zeroDir = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(Modes(clean),
            new HarmonicSolveOptions
            {
                Frequencies = [100.0],
                Damping = ModalDamping.Uniform(0.02),
                BaseExcitation = new BaseExcitation(Vector3d.Zero, BaseMotionKind.Acceleration, 1),
            }));
        Assert.Contains("non-zero direction", zeroDir.Message);
    }
}
