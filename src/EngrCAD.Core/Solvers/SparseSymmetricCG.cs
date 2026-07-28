namespace EngrCAD.Core.Solvers;

/// <summary>
/// Options for <see cref="SparseSymmetricCG.Solve"/>.
/// </summary>
public sealed record CgOptions
{
    /// <summary>
    /// Iteration cap. 0 (the default) means 10·n — in exact arithmetic CG terminates in
    /// n steps, so a cap well above n is a stall detector, not a quality knob.
    /// </summary>
    public int MaxIterations { get; init; }

    /// <summary>
    /// Convergence test: ‖r‖ ≤ tolerance · ‖b‖ (relative to the right-hand side, so the
    /// test is scale-free; a zero right-hand side converges immediately to x = 0).
    /// </summary>
    public double RelativeTolerance { get; init; } = 1e-10;

    /// <summary>
    /// Jacobi (diagonal) preconditioning, on by default. Costs one divide per unknown
    /// per iteration and repairs the badly scaled diagonals that mixed-stiffness systems
    /// (soft-constraint rows next to Laplacian rows) otherwise stall on.
    /// </summary>
    public bool UseJacobiPreconditioner { get; init; } = true;
}

/// <summary>
/// Convergence report of an iterative solve — a return value, never a log line
/// (the repo's report-what-happened convention: callers decide what a non-converged
/// solve means; the solver only refuses to lie about it).
/// </summary>
public readonly record struct SparseSolveReport(bool Converged, int Iterations, double ResidualNorm, double RhsNorm)
{
    /// <summary>‖r‖ / ‖b‖, or ‖r‖ itself when ‖b‖ = 0 (which converges at exactly 0).</summary>
    public double RelativeResidual => RhsNorm > 0 ? ResidualNorm / RhsNorm : ResidualNorm;

    public override string ToString() =>
        $"{(Converged ? "converged" : "NOT converged")} after {Iterations} iterations, |r|/|b| = {RelativeResidual:E3}";
}

/// <summary>
/// Jacobi-preconditioned conjugate gradients for symmetric positive-definite systems
/// (geometry3Sharp's <c>SparseSymmetricCG</c> role). Deterministic: no randomness, and
/// every reduction is a fixed-order sequential sum, so identical inputs produce
/// bit-identical iterates. The workhorse for systems too large or too transient to be
/// worth a <see cref="SparseCholesky"/> factorization, and the fallback for systems that
/// will be solved once; the factorization wins when one operator is solved for many
/// right-hand sides (the Laplacian solvers' shape — x, y, z share one matrix).
/// </summary>
public static class SparseSymmetricCG
{
    /// <summary>
    /// Solves A·x = b. <paramref name="x"/> carries the initial guess in (zeros are the
    /// standard cold start; a warm start from a previous solution is often most of the
    /// work) and the solution out. The matrix must be symmetric positive definite —
    /// symmetric-upper storage is the natural form, but general storage of a symmetric
    /// matrix works identically.
    /// </summary>
    public static SparseSolveReport Solve(PackedSparseMatrix a, ReadOnlySpan<double> b, Span<double> x, CgOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Rows != a.Columns)
            throw new ArgumentException("CG needs a square (symmetric positive-definite) matrix.", nameof(a));
        int n = a.Rows;
        if (b.Length != n)
            throw new ArgumentException($"b must have length {n}.", nameof(b));
        if (x.Length != n)
            throw new ArgumentException($"x must have length {n}.", nameof(x));

        options ??= new CgOptions();
        int maxIterations = options.MaxIterations > 0 ? options.MaxIterations : 10 * n;
        double rhsNorm = Norm(b);
        if (rhsNorm == 0)
        {
            // Exact-zero semantic test: b = 0 has the exact solution x = 0 for any SPD A.
            x.Clear();
            return new SparseSolveReport(Converged: true, Iterations: 0, ResidualNorm: 0, RhsNorm: 0);
        }
        double threshold = options.RelativeTolerance * rhsNorm;

        // Jacobi preconditioner: invDiag[i] = 1 / a_ii. An SPD matrix has strictly
        // positive diagonals, so a nonpositive entry means the caller's matrix is not
        // SPD there; scaling that row by 1 keeps the preconditioner positive definite
        // (a sign test, deliberately not a Tolerance comparison).
        double[]? invDiag = null;
        if (options.UseJacobiPreconditioner)
        {
            invDiag = new double[n];
            a.Diagonal(invDiag);
            for (int i = 0; i < n; i++)
                invDiag[i] = invDiag[i] > 0 ? 1.0 / invDiag[i] : 1.0;
        }

        var r = new double[n];
        var z = new double[n];
        var p = new double[n];
        var ap = new double[n];

        // r = b - A x  (x may be a warm start).
        a.Multiply(x, r);
        for (int i = 0; i < n; i++)
            r[i] = b[i] - r[i];

        double residualNorm = Norm(r);
        if (residualNorm <= threshold)
            return new SparseSolveReport(true, 0, residualNorm, rhsNorm);

        ApplyPreconditioner(invDiag, r, z);
        z.AsSpan().CopyTo(p);
        double rz = Dot(r, z);

        int iteration = 0;
        while (iteration < maxIterations)
        {
            iteration++;
            a.Multiply(p, ap);
            double pap = Dot(p, ap);
            if (pap <= 0)
            {
                // A search direction with nonpositive curvature means the matrix is not
                // positive definite (or round-off has exhausted the recurrence); report
                // honestly rather than dividing by it.
                break;
            }
            double alpha = rz / pap;
            for (int i = 0; i < n; i++)
            {
                x[i] += alpha * p[i];
                r[i] -= alpha * ap[i];
            }

            residualNorm = Norm(r);
            if (residualNorm <= threshold)
                return new SparseSolveReport(true, iteration, residualNorm, rhsNorm);

            ApplyPreconditioner(invDiag, r, z);
            double rzNext = Dot(r, z);
            double beta = rzNext / rz;
            rz = rzNext;
            for (int i = 0; i < n; i++)
                p[i] = z[i] + beta * p[i];
        }

        return new SparseSolveReport(false, iteration, residualNorm, rhsNorm);
    }

    private static void ApplyPreconditioner(double[]? invDiag, double[] r, double[] z)
    {
        if (invDiag is null)
        {
            r.AsSpan().CopyTo(z);
            return;
        }
        for (int i = 0; i < r.Length; i++)
            z[i] = invDiag[i] * r[i];
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
