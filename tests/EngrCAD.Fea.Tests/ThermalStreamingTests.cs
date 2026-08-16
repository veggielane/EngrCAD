using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The transient streaming callback: the callback sees EXACTLY the states a retained run
/// stores — same times, same fields, bit for bit — and RetainStates = false caps the
/// returned list at the two ends while the callback stays the complete record.
/// </summary>
public class ThermalStreamingTests
{
    private static readonly Material Metal = new(
        "streaming metal", 200_000, 0.3, 8e-9,
        thermalConductivity: 40.0, specificHeat: 5e8);

    private static ThermalModel Quench()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 4, 4), 10, 2, 2);
        return new ThermalModel(AnalysisMesh.Of(tets), Metal)
            .Temperature(Facets.Tag(StructuredTetMesh.XMin), 0.0);
    }

    [Fact]
    public void TheCallback_SeesExactlyWhatARetainedRunStores()
    {
        var options = new ThermalTransientOptions(0.1, 12)
        {
            InitialTemperature = 100,
            StoreEvery = 3,
        };
        var retained = ThermalSolver.SolveTransient(Quench(), options);

        var streamed = new List<ThermalResults>();
        var run = ThermalSolver.SolveTransient(Quench(),
            options with { OnState = streamed.Add, RetainStates = false });

        Assert.Equal(retained.States.Count, streamed.Count);
        for (int i = 0; i < streamed.Count; i++)
        {
            Assert.Equal(retained.States[i].Time, streamed[i].Time);
            for (int node = 0; node < streamed[i].Temperature.Count; node++)
                Assert.Equal(
                    retained.States[i].Temperature[node], streamed[i].Temperature[node]);
        }

        // RetainStates = false keeps only the two ends; the summary numbers survive.
        Assert.Equal(2, run.States.Count);
        Assert.Equal(0.0, run.States[0].Time);
        Assert.Equal(retained.Final.Time, run.Final.Time);
        for (int node = 0; node < run.Final.Temperature.Count; node++)
            Assert.Equal(retained.Final.Temperature[node], run.Final.Temperature[node]);
        Assert.Equal(
            retained.Report.EnergyBalanceResidual, run.Report.EnergyBalanceResidual);
    }
}
