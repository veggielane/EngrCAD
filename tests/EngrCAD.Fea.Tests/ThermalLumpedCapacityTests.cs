using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Lumped (HRZ) capacity — the monotonicity option. The discriminating fixture is the
/// recorded one: a quench stepped SHORT against the element diffusion time, where the
/// CONSISTENT capacity lets backward Euler undershoot the initial temperature (the matrix,
/// not the scheme) while the lumped diagonal restores the discrete maximum principle —
/// every temperature stays inside [surface, initial] for the whole run.
/// </summary>
public class ThermalLumpedCapacityTests(ITestOutputHelper output)
{
    private static readonly Material Metal = new(
        "lumped metal", 200_000, 0.3, 8e-9,
        thermalConductivity: 40.0, specificHeat: 5e8);

    private const double Alpha = 10.0;               // k/(rho c), mm^2/s

    private static (AnalysisMesh Mesh, ThermalModel Model) QuenchedBar(ElementOrder order)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 4, 4), 40, 1, 1);
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        var model = new ThermalModel(mesh, Metal)
            .Temperature(Facets.Tag(StructuredTetMesh.XMin), 0.0);
        return (mesh, model);
    }

    [Fact]
    public void TheLumpedCapacity_RestoresTheMaximumPrinciple_WhereConsistentUndershoots()
    {
        // 1 mm elements: h²/α = 0.1 s; step at 0.005 s, twenty times shorter.
        var (_, model) = QuenchedBar(ElementOrder.Linear);
        var transient = new ThermalTransientOptions(0.005, 40) { InitialTemperature = 100 };

        // The physical bounds are [surface, initial] = [0, 100]; the violation is
        // whichever side a run leaves them on (the consistent artifact pushes the node
        // NEXT to the quenched face ABOVE the initial temperature).
        double Violation(ThermalTransientOptions options)
        {
            var run = ThermalSolver.SolveTransient(model, options);
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var state in run.States)
            {
                foreach (double temperature in state.Temperature)
                {
                    min = Math.Min(min, temperature);
                    max = Math.Max(max, temperature);
                }
            }
            output.WriteLine($"  {options.Lumping}: min {min:G8}, max {max:G8}");
            return Math.Max(0 - min, max - 100);
        }

        double consistent = Violation(transient);
        double lumped = Violation(transient with { Lumping = MassLumping.Hrz });

        // The consistent capacity genuinely violates the bound (the recorded ~5% artifact)
        // — without this half the lumped assertion proves nothing.
        Assert.True(consistent > 1,
            $"the fixture must exhibit the violation; consistent {consistent:G6}");
        // Lumped backward Euler is an M-matrix step: no temperature leaves [0, 100].
        Assert.True(lumped <= 1e-9, $"lumped violation {lumped:G6}");
    }

    [Fact]
    public void HrzAndRowSum_Coincide_OnLinearElements()
    {
        var (_, model) = QuenchedBar(ElementOrder.Linear);
        var options = new ThermalTransientOptions(0.02, 10) { InitialTemperature = 100 };
        var hrz = ThermalSolver.SolveTransient(model, options with { Lumping = MassLumping.Hrz });
        var rowSum = ThermalSolver.SolveTransient(model, options with { Lumping = MassLumping.RowSum });

        // Mathematically the same lumped matrix; the ARITHMETIC differs (a row summed
        // against a diagonal scaled), so the agreement is to round-off, not to the bit.
        for (int node = 0; node < hrz.Final.Temperature.Count; node++)
        {
            double expected = rowSum.Final.Temperature[node];
            Assert.Equal(expected, hrz.Final.Temperature[node],
                Math.Abs(expected) * 1e-12 + 1e-12);
        }
    }

    [Fact]
    public void TheLumpedRun_KeepsItsOwnFirstLaw_AndTheCapacityTotal()
    {
        // The energy balance is computed against the SAME lumped matrix, so the run's
        // first law must stay at round-off — which is also the statement that lumping
        // preserved every element's capacity (a lost capacity would book stored energy
        // that never arrived).
        var (_, model) = QuenchedBar(ElementOrder.Linear);
        var run = ThermalSolver.SolveTransient(
            model,
            new ThermalTransientOptions(0.05, 40)
            {
                InitialTemperature = 100,
                Lumping = MassLumping.Hrz,
            });
        output.WriteLine($"first-law residual {run.Report.EnergyBalanceResidual:E3}");
        Assert.True(run.Report.EnergyBalanceResidual < 1e-10);
    }

    [Fact]
    public void RowSumOnQuadraticElements_RefusesByName()
    {
        var (_, model) = QuenchedBar(ElementOrder.Quadratic);
        var message = Assert.Throws<FeaException>(() => ThermalSolver.SolveTransient(
            model,
            new ThermalTransientOptions(0.05, 2) { Lumping = MassLumping.RowSum })).Message;
        Assert.Contains("NEGATIVE heat capacity", message);
        Assert.Contains("Hrz", message);
    }
}
