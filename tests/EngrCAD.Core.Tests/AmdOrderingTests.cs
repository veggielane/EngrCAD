using EngrCAD.Core.Solvers;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// AMD is a heuristic, so nothing here asserts a particular permutation. What it asserts
/// is what a fill-reducing ordering has to be: a valid permutation, an answer that agrees
/// with the natural-order (and dense) solve, deterministic, and actually less fill on the
/// patterns where fill is the whole point.
/// </summary>
public class AmdOrderingTests
{
    // ---------- fixtures ----------

    /// <summary>5-point 2D grid Laplacian + identity — the mesh-smoother shape.</summary>
    internal static PackedSparseMatrix GridLaplacian2d(int gridSize)
    {
        int n = gridSize * gridSize;
        var builder = new SparseMatrixBuilder(n, n);
        int Id(int i, int j) => i * gridSize + j;
        void Edge(int v, int u)
        {
            builder.Add(Math.Min(v, u), Math.Max(v, u), -1.0);
            builder.Add(v, v, 1.0);
            builder.Add(u, u, 1.0);
        }
        for (int i = 0; i < gridSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
            {
                int v = Id(i, j);
                builder.Add(v, v, 1.0);
                if (j + 1 < gridSize)
                    Edge(v, Id(i, j + 1));
                if (i + 1 < gridSize)
                    Edge(v, Id(i + 1, j));
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>
    /// 7-point 3D grid Laplacian + identity. FEA stiffness matrices are 3D, and 3D is
    /// where natural ordering hurts most: a 2D grid's natural fill is O(n^1.5), a 3D
    /// grid's is O(n^2).
    /// </summary>
    internal static PackedSparseMatrix GridLaplacian3d(int gridSize)
    {
        int n = gridSize * gridSize * gridSize;
        var builder = new SparseMatrixBuilder(n, n);
        int Id(int i, int j, int k) => (i * gridSize + j) * gridSize + k;
        void Edge(int v, int u)
        {
            builder.Add(Math.Min(v, u), Math.Max(v, u), -1.0);
            builder.Add(v, v, 1.0);
            builder.Add(u, u, 1.0);
        }
        for (int i = 0; i < gridSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
            {
                for (int k = 0; k < gridSize; k++)
                {
                    int v = Id(i, j, k);
                    builder.Add(v, v, 1.0);
                    if (k + 1 < gridSize)
                        Edge(v, Id(i, j, k + 1));
                    if (j + 1 < gridSize)
                        Edge(v, Id(i, j + 1, k));
                    if (i + 1 < gridSize)
                        Edge(v, Id(i + 1, j, k));
                }
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>
    /// The 5-point 2D Laplacian with DIRICHLET boundaries and no identity shift: every
    /// node keeps the full stencil diagonal (4) but only interior neighbours contribute
    /// off-diagonals. SPD, and — unlike the shifted form above — its condition number
    /// grows like the grid size squared, which is what makes it the honest stand-in for
    /// an FEA stiffness matrix when comparing a direct solve against CG.
    /// </summary>
    internal static PackedSparseMatrix GridLaplacian2dDirichlet(int gridSize)
    {
        int n = gridSize * gridSize;
        var builder = new SparseMatrixBuilder(n, n);
        int Id(int i, int j) => i * gridSize + j;
        for (int i = 0; i < gridSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
            {
                int v = Id(i, j);
                builder.Add(v, v, 4.0);
                if (j + 1 < gridSize)
                    builder.Add(v, Id(i, j + 1), -1.0);
                if (i + 1 < gridSize)
                    builder.Add(v, Id(i + 1, j), -1.0);
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>The 7-point 3D counterpart of <see cref="GridLaplacian2dDirichlet"/>.</summary>
    internal static PackedSparseMatrix GridLaplacian3dDirichlet(int gridSize)
    {
        int n = gridSize * gridSize * gridSize;
        var builder = new SparseMatrixBuilder(n, n);
        int Id(int i, int j, int k) => (i * gridSize + j) * gridSize + k;
        for (int i = 0; i < gridSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
            {
                for (int k = 0; k < gridSize; k++)
                {
                    int v = Id(i, j, k);
                    builder.Add(v, v, 6.0);
                    if (k + 1 < gridSize)
                        builder.Add(v, Id(i, j, k + 1), -1.0);
                    if (j + 1 < gridSize)
                        builder.Add(v, Id(i, j + 1, k), -1.0);
                    if (i + 1 < gridSize)
                        builder.Add(v, Id(i + 1, j, k), -1.0);
                }
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>
    /// An "arrow" matrix whose dense row is FIRST: a chain 1—2—…—(n−1) plus node 0
    /// connected to everything. Eliminating node 0 first makes the whole rest of the
    /// matrix dense, so natural ordering fills completely; any competent ordering leaves
    /// it until last and creates no fill at all. The crispest possible signal that a
    /// fill-reducing ordering is doing its job.
    /// </summary>
    private static PackedSparseMatrix ArrowWithDenseFirstRow(int n)
    {
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
            builder.Add(i, i, n + 4.0);
        for (int j = 1; j < n; j++)
            builder.Add(0, j, -1.0);
        for (int j = 1; j + 1 < n; j++)
            builder.Add(j, j + 1, -1.0);
        return builder.ToSymmetricUpper();
    }

    private static double[] Rhs(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new double[n];
        for (int i = 0; i < n; i++)
            b[i] = rng.NextDouble() * 2 - 1;
        return b;
    }

    private static double ResidualNorm(PackedSparseMatrix a, double[] x, double[] b)
    {
        var r = a.Multiply(x);
        double sum = 0;
        for (int i = 0; i < b.Length; i++)
            sum += (r[i] - b[i]) * (r[i] - b[i]);
        return Math.Sqrt(sum);
    }

    // ---------- the permutation itself ----------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(20)]
    public void Amd_ProducesAValidPermutation(int gridSize)
    {
        var a = GridLaplacian2d(gridSize);
        var permutation = SparseCholesky.Factorize(a, SparseOrdering.Amd).Permutation;

        Assert.Equal(a.Rows, permutation.Length);
        var seen = new bool[a.Rows];
        foreach (int index in permutation)
        {
            Assert.InRange(index, 0, a.Rows - 1);
            Assert.False(seen[index], $"index {index} appears twice in the permutation");
            seen[index] = true;
        }
    }

    [Fact]
    public void Natural_ReportsTheIdentityPermutation()
    {
        var a = GridLaplacian2d(6);
        var permutation = SparseCholesky.Factorize(a).Permutation;
        for (int i = 0; i < permutation.Length; i++)
            Assert.Equal(i, permutation[i]);
    }

    [Fact]
    public void Amd_IsDeterministic()
    {
        var a = GridLaplacian2d(15);
        var first = SparseCholesky.Factorize(a, SparseOrdering.Amd);
        var second = SparseCholesky.Factorize(a, SparseOrdering.Amd);
        Assert.Equal(first.Permutation, second.Permutation);
        Assert.Equal(first.FactorNonZeroCount, second.FactorNonZeroCount);

        var b = Rhs(a.Rows, 91);
        var x1 = first.Solve(b);
        var x2 = second.Solve(b);
        for (int i = 0; i < x1.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }

    // ---------- it still solves the system ----------

    [Fact]
    public void Amd_SolvesToTheSameAnswerAsNaturalOrder()
    {
        var a = GridLaplacian2d(24); // 576 unknowns
        var b = Rhs(a.Rows, 7);

        var natural = SparseCholesky.Factorize(a).Solve(b);
        var amd = SparseCholesky.Factorize(a, SparseOrdering.Amd).Solve(b);

        // Same system, different arithmetic: the answers agree to solver accuracy, and
        // are deliberately NOT asserted bit-identical — reordering IS a change of
        // summation order, which is exactly why Natural stays the default.
        for (int i = 0; i < a.Rows; i++)
            Assert.Equal(natural[i], amd[i], 10);
        Assert.True(ResidualNorm(a, amd, b) < 1e-11);
    }

    [Fact]
    public void Amd_MatchesADenseReferenceOnRandomSpd()
    {
        const int n = 40;
        var rng = new Random(321);
        var m = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                m[i, j] = rng.NextDouble() * 2 - 1;
        }
        var dense = new double[n, n];
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++)
                    sum += m[k, i] * m[k, j];
                dense[i, j] = sum + (i == j ? n : 0);
                if (j >= i)
                    builder.Add(i, j, dense[i, j]);
            }
        }
        var a = builder.ToSymmetricUpper();
        var b = Rhs(n, 6);

        var x = SparseCholesky.Factorize(a, SparseOrdering.Amd).Solve(b);

        // Gaussian elimination on the dense copy as the independent reference.
        var work = (double[,])dense.Clone();
        var rhs = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            for (int r = c + 1; r < n; r++)
            {
                double factor = work[r, c] / work[c, c];
                for (int q = c; q < n; q++)
                    work[r, q] -= factor * work[c, q];
                rhs[r] -= factor * rhs[c];
            }
        }
        var expected = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            expected[r] = rhs[r];
            for (int c = r + 1; c < n; c++)
                expected[r] -= work[r, c] * expected[c];
            expected[r] /= work[r, r];
        }
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 9);
    }

    [Fact]
    public void Amd_HandlesDisconnectedComponentsAndIsolatedNodes()
    {
        // Two disjoint chains plus a node with no off-diagonal at all — the degree-zero
        // branch, which retires a node before the first pivot is ever chosen.
        const int n = 41;
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
            builder.Add(i, i, 4.0);
        for (int i = 0; i + 1 < 20; i++)
            builder.Add(i, i + 1, -1.0);
        for (int i = 21; i + 1 < n; i++)
            builder.Add(i, i + 1, -1.0);
        var a = builder.ToSymmetricUpper();   // node 20 is isolated
        var b = Rhs(n, 3);

        var x = SparseCholesky.Factorize(a, SparseOrdering.Amd).Solve(b);
        Assert.True(ResidualNorm(a, x, b) < 1e-12);
        Assert.Equal(b[20] / 4.0, x[20], 12);
    }

    [Fact]
    public void Amd_HandlesRowsPastTheDenseCutoff()
    {
        // Several rows connected to everything: past the dense cutoff they are set aside
        // wholesale instead of being degree-ordered, which is a separate code path.
        const int n = 400;
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
            builder.Add(i, i, 2.0 * n);
        for (int hub = 0; hub < 5; hub++)
        {
            for (int j = hub + 1; j < n; j++)
                builder.Add(hub, j, -1.0);
        }
        for (int i = 5; i + 1 < n; i++)
            builder.Add(i, i + 1, -1.0);
        var a = builder.ToSymmetricUpper();
        var b = Rhs(n, 44);

        var x = SparseCholesky.Factorize(a, SparseOrdering.Amd).Solve(b);
        Assert.True(ResidualNorm(a, x, b) < 1e-9, $"residual {ResidualNorm(a, x, b):E3}");
    }

    // ---------- and it actually reduces fill ----------

    [Fact]
    public void Amd_LeavesTheArrowMatrixFillFree_WhereNaturalOrderFillsItCompletely()
    {
        const int n = 400;
        var a = ArrowWithDenseFirstRow(n);

        var natural = SparseCholesky.Factorize(a);
        var amd = SparseCholesky.Factorize(a, SparseOrdering.Amd);

        // Eliminating the hub first turns the chain into a clique: ~n²/2 entries.
        Assert.True(natural.FactorNonZeroCount > n * (n - 1) / 4,
            $"natural fill {natural.FactorNonZeroCount} was expected to be near-complete");
        // Leaving it until last costs nothing beyond A's own pattern (3n − 2 in the
        // upper triangle: n diagonal + (n−1) hub + (n−2) chain).
        Assert.True(amd.FactorNonZeroCount <= 3 * n,
            $"AMD fill {amd.FactorNonZeroCount} should be about {3 * n}");

        var b = Rhs(n, 5);
        Assert.True(ResidualNorm(a, amd.Solve(b), b) < 1e-11);
    }

    [Theory]
    [InlineData(30)]   // 900 unknowns
    [InlineData(60)]   // 3 600
    public void Amd_ReducesFillOnA2dGridLaplacian(int gridSize)
    {
        var a = GridLaplacian2d(gridSize);
        int natural = SparseCholesky.Factorize(a).FactorNonZeroCount;
        int amd = SparseCholesky.Factorize(a, SparseOrdering.Amd).FactorNonZeroCount;
        Assert.True(amd < natural, $"AMD fill {amd} vs natural {natural}");
    }

    [Fact]
    public void Amd_ReducesFillMoreOnA3dGridLaplacian()
    {
        // 3D is the case the ordering exists for: natural nested-dissection-free fill on
        // a g³ grid grows as g⁵ against AMD's much slower growth, so the ratio here is
        // far larger than the 2D one above.
        var a = GridLaplacian3d(14); // 2 744 unknowns
        int natural = SparseCholesky.Factorize(a).FactorNonZeroCount;
        int amd = SparseCholesky.Factorize(a, SparseOrdering.Amd).FactorNonZeroCount;
        Assert.True(amd * 2 < natural, $"AMD fill {amd} vs natural {natural}");
    }
}
