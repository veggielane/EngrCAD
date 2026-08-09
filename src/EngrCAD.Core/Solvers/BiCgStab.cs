namespace EngrCAD.Core.Solvers;

/// <summary>Options for <see cref="BiCgStab.Solve"/>.</summary>
public sealed record BiCgStabOptions
{
    /// <summary>
    /// Iteration cap; 0 (the default) means 10·n — a stall detector, not a quality knob.
    /// Each iteration costs two matrix–vector products and two preconditioner applies, so
    /// BiCGSTAB does about twice a GMRES step's work per iteration but needs no growing
    /// storage.
    /// </summary>
    public int MaxIterations { get; init; }

    /// <summary>
    /// Convergence test: ‖b − A·x‖ ≤ tolerance · ‖b‖. A zero right-hand side converges at
    /// x = 0.
    /// </summary>
    public double RelativeTolerance { get; init; } = 1e-10;
}

/// <summary>
/// BiCGSTAB (van der Vorst) for a general (non-symmetric) sparse system A·x = b — the
/// cheaper-per-iteration alternative to <see cref="Gmres"/>. It keeps a short fixed
/// recurrence (no growing Krylov basis, so constant storage) at the cost of GMRES's monotone
/// residual: BiCGSTAB's residual can oscillate, and on some systems it stalls where GMRES
/// grinds through. Which of the two wins is problem-dependent — CFD codes carry both — so
/// both are provided.
/// </summary>
/// <remarks>
/// <para>
/// <b>Preconditioning</b> (the <see cref="IPreconditioner"/>, <c>null</c> = none) is applied
/// to the two search directions (<c>M⁻¹·p</c>, <c>M⁻¹·s</c>), and the residual the recurrence
/// carries stays the true residual <c>b − A·x</c> throughout — there is no preconditioned-vs-
/// true-residual ambiguity to guard against here as there is in GMRES. The final reported
/// residual is nonetheless recomputed exactly as <c>‖b − A·x‖</c>, because the recurrence
/// residual can drift from the true one over many iterations, and a caller must be told the
/// number it can verify.
/// </para>
/// <para>
/// <b>Breakdown is reported, never a silent NaN.</b> BiCGSTAB has two failure modes — the
/// shadow residual going orthogonal to the residual (<c>ρ ≈ 0</c>) and the stabiliser step
/// collapsing (<c>ω ≈ 0</c>, or <c>t ≈ 0</c>) — and each is detected BEFORE the division it
/// would spoil, breaking the loop and returning <see cref="SparseSolveReport"/> with
/// <c>Converged = false</c> and the last honest residual, exactly as
/// <see cref="SparseSymmetricCG"/> reports a non-SPD search direction rather than dividing by
/// it. The near-orthogonality guards are relative to the vectors' own norms (scale-free); the
/// two denominator guards are exact-zero tests.
/// </para>
/// <para>
/// <b>Deterministic</b>: no randomness, every reduction a fixed-order sequential sum. The
/// shadow residual r̂₀ is seeded with the initial residual (the standard choice), so identical
/// inputs give a bit-identical iterate sequence. Working vectors are allocated once per solve.
/// </para>
/// </remarks>
public static class BiCgStab
{
    /// <summary>
    /// Solves A·x = b. <paramref name="x"/> carries the initial guess in and the solution out.
    /// </summary>
    /// <param name="preconditioner">Preconditioner applied to the search directions, or <c>null</c> for none.</param>
    /// <param name="progress">
    /// Optional cooperative cancellation, polled once per iteration. No fraction is reported,
    /// for the same reason as <see cref="SparseSymmetricCG"/>: an iteration count is not
    /// progress.
    /// </param>
    /// <exception cref="OperationCanceledException">Cancellation was requested; the iterate in
    /// <paramref name="x"/> is left wherever the loop stopped.</exception>
    public static SparseSolveReport Solve(
        PackedSparseMatrix a,
        ReadOnlySpan<double> b,
        Span<double> x,
        BiCgStabOptions? options = null,
        IPreconditioner? preconditioner = null,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Rows != a.Columns)
            throw new ArgumentException("BiCGSTAB needs a square matrix.", nameof(a));
        int n = a.Rows;
        if (b.Length != n)
            throw new ArgumentException($"b must have length {n}.", nameof(b));
        if (x.Length != n)
            throw new ArgumentException($"x must have length {n}.", nameof(x));
        if (preconditioner is not null && preconditioner.Rows != n)
            throw new ArgumentException(
                $"Preconditioner dimension {preconditioner.Rows} does not match matrix order {n}.",
                nameof(preconditioner));

        options ??= new BiCgStabOptions();
        int maxIterations = options.MaxIterations > 0 ? options.MaxIterations : 10 * n;

        double rhsNorm = Norm(b);
        if (rhsNorm == 0)
        {
            x.Clear();
            return new SparseSolveReport(Converged: true, Iterations: 0, ResidualNorm: 0, RhsNorm: 0);
        }
        double threshold = options.RelativeTolerance * rhsNorm;

        var r = new double[n];
        var rHat = new double[n];
        var p = new double[n];  // starts at 0
        var v = new double[n];  // starts at 0
        var s = new double[n];
        var t = new double[n];
        var pHat = new double[n];
        var sHat = new double[n];

        // r = b - A x.
        a.Multiply(x, r);
        for (int i = 0; i < n; i++)
            r[i] = b[i] - r[i];
        double residualNorm = Norm(r);
        if (residualNorm <= threshold)
            return new SparseSolveReport(true, 0, residualNorm, rhsNorm);

        r.AsSpan().CopyTo(rHat);
        double rHatNorm = residualNorm; // ‖r̂₀‖, fixed

        double rho = 1, alpha = 1, omega = 1;
        // Relative near-orthogonality tolerance for the ρ breakdown (a cosine floor). The two
        // denominator collapses (r̂₀·v and t·t) are exact-zero guards instead — see below.
        const double breakdownRel = 1e-15;

        int iteration = 0;
        while (iteration < maxIterations)
        {
            progress?.ThrowIfCancelled();
            iteration++;

            double rhoNew = Dot(rHat, r);
            if (Math.Abs(rhoNew) <= breakdownRel * rHatNorm * residualNorm)
                break; // ρ ≈ 0: r̂₀ went orthogonal to r — BiCGSTAB breakdown, reported below.

            double beta = (rhoNew / rho) * (alpha / omega);
            for (int i = 0; i < n; i++)
                p[i] = r[i] + beta * (p[i] - omega * v[i]);

            Precondition(preconditioner, p, pHat);
            a.Multiply(pHat, v);

            double rHatV = Dot(rHat, v);
            if (rHatV == 0.0)
                break; // exact-zero division guard for alpha.
            alpha = rhoNew / rHatV;

            for (int i = 0; i < n; i++)
                s[i] = r[i] - alpha * v[i];

            double sNorm = Norm(s);
            if (sNorm <= threshold)
            {
                // Half-step convergence: the residual after the α update is already small.
                for (int i = 0; i < n; i++)
                    x[i] += alpha * pHat[i];
                break;
            }

            Precondition(preconditioner, s, sHat);
            a.Multiply(sHat, t);

            double tt = Dot(t, t);
            if (tt == 0.0)
            {
                // t ≈ 0: the stabiliser step is undefined. Commit the α half-step and stop.
                for (int i = 0; i < n; i++)
                    x[i] += alpha * pHat[i];
                break;
            }
            omega = Dot(t, s) / tt;

            for (int i = 0; i < n; i++)
            {
                x[i] += alpha * pHat[i] + omega * sHat[i];
                r[i] = s[i] - omega * t[i];
            }
            residualNorm = Norm(r);
            rho = rhoNew;

            if (residualNorm <= threshold)
                break;
            if (omega == 0.0)
                break; // exact-zero guard: next iteration's β divides by ω.
        }

        // Report the TRUE residual, recomputed independently — the recurrence residual can
        // drift, and a converged/failed verdict must rest on the number a caller can check.
        a.Multiply(x, t);
        for (int i = 0; i < n; i++)
            t[i] = b[i] - t[i];
        residualNorm = Norm(t);
        bool converged = residualNorm <= threshold;
        return new SparseSolveReport(converged, iteration, residualNorm, rhsNorm);
    }

    private static void Precondition(IPreconditioner? preconditioner, ReadOnlySpan<double> input, Span<double> output)
    {
        if (preconditioner is not null)
            preconditioner.Apply(input, output);
        else
            input.CopyTo(output);
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
