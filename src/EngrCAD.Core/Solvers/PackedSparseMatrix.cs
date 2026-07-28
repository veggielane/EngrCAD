namespace EngrCAD.Core.Solvers;

/// <summary>
/// Immutable sparse matrix in compressed sparse row (CSR) form: one contiguous value
/// array, one column-index array, and a row-start offset table — the packed layout every
/// solver in this folder consumes directly (geometry3Sharp's <c>PackedSparseMatrix</c>
/// role). Built through <see cref="SparseMatrixBuilder"/>, which accepts entries in any
/// order, accumulates duplicates, and emits rows sorted by column, so construction is
/// deterministic regardless of assembly order beyond the usual floating-point summation
/// order of duplicate entries (which follows the caller's add order, itself
/// deterministic).
/// </summary>
/// <remarks>
/// <para>
/// <b>Symmetric storage</b>: a matrix built with
/// <see cref="SparseMatrixBuilder.ToSymmetricUpper"/> stores only the upper triangle
/// (column ≥ row) of a square symmetric matrix; <see cref="Multiply(ReadOnlySpan{double}, Span{double})"/>
/// mirrors each off-diagonal entry on the fly, so the product is the full symmetric
/// operator at roughly half the memory and bandwidth. <see cref="IsSymmetricUpper"/>
/// reports which form an instance is.
/// </para>
/// <para>
/// The type is deliberately dependency-free and mesh-agnostic — doubles and int indices
/// only — because it also serves future sketch-constraint solving and FEA assembly, not
/// just mesh Laplacians.
/// </para>
/// </remarks>
public sealed class PackedSparseMatrix
{
    private readonly int[] _rowStart;   // length Rows + 1
    private readonly int[] _columns;    // length NonZeroCount, sorted ascending per row
    private readonly double[] _values;  // parallel to _columns

    /// <summary>Number of rows.</summary>
    public int Rows { get; }

    /// <summary>Number of columns.</summary>
    public int Columns { get; }

    /// <summary>Number of stored entries (for symmetric-upper storage, the stored triangle only).</summary>
    public int NonZeroCount => _columns.Length;

    /// <summary>
    /// True when this instance stores only the upper triangle of a square symmetric
    /// matrix; multiplication mirrors the off-diagonal entries.
    /// </summary>
    public bool IsSymmetricUpper { get; }

    internal PackedSparseMatrix(int rows, int columns, int[] rowStart, int[] cols, double[] values, bool symmetricUpper)
    {
        Rows = rows;
        Columns = columns;
        _rowStart = rowStart;
        _columns = cols;
        _values = values;
        IsSymmetricUpper = symmetricUpper;
    }

    /// <summary>The stored entry at (row, col), or 0 when absent. For symmetric-upper storage the lower triangle is answered by symmetry.</summary>
    public double this[int row, int col]
    {
        get
        {
            if ((uint)row >= (uint)Rows)
                throw new ArgumentOutOfRangeException(nameof(row));
            if ((uint)col >= (uint)Columns)
                throw new ArgumentOutOfRangeException(nameof(col));
            if (IsSymmetricUpper && col < row)
                (row, col) = (col, row);
            int lo = _rowStart[row], hi = _rowStart[row + 1] - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = _columns[mid];
                if (c == col)
                    return _values[mid];
                if (c < col)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
            return 0.0;
        }
    }

    /// <summary>The stored column indices of one row (for symmetric-upper storage, the columns ≥ row only).</summary>
    public ReadOnlySpan<int> RowColumns(int row)
    {
        if ((uint)row >= (uint)Rows)
            throw new ArgumentOutOfRangeException(nameof(row));
        return _columns.AsSpan(_rowStart[row], _rowStart[row + 1] - _rowStart[row]);
    }

    /// <summary>The stored values of one row, parallel to <see cref="RowColumns"/>.</summary>
    public ReadOnlySpan<double> RowValues(int row)
    {
        if ((uint)row >= (uint)Rows)
            throw new ArgumentOutOfRangeException(nameof(row));
        return _values.AsSpan(_rowStart[row], _rowStart[row + 1] - _rowStart[row]);
    }

    /// <summary>
    /// Writes the main diagonal into <paramref name="diagonal"/> (missing entries are 0).
    /// The Jacobi preconditioner's input.
    /// </summary>
    public void Diagonal(Span<double> diagonal)
    {
        int n = Math.Min(Rows, Columns);
        if (diagonal.Length != n)
            throw new ArgumentException($"Diagonal span must have length {n}.", nameof(diagonal));
        for (int r = 0; r < n; r++)
            diagonal[r] = this[r, r];
    }

    /// <summary>
    /// y = A·x. Sequential and deterministic (a fixed summation order — parallel
    /// reductions would change last bits run to run, which the repo's determinism rule
    /// forbids). For symmetric-upper storage each stored off-diagonal contributes to both
    /// its row and its mirrored row.
    /// </summary>
    public void Multiply(ReadOnlySpan<double> x, Span<double> y)
    {
        if (x.Length != Columns)
            throw new ArgumentException($"x must have length {Columns}.", nameof(x));
        if (y.Length != Rows)
            throw new ArgumentException($"y must have length {Rows}.", nameof(y));

        y.Clear();
        if (!IsSymmetricUpper)
        {
            for (int r = 0; r < Rows; r++)
            {
                double sum = 0;
                int end = _rowStart[r + 1];
                for (int p = _rowStart[r]; p < end; p++)
                    sum += _values[p] * x[_columns[p]];
                y[r] = sum;
            }
            return;
        }

        for (int r = 0; r < Rows; r++)
        {
            int end = _rowStart[r + 1];
            for (int p = _rowStart[r]; p < end; p++)
            {
                int c = _columns[p];
                double v = _values[p];
                y[r] += v * x[c];
                if (c != r)
                    y[c] += v * x[r];
            }
        }
    }

    /// <summary>Convenience allocating overload of <see cref="Multiply(ReadOnlySpan{double}, Span{double})"/>.</summary>
    public double[] Multiply(ReadOnlySpan<double> x)
    {
        var y = new double[Rows];
        Multiply(x, y);
        return y;
    }

    /// <summary>
    /// This matrix with symmetric-upper storage expanded to plain CSR (both triangles
    /// stored). Returns <c>this</c> when the storage is already general — safe because
    /// the type is immutable.
    /// </summary>
    public PackedSparseMatrix ToGeneral()
    {
        if (!IsSymmetricUpper)
            return this;
        var builder = new SparseMatrixBuilder(Rows, Columns);
        for (int r = 0; r < Rows; r++)
        {
            int end = _rowStart[r + 1];
            for (int p = _rowStart[r]; p < end; p++)
            {
                int c = _columns[p];
                builder.Add(r, c, _values[p]);
                if (c != r)
                    builder.Add(c, r, _values[p]);
            }
        }
        return builder.ToMatrix();
    }

    /// <summary>
    /// The upper triangle of this (square) matrix as symmetric-upper storage. The stored
    /// upper triangle is taken as the truth — the lower triangle is <i>not</i> compared,
    /// because two halves of a numerically symmetric product (e.g. L·L for symmetric L)
    /// legitimately differ in their last bits: entry (i, j) and entry (j, i) sum the same
    /// terms in different orders.
    /// </summary>
    public PackedSparseMatrix ToSymmetricUpper()
    {
        if (IsSymmetricUpper)
            return this;
        if (Rows != Columns)
            throw new InvalidOperationException("Only a square matrix can use symmetric-upper storage.");
        var builder = new SparseMatrixBuilder(Rows, Columns);
        for (int r = 0; r < Rows; r++)
        {
            int end = _rowStart[r + 1];
            for (int p = _rowStart[r]; p < end; p++)
            {
                if (_columns[p] >= r)
                    builder.Add(r, _columns[p], _values[p]);
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>
    /// Sparse matrix product A·B (Gustavson row-merge; output rows sorted by column).
    /// Symmetric-upper operands are expanded internally. Deterministic: for fixed inputs
    /// the accumulation order per output entry is fixed by the operands' structure.
    /// </summary>
    public PackedSparseMatrix Multiply(PackedSparseMatrix other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var a = ToGeneral();
        var b = other.ToGeneral();
        if (a.Columns != b.Rows)
            throw new ArgumentException($"Inner dimensions disagree: {a.Columns} vs {b.Rows}.");

        int rows = a.Rows, cols = b.Columns;
        var rowStart = new int[rows + 1];
        var outCols = new List<int>();
        var outVals = new List<double>();

        // Dense accumulator + occupancy markers per output row (Gustavson).
        var accumulator = new double[cols];
        var marker = new int[cols];
        Array.Fill(marker, -1);
        var touched = new List<int>();

        for (int r = 0; r < rows; r++)
        {
            touched.Clear();
            int aEnd = a._rowStart[r + 1];
            for (int pa = a._rowStart[r]; pa < aEnd; pa++)
            {
                int k = a._columns[pa];
                double av = a._values[pa];
                int bEnd = b._rowStart[k + 1];
                for (int pb = b._rowStart[k]; pb < bEnd; pb++)
                {
                    int c = b._columns[pb];
                    if (marker[c] != r)
                    {
                        marker[c] = r;
                        accumulator[c] = 0;
                        touched.Add(c);
                    }
                    accumulator[c] += av * b._values[pb];
                }
            }
            touched.Sort();
            foreach (int c in touched)
            {
                outCols.Add(c);
                outVals.Add(accumulator[c]);
            }
            rowStart[r + 1] = outCols.Count;
        }

        return new PackedSparseMatrix(rows, cols, rowStart, [.. outCols], [.. outVals], symmetricUpper: false);
    }
}

/// <summary>
/// Mutable assembly stage for <see cref="PackedSparseMatrix"/>: <see cref="Add"/>
/// accumulates coefficient contributions (finite-element style — the same (row, col) may
/// be added many times), and the To* methods pack the result. Duplicate entries are
/// summed in add order after a stable per-row sort by column, so assembly is
/// deterministic for a deterministic add sequence.
/// </summary>
public sealed class SparseMatrixBuilder
{
    private readonly List<(int Col, double Value)>[] _rows;

    /// <summary>Number of rows.</summary>
    public int Rows { get; }

    /// <summary>Number of columns.</summary>
    public int Columns { get; }

    public SparseMatrixBuilder(int rows, int columns)
    {
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows));
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns));
        Rows = rows;
        Columns = columns;
        _rows = new List<(int, double)>[rows];
    }

    /// <summary>Adds <paramref name="value"/> to entry (row, col), accumulating with any earlier adds to the same entry.</summary>
    public void Add(int row, int col, double value)
    {
        if ((uint)row >= (uint)Rows)
            throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)col >= (uint)Columns)
            throw new ArgumentOutOfRangeException(nameof(col));
        (_rows[row] ??= []).Add((col, value));
    }

    /// <summary>Packs into general CSR storage.</summary>
    public PackedSparseMatrix ToMatrix() => Pack(symmetricUpper: false);

    /// <summary>
    /// Packs into symmetric-upper storage. The matrix must be square and every added
    /// entry must satisfy column ≥ row (each undirected off-diagonal pair is assembled
    /// once); a lower-triangle add throws rather than being silently mirrored, because a
    /// mirror would double-count an assembly that already followed the convention.
    /// </summary>
    public PackedSparseMatrix ToSymmetricUpper()
    {
        if (Rows != Columns)
            throw new InvalidOperationException("Only a square matrix can use symmetric-upper storage.");
        for (int r = 0; r < Rows; r++)
        {
            var row = _rows[r];
            if (row is null)
                continue;
            foreach (var (col, _) in row)
            {
                if (col < r)
                    throw new InvalidOperationException(
                        $"Symmetric-upper assembly requires column >= row; entry ({r}, {col}) is in the lower triangle.");
            }
        }
        return Pack(symmetricUpper: true);
    }

    private PackedSparseMatrix Pack(bool symmetricUpper)
    {
        var rowStart = new int[Rows + 1];
        var cols = new List<int>();
        var vals = new List<double>();
        var scratch = new List<(int Col, double Value)>();

        for (int r = 0; r < Rows; r++)
        {
            var row = _rows[r];
            if (row is not null)
            {
                scratch.Clear();
                scratch.AddRange(row);
                // Stable sort by column: duplicates stay in add order, so their sum
                // order is the caller's add order — deterministic assembly.
                StableSortByColumn(scratch);
                int i = 0;
                while (i < scratch.Count)
                {
                    int c = scratch[i].Col;
                    double sum = scratch[i].Value;
                    int j = i + 1;
                    while (j < scratch.Count && scratch[j].Col == c)
                        sum += scratch[j++].Value;
                    cols.Add(c);
                    vals.Add(sum);
                    i = j;
                }
            }
            rowStart[r + 1] = cols.Count;
        }

        return new PackedSparseMatrix(Rows, Columns, rowStart, [.. cols], [.. vals], symmetricUpper);
    }

    private static void StableSortByColumn(List<(int Col, double Value)> row)
    {
        // Insertion sort: stable, and assembly rows are short (a vertex's ring), so the
        // quadratic worst case never bites; List<T>.Sort is not stable and OrderBy
        // allocates per row.
        for (int i = 1; i < row.Count; i++)
        {
            var item = row[i];
            int j = i - 1;
            while (j >= 0 && row[j].Col > item.Col)
            {
                row[j + 1] = row[j];
                j--;
            }
            row[j + 1] = item;
        }
    }
}
