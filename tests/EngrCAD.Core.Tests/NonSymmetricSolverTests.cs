using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Core.Tests;

public class NonSymmetricSolverTests
{
    private readonly ITestOutputHelper _out;

    public NonSymmetricSolverTests(ITestOutputHelper output) => _out = output;

    // ---------- helpers ----------

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

    /// <summary>Dense Gaussian elimination with partial pivoting — the independent reference.</summary>
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

    /// <summary>Non-symmetric, diagonally dominant: R + n·I with R in [-1,1]. Nonsingular and
    /// friendly to unpreconditioned iteration.</summary>
    private static double[,] RandomNonSymmetric(int n, int seed)
    {
        var rng = new Random(seed);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                a[i, j] = rng.NextDouble() * 2 - 1;
            a[i, i] += n;
        }
        return a;
    }

    /// <summary>1D advection–diffusion −ν u'' + a u' = f, Dirichlet, on N interior nodes.
    /// With central differencing of the advection term it is non-symmetric AND, once the cell
    /// Péclet a·h/(2ν) exceeds 1, non-diagonally-dominant — the oscillatory regime CFD lives
    /// in. Upwind differencing keeps it diagonally dominant.</summary>
    private static PackedSparseMatrix ConvectionDiffusion1d(int n, double nu, double a, bool upwind)
    {
        double h = 1.0 / (n + 1);
        var builder = new SparseMatrixBuilder(n, n);
        double diff = nu / (h * h);
        for (int i = 0; i < n; i++)
        {
            double diag = 2 * diff;
            double lower = -diff;
            double upper = -diff;
            if (upwind)
            {
                // first-order upwind (a > 0): backward difference of u'.
                diag += a / h;
                lower -= a / h;
            }
            else
            {
                lower -= a / (2 * h);
                upper += a / (2 * h);
            }
            builder.Add(i, i, diag);
            if (i > 0)
                builder.Add(i, i - 1, lower);
            if (i < n - 1)
                builder.Add(i, i + 1, upper);
        }
        return builder.ToMatrix();
    }

    /// <summary>2D advection–diffusion on a g×g interior grid, central differencing — a larger,
    /// realistically non-symmetric operator for the preconditioner-acceleration tests.</summary>
    private static PackedSparseMatrix ConvectionDiffusion2d(int g, double nu, double ax, double ay)
    {
        int n = g * g;
        double h = 1.0 / (g + 1);
        double diff = nu / (h * h);
        var builder = new SparseMatrixBuilder(n, n);
        int Id(int i, int j) => i * g + j;
        for (int i = 0; i < g; i++)
        {
            for (int j = 0; j < g; j++)
            {
                int v = Id(i, j);
                builder.Add(v, v, 4 * diff);
                // x-neighbours
                if (i > 0)
                    builder.Add(v, Id(i - 1, j), -diff - ax / (2 * h));
                if (i < g - 1)
                    builder.Add(v, Id(i + 1, j), -diff + ax / (2 * h));
                // y-neighbours
                if (j > 0)
                    builder.Add(v, Id(i, j - 1), -diff - ay / (2 * h));
                if (j < g - 1)
                    builder.Add(v, Id(i, j + 1), -diff + ay / (2 * h));
            }
        }
        return builder.ToMatrix();
    }

    private static double RelResidual(PackedSparseMatrix a, ReadOnlySpan<double> x, ReadOnlySpan<double> b)
    {
        var r = a.Multiply(x);
        double num = 0, den = 0;
        for (int i = 0; i < r.Length; i++)
        {
            double d = r[i] - b[i];
            num += d * d;
            den += b[i] * b[i];
        }
        return Math.Sqrt(num / den);
    }

    private static double[] RandomVector(int n, int seed)
    {
        var rng = new Random(seed);
        var v = new double[n];
        for (int i = 0; i < n; i++)
            v[i] = rng.NextDouble() * 2 - 1;
        return v;
    }

    // ---------- GMRES against the dense reference ----------

    [Fact]
    public void Gmres_MatchesDenseReference_OnRandomNonSymmetric()
    {
        const int n = 40;
        var dense = RandomNonSymmetric(n, seed: 123);
        var sparse = ToSparse(dense);
        var b = RandomVector(n, seed: 5);
        var expected = DenseSolve(dense, b);

        var x = new double[n];
        var report = Gmres.Solve(sparse, b, x, new GmresOptions { RelativeTolerance = 1e-12 });

        Assert.True(report.Converged, report.ToString());
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 8);
    }

    [Fact]
    public void Gmres_MatchesDenseReference_OnUpwindConvectionDiffusion()
    {
        const int n = 60;
        var a = ConvectionDiffusion1d(n, nu: 0.02, a: 1.0, upwind: true);
        var dense = ToDense(a);
        var b = RandomVector(n, seed: 9);
        var expected = DenseSolve(dense, b);

        var x = new double[n];
        var report = Gmres.Solve(a, b, x, new GmresOptions { Restart = 40, RelativeTolerance = 1e-12 });
        Assert.True(report.Converged, report.ToString());
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 7);
    }

    [Fact]
    public void Gmres_MatchesDenseReference_OnHighPecletCentralDifference()
    {
        // Cell Péclet ≈ 1.2 → non-diagonally-dominant, oscillatory: the "known-hard" case.
        const int n = 50;
        var a = ConvectionDiffusion1d(n, nu: 0.01, a: 1.0, upwind: false);
        var dense = ToDense(a);
        var b = RandomVector(n, seed: 21);
        var expected = DenseSolve(dense, b);

        var ilu = Ilu0.Factorize(a);
        var x = new double[n];
        var report = Gmres.Solve(a, b, x, new GmresOptions { Restart = 50, RelativeTolerance = 1e-12 }, ilu);
        Assert.True(report.Converged, report.ToString());
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 7);
    }

    [Fact]
    public void Gmres_NoRestart_ConvergesInAtMostNIterations()
    {
        // The theorem: full GMRES (m ≥ n) reaches the exact solution in at most n steps, so
        // the residual is round-off — not merely small — at iteration n.
        const int n = 35;
        var dense = RandomNonSymmetric(n, seed: 77);
        var a = ToSparse(dense);
        var b = RandomVector(n, seed: 3);

        var x = new double[n];
        var report = Gmres.Solve(a, b, x, new GmresOptions { Restart = n, MaxIterations = n, RelativeTolerance = 1e-12 });

        Assert.True(report.Converged, report.ToString());
        Assert.True(report.Iterations <= n, $"took {report.Iterations} iterations for n = {n}");
        Assert.True(report.RelativeResidual <= 1e-10, $"residual {report.RelativeResidual:E3} is not round-off");
        _out.WriteLine($"GMRES(no restart): converged in {report.Iterations}/{n} iterations, |r|/|b| = {report.RelativeResidual:E3}");
    }

    // ---------- BiCGSTAB against the dense reference ----------

    [Fact]
    public void BiCgStab_MatchesDenseReference_OnRandomNonSymmetric()
    {
        const int n = 40;
        var dense = RandomNonSymmetric(n, seed: 456);
        var sparse = ToSparse(dense);
        var b = RandomVector(n, seed: 6);
        var expected = DenseSolve(dense, b);

        var x = new double[n];
        var report = BiCgStab.Solve(sparse, b, x, new BiCgStabOptions { RelativeTolerance = 1e-12 });

        Assert.True(report.Converged, report.ToString());
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 8);
    }

    [Fact]
    public void BiCgStab_MatchesDenseReference_OnConvectionDiffusion()
    {
        const int n = 60;
        var a = ConvectionDiffusion1d(n, nu: 0.02, a: 1.0, upwind: false);
        var dense = ToDense(a);
        var b = RandomVector(n, seed: 12);
        var expected = DenseSolve(dense, b);

        var ilu = Ilu0.Factorize(a);
        var x = new double[n];
        var report = BiCgStab.Solve(a, b, x, new BiCgStabOptions { RelativeTolerance = 1e-12 }, ilu);
        Assert.True(report.Converged, report.ToString());
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 7);
    }

    // ---------- reported residual == recomputed true residual ----------

    [Fact]
    public void Gmres_ReportedResidualEqualsTheTrueResidual()
    {
        // Right preconditioning makes the tracked residual the true one; assert the reported
        // number equals an independently recomputed ‖b − A·x‖ (the classic silent-failure guard).
        var a = ConvectionDiffusion2d(g: 7, nu: 0.05, ax: 1.0, ay: 0.5);
        int m = a.Rows;
        var b = RandomVector(m, seed: 31);
        var ilu = Ilu0.Factorize(a);

        var x = new double[m];
        var report = Gmres.Solve(a, b, x, new GmresOptions { Restart = 40, RelativeTolerance = 1e-10 }, ilu);
        Assert.True(report.Converged, report.ToString());

        double independent = RelResidual(a, x, b) * report.RhsNorm;
        Assert.Equal(independent, report.ResidualNorm, 6);
    }

    [Fact]
    public void BiCgStab_ReportedResidualEqualsTheTrueResidual()
    {
        var a = ConvectionDiffusion2d(g: 7, nu: 0.05, ax: 1.0, ay: 0.5);
        int m = a.Rows;
        var b = RandomVector(m, seed: 42);
        var ilu = Ilu0.Factorize(a);

        var x = new double[m];
        var report = BiCgStab.Solve(a, b, x, new BiCgStabOptions { RelativeTolerance = 1e-10 }, ilu);
        Assert.True(report.Converged, report.ToString());

        double independent = RelResidual(a, x, b) * report.RhsNorm;
        Assert.Equal(independent, report.ResidualNorm, 6);
    }

    // ---------- ILU acceleration (measured, asserted) ----------

    [Fact]
    public void Ilu0_StrictlyReducesGmresIterations()
    {
        var a = ConvectionDiffusion2d(g: 16, nu: 0.02, ax: 1.0, ay: 1.0);
        int n = a.Rows;
        var b = RandomVector(n, seed: 8);
        var ilu = Ilu0.Factorize(a);

        var xPlain = new double[n];
        var plain = Gmres.Solve(a, b, xPlain, new GmresOptions { Restart = 200, RelativeTolerance = 1e-8 });
        var xIlu = new double[n];
        var withIlu = Gmres.Solve(a, b, xIlu, new GmresOptions { Restart = 200, RelativeTolerance = 1e-8 }, ilu);

        Assert.True(plain.Converged && withIlu.Converged);
        _out.WriteLine($"GMRES on 2D convection-diffusion (n = {n}): {plain.Iterations} iters unpreconditioned, "
            + $"{withIlu.Iterations} with ILU(0)");
        Assert.True(withIlu.Iterations < plain.Iterations,
            $"ILU did not reduce iterations: {withIlu.Iterations} vs {plain.Iterations}");
        // Both reached the same solution.
        for (int i = 0; i < n; i++)
            Assert.Equal(xPlain[i], xIlu[i], 5);
    }

    [Fact]
    public void Ilu0_StrictlyReducesBiCgStabIterations()
    {
        var a = ConvectionDiffusion2d(g: 16, nu: 0.02, ax: 1.0, ay: 1.0);
        int n = a.Rows;
        var b = RandomVector(n, seed: 8);
        var ilu = Ilu0.Factorize(a);

        var xPlain = new double[n];
        var plain = BiCgStab.Solve(a, b, xPlain, new BiCgStabOptions { RelativeTolerance = 1e-8 });
        var xIlu = new double[n];
        var withIlu = BiCgStab.Solve(a, b, xIlu, new BiCgStabOptions { RelativeTolerance = 1e-8 }, ilu);

        Assert.True(plain.Converged && withIlu.Converged, $"{plain} / {withIlu}");
        _out.WriteLine($"BiCGSTAB on 2D convection-diffusion (n = {n}): {plain.Iterations} iters unpreconditioned, "
            + $"{withIlu.Iterations} with ILU(0)");
        Assert.True(withIlu.Iterations < plain.Iterations,
            $"ILU did not reduce iterations: {withIlu.Iterations} vs {plain.Iterations}");
    }

    [Fact]
    public void Ilu0_AsCgPreconditioner_StrictlyReducesIterations()
    {
        // ILU(0) of a symmetric matrix is symmetric (M = L D Lᵀ), so it is a valid CG
        // preconditioner. On a Dirichlet grid Laplacian (ill-conditioned, no shift) it must
        // beat unpreconditioned CG on iteration count — a measured, asserted improvement.
        int g = 24;
        var a = GridLaplacianDirichlet(g);
        int n = a.Rows;
        var b = RandomVector(n, seed: 13);
        var ilu = Ilu0.Factorize(a);

        var xPlain = new double[n];
        var plain = SparseSymmetricCG.Solve(a, b, xPlain,
            new CgOptions { UseJacobiPreconditioner = false, RelativeTolerance = 1e-10 });
        var xIlu = new double[n];
        var withIlu = SparseSymmetricCG.Solve(a, b, xIlu,
            new CgOptions { Preconditioner = ilu, RelativeTolerance = 1e-10 });

        Assert.True(plain.Converged && withIlu.Converged, $"{plain} / {withIlu}");
        _out.WriteLine($"CG on {g}×{g} Dirichlet Laplacian (n = {n}): {plain.Iterations} iters unpreconditioned, "
            + $"{withIlu.Iterations} with ILU(0)");
        Assert.True(withIlu.Iterations < plain.Iterations,
            $"ILU-PCG did not reduce iterations: {withIlu.Iterations} vs {plain.Iterations}");
        for (int i = 0; i < n; i++)
            Assert.Equal(xPlain[i], xIlu[i], 6);
    }

    // ---------- determinism ----------

    [Fact]
    public void Gmres_IsDeterministic()
    {
        var a = ConvectionDiffusion2d(g: 10, nu: 0.03, ax: 1.0, ay: 0.7);
        int n = a.Rows;
        var b = RandomVector(n, seed: 4);
        var ilu = Ilu0.Factorize(a);

        var x1 = new double[n];
        var x2 = new double[n];
        Gmres.Solve(a, b, x1, new GmresOptions { Restart = 20, RelativeTolerance = 1e-10 }, ilu);
        Gmres.Solve(a, b, x2, new GmresOptions { Restart = 20, RelativeTolerance = 1e-10 }, ilu);
        for (int i = 0; i < n; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }

    [Fact]
    public void BiCgStab_IsDeterministic()
    {
        var a = ConvectionDiffusion2d(g: 10, nu: 0.03, ax: 1.0, ay: 0.7);
        int n = a.Rows;
        var b = RandomVector(n, seed: 4);
        var ilu = Ilu0.Factorize(a);

        var x1 = new double[n];
        var x2 = new double[n];
        BiCgStab.Solve(a, b, x1, new BiCgStabOptions { RelativeTolerance = 1e-10 }, ilu);
        BiCgStab.Solve(a, b, x2, new BiCgStabOptions { RelativeTolerance = 1e-10 }, ilu);
        for (int i = 0; i < n; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }

    // ---------- honest non-convergence / no silent NaN ----------

    [Fact]
    public void Gmres_ReportsNonConvergenceHonestly_WithoutNaN()
    {
        var a = ConvectionDiffusion2d(g: 20, nu: 0.001, ax: 1.0, ay: 1.0);
        int n = a.Rows;
        var b = RandomVector(n, seed: 2);
        var x = new double[n];
        // A tiny iteration budget cannot converge this; the report must say so, finitely.
        var report = Gmres.Solve(a, b, x, new GmresOptions { Restart = 5, MaxIterations = 5, RelativeTolerance = 1e-14 });
        Assert.False(report.Converged);
        Assert.Equal(5, report.Iterations);
        Assert.True(double.IsFinite(report.ResidualNorm), "residual is not finite");
        foreach (var xi in x)
            Assert.True(double.IsFinite(xi), "solution has a NaN/Inf");
    }

    [Fact]
    public void BiCgStab_ReportsNonConvergenceHonestly_WithoutNaN()
    {
        // A skew-dominant matrix is where BiCGSTAB is most prone to breakdown; whatever it
        // does, it must return a finite report and no NaN.
        int n = 40;
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            builder.Add(i, i, 1e-3); // a tiny diagonal — nearly skew-symmetric
            if (i + 1 < n)
            {
                builder.Add(i, i + 1, 1.0);
                builder.Add(i + 1, i, -1.0);
            }
        }
        var a = builder.ToMatrix();
        var b = RandomVector(n, seed: 1);
        var x = new double[n];
        var report = BiCgStab.Solve(a, b, x, new BiCgStabOptions { MaxIterations = 50, RelativeTolerance = 1e-12 });

        Assert.True(double.IsFinite(report.ResidualNorm), $"residual is not finite: {report.ResidualNorm}");
        foreach (var xi in x)
            Assert.True(double.IsFinite(xi), "solution has a NaN/Inf");
        _out.WriteLine($"BiCGSTAB on skew-dominant: {report}");
    }

    [Fact]
    public void Solvers_ZeroRhs_ReturnZeroImmediately()
    {
        var a = ConvectionDiffusion2d(g: 6, nu: 0.05, ax: 1.0, ay: 1.0);
        int n = a.Rows;
        var b = new double[n];

        var xG = new double[n];
        xG[3] = 99;
        var rG = Gmres.Solve(a, b, xG);
        Assert.True(rG.Converged);
        Assert.Equal(0, rG.Iterations);
        Assert.All(xG, v => Assert.Equal(0.0, v));

        var xB = new double[n];
        xB[3] = 99;
        var rB = BiCgStab.Solve(a, b, xB);
        Assert.True(rB.Converged);
        Assert.Equal(0, rB.Iterations);
        Assert.All(xB, v => Assert.Equal(0.0, v));
    }

    // ---------- shared little helpers ----------

    private static double[,] ToDense(PackedSparseMatrix a)
    {
        int n = a.Rows;
        var d = new double[n, n];
        for (int r = 0; r < n; r++)
        {
            var cols = a.RowColumns(r);
            var vals = a.RowValues(r);
            for (int i = 0; i < cols.Length; i++)
                d[r, cols[i]] = vals[i];
        }
        return d;
    }

    /// <summary>Dirichlet 5-point grid Laplacian (no shift): SPD but ill-conditioned, so
    /// unpreconditioned CG takes many iterations and a preconditioner has room to help.</summary>
    private static PackedSparseMatrix GridLaplacianDirichlet(int g)
    {
        int n = g * g;
        var builder = new SparseMatrixBuilder(n, n);
        int Id(int i, int j) => i * g + j;
        for (int i = 0; i < g; i++)
        {
            for (int j = 0; j < g; j++)
            {
                int v = Id(i, j);
                builder.Add(v, v, 4.0);
                if (i + 1 < g)
                    builder.Add(v, Id(i + 1, j), -1.0);
                if (j + 1 < g)
                    builder.Add(v, Id(i, j + 1), -1.0);
            }
        }
        return builder.ToSymmetricUpper();
    }
}
