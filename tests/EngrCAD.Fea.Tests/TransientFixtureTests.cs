using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Checks on the FIXTURES themselves, which this project has twice learned is not optional: a
/// regression fixture that has quietly stopped carrying the configuration it exists to test
/// passes for ever and protects nothing.
/// </summary>
public class TransientFixtureTests(ITestOutputHelper output)
{
    [Fact]
    public void SingleDofFixture_HasOneFreeDegreeOfFreedom()
    {
        var model = TransientFixtures.SingleDof(out int freeNode);
        int free = 0;
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            var restraint = model.RestraintOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                if (((int)restraint & (1 << axis)) == 0)
                    free++;
            }
        }
        output.WriteLine($"nodes {model.Mesh.NodeCount}, free DOF {free}, free node {freeNode}");
        Assert.Equal(1, free);

        var (k, m, omega) = TransientFixtures.Properties(model, freeNode);
        output.WriteLine($"k = {k:G8}, m = {m:G8}, omega = {omega:G8}, f = {omega / (2 * Math.PI):G8} Hz");
        Assert.True(k > 0);
        Assert.True(m > 0);
        Assert.True(omega > 0);
    }

    [Fact]
    public void SpectralRadiusApproachesItsLimit()
    {
        // How large omega.dt has to be before the measured per-step decay IS the asymptotic
        // spectral radius. Measured rather than assumed, because a test that reads it at too
        // small an omega.dt would report a systematic error and look like a wrong formula.
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(0.01, 0, 0);

        foreach (double rho in new[] { 0.9, 0.8, 0.5 })
        {
            output.WriteLine($"rho_inf = {rho}:");
            foreach (double omegaDt in new[] { 1e2, 1e3, 1e4, 1e5, 1e6, 1e7 })
            {
                var results = TransientSolver.Solve(
                    model,
                    new TransientSolveOptions(omegaDt / omega, 30)
                    {
                        Integration = TimeIntegration.ForSpectralRadius(rho),
                        InitialDisplacement = initial,
                    });
                double e20 = results.States[20].TotalEnergy;
                double e30 = results.States[30].TotalEnergy;
                double measured = Math.Pow(e30 / e20, 1.0 / 20);
                // Per-step displacement ratios, to see whether the decay is clean or ringing.
                double r1 = Math.Abs(
                    results.States[29].DisplacementAt(node).X
                    / results.States[28].DisplacementAt(node).X);
                double r2 = Math.Abs(
                    results.States[30].DisplacementAt(node).X
                    / results.States[29].DisplacementAt(node).X);
                output.WriteLine(
                    $"  omega.dt {omegaDt,8:G1}: energy-measured {measured:G8} "
                    + $"({measured / rho - 1:P3}), |u| ratios {r1:G6} {r2:G6}");
            }
        }
    }
}
