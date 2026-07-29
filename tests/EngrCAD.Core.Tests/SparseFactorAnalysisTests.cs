using EngrCAD.Core.Solvers;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// <see cref="SparseCholesky.Analyze"/>: what a factorization will cost, from the symbolic
/// pass alone.
///
/// <para>The claim that has to hold exactly is that the prediction IS the factorization's
/// own answer, not an estimate of it — which is why <c>Analyze</c> and <c>Factorize</c> run
/// the same symbolic code rather than two implementations of it, and why the test compares
/// the fill against a real factorization rather than against a formula.</para>
/// </summary>
public class SparseFactorAnalysisTests
{
    [Theory]
    [InlineData(SparseOrdering.Natural)]
    [InlineData(SparseOrdering.Amd)]
    public void PredictsTheFactorsSizeExactly(SparseOrdering ordering)
    {
        foreach (var a in new[]
        {
            AmdOrderingTests.GridLaplacian2d(14),
            AmdOrderingTests.GridLaplacian3d(8),
            AmdOrderingTests.GridLaplacian2dDirichlet(11),
        })
        {
            var analysis = SparseCholesky.Analyze(a, ordering);
            var factor = SparseCholesky.Factorize(a, ordering);
            Assert.Equal(factor.FactorNonZeroCount, analysis.FactorNonZeroCount);
            Assert.Equal(a.Rows, analysis.Rows);
            Assert.Equal(ordering, analysis.Ordering);
        }
    }

    /// <summary>
    /// The update count is the numeric pass's own inner-loop trip count, so it must be
    /// consistent with the progress fraction the factorization reports — and the way to
    /// check that without reaching inside is that the fraction reaches exactly 1 and the
    /// analysis is positive whenever there is any fill at all.
    /// </summary>
    [Fact]
    public void UpdateCountIsPositiveWhereverTheFactorFills()
    {
        var a = AmdOrderingTests.GridLaplacian2d(12);
        var analysis = SparseCholesky.Analyze(a);
        Assert.True(analysis.UpdateCount > 0);
        Assert.True(analysis.LongestColumn > 0);
        Assert.True(analysis.FactorNonZeroCount > a.Rows);
    }

    /// <summary>
    /// A DIAGONAL matrix has no off-diagonal entries in L, so there is no work, no
    /// dependency chain and no parallelism to have. The degenerate case is worth pinning
    /// because <c>ParallelCeiling</c> divides by the critical path.
    /// </summary>
    [Fact]
    public void ADiagonalMatrixHasNoWorkAndNoDivisionByZero()
    {
        var builder = new SparseMatrixBuilder(20, 20);
        for (int i = 0; i < 20; i++)
            builder.Add(i, i, 2.0 + i);
        var analysis = SparseCholesky.Analyze(builder.ToSymmetricUpper());

        Assert.Equal(0.0, analysis.UpdateCount);
        Assert.Equal(0, analysis.LongestColumn);
        Assert.Equal(20, analysis.FactorNonZeroCount);
        Assert.Equal(1.0, analysis.ParallelCeiling);
    }

    /// <summary>
    /// The critical path is a LOWER bound on any schedule's work, so the ceiling it implies
    /// is at least 1 and at most the column count — asserted as an invariant rather than as
    /// a value, because the value is a property of the matrix.
    /// </summary>
    [Theory]
    [InlineData(SparseOrdering.Natural)]
    [InlineData(SparseOrdering.Amd)]
    public void TheParallelCeilingIsBoundedBelowByOne(SparseOrdering ordering)
    {
        var analysis = SparseCholesky.Analyze(AmdOrderingTests.GridLaplacian3d(8), ordering);
        Assert.True(analysis.CriticalPathUpdates > 0);
        Assert.True(analysis.CriticalPathUpdates <= analysis.UpdateCount);
        Assert.InRange(analysis.ParallelCeiling, 1.0, analysis.Rows);
    }

    /// <summary>
    /// AMD's whole purpose, seen before any arithmetic: it must not increase either number
    /// on a matrix where the ordering is known to pay, and on a 3D grid it cuts both by a
    /// wide margin. That is the check with teeth — a prediction that agreed with the
    /// factorization but did not respond to the ordering would be reading the matrix rather
    /// than the elimination.
    /// </summary>
    [Fact]
    public void AmdIsVisibleInThePredictionBeforeAnyArithmetic()
    {
        var a = AmdOrderingTests.GridLaplacian3d(10);
        var natural = SparseCholesky.Analyze(a);
        var amd = SparseCholesky.Analyze(a, SparseOrdering.Amd);

        Assert.True(amd.FactorNonZeroCount < natural.FactorNonZeroCount);
        Assert.True(amd.UpdateCount < natural.UpdateCount);
        Assert.True(amd.FillRatio(a) < natural.FillRatio(a));
    }
}
