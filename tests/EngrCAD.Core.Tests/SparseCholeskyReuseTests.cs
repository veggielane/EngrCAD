using EngrCAD.Core.Solvers;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// <see cref="SparseCholesky.AnalyzePattern"/> and <see cref="SparseCholeskySymbolic"/>: a
/// symbolic factorization analysed once and reused to factorize a family of matrices sharing
/// the pattern.
///
/// <para>The claim that has to hold EXACTLY is that a reused factorization is bit-identical to a
/// fresh <see cref="SparseCholesky.Factorize(PackedSparseMatrix, SparseOrdering,
/// ProgressCancel?)"/> of the same matrix — because the reuse must not change the arithmetic or
/// its ORDER, only skip the symbolic pass. Natural ordering is deterministic, so this is a bit
/// comparison of L's own values, not merely a residual check.</para>
/// </summary>
public class SparseCholeskyReuseTests
{
    /// <summary>The same sparsity pattern with different VALUES: each stored entry is
    /// transformed (diagonal boosted, off-diagonals scaled) so the matrix stays symmetric
    /// positive-definite and diagonally dominant while every value differs. The add sequence is
    /// the base matrix's own stored order, so the packed pattern is identical bit for bit.</summary>
    private static PackedSparseMatrix Perturb(PackedSparseMatrix a, double diagBoost, double offScale)
    {
        var b = new SparseMatrixBuilder(a.Rows, a.Columns);
        for (int r = 0; r < a.Rows; r++)
        {
            var cols = a.RowColumns(r);
            var vals = a.RowValues(r);
            for (int i = 0; i < cols.Length; i++)
            {
                int c = cols[i];
                b.Add(r, c, c == r ? vals[i] + diagBoost : vals[i] * offScale);
            }
        }
        return b.ToSymmetricUpper();
    }

    /// <summary>
    /// <b>A reused factorization is bit-identical to a fresh one</b>, on a family of matrices
    /// sharing the pattern, under both orderings. The whole feature rests on this: L's values,
    /// its pattern, and an end-to-end solve all agree through <c>DoubleToInt64Bits</c>.
    /// </summary>
    [Theory]
    [InlineData(SparseOrdering.Natural)]
    [InlineData(SparseOrdering.Amd)]
    public void ReusedFactorizationIsBitIdenticalToFresh(SparseOrdering ordering)
    {
        var pattern = AmdOrderingTests.GridLaplacian2dDirichlet(12); // 144 unknowns, real fill
        var symbolic = SparseCholesky.AnalyzePattern(pattern, ordering);
        Assert.Equal(ordering, symbolic.Ordering);
        Assert.Equal(pattern.Rows, symbolic.Rows);

        // The pattern's own matrix plus three with entirely different values.
        var family = new[]
        {
            pattern,
            Perturb(pattern, 0.5, 0.9),
            Perturb(pattern, 2.0, 0.75),
            Perturb(pattern, 0.0, 0.5),
        };

        var b = new double[pattern.Rows];
        for (int i = 0; i < b.Length; i++)
            b[i] = System.Math.Sin(i * 0.37) + 1.5;

        foreach (var a in family)
        {
            var fresh = SparseCholesky.Factorize(a, ordering);
            var reused = symbolic.Factorize(a);

            // L is bit-identical: same number of entries, same values in the same slots.
            Assert.Equal(fresh.FactorNonZeroCount, reused.FactorNonZeroCount);
            Assert.Equal(fresh.FactorNonZeroCount, (int)symbolic.FactorNonZeroCount);
            Assert.Equal(fresh.Permutation, reused.Permutation);
            var lFresh = fresh.FactorValues;
            var lReused = reused.FactorValues;
            Assert.Equal(lFresh.Length, lReused.Length);
            for (int i = 0; i < lFresh.Length; i++)
                Assert.Equal(
                    System.BitConverter.DoubleToInt64Bits(lFresh[i]),
                    System.BitConverter.DoubleToInt64Bits(lReused[i]));

            // And the whole solve is bit-identical end to end.
            var xFresh = fresh.Solve(b);
            var xReused = reused.Solve(b);
            for (int i = 0; i < b.Length; i++)
                Assert.Equal(
                    System.BitConverter.DoubleToInt64Bits(xFresh[i]),
                    System.BitConverter.DoubleToInt64Bits(xReused[i]));
        }
    }

    /// <summary>
    /// The same holds on a 3D pattern, where AMD's fill reduction and the permuted value gather
    /// matter most — the regime the topology loop lives in.
    /// </summary>
    [Theory]
    [InlineData(SparseOrdering.Natural)]
    [InlineData(SparseOrdering.Amd)]
    public void ReusedFactorizationIsBitIdentical3d(SparseOrdering ordering)
    {
        var pattern = AmdOrderingTests.GridLaplacian3dDirichlet(6); // 216 unknowns
        var symbolic = SparseCholesky.AnalyzePattern(pattern, ordering);
        var a = Perturb(pattern, 1.25, 0.8);

        var fresh = SparseCholesky.Factorize(a, ordering);
        var reused = symbolic.Factorize(a);

        var lFresh = fresh.FactorValues;
        var lReused = reused.FactorValues;
        Assert.Equal(lFresh.Length, lReused.Length);
        for (int i = 0; i < lFresh.Length; i++)
            Assert.Equal(
                System.BitConverter.DoubleToInt64Bits(lFresh[i]),
                System.BitConverter.DoubleToInt64Bits(lReused[i]));
    }

    /// <summary>
    /// A non-upper input is accepted and gives the same answer — the reuse path converts it to
    /// symmetric-upper exactly as a fresh factorization does, so the gather map still lines up.
    /// </summary>
    [Fact]
    public void AcceptsAGeneralStorageMatrixOfTheSamePattern()
    {
        var upper = AmdOrderingTests.GridLaplacian2dDirichlet(8);
        var symbolic = SparseCholesky.AnalyzePattern(upper, SparseOrdering.Amd);

        var general = Perturb(upper, 0.7, 0.85).ToGeneral();
        Assert.False(general.IsSymmetricUpper);

        var reused = symbolic.Factorize(general);
        var fresh = SparseCholesky.Factorize(general, SparseOrdering.Amd);
        var lFresh = fresh.FactorValues;
        var lReused = reused.FactorValues;
        for (int i = 0; i < lFresh.Length; i++)
            Assert.Equal(
                System.BitConverter.DoubleToInt64Bits(lFresh[i]),
                System.BitConverter.DoubleToInt64Bits(lReused[i]));
    }

    /// <summary>
    /// <b>The pattern guard fires.</b> A matrix of a different dimension, or with a different
    /// number of stored entries, is refused by name rather than silently gathering the wrong
    /// values — the one footgun a cheap guard can catch.
    /// </summary>
    [Fact]
    public void RefusesAMatrixWhosePatternDoesNotMatch()
    {
        var symbolic = SparseCholesky.AnalyzePattern(AmdOrderingTests.GridLaplacian2dDirichlet(8));

        // Different dimension.
        var wrongSize = AmdOrderingTests.GridLaplacian2dDirichlet(9);
        var e1 = Assert.Throws<System.ArgumentException>(() => symbolic.Factorize(wrongSize));
        Assert.Contains("IDENTICAL sparsity pattern", e1.Message);

        // Same dimension, different number of stored entries (an extra off-diagonal band).
        int gridSize = 8, n = gridSize * gridSize;
        var builder = new SparseMatrixBuilder(n, n);
        int Id(int i, int j) => i * gridSize + j;
        for (int i = 0; i < gridSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
            {
                int v = Id(i, j);
                builder.Add(v, v, 6.0);
                if (j + 1 < gridSize) builder.Add(v, Id(i, j + 1), -1.0);
                if (i + 1 < gridSize) builder.Add(v, Id(i + 1, j), -1.0);
                if (i + 1 < gridSize && j + 1 < gridSize) builder.Add(v, Id(i + 1, j + 1), -1.0);
            }
        }
        var denser = builder.ToSymmetricUpper();
        Assert.Equal(n, denser.Rows);
        var e2 = Assert.Throws<System.ArgumentException>(() => symbolic.Factorize(denser));
        Assert.Contains("IDENTICAL sparsity pattern", e2.Message);
    }

    /// <summary>
    /// One symbolic analysis serves a whole family of solves correctly — the loop shape the
    /// topology optimiser uses. Every reused factorization solves its own system to the same
    /// residual a fresh one would.
    /// </summary>
    [Fact]
    public void OneAnalysisServesAFamilyOfSolves()
    {
        var pattern = AmdOrderingTests.GridLaplacian2dDirichlet(10);
        var symbolic = SparseCholesky.AnalyzePattern(pattern, SparseOrdering.Amd);

        var b = new double[pattern.Rows];
        for (int i = 0; i < b.Length; i++)
            b[i] = 1.0;

        for (int k = 0; k < 6; k++)
        {
            var a = Perturb(pattern, 0.1 * k, 1.0 - 0.05 * k);
            var x = symbolic.Factorize(a).Solve(b);
            // A·x = b to round-off.
            var ax = new double[b.Length];
            a.Multiply(x, ax);
            double worst = 0;
            for (int i = 0; i < b.Length; i++)
                worst = System.Math.Max(worst, System.Math.Abs(ax[i] - b[i]));
            Assert.True(worst < 1e-9, $"k={k}: residual {worst:G4}");
        }
    }
}
