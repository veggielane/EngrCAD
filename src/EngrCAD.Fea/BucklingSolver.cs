using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>Options for <see cref="BucklingSolver.Solve"/>.</summary>
public sealed record BucklingSolveOptions
{
    /// <summary>How many buckling modes to extract, lowest load factor first (default 4).</summary>
    public int ModeCount
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 4;

    /// <summary>Elimination ordering for the factorization (default
    /// <see cref="SparseOrdering.Amd"/>, the same reason the static and modal solvers default
    /// to it — a stiffness matrix is exactly where fill dominates).</summary>
    public SparseOrdering Ordering { get; init; } = SparseOrdering.Amd;

    /// <summary>
    /// The relative residual <c>|K phi + lambda Kg phi| / (|K phi| + |lambda||Kg phi|)</c> a
    /// mode must reach before it is accepted — <b>1e-7, two decades looser than
    /// <see cref="ModalSolveOptions.Tolerance"/>, and that is a measured decision rather than
    /// a hedge.</b>
    ///
    /// <para>The residual is a TOTAL cancellation of <c>K phi</c> against
    /// <c>lambda Kg phi</c>, so its floor is set by how accurately each product can be formed
    /// — about <c>eps·kappa(K)</c> relative to a smooth mode's own energy. A modal solve
    /// escapes that because its Krylov vectors come out of <c>K^-1 M</c>, which SMOOTHS: the
    /// high-frequency content that <c>K</c> amplifies is suppressed before <c>K</c> ever sees
    /// it. This solver's operator is <c>K^-1 Kg</c>, and a geometric stiffness is
    /// derivative-like, so the two halves roughly cancel in frequency content and the Lanczos
    /// vectors keep the high-frequency part that sets the floor. Measured on this project's
    /// own pinned-pinned column at slenderness 69: every mesh up to 9 310 DOF reaches 1e-10,
    /// and a 23 166-DOF one stalls at <b>1.76e-9</b> — so a 1e-9 default would refuse a
    /// perfectly ordinary refinement of a perfectly ordinary model.</para>
    ///
    /// <para>Nothing is given up for it. An eigenvalue is accurate to roughly the SQUARE of
    /// the residual over the spectral gap, so 1e-7 buys load factors good to about 1e-14
    /// relative — fourteen digits below anything the mesh they are computed on can support.
    /// The same 23 166-DOF column accepted at 1e-5 returns 15 437.12 N against the
    /// 9 310-DOF mesh's 15 437.99 N.</para>
    /// </summary>
    public double Tolerance
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 1e-7;

    /// <summary>Cap on the Krylov dimension of one Lanczos run. Null sizes it from
    /// <see cref="ModeCount"/> as <c>max(3k + 30, 40)</c>, the modal solver's rule.</summary>
    public int? MaxKrylovDimension { get; init; }

    /// <summary>Cap on Lanczos restarts (default 8).</summary>
    public int MaxRestarts
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 8;

    /// <summary>Quadrature rule override for the elastic stiffness, for tests that check the
    /// production rule is exact. Null uses the cheapest exact rule for the element order.</summary>
    internal int? StiffnessQuadratureDegree { get; init; }

    /// <summary>Quadrature rule override for the GEOMETRIC stiffness — the seam an
    /// independent-integrator check needs. Null uses
    /// <see cref="TetQuadrature.ForGeometric"/>.</summary>
    internal int? GeometricQuadratureDegree { get; init; }
}

/// <summary>
/// What a buckling solve did — sizes, what the eigensolver spent, and the worst residual it
/// accepted. A return value, never a log line.
/// </summary>
public sealed record BucklingSolveReport
{
    /// <summary>Nodes in the analysis mesh.</summary>
    public required int NodeCount { get; init; }

    /// <summary>Elements in the analysis mesh.</summary>
    public required int ElementCount { get; init; }

    /// <summary>Linear or quadratic elements.</summary>
    public required ElementOrder Order { get; init; }

    /// <summary>Degrees of freedom actually solved for.</summary>
    public required int FreeDofs { get; init; }

    /// <summary>Stored entries of the reduced elastic stiffness matrix (upper triangle).</summary>
    public required int StiffnessNonZeros { get; init; }

    /// <summary>Stored entries of the reduced geometric stiffness matrix (upper
    /// triangle). Its sparsity pattern is the elastic matrix's, so a materially smaller
    /// number means part of the model carries no prestress at all.</summary>
    public required int GeometricNonZeros { get; init; }

    /// <summary>Stored entries of the Cholesky factor — the fill diagnostic.</summary>
    public required int FactorNonZeros { get; init; }

    /// <summary>The elimination ordering.</summary>
    public required SparseOrdering Ordering { get; init; }

    /// <summary>
    /// The spectral shift, which is <b>always exactly zero</b> and is reported for exactly
    /// that reason. A modal solve chooses a shift; a buckling solve has nothing to choose,
    /// because the substitution <c>theta = 1/lambda</c> has already turned the wanted
    /// eigenvalue into an extreme one and the matrix being factorized is <c>K</c> itself.
    /// See <see cref="BucklingSolver"/> for the argument in full.
    /// </summary>
    public required double Shift { get; init; }

    /// <summary>Total Lanczos steps, each one a back-substitution through the SINGLE
    /// factorization of K.</summary>
    public required int Iterations { get; init; }

    /// <summary>How many times the Krylov space was rebuilt from a fresh start vector.</summary>
    public required int Restarts { get; init; }

    /// <summary>False when the eigensolver hit its caps with fewer modes than asked for —
    /// which on this path also happens legitimately, when the load case simply has fewer
    /// POSITIVE buckling factors than were requested.</summary>
    public required bool Converged { get; init; }

    /// <summary>How many modes came back.</summary>
    public required int ModeCount { get; init; }

    /// <summary>The largest MEASURED residual among the accepted modes.</summary>
    public required double WorstResidual { get; init; }

    /// <summary>Milliseconds spent assembling K and Kg.</summary>
    public required double AssembleMs { get; init; }

    /// <summary>Milliseconds spent in the one factorization.</summary>
    public required double FactorMs { get; init; }

    /// <summary>Milliseconds spent in the eigensolver.</summary>
    public required double EigenMs { get; init; }

    /// <summary>A readable summary.</summary>
    public string ToText() => string.Join(Environment.NewLine,
        $"{ElementCount:N0} {(Order == ElementOrder.Linear ? "linear" : "quadratic")} elements, "
            + $"{NodeCount:N0} nodes, {FreeDofs:N0} free DOF",
        $"K {StiffnessNonZeros:N0} nnz, Kg {GeometricNonZeros:N0} nnz, "
            + $"factor {FactorNonZeros:N0} nnz ({Ordering})",
        $"shift {Shift:G6} (always zero on this path - the metric is K, not Kg)",
        $"{(Converged ? "converged" : "NOT CONVERGED")}: {ModeCount} mode"
            + $"{(ModeCount == 1 ? "" : "s")} from {Iterations} Lanczos steps through ONE "
            + $"factorization{(Restarts > 0 ? $" and {Restarts} restart{(Restarts == 1 ? "" : "s")}" : "")}, "
            + $"worst residual {WorstResidual:E2}",
        $"assemble {AssembleMs:F1} ms, factor {FactorMs:F1} ms, eigen {EigenMs:F1} ms");

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>
/// Linear (eigenvalue) buckling: the multiple of a reference load case at which a structure
/// loses stability, from the geometric stiffness that load case's own stress field produces.
///
/// <para>The eigenproblem is <c>(K + lambda·Kg) phi = 0</c>, i.e.
/// <c>K phi = lambda·(-Kg) phi</c>, where <c>Kg</c> is
/// <see cref="TetElement.GeometricStiffness"/> assembled from
/// <see cref="StructuralResults"/>' own recovered stress — the SAME field the reference
/// solve reports, thermal-strain subtraction included, because it is read through that
/// class's recovery seam rather than recomputed here.</para>
///
/// <para><b>Kg is INDEFINITE, and that changes the eigensolver's shift strategy completely
/// — it does not merely need a different value.</b> The modal solver puts a small NEGATIVE
/// shift under the spectrum so that <c>K - sigma·M = K + |sigma|·M</c> is positive definite
/// and Cholesky-factorable. That works only because <c>M</c> is positive semi-definite:
/// adding a positive multiple of it can only help. A geometric stiffness is indefinite by
/// nature — tension stiffens, compression softens, and one bending prestress field does both
/// at once — so <c>K - sigma·Kg</c> is a positive definite matrix plus a signed multiple of
/// an indefinite one, and NO sign of sigma makes it reliably factorable. Worse, the modal
/// escalation loop is not merely inapplicable: multiplying a failing shift by 100 and
/// retrying pushes <c>K - sigma·Kg</c> further from definiteness rather than towards it, so
/// a solver that reused it would spend its retries getting steadily more wrong before
/// refusing.</para>
///
/// <para><b>The free parameter is not the shift; it is the METRIC.</b> Lanczos needs its
/// inner product to be an inner product — positive definite — and
/// <see cref="LanczosEigen"/> records that <c>A^-1 B</c> is self-adjoint in the A inner
/// product AND in the B inner product whenever A and B are symmetric. A modal solve runs in
/// M's because M is the definite matrix there. Here the definite matrix is <c>K</c>: the
/// reference solve has already established that (its factorization succeeded, and this
/// solver refuses an unrestrained body for exactly the same reason the static solver does).
/// So the iteration runs in the <b>K inner product</b> with the operator
/// <c>K^-1(-Kg)</c>, and the matrix that gets factorized is <c>K</c> itself — literally the
/// static solver's factorization, on every model, with nothing to choose.</para>
///
/// <para><b>And once the metric is K, the shift has no work left to do.</b> Shift-and-invert
/// exists to turn INTERIOR eigenvalues into extreme ones. Here the reciprocal substitution
/// has already done it: the operator <c>K^-1(-Kg)</c> has eigenvalues <c>theta = 1/lambda</c>,
/// so the smallest critical load factor — the only one an engineer wants — is the operator's
/// LARGEST eigenvalue, and extreme eigenvalues are what Lanczos converges to first with no
/// transformation at all. <c>sigma = 0</c> is therefore the answer rather than a fallback,
/// and <see cref="BucklingSolveReport.Shift"/> exists to say so. (ARPACK reaches the same
/// place from the other side: its "buckling mode" uses <c>OP = (A - sigma·M)^-1 A</c> with
/// the metric <c>B = A</c>, and REQUIRES <c>sigma != 0</c> — because at zero that operator is
/// the identity. Inverting the other matrix removes the requirement along with the choice.)</para>
///
/// <para><b>Only POSITIVE load factors are reported, and the walk stops at the first
/// non-positive one.</b> A negative theta is a negative lambda: a factor at which the
/// REVERSED load case buckles, which is a real answer to a different question (a strut that
/// is safe in compression and unstable in tension is not a thing, but a bending prestress
/// routinely gives both signs). The descending-theta walk in <see cref="LanczosEigen"/>
/// therefore stops at the first non-positive value rather than skipping it, which is the same
/// contiguous-prefix rule that makes "the lowest k" mean it — so the positive family comes
/// back in ascending lambda and the two families are never mixed. A load case with no
/// positive factor at all is refused by name rather than reported as an empty list.</para>
///
/// <para><b>The indefiniteness caveat is about the general case, not about a column.</b>
/// Under a uniform uniaxial compression <c>sigma_xx = -s</c> the element integral collapses
/// to <c>u'Kg u = -s·integral(|du/dx|² dV)</c>, which is negative SEMI-definite for every
/// displacement field — so <c>-Kg</c> is positive semi-definite, the pencil is definite, and
/// every load factor is positive. That is why the Euler column cases converge as cleanly as
/// they do; the machinery above is what makes a bending or mixed prestress work too.</para>
/// </summary>
public static class BucklingSolver
{
    /// <summary>
    /// Solves for the lowest positive critical load factors and their buckled shapes.
    /// </summary>
    /// <param name="reference">A completed static solve. It supplies the prestress AND the
    /// model, which is what makes it impossible to stiffen one model with another's stress.</param>
    /// <param name="options">Solve options, or null for the defaults.</param>
    /// <param name="progress">Optional cooperative cancellation and progress; see
    /// <see cref="StructuralSolver.Solve"/> for why the reported fraction is the
    /// factorization's own.</param>
    public static BucklingResults Solve(
        StructuralResults reference, BucklingSolveOptions? options = null,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        options ??= new BucklingSolveOptions();
        var model = reference.Model;
        var mesh = model.Mesh;

        var stiffnessRule = SelectRule(TetQuadrature.For(mesh.Order), options.StiffnessQuadratureDegree);
        var geometricRule = SelectRule(
            TetQuadrature.ForGeometric(mesh.Order), options.GeometricQuadratureDegree);

        FeaGuards.RequireUsableElements(mesh, stiffnessRule, "stiffness");
        StructuralSolver.RequireRestraint(
            model,
            "A buckling analysis linearises about an equilibrium state, and an unrestrained "
            + "body under load has none - the reference solve it needs would not have a unique "
            + "answer either. Add supports.");
        RequireConvergedReference(reference);
        RequirePrestress(reference);

        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        if (freeCount == 0)
            throw new FeaException(
                "Every degree of freedom is restrained; there is nothing that can buckle.");

        var stopwatch = Stopwatch.StartNew();
        var k = FeaAssembly.Reduce(
            FeaAssembly.Stiffness(model, stiffnessRule), reduced, freeCount);
        // -Kg, assembled directly with the sign already applied: the eigenproblem is
        // K phi = lambda·(-Kg) phi, and forming Kg and negating it afterwards would be one
        // more place for a sign to be lost.
        var b = FeaAssembly.Reduce(
            FeaAssembly.Geometric(reference, geometricRule, -1.0), reduced, freeCount);
        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        SparseCholesky factor;
        try
        {
            factor = SparseCholesky.Factorize(k, options.Ordering, progress);
        }
        catch (InvalidOperationException ex)
        {
            throw new FeaException(
                "The elastic stiffness matrix will not factor even though no whole-body rigid "
                + "motion survives the supports, so the model is a mechanism or its elements are "
                + "too ill-conditioned. A buckling solve factorizes K itself - there is no shift "
                + "to fall back on, because the shift a modal solve uses relies on the right-hand "
                + "matrix being positive semi-definite and a geometric stiffness is not."
                + FeaGuards.DescribeElementShape(mesh)
                + $" (Underlying: {ex.Message})", ex);
        }
        double factorMs = stopwatch.Elapsed.TotalMilliseconds;

        int krylov = options.MaxKrylovDimension ?? Math.Max(3 * options.ModeCount + 30, 40);
        stopwatch.Restart();
        // k as the METRIC as well as the left-hand matrix: see the class remarks. The shift
        // is zero, so `factor` really is a factorization of `K - 0·B`.
        var eigen = LanczosEigen.Solve(
            k, b, k, factor, 0.0, [], options.ModeCount,
            options.Tolerance, krylov, options.MaxRestarts, progress);
        double eigenMs = stopwatch.Elapsed.TotalMilliseconds;

        if (eigen.Pairs.Count == 0)
            throw NothingFound(reference, eigen, options.Tolerance);

        var modes = BuildModes(mesh, eigen.Pairs, reduced);

        var report = new BucklingSolveReport
        {
            NodeCount = mesh.NodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            FreeDofs = freeCount,
            StiffnessNonZeros = k.NonZeroCount,
            GeometricNonZeros = b.NonZeroCount,
            FactorNonZeros = factor.FactorNonZeroCount,
            Ordering = options.Ordering,
            Shift = 0.0,
            Iterations = eigen.Iterations,
            Restarts = eigen.Restarts,
            Converged = eigen.Converged,
            ModeCount = modes.Length,
            WorstResidual = eigen.WorstResidual,
            AssembleMs = assembleMs,
            FactorMs = factorMs,
            EigenMs = eigenMs,
        };

        return new BucklingResults(
            reference, modes, model.AppliedForce, reference.StrainEnergy, report);
    }

    /// <summary>
    /// The refusal when the eigensolver locked nothing — and it is TWO refusals, because an
    /// empty result has two unrelated causes and telling them apart is the whole value of the
    /// message.
    ///
    /// <para>If the iteration never saw a positive candidate, the structure genuinely has no
    /// buckling factor in the applied direction: a body entirely in tension is the ordinary
    /// case, and it is stable under this load and every multiple of it. If it DID see one and
    /// could not drive the residual down, the answer is there and the tolerance is the thing
    /// in the way — which on a slender, finely meshed model is not a stalling iteration but a
    /// floor: the residual <c>|K phi - lambda Kg phi|</c> is a total cancellation of two
    /// quantities each computed to a relative accuracy of <c>eps·kappa(K)</c>, so a
    /// conditioning of 1e10 puts the best achievable relative residual near 1e-6 whatever the
    /// eigensolver does. Measured on this project's own column: a 48-element quadratic mesh
    /// at slenderness 69 could not reach 1e-9 while every coarser mesh did.</para>
    /// </summary>
    private static FeaException NothingFound(
        StructuralResults reference, LanczosResult eigen, double tolerance)
    {
        string spent =
            $"in {eigen.Iterations} Lanczos steps across {eigen.Restarts + 1} runs";
        if (eigen.Candidate is { } candidate)
        {
            return new FeaException(
                $"The buckling eigensolver did not converge {spent}. It FOUND a candidate load "
                + $"factor of {candidate.Eigenvalue:G6} and stalled at a measured relative "
                + $"residual of {candidate.Residual:E2}, against a requested "
                + $"{tolerance:E1} - so the answer is there and the tolerance is what is in the "
                + "way. That is usually a FLOOR rather than a stall: the residual is a complete "
                + "cancellation of K·phi against lambda·Kg·phi, each accurate to about "
                + "eps·kappa(K), so a slender or finely meshed model cannot reach an arbitrarily "
                + "small relative residual however long it iterates. Raise "
                + "BucklingSolveOptions.Tolerance to something above the residual quoted here "
                + "(the eigenvalue is accurate to roughly its square over the spectral gap, so a "
                + "1e-6 residual still buys far more precision than the mesh), or raise "
                + "MaxKrylovDimension if the model is genuinely large.");
        }

        return new FeaException(
            $"No POSITIVE buckling load factor was found {spent}, and no positive candidate "
            + "ever appeared - so this is a statement about the structure rather than about the "
            + $"iteration. The reference load case produces stress (strain energy "
            + $"{reference.StrainEnergy:G6}) but no multiple of it makes K + lambda·Kg singular "
            + "in the applied direction, which is what a structure stable under this load and "
            + "everything proportional to it looks like: a body entirely in tension is the "
            + "ordinary case. Reversing the load case is a question this solver does not answer "
            + "for you, because a negative factor is a statement about a load nobody applied.");
    }

    private static TetQuadrature SelectRule(TetQuadrature preferred, int? degree) => degree switch
    {
        null => preferred,
        1 => TetQuadrature.Degree1,
        2 => TetQuadrature.Degree2,
        3 => TetQuadrature.Degree3,
        5 => TetQuadrature.Degree5,
        _ => throw new ArgumentOutOfRangeException(
            nameof(degree), degree, "Rules of degree 1, 2, 3 and 5 are available."),
    };

    /// <summary>
    /// Refuses a reference solve the iterative path did not converge on. Its stress field
    /// would be wrong by whatever the solver gave up at, and a geometric stiffness built from
    /// it would report a plausible load factor for a structure nobody analysed.
    /// </summary>
    private static void RequireConvergedReference(StructuralResults reference)
    {
        if (reference.Report.Converged)
            return;
        throw new FeaException(
            $"The reference static solve did not converge ({reference.Report.Method}, "
            + $"{reference.Report.Iterations:N0} iterations, relative residual "
            + $"{reference.Report.RelativeResidual:E2}), so its stress field is not the "
            + "equilibrium one. A buckling analysis is a statement ABOUT that stress field, so "
            + "it would inherit the error silently as a load factor rather than loudly as a "
            + "residual. Tighten StructuralSolveOptions.Cg, or solve the reference case with "
            + "FeaSolveMethod.Direct.");
    }

    /// <summary>
    /// Refuses a reference solve carrying no stress at all.
    ///
    /// <para><b>The check is the STRAIN ENERGY, not the applied force resultant</b>, and the
    /// distinction is the whole point: the load cases a buckling analysis most often uses are
    /// self-equilibrated (a strut pushed from both ends, an enforced end displacement, a
    /// uniform temperature rise against fixed ends), so their resultant is exactly zero while
    /// their stress field is precisely what buckles the structure. Strain energy is positive
    /// for every load case that stresses the body and zero only for the one that does not.</para>
    /// </summary>
    private static void RequirePrestress(StructuralResults reference)
    {
        // Exact-zero semantic test: a solve with no loads at all produces a displacement
        // field that is identically zero, so its strain energy is exactly zero rather than
        // small. A merely SMALL prestress is a legitimate model with a correspondingly large
        // load factor, and there is nothing here for an epsilon to protect against.
        if (reference.StrainEnergy != 0)
            return;
        throw new FeaException(
            "The reference solve carries no strain energy, so its stress field is zero and the "
            + "geometric stiffness would be the zero matrix - every load factor would be "
            + "infinite. A buckling analysis needs a load case to be a factor OF: apply the "
            + "loads (or the enforced displacement, or the thermal field) to the model, solve it "
            + "statically, and pass those results here.");
    }

    private static BucklingMode[] BuildModes(
        AnalysisMesh mesh, IReadOnlyList<EigenPair> pairs, int[] reduced)
    {
        var modes = new BucklingMode[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var free = pair.Vector;

            // The sign of an eigenvector is arbitrary, so it is PINNED rather than left to
            // whichever start vector the run used - the modal solver's convention verbatim.
            int largest = 0;
            for (int j = 1; j < free.Length; j++)
            {
                if (Math.Abs(free[j]) > Math.Abs(free[largest]))
                    largest = j;
            }
            double sign = free.Length > 0 && free[largest] < 0 ? -1.0 : 1.0;

            var shape = new Vector3d[mesh.NodeCount];
            double peak = 0;
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                double x = Component(free, reduced, node, 0) * sign;
                double y = Component(free, reduced, node, 1) * sign;
                double z = Component(free, reduced, node, 2) * sign;
                var u = new Vector3d(x, y, z);
                shape[node] = u;
                peak = Math.Max(peak, u.Length);
            }

            modes[i] = new BucklingMode(i + 1, pair.Eigenvalue, shape, pair.Residual, peak);
        }
        return modes;
    }

    private static double Component(double[] free, int[] reduced, int node, int axis)
    {
        int r = reduced[3 * node + axis];
        // A restrained degree of freedom does not move in ANY buckling mode - that is what
        // the support means - so it is exactly zero rather than interpolated from anything.
        return r >= 0 ? free[r] : 0;
    }
}
