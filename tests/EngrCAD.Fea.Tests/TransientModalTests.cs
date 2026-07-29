using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The cross-check between the two solvers, which is worth more than either alone: a free
/// vibration seeded with ONE mode shape must stay in that mode and oscillate at that mode's
/// frequency.
///
/// <para><b>Why it is exact rather than approximate.</b> Put <c>u = q(t)·phi</c> into
/// <c>M·u'' + K·u = 0</c>; since <c>K·phi = omega²·M·phi</c> this is
/// <c>M·phi·(q'' + omega²·q) = 0</c>, and <c>M·phi</c> is not the zero vector, so
/// <c>q'' + omega²·q = 0</c> exactly — for the DISCRETE system, with no appeal to the
/// continuum. So the response provably never leaves the mode, and the two solvers can only
/// both pass this if both are right: a wrong mass matrix moves the frequency, a wrong mode
/// shape leaks into other modes, and a wrong integrator does one or the other.</para>
/// </summary>
public class TransientModalTests(ITestOutputHelper output)
{
    private static (StructuralModel Model, VibrationMode Mode) Bar(int modeNumber = 1)
    {
        var model = ModalFixtures.AxialBar(200, 10, 20, ElementOrder.Quadratic);
        var modal = ModalSolver.Solve(
            model, new ModalSolveOptions { ModeCount = Math.Max(modeNumber, 2) });
        return (model, modal.Mode(modeNumber));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ASingleModeSeed_StaysInThatMode(int modeNumber)
    {
        var (model, mode) = Bar(modeNumber);
        double omega = mode.AngularFrequency;
        double period = 2 * Math.PI / omega;

        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(period / 120, 120 * 4)
            {
                InitialDisplacement = mode.Shape,
            });

        // The mode's own scale, so "how far out of the mode" is relative to something.
        double scale = 0;
        for (int node = 0; node < model.Mesh.NodeCount; node++)
            scale = Math.Max(scale, mode.ShapeAt(node).Length);

        // The reference amplitude q(t) is read off the node with the largest shape component,
        // and every other node must then reproduce q(t)·phi to round-off.
        int probe = 0;
        double best = 0;
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            if (Math.Abs(mode.ShapeAt(node).X) > best)
            {
                best = Math.Abs(mode.ShapeAt(node).X);
                probe = node;
            }
        }

        double worstLeak = 0;
        double worstFrequency = 0;
        foreach (var state in results.States)
        {
            double q = state.DisplacementAt(probe).X / mode.ShapeAt(probe).X;
            for (int node = 0; node < model.Mesh.NodeCount; node++)
            {
                var expected = mode.ShapeAt(node) * q;
                worstLeak = Math.Max(
                    worstLeak, (state.DisplacementAt(node) - expected).Length / (scale * Math.Max(Math.Abs(q), 1e-3)));
            }
            worstFrequency = Math.Max(
                worstFrequency, Math.Abs(q - Math.Cos(omega * state.Time)));
        }

        output.WriteLine(
            $"mode {modeNumber} at {mode.Frequency:G8} Hz on {model.Mesh.NodeCount} nodes "
            + $"({model.Mesh.ElementCount} quadratic elements): worst leak out of the mode "
            + $"{worstLeak:E3}, worst |q - cos(wt)| {worstFrequency:E3}");

        // Out of the mode: a linear-solve residual, not a physical quantity.
        Assert.True(worstLeak < 1e-9, $"leak out of mode {modeNumber}: {worstLeak:E3}");

        // In the mode: the integrator's own phase error at 120 steps per period over four
        // periods, 2.pi.4.(2.pi/120)^2/12 = 5.7e-3 radians.
        double w = 2 * Math.PI / 120;
        double predicted = 2 * Math.PI * 4 * w * w / 12.0;
        output.WriteLine($"  predicted phase error {predicted:E3} radians");
        Assert.InRange(worstFrequency, 0.85 * predicted, 1.05 * predicted);
    }

    [Fact]
    public void TheMeasuredPeriodAgreesWithTheModalFrequency()
    {
        // The frequency read off the time history, against the eigen-solve's own. They come
        // from completely different algorithms - shift-and-invert Lanczos against a stepped
        // linear system - over the same assembled K and M.
        var (model, mode) = Bar();
        double omega = mode.AngularFrequency;
        double period = 2 * Math.PI / omega;

        int probe = 0;
        double best = 0;
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            if (Math.Abs(mode.ShapeAt(node).X) > best)
            {
                best = Math.Abs(mode.ShapeAt(node).X);
                probe = node;
            }
        }

        const int stepsPerPeriod = 400;
        const int periods = 6;
        var results = TransientSolver.Solve(
            model,
            new TransientSolveOptions(period / stepsPerPeriod, stepsPerPeriod * periods)
            {
                InitialDisplacement = mode.Shape,
            });

        // Zero crossings, linearly interpolated. The seed is a cosine, so the first crossing
        // is at a quarter period and they follow every half period after it.
        var crossings = new List<double>();
        var states = results.States;
        for (int i = 1; i < states.Count; i++)
        {
            double a = states[i - 1].DisplacementAt(probe).X;
            double b = states[i].DisplacementAt(probe).X;
            if (a == 0 || (a < 0) == (b < 0))
                continue;
            double t = states[i - 1].Time
                + (states[i].Time - states[i - 1].Time) * a / (a - b);
            crossings.Add(t);
        }

        // The period from the FIRST and LAST crossings, so the measurement averages over the
        // whole run rather than over one cycle.
        double measuredPeriod =
            2 * (crossings[^1] - crossings[0]) / (crossings.Count - 1);
        double measuredHertz = 1.0 / measuredPeriod;
        output.WriteLine(
            $"{crossings.Count} zero crossings over {periods} periods: measured "
            + $"{measuredHertz:G10} Hz against the modal solver's {mode.Frequency:G10} Hz "
            + $"({measuredHertz / mode.Frequency - 1:P4})");

        // The algorithmic frequency is omega·(1 - (omega·dt)²/12) exactly, so the transient
        // is EXPECTED to read low by that much and the comparison is against the prediction
        // rather than against the eigen-solve's value directly.
        double w = 2 * Math.PI / stepsPerPeriod;
        double expectedRatio = 1 - w * w / 12.0;
        output.WriteLine(
            $"predicted ratio {expectedRatio:G12}, measured {measuredHertz / mode.Frequency:G12}");
        Assert.Equal(expectedRatio, measuredHertz / mode.Frequency, 1e-6);
    }

    [Fact]
    public void AFreeBodyIsAccepted_AndAcceleratesAtForceOverMass()
    {
        // The static solver REFUSES an unrestrained body, and the transient solver must not:
        // K alone is singular for a free body but the effective stiffness carries a0·M, which
        // is positive definite on its own. What the run must then reproduce is Newton's second
        // law - the inertia resultant equals the applied force at every instant, exactly,
        // because there are no reactions to take up any of it.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 4, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Materials.Steel)
            .Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(500, 0, 0));

        // The static solver refuses it, by name - which is what makes the transient's
        // acceptance a decision rather than an oversight.
        var refusal = Assert.Throws<FeaException>(() => StructuralSolver.Solve(model));
        output.WriteLine($"static solver: {refusal.Message.Split('.')[0]}.");

        var modal = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
        double period = 1.0 / modal.Mode(1).Frequency;
        var results = TransientSolver.Solve(
            model, new TransientSolveOptions(period / 50, 200));

        output.WriteLine(results.Report.ToText());
        double worst = 0;
        foreach (var state in results.States)
        {
            worst = Math.Max(
                worst,
                (state.InertiaForce - new Vector3d(500, 0, 0)).Length / 500);
        }
        output.WriteLine($"worst |sum(M.a) - F| / |F| = {worst:E3}");
        Assert.True(worst < 1e-10, $"Newton's second law residual {worst:E3}");

        // And the body has moved: a free body under a load does not sit still.
        Assert.True(results.Final.MaxDisplacement > 0);
    }
}
