using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The structural transient's streaming callback — the thermal twin's pattern on the
/// solver whose states are heaviest (each carries a full StructuralResults): the callback
/// sees exactly the states a retained run stores, bit for bit, and RetainStates = false
/// caps the returned list at the two ends with the summary numbers identical.
/// </summary>
public class TransientStreamingTests
{
    [Fact]
    public void TheCallback_SeesExactlyWhatARetainedRunStores()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(0.01, 0, 0);
        var options = new TransientSolveOptions(1e-6, 30)
        {
            InitialDisplacement = initial,
            StoreEvery = 4,
        };

        var retained = TransientSolver.Solve(model, options);
        var streamed = new List<TransientState>();
        var run = TransientSolver.Solve(model,
            options with { OnState = streamed.Add, RetainStates = false });

        Assert.Equal(retained.States.Count, streamed.Count);
        for (int i = 0; i < streamed.Count; i++)
        {
            Assert.Equal(retained.States[i].Time, streamed[i].Time);
            for (int n = 0; n < model.Mesh.NodeCount; n++)
                Assert.Equal(
                    retained.States[i].Results.Displacement[n],
                    streamed[i].Results.Displacement[n]);
        }

        Assert.Equal(2, run.States.Count);
        Assert.Equal(0.0, run.States[0].Time);
        Assert.Equal(retained.Final.Time, run.Final.Time);
        // The summary numbers track every streamed state, not the retained list.
        Assert.Equal(retained.Report.PeakDisplacement, run.Report.PeakDisplacement);
        Assert.Equal(retained.Report.EnergyBalanceResidual, run.Report.EnergyBalanceResidual);
    }
}
