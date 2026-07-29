using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Properties of <see cref="TimeIntegration"/> itself: the identity that makes HHT a
/// generalisation of Newmark rather than a second scheme, and the measurement that turns its
/// spectral-radius parameter from a transcribed formula into a checked one.
/// </summary>
public class TransientSchemeTests(ITestOutputHelper output)
{
    [Fact]
    public void HhtAtZeroAlpha_IsNewmarkAverageAccelerationExactly()
    {
        // beta = (1-0)²/4 = 1/4 and gamma = (1-0)/2 = 1/2 fall out of HHT's own formulas, so
        // the two members are the same value - asserted rather than assumed, because if they
        // were not, every HHT run would carry a discontinuity at the parameter's own end.
        var hht = TimeIntegration.HilberHughesTaylor(0);
        Assert.Equal(TimeIntegration.AverageAcceleration, hht);
        Assert.Equal(0.25, hht.Beta);
        Assert.Equal(0.5, hht.Gamma);
        Assert.Equal(0.0, hht.Alpha);

        // And a neutral spectral radius asks for the same member by the other name.
        Assert.Equal(TimeIntegration.AverageAcceleration, TimeIntegration.ForSpectralRadius(1.0));
    }

    [Fact]
    public void HhtAtZeroAlpha_ProducesBitIdenticalOutput()
    {
        // The value being equal is one claim; the alpha-weighted code path collapsing onto
        // the plain one is another, and only a bit comparison of a real run settles it. Every
        // "alpha != 0" branch in the stepper is an exact-zero test for exactly this reason.
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        model.NodalForce(node, new Vector3d(1000, 0, 0));
        double period = 2 * Math.PI / omega;
        var damping = RayleighDamping.FromRatios(
            omega / (4 * Math.PI), 0.03, omega / Math.PI, 0.05);

        TransientResults Run(TimeIntegration scheme) => TransientSolver.Solve(
            model,
            new TransientSolveOptions(period / 40, 200)
            {
                Integration = scheme,
                Damping = damping,
                LoadFactor = t => Math.Sin(0.7 * omega * t),
            });

        var newmark = Run(TimeIntegration.AverageAcceleration);
        var hht = Run(TimeIntegration.HilberHughesTaylor(0));

        Assert.Equal(newmark.States.Count, hht.States.Count);
        int compared = 0;
        for (int i = 0; i < newmark.States.Count; i++)
        {
            for (int n = 0; n < model.Mesh.NodeCount; n++)
            {
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(newmark.States[i].DisplacementAt(n).X),
                    BitConverter.DoubleToInt64Bits(hht.States[i].DisplacementAt(n).X));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(newmark.States[i].VelocityAt(n).X),
                    BitConverter.DoubleToInt64Bits(hht.States[i].VelocityAt(n).X));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(newmark.States[i].AccelerationAt(n).X),
                    BitConverter.DoubleToInt64Bits(hht.States[i].AccelerationAt(n).X));
                compared += 3;
            }
        }
        output.WriteLine($"{compared:N0} values bit-identical across {newmark.States.Count} states");
        Assert.True(compared > 1000);
    }

    [Theory]
    [InlineData(0.95)]
    [InlineData(0.9)]
    [InlineData(0.8)]
    [InlineData(0.6)]
    [InlineData(0.5)]
    public void TheSpectralRadiusIsMeasured_NotJustTranscribed(double requested)
    {
        // alpha = (rho - 1)/(rho + 1) is the standard relation, and a standard relation with a
        // sign convention in it is exactly the kind of thing this project has been caught
        // transcribing wrongly. So it is checked against the run rather than trusted: drive
        // one mode at omega·dt = 1e5, which is the "as omega·dt grows" limit for any practical
        // purpose (measured: the reading is stable to eight digits from 1e4 upwards), and read
        // the decay off the ENERGY, a quadratic form.
        //
        // <b>The obvious estimator is wrong by a factor that looks exactly like a wrong
        // formula, and finding out which took a derivation.</b> A two-point ratio
        // (E30/E20)^(1/20) reads 4.138% high at EVERY radius - 0.9 -> 0.93724, 0.8 -> 0.83310,
        // 0.5 -> 0.52069 - and converges to that offset rather than drifting, so it is not a
        // failure to reach the asymptote. HHT's amplification matrix in the omega·dt -> inf
        // limit turns out to be DEFECTIVE: eliminating the acceleration leaves a 2x2 block
        // whose trace is 2 - 4/(1-alpha) and whose determinant is ((1+alpha)/(1-alpha))², and
        // its discriminant `trace² - 4·det` is IDENTICALLY ZERO for every alpha. So there is a
        // double real eigenvalue at -rho_inf with one eigenvector, the state decays as
        // n·rho^n rather than rho^n, and the energy as n²·rho^(2n). Dividing the n² out is the
        // whole correction, and it is exact: (30/20)^(2/20) = 1.041380, the measured offset to
        // six digits.
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        var scheme = TimeIntegration.ForSpectralRadius(requested);

        double dt = 1e5 / omega;
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(0.01, 0, 0);

        const int first = 20, last = 30;
        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(dt, last)
            {
                Integration = scheme,
                InitialDisplacement = initial,
            });

        double e1 = results.States[first].TotalEnergy / (first * (double)first);
        double e2 = results.States[last].TotalEnergy / (last * (double)last);
        double measured = Math.Pow(e2 / e1, 1.0 / (2.0 * (last - first)));
        double naive = Math.Pow(
            results.States[last].TotalEnergy / results.States[first].TotalEnergy,
            1.0 / (2.0 * (last - first)));
        output.WriteLine(
            $"rho_inf requested {requested}, alpha {scheme.Alpha:G6}, "
            + $"stated {scheme.SpectralRadiusAtInfinity:G8}, measured {measured:G8} "
            + $"({measured / requested - 1:P4}); the uncorrected two-point ratio would say "
            + $"{naive:G8} ({naive / requested - 1:P4})");

        Assert.Equal(requested, scheme.SpectralRadiusAtInfinity!.Value, 12);
        Assert.Equal(requested, measured, requested * 1e-4);

        // And the secular factor is the SAME for every radius, which is what shows it belongs
        // to the Jordan block rather than to any particular alpha.
        Assert.Equal(Math.Pow(last / (double)first, 2.0 / (2.0 * (last - first))), naive / measured, 1e-9);
    }

    [Fact]
    public void TheNeutralSchemeHasSpectralRadiusOneAtEveryStepSize()
    {
        // The counterpart: the default scheme removes nothing however coarse the step, which
        // is the property that makes the energy identity hold and the reason a mesh's
        // unresolved top modes ring for ever under it.
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(0.01, 0, 0);

        foreach (double omegaDt in new[] { 0.1, 1.0, 10.0, 1000.0 })
        {
            var results = TransientSolver.Solve(
                model,
                new TransientSolveOptions(omegaDt / omega, 40) { InitialDisplacement = initial });
            double retained = results.Report.FinalEnergy / results.Report.InitialEnergy;
            output.WriteLine($"omega.dt = {omegaDt,6}: energy retained {retained:G12}");
            Assert.Equal(1.0, retained, 1e-11);
        }
        Assert.Equal(1.0, TimeIntegration.AverageAcceleration.SpectralRadiusAtInfinity);
    }

    [Fact]
    public void SchemePropertiesAreStatedCorrectly()
    {
        Assert.True(TimeIntegration.AverageAcceleration.IsUnconditionallyStable);
        Assert.True(TimeIntegration.AverageAcceleration.IsSecondOrder);
        Assert.True(TimeIntegration.HilberHughesTaylor(-1.0 / 3.0).IsSecondOrder);
        Assert.True(TimeIntegration.HilberHughesTaylor(-1.0 / 3.0).IsUnconditionallyStable);
        Assert.False(TimeIntegration.NumericallyDamped(0.7).IsSecondOrder);
        Assert.True(TimeIntegration.NumericallyDamped(0.7).IsUnconditionallyStable);

        // A numerically damped Newmark member's high-frequency limit depends on beta as well,
        // so it is NOT reported as a single constant of the family.
        Assert.Null(TimeIntegration.NumericallyDamped(0.7).SpectralRadiusAtInfinity);

        // alpha = -1/3 is the family's floor and reaches exactly 1/2.
        Assert.Equal(
            0.5, TimeIntegration.HilberHughesTaylor(-1.0 / 3.0).SpectralRadiusAtInfinity!.Value, 12);
        output.WriteLine(
            $"{TimeIntegration.AverageAcceleration}\n"
            + $"{TimeIntegration.HilberHughesTaylor(-1.0 / 3.0)}\n"
            + $"{TimeIntegration.NumericallyDamped(0.7)}\n"
            + $"{TimeIntegration.ForSpectralRadius(0.8)}");
    }
}
