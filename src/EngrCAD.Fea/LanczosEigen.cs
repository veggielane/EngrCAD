using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>One converged eigenpair of <c>K·phi = lambda·M·phi</c>.</summary>
/// <param name="Eigenvalue">lambda.</param>
/// <param name="Vector">phi, normalised so <c>phi' M phi = 1</c>.</param>
/// <param name="Residual">The MEASURED relative residual
/// <c>|K phi - lambda M phi| / (|K phi| + |lambda| |M phi|)</c> — not a bound, not an
/// estimate.</param>
internal readonly record struct EigenPair(double Eigenvalue, double[] Vector, double Residual);

/// <summary>What a Lanczos run cost and whether it finished.</summary>
/// <param name="Pairs">The converged pairs, ascending in eigenvalue.</param>
/// <param name="Restarts">How many times the Krylov space was rebuilt from a fresh start
/// vector.</param>
/// <param name="Iterations">Total Lanczos steps, i.e. total back-substitutions through the
/// single factorization — the number that makes the factor-once argument concrete.</param>
/// <param name="Converged">False when the run hit its caps with fewer pairs than asked
/// for.</param>
/// <param name="WorstResidual">The largest measured residual among the returned pairs.</param>
/// <param name="Candidate">
/// The best eigenvalue the iteration SAW but did not accept, and the residual it stalled at
/// — null when nothing beyond the accepted pairs ever appeared.
///
/// <para><b>It exists because "no pair converged" has two completely different causes and a
/// caller cannot tell them apart from an empty list.</b> Either the spectrum genuinely holds
/// nothing wanted (a structure with no positive buckling factor at all), or a candidate was
/// there the whole time and its measured residual never reached the tolerance — which
/// happens on a slender, finely meshed model where the residual's own floor is set by the
/// conditioning of K rather than by the iteration. The two want opposite responses from the
/// user, so a refusal that cannot name which one it hit sends people to look in the wrong
/// place.</para>
/// </param>
internal sealed record LanczosResult(
    IReadOnlyList<EigenPair> Pairs,
    int Restarts,
    int Iterations,
    bool Converged,
    double WorstResidual,
    (double Eigenvalue, double Residual)? Candidate);

/// <summary>
/// Shift-and-invert Lanczos for the generalized symmetric eigenproblem
/// <c>K·phi = lambda·M·phi</c>, at the LOW end of the spectrum.
///
/// <para><b>The operator is <c>A^-1 M</c> with <c>A = K - sigma·M</c>, and the inner product
/// is M's.</b> That operator is self-adjoint with respect to <c>&lt;x, y&gt; = x' M y</c>,
/// which is what makes a three-term Lanczos recursion legal at all; its eigenvalues are
/// <c>theta = 1/(lambda - sigma)</c>, so the eigenvalues nearest the shift become the
/// LARGEST of the operator — and extreme eigenvalues are exactly what Lanczos converges to
/// first. Put sigma at or just below zero and the lowest natural frequencies come out
/// first, which is the only end of the spectrum an engineer ever wants.</para>
///
/// <para><b>The METRIC is a separate parameter from M, and that is what makes this one
/// implementation serve buckling as well as vibration.</b> <c>A^-1 B</c> is self-adjoint in
/// the A inner product AND in the B inner product whenever A and B are symmetric —
/// <c>&lt;A^-1 Bx, y&gt;_A = x' B y = &lt;x, A^-1 By&gt;_A</c> and likewise in B — so the
/// iteration may run in either, and the only requirement is that the one it runs in be
/// positive DEFINITE. A modal solve runs in M's, which is the definite one there. A
/// buckling solve cannot: its right-hand matrix is a geometric stiffness, which is
/// indefinite, so it runs in K's instead and factorizes K itself. Passing the same matrix
/// for <c>m</c> and <c>metric</c> takes exactly the arithmetic the modal path always had,
/// operation for operation, which is why that path's output is unchanged to the bit.</para>
///
/// <para><b>This is the case where a direct factorization genuinely amortises.</b> Every
/// Lanczos step is one back-substitution through ONE factorization of A. A run that takes
/// sixty steps to deliver ten modes pays for one factorization and spends sixty solves —
/// the "factor once, solve many right-hand sides" argument that
/// <see cref="FeaSolveMethod.Direct"/> honestly records as NOT applying to the static
/// solver, which factors and discards.</para>
///
/// <para><b>Full reorthogonalization, and the multiplicity problem it creates.</b> Every new
/// Lanczos vector is re-orthogonalized (in M) against every vector already in the basis, in
/// two passes. Without it, round-off reintroduces converged directions and the run returns
/// spurious duplicate copies of eigenvalues it has already found — the classic ghost. With
/// it, a GENUINE multiplicity is invisible: single-vector Lanczos spans a Krylov space that
/// contains only one vector from each eigenspace, and a square shaft's two identical bending
/// modes are a real, common multiplicity. The fix here is LOCKING AND RESTARTING: converged
/// Ritz vectors join the deflation set, and a fresh start vector is orthogonalized against
/// them, so the next run's extreme eigenvalue is the second copy. That reuses the same
/// deflation machinery the rigid-body modes already need, rather than adding a block
/// variant.</para>
///
/// <para><b>Convergence is MEASURED, not bounded.</b> The textbook test is the Lanczos
/// bound <c>beta_m·|y_m|</c>, which is a bound on the residual of the SHIFTED, INVERTED
/// operator — one transformation away from the quantity a caller cares about. This computes
/// the true residual <c>K phi - lambda M phi</c> instead, at the cost of two sparse
/// matrix-vector products per candidate per check, and reports it. It is the same choice
/// the rest of this project makes wherever a bound and a measurement are both available.
/// </para>
/// </summary>
internal static class LanczosEigen
{
    /// <summary>
    /// Runs the iteration.
    /// </summary>
    /// <param name="k">The left-hand matrix of <c>K phi = lambda M phi</c> (symmetric upper)
    /// — the reduced stiffness for a modal solve.</param>
    /// <param name="m">The right-hand matrix (symmetric upper): the reduced mass for a modal
    /// solve, the negated geometric stiffness for a buckling one. It multiplies the vector
    /// the factorization is applied to, and it appears in the measured residual.</param>
    /// <param name="metric">The POSITIVE DEFINITE matrix whose inner product the iteration
    /// runs in. Pass <paramref name="m"/> itself for a modal solve; pass
    /// <paramref name="k"/> where the right-hand matrix is indefinite.</param>
    /// <param name="factor">A factorization of <c>K - shift·M</c>.</param>
    /// <param name="shift">sigma. Zero when K is positive definite.</param>
    /// <param name="deflation">Metric-orthonormal vectors to exclude from the search — the
    /// rigid-body modes, whose eigenvalue is zero and which are reported separately.</param>
    /// <param name="wanted">How many eigenpairs above the deflation space to return.</param>
    /// <param name="tolerance">Relative residual a pair must reach to be locked.</param>
    /// <param name="maxKrylov">Cap on the Krylov dimension of ONE run.</param>
    /// <param name="maxRestarts">Cap on the number of restarts.</param>
    public static LanczosResult Solve(
        PackedSparseMatrix k,
        PackedSparseMatrix m,
        PackedSparseMatrix metric,
        SparseCholesky factor,
        double shift,
        IReadOnlyList<double[]> deflation,
        int wanted,
        double tolerance,
        int maxKrylov,
        int maxRestarts)
    {
        int n = k.Rows;
        var locked = new List<EigenPair>();
        // The deflation set grows as pairs lock: rigid modes first, then everything found.
        var deflate = new List<double[]>(deflation);

        // ONE MORE than asked for, and the extra is not a safety margin — it is what makes
        // an exactly degenerate pair come out right. A single-vector Krylov space contains
        // one vector from each eigenspace, so a run finds ONE copy of a double eigenvalue;
        // the second copy is only reachable after the first is deflated, i.e. in a later
        // run, by which time a solve that stopped the moment it had `wanted` pairs would
        // already have returned the NEXT distinct eigenvalue in its place. Targeting one
        // extra and then returning the lowest `wanted` gives the missed copy a run to appear
        // in, and it sorts ahead of the extra. (This is not a proof of completeness for
        // multiplicity three and above — that wants a block method, and it is filed rather
        // than pretended.)
        int target = Math.Min(wanted + 1, Math.Max(wanted, n - deflation.Count));

        var scratchS = new double[n];
        var scratchR = new double[n];
        int totalIterations = 0;
        int restart = 0;
        (double Eigenvalue, double Residual)? candidate = null;

        while (locked.Count < target && restart <= maxRestarts)
        {
            var start = StartVector(n, restart);
            if (!Purge(metric, deflate, start, scratchS))
            {
                // The start vector lay entirely in the deflation space. A different one
                // will not (they are constructed to differ), so this is a restart, not a
                // failure — but it does consume one.
                restart++;
                continue;
            }

            var run = RunOnce(
                k, m, metric, factor, shift, deflate, start,
                target - locked.Count, tolerance, maxKrylov, scratchS, scratchR);
            totalIterations += run.Steps;
            restart++;

            // Keep the closest miss across every run, not the last one: a later restart may
            // stall further away, and the number worth reporting is the best the iteration
            // ever managed.
            if (run.Candidate is { } miss
                && (candidate is not { } best || miss.Residual < best.Residual))
                candidate = miss;

            if (run.Pairs.Count == 0)
                continue;

            foreach (var pair in run.Pairs)
            {
                locked.Add(pair);
                deflate.Add(pair.Vector);
                if (locked.Count >= target)
                    break;
            }
        }

        locked.Sort((a, b) => a.Eigenvalue.CompareTo(b.Eigenvalue));
        bool converged = locked.Count >= wanted;
        if (locked.Count > wanted)
            locked.RemoveRange(wanted, locked.Count - wanted);

        double worst = 0;
        foreach (var pair in locked)
            worst = Math.Max(worst, pair.Residual);

        return new LanczosResult(
            locked, Math.Max(0, restart - 1), totalIterations, converged, worst, candidate);
    }

    private readonly record struct RunResult(
        List<EigenPair> Pairs, int Steps, (double Eigenvalue, double Residual)? Candidate);

    /// <summary>One Lanczos run from one start vector, stopping when it has converged
    /// <paramref name="wanted"/> pairs, hit <paramref name="maxKrylov"/>, or found an
    /// invariant subspace.</summary>
    private static RunResult RunOnce(
        PackedSparseMatrix k,
        PackedSparseMatrix m,
        PackedSparseMatrix metric,
        SparseCholesky factor,
        double shift,
        List<double[]> deflate,
        double[] start,
        int wanted,
        double tolerance,
        int maxKrylov,
        double[] scratchS,
        double[] scratchR)
    {
        int n = k.Rows;
        int cap = Math.Min(maxKrylov, n);
        var basis = new List<double[]>(cap);
        var alpha = new List<double>(cap);
        var beta = new List<double>(cap);

        // Reference equality, and deliberately so: when the caller passes ONE matrix for
        // both roles the two products below are literally the same numbers, so aliasing the
        // buffers is what keeps the modal path's arithmetic identical to what it was before
        // the metric became a parameter — not merely equivalent to it.
        bool separateMetric = !ReferenceEquals(metric, m);

        var q = start;
        var p = new double[n];
        m.Multiply(q, p);                       // p == M q
        // metricQ == metric·q, the vector every inner product is taken against; q is
        // already metric-normalised.
        var metricQ = p;
        if (separateMetric)
        {
            metricQ = new double[n];
            metric.Multiply(q, metricQ);
        }
        var found = new List<EigenPair>();
        (double Eigenvalue, double Residual)? candidate = null;
        double alphaScale = 0;
        int steps = 0;

        for (int j = 0; j < cap; j++)
        {
            basis.Add(q);
            steps++;

            var r = scratchR;
            factor.Solve(p, r);                 // r = A^-1 M q_j — the one back-substitution
            double a = Dot(metricQ, r);         // = <q_j, r>_metric
            alpha.Add(a);
            alphaScale = Math.Max(alphaScale, Math.Abs(a));

            for (int i = 0; i < n; i++)
                r[i] -= a * q[i];
            if (j > 0)
            {
                double b = beta[j - 1];
                var previous = basis[j - 1];
                for (int i = 0; i < n; i++)
                    r[i] -= b * previous[i];
            }

            // Full reorthogonalization in the M inner product, two passes. Deliberately
            // unconditional rather than DGKS's "repeat if the norm dropped": a conditional
            // second pass makes the arithmetic depend on a threshold, and this run has to be
            // reproducible for the same reason every other kernel here does.
            for (int pass = 0; pass < 2; pass++)
            {
                metric.Multiply(r, scratchS);
                foreach (var v in deflate)
                    Subtract(r, v, Dot(v, scratchS));
                foreach (var v in basis)
                    Subtract(r, v, Dot(v, scratchS));
            }

            metric.Multiply(r, scratchS);
            double nu = Dot(r, scratchS);
            double b2 = nu > 0 ? Math.Sqrt(nu) : 0;

            // Scale-free breakdown test: beta measured against the operator's own diagonal
            // scale (the largest alpha seen), not against an absolute length. A vanishing
            // beta means the Krylov space is INVARIANT, which is a success — every Ritz
            // pair in it is exact — so the run stops and the caller restarts elsewhere.
            bool breakdown = b2 <= 1e-13 * alphaScale;

            bool lastStep = j == cap - 1;
            bool check = breakdown || lastStep
                || (j + 1 >= wanted && (j + 1 - wanted) % CheckEvery == 0);

            if (check)
            {
                // Its OWN scratch, not scratchS: the next Lanczos vector's M-product is
                // sitting in scratchS at this point and is consumed three lines below. The
                // first version shared it, the Ritz step overwrote it, and every run past
                // its first convergence check built its next basis vector from a
                // half-finished matrix-vector product — which showed up as the whole
                // iteration breaking down after four steps rather than as a wrong answer.
                found = Ritz(
                    k, m, metric, basis, alpha, beta, shift, wanted, tolerance, new double[n],
                    out var miss);
                if (miss is { } m2 && (candidate is not { } best || m2.Residual < best.Residual))
                    candidate = m2;
                if (found.Count >= wanted || breakdown || lastStep)
                    return new RunResult(found, steps, candidate);
            }

            if (breakdown)
                return new RunResult(found, steps, candidate);

            beta.Add(b2);
            var next = new double[n];
            var nextMetric = new double[n];
            double inverse = 1.0 / b2;
            for (int i = 0; i < n; i++)
            {
                next[i] = r[i] * inverse;
                nextMetric[i] = scratchS[i] * inverse;
            }
            var nextP = nextMetric;
            if (separateMetric)
            {
                // scratchS holds metric·r, not m·r, so the operator's right-hand product has
                // to be taken afresh — one extra sparse multiply per step, against a
                // back-substitution through the whole factorization.
                nextP = new double[n];
                m.Multiply(next, nextP);
            }
            q = next;
            p = nextP;
            metricQ = nextMetric;
        }

        return new RunResult(found, steps, candidate);
    }

    /// <summary>How often to run the Rayleigh-Ritz check once the basis is large enough.
    /// Each check costs a small dense eigen-decomposition plus a few matrix-vector products
    /// per candidate, so checking every step would dominate a cheap solve while checking
    /// only at the end would over-run an easy one.</summary>
    private const int CheckEvery = 5;

    /// <summary>
    /// The Rayleigh-Ritz step: eigen-decompose the tridiagonal projection, map each Ritz
    /// value back through <c>lambda = sigma + 1/theta</c>, form the Ritz vectors, and keep
    /// those whose MEASURED residual is within tolerance.
    /// </summary>
    private static List<EigenPair> Ritz(
        PackedSparseMatrix k,
        PackedSparseMatrix m,
        PackedSparseMatrix metric,
        List<double[]> basis,
        List<double> alpha,
        List<double> beta,
        double shift,
        int wanted,
        double tolerance,
        double[] scratch,
        out (double Eigenvalue, double Residual)? rejected)
    {
        rejected = null;
        int size = basis.Count;
        int n = basis[0].Length;
        var t = new double[size * size];
        for (int i = 0; i < size; i++)
        {
            t[i * size + i] = alpha[i];
            if (i + 1 < size)
            {
                t[i * size + i + 1] = beta[i];
                t[(i + 1) * size + i] = beta[i];
            }
        }

        var (values, vectors) = SmallSymmetricEigen.Solve(t, size);

        // Largest theta first: theta = 1/(lambda - sigma), so the largest theta is the
        // eigenvalue closest to the shift from above, which is the lowest frequency.
        var order = Enumerable.Range(0, size).OrderByDescending(i => values[i]).ToList();

        var accepted = new List<EigenPair>();
        var phi = new double[n];
        var kPhi = new double[n];
        var mPhi = new double[n];

        foreach (int column in order)
        {
            double theta = values[column];
            // Exact-zero division guard, and a semantic one: a non-positive theta is an
            // eigenvalue at or BELOW the shift, which for a shift placed under the spectrum
            // can only be round-off around the deflated null space. Nothing beyond it in
            // this descending order is wanted either, so the walk stops rather than skips.
            if (!(theta > 0))
                break;

            Array.Clear(phi);
            for (int i = 0; i < size; i++)
            {
                double y = vectors[i * size + column];
                if (y == 0)
                    continue;
                Subtract(phi, basis[i], -y);
            }

            // Re-normalise in the METRIC. The Ritz vector is metric-orthonormal by
            // construction (the basis is, and y is orthonormal), so this is a round-off
            // correction — but it is the quantity every downstream identity is stated in, so
            // it is asked for rather than assumed.
            metric.Multiply(phi, scratch);
            double norm = Dot(phi, scratch);
            if (!(norm > 0))
                break;
            double inverse = 1.0 / Math.Sqrt(norm);
            for (int i = 0; i < n; i++)
                phi[i] *= inverse;

            double lambda = shift + 1.0 / theta;
            k.Multiply(phi, kPhi);
            m.Multiply(phi, mPhi);
            double numerator = 0, kNorm = 0, mNorm = 0;
            for (int i = 0; i < n; i++)
            {
                double d = kPhi[i] - lambda * mPhi[i];
                numerator += d * d;
                kNorm += kPhi[i] * kPhi[i];
                mNorm += mPhi[i] * mPhi[i];
            }
            double scale = Math.Sqrt(kNorm) + Math.Abs(lambda) * Math.Sqrt(mNorm);
            double residual = scale > 0 ? Math.Sqrt(numerator) / scale : Math.Sqrt(numerator);

            // A CONTIGUOUS PREFIX, and this is the load-bearing line. Ritz values converge
            // from the extreme end inwards, but not in lock step — a run can have the SECOND
            // eigenvalue converged while the first is still moving. Accepting whatever has
            // converged then returns eigenvalues 2 and 3 as "the two lowest", which is not a
            // slightly worse answer but a different one: measured on a 19 440-DOF cantilever
            // asked for one mode, it returned 4 997.9 Hz for a first bending mode of 834.9 Hz
            // — a plausible number that happened to be the SECOND bending pair. Stopping at
            // the first pair that has not converged is what makes "the lowest k" mean it.
            if (residual > tolerance)
            {
                // The closest miss of this check, recorded before the walk stops: it is the
                // only evidence a caller has that the wanted eigenvalue was there at all.
                rejected = (lambda, residual);
                break;
            }

            accepted.Add(new EigenPair(lambda, (double[])phi.Clone(), residual));
            if (accepted.Count >= wanted)
                break;
        }
        return accepted;
    }

    /// <summary>
    /// Orthogonalizes <paramref name="v"/> against <paramref name="against"/> in the
    /// <paramref name="metric"/> inner product (two passes) and normalizes it there. False
    /// when nothing survives, i.e. the vector lay in the space.
    /// </summary>
    public static bool Purge(
        PackedSparseMatrix metric, IReadOnlyList<double[]> against, double[] v, double[] scratch)
    {
        int n = v.Length;
        double initial = 0;
        for (int i = 0; i < n; i++)
            initial += v[i] * v[i];

        for (int pass = 0; pass < 2; pass++)
        {
            metric.Multiply(v, scratch);
            foreach (var u in against)
                Subtract(v, u, Dot(u, scratch));
        }

        metric.Multiply(v, scratch);
        double norm = Dot(v, scratch);
        // Relative to what came in, so the test is scale-free: a vector that lost all its
        // Euclidean length to the projection is in the space, whatever the model's units.
        if (!(norm > 0) || !(initial > 0))
            return false;
        double euclidean = 0;
        for (int i = 0; i < n; i++)
            euclidean += v[i] * v[i];
        if (euclidean <= 1e-24 * initial)
            return false;

        double inverse = 1.0 / Math.Sqrt(norm);
        for (int i = 0; i < n; i++)
            v[i] *= inverse;
        return true;
    }

    /// <summary>
    /// A deterministic start vector, different for every restart. Deliberately NOT all-ones:
    /// that vector is orthogonal to some eigenvectors of a symmetric operator often enough to
    /// matter, and a Krylov space started orthogonal to a mode never finds it.
    /// </summary>
    private static double[] StartVector(int n, int restart)
    {
        var v = new double[n];
        int a = 7 + 2 * restart, b = 3 + restart, c = 11 + 3 * restart;
        for (int i = 0; i < n; i++)
            v[i] = 1.0 + (i % a) * 0.13 - (i % b) * 0.21 + (i % c) * 0.07;
        return v;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    /// <summary><c>target -= coefficient · source</c>.</summary>
    private static void Subtract(double[] target, double[] source, double coefficient)
    {
        // Exact-zero skip: subtracting nothing is a no-op, and this runs once per basis
        // vector per step.
        if (coefficient == 0)
            return;
        for (int i = 0; i < target.Length; i++)
            target[i] -= coefficient * source[i];
    }
}
