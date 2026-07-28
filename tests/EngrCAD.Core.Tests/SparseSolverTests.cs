using EngrCAD.Core.Solvers;
using Xunit;

namespace EngrCAD.Core.Tests;

public class SparseSolverTests
{
    // ---------- helpers ----------

    /// <summary>Deterministic pseudo-random SPD matrix A = Bᵀ·B + n·I as dense storage.</summary>
    private static double[,] RandomSpd(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                b[i, j] = rng.NextDouble() * 2 - 1;
        }
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++)
                    sum += b[k, i] * b[k, j];
                a[i, j] = sum + (i == j ? n : 0);
            }
        }
        return a;
    }

    private static PackedSparseMatrix ToSparse(double[,] dense)
    {
        int rows = dense.GetLength(0), cols = dense.GetLength(1);
        var builder = new SparseMatrixBuilder(rows, cols);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (dense[r, c] != 0)
                    builder.Add(r, c, dense[r, c]);
            }
        }
        return builder.ToMatrix();
    }

    private static PackedSparseMatrix ToSymmetricSparse(double[,] dense)
    {
        int n = dense.GetLength(0);
        var builder = new SparseMatrixBuilder(n, n);
        for (int r = 0; r < n; r++)
        {
            for (int c = r; c < n; c++)
            {
                if (dense[r, c] != 0)
                    builder.Add(r, c, dense[r, c]);
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>Dense Gaussian elimination with partial pivoting — the reference solver.</summary>
    private static double[] DenseSolve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                    pivot = r;
            }
            if (pivot != col)
            {
                for (int c = 0; c < n; c++)
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                for (int c = col; c < n; c++)
                    m[r, c] -= f * m[col, c];
                x[r] -= f * x[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            for (int c = r + 1; c < n; c++)
                x[r] -= m[r, c] * x[c];
            x[r] /= m[r, r];
        }
        return x;
    }

    /// <summary>5-point 2D grid Laplacian + identity: the SPD shape a mesh smoother assembles.</summary>
    private static PackedSparseMatrix GridLaplacianPlusIdentity(int gridSize)
    {
        int n = gridSize * gridSize;
        var builder = new SparseMatrixBuilder(n, n);
        int Id(int i, int j) => i * gridSize + j;
        void Edge(int v, int u)
        {
            // Each undirected edge assembled once (u > v by construction below).
            builder.Add(v, u, -1.0);
            builder.Add(v, v, 1.0);
            builder.Add(u, u, 1.0);
        }
        for (int i = 0; i < gridSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
            {
                int v = Id(i, j);
                builder.Add(v, v, 1.0); // identity
                if (j + 1 < gridSize)
                    Edge(v, Id(i, j + 1));
                if (i + 1 < gridSize)
                    Edge(v, Id(i + 1, j));
            }
        }
        return builder.ToSymmetricUpper();
    }

    // ---------- PackedSparseMatrix ----------

    [Fact]
    public void Builder_AccumulatesDuplicates_AndSortsColumns()
    {
        var builder = new SparseMatrixBuilder(2, 3);
        builder.Add(0, 2, 5.0);
        builder.Add(0, 0, 1.0);
        builder.Add(0, 2, 2.0);
        builder.Add(1, 1, 4.0);
        var m = builder.ToMatrix();

        Assert.Equal(3, m.NonZeroCount);
        Assert.Equal(1.0, m[0, 0]);
        Assert.Equal(7.0, m[0, 2]);
        Assert.Equal(4.0, m[1, 1]);
        Assert.Equal(0.0, m[0, 1]);
        Assert.Equal(new[] { 0, 2 }, m.RowColumns(0).ToArray());
    }

    [Fact]
    public void SymmetricUpper_MultiplyMatchesGeneralStorage()
    {
        var dense = RandomSpd(12, seed: 42);
        var full = ToSparse(dense);
        var upper = ToSymmetricSparse(dense);
        Assert.True(upper.IsSymmetricUpper);
        Assert.True(upper.NonZeroCount < full.NonZeroCount);

        var rng = new Random(7);
        var x = new double[12];
        for (int i = 0; i < x.Length; i++)
            x[i] = rng.NextDouble();

        var yFull = full.Multiply(x);
        var yUpper = upper.Multiply(x);
        for (int i = 0; i < x.Length; i++)
            Assert.Equal(yFull[i], yUpper[i], 12);
    }

    [Fact]
    public void SymmetricUpper_IndexerAnswersLowerTriangleBySymmetry()
    {
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 1, 2.5);
        builder.Add(0, 0, 1.0);
        var m = builder.ToSymmetricUpper();
        Assert.Equal(2.5, m[0, 1]);
        Assert.Equal(2.5, m[1, 0]);
    }

    [Fact]
    public void SymmetricUpper_RejectsLowerTriangleEntries()
    {
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(2, 0, 1.0);
        Assert.Throws<InvalidOperationException>(() => builder.ToSymmetricUpper());
    }

    [Fact]
    public void SparseProduct_MatchesDenseProduct()
    {
        var rng = new Random(11);
        var a = new double[5, 7];
        var b = new double[7, 4];
        for (int i = 0; i < 5; i++)
        {
            for (int j = 0; j < 7; j++)
                a[i, j] = rng.Next(4) == 0 ? 0 : rng.NextDouble() * 2 - 1;
        }
        for (int i = 0; i < 7; i++)
        {
            for (int j = 0; j < 4; j++)
                b[i, j] = rng.Next(4) == 0 ? 0 : rng.NextDouble() * 2 - 1;
        }

        var product = ToSparse(a).Multiply(ToSparse(b));
        for (int i = 0; i < 5; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                double expected = 0;
                for (int k = 0; k < 7; k++)
                    expected += a[i, k] * b[k, j];
                Assert.Equal(expected, product[i, j], 12);
            }
        }
    }

    [Fact]
    public void ToGeneral_RoundTripsSymmetricStorage()
    {
        var dense = RandomSpd(9, seed: 3);
        var upper = ToSymmetricSparse(dense);
        var general = upper.ToGeneral();
        Assert.False(general.IsSymmetricUpper);
        for (int r = 0; r < 9; r++)
        {
            for (int c = 0; c < 9; c++)
                Assert.Equal(upper[r, c], general[r, c]);
        }
        var back = general.ToSymmetricUpper();
        Assert.True(back.IsSymmetricUpper);
        Assert.Equal(upper.NonZeroCount, back.NonZeroCount);
    }

    // ---------- SparseSymmetricCG ----------

    [Fact]
    public void Cg_MatchesDenseReference_OnRandomSpd()
    {
        const int n = 40;
        var dense = RandomSpd(n, seed: 123);
        var sparse = ToSymmetricSparse(dense);
        var rng = new Random(5);
        var b = new double[n];
        for (int i = 0; i < n; i++)
            b[i] = rng.NextDouble() * 2 - 1;

        var expected = DenseSolve(dense, b);
        var x = new double[n];
        var report = SparseSymmetricCG.Solve(sparse, b, x, new CgOptions { RelativeTolerance = 1e-12 });

        Assert.True(report.Converged, report.ToString());
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 8);
    }

    [Fact]
    public void Cg_SolvesPoissonChainExactly()
    {
        // -u'' = 1 on a chain with zero ends: tridiagonal SPD with a known smooth solution.
        const int n = 100;
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            builder.Add(i, i, 2.0);
            if (i + 1 < n)
                builder.Add(i, i + 1, -1.0);
        }
        var a = builder.ToSymmetricUpper();
        var b = new double[n];
        Array.Fill(b, 1.0);

        var x = new double[n];
        var report = SparseSymmetricCG.Solve(a, b, x, new CgOptions { RelativeTolerance = 1e-12 });
        Assert.True(report.Converged);

        // Analytic: u_i = (i+1)(n-i)/2 for the discrete system with h = 1.
        for (int i = 0; i < n; i++)
            Assert.Equal((i + 1.0) * (n - i) / 2.0, x[i], 6);
    }

    [Fact]
    public void Cg_JacobiPreconditioner_HandlesWildDiagonalScaling()
    {
        // Diagonal condition number 1e12: Jacobi preconditioning makes this exactly
        // conditioned again (M⁻¹A = I), so it must converge in O(1) iterations.
        const int n = 50;
        var builder = new SparseMatrixBuilder(n, n);
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            double d = Math.Pow(10, 12.0 * i / (n - 1));
            builder.Add(i, i, d);
            b[i] = d * (i + 1); // exact solution x_i = i + 1
        }
        var a = builder.ToSymmetricUpper();

        var x = new double[n];
        var report = SparseSymmetricCG.Solve(a, b, x, new CgOptions { RelativeTolerance = 1e-14 });
        Assert.True(report.Converged);
        Assert.True(report.Iterations <= 3, $"Jacobi-preconditioned diagonal solve took {report.Iterations} iterations.");
        for (int i = 0; i < n; i++)
            Assert.Equal(i + 1.0, x[i], 8);
    }

    [Fact]
    public void Cg_GradedStiffnessChain_Converges()
    {
        // Spring chain with stiffness jumping 1 ↔ 1e8 — a condition-number torture case
        // the preconditioner does NOT trivialize (off-diagonal coupling survives).
        const int n = 64;
        var builder = new SparseMatrixBuilder(n, n);
        double K(int i) => i % 2 == 0 ? 1.0 : 1e8;
        for (int i = 0; i < n; i++)
        {
            double left = i > 0 ? K(i - 1) : K(0); // ground spring at the start
            double right = K(i);
            builder.Add(i, i, left + right);
            if (i + 1 < n)
                builder.Add(i, i + 1, -right);
        }
        var a = builder.ToSymmetricUpper();
        var b = new double[n];
        b[n - 1] = 1.0; // pull the free end

        var x = new double[n];
        var report = SparseSymmetricCG.Solve(a, b, x, new CgOptions { RelativeTolerance = 1e-10 });
        Assert.True(report.Converged, report.ToString());

        // Residual check against the operator itself.
        var r = a.Multiply(x);
        double norm = 0;
        for (int i = 0; i < n; i++)
            norm += (r[i] - b[i]) * (r[i] - b[i]);
        Assert.True(Math.Sqrt(norm) <= 1e-9, $"Residual {Math.Sqrt(norm):E3}");
    }

    [Fact]
    public void Cg_ReportsNonConvergenceHonestly()
    {
        var dense = RandomSpd(30, seed: 77);
        var sparse = ToSymmetricSparse(dense);
        var b = new double[30];
        b[0] = 1;

        var x = new double[30];
        var report = SparseSymmetricCG.Solve(sparse, b, x, new CgOptions { MaxIterations = 2, RelativeTolerance = 1e-14 });
        Assert.False(report.Converged);
        Assert.Equal(2, report.Iterations);
        Assert.True(report.ResidualNorm > 0);
    }

    [Fact]
    public void Cg_ZeroRhs_ReturnsZeroImmediately()
    {
        var a = GridLaplacianPlusIdentity(5);
        var b = new double[25];
        var x = new double[25];
        x[3] = 99; // stale guess must be overwritten
        var report = SparseSymmetricCG.Solve(a, b, x);
        Assert.True(report.Converged);
        Assert.Equal(0, report.Iterations);
        Assert.All(x, v => Assert.Equal(0.0, v));
    }

    [Fact]
    public void Cg_IsDeterministic()
    {
        var a = GridLaplacianPlusIdentity(8);
        var b = new double[64];
        var rng = new Random(9);
        for (int i = 0; i < b.Length; i++)
            b[i] = rng.NextDouble();

        var x1 = new double[64];
        var x2 = new double[64];
        SparseSymmetricCG.Solve(a, b, x1);
        SparseSymmetricCG.Solve(a, b, x2);
        for (int i = 0; i < 64; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }

    // ---------- SparseCholesky ----------

    [Fact]
    public void Cholesky_MatchesDenseReference_OnRandomSpd()
    {
        const int n = 40;
        var dense = RandomSpd(n, seed: 321);
        var sparse = ToSymmetricSparse(dense);
        var rng = new Random(6);
        var b = new double[n];
        for (int i = 0; i < n; i++)
            b[i] = rng.NextDouble() * 2 - 1;

        var expected = DenseSolve(dense, b);
        var chol = SparseCholesky.Factorize(sparse);
        var x = chol.Solve(b);
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 9);
    }

    [Fact]
    public void Cholesky_AcceptsGeneralStorage()
    {
        var dense = RandomSpd(15, seed: 8);
        var general = ToSparse(dense);
        Assert.False(general.IsSymmetricUpper);
        var chol = SparseCholesky.Factorize(general);
        var b = new double[15];
        b[7] = 1;
        var x = chol.Solve(b);
        var expected = DenseSolve(dense, b);
        for (int i = 0; i < 15; i++)
            Assert.Equal(expected[i], x[i], 9);
    }

    [Fact]
    public void Cholesky_GridLaplacian_SolveMatchesCg()
    {
        var a = GridLaplacianPlusIdentity(20); // 400 unknowns, the mesh-smoother shape
        var rng = new Random(13);
        var b = new double[400];
        for (int i = 0; i < b.Length; i++)
            b[i] = rng.NextDouble() * 2 - 1;

        var chol = SparseCholesky.Factorize(a);
        var xDirect = chol.Solve(b);

        var xCg = new double[400];
        var report = SparseSymmetricCG.Solve(a, b, xCg, new CgOptions { RelativeTolerance = 1e-13 });
        Assert.True(report.Converged);
        for (int i = 0; i < 400; i++)
            Assert.Equal(xDirect[i], xCg[i], 8);
    }

    [Fact]
    public void Cholesky_ReusesFactorizationForManyRhs()
    {
        var dense = RandomSpd(25, seed: 55);
        var chol = SparseCholesky.Factorize(ToSymmetricSparse(dense));
        for (int rhs = 0; rhs < 3; rhs++)
        {
            var b = new double[25];
            b[rhs * 7] = 1 + rhs;
            var x = chol.Solve(b);
            var expected = DenseSolve(dense, b);
            for (int i = 0; i < 25; i++)
                Assert.Equal(expected[i], x[i], 9);
        }
    }

    [Fact]
    public void Cholesky_RejectsIndefiniteMatrix_NamingTheColumn()
    {
        // [[1, 2], [2, 1]] has eigenvalues 3 and -1: not positive definite.
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 1.0);
        builder.Add(0, 1, 2.0);
        builder.Add(1, 1, 1.0);
        var a = builder.ToSymmetricUpper();
        var ex = Assert.Throws<InvalidOperationException>(() => SparseCholesky.Factorize(a));
        Assert.Contains("column 1", ex.Message);
        Assert.Contains("positive definite", ex.Message);
    }

    [Fact]
    public void Cholesky_IsDeterministic()
    {
        var a = GridLaplacianPlusIdentity(10);
        var b = new double[100];
        var rng = new Random(17);
        for (int i = 0; i < b.Length; i++)
            b[i] = rng.NextDouble();

        var x1 = SparseCholesky.Factorize(a).Solve(b);
        var x2 = SparseCholesky.Factorize(a).Solve(b);
        for (int i = 0; i < 100; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }

    [Fact]
    public void Cholesky_IllConditionedGradedChain_StaysAccurate()
    {
        // Same graded-stiffness torture as the CG test: a direct solve should nail it.
        const int n = 64;
        var builder = new SparseMatrixBuilder(n, n);
        double K(int i) => i % 2 == 0 ? 1.0 : 1e8;
        for (int i = 0; i < n; i++)
        {
            double left = i > 0 ? K(i - 1) : K(0);
            double right = K(i);
            builder.Add(i, i, left + right);
            if (i + 1 < n)
                builder.Add(i, i + 1, -right);
        }
        var a = builder.ToSymmetricUpper();
        var b = new double[n];
        b[n - 1] = 1.0;

        var x = SparseCholesky.Factorize(a).Solve(b);
        var r = a.Multiply(x);
        double norm = 0;
        for (int i = 0; i < n; i++)
            norm += (r[i] - b[i]) * (r[i] - b[i]);
        Assert.True(Math.Sqrt(norm) <= 1e-12, $"Residual {Math.Sqrt(norm):E3}");
    }
}
