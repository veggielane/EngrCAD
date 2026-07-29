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
/// What <see cref="SparseCholesky.Analyze"/> reads off the symbolic pass: what a
/// factorization will cost, before any of it is paid.
/// </summary>
/// <param name="Rows">Dimension of the matrix.</param>
/// <param name="FactorNonZeroCount">Stored entries L will have, diagonal included — exactly
/// what <see cref="SparseCholesky.FactorNonZeroCount"/> would report. The memory
/// question.</param>
/// <param name="UpdateCount">The numeric pass's inner-loop trip count, which its time is
/// proportional to. A <c>double</c> because a large factorization overflows an int (a
/// 19-million-entry factor's runs to 1e10).</param>
/// <param name="CriticalPathUpdates">The heaviest root-to-leaf path of the elimination
/// tree, in the same units. A column cannot start before its children finish, so this is a
/// LOWER BOUND on any parallel schedule — <c>UpdateCount / CriticalPathUpdates</c> is the
/// ceiling on what tree parallelism could ever buy, whatever the machine.</param>
/// <param name="LongestColumn">Off-diagonal entries in the largest column of L. The
/// supernode question: blocked (BLAS-3) factorizations pay in proportion to how much work
/// sits in long columns.</param>
/// <param name="Ordering">The ordering these numbers describe.</param>
public readonly record struct SparseFactorAnalysis(
    int Rows,
    long FactorNonZeroCount,
    double UpdateCount,
    double CriticalPathUpdates,
    int LongestColumn,
    SparseOrdering Ordering)
{
    /// <summary>Stored entries of L over stored entries of A's upper triangle — the fill
    /// ratio, reported by a factorization after the fact and by this before it.</summary>
    public double FillRatio(PackedSparseMatrix a) =>
        (double)FactorNonZeroCount / Math.Max(1, a.IsSymmetricUpper ? a.NonZeroCount : a.ToSymmetricUpper().NonZeroCount);

    /// <summary>
    /// <see cref="UpdateCount"/> over <see cref="CriticalPathUpdates"/>: the most a
    /// perfectly scheduled parallel factorization could win over this sequential one. An
    /// UPPER bound and nothing more — it assumes unlimited processors and free
    /// synchronisation, so a real scheduler lands well below it.
    /// </summary>
    public double ParallelCeiling =>
        CriticalPathUpdates > 0 ? UpdateCount / CriticalPathUpdates : 1;

    /// <summary>A readable summary.</summary>
    public override string ToString() =>
        $"{Rows:N0} unknowns, factor {FactorNonZeroCount:N0} nnz, {UpdateCount:E2} updates, "
        + $"longest column {LongestColumn:N0}, parallel ceiling {ParallelCeiling:F1}x ({Ordering})";
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
/// <para>
/// <b>Cancellable.</b> <see cref="Factorize(PackedSparseMatrix, SparseOrdering, ProgressCancel?)"/>
/// takes Core's optional trailing <see cref="ProgressCancel"/> and polls it per eliminated
/// column, which matters because this is the slowest single operation in the library — an
/// FEA stiffness matrix at 46 800 unknowns takes over a minute and a half here, against a
/// third of a second to assemble. A null argument costs one null check per column.
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
    /// <param name="a">The matrix to factor.</param>
    /// <param name="ordering">The elimination order.</param>
    /// <param name="progress">
    /// Optional cooperative cancellation and progress. Polled once per eliminated column of
    /// the numeric pass — the finest granularity available without restructuring the inner
    /// loops, so the worst-case latency between a cancel request and the
    /// <see cref="OperationCanceledException"/> is one column's own elimination.
    /// <para>The reported fraction is the numeric pass's <b>inner-loop update count</b>, not
    /// the column number, and it is exact rather than an estimate: the symbolic pass has
    /// already counted every column of L, and column j is used by exactly as many rows as it
    /// has off-diagonal entries, accumulating one more entry each time — so the total is
    /// <c>sum over j of c_j·(c_j + 1)/2</c>, known before the first multiply.
    /// <b>Column number is a misleading progress bar wherever the factor FILLS</b>: the
    /// arithmetic a column costs grows with how many entries already stand in the columns it
    /// updates, so a dense factor has spent only 12.5% of its work at the halfway column and
    /// a bar driven by column number would be reading 50%. (Where the factor is merely
    /// BANDED — a natural-ordered 2D grid Laplacian, the shape this class was first written
    /// for — the two agree closely: 0.468 at halfway, measured. The re-weighting costs
    /// nothing there and is what makes the number honest on the systems worth
    /// cancelling.)</para>
    /// <para>Progress covers the numeric pass only, and the doc says so rather than
    /// inventing a weighting for the phases in front of it: the ordering, the elimination
    /// tree and the symbolic pass are linear in the matrix's own entries and are together a
    /// small fraction of a factorization that is worth cancelling. They still poll for
    /// cancellation, at their phase boundaries and per symbolic column.</para>
    /// </param>
    /// <exception cref="OperationCanceledException">Cancellation was requested. No partial
    /// factorization is returned — the kernel convention that a cancelled operation produces
    /// nothing rather than something half-built.</exception>
    public static SparseCholesky Factorize(
        PackedSparseMatrix a, SparseOrdering ordering, ProgressCancel? progress = null)
    {
        var (n, colStart, rowIndex, values, permutation, parent, counts, stamp, reach, pathStack) =
            Symbolic(a, ordering, progress);

        // The numeric pass's exact inner-loop update count, available here because the
        // symbolic pass has just counted every column of L. Accumulated in double because a
        // large factorization's update count overflows int (a 19-million-entry factor's runs
        // to 1e10).
        double totalUpdates = 0;
        if (progress is not null)
        {
            foreach (int c in counts)
                totalUpdates += 0.5 * c * (c + 1);
        }
        double doneUpdates = 0;
        double nextReport = 0;

        var lColStart = new int[n + 1];
        for (int j = 0; j < n; j++)
            lColStart[j + 1] = lColStart[j] + counts[j] + 1; // +1 diagonal
        var lRow = new int[lColStart[n]];
        var lVal = new double[lColStart[n]];
        var cursor = new int[n]; // next write slot per column (diagonal written on completion)
        for (int j = 0; j < n; j++)
            cursor[j] = lColStart[j] + 1;

        // Numeric up-looking pass: row k of L solves L[0..k,0..k]·y = A[0..k, k].
        // The stamps double as ereach's visited marks and are reset by value k, so the
        // symbolic pass's leftovers have to be cleared before the numeric one reuses them.
        Array.Fill(stamp, -1);
        var x = new double[n]; // sparse accumulator, cleared entry-by-entry
        for (int k = 0; k < n; k++)
        {
            if (progress is not null)
            {
                progress.ThrowIfCancelled();
                // Reported on WORK crossing a threshold rather than every column: a
                // factorization has as many columns as unknowns, and a callback that
                // repaints a UI 46 800 times would cost more than the arithmetic it reports
                // on. Two hundred steps is finer than any progress bar can show.
                if (doneUpdates >= nextReport && totalUpdates > 0)
                {
                    progress.Report(doneUpdates / totalUpdates);
                    nextReport = doneUpdates + totalUpdates / 200;
                }
            }

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
                // Counted before the cursor moves: cursor[j] - lColStart[j] is the number of
                // entries this use touches (the inner loop's trip count, plus the write that
                // follows it), which is exactly the term the pre-computed total sums.
                // Guarded rather than unconditional because this is the innermost loop in
                // the library and a caller who passed no progress must pay nothing for one.
                if (progress is not null)
                    doneUpdates += cursor[j] - lColStart[j];
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

        progress?.Report(1);
        return new SparseCholesky(n, lColStart, lRow, lVal, permutation, ordering);
    }

    /// <summary>
    /// Everything a factorization knows before it does any arithmetic: the ordering, the
    /// elimination tree and the column counts of L. Shared by
    /// <see cref="Factorize(PackedSparseMatrix, SparseOrdering, ProgressCancel?)"/> and
    /// <see cref="Analyze"/> so the prediction and the run cannot disagree about what the
    /// factorization is — the "ask the same code rather than restating it" rule, which this
    /// project has paid for three times elsewhere.
    /// </summary>
    private static (
        int N, int[] ColStart, int[] RowIndex, double[] Values, int[]? Permutation,
        int[] Parent, int[] Counts, int[] Stamp, int[] Reach, int[] PathStack)
        Symbolic(PackedSparseMatrix a, SparseOrdering ordering, ProgressCancel? progress)
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
            // One checkpoint per phase for the linear passes, per the polling rule: their
            // cost follows the matrix's own entry count, so a checkpoint inside would buy
            // latency nobody can measure.
            progress?.ThrowIfCancelled();
            permutation = AmdOrdering.Order(n, colStart, rowIndex);
            (colStart, rowIndex, values) = SymmetricPermute(n, colStart, rowIndex, values, permutation);
        }
        progress?.ThrowIfCancelled();

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
            progress?.ThrowIfCancelled();
            int top = Ereach(k, colStart, rowIndex, parent, stamp, reach, pathStack);
            for (int t = top; t < n; t++)
                counts[reach[t]]++;
        }

        return (n, colStart, rowIndex, values, permutation, parent, counts, stamp, reach, pathStack);
    }

    /// <summary>
    /// What a factorization WILL cost, from the symbolic pass alone — no arithmetic, no L,
    /// and typically a small fraction of a second where the factorization itself may be
    /// minutes.
    ///
    /// <para>It exists because the two numbers that decide whether a direct solve is the
    /// right choice are both known before the first multiply, and both are otherwise only
    /// learnable by paying: <see cref="SparseFactorAnalysis.FactorNonZeroCount"/> says
    /// whether L fits, and <see cref="SparseFactorAnalysis.UpdateCount"/> is the exact
    /// inner-loop trip count the numeric pass will run, which is what its time is
    /// proportional to. Compare two orderings, or two meshes, without factoring either.</para>
    ///
    /// <para>It is a PREDICTION of this implementation, not a general flop count: the
    /// numbers come from the same symbolic pass the factorization runs, so they describe
    /// the up-looking algorithm as written.</para>
    /// </summary>
    public static SparseFactorAnalysis Analyze(
        PackedSparseMatrix a, SparseOrdering ordering = SparseOrdering.Natural,
        ProgressCancel? progress = null)
    {
        var (n, _, _, _, _, parent, counts, _, _, _) = Symbolic(a, ordering, progress);

        double totalUpdates = 0;
        long nonZeros = n;   // the diagonal
        int longest = 0;
        // The elimination tree is the dependency graph of a column factorization: column j
        // cannot start until its children are done. The heaviest root-to-leaf path is
        // therefore a LOWER BOUND on any parallel schedule's work, whatever the machine —
        // which is the number that says whether writing a parallel scheduler is worth it.
        // Parents outrank children, so one ascending sweep suffices.
        var below = new double[n];
        double critical = 0;
        for (int j = 0; j < n; j++)
        {
            double work = 0.5 * counts[j] * (counts[j] + 1);
            totalUpdates += work;
            nonZeros += counts[j];
            longest = Math.Max(longest, counts[j]);

            double level = below[j] + work;
            critical = Math.Max(critical, level);
            if (parent[j] >= 0)
                below[parent[j]] = Math.Max(below[parent[j]], level);
        }

        return new SparseFactorAnalysis(n, nonZeros, totalUpdates, critical, longest, ordering);
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
