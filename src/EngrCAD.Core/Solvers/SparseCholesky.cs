namespace EngrCAD.Core.Solvers;

/// <summary>
/// Which elimination order <see cref="SparseCholesky"/> factors in. The order does not
/// change the answer mathematically, only how much fill L carries — and therefore how
/// much time and memory the factorization costs.
/// </summary>
public enum SparseOrdering
{
    /// <summary>
    /// The caller's own row order, used verbatim. The default, because it is what every
    /// existing consumer measured and because a permutation is not free: it costs a
    /// symbolic pass and it moves round-off, so results are no longer bit-identical.
    /// </summary>
    Natural,

    /// <summary>
    /// Approximate minimum degree (<see cref="AmdOrdering"/>) — the standard
    /// fill-reducing permutation. Pays for itself the moment fill dominates, which on
    /// this repo's grid Laplacians is from a few thousand unknowns upward; see the
    /// measurement table in the Core README.
    /// </summary>
    Amd,
}

/// <summary>
/// Sparse Cholesky factorization A = L·Lᵀ of a symmetric positive-definite matrix, by
/// the standard up-looking algorithm (elimination tree + per-row reach; Davis,
/// <i>Direct Methods for Sparse Linear Systems</i>, ch. 4). Factor once, then
/// <see cref="Solve(ReadOnlySpan{double}, Span{double})"/> any number of right-hand
/// sides by forward/back substitution — the shape of every Laplacian mesh solve, where
/// x, y and z share one operator. Deterministic: no pivoting, no randomness, and the
/// elimination order is a pure function of the matrix pattern.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering</b> is a choice (<see cref="SparseOrdering"/>) and it defaults to
/// <see cref="SparseOrdering.Natural"/>. That default is deliberate rather than lazy:
/// a permutation changes the summation order, so an AMD-ordered solve is NOT
/// bit-identical to a natural-ordered one, and every current consumer's committed
/// numbers were measured natural. Where fill dominates —
/// <see cref="SparseOrdering.Amd"/> — the win is large and measured (Core README).
/// </para>
/// <para>
/// A nonpositive pivot throws, naming the column — for the SPD systems this library
/// builds (graph Laplacians plus positive diagonal terms) that always means an assembly
/// bug, and a silent least-squares-ish answer would hide it. The column named is the one
/// in the FACTORED order; with a permutation in play, <see cref="Permutation"/> maps it
/// back to the caller's index.
/// </para>
/// </remarks>
public sealed class SparseCholesky
{
    // L in compressed sparse column form; within each column the diagonal entry comes
    // first (at _colStart[j]) and the off-diagonal rows follow in ascending order — a
    // property the up-looking construction yields for free and both solves rely on.
    private readonly int[] _colStart;
    private readonly int[] _rowIndex;
    private readonly double[] _values;
    // null for Natural: the identity permutation is spelled as its own absence so the
    // unpermuted solve keeps exactly the loop it always had.
    private readonly int[]? _permutation;

    /// <summary>Dimension of the factored matrix.</summary>
    public int Rows { get; }

    /// <summary>Stored entries of L (diagonal included) — the fill diagnostic.</summary>
    public int FactorNonZeroCount => _rowIndex.Length;

    /// <summary>The ordering this factorization was built with.</summary>
    public SparseOrdering Ordering { get; }

    /// <summary>
    /// The elimination order actually used: <c>Permutation[k]</c> is the caller's index
    /// that was eliminated k-th. Always a valid permutation, and the identity for
    /// <see cref="SparseOrdering.Natural"/>.
    /// </summary>
    public int[] Permutation
    {
        get
        {
            if (_permutation is not null)
                return (int[])_permutation.Clone();
            var identity = new int[Rows];
            for (int i = 0; i < Rows; i++)
                identity[i] = i;
            return identity;
        }
    }

    private SparseCholesky(
        int n, int[] colStart, int[] rowIndex, double[] values, int[]? permutation, SparseOrdering ordering)
    {
        Rows = n;
        _colStart = colStart;
        _rowIndex = rowIndex;
        _values = values;
        _permutation = permutation;
        Ordering = ordering;
    }

    /// <summary>
    /// Factors <paramref name="a"/> (symmetric positive definite; symmetric-upper
    /// storage is used directly, general storage has its upper triangle extracted) in the
    /// caller's own row order. Throws <see cref="InvalidOperationException"/> on a
    /// nonpositive pivot.
    /// </summary>
    public static SparseCholesky Factorize(PackedSparseMatrix a) => Factorize(a, SparseOrdering.Natural);

    /// <summary>
    /// Factors <paramref name="a"/> under <paramref name="ordering"/>. An AMD-ordered
    /// factorization solves the same system to the same accuracy but is NOT bit-identical
    /// to the natural one — a different elimination order is different arithmetic.
    /// </summary>
    public static SparseCholesky Factorize(PackedSparseMatrix a, SparseOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Rows != a.Columns)
            throw new ArgumentException("Cholesky needs a square symmetric positive-definite matrix.", nameof(a));

        var upper = a.IsSymmetricUpper ? a : a.ToSymmetricUpper();
        int n = upper.Rows;

        // Upper triangle in CSC form: column k lists rows i <= k ascending. (CSR rows of
        // the upper triangle are its columns transposed.)
        var (colStart, rowIndex, values) = UpperCsc(upper);

        int[]? permutation = null;
        if (ordering == SparseOrdering.Amd && n > 0)
        {
            permutation = AmdOrdering.Order(n, colStart, rowIndex);
            (colStart, rowIndex, values) = SymmetricPermute(n, colStart, rowIndex, values, permutation);
        }

        // Elimination tree (Davis 4.1) via path-compressed ancestors.
        var parent = new int[n];
        var ancestor = new int[n];
        for (int k = 0; k < n; k++)
        {
            parent[k] = -1;
            ancestor[k] = -1;
            for (int p = colStart[k]; p < colStart[k + 1]; p++)
            {
                int i = rowIndex[p];
                while (i != -1 && i < k)
                {
                    int next = ancestor[i];
                    ancestor[i] = k;
                    if (next == -1)
                        parent[i] = k;
                    i = next;
                }
            }
        }

        // Symbolic pass: column counts of L from each row's ereach.
        var counts = new int[n]; // off-diagonal entries per column of L
        var stamp = new int[n];
        Array.Fill(stamp, -1);
        var reach = new int[n];
        var pathStack = new int[n];
        for (int k = 0; k < n; k++)
        {
            int top = Ereach(k, colStart, rowIndex, parent, stamp, reach, pathStack);
            for (int t = top; t < n; t++)
                counts[reach[t]]++;
        }

        var lColStart = new int[n + 1];
        for (int j = 0; j < n; j++)
            lColStart[j + 1] = lColStart[j] + counts[j] + 1; // +1 diagonal
        var lRow = new int[lColStart[n]];
        var lVal = new double[lColStart[n]];
        var cursor = new int[n]; // next write slot per column (diagonal written on completion)
        for (int j = 0; j < n; j++)
            cursor[j] = lColStart[j] + 1;

        // Numeric up-looking pass: row k of L solves L[0..k,0..k]·y = A[0..k, k].
        Array.Fill(stamp, -1);
        var x = new double[n]; // sparse accumulator, cleared entry-by-entry
        for (int k = 0; k < n; k++)
        {
            int top = Ereach(k, colStart, rowIndex, parent, stamp, reach, pathStack);

            double d = 0;
            for (int p = colStart[k]; p < colStart[k + 1]; p++)
            {
                int i = rowIndex[p];
                if (i == k)
                    d = values[p];
                else
                    x[i] = values[p];
            }

            for (int t = top; t < n; t++)
            {
                int j = reach[t];
                double lkj = x[j] / lVal[lColStart[j]]; // divide by L[j,j]
                x[j] = 0;
                for (int p = lColStart[j] + 1; p < cursor[j]; p++)
                    x[lRow[p]] -= lVal[p] * lkj;
                d -= lkj * lkj;
                lRow[cursor[j]] = k;
                lVal[cursor[j]] = lkj;
                cursor[j]++;
            }

            if (d <= 0)
            {
                // Sign test, deliberately not a Tolerance comparison: an SPD matrix has
                // strictly positive pivots, and how close to zero a legitimate pivot may
                // come is a property of the caller's conditioning, not of geometry.
                // Named in the CALLER's indices whenever a permutation is in play, since
                // the factored column number would be meaningless to whoever assembled
                // the matrix.
                int reported = permutation is null ? k : permutation[k];
                throw new InvalidOperationException(
                    $"Matrix is not positive definite: nonpositive pivot {d:G6} at column {reported}.");
            }
            lRow[lColStart[k]] = k;
            lVal[lColStart[k]] = Math.Sqrt(d);
        }

        return new SparseCholesky(n, lColStart, lRow, lVal, permutation, ordering);
    }

    /// <summary>Solves A·x = b using the factorization (forward then back substitution).</summary>
    public void Solve(ReadOnlySpan<double> b, Span<double> x)
    {
        if (b.Length != Rows)
            throw new ArgumentException($"b must have length {Rows}.", nameof(b));
        if (x.Length != Rows)
            throw new ArgumentException($"x must have length {Rows}.", nameof(x));
        if (_permutation is not null)
        {
            // A = Pᵀ·Â·P with Â = L·Lᵀ, so the permuted solve brackets the substitutions
            // with a gather and a scatter. The scratch comes from the pool rather than a
            // field: a factorization is immutable and callers do solve x/y/z from several
            // threads, so a shared buffer would be the one piece of mutable state here.
            var rented = System.Buffers.ArrayPool<double>.Shared.Rent(Rows);
            try
            {
                var permuted = rented.AsSpan(0, Rows);
                for (int k = 0; k < Rows; k++)
                    permuted[k] = b[_permutation[k]];
                Substitute(permuted);
                for (int k = 0; k < Rows; k++)
                    x[_permutation[k]] = permuted[k];
            }
            finally
            {
                System.Buffers.ArrayPool<double>.Shared.Return(rented);
            }
            return;
        }

        b.CopyTo(x);
        Substitute(x);
    }

    /// <summary>Forward then back substitution, in place, in the factored order.</summary>
    private void Substitute(Span<double> x)
    {
        // L y = b.
        for (int j = 0; j < Rows; j++)
        {
            int start = _colStart[j];
            int end = _colStart[j + 1];
            double yj = x[j] / _values[start];
            x[j] = yj;
            for (int p = start + 1; p < end; p++)
                x[_rowIndex[p]] -= _values[p] * yj;
        }

        // Lᵀ x = y.
        for (int j = Rows - 1; j >= 0; j--)
        {
            int start = _colStart[j];
            int end = _colStart[j + 1];
            double sum = x[j];
            for (int p = start + 1; p < end; p++)
                sum -= _values[p] * x[_rowIndex[p]];
            x[j] = sum / _values[start];
        }
    }

    /// <summary>Allocating overload of <see cref="Solve(ReadOnlySpan{double}, Span{double})"/>.</summary>
    public double[] Solve(ReadOnlySpan<double> b)
    {
        var x = new double[Rows];
        Solve(b, x);
        return x;
    }

    /// <summary>
    /// Nonzero pattern of row k of L (excluding the diagonal): walks each entry of A's
    /// column k up the elimination tree until an already-visited node, then unwinds so
    /// <paramref name="reach"/>[top..n) lists the row's columns in topological order
    /// (Davis's cs_ereach). Stamps double as the visited marks, reset by value k.
    /// </summary>
    private static int Ereach(
        int k, int[] colStart, int[] rowIndex, int[] parent, int[] stamp, int[] reach, int[] pathStack)
    {
        int n = parent.Length;
        int top = n;
        stamp[k] = k;
        for (int p = colStart[k]; p < colStart[k + 1]; p++)
        {
            int i = rowIndex[p];
            if (i >= k)
                continue;
            int len = 0;
            while (stamp[i] != k)
            {
                pathStack[len++] = i;
                stamp[i] = k;
                i = parent[i];
            }
            while (len > 0)
                reach[--top] = pathStack[--len];
        }
        return top;
    }

    /// <summary>
    /// The upper triangle of P·A·Pᵀ, given A's upper triangle in CSC form and
    /// <paramref name="permutation"/> mapping new index → old index. Entry (i, j) of A
    /// lands at (min, max) of the two new indices, which is what keeps the result an
    /// upper triangle whichever way the permutation flips a pair.
    /// <para>Rows are emitted ascending per column, because the up-looking pass reads
    /// the diagonal by scanning for <c>i == k</c> and the rest of this class documents
    /// its columns as sorted — an unsorted CSC would work today and rot silently.</para>
    /// </summary>
    private static (int[] ColStart, int[] RowIndex, double[] Values) SymmetricPermute(
        int n, int[] colStart, int[] rowIndex, double[] values, int[] permutation)
    {
        var inverse = new int[n];
        for (int k = 0; k < n; k++)
            inverse[permutation[k]] = k;

        var counts = new int[n];
        for (int j = 0; j < n; j++)
        {
            int jNew = inverse[j];
            for (int p = colStart[j]; p < colStart[j + 1]; p++)
            {
                int iNew = inverse[rowIndex[p]];
                counts[Math.Max(iNew, jNew)]++;
            }
        }

        var newColStart = new int[n + 1];
        for (int c = 0; c < n; c++)
            newColStart[c + 1] = newColStart[c] + counts[c];
        var newRowIndex = new int[newColStart[n]];
        var newValues = new double[newColStart[n]];

        // Scattering column by column leaves the rows in whatever order the permutation
        // produced, so each column is sorted afterwards; columns are short (the fill
        // lives in L, not in A) and this runs once per factorization.
        var cursor = new int[n];
        newColStart.AsSpan(0, n).CopyTo(cursor);
        for (int j = 0; j < n; j++)
        {
            int jNew = inverse[j];
            for (int p = colStart[j]; p < colStart[j + 1]; p++)
            {
                int iNew = inverse[rowIndex[p]];
                int column = Math.Max(iNew, jNew);
                int slot = cursor[column]++;
                newRowIndex[slot] = Math.Min(iNew, jNew);
                newValues[slot] = values[p];
            }
        }
        for (int c = 0; c < n; c++)
        {
            int from = newColStart[c], to = newColStart[c + 1];
            newRowIndex.AsSpan(from, to - from).Sort(newValues.AsSpan(from, to - from));
        }
        return (newColStart, newRowIndex, newValues);
    }

    /// <summary>The stored upper triangle re-indexed by column (CSC), rows ascending per column.</summary>
    private static (int[] ColStart, int[] RowIndex, double[] Values) UpperCsc(PackedSparseMatrix upper)
    {
        int n = upper.Rows;
        var colCount = new int[n];
        for (int r = 0; r < n; r++)
        {
            foreach (int c in upper.RowColumns(r))
                colCount[c]++;
        }
        var colStart = new int[n + 1];
        for (int c = 0; c < n; c++)
            colStart[c + 1] = colStart[c] + colCount[c];
        var rowIndex = new int[colStart[n]];
        var values = new double[colStart[n]];
        var cursor = new int[n];
        colStart.AsSpan(0, n).CopyTo(cursor);
        // Row-major scan writes each column's rows in ascending order automatically.
        for (int r = 0; r < n; r++)
        {
            var cols = upper.RowColumns(r);
            var vals = upper.RowValues(r);
            for (int i = 0; i < cols.Length; i++)
            {
                int c = cols[i];
                rowIndex[cursor[c]] = r;
                values[cursor[c]] = vals[i];
                cursor[c]++;
            }
        }
        return (colStart, rowIndex, values);
    }
}
