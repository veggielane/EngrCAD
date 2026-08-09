using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Adaptive time stepping (<see cref="TransientSolver.SolveAdaptive"/>): a small DYADIC set of
/// step sizes with a factorization cached per size, so a multi-scale run — a sharp start then a
/// long ring-down — spends the fine step only where the local error demands it.
///
/// <para><b>The oracle is fuzzier than a closed form, so it is pinned in two exact parts and one
/// measured one.</b> Exact: a single-element size-set (<c>Levels == 1</c>) reproduces the
/// constant-step run BIT for bit — the adaptive path is the same step arithmetic, so with no
/// finer size to choose it is the constant path — and the FACTORIZATION COUNT is exactly the
/// distinct sizes used (plus the mass factorization). Measured: a genuinely multi-scale problem
/// — a damped free decay whose amplitude falls by orders of magnitude — matches a uniform-fine
/// reference to a stated tolerance while taking materially FEWER steps and factoring at most one
/// matrix per size, which is what the whole design buys over a continuously varying step that
/// would refactor at every change.</para>
/// </summary>
public class TransientAdaptiveTests(ITestOutputHelper output)
{
    /// <summary>
    /// A single-element size-set reproduces the constant-step <see cref="TransientSolver.Solve"/>
    /// run bit for bit, and factors the same number of matrices — the internal seam that pins
    /// the adaptive path as a strict extension. Driven by an initial displacement AND damping so
    /// the run genuinely moves and every arm of the step (mass, stiffness, damping) is exercised.
    /// </summary>
    [Fact]
    public void ASingleSizeSet_IsBitIdenticalToTheConstantRun()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, mass, omega) = TransientFixtures.Properties(model, node);

        const double zeta = 0.03;
        model.Dashpot(node, new Vector3d(1, 0, 0), 2.0 * mass * omega * zeta);

        const double u0 = 0.01;
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(u0, 0, 0);

        double dt = (2 * Math.PI / omega) / 40;
        const int steps = 400;
        var transient = new TransientSolveOptions(dt, steps) { InitialDisplacement = initial };

        var constant = TransientSolver.Solve(model, transient);
        var adaptive = TransientSolver.SolveAdaptive(
            model, transient, new TransientAdaptiveOptions { Levels = 1, Tolerance = 1e9 });

        Assert.Equal(constant.States.Count, adaptive.States.Count);
        Assert.Equal(constant.Report.Factorizations, adaptive.Report.Factorizations);
        Assert.Equal(steps, adaptive.Report.AdaptiveSteps);

        long differing = 0;
        for (int i = 0; i < constant.States.Count; i++)
        {
            var a = constant.States[i];
            var b = adaptive.States[i];
            Assert.Equal(a.Time, b.Time);
            for (int n = 0; n < model.Mesh.NodeCount; n++)
            {
                differing += CountBitDifferences(a.DisplacementAt(n), b.DisplacementAt(n));
                differing += CountBitDifferences(a.VelocityAt(n), b.VelocityAt(n));
                differing += CountBitDifferences(a.AccelerationAt(n), b.AccelerationAt(n));
            }
        }
        output.WriteLine($"factorizations {adaptive.Report.Factorizations}, differing bits {differing}");
        Assert.Equal(0, differing);
    }

    /// <summary>
    /// A multi-scale problem — a damped free decay from a large initial velocity, whose
    /// amplitude falls by orders of magnitude — is matched by the adaptive run to a stated
    /// tolerance while taking materially FEWER steps than the uniform-fine reference and
    /// factoring at most one matrix per size.
    /// </summary>
    [Fact]
    public void AMultiScaleDecay_MatchesTheFineReferenceWithFewerSteps()
    {
        var model = TransientFixtures.SingleDof(out int node);
        var (_, mass, omega) = TransientFixtures.Properties(model, node);

        const double zeta = 0.12;   // enough that the ring-down goes quiet within the run
        model.Dashpot(node, new Vector3d(1, 0, 0), 2.0 * mass * omega * zeta);

        // A sharp start: plucked to a large displacement and released, then damped free decay —
        // the amplitude falls by orders of magnitude, so the local error does too.
        double period = 2 * Math.PI / omega;
        var initial = new Vector3d[model.Mesh.NodeCount];
        initial[node] = new Vector3d(0.02, 0, 0);

        const int levels = 3;                 // sizes {dt0, dt0/2, dt0/4}
        double dt0 = period / 16;             // the coarsest resolves the mode adequately
        int coarseSteps = 16 * 30;            // 30 periods of coarse steps
        var transient = new TransientSolveOptions(dt0, coarseSteps) { InitialDisplacement = initial };

        // The uniform-fine reference: the finest size at every step.
        double fine = dt0 / (1 << (levels - 1));
        var reference = TransientSolver.Solve(
            model, new TransientSolveOptions(fine, coarseSteps * (1 << (levels - 1)))
            {
                InitialDisplacement = initial,
            });

        // A local-error tolerance that refines the large-amplitude start and coarsens the
        // decayed tail — the amplitude falls ~1000x over the run, so the local error does too.
        var adaptive = TransientSolver.SolveAdaptive(
            model, transient,
            new TransientAdaptiveOptions { Levels = levels, Tolerance = 1e-6 });

        // A genuinely multi-scale split: more than one size was used.
        int levelsUsed = adaptive.Report.StepsPerLevel!.Count(s => s > 0);
        output.WriteLine("steps per level: "
            + string.Join(", ", adaptive.Report.StepsPerLevel!));
        Assert.True(levelsUsed >= 2, $"only {levelsUsed} size(s) used");

        // At most one factorization per size, plus one for the initial acceleration's mass
        // solve — NOT one per step change (the whole point of caching per size).
        Assert.True(adaptive.Report.Factorizations <= levels + 1,
            $"{adaptive.Report.Factorizations} factorizations for {levels} sizes");

        // Materially fewer steps than the uniform-fine reference — and, the headline, only a
        // handful of factorizations against the ~thousand a continuously varying step would do.
        int fineSteps = reference.Report.Steps;
        output.WriteLine($"adaptive steps {adaptive.Report.AdaptiveSteps} vs fine {fineSteps}, "
            + $"factorizations {adaptive.Report.Factorizations}");
        Assert.True(adaptive.Report.AdaptiveSteps < 0.6 * fineSteps,
            $"adaptive {adaptive.Report.AdaptiveSteps} not materially fewer than {fineSteps}");

        // Matches the fine reference to a tolerance: the adaptive stores at dyadic times, each of
        // which is a fine-grid time, so compare at the exact same instant.
        double peak = reference.States.Max(s => Math.Abs(s.DisplacementAt(node).X));
        double worst = 0;
        foreach (var state in adaptive.States)
        {
            int fineIndex = (int)Math.Round(state.Time / fine);
            fineIndex = Math.Clamp(fineIndex, 0, reference.States.Count - 1);
            var refState = reference.States[fineIndex];
            worst = Math.Max(worst, Math.Abs(state.DisplacementAt(node).X - refState.DisplacementAt(node).X));
        }
        double relative = worst / peak;
        output.WriteLine($"worst |adaptive - fine| {worst:G6}, peak {peak:G6}, relative {relative:P4}");
        // The adaptive controls LOCAL error, so it tracks the fine reference to well under 1%
        // (measured ~0.01%); the bound is loose enough to survive step-count retuning.
        Assert.True(relative < 5e-3, $"adaptive vs fine {relative:P4}");
    }

    /// <summary>An iterative solve is refused: adaptive stepping exists to reuse a
    /// factorization, which an iterative solve has none of.</summary>
    [Fact]
    public void AnIterativeSolve_IsRefused()
    {
        var model = TransientFixtures.SingleDof(out _);
        var ex = Assert.Throws<FeaException>(() => TransientSolver.SolveAdaptive(
            model, new TransientSolveOptions(1e-5, 10),
            new TransientAdaptiveOptions { Levels = 2, Tolerance = 1e-3 },
            new StructuralSolveOptions { Method = FeaSolveMethod.ConjugateGradient }));
        Assert.Contains("Direct", ex.Message);
    }

    /// <summary>A prescribed (moving or held) support is refused in v1: it fights the per-size
    /// caching.</summary>
    [Fact]
    public void APrescribedSupport_IsRefused()
    {
        var model = TransientFixtures.SingleDof(out int node);
        // A fully-fixed node moved off zero — a held support, which v1 refuses.
        int held = node == 0 ? 1 : 0;
        model.PrescribeNode(held, new Vector3d(0.001, 0, 0));
        var ex = Assert.Throws<FeaException>(() => TransientSolver.SolveAdaptive(
            model, new TransientSolveOptions(1e-5, 10),
            new TransientAdaptiveOptions { Levels = 2, Tolerance = 1e-3 }));
        Assert.Contains("prescribed", ex.Message);
    }

    private static long CountBitDifferences(Vector3d a, Vector3d b) =>
        (BitDiff(a.X, b.X) ? 1 : 0) + (BitDiff(a.Y, b.Y) ? 1 : 0) + (BitDiff(a.Z, b.Z) ? 1 : 0);

    private static bool BitDiff(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b);
}
