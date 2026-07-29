using EngrCAD.Core.Solvers;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The <see cref="ProgressCancel"/> seam on the two linear solvers: that it can abort the
/// slowest operation in the library, that passing one changes no arithmetic, and that the
/// fraction it reports means what the doc says it means.
/// </summary>
public class SparseSolverProgressTests
{
    /// <summary>A deterministic dense SPD matrix in sparse storage — the case where a
    /// factor fills completely, so the work per column grows quadratically and column
    /// number is at its most misleading as a progress measure.</summary>
    private static PackedSparseMatrix DenseSpd(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                b[i, j] = rng.NextDouble() * 2 - 1;
        }
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++)
                    sum += b[k, i] * b[k, j];
                builder.Add(i, j, sum + (i == j ? n : 0));
            }
        }
        return builder.ToSymmetricUpper();
    }

    private static double[] Rhs(int n)
    {
        var b = new double[n];
        for (int i = 0; i < n; i++)
            b[i] = 1.0 + (i % 7) * 0.25 - (i % 3) * 0.5;
        return b;
    }

    // ---------- the arithmetic must not move ----------

    /// <summary>
    /// The whole point of a guarded accumulator: a caller who asks for progress must get
    /// the SAME factorization, not one that is nearly the same. A bit comparison rather
    /// than a tolerance, because there is no mechanism by which these two could
    /// legitimately differ in the last place — if they do, the progress path has changed
    /// the arithmetic and the report would be hiding it.
    /// </summary>
    [Theory]
    [InlineData(SparseOrdering.Natural)]
    [InlineData(SparseOrdering.Amd)]
    public void Cholesky_ProgressDoesNotChangeTheFactorization(SparseOrdering ordering)
    {
        var a = AmdOrderingTests.GridLaplacian2d(16);
        var b = Rhs(a.Rows);

        var plain = SparseCholesky.Factorize(a, ordering);
        var watched = SparseCholesky.Factorize(a, ordering, new ProgressCancel(_ => { }));

        Assert.Equal(plain.FactorNonZeroCount, watched.FactorNonZeroCount);
        Assert.Equal(plain.Permutation, watched.Permutation);

        var x1 = plain.Solve(b);
        var x2 = watched.Solve(b);
        for (int i = 0; i < x1.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }

    [Fact]
    public void Cg_ProgressDoesNotChangeTheIterates()
    {
        var a = AmdOrderingTests.GridLaplacian2dDirichlet(20);
        var b = Rhs(a.Rows);
        var options = new CgOptions { RelativeTolerance = 1e-10 };

        var x1 = new double[a.Rows];
        var r1 = SparseSymmetricCG.Solve(a, b, x1, options);
        var x2 = new double[a.Rows];
        var r2 = SparseSymmetricCG.Solve(a, b, x2, options, new ProgressCancel(_ => { }));

        Assert.Equal(r1.Iterations, r2.Iterations);
        for (int i = 0; i < x1.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }

    // ---------- cancellation ----------

    [Fact]
    public void Cholesky_CancelsDuringTheNumericPass()
    {
        var a = AmdOrderingTests.GridLaplacian2d(24);
        // Cancel after the symbolic pass has been through every column once, so the request
        // provably lands in the numeric loop rather than being satisfied by a phase
        // boundary before any work has happened.
        int polls = 0;
        var progress = new ProgressCancel(() => ++polls > a.Rows + 8);

        Assert.Throws<OperationCanceledException>(() => SparseCholesky.Factorize(a, SparseOrdering.Natural, progress));
    }

    [Fact]
    public void Cholesky_CancelsBeforeAnyWorkWhenAlreadyRequested()
    {
        var a = AmdOrderingTests.GridLaplacian2d(12);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => SparseCholesky.Factorize(a, SparseOrdering.Amd, new ProgressCancel(source.Token)));
    }

    [Fact]
    public void Cg_CancelsDuringTheIteration()
    {
        var a = AmdOrderingTests.GridLaplacian2dDirichlet(24);
        var b = Rhs(a.Rows);
        int polls = 0;
        var progress = new ProgressCancel(() => ++polls > 5);

        Assert.Throws<OperationCanceledException>(
            () => SparseSymmetricCG.Solve(a, b, new double[a.Rows], new CgOptions(), progress));
    }

    // ---------- what the fraction means ----------

    [Fact]
    public void Cholesky_ReportsAMonotoneFractionEndingAtExactlyOne()
    {
        var a = AmdOrderingTests.GridLaplacian2d(20);
        var seen = new List<double>();
        SparseCholesky.Factorize(a, SparseOrdering.Natural, new ProgressCancel(seen.Add));

        Assert.NotEmpty(seen);
        Assert.Equal(0.0, seen[0]);
        for (int i = 1; i < seen.Count; i++)
            Assert.True(seen[i] >= seen[i - 1], $"fraction went backwards at {i}: {seen[i - 1]} -> {seen[i]}");
        Assert.All(seen, f => Assert.InRange(f, 0.0, 1.0));
        // Exactly 1, not 0.999...: a progress bar that never reaches its end reads as a
        // hung operation, which is the failure this seam exists to remove.
        Assert.Equal(1.0, seen[^1]);
        // The last report before the explicit completion must already be close, or the
        // accounting has lost track of the work rather than merely being coarse.
        Assert.True(seen[^2] > 0.95, $"the last in-loop report was only {seen[^2]:F3}");
    }

    /// <summary>
    /// The callback count is bounded by the 200-step throttle rather than by the unknown
    /// count — a 400-unknown factorization must not produce 400 callbacks, or a UI
    /// repainting on each would cost more than the arithmetic it reports on.
    /// </summary>
    [Fact]
    public void Cholesky_ThrottlesItsReports()
    {
        var a = AmdOrderingTests.GridLaplacian2d(20); // 400 unknowns
        int reports = 0;
        SparseCholesky.Factorize(a, SparseOrdering.Natural, new ProgressCancel(_ => reports++));

        Assert.True(reports <= 202, $"{reports} reports for {a.Rows} columns");
        Assert.True(reports > 1, "a factorization this size should report more than once");
    }

    /// <summary>
    /// The headline claim, measured: the fraction tracks WORK, not column number. On a
    /// factor that FILLS, the work attributable to column k is the number of entries
    /// already standing in the columns it updates, which grows with k — so at the halfway
    /// column only about an eighth of the arithmetic is done, and a bar driven by column
    /// number would be showing 50%.
    ///
    /// <para><b>The fixture is dense on purpose, and that is the honest part.</b> A
    /// natural-ordered 2D grid Laplacian's factor is BANDED, so its columns cost nearly the
    /// same as each other and column number is a perfectly good proxy there (measured:
    /// 0.468 at halfway on a 24x24 grid). The re-weighting only earns its keep where fill
    /// concentrates, which is every case worth cancelling.</para>
    ///
    /// <para>The column count comes from the cancel callback, which is invoked exactly once
    /// per column of the numeric pass; the baseline taken at the first report (which is
    /// column 0, fraction 0) discards the symbolic pass's own polls.</para>
    /// </summary>
    [Fact]
    public void Cholesky_ReportsWorkDoneRatherThanColumnsDone()
    {
        var a = DenseSpd(120, seed: 7);
        int polls = 0;
        int baseline = -1;
        var samples = new List<(int Column, double Fraction)>();
        // The cancel delegate is the per-column poll, so it doubles as the column counter.
        var counting = new ProgressCancel(
            () => { polls++; return false; },
            f =>
            {
                if (baseline < 0)
                    baseline = polls;
                samples.Add((polls - baseline, f));
            });

        SparseCholesky.Factorize(a, SparseOrdering.Natural, counting);

        int n = a.Rows;
        // The last report at or before the halfway column. A dense factor's cumulative work
        // is cubic in the column, so the exact expectation is (1/2)^3 = 0.125.
        var atHalfway = samples.Where(s => s.Column <= n / 2).Select(s => s.Fraction).Last();
        Assert.InRange(atHalfway, 0.10, 0.16);
        // ...and the accounting still has to arrive, so it is a re-weighting rather than a
        // stall.
        Assert.Equal(1.0, samples[^1].Fraction);
    }
}
