using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Convergence in TIME, measured against an exact solution of the semi-discrete system.
///
/// <para><b>The fixture is chosen so that the order is genuinely observable</b>, which this
/// project has twice found is not automatic — a geometrically graded conduction mesh made the
/// exact profile satisfy the discrete equations identically, and a cubic manufactured solution
/// was reproduced exactly at the nodes; both reported a "convergence order" that was the ratio
/// of two round-off figures. Here the exact answer is <c>u0·cos(omega·t)</c>, which no member
/// of the Newmark family reproduces at any step size (the algorithmic frequency is
/// <c>omega·(1 - (omega·dt)²/12)</c>, never <c>omega</c>), so the error is genuinely
/// <c>O(dt²)</c> and genuinely non-zero. There is no spatial error at all: the reduced system
/// is <c>1 x 1</c>, so <c>u0·cos(omega·t)</c> solves it exactly.</para>
///
/// <para><b>The first-order control is the half that earns its keep.</b> Measuring 2.00 for a
/// second-order scheme proves nothing on its own — a fixture that reports the same number
/// whatever it is handed would say it too. Newmark with <c>gamma = 0.6</c> IS first order, and
/// the same fixture measuring 1.0 for it is what shows the study can tell the difference.</para>
///
/// <para><b>WHERE the error is sampled decided the answer, twice, and the study reported an
/// impossible order both times before it was right.</b> A Newmark run's error has two
/// components — a PHASE lag and an AMPLITUDE decay — and a single-instant probe sees whichever
/// one that instant exposes, never both.</para>
/// <list type="bullet">
/// <item>At a whole number of periods the exact cosine is at a MAXIMUM, where it is
/// stationary: a phase lag <c>d</c> gives an error
/// <c>u0·[cos(2·pi·N - d) - cos(2·pi·N)] ~ u0·d²/2</c>, quadratic in the phase, so an
/// <c>O(dt²)</c> phase error measured <b>3.9997</b> against a theory of 2 (HHT measured 3.18,
/// a mixture with its own amplitude term).</item>
/// <item>At a QUARTER period the cosine passes through zero, so the amplitude multiplies
/// nothing and only the phase shows: the two second-order schemes then measured a clean 1.9998
/// and 1.9997 — and the FIRST-order control measured <b>1.344</b>, because its amplitude decay,
/// the very thing that makes it first order, was invisible there.</item>
/// </list>
/// <para>So the error is measured as a MAXIMUM over the run, on a time grid common to every
/// refinement level (<see cref="TransientSolveOptions.StoreEvery"/> is set so that every level
/// stores exactly the coarsest level's instants — otherwise a finer run is also a finer
/// SAMPLING and the study measures that too). Both components are then in the number and the
/// three schemes separate as they should. It is the same family as this project's
/// graded-conduction-mesh and cubic-manufactured-solution findings: a fixture can make a
/// convergence study measure something other than what it names, an impossible order is the
/// only symptom, and the control that exists to catch a broken study can be broken the same
/// way.</para>
/// </summary>
public class TransientConvergenceTests(ITestOutputHelper output)
{
    /// <summary>Steps per period at the coarsest level; each level halves the step. Every
    /// level is a multiple of the coarsest, which is what lets them share a sampling grid.</summary>
    private static readonly int[] Levels = [48, 96, 192, 384];

    /// <summary>Nine quarter-periods — over two full cycles, so the phase error has
    /// accumulated but the response has not saturated (the error is
    /// <c>amplitude·2·sin(phase/2)</c>, which stops being linear past about a radian and would
    /// drag the measured order down).</summary>
    private const int Quarters = 9;

    /// <summary>
    /// Runs the four refinement levels of a free vibration seeded with
    /// <paramref name="initial"/>, and reports the worst error against
    /// <c>amplitude·cos(omega·t)</c> at <paramref name="probe"/> on the shared time grid.
    /// </summary>
    private double[] Refine(
        TimeIntegration scheme, StructuralModel model, int probe,
        Vector3d[] initial, double amplitude, double omega)
    {
        double period = 2 * Math.PI / omega;
        var errors = new double[Levels.Length];
        for (int i = 0; i < Levels.Length; i++)
        {
            var results = TransientSolver.Solve(
                model,
                new TransientSolveOptions(period / Levels[i], Levels[i] * Quarters / 4)
                {
                    Integration = scheme,
                    InitialDisplacement = initial,
                    StoreEvery = Levels[i] / Levels[0],
                });
            errors[i] = TransientFixtures.WorstError(
                results, probe, t => TransientFixtures.FreeVibration(amplitude, omega, t));
            output.WriteLine(
                $"  {Levels[i],4} steps/period: {results.States.Count} samples, "
                + $"error {errors[i]:E4}"
                + (i > 0 ? $", order {TransientFixtures.Order(errors[i - 1], errors[i]):F3}" : ""));
        }
        return errors;
    }

    private double[] RefineOscillator(TimeIntegration scheme)
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, _, omega) = TransientFixtures.Properties(model, node);
        const double u0 = 0.01;
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(u0, 0, 0);
        return Refine(scheme, model, node, initial, u0, omega);
    }

    [Fact]
    public void NewmarkAverageAcceleration_IsSecondOrderInTheStep()
    {
        output.WriteLine("Newmark (beta 1/4, gamma 1/2), theory 2:");
        var errors = RefineOscillator(TimeIntegration.AverageAcceleration);
        double order = TransientFixtures.Order(errors[^2], errors[^1]);
        output.WriteLine($"finest measured order {order:F4} against theory 2");
        Assert.InRange(order, 1.95, 2.05);
    }

    [Fact]
    public void HilberHughesTaylor_KeepsSecondOrder()
    {
        // The whole point of HHT: numerical damping WITHOUT the order Newmark's raised gamma
        // costs. Its gamma here is 0.55, which on its own would be first order.
        var scheme = TimeIntegration.HilberHughesTaylor(-0.05);
        output.WriteLine($"{scheme} (gamma {scheme.Gamma:G6}), theory 2:");
        var errors = RefineOscillator(scheme);
        double order = TransientFixtures.Order(errors[^2], errors[^1]);
        output.WriteLine($"finest measured order {order:F4} against theory 2");
        Assert.InRange(order, 1.9, 2.1);
        Assert.True(scheme.IsSecondOrder);
    }

    [Fact]
    public void ANumericallyDampedNewmarkMember_IsOnlyFirstOrder()
    {
        // The control. A study that cannot report 1 here cannot be believed when it reports 2
        // above.
        var scheme = TimeIntegration.NumericallyDamped(0.6);
        output.WriteLine($"{scheme}, theory 1:");
        var errors = RefineOscillator(scheme);
        double order = TransientFixtures.Order(errors[^2], errors[^1]);
        output.WriteLine($"finest measured order {order:F4} against theory 1");
        Assert.InRange(order, 0.9, 1.1);
        Assert.False(scheme.IsSecondOrder);
    }

    [Fact]
    public void TheOrderSurvivesOnARealMeshThroughASingleMode()
    {
        // The single-degree-of-freedom fixture removes the space discretization entirely,
        // which is what makes it the right place to measure an order - and also what leaves
        // the question of whether the same order survives assembly, reduction and a genuine
        // sparse solve. Seeding a real bar with one MODE SHAPE answers it: the response
        // provably stays in that mode (M.phi.(q'' + omega^2.q) = 0 follows from
        // K.phi = omega^2.M.phi), so the exact answer is again a cosine and the only error is
        // still the integrator's.
        var model = ModalFixtures.AxialBar(200, 10, 16, ElementOrder.Linear);
        var modal = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 1 });
        var mode = modal.Mode(1);

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
        output.WriteLine(
            $"axial bar: {model.Mesh.NodeCount} nodes, {model.Mesh.ElementCount} elements, "
            + $"mode 1 at {mode.Frequency:G8} Hz, probe node {probe} at "
            + $"shape {mode.ShapeAt(probe).X:G6}");

        var errors = Refine(
            TimeIntegration.AverageAcceleration, model, probe,
            [.. mode.Shape], mode.ShapeAt(probe).X, mode.AngularFrequency);

        double order = TransientFixtures.Order(errors[^2], errors[^1]);
        output.WriteLine($"finest measured order {order:F4} against theory 2");
        Assert.InRange(order, 1.9, 2.1);
    }
}
