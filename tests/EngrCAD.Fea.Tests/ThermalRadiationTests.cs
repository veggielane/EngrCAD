using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Grey-body radiation as the linearized outer iteration. The oracle is the lumped
/// Stefan–Boltzmann equilibrium solved INDEPENDENTLY (bisection on
/// <c>sigma·eps·A·(T^4 − Ts^4) = P</c>, sharing no line with the solver's per-facet
/// linearization), plus the small-signal limit where radiation must degenerate to a
/// convective film at <c>4·sigma·eps·Ts^3</c>.
/// </summary>
public class ThermalRadiationTests(ITestOutputHelper output)
{
    private static readonly Material Metal = new(
        "radiating metal", 200_000, 0.3, 8e-9,
        thermalConductivity: 40.0, specificHeat: 5e8);

    private const double Side = 10;
    private const double Area = 6 * Side * Side;
    private const double Volume = Side * Side * Side;

    private static (AnalysisMesh Mesh, ThermalModel Model) Cube(double generationRate)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(Side, Side, Side), 3, 3, 3);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Metal).Generation(generationRate);
        return (mesh, model);
    }

    [Fact]
    public void AGeneratingCube_SettlesAtTheStefanBoltzmannEquilibrium()
    {
        // Power chosen to put the equilibrium near 500 K against 300 K surroundings; the
        // radiative Biot number is ~1e-3, so the lumped closed form is the truth to that
        // grade. The reference is solved by BISECTION on the balance itself.
        const double emissivity = 0.8, surroundings = 300;
        const double power = 1500.0;                  // mW
        var (_, model) = Cube(power / Volume);

        var run = ThermalRadiation.Solve(model,
            [new RadiationSurface(Facets.All, emissivity, surroundings)]);

        double sigmaEps = ThermalRadiation.StefanBoltzmann * emissivity;
        double lo = surroundings, hi = 3000;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            double flux = sigmaEps * Area
                * (mid * mid * mid * mid - Math.Pow(surroundings, 4));
            if (flux < power)
                lo = mid;
            else
                hi = mid;
        }
        double exact = 0.5 * (lo + hi);
        double mean = run.Results.Temperature.Average();
        output.WriteLine($"equilibrium {mean:G8} K against the closed form {exact:G8} K "
            + $"in {run.Iterations} iterations (last change {run.LastRelativeChange:E2})");

        Assert.True(run.Iterations >= 2, "the nonlinearity must take more than one pass");
        Assert.True(Math.Abs(mean - exact) / exact < 2e-3,
            $"mean {mean:G6} vs closed form {exact:G6}");
    }

    [Fact]
    public void InTheSmallSignalLimit_RadiationIsAFilmAtFourSigmaEpsTsCubed()
    {
        // A tiny load keeps T − Ts ~ 1 K, where sigma·eps·(T^4 − Ts^4) ≈ 4·sigma·eps·Ts^3
        // ·(T − Ts) — so the radiating answer must agree with a plain convective solve at
        // that film, within the quadratic correction ~(3/2)·dT/Ts.
        const double emissivity = 0.6, surroundings = 300;
        const double power = 1.5;                     // mW → about 0.7 K of rise
        var (mesh, model) = Cube(power / Volume);

        var radiating = ThermalRadiation.Solve(model,
            [new RadiationSurface(Facets.All, emissivity, surroundings)]);

        double film = 4 * ThermalRadiation.StefanBoltzmann * emissivity
            * Math.Pow(surroundings, 3);
        var convecting = ThermalSolver.Solve(new ThermalModel(mesh, Metal)
            .Generation(power / Volume)
            .Convection(Facets.All, film, surroundings));

        double radRise = radiating.Results.Temperature.Average() - surroundings;
        double convRise = convecting.Temperature.Average() - surroundings;
        output.WriteLine($"rise: radiating {radRise:G6} K, film {convRise:G6} K");
        Assert.True(Math.Abs(radRise - convRise) / convRise < 0.01,
            $"radiating {radRise:G6} vs film {convRise:G6}");
    }

    [Fact]
    public void TheModel_IsUntouchedAfterwards_AndTheSolveIsDeterministic()
    {
        const double power = 1500.0;
        var (_, model) = Cube(power / Volume);
        var surfaces = new[] { new RadiationSurface(Facets.All, 0.8, 300.0) };

        var a = ThermalRadiation.Solve(model, surfaces);
        var b = ThermalRadiation.Solve(model, surfaces);
        Assert.Equal(a.Iterations, b.Iterations);
        for (int node = 0; node < a.Results.Temperature.Count; node++)
            Assert.Equal(a.Results.Temperature[node], b.Results.Temperature[node]);

        // The overlay was cleared: a plain steady solve of the same model must refuse as
        // undriven (generation with no held temperature and no film), proving no film
        // leaked into the model's own conditions.
        Assert.Throws<FeaException>(() => ThermalSolver.Solve(model));
    }

    [Fact]
    public void TheRefusals_AreByName()
    {
        var (_, model) = Cube(1.0);
        Assert.Contains("Emissivity", Assert.Throws<FeaException>(() =>
            ThermalRadiation.Solve(model,
                [new RadiationSurface(Facets.All, 1.2, 300)])).Message);
        Assert.Contains("ABSOLUTE", Assert.Throws<FeaException>(() =>
            ThermalRadiation.Solve(model,
                [new RadiationSurface(Facets.All, 0.8, 0)])).Message);
        Assert.Contains("no boundary facets", Assert.Throws<FeaException>(() =>
            ThermalRadiation.Solve(model,
                [new RadiationSurface(_ => false, 0.8, 300)])).Message);
    }
}
