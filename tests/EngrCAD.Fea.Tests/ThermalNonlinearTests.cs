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

    // ---- the property-nonlinear TRANSIENT: c(T) (and k(T)) per step ----

    /// <summary>An insulated cube under uniform generation: the field stays spatially
    /// uniform (generation loads and capacity rows share the partition-of-unity
    /// weights), so the FE step reduces EXACTLY to the scalar recurrence
    /// T_next = T + dt·g/(ρc(T)) under backward Euler with start-of-step properties.</summary>
    private static ThermalModel AdiabaticCube(double generation, out AnalysisMesh mesh)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(8, 8, 8), 2, 2, 2);
        mesh = AnalysisMesh.Of(tets);
        return new ThermalModel(mesh, Metal).Generation(generation);
    }

    [Fact]
    public void CapacityLaw_MatchesTheScalarRecurrenceExactly()
    {
        // c(T) = c0(1 + gamma·T): the uniform-field FE step IS the scalar recurrence,
        // an identity rather than a convergence claim — a wrong evaluation point, a
        // dropped density or a capacity assembled from the wrong temperature all break
        // it at the first step.
        const double c0 = 5e8, gamma = 0.002, g = 2.0, dt = 0.5;
        const int steps = 20;
        var model = AdiabaticCube(g, out var mesh);
        var run = ThermalNonlinear.SolveTransient(
            model, new ThermalTransientOptions(dt, steps),
            capacityByRegion: new Dictionary<int, Func<double, double>>
            {
                [0] = t => c0 * (1 + gamma * t),
            });

        double rho = Metal.Density;
        double expected = 0;
        for (int n = 0; n < steps; n++)
            expected += dt * g / (rho * c0 * (1 + gamma * expected));

        var final = run.States[^1].Temperature;
        for (int node = 0; node < mesh.NodeCount; node++)
            Assert.Equal(expected, final[node], 6);
        Assert.Equal(steps, run.Factorizations);
        Assert.True(run.Converged);
    }

    [Fact]
    public void CapacityLaw_ConvergesOnTheEnthalpyClosedForm()
    {
        // The physics check the recurrence identity cannot make: as dt shrinks, the
        // run converges on the enthalpy closed form ρ(c0·T + c0γT²/2) = g·t — at first
        // order, since the property is evaluated explicitly at the step's start
        // (matching backward Euler's own order).
        const double c0 = 5e8, gamma = 0.004, g = 4.0, duration = 40;
        double rho = Metal.Density;
        // g·t = ρc0(T + γT²/2)  →  T = (√(1 + 2γ·g·t/(ρc0)) − 1)/γ.
        double exact = (Math.Sqrt(1 + 2 * gamma * g * duration / (rho * c0)) - 1) / gamma;

        var errors = new List<double>();
        foreach (int steps in new[] { 10, 20, 40 })
        {
            var model = AdiabaticCube(g, out _);
            var run = ThermalNonlinear.SolveTransient(
                model, new ThermalTransientOptions(duration / steps, steps),
                capacityByRegion: new Dictionary<int, Func<double, double>>
                {
                    [0] = t => c0 * (1 + gamma * t),
                });
            errors.Add(Math.Abs(run.States[^1].Temperature[0] - exact));
        }
        output.WriteLine($"errors {string.Join(", ", errors.Select(e => e.ToString("E3")))}");
        Assert.True(errors[0] > errors[1] && errors[1] > errors[2], "errors must fall with dt");
        double order = Math.Log2(errors[0] / errors[2]) / 2;
        Assert.InRange(order, 0.7, 1.5); // explicit-in-property backward Euler is first order
    }

    [Fact]
    public void ConstantLaws_ReproduceThePlainTransientBitForBit()
    {
        // The degeneration with teeth: laws returning exactly the material's own
        // constants overlay the SAME doubles the plain assembly reads (the wrapper
        // multiplies Density by the returned c, the same product the material caches),
        // so every stored state must match the plain run to the BIT.
        const double dt = 0.4;
        const int steps = 6;
        var plainModel = Bar(out var mesh);
        var plain = ThermalSolver.SolveTransient(
            plainModel, new ThermalTransientOptions(dt, steps) { InitialTemperature = 50 });

        var lawModel = Bar(out _);
        var run = ThermalNonlinear.SolveTransient(
            lawModel, new ThermalTransientOptions(dt, steps) { InitialTemperature = 50 },
            conductivityByRegion: new Dictionary<int, Func<double, double>>
            {
                [0] = _ => Metal.ThermalConductivity,
            },
            capacityByRegion: new Dictionary<int, Func<double, double>>
            {
                [0] = _ => Metal.SpecificHeat,
            });

        Assert.Equal(plain.States.Count, run.States.Count);
        for (int s = 0; s < plain.States.Count; s++)
        {
            var a = plain.States[s].Temperature;
            var b = run.States[s].Temperature;
            for (int node = 0; node < mesh.NodeCount; node++)
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(a[node]),
                    BitConverter.DoubleToInt64Bits(b[node]));
        }
    }

    [Fact]
    public void TimeLaws_ComposeThroughTheSubRuns_BitForBit()
    {
        // The re-basing oracle: with constant-property laws the sub-runs' matrices are
        // the plain run's, so any state difference can only come from a law sampled at
        // the wrong instant — a sub-run's clock restarts at zero, and the model's law
        // time offset is what re-bases it. A ramped flux AND a ramped prescribed
        // temperature cover both law kinds; every stored state must match to the bit.
        // The step is DYADIC on purpose: the re-based instant is (n−1)·dt + dt against
        // the plain run's n·dt, exactly equal only when dt's products are exact — at a
        // general step the two spellings differ by their own ulp, which is the honest
        // boundary of the bit claim (a WRONG offset shifts the ramp by whole steps,
        // which this would catch at any dt).
        const double dt = 0.5;
        const int steps = 6;
        ThermalModel Lawed() => Bar(out _)
            .HeatFlux(Facets.Tag(StructuredTetMesh.YMax), t => 3.0 * t)
            .Temperature(Facets.Tag(StructuredTetMesh.XMax), t => 100.0 + 5.0 * t);

        var plain = ThermalSolver.SolveTransient(
            Lawed(), new ThermalTransientOptions(dt, steps) { InitialTemperature = 20 });
        var run = ThermalNonlinear.SolveTransient(
            Lawed(), new ThermalTransientOptions(dt, steps) { InitialTemperature = 20 },
            capacityByRegion: new Dictionary<int, Func<double, double>>
            {
                [0] = _ => Metal.SpecificHeat,
            });

        Assert.Equal(plain.States.Count, run.States.Count);
        for (int s = 0; s < plain.States.Count; s++)
        {
            var a = plain.States[s].Temperature;
            var b = run.States[s].Temperature;
            for (int node = 0; node < a.Count; node++)
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(a[node]),
                    BitConverter.DoubleToInt64Bits(b[node]));
        }
    }

    [Fact]
    public void TransientLawRefusals_AreByName()
    {
        // No laws at all.
        var model = Bar(out _);
        var ex = Assert.Throws<FeaException>(() => ThermalNonlinear.SolveTransient(
            model, new ThermalTransientOptions(1, 2)));
        Assert.Contains("At least one", ex.Message, StringComparison.Ordinal);

        // A non-positive specific heat is named at its own temperature.
        var bad = AdiabaticCube(1.0, out _);
        ex = Assert.Throws<FeaException>(() => ThermalNonlinear.SolveTransient(
            bad, new ThermalTransientOptions(1, 2),
            capacityByRegion: new Dictionary<int, Func<double, double>> { [0] = _ => -1 }));
        Assert.Contains("capacity law returned", ex.Message, StringComparison.Ordinal);

        // An unknown region has nothing to act on.
        var unknown = AdiabaticCube(1.0, out _);
        ex = Assert.Throws<FeaException>(() => ThermalNonlinear.SolveTransient(
            unknown, new ThermalTransientOptions(1, 2),
            capacityByRegion: new Dictionary<int, Func<double, double>> { [7] = _ => 5e8 }));
        Assert.Contains("region id 7", ex.Message, StringComparison.Ordinal);
    }
}
