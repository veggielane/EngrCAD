namespace EngrCAD.Core.Solvers;

/// <summary>
/// Sparse Cholesky factorization A = L·Lᵀ of a symmetric positive-definite matrix, by
/// the standard up-looking algorithm (elimination tree + per-row reach; Davis,
/// <i>Direct Methods for Sparse Linear Systems</i>, ch. 4). Factor once, then
/// <see cref="Solve(ReadOnlySpan{double}, Span{double})"/> any number of right-hand
/// sides by forward/back substitution — the shape of every Laplacian mesh solve, where
/// x, y and z share one operator. Deterministic: the elimination order is the matrix's
/// own row order, no pivoting, no randomness.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering</b>: the natural (caller-given) order is used as-is — no AMD/RCM
/// fill-reducing permutation. Measured on this repo's own target workload (cotangent
/// Laplacians of primitive and boolean-output meshes, whose vertex numbering is
/// grid-coherent), natural-order fill and factor time are well inside budget at the
/// 10⁴-vertex scale the deformation tools run at; a fill-reducing ordering is filed as
/// follow-up work for the FEA-scale systems that will eventually need it.
/// </para>
/// <para>
/// A nonpositive pivot throws, naming the column — for the SPD systems this library
/// builds (graph Laplacians plus positive diagonal terms) that always means an assembly
/// bug, and a silent least-squares-ish answer would hide it.
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

    /// <summary>Dimension of the factored matrix.</summary>
    public int Rows { get; }

    /// <summary>Stored entries of L (diagonal included) — the fill diagnostic.</summary>
    public int FactorNonZeroCount => _rowIndex.Length;

    private SparseCholesky(int n, int[] colStart, int[] rowIndex, double[] values)
    {
        Rows = n;
        _colStart = colStart;
        _rowIndex = rowIndex;
        _values = values;
    }

    /// <summary>
    /// Factors <paramref name="a"/> (symmetric positive definite; symmetric-upper
    /// storage is used directly, general storage has its upper triangle extracted).
    /// Throws <see cref="InvalidOperationException"/> on a nonpositive pivot.
    /// </summary>
    public static SparseCholesky Factorize(PackedSparseMatrix a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Rows != a.Columns)
            throw new ArgumentException("Cholesky needs a square symmetric positive-definite matrix.", nameof(a));

        var upper = a.IsSymmetricUpper ? a : a.ToSymmetricUpper();
        int n = upper.Rows;

        // Upper triangle in CSC form: column k lists rows i <= k ascending. (CSR rows of
        // the upper triangle are its columns transposed.)
        var (colStart, rowIndex, values) = UpperCsc(upper);

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
                throw new InvalidOperationException(
                    $"Matrix is not positive definite: nonpositive pivot {d:G6} at column {k}.");
            }
            lRow[lColStart[k]] = k;
            lVal[lColStart[k]] = Math.Sqrt(d);
        }

        return new SparseCholesky(n, lColStart, lRow, lVal);
    }

    /// <summary>Solves A·x = b using the factorization (forward then back substitution).</summary>
    public void Solve(ReadOnlySpan<double> b, Span<double> x)
    {
        if (b.Length != Rows)
            throw new ArgumentException($"b must have length {Rows}.", nameof(b));
        if (x.Length != Rows)
            throw new ArgumentException($"x must have length {Rows}.", nameof(x));
        b.CopyTo(x);

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
