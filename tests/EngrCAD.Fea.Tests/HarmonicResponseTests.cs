using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Frequency response by modal superposition, against the closed forms a single-degree-of-
/// freedom oscillator has: the resonant amplification <c>1/(2·zeta)</c>, the half-power
/// bandwidth <c>2·zeta</c>, the 90-degree phase lag at resonance, and the static limit.
///
/// <para>A tip-driven cantilever is the fixture because its first bending mode carries almost
/// all of the response — the second is six times higher in frequency — so the SDOF formulas
/// apply to it directly and any deviation is a measurement of how much the other modes
/// contribute rather than a fudge factor.</para>
/// </summary>
public class HarmonicResponseTests(ITestOutputHelper output)
{
    private const double Length = 100.0;
    private const double Width = 12.0;
    private const double Depth = 8.0;
    private const double TipForce = 50.0;

    /// <summary>A cantilever driven by a tip force along Z (the thin direction), with a
    /// RECTANGULAR section so the two bending families separate and the driven mode is not one
    /// of a degenerate pair.</summary>
    private static StructuralModel Cantilever(int nx, int ny, int nz, ElementOrder order)
    {
        var mesh = ModalFixtures.Beam(Length, Width, Depth, nx, ny, nz, order);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -TipForce));
        return model;
    }

    /// <summary>The node at the tip's centre, where the response is probed.</summary>
    private static int TipNode(AnalysisMesh mesh)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        var target = new Vector3d(Length, Width / 2, Depth / 2);
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            double d = mesh.Position(v).DistanceTo(target);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = v;
            }
        }
        return best;
    }

    [Fact]
    public void ResonantAmplificationIsOneOverTwiceTheDampingRatio()
    {
        const double zeta = 0.02;
        var model = Cantilever(16, 2, 2, ElementOrder.Quadratic);
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 6 });
        int tip = TipNode(model.Mesh);
        double first = modes.Mode(1).Frequency;

        // The static answer from the SAME load, so the amplification is measured against the
        // structure's own compliance rather than against a formula.
        var statics = StructuralSolver.Solve(model);
        double staticTip = Math.Abs(statics.DisplacementAt(tip).Z);

        var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = HarmonicSweep.Around(first, 0.06, 601),
            Damping = ModalDamping.Uniform(zeta),
            StaticCorrection = statics,
        });
        var probe = response.ResponseAt(tip, 2);

        double peak = 0;
        int peakIndex = 0;
        for (int k = 0; k < probe.Length; k++)
        {
            if (probe[k].Magnitude > peak)
            {
                peak = probe[k].Magnitude;
                peakIndex = k;
            }
        }

        // The amplification 1/(2·zeta) is a statement about ONE oscillator, so the reference
        // is that mode's own static contribution phi_1·F_1/w_1² — not the structure's whole
        // static deflection, which also carries every OTHER mode's flexibility. Getting this
        // wrong reads as a 3% solver error and is entirely a modelling mistake in the test:
        // measured below, the modes above the first supply 3.08% of this cantilever's static
        // tip deflection.
        double modalStatic = Math.Abs(
            modes.Mode(1).ShapeAt(tip).Z * response.ModalForces[0] / modes.Mode(1).Eigenvalue);
        double measured = peak / modalStatic;
        output.WriteLine(
            $"first mode {first:N2} Hz, peak at {response.Frequencies[peakIndex]:N2} Hz; "
            + $"mode-1 static contribution {modalStatic:G6} mm, whole static tip "
            + $"{staticTip:G6} mm (the other modes carry "
            + $"{(staticTip - modalStatic) / staticTip:P2} of it), peak {peak:G6} mm");
        output.WriteLine(
            $"amplification {measured:N3} against 1/(2·zeta) = {1 / (2 * zeta):N3}, "
            + $"{(measured - 1 / (2 * zeta)) * 2 * zeta:P2}");

        // Within 1%: the other five modes contribute a little at resonance, and the damped
        // peak sits at w·sqrt(1-2·zeta²) rather than exactly at w — both are real effects
        // rather than error, and both are far below 1% at 2% damping.
        Assert.Equal(1.0 / (2 * zeta), measured, 0.01 / (2 * zeta));

        // The peak lands on the mode, to the resolution of the sweep.
        Assert.Equal(first, response.Frequencies[peakIndex], first * 0.001);

        // And the phase lag is 90 degrees at resonance — the signature of a resonance, and
        // something a magnitude-only response cannot express at all.
        // The phase is read AT the mode's own frequency, not at the sweep's peak sample. Those
        // are two steps apart here, and detuning by even 0.04% of the frequency rotates the
        // phase by atan(2·delta_w/w / (2·zeta)) = 1.15 degrees at 2% damping — so probing the
        // peak sample would be measuring the sweep's resolution rather than the response.
        int atResonance = 0;
        for (int k = 1; k < response.Frequencies.Count; k++)
        {
            if (Math.Abs(response.Frequencies[k] - first)
                < Math.Abs(response.Frequencies[atResonance] - first))
                atResonance = k;
        }
        // The SIGN follows the signs of the modal force and the shape component, both of which
        // are conventions rather than physics, so the QUARTER TURN is what is asserted.
        double phase = probe[atResonance].Phase * 180.0 / Math.PI;
        output.WriteLine(
            $"phase at {response.Frequencies[atResonance]:N3} Hz (the mode's own frequency) is "
            + $"{phase:N3} degrees - a quarter turn from the load; at the sweep's peak sample "
            + $"{probe[peakIndex].Phase * 180.0 / Math.PI:N3}");
        Assert.Equal(90.0, Math.Abs(phase), 0.5);
    }

    [Fact]
    public void TheHalfPowerBandwidthIsTwiceTheDampingRatio()
    {
        // Delta_f / f_n = 2·zeta, the standard way a damping ratio is MEASURED from a
        // frequency response, run here in reverse as a check on the response.
        foreach (double zeta in new[] { 0.01, 0.02, 0.05 })
        {
            var model = Cantilever(12, 2, 2, ElementOrder.Quadratic);
            var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 3 });
            int tip = TipNode(model.Mesh);
            double first = modes.Mode(1).Frequency;

            var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
            {
                Frequencies = HarmonicSweep.Around(first, 8 * zeta, 4001),
                Damping = ModalDamping.Uniform(zeta),
            });
            var probe = response.ResponseAt(tip, 2);

            double peak = 0;
            int peakIndex = 0;
            for (int k = 0; k < probe.Length; k++)
            {
                if (probe[k].Magnitude > peak)
                {
                    peak = probe[k].Magnitude;
                    peakIndex = k;
                }
            }
            double half = peak / Math.Sqrt(2.0);

            double lower = Interpolate(response, probe, peakIndex, -1, half);
            double upper = Interpolate(response, probe, peakIndex, +1, half);
            double bandwidth = (upper - lower) / response.Frequencies[peakIndex];

            output.WriteLine(
                $"zeta {zeta:P1}: peak {peak:G6} at {response.Frequencies[peakIndex]:N3} Hz, "
                + $"half-power {lower:N3}-{upper:N3} Hz, bandwidth {bandwidth:P4} "
                + $"against 2·zeta = {2 * zeta:P4}, {(bandwidth - 2 * zeta) / (2 * zeta):P2}");
            Assert.Equal(2 * zeta, bandwidth, 0.03 * 2 * zeta);
        }
    }

    /// <summary>Walks outward from the peak to where the magnitude first crosses
    /// <paramref name="target"/>, and interpolates the crossing linearly.</summary>
    private static double Interpolate(
        HarmonicResponse response, System.Numerics.Complex[] probe,
        int from, int step, double target)
    {
        for (int k = from; k >= 0 && k < probe.Length - 1 && k > 0; k += step)
        {
            double a = probe[k].Magnitude, b = probe[k + step].Magnitude;
            if (a < target || b > target)
                continue;
            double fa = response.Frequencies[k], fb = response.Frequencies[k + step];
            return fa + (fb - fa) * (a - target) / (a - b);
        }
        throw new InvalidOperationException(
            "The sweep does not reach the half-power level; widen it.");
    }

    [Fact]
    public void TheStaticCorrectionMakesTheZeroFrequencyResponseEXACT()
    {
        // The identity that makes the mode-acceleration method worth having: the bracket
        // [1/(w²-W²+2i·zeta·w·W) - 1/w²] vanishes at W = 0, so the corrected response is the
        // TRUE static answer there however few modes were kept — while the plain modal sum is
        // short by exactly the flexibility of the modes left out.
        var model = Cantilever(12, 2, 2, ElementOrder.Quadratic);
        var statics = StructuralSolver.Solve(model);
        int tip = TipNode(model.Mesh);
        double exact = statics.DisplacementAt(tip).Z;

        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
        var sweep = new[] { 0.0 };

        var plain = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = sweep,
            Damping = ModalDamping.Uniform(0.02),
        });
        var corrected = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = sweep,
            Damping = ModalDamping.Uniform(0.02),
            StaticCorrection = statics,
        });

        double plainTip = plain.ResponseAt(tip, 2)[0].Real;
        double correctedTip = corrected.ResponseAt(tip, 2)[0].Real;
        output.WriteLine(
            $"static tip {exact:G10} mm; one-mode sum {plainTip:G10} "
            + $"({(plainTip - exact) / exact:P3}); corrected {correctedTip:G10} "
            + $"({(correctedTip - exact) / exact:E2})");

        Assert.True(Math.Abs(plainTip - exact) / Math.Abs(exact) > 1e-4,
            "the one-mode sum is suspiciously exact; the truncation test is measuring nothing");
        Assert.Equal(exact, correctedTip, Math.Abs(exact) * 1e-12);

        // And the reported truncation error IS the discrepancy, not an estimate of it.
        output.WriteLine($"reported truncation error {corrected.TruncationError:P4}");
        Assert.True(corrected.TruncationError > 0);
        Assert.True(double.IsNaN(plain.TruncationError));
    }

    [Fact]
    public void TruncationErrorFallsAsModesAreAdded()
    {
        var model = Cantilever(12, 2, 2, ElementOrder.Quadratic);
        var statics = StructuralSolver.Solve(model);

        var errors = new List<double>();
        foreach (int count in new[] { 1, 2, 4, 8 })
        {
            var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = count });
            var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
            {
                Frequencies = [0.0],
                Damping = ModalDamping.None,
                StaticCorrection = statics,
            });
            errors.Add(response.TruncationError);
            output.WriteLine(
                $"{count} mode{(count == 1 ? "" : "s")}: the modal sum misses "
                + $"{response.TruncationError:P4} of the static response");
        }

        // Monotone, and by a lot: the first mode of a tip-loaded cantilever already carries
        // most of the static flexibility, and each further one takes a bite out of what is
        // left. It is the number that says whether a truncated sweep can be trusted at the
        // low end at all.
        for (int i = 1; i < errors.Count; i++)
            Assert.True(errors[i] <= errors[i - 1], $"adding modes at step {i} made it worse");
        Assert.True(errors[^1] < errors[0]);
    }

    [Fact]
    public void AnUnexcitedModeContributesNothingHoweverCloseItsFrequency()
    {
        // The property that separates a modal force from a frequency: a load orthogonal to a
        // mode shape does not excite it at all. Driving the cantilever along Z leaves the
        // Y-bending family with a modal force of zero, so those modes are missing from the
        // response even though they sit right beside the driven ones.
        var model = Cantilever(12, 2, 2, ElementOrder.Quadratic);
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 4 });
        var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = HarmonicSweep.Linear(10, 4000, 40),
            Damping = ModalDamping.Uniform(0.02),
        });

        output.WriteLine(response.ToText());
        double largest = response.ModalForces.Max(Math.Abs);
        var quiet = new List<double>();
        for (int i = 0; i < response.ModalForces.Count; i++)
        {
            double relative = Math.Abs(response.ModalForces[i]) / largest;
            if (relative < 0.01)
                quiet.Add(relative);
        }
        output.WriteLine(
            $"{quiet.Count} of {response.ModalForces.Count} modes carry a modal force under 1% "
            + $"of the largest: {string.Join(", ", quiet.Select(r => r.ToString("E2")))}");

        // Three orders of magnitude down, and NOT exactly zero — the same asymmetry the modal
        // beam tests measure as a degenerate pair's splitting. Kuhn's subdivision picks its
        // diagonals by index order and no reflection preserves that, so a Y-bending mode is
        // not exactly orthogonal to a Z-directed load on this mesh. Asserting exact zero would
        // be asserting a symmetry the discretization does not have.
        Assert.True(quiet.Count >= 1, "a Z-driven rectangular cantilever should leave the Y family nearly unexcited");
        Assert.All(quiet, r => Assert.True(r < 3e-3, $"a 'quiet' mode carried {r:E2} of the load"));
    }

    [Fact]
    public void RayleighDampingRatiosReachTheResponseUnchanged()
    {
        var model = Cantilever(12, 2, 2, ElementOrder.Quadratic);
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 4 });
        var rayleigh = RayleighDamping.FromRatios(
            modes.Mode(1).Frequency, 0.02, modes.Mode(4).Frequency, 0.02);

        var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = [modes.Mode(1).Frequency],
            Damping = ModalDamping.Rayleigh(rayleigh),
        });

        for (int i = 0; i < modes.Modes.Count; i++)
        {
            output.WriteLine(
                $"mode {i + 1} at {modes.Modes[i].Frequency,10:N2} Hz: "
                + $"zeta {response.DampingRatios[i]:P4}");
            Assert.Equal(
                rayleigh.RatioAtFrequency(modes.Modes[i].Frequency),
                response.DampingRatios[i], 1e-15);
        }
        // The fitted pair reads exactly 2%, and everything BETWEEN them reads less — the U
        // again, now on the modes it will actually be applied to.
        Assert.Equal(0.02, response.DampingRatios[0], 1e-12);
        Assert.Equal(0.02, response.DampingRatios[^1], 1e-12);
        Assert.True(response.DampingRatios[1] < 0.02);
    }

    [Fact]
    public void UndampedResonanceIsNotAFiniteNumber()
    {
        // Correct, and left alone rather than clamped: an undamped oscillator driven exactly
        // at its own frequency has no steady state, and returning a big finite number instead
        // would be a quiet claim that it does. The exact spelling is the complex division's
        // own — .NET returns (NaN, NaN) for a finite numerator over an exactly zero complex
        // denominator rather than an infinity — so the claim asserted is the one that is
        // actually meant: not finite.
        var model = Cantilever(8, 1, 1, ElementOrder.Quadratic);
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
        var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = [modes.Mode(1).Frequency],
            Damping = ModalDamping.None,
        });
        var q = response.ModalCoordinate(0, 1);
        output.WriteLine($"undamped modal coordinate at resonance: {q}");
        Assert.False(double.IsFinite(q.Magnitude));

        // A hair off resonance it is finite and enormous, which is what makes the point above
        // a statement about one point rather than about the method.
        var nearby = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = [modes.Mode(1).Frequency * 1.0001],
            Damping = ModalDamping.None,
        });
        double near = nearby.ModalCoordinate(0, 1).Magnitude;
        output.WriteLine($"0.01% above resonance: {near:G6}");
        Assert.True(double.IsFinite(near) && near > 0);
    }

    [Fact]
    public void TheCsvCarriesTheSweepInAFormAPlotCanRead()
    {
        var model = Cantilever(8, 1, 1, ElementOrder.Quadratic);
        var modes = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 2 });
        var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
        {
            Frequencies = HarmonicSweep.Logarithmic(10, 5000, 5),
            Damping = ModalDamping.Uniform(0.02),
        });

        var lines = response.ToCsv(TipNode(model.Mesh), 2).Split(Environment.NewLine);
        output.WriteLine(string.Join(Environment.NewLine, lines));
        Assert.Equal(6, lines.Length);
        Assert.StartsWith("frequency_hz,peak_amplitude,", lines[0]);
        Assert.Equal(4, lines[1].Split(',').Length);
        // "R" round-trip formatting, so a plot reads the numbers this project computed rather
        // than a rounded copy of them.
        Assert.All(lines.Skip(1), l => Assert.All(l.Split(','), v => Assert.True(double.TryParse(v, out _))));
    }

    [Fact]
    public void SweepHelpersHitTheirEndpointsExactly()
    {
        var linear = HarmonicSweep.Linear(10, 500, 50);
        Assert.Equal(10.0, linear[0]);
        Assert.Equal(500.0, linear[^1]);
        // Three points over two decades, so the middle one is the geometric mean — which is
        // exactly the property "evenly spaced in the logarithm" means and the one an arbitrary
        // count would not make checkable.
        // The logarithmic sweep goes through exp(log(x)), which is a round trip that is NOT
        // the identity in doubles, so its endpoints are exact to a few ulps rather than to the
        // bit — stated as a tolerance instead of being papered over.
        var log = HarmonicSweep.Logarithmic(10, 1000, 3);
        Assert.Equal(10.0, log[0], 1e-9);
        Assert.Equal(1000.0, log[^1], 1e-9);
        Assert.Equal(100.0, log[1], 1e-9);
        var around = HarmonicSweep.Around(100, 0.1, 3);
        // A tolerance, not an equality: 100*(1-0.1) is 90.00000000000001, and demanding an
        // exact 90 would be asserting that a product of two decimals is representable.
        Assert.Equal(90.0, around[0], 1e-12);
        Assert.Equal(100.0, around[1], 1e-12);
        Assert.Equal(110.0, around[2], 1e-12);

        Assert.Throws<ArgumentException>(() => HarmonicSweep.Linear(500, 10, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => HarmonicSweep.Logarithmic(0, 10, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => HarmonicSweep.Linear(10, 20, 0));
    }
}
