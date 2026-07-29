using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The solve entry points' <see cref="ProgressCancel"/> parameter, which exists for one
/// reason: a factorization is the slowest thing this library does, and someone watching a
/// two-minute solve should be able to abort it rather than read about it afterwards.
///
/// <para>The parameter is only honest because <c>SparseCholesky.Factorize</c> honours it —
/// the backlog's own condition for adding it here. These tests hold that line: cancellation
/// must reach the phase that costs the time, and asking for progress must not move a single
/// bit of the answer.</para>
/// </summary>
public class SolveCancellationTests
{
    // A density is stated because the modal solver needs one; every other solve here is
    // indifferent to it.
    private static readonly Material Steel = Materials.Steel;

    private static StructuralModel Beam(int n = 3, ElementOrder order = ElementOrder.Linear)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 20, 10), 4 * n, 2 * n, n);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -4000));
        return model;
    }

    /// <summary>
    /// Cancellation requested before the call reaches the factorization. Uses an
    /// already-cancelled token so the test does not have to guess where the poll lands: any
    /// checkpoint at all will see it, which is the property being asserted (that there IS a
    /// checkpoint before the expensive phase, not that it is in a particular place).
    /// </summary>
    [Fact]
    public void Structural_DirectSolveCancels()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => StructuralSolver.Solve(Beam(), null, new ProgressCancel(source.Token)));
    }

    /// <summary>
    /// The same for the iterative method. This is the case the backlog's rule warns about
    /// from the other side: a cancellation that works for a direct solve and not for a CG
    /// one would be exactly the API that looks like it works.
    /// </summary>
    [Fact]
    public void Structural_IterativeSolveCancels()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var options = new StructuralSolveOptions { Method = FeaSolveMethod.ConjugateGradient };
        Assert.Throws<OperationCanceledException>(
            () => StructuralSolver.Solve(Beam(), options, new ProgressCancel(source.Token)));
    }

    /// <summary>
    /// Cancellation that arrives DURING the factorization rather than in front of it: the
    /// poll count is set past every checkpoint the assembly and the guards can make, so the
    /// only place left to observe it is the numeric elimination loop.
    /// </summary>
    [Fact]
    public void Structural_CancelsInsideTheFactorization()
    {
        var model = Beam(4);
        int polls = 0;
        // Assembly polls once per 1024 elements and the guards once each; a beam this size
        // has a few thousand elements, so a few hundred polls is comfortably inside the
        // factorization's per-column loop.
        var progress = new ProgressCancel(() => ++polls > 200);
        Assert.Throws<OperationCanceledException>(() => StructuralSolver.Solve(model, null, progress));
        Assert.True(polls > 200);
    }

    [Fact]
    public void Modal_SolveCancels()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => ModalSolver.Solve(Beam(), new ModalSolveOptions { ModeCount = 2 }, new ProgressCancel(source.Token)));
    }

    [Fact]
    public void Buckling_SolveCancels()
    {
        var model = Beam();
        var reference = StructuralSolver.Solve(model);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => BucklingSolver.Solve(reference, new BucklingSolveOptions { ModeCount = 1 }, new ProgressCancel(source.Token)));
    }

    [Fact]
    public void Thermal_SteadySolveCancels()
    {
        var mesh = AnalysisMesh.Of(StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 10, 10), 8, 4, 4));
        var model = new ThermalModel(mesh, Materials.Steel);
        model.Temperature(Facets.Tag(StructuredTetMesh.XMin), 100);
        model.Temperature(Facets.Tag(StructuredTetMesh.XMax), 0);

        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => ThermalSolver.Solve(model, null, new ProgressCancel(source.Token)));
    }

    /// <summary>
    /// A transient's progress is its STEP count, and that is the one solve here whose
    /// fraction is not the factorization's — because it factors once and then spends the run
    /// in back-substitutions of uniform cost. Asserted as a property of the numbers rather
    /// than by reading the code: the fractions must be monotone, start at zero, and finish
    /// at exactly one.
    /// </summary>
    [Fact]
    public void Thermal_TransientReportsItsStepFractionAndCancels()
    {
        var mesh = AnalysisMesh.Of(StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(20, 10, 10), 8, 4, 4));
        var model = new ThermalModel(mesh, Materials.Steel);
        model.Temperature(Facets.Tag(StructuredTetMesh.XMin), 100);

        var transient = new ThermalTransientOptions(0.05, 20) { InitialTemperature = 0 };

        var seen = new List<double>();
        ThermalSolver.SolveTransient(model, transient, null, new ProgressCancel(seen.Add));
        Assert.Equal(0.0, seen[0]);
        for (int i = 1; i < seen.Count; i++)
            Assert.True(seen[i] >= seen[i - 1]);
        Assert.Equal(1.0, seen[^1]);
        // One report per step plus the completion: a step is the unit of work, so the
        // fraction is exact rather than a phase estimate.
        Assert.Equal(transient.Steps + 1, seen.Count);

        int polls = 0;
        var cancelAfterAFewSteps = new ProgressCancel(() => ++polls > 5);
        Assert.Throws<OperationCanceledException>(
            () => ThermalSolver.SolveTransient(model, transient, null, cancelAfterAFewSteps));
    }

    /// <summary>
    /// Asking for progress must not change the answer. Bit-for-bit, because there is no
    /// mechanism by which a progress callback could legitimately move a displacement —
    /// a tolerance here would let a real change through as "close enough".
    /// </summary>
    [Theory]
    [InlineData(FeaSolveMethod.Direct)]
    [InlineData(FeaSolveMethod.ConjugateGradient)]
    public void Structural_ProgressDoesNotMoveTheAnswer(FeaSolveMethod method)
    {
        var options = new StructuralSolveOptions { Method = method };
        var plain = StructuralSolver.Solve(Beam(), options);
        var watched = StructuralSolver.Solve(Beam(), options, new ProgressCancel(_ => { }));

        Assert.Equal(plain.Displacement.Count, watched.Displacement.Count);
        for (int i = 0; i < plain.Displacement.Count; i++)
        {
            var a = plain.Displacement[i];
            var b = watched.Displacement[i];
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.X), BitConverter.DoubleToInt64Bits(b.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Y), BitConverter.DoubleToInt64Bits(b.Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Z), BitConverter.DoubleToInt64Bits(b.Z));
        }
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(plain.Report.StrainEnergy),
            BitConverter.DoubleToInt64Bits(watched.Report.StrainEnergy));
    }
}
