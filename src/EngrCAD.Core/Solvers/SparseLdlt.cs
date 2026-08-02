namespace EngrCAD.Core.Solvers;

/// <summary>
/// Sparse symmetric-indefinite factorization A = L·D·Lᵀ (L unit lower triangular, D
/// diagonal), real or complex symmetric, by the same up-looking elimination
/// (tree + per-row ereach) as <see cref="SparseCholesky"/> — which correctly REFUSES an
/// indefinite matrix, and this class is the tool for exactly the systems it refuses.
/// The consumer it was built for is the direct per-frequency harmonic solve:
/// <c>(K − ω²M + iωC)·u = f</c>, whose matrix is complex SYMMETRIC (not Hermitian) and
/// whose equivalent real form is symmetric indefinite by construction, its eigenvalues
/// coming in ±pairs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which of the three candidates this is, and why.</b> The backlog named three ways to
/// serve that system: a real Bunch–Kaufman LDLᵀ with 2×2 pivots on the 2n×2n real form, a
/// complex symmetric LDLᵀ, or an iterative method (COCG/QMR). This is the second, with a
/// real overload riding the same machinery, and the reasoning is recorded because it was
/// the deliverable:
/// <list type="bullet">
/// <item><b>COCG/QMR is rejected</b> because the item asks for a DIRECT solve: a shifted
/// system near resonance is precisely where a Krylov method's convergence becomes
/// unpredictable, and re-importing that unpredictability is what the direct solve exists
/// to remove.</item>
/// <item><b>Real Bunch–Kaufman is rejected FOR NOW, on structure rather than on taste.</b>
/// A magnitude-searched 2×2 pivot merges two columns' patterns, so the symbolic pass no
/// longer predicts the numeric structure and the AMD ordering's counts go stale — which is
/// why production sparse indefinite solvers (MA57, PARDISO) are multifrontal machines with
/// delayed pivots, a project of a different order. It stays filed for genuinely real
/// indefinite systems that need pivoting; nothing in the repo produces one today (a saddle
/// system factors unpivoted with constraints ordered last — see below).</item>
/// <item><b>The complex spelling wins because for THIS family the "2×2 pivots" are fixed by
/// structure, never searched.</b> A complex pivot r + is plays exactly the role a
/// Bunch–Kaufman 2×2 block plays on the real form's paired ±structure — it is invertible
/// whenever (r, s) ≠ (0, 0), where the real form's leading block is K − ω²M alone and an
/// unpivoted real factorization of it breaks down near every resonance, the very regime a
/// harmonic sweep operates in. Because the pivots are structurally 1×1 (in the complex
/// field), the elimination structure IS the Cholesky one on the union pattern of the two
/// parts: <b>the symbolic pass is shared verbatim</b> (<see cref="SparseCholesky"/>'s
/// internals, not a copy) <b>and <see cref="SparseOrdering.Amd"/> applies unchanged</b> —
/// the two properties a searched pivot would destroy.</item>
/// </list>
/// </para>
/// <para>
/// <b>When the factorization provably exists.</b> Write the complex matrix Z = R + iS with
/// R, S real symmetric. If Z's leading principal submatrix Z_k is singular, there is a
/// complex x = u + iv ≠ 0 with R_k·u = S_k·v and S_k·u = −R_k·v, from which
/// uᵀS_k·u + vᵀS_k·v = 0 — so for S positive SEMI-definite both terms vanish, S_k·u =
/// S_k·v = 0, and then R_k·u = R_k·v = 0: breakdown requires a vector annihilated by BOTH
/// parts. With any positive-definite damping present (Rayleigh damping is, whenever either
/// coefficient is nonzero) that is impossible and every pivot is nonzero; with damping only
/// somewhere, it requires an entirely undamped subsystem exactly at one of its own
/// resonances — where the physical steady state is unbounded too, so the loud refusal is
/// the right answer rather than a limitation.
/// </para>
/// <para>
/// <b>The real overload is unpivoted, and the caveat is stated rather than hidden.</b> A
/// real symmetric indefinite matrix factors with 1×1 pivots iff every leading principal
/// minor is nonsingular. That holds for the systems this library sees — a shifted
/// <c>K − ω²M</c> away from the measure-zero set of ω where a minor crosses zero, and a
/// saddle system <c>[[A, B],[Bᵀ, 0]]</c> with the constraints ordered LAST (the Schur
/// complement −BᵀA⁻¹B is negative definite) — but a NEAR-singular minor still amplifies
/// round-off with nothing here to repair it. The refusal is an exactly-zero pivot (an
/// exact-zero division guard, deliberately not a Tolerance comparison: how small a
/// legitimate pivot may be is the caller's conditioning), and
/// <see cref="SmallestPivotMagnitude"/>/<see cref="LargestPivotMagnitude"/> report the
/// spread so a caller can judge what it factored. Note the AMD hazard that follows:
/// AMD reads the PATTERN and not the values, so it can reorder a saddle system's
/// structurally-zero diagonal ahead of its constraints and turn a factorable matrix into
/// an exact-zero pivot. Natural stays the default; AMD is the right choice for the
/// shifted-Helmholtz family, whose diagonal is structurally full.
/// </para>
/// <para>
/// <b>Conventions carried over from <see cref="SparseCholesky"/></b>: deterministic (no
/// pivot search, no randomness — two factorizations of the same matrix are bit-identical);
/// factor once, solve any number of right-hand sides; cancellable via the optional
/// trailing <see cref="ProgressCancel"/>, polled per eliminated column, with the reported
/// fraction the numeric pass's exact inner-loop update count; all-or-nothing on
/// cancellation. <see cref="SparseCholesky.Analyze"/> predicts this factorization's
/// structure and cost too — it runs the same symbolic pass — when handed the (union)
/// pattern.
/// </para>
/// </remarks>
public sealed class SparseLdlt
{
    // L in compressed sparse column form, unit diagonal implicit: within each column the
    // slot at _colStart[j] holds D_j (the pivot) and the off-diagonal rows follow in
    // ascending order — the layout SparseCholesky uses, with D where sqrt(D) was.
    private readonly int[] _colStart;
    private readonly int[] _rowIndex;
    private readonly double[] _re;
    private readonly double[]? _im; // null for a real factorization
    private readonly int[]? _permutation;

    /// <summary>Dimension of the factored matrix.</summary>
    public int Rows { get; }

    /// <summary>Stored entries of L and D (one diagonal slot per column) — the fill diagnostic.</summary>
    public int FactorNonZeroCount => _rowIndex.Length;

    /// <summary>The ordering this factorization was built with.</summary>
    public SparseOrdering Ordering { get; }

    /// <summary>True for a complex symmetric factorization, false for a real one.</summary>
    public bool IsComplex => _im is not null;

    /// <summary>
    /// The smallest pivot magnitude |D_k| the factorization met. A conditioning report,
    /// not a guarantee: an unpivoted factorization's accuracy degrades as this approaches
    /// round-off relative to <see cref="LargestPivotMagnitude"/>, and the honest check on
    /// a suspect solve is the caller's own residual.
    /// </summary>
    public double SmallestPivotMagnitude { get; }

    /// <summary>The largest pivot magnitude |D_k| — <see cref="SmallestPivotMagnitude"/>'s partner.</summary>
    public double LargestPivotMagnitude { get; }

    /// <summary>
    /// The elimination order actually used: <c>Permutation[k]</c> is the caller's index
    /// eliminated k-th. The identity for <see cref="SparseOrdering.Natural"/>.
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

    private SparseLdlt(
        int n, int[] colStart, int[] rowIndex, double[] re, double[]? im, int[]? permutation,
        SparseOrdering ordering, double minPivot, double maxPivot)
    {
        Rows = n;
        _colStart = colStart;
        _rowIndex = rowIndex;
        _re = re;
        _im = im;
        _permutation = permutation;
        Ordering = ordering;
        SmallestPivotMagnitude = minPivot;
        LargestPivotMagnitude = maxPivot;
    }

    // ---- factorization ----

    /// <summary>
    /// Factors a real symmetric matrix — indefinite welcome, which is the point
    /// (symmetric-upper storage is used directly, general storage has its upper triangle
    /// extracted). Unpivoted: throws <see cref="InvalidOperationException"/> on an exactly
    /// zero pivot, naming the column in the caller's indices.
    /// </summary>
    public static SparseLdlt Factorize(
        PackedSparseMatrix a, SparseOrdering ordering = SparseOrdering.Natural,
        ProgressCancel? progress = null)
    {
        var (n, colStart, rowIndex, values, permutation, parent, counts, stamp, reach, pathStack) =
            SparseCholesky.Symbolic(a, ordering, progress);
        return FactorizeReal(
            n, colStart, rowIndex, values, permutation, parent, counts, stamp, reach, pathStack,
            ordering, progress);
    }

    /// <summary>
    /// Factors a complex symmetric matrix given as its real and imaginary parts (each real
    /// symmetric; symmetric-upper storage used directly). This is the harmonic solve's
    /// shape: real = K − ω²M, imaginary = ωC. The factored pattern is the UNION of the two
    /// parts' patterns, so the parts need not have entries in the same places. Throws
    /// <see cref="InvalidOperationException"/> on an exactly zero complex pivot — see the
    /// class remarks for when that can and cannot happen.
    /// </summary>
    public static SparseLdlt Factorize(
        PackedSparseMatrix real, PackedSparseMatrix imaginary,
        SparseOrdering ordering = SparseOrdering.Natural, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(imaginary);
        if (real.Rows != real.Columns)
            throw new ArgumentException("LDLT needs a square symmetric matrix.", nameof(real));
        if (imaginary.Rows != real.Rows || imaginary.Columns != real.Columns)
            throw new ArgumentException(
                $"The imaginary part must match the real part's dimension {real.Rows}.", nameof(imaginary));

        var (unionRe, unionIm) = UnionUpper(real, imaginary);

        var (n, colStart, rowIndex, reValues, permutation, parent, counts, stamp, reach, pathStack) =
            SparseCholesky.Symbolic(unionRe, ordering, progress);

        // The imaginary values through the SAME permutation over the SAME union pattern:
        // SymmetricPermute is a pure function of (pattern, values, permutation) and column
        // rows are distinct, so its per-column sort applies the identical slot order and
        // the two value arrays stay aligned.
        double[] imValues;
        var (imColStart, imRowIndex, imUnpermuted) = SparseCholesky.UpperCsc(unionIm);
        if (permutation is not null)
            (_, _, imValues) = SparseCholesky.SymmetricPermute(n, imColStart, imRowIndex, imUnpermuted, permutation);
        else
            imValues = imUnpermuted;

        return FactorizeComplex(
            n, colStart, rowIndex, reValues, imValues, permutation, parent, counts, stamp, reach, pathStack,
            ordering, progress);
    }

    private static SparseLdlt FactorizeReal(
        int n, int[] colStart, int[] rowIndex, double[] values, int[]? permutation,
        int[] parent, int[] counts, int[] stamp, int[] reach, int[] pathStack,
        SparseOrdering ordering, ProgressCancel? progress)
    {
        // Progress bookkeeping mirrors SparseCholesky's exactly: the inner-loop update
        // count is known from the symbolic column counts before the first multiply.
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
            lColStart[j + 1] = lColStart[j] + counts[j] + 1; // +1 for the D slot
        var lRow = new int[lColStart[n]];
        var lVal = new double[lColStart[n]];
        var cursor = new int[n];
        for (int j = 0; j < n; j++)
            cursor[j] = lColStart[j] + 1;

        Array.Fill(stamp, -1);
        var x = new double[n];
        double minPivot = double.PositiveInfinity, maxPivot = 0;
        for (int k = 0; k < n; k++)
        {
            if (progress is not null)
            {
                progress.ThrowIfCancelled();
                if (doneUpdates >= nextReport && totalUpdates > 0)
                {
                    progress.Report(doneUpdates / totalUpdates);
                    nextReport = doneUpdates + totalUpdates / 200;
                }
            }

            int top = SparseCholesky.Ereach(k, colStart, rowIndex, parent, stamp, reach, pathStack);

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
                double yj = x[j];
                x[j] = 0;
                double lkj = yj / lVal[lColStart[j]]; // divide by D_j
                if (progress is not null)
                    doneUpdates += cursor[j] - lColStart[j];
                for (int p = lColStart[j] + 1; p < cursor[j]; p++)
                    x[lRow[p]] -= lVal[p] * yj;
                d -= lkj * yj;
                lRow[cursor[j]] = k;
                lVal[cursor[j]] = lkj;
                cursor[j]++;
            }

            if (d == 0.0)
            {
                // Exact-zero division guard (deliberately not a Tolerance comparison):
                // indefinite pivots are the point here, so only a pivot that cannot be
                // divided by refuses. Named in the caller's indices under a permutation.
                int reported = permutation is null ? k : permutation[k];
                throw new InvalidOperationException(
                    $"LDLT hit an exactly zero pivot at column {reported}: the leading principal minor is "
                    + "singular. Reorder (a saddle system's constraints must come after its primal unknowns), "
                    + "or perturb the shift if this is a resonant frequency.");
            }
            double magnitude = Math.Abs(d);
            if (magnitude < minPivot) minPivot = magnitude;
            if (magnitude > maxPivot) maxPivot = magnitude;
            lRow[lColStart[k]] = k;
            lVal[lColStart[k]] = d;
        }

        progress?.Report(1);
        return new SparseLdlt(n, lColStart, lRow, lVal, null, permutation, ordering,
            n == 0 ? 0 : minPivot, maxPivot);
    }

    private static SparseLdlt FactorizeComplex(
        int n, int[] colStart, int[] rowIndex, double[] reValues, double[] imValues, int[]? permutation,
        int[] parent, int[] counts, int[] stamp, int[] reach, int[] pathStack,
        SparseOrdering ordering, ProgressCancel? progress)
    {
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
            lColStart[j + 1] = lColStart[j] + counts[j] + 1;
        var lRow = new int[lColStart[n]];
        var lRe = new double[lColStart[n]];
        var lIm = new double[lColStart[n]];
        var cursor = new int[n];
        for (int j = 0; j < n; j++)
            cursor[j] = lColStart[j] + 1;

        Array.Fill(stamp, -1);
        var xr = new double[n];
        var xi = new double[n];
        double minPivot = double.PositiveInfinity, maxPivot = 0;
        for (int k = 0; k < n; k++)
        {
            if (progress is not null)
            {
                progress.ThrowIfCancelled();
                if (doneUpdates >= nextReport && totalUpdates > 0)
                {
                    progress.Report(doneUpdates / totalUpdates);
                    nextReport = doneUpdates + totalUpdates / 200;
                }
            }

            int top = SparseCholesky.Ereach(k, colStart, rowIndex, parent, stamp, reach, pathStack);

            double dr = 0, di = 0;
            for (int p = colStart[k]; p < colStart[k + 1]; p++)
            {
                int i = rowIndex[p];
                if (i == k)
                {
                    dr = reValues[p];
                    di = imValues[p];
                }
                else
                {
                    xr[i] = reValues[p];
                    xi[i] = imValues[p];
                }
            }

            for (int t = top; t < n; t++)
            {
                int j = reach[t];
                double yr = xr[j], yi = xi[j];
                xr[j] = 0;
                xi[j] = 0;
                int diag = lColStart[j];
                ComplexDivide(yr, yi, lRe[diag], lIm[diag], out double lr, out double li);
                if (progress is not null)
                    doneUpdates += cursor[j] - diag;
                for (int p = diag + 1; p < cursor[j]; p++)
                {
                    int i = lRow[p];
                    // x_i -= L(i, j) · y — complex symmetric, so a plain (unconjugated)
                    // complex product.
                    xr[i] -= lRe[p] * yr - lIm[p] * yi;
                    xi[i] -= lRe[p] * yi + lIm[p] * yr;
                }
                dr -= lr * yr - li * yi;
                di -= lr * yi + li * yr;
                lRow[cursor[j]] = k;
                lRe[cursor[j]] = lr;
                lIm[cursor[j]] = li;
                cursor[j]++;
            }

            if (dr == 0.0 && di == 0.0)
            {
                int reported = permutation is null ? k : permutation[k];
                throw new InvalidOperationException(
                    $"Complex LDLT hit an exactly zero pivot at column {reported}: an undamped subsystem sits "
                    + "exactly at a resonance of a leading principal minor (see the class remarks — with "
                    + "positive-definite damping this cannot happen). Perturb the frequency, or damp the model.");
            }
            double magnitude = ComplexMagnitude(dr, di);
            if (magnitude < minPivot) minPivot = magnitude;
            if (magnitude > maxPivot) maxPivot = magnitude;
            lRow[lColStart[k]] = k;
            lRe[lColStart[k]] = dr;
            lIm[lColStart[k]] = di;
        }

        progress?.Report(1);
        return new SparseLdlt(n, lColStart, lRow, lRe, lIm, permutation, ordering,
            n == 0 ? 0 : minPivot, maxPivot);
    }

    // ---- solves ----

    /// <summary>
    /// Solves A·x = b for a REAL factorization (forward substitution against unit L, a
    /// diagonal divide by D, back substitution against Lᵀ). Throws on a complex
    /// factorization — a complex system's right-hand side has two parts, and pretending
    /// otherwise would silently drop one.
    /// </summary>
    public void Solve(ReadOnlySpan<double> b, Span<double> x)
    {
        if (IsComplex)
            throw new InvalidOperationException(
                "This is a complex factorization; use the (bRe, bIm, xRe, xIm) overload.");
        if (b.Length != Rows)
            throw new ArgumentException($"b must have length {Rows}.", nameof(b));
        if (x.Length != Rows)
            throw new ArgumentException($"x must have length {Rows}.", nameof(x));

        if (_permutation is not null)
        {
            var rented = System.Buffers.ArrayPool<double>.Shared.Rent(Rows);
            try
            {
                var permuted = rented.AsSpan(0, Rows);
                for (int k = 0; k < Rows; k++)
                    permuted[k] = b[_permutation[k]];
                SubstituteReal(permuted);
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
        SubstituteReal(x);
    }

    /// <summary>Allocating overload of <see cref="Solve(ReadOnlySpan{double}, Span{double})"/>.</summary>
    public double[] Solve(ReadOnlySpan<double> b)
    {
        var x = new double[Rows];
        Solve(b, x);
        return x;
    }

    /// <summary>
    /// Solves Z·x = b for a COMPLEX factorization, right-hand side and solution as
    /// separate real/imaginary spans (the layout the harmonic assembly already holds —
    /// a real load is bIm all zeros). Throws on a real factorization: a real A against a
    /// complex b is two independent real solves, and the caller should say so.
    /// </summary>
    public void Solve(
        ReadOnlySpan<double> bRe, ReadOnlySpan<double> bIm, Span<double> xRe, Span<double> xIm)
    {
        if (!IsComplex)
            throw new InvalidOperationException(
                "This is a real factorization; solve the real and imaginary right-hand sides separately.");
        if (bRe.Length != Rows || bIm.Length != Rows)
            throw new ArgumentException($"b must have length {Rows}.");
        if (xRe.Length != Rows || xIm.Length != Rows)
            throw new ArgumentException($"x must have length {Rows}.");

        if (_permutation is not null)
        {
            var rented = System.Buffers.ArrayPool<double>.Shared.Rent(2 * Rows);
            try
            {
                var pr = rented.AsSpan(0, Rows);
                var pi = rented.AsSpan(Rows, Rows);
                for (int k = 0; k < Rows; k++)
                {
                    pr[k] = bRe[_permutation[k]];
                    pi[k] = bIm[_permutation[k]];
                }
                SubstituteComplex(pr, pi);
                for (int k = 0; k < Rows; k++)
                {
                    xRe[_permutation[k]] = pr[k];
                    xIm[_permutation[k]] = pi[k];
                }
            }
            finally
            {
                System.Buffers.ArrayPool<double>.Shared.Return(rented);
            }
            return;
        }

        bRe.CopyTo(xRe);
        bIm.CopyTo(xIm);
        SubstituteComplex(xRe, xIm);
    }

    private void SubstituteReal(Span<double> x)
    {
        // L y = b (unit diagonal: no divide).
        for (int j = 0; j < Rows; j++)
        {
            int start = _colStart[j];
            int end = _colStart[j + 1];
            double yj = x[j];
            for (int p = start + 1; p < end; p++)
                x[_rowIndex[p]] -= _re[p] * yj;
        }

        // D z = y.
        for (int j = 0; j < Rows; j++)
            x[j] /= _re[_colStart[j]];

        // Lᵀ x = z (unit diagonal).
        for (int j = Rows - 1; j >= 0; j--)
        {
            int start = _colStart[j];
            int end = _colStart[j + 1];
            double sum = x[j];
            for (int p = start + 1; p < end; p++)
                sum -= _re[p] * x[_rowIndex[p]];
            x[j] = sum;
        }
    }

    private void SubstituteComplex(Span<double> xr, Span<double> xi)
    {
        var im = _im!;

        for (int j = 0; j < Rows; j++)
        {
            int start = _colStart[j];
            int end = _colStart[j + 1];
            double yr = xr[j], yi = xi[j];
            for (int p = start + 1; p < end; p++)
            {
                int i = _rowIndex[p];
                xr[i] -= _re[p] * yr - im[p] * yi;
                xi[i] -= _re[p] * yi + im[p] * yr;
            }
        }

        for (int j = 0; j < Rows; j++)
        {
            int diag = _colStart[j];
            ComplexDivide(xr[j], xi[j], _re[diag], im[diag], out double zr, out double zi);
            xr[j] = zr;
            xi[j] = zi;
        }

        for (int j = Rows - 1; j >= 0; j--)
        {
            int start = _colStart[j];
            int end = _colStart[j + 1];
            double sr = xr[j], si = xi[j];
            for (int p = start + 1; p < end; p++)
            {
                int i = _rowIndex[p];
                sr -= _re[p] * xr[i] - im[p] * xi[i];
                si -= _re[p] * xi[i] + im[p] * xr[i];
            }
            xr[j] = sr;
            xi[j] = si;
        }
    }

    // ---- helpers ----

    /// <summary>
    /// (ar + i·ai) / (br + i·bi) by Smith's algorithm — the textbook formula squares the
    /// denominator's parts and overflows four hundred orders of magnitude early; scaling
    /// by the larger part first keeps every intermediate near the result's own scale.
    /// Deterministic, and exactly the real division when bi = 0.
    /// </summary>
    private static void ComplexDivide(double ar, double ai, double br, double bi, out double cr, out double ci)
    {
        if (Math.Abs(br) >= Math.Abs(bi))
        {
            double r = bi / br;
            double den = br + bi * r;
            cr = (ar + ai * r) / den;
            ci = (ai - ar * r) / den;
        }
        else
        {
            double r = br / bi;
            double den = br * r + bi;
            cr = (ar * r + ai) / den;
            ci = (ai * r - ar) / den;
        }
    }

    /// <summary>|r + i·s| without squaring first (the overflow-safe two-step form).</summary>
    private static double ComplexMagnitude(double r, double s)
    {
        double ar = Math.Abs(r), as_ = Math.Abs(s);
        double max = Math.Max(ar, as_);
        if (max == 0)
            return 0;
        double min = Math.Min(ar, as_);
        double ratio = min / max;
        return max * Math.Sqrt(1 + ratio * ratio);
    }

    /// <summary>
    /// The two parts re-expressed over ONE union pattern (symmetric-upper), zero-filled
    /// where only the other part has an entry, so the symbolic pass sees the pattern the
    /// complex matrix actually has and the two value arrays stay slot-aligned.
    /// </summary>
    private static (PackedSparseMatrix Re, PackedSparseMatrix Im) UnionUpper(
        PackedSparseMatrix real, PackedSparseMatrix imaginary)
    {
        var upperRe = real.IsSymmetricUpper ? real : real.ToSymmetricUpper();
        var upperIm = imaginary.IsSymmetricUpper ? imaginary : imaginary.ToSymmetricUpper();
        int n = upperRe.Rows;
        var reBuilder = new SparseMatrixBuilder(n, n);
        var imBuilder = new SparseMatrixBuilder(n, n);
        for (int r = 0; r < n; r++)
        {
            var reCols = upperRe.RowColumns(r);
            var reVals = upperRe.RowValues(r);
            var imCols = upperIm.RowColumns(r);
            var imVals = upperIm.RowValues(r);
            int p = 0, q = 0;
            while (p < reCols.Length || q < imCols.Length)
            {
                int cr = p < reCols.Length ? reCols[p] : int.MaxValue;
                int ci = q < imCols.Length ? imCols[q] : int.MaxValue;
                if (cr < ci)
                {
                    reBuilder.Add(r, cr, reVals[p]);
                    imBuilder.Add(r, cr, 0.0);
                    p++;
                }
                else if (ci < cr)
                {
                    reBuilder.Add(r, ci, 0.0);
                    imBuilder.Add(r, ci, imVals[q]);
                    q++;
                }
                else
                {
                    reBuilder.Add(r, cr, reVals[p]);
                    imBuilder.Add(r, cr, imVals[q]);
                    p++;
                    q++;
                }
            }
        }
        return (reBuilder.ToSymmetricUpper(), imBuilder.ToSymmetricUpper());
    }
}
