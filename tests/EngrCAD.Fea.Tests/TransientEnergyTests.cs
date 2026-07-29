using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The energy identity, which is this solver's strongest verification lever because it is an
/// IDENTITY and not a trend.
///
/// <para>Newmark's constant-average-acceleration member satisfies
/// <c>E(n+1) - E(n) = ½(u(n+1) - u(n))'(f(n) + f(n+1)) - dt·w'Cw</c> exactly, with
/// <c>w = (v(n) + v(n+1))/2</c> — see <see cref="TimeIntegration.AverageAcceleration"/> for
/// the three lines of algebra. Nothing in that is approximate, so a drift is a defect and
/// there is no tolerance to negotiate: the bar is round-off accumulated over the run.</para>
///
/// <para><b>The undamped, unloaded case is the one to read first</b>, because its work and
/// dissipation are both exactly zero and the balance residual collapses to the relative
/// energy drift. A slow drift there would be a defect that every accuracy test in this suite
/// passes over — the phase would be right, the amplitude would be right at any single
/// instant, and the run would still be leaking.</para>
/// </summary>
public class TransientEnergyTests(ITestOutputHelper output)
{
    [Fact]
    public void UndampedFreeVibration_ConservesEnergyToRoundOff()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        double period = 2 * Math.PI / omega;
        const int periods = 50;
        const int stepsPerPeriod = 60;
        const double u0 = 0.01;

        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(u0, 0, 0);

        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(period / stepsPerPeriod, periods * stepsPerPeriod)
            {
                InitialDisplacement = initial,
            });

        double drift = Math.Abs(results.Report.FinalEnergy - results.Report.InitialEnergy)
            / results.Report.InitialEnergy;
        output.WriteLine(
            $"{periods} periods at {stepsPerPeriod} steps/period ({periods * stepsPerPeriod:N0} steps): "
            + $"energy {results.Report.InitialEnergy:G12} -> {results.Report.FinalEnergy:G12}, "
            + $"relative drift {drift:E3}");
        output.WriteLine(results.Report.ToText());

        // Work and dissipation are EXACTLY zero here, so the balance residual and the drift
        // are the same number - stated rather than left to be inferred.
        Assert.Equal(0.0, results.Report.WorkDone);
        Assert.Equal(0.0, results.Report.Dissipated);

        // A round-off bar, not a physics one: 3000 steps of a linear solve whose own residual
        // is ~3e-16 cannot hold the energy tighter than the accumulated arithmetic. Anything
        // above 1e-12 is a leak rather than round-off.
        Assert.True(drift < 1e-12, $"energy drift {drift:E3} over {periods} periods");

        // And the energy does not merely return - it never wanders. The largest excursion at
        // ANY step is the same round-off, which is what separates conservation from a drift
        // that happens to come back.
        double worst = 0;
        foreach (var state in results.States)
        {
            worst = Math.Max(
                worst,
                Math.Abs(state.TotalEnergy - results.Report.InitialEnergy)
                    / results.Report.InitialEnergy);
        }
        output.WriteLine($"largest excursion at any step: {worst:E3}");
        Assert.True(worst < 1e-12, $"largest energy excursion {worst:E3}");
    }

    [Fact]
    public void TheBalanceIsAnIdentityWithBothWorkAndDissipationPresent()
    {
        // The undamped free case has zero on both sides of the balance, so it cannot catch a
        // wrong work term or a wrong dissipation term. This one has both, and they are large:
        // the run does 0.3 units of work and dissipates half of it.
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        model.NodalForce(node, new Vector3d(1000, 0, 0));
        var damping = RayleighDamping.FromRatios(
            omega / (4 * Math.PI), 0.03, omega / Math.PI, 0.05);
        double period = 2 * Math.PI / omega;

        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(0.002, 0, 0);
        var velocity = new Vector3d[model.Mesh.NodeCount];
        velocity[node] = new Vector3d(50, 0, 0);

        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(period / 150, 150 * 12)
            {
                Damping = damping,
                InitialDisplacement = initial,
                InitialVelocity = velocity,
                // A load history that is neither constant nor periodic, so the work term is
                // not accidentally zero over the run.
                LoadFactor = t => Math.Sin(0.7 * omega * t) + 0.5,
            });

        var report = results.Report;
        double change = report.FinalEnergy - report.InitialEnergy;
        output.WriteLine(
            $"energy {report.InitialEnergy:G8} -> {report.FinalEnergy:G8} (change {change:G8}), "
            + $"work {report.WorkDone:G8}, dissipated {report.Dissipated:G8}");
        output.WriteLine(
            $"residual |dE - W + D| = {Math.Abs(change - report.WorkDone + report.Dissipated):E3}, "
            + $"relative {report.EnergyBalanceResidual:E3}");
        output.WriteLine(report.ToText());

        // Both flows are large, so the identity is genuinely being tested.
        Assert.True(Math.Abs(report.WorkDone) > 0.1 * report.PeakEnergy);
        Assert.True(Math.Abs(report.Dissipated) > 0.1 * report.PeakEnergy);
        Assert.True(
            report.EnergyBalanceResidual < 1e-12,
            $"energy balance {report.EnergyBalanceResidual:E3}");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.9)]
    [InlineData(0.8)]
    [InlineData(0.5)]
    public void HhtRemovesEnergyInProportionToItsSpectralRadius(double spectralRadius)
    {
        // The dissipative schemes: here the balance residual is not an error but the
        // MEASUREMENT - it is the energy the algorithm removed, which is exactly what a user
        // of numerical damping wants reported. The undamped physical problem does no work and
        // dissipates nothing viscously, so every joule that leaves is the scheme's.
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        double period = 2 * Math.PI / omega;
        const double u0 = 0.01;

        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(u0, 0, 0);

        // A COARSE step, because HHT's dissipation is aimed at modes whose period the step
        // cannot resolve: at 8 steps per period omega.dt = 0.785 and the damping is visible.
        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(period / 8, 8 * 20)
            {
                Integration = TimeIntegration.ForSpectralRadius(spectralRadius),
                InitialDisplacement = initial,
            });

        double retained = results.Report.FinalEnergy / results.Report.InitialEnergy;
        output.WriteLine(
            $"rho_inf {spectralRadius}: {results.Report.Integration}, energy retained after "
            + $"20 periods {retained:P4}, balance residual {results.Report.EnergyBalanceResidual:E3}");

        if (spectralRadius == 1.0)
        {
            // The neutral member IS Newmark's average acceleration and conserves exactly.
            Assert.InRange(retained, 1 - 1e-12, 1 + 1e-12);
            Assert.True(results.Report.EnergyBalanceResidual < 1e-12);
        }
        else
        {
            Assert.True(retained < 1.0, $"rho {spectralRadius} retained {retained:P4}");

            // <b>The decay is not STEP-monotone in the mechanical energy, and asserting that
            // it is would be wrong rather than strict.</b> What HHT contracts is its own
            // amplification operator, whose invariant is a modified energy including the
            // alpha-weighted term; the physical kinetic-plus-strain sum is a different
            // quadratic form and rises within a cycle before falling across it. Measured
            // here, and reported rather than asserted away.
            double largestRise = 0;
            for (int i = 1; i < results.States.Count; i++)
            {
                largestRise = Math.Max(
                    largestRise,
                    (results.States[i].TotalEnergy - results.States[i - 1].TotalEnergy)
                        / results.Report.InitialEnergy);
            }
            output.WriteLine($"  largest single-step rise {largestRise:P4} of the initial energy");

            // What IS monotone is the envelope: the energy never exceeds where it started,
            // and every whole period leaves it lower than the period before.
            foreach (var state in results.States)
            {
                Assert.True(
                    state.TotalEnergy <= results.Report.InitialEnergy * (1 + 1e-12),
                    $"energy exceeded its initial value at t = {state.Time:G6}");
            }
            for (int period0 = 1; period0 * 8 < results.States.Count; period0++)
            {
                Assert.True(
                    results.States[period0 * 8].TotalEnergy
                        < results.States[(period0 - 1) * 8].TotalEnergy,
                    $"energy did not fall across period {period0}");
            }
        }
    }

    [Fact]
    public void MoreNumericalDampingRemovesMoreEnergy()
    {
        // The ordering is the property that makes the parameter mean what it says: a smaller
        // spectral radius must remove more, monotonically, at the same step.
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        double period = 2 * Math.PI / omega;
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(0.01, 0, 0);

        double previous = double.MaxValue;
        foreach (double rho in new[] { 1.0, 0.95, 0.9, 0.8, 0.7, 0.6, 0.5 })
        {
            var results = TransientSolver.Solve(
                model,
                new TransientSolveOptions(period / 8, 8 * 10)
                {
                    Integration = TimeIntegration.ForSpectralRadius(rho),
                    InitialDisplacement = initial,
                });
            double retained = results.Report.FinalEnergy / results.Report.InitialEnergy;
            output.WriteLine($"rho_inf {rho:F2}: retained {retained:P4}");
            Assert.True(retained < previous, $"rho {rho} retained {retained:P4}, not less than {previous:P4}");
            previous = retained;
        }
    }
}
