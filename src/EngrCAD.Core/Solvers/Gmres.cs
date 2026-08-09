namespace EngrCAD.Core.Solvers;

/// <summary>Options for <see cref="Gmres.Solve"/>.</summary>
public sealed record GmresOptions
{
    /// <summary>
    /// The restart length m of GMRES(m): the Krylov subspace is rebuilt from scratch every
    /// m Arnoldi steps, which bounds the storage at m + 1 basis vectors and the
    /// per-iteration orthogonalization cost at O(m·n), at the price of the finite-termination
    /// guarantee (which only full GMRES has). Clamped to n internally, since the Krylov
    /// dimension cannot exceed the matrix order — set it ≥ n for un-restarted GMRES, which
    /// converges in at most n iterations. Default 30, the usual middle ground.
    /// </summary>
    public int Restart { get; init; } = 30;

    /// <summary>
    /// Cap on the TOTAL number of Arnoldi steps (matrix–vector products) across all restart
    /// cycles. 0 (the default) means 10·n — a stall detector, since a well-preconditioned
    /// system converges in far fewer and a badly conditioned one should be reported as
    /// non-converged rather than ground on forever.
    /// </summary>
    public int MaxIterations { get; init; }

    /// <summary>
    /// Convergence test: ‖b − A·x‖ ≤ tolerance · ‖b‖, on the TRUE residual (right
    /// preconditioning makes the cheap in-cycle estimate equal to it, and the residual is
    /// recomputed exactly at every restart anyway). A zero right-hand side converges at x = 0.
    /// </summary>
    public double RelativeTolerance { get; init; } = 1e-10;
}

/// <summary>
/// Restarted GMRES(m) for a general (non-symmetric) sparse system A·x = b — the workhorse
/// Krylov method when <see cref="SparseCholesky"/>/<see cref="SparseSymmetricCG"/> do not
/// apply because the operator is not symmetric (advection, the momentum equations of flow).
/// It minimises the true residual over a growing Krylov subspace by Arnoldi + Givens QR of
/// the Hessenberg, so within one un-restarted cycle the residual is monotone and non-increasing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Right preconditioning</b> (the <see cref="IPreconditioner"/>, <c>null</c> = none): the
/// Krylov subspace is built on <c>A·M⁻¹</c> from <c>r₀ = b − A·x₀</c>, and the solution
/// correction is <c>x += M⁻¹·(V·y)</c>. The point of the RIGHT side rather than the left is
/// that the Krylov residual the Givens rotations track is the residual of the ORIGINAL
/// system <c>b − A·x</c>, not of a preconditioned one — so the number the solver watches and
/// the number a caller would recompute are the same quantity, and a solver that "converged"
/// on the wrong residual (the classic silent failure) cannot happen here. The reported
/// residual is in fact recomputed exactly, <c>‖b − A·x‖</c>, at the close of every restart
/// cycle.
/// </para>
/// <para>
/// <b>Happy breakdown is convergence, not failure.</b> When a new Arnoldi vector has
/// (near-)zero norm the Krylov subspace is invariant and the current iterate is already the
/// exact solution; the code detects it, forms that solution and reports converged, rather
/// than dividing by the zero and producing a NaN.
/// </para>
/// <para>
/// <b>Deterministic</b>: no randomness, modified Gram–Schmidt in a fixed order, every
/// reduction a fixed-order sequential sum — identical inputs give a bit-identical iterate
/// sequence. Working storage (the m + 1 basis vectors and the small Hessenberg) is allocated
/// once per solve and reused across restart cycles, matching
/// <see cref="SparseSymmetricCG"/>'s per-solve allocation.
/// </para>
/// </remarks>
public static class Gmres
{
    /// <summary>
    /// Solves A·x = b. <paramref name="x"/> carries the initial guess in (zeros are the
    /// standard cold start) and the solution out.
    /// </summary>
    /// <param name="preconditioner">Right preconditioner, or <c>null</c> for none.</param>
    /// <param name="progress">
    /// Optional cooperative cancellation, polled once per Arnoldi step and once per restart.
    /// No fraction is reported, deliberately — as with <see cref="SparseSymmetricCG"/>, an
    /// iteration count is not progress, since the residual falls at a rate nobody knows in
    /// advance and the cap is a stall detector.
    /// </param>
    /// <exception cref="OperationCanceledException">Cancellation was requested; the iterate in
    /// <paramref name="x"/> is left wherever the loop stopped.</exception>
    public static SparseSolveReport Solve(
        PackedSparseMatrix a,
        ReadOnlySpan<double> b,
        Span<double> x,
        GmresOptions? options = null,
        IPreconditioner? preconditioner = null,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Rows != a.Columns)
            throw new ArgumentException("GMRES needs a square matrix.", nameof(a));
        int n = a.Rows;
        if (b.Length != n)
            throw new ArgumentException($"b must have length {n}.", nameof(b));
        if (x.Length != n)
            throw new ArgumentException($"x must have length {n}.", nameof(x));
        if (preconditioner is not null && preconditioner.Rows != n)
            throw new ArgumentException(
                $"Preconditioner dimension {preconditioner.Rows} does not match matrix order {n}.",
                nameof(preconditioner));

        options ??= new GmresOptions();
        int m = options.Restart > 0 ? Math.Min(options.Restart, n) : n;
        int maxIterations = options.MaxIterations > 0 ? options.MaxIterations : Math.Max(10 * n, m);

        double rhsNorm = Norm(b);
        if (rhsNorm == 0)
        {
            // Exact-zero semantic test: b = 0 has the exact solution x = 0.
            x.Clear();
            return new SparseSolveReport(Converged: true, Iterations: 0, ResidualNorm: 0, RhsNorm: 0);
        }
        double threshold = options.RelativeTolerance * rhsNorm;

        // Working storage, allocated once and reused across restarts.
        var v = new double[(m + 1) * n]; // Krylov basis; vector j is v[j*n .. j*n+n)
        var h = new double[(m + 1) * m]; // Hessenberg, column-major: H(i,j) = h[i + (m+1)*j]
        var cs = new double[m];
        var sn = new double[m];
        var g = new double[m + 1];
        var y = new double[m];
        var w = new double[n];  // Arnoldi work vector, and the residual b - A x between cycles
        var mv = new double[n]; // M⁻¹ applied vector
        var z = new double[n];  // accumulated correction V·y before the final M⁻¹

        int totalIterations = 0;
        bool converged = false;

        // Initial residual r = b - A x.
        a.Multiply(x, w);
        for (int i = 0; i < n; i++)
            w[i] = b[i] - w[i];
        double residualNorm = Norm(w);
        if (residualNorm <= threshold)
            return new SparseSolveReport(true, 0, residualNorm, rhsNorm);

        while (!converged && totalIterations < maxIterations)
        {
            progress?.ThrowIfCancelled();

            // Start a restart cycle from the current true residual, held in w.
            double beta = residualNorm;
            var v0 = v.AsSpan(0, n);
            for (int i = 0; i < n; i++)
                v0[i] = w[i] / beta;
            Array.Clear(g);
            g[0] = beta;

            int cycleBudget = Math.Min(m, maxIterations - totalIterations);
            if (cycleBudget <= 0)
                break;

            int k = 0; // Arnoldi steps completed this cycle
            for (int j = 0; j < cycleBudget; j++)
            {
                progress?.ThrowIfCancelled();
                var vj = v.AsSpan(j * n, n);

                // w = A · M⁻¹ · v_j (right preconditioning).
                if (preconditioner is not null)
                {
                    preconditioner.Apply(vj, mv);
                    a.Multiply(mv, w);
                }
                else
                {
                    a.Multiply(vj, w);
                }

                // Modified Gram–Schmidt against v_0 .. v_j.
                double sumHij2 = 0;
                for (int i = 0; i <= j; i++)
                {
                    var vi = v.AsSpan(i * n, n);
                    double hij = Dot(w, vi);
                    h[i + (m + 1) * j] = hij;
                    sumHij2 += hij * hij;
                    for (int t = 0; t < n; t++)
                        w[t] -= hij * vi[t];
                }
                double hNext = Norm(w);
                h[(j + 1) + (m + 1) * j] = hNext;

                // Happy-breakdown test, scale-free against the pre-orthogonalization norm
                // (‖A M⁻¹ v_j‖² = Σ h_ij² + hNext²). A (near-)zero new vector means the
                // Krylov subspace is invariant and the current least-squares solution is exact.
                double preNorm = Math.Sqrt(sumHij2 + hNext * hNext);
                bool canContinue = hNext > 1e-14 * preNorm;
                if (canContinue)
                {
                    var vNext = v.AsSpan((j + 1) * n, n);
                    for (int t = 0; t < n; t++)
                        vNext[t] = w[t] / hNext;
                }

                // Apply the accumulated Givens rotations to column j.
                for (int i = 0; i < j; i++)
                {
                    double hi = h[i + (m + 1) * j];
                    double hi1 = h[(i + 1) + (m + 1) * j];
                    h[i + (m + 1) * j] = cs[i] * hi + sn[i] * hi1;
                    h[(i + 1) + (m + 1) * j] = -sn[i] * hi + cs[i] * hi1;
                }

                // New rotation zeroing the subdiagonal (h_jj, h_{j+1,j}).
                double hjj = h[j + (m + 1) * j];
                double hj1 = h[(j + 1) + (m + 1) * j];
                double r = double.Hypot(hjj, hj1);
                double c, s;
                if (r == 0)
                {
                    // Exact-zero guard: an all-zero column cannot rotate; leave it as the
                    // identity rotation. Reached only in a degenerate breakdown.
                    c = 1;
                    s = 0;
                }
                else
                {
                    c = hjj / r;
                    s = hj1 / r;
                }
                cs[j] = c;
                sn[j] = s;
                h[j + (m + 1) * j] = c * hjj + s * hj1; // = r
                h[(j + 1) + (m + 1) * j] = 0;

                // Residual recurrence.
                g[j + 1] = -s * g[j];
                g[j] = c * g[j];
                k = j + 1;
                totalIterations++;
                double estimate = Math.Abs(g[j + 1]);
                if (estimate <= threshold || !canContinue)
                    break;
            }

            // Solve the k×k upper-triangular system R·y = g by back substitution.
            for (int i = k - 1; i >= 0; i--)
            {
                double sum = g[i];
                for (int col = i + 1; col < k; col++)
                    sum -= h[i + (m + 1) * col] * y[col];
                y[i] = sum / h[i + (m + 1) * i];
            }

            // z = Σ y_i v_i, then x += M⁻¹ z (right preconditioning applied once to the whole
            // correction — equivalent to preconditioning each basis vector, since M⁻¹ is linear).
            Array.Clear(z);
            for (int i = 0; i < k; i++)
            {
                var vi = v.AsSpan(i * n, n);
                double yi = y[i];
                for (int t = 0; t < n; t++)
                    z[t] += yi * vi[t];
            }
            if (preconditioner is not null)
            {
                preconditioner.Apply(z, mv);
                for (int t = 0; t < n; t++)
                    x[t] += mv[t];
            }
            else
            {
                for (int t = 0; t < n; t++)
                    x[t] += z[t];
            }

            // Recompute the TRUE residual for the next cycle and the final report.
            a.Multiply(x, w);
            for (int i = 0; i < n; i++)
                w[i] = b[i] - w[i];
            residualNorm = Norm(w);
            if (residualNorm <= threshold)
                converged = true;
        }

        return new SparseSolveReport(converged, totalIterations, residualNorm, rhsNorm);
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private static double Norm(ReadOnlySpan<double> v) => Math.Sqrt(Dot(v, v));
}
