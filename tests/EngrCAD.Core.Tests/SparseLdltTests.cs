using System.Numerics;
using EngrCAD.Core.Solvers;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The symmetric-indefinite factorization — the systems <see cref="SparseCholesky"/>
/// correctly refuses. Ground truth throughout is a dense solve written independently in
/// this file (Gaussian elimination with partial pivoting, real and complex), and residuals
/// are measured against the backward-error scale ‖|A|·|x| + |b|‖∞ rather than against ‖b‖,
/// because an indefinite solve near a resonance legitimately produces ‖x‖ ≫ ‖b‖ and a
/// b-relative bound would then be either vacuous or unfair depending on the fixture.
/// </summary>
public class SparseLdltTests
{
    // ---------- helpers: fixtures ----------

    /// <summary>Deterministic dense symmetric matrix with entries in [-1, 1] and a shifted
    /// diagonal chosen to make it INDEFINITE (verified per test via Cholesky's refusal).</summary>
    private static double[,] RandomSymmetricIndefinite(int n, int seed)
    {
        var rng = new Random(seed);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                double v = rng.NextDouble() * 2 - 1;
                a[i, j] = v;
                a[j, i] = v;
            }
            // Alternate strong positive and negative diagonals: eigenvalues of both signs.
            a[i, i] = (i % 2 == 0 ? 4.0 : -4.0) + a[i, i];
        }
        return a;
    }

    /// <summary>
    /// The 1D bar pair the harmonic solve actually produces, hand-assembled: fixed-free
    /// bar, K = (EA/h)·tridiag(-1, 2, -1), consistent M = (ρAh/6)·tridiag(1, 4, 1), in
    /// ModelUnits' mm/N/MPa/tonne/s (steel: E = 210000 MPa, ρ = 7.85e-9 tonne/mm³,
    /// A = 100 mm², L = 100 mm over <paramref name="elements"/> elements).
    /// </summary>
    private static (double[,] K, double[,] M) BarKm(int elements)
    {
        const double youngsModulus = 210000.0;
        const double density = 7.85e-9;
        const double area = 100.0;
        const double length = 100.0;
        double h = length / elements;
        double kCoeff = youngsModulus * area / h;
        double mCoeff = density * area * h / 6.0;

        int n = elements; // node 0 is fixed and eliminated; unknowns are nodes 1..elements
        var k = new double[n, n];
        var m = new double[n, n];
        for (int e = 0; e < elements; e++)
        {
            // Element e spans nodes e..e+1; unknown indices are node-1.
            int i = e - 1, j = e;
            if (i >= 0)
            {
                k[i, i] += kCoeff;
                m[i, i] += 2 * mCoeff;
                k[i, j] -= kCoeff;
                k[j, i] -= kCoeff;
                m[i, j] += mCoeff;
                m[j, i] += mCoeff;
            }
            k[j, j] += kCoeff;
            m[j, j] += 2 * mCoeff;
        }
        // The free end's mass diagonal is 2·mCoeff from its single element — already right.
        return (k, m);
    }

    private static double[,] Combine(double[,] a, double coeffA, double[,] b, double coeffB)
    {
        int n = a.GetLength(0);
        var r = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                r[i, j] = coeffA * a[i, j] + coeffB * b[i, j];
        return r;
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

    private static double[] Rhs(int n)
    {
        var b = new double[n];
        for (int i = 0; i < n; i++)
            b[i] = 1.0 + (i % 5) * 0.5 - (i % 3) * 0.75;
        return b;
    }

    // ---------- helpers: independent dense ground truth ----------

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
                if (f == 0)
                    continue;
                for (int c = col; c < n; c++)
                    m[r, c] -= f * m[col, c];
                x[r] -= f * x[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = x[r];
            for (int c = r + 1; c < n; c++)
                sum -= m[r, c] * x[c];
            x[r] = sum / m[r, r];
        }
        return x;
    }

    /// <summary>Dense COMPLEX Gaussian elimination with partial pivoting (by magnitude).</summary>
    private static Complex[] DenseSolve(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
        var m = (Complex[,])a.Clone();
        var x = (Complex[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (m[r, col].Magnitude > m[pivot, col].Magnitude)
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
                Complex f = m[r, col] / m[col, col];
                if (f == Complex.Zero)
                    continue;
                for (int c = col; c < n; c++)
                    m[r, c] -= f * m[col, c];
                x[r] -= f * x[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            Complex sum = x[r];
            for (int c = r + 1; c < n; c++)
                sum -= m[r, c] * x[c];
            x[r] = sum / m[r, r];
        }
        return x;
    }

    /// <summary>max_i |A·x − b|_i / max_i (|A|·|x| + |b|)_i — the backward-error residual.</summary>
    private static double BackwardResidual(double[,] a, double[] x, double[] b)
    {
        int n = b.Length;
        double worstResidual = 0, scale = 0;
        for (int i = 0; i < n; i++)
        {
            double sum = 0, abs = Math.Abs(b[i]);
            for (int j = 0; j < n; j++)
            {
                sum += a[i, j] * x[j];
                abs += Math.Abs(a[i, j]) * Math.Abs(x[j]);
            }
            worstResidual = Math.Max(worstResidual, Math.Abs(sum - b[i]));
            scale = Math.Max(scale, abs);
        }
        return worstResidual / scale;
    }

    private static double BackwardResidual(Complex[,] a, Complex[] x, Complex[] b)
    {
        int n = b.Length;
        double worstResidual = 0, scale = 0;
        for (int i = 0; i < n; i++)
        {
            Complex sum = Complex.Zero;
            double abs = b[i].Magnitude;
            for (int j = 0; j < n; j++)
            {
                sum += a[i, j] * x[j];
                abs += a[i, j].Magnitude * x[j].Magnitude;
            }
            worstResidual = Math.Max(worstResidual, (sum - b[i]).Magnitude);
            scale = Math.Max(scale, abs);
        }
        return worstResidual / scale;
    }

    // ---------- real indefinite ----------

    [Theory]
    [InlineData(12, 3)]
    [InlineData(40, 17)]
    public void RealIndefinite_MatchesDenseGaussianElimination(int n, int seed)
    {
        var dense = RandomSymmetricIndefinite(n, seed);
        var sparse = ToSymmetricSparse(dense);
        // The fixture must actually be indefinite, or this test measures nothing new.
        Assert.Throws<InvalidOperationException>(() => SparseCholesky.Factorize(sparse));

        var b = Rhs(n);
        var factor = SparseLdlt.Factorize(sparse);
        var x = factor.Solve(b);

        var reference = DenseSolve(dense, b);
        for (int i = 0; i < n; i++)
            Assert.Equal(reference[i], x[i], 8);
        Assert.True(BackwardResidual(dense, x, b) < 1e-13);
        Assert.False(factor.IsComplex);
        Assert.True(factor.SmallestPivotMagnitude > 0);
        Assert.True(factor.LargestPivotMagnitude >= factor.SmallestPivotMagnitude);
    }

    // ---------- the saddle system ----------

    /// <summary>
    /// minimize ½‖x‖² − fᵀx subject to Σx = c: KKT matrix [[I, 1],[1ᵀ, 0]], analytic
    /// solution x = f − λ·1 with λ = (Σf − c)/n. The constraints-LAST ordering is what
    /// makes the unpivoted factorization exist (the Schur complement onto the multiplier
    /// is −n &lt; 0), and Cholesky's refusal of the same matrix is asserted because this
    /// class exists for exactly the systems it refuses.
    /// </summary>
    [Fact]
    public void SaddleSystem_MatchesTheAnalyticLagrangeSolution()
    {
        const int n = 9;
        const double c = 4.0;
        var f = new double[n];
        for (int i = 0; i < n; i++)
            f[i] = Math.Sin(i + 1.0);

        var builder = new SparseMatrixBuilder(n + 1, n + 1);
        for (int i = 0; i < n; i++)
        {
            builder.Add(i, i, 1.0);
            builder.Add(i, n, 1.0);
        }
        var kkt = builder.ToSymmetricUpper();
        Assert.Throws<InvalidOperationException>(() => SparseCholesky.Factorize(kkt));

        var rhs = new double[n + 1];
        f.CopyTo(rhs, 0);
        rhs[n] = c;

        var factor = SparseLdlt.Factorize(kkt);
        var solution = factor.Solve(rhs);

        double lambda = (f.Sum() - c) / n;
        for (int i = 0; i < n; i++)
            Assert.Equal(f[i] - lambda, solution[i], 12);
        Assert.Equal(lambda, solution[n], 12);
    }

    // ---------- the shifted (harmonic) system ----------

    /// <summary>
    /// K − ω²M above the bar's first resonance: indefinite by construction (asserted via
    /// Cholesky's refusal), which is the undamped harmonic solve's matrix.
    /// </summary>
    [Fact]
    public void ShiftedBar_IsRefusedByCholeskyAndSolvedHere()
    {
        var (k, m) = BarKm(elements: 24);
        int n = k.GetLength(0);
        // Fixed-free bar: ω₁ = (π/2)·√(E/ρ)/L ≈ 8.12e4 rad/s. 1.5× that sits between the
        // first and second resonances, so K − ω²M has exactly one negative eigenvalue.
        double omega = 1.5 * (Math.PI / 2) * Math.Sqrt(210000.0 / 7.85e-9) / 100.0;
        var shifted = Combine(k, 1.0, m, -omega * omega);
        var sparse = ToSymmetricSparse(shifted);
        Assert.Throws<InvalidOperationException>(() => SparseCholesky.Factorize(sparse));

        var b = Rhs(n);
        var factor = SparseLdlt.Factorize(sparse);
        var x = factor.Solve(b);

        Assert.True(BackwardResidual(shifted, x, b) < 1e-13);
        var reference = DenseSolve(shifted, b);
        for (int i = 0; i < n; i++)
            Assert.Equal(reference[i], x[i], 6);
    }

    /// <summary>AMD reorders the elimination but must reach the same solution; the
    /// shifted-Helmholtz family has a structurally full diagonal, so it is the family AMD
    /// is safe on (a saddle system's zero diagonal is the documented hazard).</summary>
    [Fact]
    public void Amd_SolvesTheShiftedSystemToTheSameAccuracy()
    {
        var (k, m) = BarKm(elements: 24);
        int n = k.GetLength(0);
        double omega = 1.5 * (Math.PI / 2) * Math.Sqrt(210000.0 / 7.85e-9) / 100.0;
        var shifted = Combine(k, 1.0, m, -omega * omega);
        var sparse = ToSymmetricSparse(shifted);
        var b = Rhs(n);

        var natural = SparseLdlt.Factorize(sparse).Solve(b);
        var amdFactor = SparseLdlt.Factorize(sparse, SparseOrdering.Amd);
        var amd = amdFactor.Solve(b);

        Assert.True(BackwardResidual(shifted, amd, b) < 1e-13);
        for (int i = 0; i < n; i++)
            Assert.Equal(natural[i], amd[i], 6);

        var permutation = amdFactor.Permutation;
        Assert.Equal(n, permutation.Length);
        Assert.Equal(Enumerable.Range(0, n), permutation.OrderBy(p => p));
    }

    // ---------- the damped (complex) system ----------

    /// <summary>
    /// The system the direct harmonic solve exists for: Z = (K − ω²M) + iωC with Rayleigh
    /// C = αM + βK, verified against an independent dense complex solve. ωC is positive
    /// definite here, so per the class remarks the factorization provably cannot break
    /// down at any ω — including exactly AT a resonance, which is where this fixture sits.
    /// </summary>
    [Fact]
    public void DampedHarmonic_MatchesADenseComplexSolve()
    {
        var (k, m) = BarKm(elements: 24);
        int n = k.GetLength(0);
        // Deliberately AT the first resonance to within the fixture's own discretization:
        // the real part alone is nearly singular, which is the hostile regime.
        double omega = (Math.PI / 2) * Math.Sqrt(210000.0 / 7.85e-9) / 100.0;
        const double alpha = 5.0;      // 1/s — mass-proportional damping
        const double beta = 2e-7;      // s — stiffness-proportional damping
        var real = Combine(k, 1.0, m, -omega * omega);
        var imag = Combine(m, alpha * omega, k, beta * omega);

        var bRe = Rhs(n);
        var bIm = new double[n]; // a real load

        var factor = SparseLdlt.Factorize(ToSymmetricSparse(real), ToSymmetricSparse(imag));
        Assert.True(factor.IsComplex);
        var xRe = new double[n];
        var xIm = new double[n];
        factor.Solve(bRe, bIm, xRe, xIm);

        var z = new Complex[n, n];
        var b = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = bRe[i];
            for (int j = 0; j < n; j++)
                z[i, j] = new Complex(real[i, j], imag[i, j]);
        }
        var reference = DenseSolve(z, b);
        var x = new Complex[n];
        for (int i = 0; i < n; i++)
            x[i] = new Complex(xRe[i], xIm[i]);

        Assert.True(BackwardResidual(z, x, b) < 1e-13);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(reference[i].Real, xRe[i], 6);
            Assert.Equal(reference[i].Imaginary, xIm[i], 6);
        }
    }

    /// <summary>
    /// Non-proportional damping is the case the direct solve is FOR, and its C has a
    /// pattern of its own — one dashpot touches two DOFs. The factored pattern is the
    /// union of the parts' patterns, asserted here by giving C entries K − ω²M has and
    /// vice versa cannot cover.
    /// </summary>
    [Fact]
    public void NonProportionalDamping_PartsWithDifferentPatterns_Solve()
    {
        var (k, m) = BarKm(elements: 16);
        int n = k.GetLength(0);
        double omega = 1.2 * (Math.PI / 2) * Math.Sqrt(210000.0 / 7.85e-9) / 100.0;
        var real = Combine(k, 1.0, m, -omega * omega);

        // A single dashpot between DOFs 3 and 11 — far outside the tridiagonal pattern.
        const double dashpot = 0.02; // N·s/mm
        var damping = new double[n, n];
        damping[3, 3] += dashpot;
        damping[11, 11] += dashpot;
        damping[3, 11] -= dashpot;
        damping[11, 3] -= dashpot;
        var imag = Combine(damping, omega, damping, 0);

        var bRe = Rhs(n);
        var bIm = new double[n];
        var factor = SparseLdlt.Factorize(ToSymmetricSparse(real), ToSymmetricSparse(imag));
        var xRe = new double[n];
        var xIm = new double[n];
        factor.Solve(bRe, bIm, xRe, xIm);

        var z = new Complex[n, n];
        var b = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = bRe[i];
            for (int j = 0; j < n; j++)
                z[i, j] = new Complex(real[i, j], imag[i, j]);
        }
        var x = new Complex[n];
        for (int i = 0; i < n; i++)
            x[i] = new Complex(xRe[i], xIm[i]);
        Assert.True(BackwardResidual(z, x, b) < 1e-13);

        var reference = DenseSolve(z, b);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(reference[i].Real, xRe[i], 6);
            Assert.Equal(reference[i].Imaginary, xIm[i], 6);
        }
    }

    /// <summary>
    /// The structural-pivot claim, in the smallest matrix that can carry it: R = [[0, 1],
    /// [1, 0]] has a singular leading minor, so the REAL factorization refuses — and the
    /// complex one over the same R with S = I factors, because a complex pivot r + is only
    /// vanishes when both parts do. This is the robustness a Bunch–Kaufman 2×2 pivot buys
    /// on the real form, obtained from structure rather than from a magnitude search.
    /// </summary>
    [Fact]
    public void ComplexPivot_SucceedsWhereTheRealPartAloneBreaksDown()
    {
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 1, 1.0);
        var r = builder.ToSymmetricUpper();

        var ex = Assert.Throws<InvalidOperationException>(() => SparseLdlt.Factorize(r));
        Assert.Contains("column 0", ex.Message);

        var identity = new SparseMatrixBuilder(2, 2);
        identity.Add(0, 0, 1.0);
        identity.Add(1, 1, 1.0);
        var s = identity.ToSymmetricUpper();

        var factor = SparseLdlt.Factorize(r, s);
        // Z = [[i, 1],[1, i]]: det = −2, Z⁻¹ = [[i, −1],[−1, i]]/(−2), so Z·x = (1, 0)
        // has the closed-form solution x = (−i/2, 1/2).
        var xRe = new double[2];
        var xIm = new double[2];
        factor.Solve([1.0, 0.0], [0.0, 0.0], xRe, xIm);
        Assert.Equal(0.0, xRe[0], 14);
        Assert.Equal(-0.5, xIm[0], 14);
        Assert.Equal(0.5, xRe[1], 14);
        Assert.Equal(0.0, xIm[1], 14);
    }

    // ---------- agreement, determinism, refusals ----------

    [Fact]
    public void SpdInput_AgreesWithSparseCholesky()
    {
        var a = AmdOrderingTests.GridLaplacian2d(14);
        var b = Rhs(a.Rows);

        var cholesky = SparseCholesky.Factorize(a).Solve(b);
        var ldlt = SparseLdlt.Factorize(a).Solve(b);
        for (int i = 0; i < b.Length; i++)
            Assert.Equal(cholesky[i], ldlt[i], 10);
    }

    [Fact]
    public void Determinism_RepeatFactorizationsAreBitIdentical()
    {
        var (k, m) = BarKm(elements: 20);
        double omega = 1.5 * (Math.PI / 2) * Math.Sqrt(210000.0 / 7.85e-9) / 100.0;
        var shifted = ToSymmetricSparse(Combine(k, 1.0, m, -omega * omega));
        var b = Rhs(k.GetLength(0));

        var x1 = SparseLdlt.Factorize(shifted).Solve(b);
        var x2 = SparseLdlt.Factorize(shifted).Solve(b);
        for (int i = 0; i < b.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }

    [Fact]
    public void ZeroPivot_RefusesNamingTheCallerColumn()
    {
        // Constraints FIRST: the multiplier's structurally zero diagonal is eliminated
        // before anything can fill it — the ordering hazard the docs name.
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 1, 1.0);
        builder.Add(0, 2, 1.0);
        builder.Add(1, 1, 1.0);
        builder.Add(2, 2, 1.0);
        var kkt = builder.ToSymmetricUpper();

        var ex = Assert.Throws<InvalidOperationException>(() => SparseLdlt.Factorize(kkt));
        Assert.Contains("column 0", ex.Message);
    }

    [Fact]
    public void SolveOverloads_RefuseTheWrongFactorizationKind()
    {
        var a = AmdOrderingTests.GridLaplacian2d(4);
        int n = a.Rows;
        var real = SparseLdlt.Factorize(a);
        Assert.Throws<InvalidOperationException>(
            () => real.Solve(new double[n], new double[n], new double[n], new double[n]));

        var complexFactor = SparseLdlt.Factorize(a, a);
        Assert.Throws<InvalidOperationException>(() => complexFactor.Solve(new double[n]));
    }

    [Fact]
    public void MismatchedImaginaryDimension_Refuses()
    {
        var a = AmdOrderingTests.GridLaplacian2d(4);
        var b = AmdOrderingTests.GridLaplacian2d(5);
        Assert.Throws<ArgumentException>(() => SparseLdlt.Factorize(a, b));
    }

    // ---------- the ProgressCancel conventions ----------

    [Fact]
    public void ProgressDoesNotChangeTheFactorization()
    {
        var (k, m) = BarKm(elements: 20);
        double omega = 1.5 * (Math.PI / 2) * Math.Sqrt(210000.0 / 7.85e-9) / 100.0;
        var shifted = ToSymmetricSparse(Combine(k, 1.0, m, -omega * omega));
        var b = Rhs(k.GetLength(0));

        var plain = SparseLdlt.Factorize(shifted).Solve(b);
        var watched = SparseLdlt.Factorize(shifted, SparseOrdering.Natural, new ProgressCancel(_ => { })).Solve(b);
        for (int i = 0; i < b.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(plain[i]), BitConverter.DoubleToInt64Bits(watched[i]));
    }

    [Fact]
    public void Cancellation_AbortsTheFactorization()
    {
        var a = AmdOrderingTests.GridLaplacian2d(24);
        int polls = 0;
        var progress = new ProgressCancel(() => ++polls > a.Rows + 8);
        Assert.Throws<OperationCanceledException>(
            () => SparseLdlt.Factorize(a, SparseOrdering.Natural, progress));
    }

    [Fact]
    public void Progress_ReportsAMonotoneFractionEndingAtExactlyOne()
    {
        var a = AmdOrderingTests.GridLaplacian2d(20);
        var seen = new List<double>();
        SparseLdlt.Factorize(a, a, SparseOrdering.Natural, new ProgressCancel(seen.Add));

        Assert.NotEmpty(seen);
        for (int i = 1; i < seen.Count; i++)
            Assert.True(seen[i] >= seen[i - 1]);
        Assert.All(seen, f => Assert.InRange(f, 0.0, 1.0));
        Assert.Equal(1.0, seen[^1]);
    }
}
