using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Time-varying thermal boundary conditions. The oracle with teeth is DISCRETE exactness:
/// for a prescribed ramp <c>R·t</c> the discrete particular solution is <c>a·t + b</c>
/// with <c>a</c> the uniform vector <c>R</c> (the discrete steady solution of a constant
/// Dirichlet value is the constant) and <c>K·b = −M·a</c> — which makes <c>b</c> the
/// STEADY SOLVER'S OWN ANSWER for a uniform generation of <c>−ρc·R</c> held at zero — and
/// any theta scheme integrates a linear-in-time particular solution exactly. Seed the run
/// with <c>b</c> and every step must land on <c>b + R·t</c> to round-off, for backward
/// Euler and Crank–Nicolson alike.
/// </summary>
public class ThermalTimeLawTests(ITestOutputHelper output)
{
    private static readonly Material Metal = new(
        "law metal", 200_000, 0.3, 8e-9,
        thermalConductivity: 40.0, specificHeat: 5e8);

    private const double RhoC = 8e-9 * 5e8;          // = 4.0, model units

    [Theory]
    [InlineData(ThermalTimeScheme.BackwardEuler)]
    [InlineData(ThermalTimeScheme.CrankNicolson)]
    public void ARampedPrescribedTemperature_IsIntegratedExactly_FromItsParticularSolution(
        ThermalTimeScheme scheme)
    {
        const double rampRate = 3.0;                  // K per second
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 4, 4), 10, 2, 2);
        var mesh = AnalysisMesh.Of(tets);

        // b: the steady answer for generation −ρc·R with the ramped face held at law(0) = 0.
        var steady = new ThermalModel(mesh, Metal)
            .Temperature(Facets.Tag(StructuredTetMesh.XMin), 0.0)
            .Generation(-RhoC * rampRate);
        var b = ThermalSolver.Solve(steady);

        var model = new ThermalModel(mesh, Metal)
            .Temperature(Facets.Tag(StructuredTetMesh.XMin), t => rampRate * t);
        var transient = new ThermalTransientOptions(0.5, 8)
        {
            Scheme = scheme,
            InitialField = [.. b.Temperature],
        };
        var run = ThermalSolver.SolveTransient(model, transient);

        double worst = 0;
        foreach (var state in run.States)
        {
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                double expected = b.Temperature[node] + rampRate * state.Time;
                worst = Math.Max(worst, Math.Abs(state.Temperature[node] - expected));
            }
        }
        output.WriteLine($"{scheme}: worst |T − (b + R·t)| = {worst:E3}");
        Assert.True(worst < 1e-9, $"the linear particular solution must integrate exactly; worst {worst:E3}");
    }

    [Fact]
    public void ASinusoidalAmbient_DrivesTheLumpedResponse_AtTheClosedFormAmplitudeAndPhase()
    {
        // A small conductive cube under convection whose ambient swings A·sin(ωt): the
        // lumped first-order system responds at amplitude A/√(1+(ωτ)²) with phase lag
        // atan(ωτ). Run at ωτ = 1 (the fattest phase) and compare the settled cycles.
        const double side = 4, film = 0.02;
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(side, side, side), 3, 3, 3);
        var mesh = AnalysisMesh.Of(tets);
        double volume = side * side * side, area = 6 * side * side;
        double tau = RhoC * volume / (film * area);
        double omega = 1.0 / tau;
        const double amplitude = 10.0;

        var model = new ThermalModel(mesh, Metal)
            .Convection(Facets.All, film, t => amplitude * Math.Sin(omega * t));
        double period = 2 * Math.PI / omega;
        var transient = new ThermalTransientOptions(period / 128, 128 * 8)
        {
            Scheme = ThermalTimeScheme.CrankNicolson,   // eight periods: transients die
            StoreEvery = 4,
        };
        var run = ThermalSolver.SolveTransient(model, transient);

        double expectedAmplitude = amplitude / Math.Sqrt(2);
        double phase = Math.Atan(1.0);
        double worst = 0;
        foreach (var state in run.States)
        {
            if (state.Time < 6 * period)
                continue;                             // settled cycles only
            double expected = expectedAmplitude * Math.Sin(omega * state.Time - phase);
            double centre = state.Temperature.Average();
            worst = Math.Max(worst, Math.Abs(centre - expected));
        }
        output.WriteLine($"tau {tau:G4} s, worst settled deviation {worst:G4} K "
            + $"of a {expectedAmplitude:G4} K swing");
        // Time discretization (CN at 128/period) + the small Biot gradient.
        Assert.True(worst < 0.02 * expectedAmplitude,
            $"settled response off by {worst:G4} against {expectedAmplitude:G4}");
    }

    [Fact]
    public void APulsedHeatLoad_KeepsTheRunsOwnFirstLaw()
    {
        // An insulated body under a square-pulse heat load: the whole-run energy balance
        // is the scheme's own identity and must stay at round-off whatever the law does —
        // including across the discontinuity.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(10, 4, 4), 5, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Metal)
            .HeatLoad(Facets.Tag(StructuredTetMesh.XMax), t => t is >= 1 and < 3 ? 500.0 : 0.0);
        var transient = new ThermalTransientOptions(0.25, 24);
        var run = ThermalSolver.SolveTransient(model, transient);

        output.WriteLine($"first-law residual {run.Report.EnergyBalanceResidual:E3}");
        Assert.True(run.Report.EnergyBalanceResidual < 1e-10);
        // And the body genuinely warmed while the pulse was on, then held (insulated).
        Assert.True(run.Final.Temperature.Average() > 1);
    }

    [Fact]
    public void AConstantLaw_AgreesWithItsConstantTwin()
    {
        // Convection to a law that happens to be constant must match the constant
        // condition to round-off — the pattern is the supply's twin, integrated by the
        // same facet quadrature, differing only in summation order.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(8, 4, 4), 4, 2, 2);
        var mesh = AnalysisMesh.Of(tets);
        var transient = new ThermalTransientOptions(0.5, 6);

        var constant = ThermalSolver.SolveTransient(
            new ThermalModel(mesh, Metal).Convection(Facets.All, 0.05, 80.0), transient);
        var law = ThermalSolver.SolveTransient(
            new ThermalModel(mesh, Metal).Convection(Facets.All, 0.05, _ => 80.0), transient);

        for (int node = 0; node < mesh.NodeCount; node++)
            Assert.Equal(constant.Final.Temperature[node], law.Final.Temperature[node], 9);
    }

    [Fact]
    public void ASteadySolve_RefusesATimeLaw_ByName()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(8, 4, 4), 2, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Metal)
            .Temperature(Facets.Tag(StructuredTetMesh.XMin), 0.0)
            .HeatFlux(Facets.Tag(StructuredTetMesh.XMax), t => 10.0 * t);
        var message = Assert.Throws<FeaException>(() => ThermalSolver.Solve(model)).Message;
        Assert.Contains("time", message);
        Assert.Contains("SolveTransient", message);
    }
}
