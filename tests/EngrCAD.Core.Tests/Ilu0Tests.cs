using EngrCAD.Core.Solvers;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Ilu0Tests
{
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

    /// <summary>Dense LU without pivoting (Doolittle), combined L\U in one matrix — the
    /// reference a no-fill ILU(0) must reproduce exactly.</summary>
    private static double[,] DenseLuNoPivot(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        for (int k = 0; k < n; k++)
        {
            for (int i = k + 1; i < n; i++)
            {
                double f = m[i, k] / m[k, k];
                m[i, k] = f;
                for (int j = k + 1; j < n; j++)
                    m[i, j] -= f * m[k, j];
            }
        }
        return m;
    }

    /// <summary>A non-symmetric, diagonally dominant tridiagonal — the matrix whose complete
    /// LU has NO fill, so ILU(0) is the exact LU.</summary>
    private static double[,] Tridiagonal(int n, int seed)
    {
        var rng = new Random(seed);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            double lower = i > 0 ? rng.NextDouble() * 2 - 1 : 0;
            double upper = i < n - 1 ? rng.NextDouble() * 2 - 1 : 0;
            if (i > 0)
                a[i, i - 1] = lower;
            if (i < n - 1)
                a[i, i + 1] = upper;
            a[i, i] = Math.Abs(lower) + Math.Abs(upper) + 2.0; // strictly diagonally dominant
        }
        return a;
    }

    [Fact]
    public void Ilu0_OnNoFillMatrix_ReproducesTheExactLu()
    {
        // The identity: a tridiagonal matrix's LU has no fill, so ILU(0) drops nothing and
        // equals the complete factorization entry for entry.
        const int n = 30;
        var dense = Tridiagonal(n, seed: 11);
        var ilu = Ilu0.Factorize(ToSparse(dense));
        var reference = DenseLuNoPivot(dense);

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                // Only the tridiagonal band is stored; off-band ILU entries are 0 and the
                // reference is 0 there too (no fill).
                Assert.Equal(reference[i, j], ilu[i, j], 12);
            }
        }
        Assert.Equal(3 * n - 2, ilu.FactorNonZeroCount); // exactly A's nonzeros
    }

    [Fact]
    public void Ilu0_OnNoFillMatrix_SolvesExactly()
    {
        // With no fill, ILU(0) IS a complete factorization, so applying it once solves the
        // system to round-off — the identity, exercised as a direct solve.
        const int n = 50;
        var dense = Tridiagonal(n, seed: 7);
        var a = ToSparse(dense);
        var ilu = Ilu0.Factorize(a);

        var rng = new Random(3);
        var b = new double[n];
        for (int i = 0; i < n; i++)
            b[i] = rng.NextDouble() * 2 - 1;

        var x = new double[n];
        ilu.Apply(b, x); // M⁻¹ b = A⁻¹ b exactly here

        var r = a.Multiply(x);
        double norm = 0, bnorm = 0;
        for (int i = 0; i < n; i++)
        {
            norm += (r[i] - b[i]) * (r[i] - b[i]);
            bnorm += b[i] * b[i];
        }
        Assert.True(Math.Sqrt(norm / bnorm) <= 1e-13, $"Direct-solve residual {Math.Sqrt(norm / bnorm):E3}");
    }

    [Fact]
    public void Ilu0_ExpandsSymmetricUpperStorage()
    {
        // A symmetric-upper matrix only stores half the pattern; ILU(0) must expand it, and
        // for a symmetric matrix the result is symmetric (M = L D Lᵀ).
        int n = 10;
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            builder.Add(i, i, 4.0);
            if (i + 1 < n)
                builder.Add(i, i + 1, -1.0);
        }
        var upper = builder.ToSymmetricUpper();
        var ilu = Ilu0.Factorize(upper);

        // Symmetry of the incomplete factor: U[i,j] == U[i,i] * L[j,i] for a symmetric A.
        for (int i = 0; i < n - 1; i++)
        {
            double u = ilu[i, i + 1];
            double d = ilu[i, i];
            double l = ilu[i + 1, i];
            Assert.Equal(u, d * l, 12);
        }
    }

    [Fact]
    public void Ilu0_MissingDiagonal_ThrowsNamingTheRow()
    {
        // Row 1 has no (1,1) entry — ILU(0) divides by it, so it refuses up front.
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 2.0);
        builder.Add(1, 0, 1.0);
        builder.Add(1, 2, 1.0);
        builder.Add(2, 2, 2.0);
        var a = builder.ToMatrix();
        var ex = Assert.Throws<InvalidOperationException>(() => Ilu0.Factorize(a));
        Assert.Contains("row 1", ex.Message);
    }

    [Fact]
    public void Ilu0_ZeroPivot_ThrowsNamingTheRow()
    {
        // A structural zero on the diagonal is a zero pivot: [[0,1],[1,0]].
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 0.0);
        builder.Add(0, 1, 1.0);
        builder.Add(1, 0, 1.0);
        builder.Add(1, 1, 0.0);
        var a = builder.ToMatrix();
        var ex = Assert.Throws<InvalidOperationException>(() => Ilu0.Factorize(a));
        Assert.Contains("row 0", ex.Message);
    }

    [Fact]
    public void Ilu0_IsDeterministic()
    {
        var dense = Tridiagonal(40, seed: 99);
        var a = ToSparse(dense);
        var b = new double[40];
        var rng = new Random(5);
        for (int i = 0; i < 40; i++)
            b[i] = rng.NextDouble();

        var x1 = new double[40];
        var x2 = new double[40];
        Ilu0.Factorize(a).Apply(b, x1);
        Ilu0.Factorize(a).Apply(b, x2);
        for (int i = 0; i < 40; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(x1[i]), BitConverter.DoubleToInt64Bits(x2[i]));
    }
}
