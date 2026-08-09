using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Structural (hysteretic) damping in the direct per-frequency harmonic solve — the complex
/// modulus <c>K(1 + i·eta)</c>, whose imaginary stiffness <c>eta·K</c> is
/// frequency-INDEPENDENT where a viscous dashpot's <c>omega·C</c> is not.
///
/// <para><b>The single-degree-of-freedom fixture makes the closed form exact.</b> With every
/// degree of freedom restrained but one, the reduced system is <c>1 x 1</c> and the response
/// is <c>u = f / (k - omega²·m + i·eta·k)</c>, so
/// <c>|u| = f / sqrt((k - omega²·m)² + (eta·k)²)</c> with no discretization error at all. Its
/// stiffness comes from a static solve and its natural frequency from a modal solve, so the
/// reference is never the harmonic solver's own arithmetic.</para>
///
/// <para><b>The two verifications that have teeth</b>: at resonance the amplification is
/// exactly <c>1/eta</c> (against <c>1/(2·zeta)</c> for viscous — so <c>eta = 2·zeta</c> matches
/// the peak and nothing else), and OFF resonance the hysteretic and viscous responses DIFFER,
/// which is the whole reason the two models are not one. A sweep against the closed form checks
/// that the imaginary part stayed constant rather than scaling with frequency, which a viscous
/// term substituted by mistake would fail everywhere but at the tuning frequency.</para>
/// </summary>
public class HystereticDampingTests(ITestOutputHelper output)
{
    [Fact]
    public void ResonantAmplificationIsExactlyOneOverEta()
    {
        // The cleanest identity: at the fixture's own natural frequency the real part
        // k - omega²·m vanishes, so |u| = f/(eta·k) and |u|/u_static = 1/eta exactly.
        const double eta = 0.04;
        var model = TransientFixtures.SingleDof(out int free);
        var (k, _, omega) = TransientFixtures.Properties(model, free);
        const double force = 10.0;
        model.NodalForce(free, new Vector3d(force, 0, 0));
        model.SetLossFactor(eta);

        var response = DirectHarmonicSolver.Solve(model, new DirectHarmonicOptions
        {
            Frequencies = [omega / (2.0 * Math.PI)],
        });

        double amplitude = response.ResponseAt(free, 0)[0].Magnitude;
        double amplification = amplitude / (force / k);
        output.WriteLine($"amplification {amplification:G10} against {1.0 / eta}");
        output.WriteLine(response.ToText());
        Assert.Contains("loss factor", response.Report.Damping);

        // The only error sources are the factorization's round-off and the ulp gap between the
        // driven omega and the modal solve's own — both far below 1e-6.
        double relative = Math.Abs(1.0 / eta - amplification) / (1.0 / eta);
        Assert.True(relative <= 1e-6, $"amplification {amplification:G17}, relative {relative:E2}");

        // The phase at resonance is -90 deg: u = f/(i·eta·k) = -i·|u| relative to the drive,
        // the same quarter-turn viscous damping gives, since at resonance both are purely
        // imaginary impedance.
        double phase = response.ResponseAt(free, 0)[0].Phase * 180.0 / Math.PI;
        Assert.Equal(-90.0, phase, 1e-4);
    }

    [Fact]
    public void TheSweepMatchesTheClosedFormEverywhere_ImaginaryPartIsConstant()
    {
        // A sweep across resonance against |u| = (f/k)/sqrt((1-r²)² + eta²) with r = f/f_n.
        // The constant eta² term under the root is what a viscous substitution — 2·zeta·r
        // there, rising with frequency — would get wrong away from r = 1, so agreement over
        // the whole sweep is the statement that the imaginary part stayed frequency-independent.
        const double eta = 0.05;
        var model = TransientFixtures.SingleDof(out int free);
        var (k, _, omega) = TransientFixtures.Properties(model, free);
        const double force = 3.0;
        model.NodalForce(free, new Vector3d(force, 0, 0));
        model.SetLossFactor(eta);

        double fn = omega / (2.0 * Math.PI);
        double[] ratios = [0.5, 0.8, 0.95, 1.0, 1.05, 1.25, 2.0];
        double[] sweep = [.. ratios.Select(r => r * fn)];
        var response = DirectHarmonicSolver.Solve(
            model, new DirectHarmonicOptions { Frequencies = sweep });

        double uStatic = force / k;
        double worst = 0;
        for (int i = 0; i < ratios.Length; i++)
        {
            double r = ratios[i];
            double expected = uStatic / Math.Sqrt((1 - r * r) * (1 - r * r) + eta * eta);
            double measured = response.ResponseAt(free, 0)[i].Magnitude;
            double rel = Math.Abs(expected - measured) / expected;
            worst = Math.Max(worst, rel);
            output.WriteLine($"r = {r:F2}: closed form {expected:G8}, measured {measured:G8} ({rel:E2})");
        }
        Assert.True(worst < 1e-5, $"worst relative error {worst:E2}");
    }

    [Fact]
    public void HystereticDiffersFromViscousOffResonance()
    {
        // eta and zeta = eta/2 are tuned to the SAME resonant peak, but the two models are not
        // the same model: off resonance the viscous imaginary part 2·zeta·r·k scales with
        // frequency where the hysteretic one, eta·k, is constant. Tuned so they are equal at
        // r = 1, the constant EXCEEDS the viscous below resonance (so the hysteretic amplitude
        // is SMALLER there) and falls short of it above (so the hysteretic amplitude is LARGER
        // there) — a crossover the two closed forms predict and a viscous term in disguise
        // could not produce.
        const double eta = 0.06;
        double zeta = eta / 2.0;
        var hysteretic = TransientFixtures.SingleDof(out int free);
        var (k, _, omega) = TransientFixtures.Properties(hysteretic, free);
        const double force = 3.0;
        hysteretic.NodalForce(free, new Vector3d(force, 0, 0));
        hysteretic.SetLossFactor(eta);

        var viscous = TransientFixtures.SingleDof(out _);
        viscous.NodalForce(free, new Vector3d(force, 0, 0));
        viscous.SetDamping(new RayleighDamping(0.0, 2.0 * zeta / omega));  // beta·K, ratio zeta at omega

        double fn = omega / (2.0 * Math.PI);
        double[] sweep = [0.5 * fn, fn, 2.0 * fn];
        var h = DirectHarmonicSolver.Solve(hysteretic, new DirectHarmonicOptions { Frequencies = sweep });
        var v = DirectHarmonicSolver.Solve(viscous, new DirectHarmonicOptions { Frequencies = sweep });

        // Tuned to the SAME peak at r = 1.
        double atResonanceH = h.ResponseAt(free, 0)[1].Magnitude;
        double atResonanceV = v.ResponseAt(free, 0)[1].Magnitude;
        output.WriteLine($"at resonance: hysteretic {atResonanceH:G8}, viscous {atResonanceV:G8}");
        Assert.True(Math.Abs(atResonanceH - atResonanceV) / atResonanceH < 1e-4);

        double uStatic = force / k;
        double belowH = h.ResponseAt(free, 0)[0].Magnitude;
        double belowV = v.ResponseAt(free, 0)[0].Magnitude;
        double aboveH = h.ResponseAt(free, 0)[2].Magnitude;
        double aboveV = v.ResponseAt(free, 0)[2].Magnitude;
        // Both closed forms verified, then the crossover asserted from them.
        Assert.Equal(uStatic / Math.Sqrt(0.75 * 0.75 + eta * eta), belowH, 1e-5 * belowH);
        Assert.Equal(uStatic / Math.Sqrt(0.75 * 0.75 + (2 * zeta * 0.5) * (2 * zeta * 0.5)), belowV, 1e-5 * belowV);
        output.WriteLine(
            $"r=0.5: hysteretic {belowH:G8}, viscous {belowV:G8}  |  "
            + $"r=2.0: hysteretic {aboveH:G8}, viscous {aboveV:G8}");
        // Below resonance the constant eta·k dominates, so hysteretic is the smaller response;
        // above resonance the rising omega·c dominates, so the ordering flips. Neither is a
        // round-off tie — the two damping models are genuinely different.
        Assert.True(belowH < belowV);
        Assert.True(aboveH > aboveV);
    }

    [Fact]
    public void ViscousAndHystereticCompose_TheImaginaryPartsAdd()
    {
        // A model carrying BOTH a viscous dashpot and a loss factor: the imaginary impedance is
        // omega·c + eta·k, so |u| = f/sqrt((k - omega²·m)² + (omega·c + eta·k)²). Composition
        // is the point — a real structure can have both material and joint damping — and the
        // sum is well defined, which this pins.
        const double eta = 0.03;
        var model = TransientFixtures.SingleDof(out int free);
        var (k, mass, omega) = TransientFixtures.Properties(model, free);
        const double force = 5.0;
        model.NodalForce(free, new Vector3d(force, 0, 0));
        model.SetLossFactor(eta);
        // A grounded dashpot along the free axis adds c to the reduced 1x1 imaginary part.
        double zetaV = 0.02;
        double c = 2.0 * mass * omega * zetaV;
        model.Dashpot(free, new Vector3d(1, 0, 0), c);

        double fn = omega / (2.0 * Math.PI);
        double[] sweep = [0.7 * fn, fn, 1.3 * fn];
        var response = DirectHarmonicSolver.Solve(
            model, new DirectHarmonicOptions { Frequencies = sweep });
        Assert.True(response.Report.NonProportional);  // a dashpot makes C non-proportional

        double worst = 0;
        foreach (int i in new[] { 0, 1, 2 })
        {
            double f = sweep[i];
            double w = 2 * Math.PI * f;
            double real = k - w * w * mass;
            double imag = w * c + eta * k;
            double expected = force / double.Hypot(real, imag);
            double measured = response.ResponseAt(free, 0)[i].Magnitude;
            worst = Math.Max(worst, Math.Abs(expected - measured) / expected);
            output.WriteLine($"f = {f:F1}: closed form {expected:G8}, measured {measured:G8}");
        }
        Assert.True(worst < 1e-5, $"worst relative error {worst:E2}");
    }

    [Fact]
    public void TheModalAndTransientRoutesRefuseALossFactorByName()
    {
        var model = TransientFixtures.SingleDof(out int free);
        model.NodalForce(free, new Vector3d(1, 0, 0));
        model.SetLossFactor(0.03);

        // The modal superposition route refuses it (no per-mode real ratio off resonance).
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
        var ex1 = Assert.Throws<FeaException>(() => HarmonicSolver.Solve(modes,
            new HarmonicSolveOptions { Frequencies = [100.0], Damping = ModalDamping.Uniform(0.02) }));
        Assert.Contains("loss factor", ex1.Message);
        Assert.Contains("DirectHarmonicSolver", ex1.Message);

        // The transient refuses it (no causal time-domain form).
        var ex2 = Assert.Throws<FeaException>(() => TransientSolver.Solve(
            model, new TransientSolveOptions(1e-6, 2)));
        Assert.Contains("time-domain", ex2.Message);
        Assert.Contains("DirectHarmonicSolver", ex2.Message);
    }

    [Fact]
    public void TheLossFactorVocabularyRefusesTheMeaninglessSpellings()
    {
        var model = TransientFixtures.SingleDof(out _);
        Assert.Throws<FeaException>(() => model.SetLossFactor(-0.01));
        Assert.Throws<FeaException>(() => model.SetLossFactor(0, -0.01));
        Assert.Throws<FeaException>(() => model.SetLossFactor(double.PositiveInfinity));

        // Zero says "no hysteretic damping" and does not flip the flag.
        Assert.False(model.HasLossFactor);
        model.SetLossFactor(0.0);
        Assert.False(model.HasLossFactor);
        model.SetLossFactor(0.04);
        Assert.True(model.HasLossFactor);
        Assert.Contains("loss factor 0.04", model.DampingDescription);
    }
}
