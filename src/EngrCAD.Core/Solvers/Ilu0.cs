namespace EngrCAD.Core.Solvers;

/// <summary>
/// ILU(0) — incomplete LU factorization with ZERO fill: A ≈ L·U where L is unit lower
/// triangular, U upper triangular, and <b>both have exactly A's own sparsity pattern</b>.
/// The workhorse preconditioner for the non-symmetric sparse systems the symmetric solvers
/// cannot touch (advection–diffusion, the momentum equations of incompressible flow), and
/// the "ILU at minimum" a Krylov method needs to converge on them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The algorithm is Gaussian elimination that never leaves the pattern.</b> The complete
/// LU of a sparse matrix fills in — an eliminated pivot creates a nonzero at every (i, j)
/// where row i and column k both reach — and it is that fill a fill-reducing ordering
/// exists to limit. ILU(0) simply <em>drops</em> every fill: the update
/// <c>a[i,j] -= (a[i,k]/a[k,k])·a[k,j]</c> is applied only where (i, j) is already a stored
/// entry of A, and discarded otherwise. So L and U cost exactly A's memory, the
/// factorization is O(nnz·bandwidth) rather than O(fill), and M = L·U is an approximation
/// whose error is precisely the dropped fill.
/// </para>
/// <para>
/// <b>Why there is no ordering parameter, deliberately.</b> AMD reduces the FILL of a
/// complete factorization, and ILU(0) has no fill to reduce — that is its definition. A
/// permutation would change only <em>which entries get dropped</em>, i.e. the
/// preconditioner's accuracy, which is a different question from fill and wants a different
/// ordering (RCM for bandwidth, or a multicolour ordering for parallelism), and one that
/// only earns its keep once fill is admitted (ILU(p &gt; 0), ILUT). Reusing
/// <see cref="AmdOrdering"/> here would spend a symbolic pass to move round-off around for
/// no fill saving and would break the "no fill ⇒ ILU(0) IS the exact LU" identity, so it is
/// left out until the tier that would use it exists. The factorization stays in the caller's
/// own order and is therefore deterministic and bit-reproducible.
/// </para>
/// <para>
/// <b>For a symmetric matrix with a symmetric pattern (every SPD system this repo assembles)
/// ILU(0) is symmetric</b>, because the dropped fill is symmetric too: the value it computes
/// for U[k,j] equals U[k,k]·L[j,k], so M = L·U = L·D·Lᵀ. That makes it a legitimate
/// preconditioner for conjugate gradients (see <see cref="CgOptions.Preconditioner"/>) — the
/// incomplete-Cholesky factor under another name — rather than the spectrum-splitting
/// heuristic it is for a genuinely non-symmetric system.
/// </para>
/// <para>
/// <b>Pivots.</b> The factorization needs a structural diagonal entry in every row (it
/// divides by U[k,k]); a missing one throws, naming the row. A zero pivot PRODUCED by the
/// incomplete elimination also throws — the factorization is all-or-nothing, like
/// <see cref="SparseCholesky"/> and <see cref="SparseLdlt"/>, never a half-built factor — and
/// the guard is an exact-zero test (deliberately not a <c>Tolerance</c> comparison: how small
/// a legitimate pivot may be is the caller's conditioning, and a positive diagonal shift is
/// the standard fix when a pattern genuinely does not admit an ILU(0)).
/// </para>
/// </remarks>
public sealed class Ilu0 : IPreconditioner
{
    // A's own CSR pattern, columns ascending per row. _values holds, after factorization,
    // L's strict-lower entries (the multipliers, at columns < i) and U's diagonal+upper
    // entries (at columns >= i) sharing one array — the classic combined storage, unit
    // diagonal of L implicit.
    private readonly int[] _rowStart;
    private readonly int[] _columns;
    private readonly double[] _values;
    private readonly int[] _diag; // _diag[i] = index in _columns/_values of the (i, i) entry

    /// <summary>Dimension of the factored matrix.</summary>
    public int Rows { get; }

    /// <summary>Stored entries of L and U together — exactly A's nonzero count (zero fill).</summary>
    public int FactorNonZeroCount => _values.Length;

    private Ilu0(int rows, int[] rowStart, int[] columns, double[] values, int[] diag)
    {
        Rows = rows;
        _rowStart = rowStart;
        _columns = columns;
        _values = values;
        _diag = diag;
    }

    /// <summary>
    /// Factors <paramref name="a"/> into ILU(0). A symmetric-upper matrix is expanded to its
    /// full pattern first (ILU needs both triangles); a general matrix is used as given.
    /// Throws <see cref="InvalidOperationException"/> on a missing or zero diagonal pivot,
    /// naming the row.
    /// </summary>
    public static Ilu0 Factorize(PackedSparseMatrix a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Rows != a.Columns)
            throw new ArgumentException("ILU(0) needs a square matrix.", nameof(a));

        // ILU works on the full pattern; ToGeneral() is a no-op for a matrix already general
        // and expands a symmetric-upper one. Columns come out ascending per row either way,
        // which the elimination below relies on.
        var general = a.ToGeneral();
        int n = general.Rows;

        var rowStart = new int[n + 1];
        for (int r = 0; r < n; r++)
            rowStart[r + 1] = rowStart[r] + general.RowColumns(r).Length;
        int nnz = rowStart[n];
        var columns = new int[nnz];
        var values = new double[nnz];
        for (int r = 0; r < n; r++)
        {
            general.RowColumns(r).CopyTo(columns.AsSpan(rowStart[r]));
            general.RowValues(r).CopyTo(values.AsSpan(rowStart[r]));
        }

        var diag = new int[n];
        for (int i = 0; i < n; i++)
        {
            int d = -1;
            for (int p = rowStart[i]; p < rowStart[i + 1]; p++)
            {
                if (columns[p] == i)
                {
                    d = p;
                    break;
                }
            }
            if (d < 0)
                throw new InvalidOperationException(
                    $"ILU(0) needs a structural diagonal entry, but row {i} has none. Assemble a "
                    + "(possibly zero) diagonal, or add a small diagonal shift before factoring.");
            diag[i] = d;
        }

        // IKJ incomplete Gaussian elimination. A scatter array (fill[col] = the row's slot for
        // that column, else -1) turns "is (i, j) in the pattern?" — the drop test — into an
        // O(1) lookup, so the whole factorization is one pass over each row's entries.
        var fill = new int[n];
        Array.Fill(fill, -1);
        for (int i = 0; i < n; i++)
        {
            int rowBegin = rowStart[i], rowEnd = rowStart[i + 1];
            for (int p = rowBegin; p < rowEnd; p++)
                fill[columns[p]] = p;

            for (int p = rowBegin; p < rowEnd; p++)
            {
                int k = columns[p];
                if (k >= i)
                    break; // columns ascending: the diagonal and the upper part remain
                // L[i,k] = a[i,k] / U[k,k]. Row k finished earlier (k < i), so its pivot is
                // already the nonzero its own diagonal guard verified.
                double lik = values[p] / values[diag[k]];
                values[p] = lik;
                // Row i -= L[i,k] · (row k's upper part), dropping anything not in A's pattern.
                for (int q = diag[k] + 1; q < rowStart[k + 1]; q++)
                {
                    int slot = fill[columns[q]];
                    if (slot >= 0)
                        values[slot] -= lik * values[q];
                }
            }

            if (values[diag[i]] == 0.0)
                throw new InvalidOperationException(
                    $"ILU(0) produced a zero pivot at row {i}: A's pattern does not admit an ILU(0) "
                    + "(the incomplete factor is singular there). A positive diagonal shift is the usual fix.");

            for (int p = rowBegin; p < rowEnd; p++)
                fill[columns[p]] = -1;
        }

        return new Ilu0(n, rowStart, columns, values, diag);
    }

    /// <summary>
    /// Writes <c>z = M⁻¹·r = U⁻¹·L⁻¹·r</c> by forward substitution against unit-lower L then
    /// back substitution against U. In-place-safe internally; <paramref name="r"/> and
    /// <paramref name="z"/> must be distinct buffers of length <see cref="Rows"/>.
    /// </summary>
    public void Apply(ReadOnlySpan<double> r, Span<double> z)
    {
        if (r.Length != Rows)
            throw new ArgumentException($"r must have length {Rows}.", nameof(r));
        if (z.Length != Rows)
            throw new ArgumentException($"z must have length {Rows}.", nameof(z));

        r.CopyTo(z);

        // L y = r, unit diagonal: strict-lower entries are the slots before the diagonal.
        for (int i = 0; i < Rows; i++)
        {
            double sum = z[i];
            for (int p = _rowStart[i]; p < _diag[i]; p++)
                sum -= _values[p] * z[_columns[p]];
            z[i] = sum;
        }

        // U x = y: strict-upper entries are the slots after the diagonal, divide by U[i,i].
        for (int i = Rows - 1; i >= 0; i--)
        {
            double sum = z[i];
            for (int p = _diag[i] + 1; p < _rowStart[i + 1]; p++)
                sum -= _values[p] * z[_columns[p]];
            z[i] = sum / _values[_diag[i]];
        }
    }

    /// <summary>
    /// The stored factor entry L/U at (row, col), or 0 when absent — the diagnostic that
    /// lets a test compare ILU(0) against a complete LU on a matrix with no fill (where the
    /// two are identical). Below the diagonal it is L (unit diagonal implicit), on and above
    /// it is U.
    /// </summary>
    public double this[int row, int col]
    {
        get
        {
            if ((uint)row >= (uint)Rows)
                throw new ArgumentOutOfRangeException(nameof(row));
            if ((uint)col >= (uint)Rows)
                throw new ArgumentOutOfRangeException(nameof(col));
            for (int p = _rowStart[row]; p < _rowStart[row + 1]; p++)
            {
                if (_columns[p] == col)
                    return _values[p];
            }
            return 0.0;
        }
    }
}
