using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Temperature-dependent conductivity. The oracle is the KIRCHHOFF TRANSFORM: for
/// k(T) = k0(1 + beta T), the variable theta = int k dT is linear in x, so a slab held at
/// T1/T2 carries flux q = (k0/L)[(T1 - T2) + beta(T1^2 - T2^2)/2] EXACTLY — a closed form
/// the per-element-constant discretization must converge to, sharing no line with the
/// solver's own linearization.
/// </summary>
public class ThermalNonlinearTests(ITestOutputHelper output)
{
    private static readonly Material Metal = new(
        "nonlinear metal", 200_000, 0.3, 8e-9,
        thermalConductivity: 40.0, specificHeat: 5e8);

    private static ThermalModel Bar(out AnalysisMesh mesh)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 4, 4), 40, 1, 1);
        mesh = AnalysisMesh.Of(tets);
        return new ThermalModel(mesh, Metal)
            .Temperature(Facets.Tag(StructuredTetMesh.XMin), 0.0)
            .Temperature(Facets.Tag(StructuredTetMesh.XMax), 100.0);
    }

    [Fact]
    public void ASlabWithLinearInTConductivity_CarriesTheKirchhoffFlux()
    {
        const double k0 = 40, beta = 0.01, length = 40, hot = 100;
        var model = Bar(out var mesh);
        var run = ThermalNonlinear.Solve(model,
            new Dictionary<int, Func<double, double>> { [0] = t => k0 * (1 + beta * t) });

        // q = (k0/L)[(T1 − T2) + beta(T1² − T2²)/2] = (40/40)(100 + 0.01·10000/2) = 150.
        double exact = k0 / length * (hot + beta * hot * hot / 2);

        // The FIELD check: the Kirchhoff variable θ(T) = k0(T + βT²/2) is linear in x,
        // so θ(T_node)/θ(hot) must equal x/L at every node.
        double thetaHot = k0 * (hot + beta * hot * hot / 2);
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            double t = run.Results.Temperature[node];
            double theta = k0 * (t + beta * t * t / 2);
            double x = mesh.Position(node).X;
            Assert.True(Math.Abs(theta / thetaHot - x / length) < 5e-3,
                $"node at x={x:G4}: θ-fraction {theta / thetaHot:G6} vs {x / length:G6}");
        }

        // The FLUX check, through the converged per-element k the result carries (the
        // ThermalResults accessor reads the model's constant law — the stated caveat).
        double mean = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
            mean += run.Results.ElementFlux(e).X * run.ElementConductivity[e] / k0;
        mean /= mesh.ElementCount;
        output.WriteLine($"flux {mean:G8} vs Kirchhoff {exact:G8} "
            + $"in {run.Iterations} iterations (last change {run.LastRelativeChange:E2})");

        Assert.True(run.Iterations >= 2, "the nonlinearity must take more than one pass");
        Assert.True(Math.Abs(Math.Abs(mean) - exact) / exact < 2e-3,
            $"flux {mean:G6} vs Kirchhoff {exact:G6}");
    }

    [Fact]
    public void AConstantLaw_IsThePlainSolve_InOnePass()
    {
        var model = Bar(out _);
        var plain = ThermalSolver.Solve(model);
        var run = ThermalNonlinear.Solve(model,
            new Dictionary<int, Func<double, double>> { [0] = _ => 40.0 });

        Assert.Equal(1, run.Iterations);
        for (int node = 0; node < plain.Temperature.Count; node++)
            Assert.Equal(plain.Temperature[node], run.Results.Temperature[node]);

        // The overlay is cleared afterwards: the plain solve still answers identically.
        var again = ThermalSolver.Solve(model);
        for (int node = 0; node < plain.Temperature.Count; node++)
            Assert.Equal(plain.Temperature[node], again.Temperature[node]);
    }

    [Fact]
    public void TheSolve_IsDeterministic_AndRefusesByName()
    {
        var model = Bar(out _);
        var laws = new Dictionary<int, Func<double, double>> { [0] = t => 40 + 0.2 * t };
        var a = ThermalNonlinear.Solve(model, laws);
        var b = ThermalNonlinear.Solve(model, laws);
        Assert.Equal(a.Iterations, b.Iterations);
        for (int node = 0; node < a.Results.Temperature.Count; node++)
            Assert.Equal(a.Results.Temperature[node], b.Results.Temperature[node]);

        Assert.Contains("region id", Assert.Throws<FeaException>(() =>
            ThermalNonlinear.Solve(model,
                new Dictionary<int, Func<double, double>> { [7] = t => 40.0 })).Message);
        Assert.Contains("positive", Assert.Throws<FeaException>(() =>
            ThermalNonlinear.Solve(model,
                new Dictionary<int, Func<double, double>> { [0] = _ => -1.0 })).Message);

        var directional = Bar(out _);
        directional.SetConductivity(0, ConductivityLaw.Orthotropic(
            EngrCAD.Core.Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
            40, 30, 20));
        Assert.Contains("DIRECTIONAL", Assert.Throws<FeaException>(() =>
            ThermalNonlinear.Solve(directional,
                new Dictionary<int, Func<double, double>> { [0] = t => 40.0 })).Message);
    }
}
