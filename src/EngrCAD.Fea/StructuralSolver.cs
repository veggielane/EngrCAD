using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>Which linear solver a structural solve uses.</summary>
public enum FeaSolveMethod
{
    /// <summary>
    /// Sparse Cholesky with an AMD fill-reducing ordering — the default, and <b>not
    /// because it is faster</b>. Measured on this project's own cantilever (Release,
    /// win-x64, direct and CG interleaved in one sitting), it loses to
    /// <see cref="ConjugateGradient"/> at every size tested with linear elements and the
    /// gap widens sharply with the mesh:
    /// <code>
    ///   free DOF   direct     CG        element order
    ///      2 160     247 ms     122 ms   linear      (CG 2.0x)
    ///      6 552   1 791 ms     461 ms   linear      (CG 3.9x)
    ///     14 688  10 754 ms     705 ms   linear      (CG 15.3x)
    ///     46 800 108 459 ms   2 232 ms   linear      (CG 48.6x)
    ///      2 160      45 ms      74 ms   quadratic   (direct 1.6x)
    ///      6 552     308 ms     354 ms   quadratic   (direct 1.1x)
    ///     14 688   2 688 ms   1 094 ms   quadratic   (CG 2.5x)
    /// </code>
    /// <para>It is the default because it is <b>exact and deterministic</b>, and that is
    /// what this project's verification claims rest on: "the patch test is reproduced to
    /// round-off", strain errors at 1e-13, the two element orders agreeing on strain
    /// energy to twelve digits. None of those statements can be made about an iterative
    /// solve stopped at a relative residual, so a default of CG would make every headline
    /// accuracy claim a statement about an opt-in path. It also reports its fill, which is
    /// the diagnostic that says a mesh is bad.</para>
    /// <para><b>Choose <see cref="ConjugateGradient"/> for a large single solve</b> — the
    /// table above is the rule, and past roughly 15 000 unknowns it is not close. No
    /// automatic size-based switch is offered, deliberately: a crossover measured on one
    /// operator is a measurement of that operator's conditioning (CLAUDE.md records the
    /// same lesson from the opposite direction, where Core's grid Laplacian put the
    /// crossover somewhere else entirely), so baking a threshold taken from one cantilever
    /// into the library would be that mistake with a number attached.</para>
    /// <para>The usual second argument for a direct solver — factor once, solve many
    /// right-hand sides — does <b>not</b> apply here yet, and it would be dishonest to
    /// claim it: <see cref="StructuralSolver.Solve"/> factors and discards, so a second
    /// load case pays for a second factorization. A multi-load-case entry point is filed
    /// in todo.md, and it is what would make the amortisation real.</para>
    /// </summary>
    Direct,

    /// <summary>
    /// Jacobi-preconditioned conjugate gradients: lower memory (no fill), much faster on
    /// large systems (see <see cref="Direct"/> for the measured table), and the only
    /// option when a factorization will not fit. The answer is approximate to
    /// <see cref="CgOptions.RelativeTolerance"/> rather than exact, and both the iteration
    /// count and the achieved residual are reported, so a stalled solve says so instead of
    /// returning a plausible-looking wrong answer.
    /// </summary>
    ConjugateGradient,
}

/// <summary>Options for <see cref="StructuralSolver.Solve(StructuralModel, StructuralSolveOptions?)"/>.</summary>
public sealed record StructuralSolveOptions
{
    /// <summary>Which linear solver (default <see cref="FeaSolveMethod.Direct"/>).</summary>
    public FeaSolveMethod Method { get; init; } = FeaSolveMethod.Direct;

    /// <summary>Elimination ordering for the direct solver (default
    /// <see cref="SparseOrdering.Amd"/> — a stiffness matrix is exactly the case where
    /// fill dominates).</summary>
    public SparseOrdering Ordering { get; init; } = SparseOrdering.Amd;

    /// <summary>Iterative-solver settings (ignored for the direct method).</summary>
    public CgOptions Cg { get; init; } = new();

    /// <summary>
    /// Estimate the stiffness matrix's condition number by power / inverse-power
    /// iteration and report it. Off by default because it costs roughly
    /// <see cref="ConditionIterations"/> extra back-substitutions; on, it is the number
    /// that tells a badly shaped mesh from a badly posed model.
    /// <para><b>Direct solves only.</b> The small end of the spectrum needs inverse
    /// iteration, which needs the factorization, so combining this with
    /// <see cref="FeaSolveMethod.ConjugateGradient"/> is refused rather than quietly
    /// reporting null.</para>
    /// </summary>
    public bool EstimateCondition { get; init; }

    /// <summary>Iterations per end of the spectrum for <see cref="EstimateCondition"/>.
    /// Must be at least 1: at zero, both ends would report the Rayleigh quotient of a
    /// START VECTOR and the ratio would come out near 1 — a plausible-looking wrong
    /// answer, which is the one thing this class exists not to produce.</summary>
    public int ConditionIterations
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 100;

    /// <summary>
    /// Quadrature rule override, for tests that check the production rule is exact.
    /// Null uses the cheapest exact rule for the element order.
    /// </summary>
    internal int? QuadratureDegree { get; init; }
}

/// <summary>
/// What a structural solve did — sizes, timings, the residual, the energy and the
/// equilibrium check. A return value, never a log line: the caller decides what a large
/// residual means, and the solver only refuses to hide it.
/// </summary>
public sealed record FeaSolveReport
{
    /// <summary>Nodes in the analysis mesh.</summary>
    public required int NodeCount { get; init; }

    /// <summary>Elements in the analysis mesh.</summary>
    public required int ElementCount { get; init; }

    /// <summary>Linear or quadratic elements.</summary>
    public required ElementOrder Order { get; init; }

    /// <summary>Total degrees of freedom (3 per node).</summary>
    public required int TotalDofs { get; init; }

    /// <summary>Degrees of freedom actually solved for.</summary>
    public required int FreeDofs { get; init; }

    /// <summary>Degrees of freedom removed by supports.</summary>
    public int ConstrainedDofs => TotalDofs - FreeDofs;

    /// <summary>Stored entries of the reduced stiffness matrix (upper triangle).</summary>
    public required int MatrixNonZeros { get; init; }

    /// <summary>Stored entries of the Cholesky factor, or 0 for an iterative solve — the
    /// fill diagnostic.</summary>
    public required int FactorNonZeros { get; init; }

    /// <summary>Which solver ran.</summary>
    public required FeaSolveMethod Method { get; init; }

    /// <summary>The elimination ordering, for a direct solve.</summary>
    public required SparseOrdering Ordering { get; init; }

    /// <summary>False only for an iterative solve that hit its iteration cap.</summary>
    public required bool Converged { get; init; }

    /// <summary>Iterations, for an iterative solve.</summary>
    public required int Iterations { get; init; }

    /// <summary>‖K·u - f‖ / ‖f‖ over the reduced system — measured after the solve,
    /// whichever method ran.</summary>
    public required double RelativeResidual { get; init; }

    /// <summary>Strain energy ½·u'·K·u, summed element by element.</summary>
    public required double StrainEnergy { get; init; }

    /// <summary>Resultant of the applied loads.</summary>
    public required Vector3d AppliedForce { get; init; }

    /// <summary>Resultant of the support reactions.</summary>
    public required Vector3d ReactionForce { get; init; }

    /// <summary>
    /// ‖applied + reaction‖ over the SUM OF THE MAGNITUDES of the individual applied and
    /// reaction forces — global force equilibrium, which must hold to round-off for ANY
    /// correct model. It is the cheapest end-to-end check there is: an error in the
    /// consistent load weights, in assembly or in the solve all show up here, and none of
    /// them is visible in a displacement plot.
    /// <para>The denominator is deliberately not the resultant: a self-equilibrated load
    /// case, or one driven entirely by prescribed displacement, has both resultants
    /// legitimately at round-off, and dividing one by the other reports 1.0 for a perfect
    /// answer.</para>
    /// </summary>
    public required double EquilibriumResidual { get; init; }

    /// <summary>Milliseconds spent assembling.</summary>
    public required double AssembleMs { get; init; }

    /// <summary>Milliseconds spent factoring (0 for an iterative solve).</summary>
    public required double FactorMs { get; init; }

    /// <summary>Milliseconds spent in the solve itself.</summary>
    public required double SolveMs { get; init; }

    /// <summary>Condition-number estimate when
    /// <see cref="StructuralSolveOptions.EstimateCondition"/> was set — a power-iteration
    /// ESTIMATE, honest about being one, not a bound.</summary>
    public double? ConditionEstimate { get; init; }

    /// <summary>
    /// A note about this solve worth acting on, or null when there is nothing to say.
    /// Appears in <see cref="ToText"/>.
    ///
    /// <para><b>It is a measurement, not a decision.</b> The one case it currently covers
    /// is a direct factorization that dominated its own solve: the text states what THIS
    /// run spent where, cites the benchmark ratio at a comparable size, and names the
    /// trade the alternative carries. Nothing switches — <see cref="FeaSolveMethod"/>
    /// explains at length why an automatic size-based pick would be unfounded — but a
    /// caller who has just waited two minutes should not have to go and read a benchmark
    /// table to find out there was a faster option.</para>
    ///
    /// <para>That asymmetry is what makes a heuristic acceptable here and not there: a
    /// wrong threshold in a default produces a worse answer, while a wrong threshold in an
    /// advisory produces a line of text nobody needed.</para>
    /// </summary>
    public string? Advisory { get; init; }

    /// <summary>A readable summary.</summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            $"{ElementCount:N0} {(Order == ElementOrder.Linear ? "linear" : "quadratic")} elements, "
                + $"{NodeCount:N0} nodes, {FreeDofs:N0} of {TotalDofs:N0} DOF free",
            $"matrix {MatrixNonZeros:N0} nnz"
                + (FactorNonZeros > 0
                    ? $", factor {FactorNonZeros:N0} nnz (fill {(double)FactorNonZeros / Math.Max(1, MatrixNonZeros):F1}x, {Ordering})"
                    : ""),
            $"{Method}: {(Converged ? "converged" : "NOT CONVERGED")}"
                + (Method == FeaSolveMethod.ConjugateGradient ? $" in {Iterations} iterations" : "")
                + $", |Ku-f|/|f| = {RelativeResidual:E2}",
            $"strain energy {StrainEnergy:G6}",
            $"applied {Format(AppliedForce)}, reaction {Format(ReactionForce)}, equilibrium {EquilibriumResidual:E2}",
            $"assemble {AssembleMs:F1} ms, factor {FactorMs:F1} ms, solve {SolveMs:F1} ms",
        };
        if (ConditionEstimate is { } condition)
            lines.Add($"condition number ~ {condition:E2} (power-iteration estimate)");
        if (Advisory is { } advisory)
            lines.Add(advisory);
        return string.Join(Environment.NewLine, lines);
    }

    private static string Format(Vector3d v) => $"({v.X:G4}, {v.Y:G4}, {v.Z:G4})";

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>
/// Assembles and solves a <see cref="StructuralModel"/>: small-strain linear elasticity,
/// 3 displacement degrees of freedom per node, on
/// <see cref="EngrCAD.Core.Solvers.SparseCholesky"/> (AMD-ordered) or
/// <see cref="EngrCAD.Core.Solvers.SparseSymmetricCG"/>.
///
/// <para><b>Supports are eliminated, not penalised.</b> Constrained degrees of freedom are
/// removed from the system rather than given a large diagonal, so the reduced matrix is
/// genuinely positive definite, its conditioning is the model's own, and a prescribed
/// non-zero displacement moves cleanly to the right-hand side as
/// <c>f_free -= K_fc · u_c</c>. A penalty stiffness would have to be chosen relative to
/// the material, and choosing it wrong is invisible in the answer.</para>
///
/// <para><b>An unrestrained body is refused BEFORE the factorization, by name.</b> The
/// six rigid-body modes are built per connected component and restricted to the
/// constrained degrees of freedom; the NULL SPACE of that restriction is exactly the set
/// of motions the supports permit at zero energy, and it comes from a Jacobi
/// eigen-decomposition of the small Gram matrix. Each surviving motion is then unpacked
/// back into a translation and a located axis, so the message says which body and which
/// motions. Letting the factorization discover it instead gives "nonpositive pivot at
/// column 4713", which tells nobody anything.</para>
/// </summary>
public static class StructuralSolver
{
    /// <summary>
    /// Relative eigenvalue floor for the rigid-body null-space test. A Gram matrix's
    /// eigenvalues are SQUARED singular values, so 1e-12 here is a 1e-6 relative singular
    /// value — the floor the sketch-constraint solver arrived at, after 1e-8 proved to sit
    /// below the elimination's own round-off and report impossible ranks.
    /// </summary>
    private const double RankEigenvalueFloor = 1e-12;

    /// <summary>Solves a model and returns displacements, stresses and the report.</summary>
    public static StructuralResults Solve(StructuralModel model, StructuralSolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new StructuralSolveOptions();
        var mesh = model.Mesh;
        if (options.EstimateCondition && options.Method != FeaSolveMethod.Direct)
            throw new FeaException(
                $"EstimateCondition needs the factorization for its inverse iteration, so it is "
                + $"available only with {nameof(FeaSolveMethod.Direct)}; {options.Method} was asked "
                + "for. Returning null there would be a silent no-op on an option the caller set.");

        // ONE rule for the whole solve: the degeneracy guard, the assembly, the reaction
        // pass and the stress recovery all integrate at the same points. Selecting it
        // twice would be two chances for them to disagree, which is exactly the defect the
        // guard exists to catch.
        var rule = SelectRule(mesh.Order, options.QuadratureDegree);

        FeaGuards.RequireUsableElements(mesh, rule, "stiffness");
        RequireRestraint(model);

        int totalDofs = 3 * mesh.NodeCount;
        var reduced = new int[totalDofs];
        int freeCount = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var restraint = model.RestraintOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                bool fixedHere = ((int)restraint & (1 << axis)) != 0;
                reduced[3 * node + axis] = fixedHere ? -1 : freeCount++;
            }
        }
        if (freeCount == 0)
            throw new FeaException(
                "Every degree of freedom is restrained; there is nothing to solve for.");

        var stopwatch = Stopwatch.StartNew();
        var (matrix, rhs) = Assemble(model, reduced, freeCount, rule);
        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        var free = new double[freeCount];
        double factorMs = 0, solveMs = 0;
        bool converged = true;
        int iterations = 0;
        int factorNonZeros = 0;
        double? condition = null;

        if (options.Method == FeaSolveMethod.Direct)
        {
            stopwatch.Restart();
            SparseCholesky factor;
            try
            {
                factor = SparseCholesky.Factorize(matrix, options.Ordering);
            }
            catch (InvalidOperationException ex)
            {
                throw SingularSystem(model, matrix, reduced, ex);
            }
            factorMs = stopwatch.Elapsed.TotalMilliseconds;
            factorNonZeros = factor.FactorNonZeroCount;

            stopwatch.Restart();
            factor.Solve(rhs, free);
            solveMs = stopwatch.Elapsed.TotalMilliseconds;

            if (options.EstimateCondition)
                condition = EstimateCondition(matrix, factor, options.ConditionIterations);
        }
        else
        {
            stopwatch.Restart();
            var report = SparseSymmetricCG.Solve(matrix, rhs, free, options.Cg);
            solveMs = stopwatch.Elapsed.TotalMilliseconds;
            converged = report.Converged;
            iterations = report.Iterations;
        }

        // The full displacement vector: solved values at free DOFs, prescribed at the rest.
        var displacement = new Vector3d[mesh.NodeCount];
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var prescribed = model.PrescribedOf(node);
            double x = reduced[3 * node] >= 0 ? free[reduced[3 * node]] : prescribed.X;
            double y = reduced[3 * node + 1] >= 0 ? free[reduced[3 * node + 1]] : prescribed.Y;
            double z = reduced[3 * node + 2] >= 0 ? free[reduced[3 * node + 2]] : prescribed.Z;
            displacement[node] = new Vector3d(x, y, z);
        }

        var (reactions, strainEnergy) = ReactionsAndEnergy(model, displacement, rule);
        var reactionTotal = Vector3d.Zero;
        // Normalised against the sum of the individual load and reaction MAGNITUDES, not
        // against the resultants. Two support forces that cancel are the normal case (a
        // self-equilibrated load, or a model driven entirely by prescribed displacement,
        // where both resultants are legitimately zero), and dividing by a resultant that
        // is itself round-off reports a relative error of 1 for a perfect answer.
        double loadScale = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            loadScale += model.ForceOf(node).Length;
            var restraint = model.RestraintOf(node);
            if (restraint == Dof.None)
                continue;
            var reaction = new Vector3d(
                restraint.HasFlag(Dof.X) ? reactions[node].X : 0,
                restraint.HasFlag(Dof.Y) ? reactions[node].Y : 0,
                restraint.HasFlag(Dof.Z) ? reactions[node].Z : 0);
            reactionTotal += reaction;
            loadScale += reaction.Length;
        }

        var applied = model.AppliedForce;
        double equilibrium = loadScale > 0 ? (applied + reactionTotal).Length / loadScale : 0;

        var reportOut = new FeaSolveReport
        {
            NodeCount = mesh.NodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            TotalDofs = totalDofs,
            FreeDofs = freeCount,
            MatrixNonZeros = matrix.NonZeroCount,
            FactorNonZeros = factorNonZeros,
            Method = options.Method,
            Ordering = options.Method == FeaSolveMethod.Direct ? options.Ordering : SparseOrdering.Natural,
            Converged = converged,
            Iterations = iterations,
            RelativeResidual = Residual(matrix, free, rhs),
            StrainEnergy = strainEnergy,
            AppliedForce = applied,
            ReactionForce = reactionTotal,
            EquilibriumResidual = equilibrium,
            AssembleMs = assembleMs,
            FactorMs = factorMs,
            SolveMs = solveMs,
            ConditionEstimate = condition,
            Advisory = AdvisoryFor(
                options.Method, freeCount, factorMs, assembleMs + factorMs + solveMs),
        };

        return new StructuralResults(model, displacement, reactions, reportOut);
    }

    /// <summary>
    /// The note for <see cref="FeaSolveReport.Advisory"/>, or null.
    ///
    /// <para>Both conditions are about what THIS RUN measured, not about what a model of
    /// this size is predicted to cost: the factorization has to have taken real time AND
    /// to have dominated the solve it was part of. A system that factors quickly says
    /// nothing however many unknowns it has, which is the property that keeps this from
    /// being a disguised threshold on size.</para>
    ///
    /// <para>The cited ratio is from <c>FeaBenchmark.DirectVersusIterative</c> — one
    /// cantilever, direct and CG interleaved in a single sitting. It is quoted as a
    /// comparison at a stated size and fixture rather than as a prediction for the
    /// caller's model, because a crossover measured on one operator measures that
    /// operator's conditioning; see <see cref="FeaSolveMethod.Direct"/>.</para>
    /// </summary>
    internal static string? AdvisoryFor(
        FeaSolveMethod method, int freeDofs, double factorMs, double totalMs)
    {
        // A DURATION, and therefore deliberately absolute — the epsilon ladder does not
        // reach it. Every relative tier in this codebase exists because a length, an area
        // or a determinant carries the model's scale; wall-clock seconds carry no scale to
        // be relative to, and "the factorization took a fifth of the model's extent" is
        // not a sentence. Two seconds is where a solve stops feeling instant, so it is a
        // judgement about people rather than about geometry.
        const double SlowFactorMs = 2_000;

        // Dimensionless share of the solve, so no tier applies here either.
        const double DominatesShare = 0.8;

        if (method != FeaSolveMethod.Direct || factorMs < SlowFactorMs)
            return null;
        if (!(totalMs > 0) || factorMs / totalMs < DominatesShare)
            return null;

        return
            $"note: the factorization took {factorMs / 1000:F1} s, "
            + $"{factorMs / totalMs:P0} of this solve. On this project's cantilever benchmark "
            + "FeaSolveMethod.ConjugateGradient measured 48.6x faster than Direct at 46 800 free "
            + $"DOF (2.2 s against 108.5 s) and 15.3x at 14 688; this solve has {freeDofs:N0}. "
            + "The trade is an answer accurate to the iterative tolerance instead of exact, and "
            + "no fill diagnostic — see FeaSolveMethod for why the default is not switched for you.";
    }

    private static TetQuadrature SelectRule(ElementOrder order, int? degree) => degree switch
    {
        null => TetQuadrature.For(order),
        1 => TetQuadrature.Degree1,
        2 => TetQuadrature.Degree2,
        3 => TetQuadrature.Degree3,
        5 => TetQuadrature.Degree5,
        _ => throw new ArgumentOutOfRangeException(
            nameof(degree), degree, "Rules of degree 1, 2, 3 and 5 are available."),
    };

    private static (PackedSparseMatrix Matrix, double[] Rhs) Assemble(
        StructuralModel model, int[] reduced, int freeCount, in TetQuadrature rule)
    {
        var mesh = model.Mesh;
        int perElement = mesh.NodesPerElement;
        int elementDofs = 3 * perElement;

        var builder = new SparseMatrixBuilder(freeCount, freeCount);
        var rhs = new double[freeCount];

        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var force = model.ForceOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                int r = reduced[3 * node + axis];
                if (r >= 0)
                    rhs[r] = force[axis];
            }
        }

        var ke = new double[elementDofs * elementDofs];
        var positions = new Vector3d[perElement];
        // The element's reduced DOF indices and its prescribed values, gathered ONCE per
        // element: the inner loops read them up to 900 times each for a 10-node element,
        // and every read was a multiply-add into `reduced` plus a bounds-checked property
        // call into the model.
        var elementReduced = new int[elementDofs];
        var elementPrescribed = new double[elementDofs];
        var materials = ElementMaterials(model);

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
            {
                int node = nodes[i];
                positions[i] = mesh.Position(node);
                var prescribed = model.PrescribedOf(node);
                for (int a = 0; a < 3; a++)
                {
                    elementReduced[3 * i + a] = reduced[3 * node + a];
                    elementPrescribed[3 * i + a] = prescribed[a];
                }
            }
            TetElement.Stiffness(mesh.Order, positions, materials[e], rule, ke);

            for (int i = 0; i < perElement; i++)
            {
                for (int a = 0; a < 3; a++)
                {
                    int ri = elementReduced[3 * i + a];
                    if (ri < 0)
                        continue;
                    int row = (3 * i + a) * elementDofs;
                    for (int j = 0; j < perElement; j++)
                    {
                        for (int b = 0; b < 3; b++)
                        {
                            double v = ke[row + 3 * j + b];
                            // Exact-zero skip, deliberate: a structurally absent entry is
                            // absent from the sparsity pattern too, which is what CSR
                            // means. It decides the pattern, so it is not a tolerance.
                            if (v == 0)
                                continue;
                            int rj = elementReduced[3 * j + b];
                            if (rj < 0)
                            {
                                // Prescribed displacement: the known column moves to the
                                // right-hand side. Zero-valued supports contribute nothing,
                                // which is why the common case costs one multiply.
                                double value = elementPrescribed[3 * j + b];
                                if (value != 0)
                                    rhs[ri] -= v * value;
                            }
                            else if (ri <= rj)
                            {
                                // Ke is symmetric, so exactly one of the ordered pairs
                                // (i,a)-(j,b) and (j,b)-(i,a) satisfies this and the
                                // undirected entry is assembled once, as symmetric-upper
                                // storage requires.
                                builder.Add(ri, rj, v);
                            }
                        }
                    }
                }
            }
        }

        return (builder.ToSymmetricUpper(), rhs);
    }

    /// <summary>Each element's material resolved once, so the assembly and reaction loops
    /// index an array instead of hashing a region id per element.</summary>
    private static Material[] ElementMaterials(StructuralModel model)
    {
        var materials = new Material[model.Mesh.ElementCount];
        for (int e = 0; e < materials.Length; e++)
            materials[e] = model.MaterialOf(e);
        return materials;
    }

    private static (Vector3d[] Reactions, double StrainEnergy) ReactionsAndEnergy(
        StructuralModel model, Vector3d[] displacement, in TetQuadrature rule)
    {
        var mesh = model.Mesh;
        int perElement = mesh.NodesPerElement;
        int elementDofs = 3 * perElement;
        var materials = ElementMaterials(model);

        var internalForce = new Vector3d[mesh.NodeCount];
        var ke = new double[elementDofs * elementDofs];
        var positions = new Vector3d[perElement];
        var ue = new double[elementDofs];
        double energy = 0;

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
            {
                positions[i] = mesh.Position(nodes[i]);
                var u = displacement[nodes[i]];
                ue[3 * i] = u.X;
                ue[3 * i + 1] = u.Y;
                ue[3 * i + 2] = u.Z;
            }
            TetElement.Stiffness(mesh.Order, positions, materials[e], rule, ke);

            for (int i = 0; i < perElement; i++)
            {
                double fx = 0, fy = 0, fz = 0;
                for (int a = 0; a < 3; a++)
                {
                    double sum = 0;
                    int row = (3 * i + a) * elementDofs;
                    for (int c = 0; c < elementDofs; c++)
                        sum += ke[row + c] * ue[c];
                    if (a == 0) fx = sum;
                    else if (a == 1) fy = sum;
                    else fz = sum;
                    energy += 0.5 * sum * ue[3 * i + a];
                }
                internalForce[nodes[i]] += new Vector3d(fx, fy, fz);
            }
        }

        // Reaction = internal force - applied force. At a free DOF this is the residual
        // (zero to solver accuracy); at a constrained one it is what the support carries.
        var reactions = new Vector3d[mesh.NodeCount];
        for (int node = 0; node < mesh.NodeCount; node++)
            reactions[node] = internalForce[node] - model.ForceOf(node);
        return (reactions, energy);
    }

    private static double Residual(PackedSparseMatrix a, double[] x, double[] b)
    {
        var product = new double[b.Length];
        a.Multiply(x, product);
        double numerator = 0, denominator = 0;
        for (int i = 0; i < b.Length; i++)
        {
            double d = product[i] - b[i];
            numerator += d * d;
            denominator += b[i] * b[i];
        }
        numerator = Math.Sqrt(numerator);
        denominator = Math.Sqrt(denominator);
        return denominator > 0 ? numerator / denominator : numerator;
    }

    /// <summary>
    /// Condition-number estimate: power iteration for the largest eigenvalue, inverse
    /// power iteration through the existing factorization for the smallest, Rayleigh
    /// quotients at the end. Deterministic (fixed start vector, fixed iteration count) and
    /// honestly labelled an estimate — it converges slowly when the extreme eigenvalues
    /// are clustered, and it never claims a bound.
    /// </summary>
    private static double EstimateCondition(PackedSparseMatrix a, SparseCholesky factor, int iterations)
    {
        int n = a.Rows;
        var v = new double[n];
        var w = new double[n];
        // A deterministic start, deliberately NOT all-ones: that vector is orthogonal to
        // some eigenvectors of a symmetric operator often enough to matter, and power
        // iteration started orthogonal to the dominant one never finds it.
        for (int i = 0; i < n; i++)
            v[i] = 1.0 + (i % 7) * 0.13 - (i % 3) * 0.21;
        Normalize(v);

        for (int k = 0; k < iterations; k++)
        {
            a.Multiply(v, w);
            if (!Normalize(w))
                break;
            w.CopyTo(v, 0);
        }
        a.Multiply(v, w);
        double lambdaMax = Dot(v, w);

        for (int i = 0; i < n; i++)
            v[i] = 1.0 + (i % 5) * 0.17 - (i % 2) * 0.31;
        Normalize(v);
        for (int k = 0; k < iterations; k++)
        {
            factor.Solve(v, w);
            if (!Normalize(w))
                break;
            w.CopyTo(v, 0);
        }
        a.Multiply(v, w);
        double lambdaMin = Dot(v, w);

        if (!(lambdaMin > 0) || !(lambdaMax > 0))
            return double.PositiveInfinity;
        return lambdaMax / lambdaMin;
    }

    private static bool Normalize(double[] v)
    {
        double norm = Math.Sqrt(Dot(v, v));
        if (!(norm > 0))
            return false;
        for (int i = 0; i < v.Length; i++)
            v[i] /= norm;
        return true;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private static FeaException SingularSystem(
        StructuralModel model, PackedSparseMatrix matrix, int[] reduced, Exception inner)
    {
        // The rigid-body check already passed, so this is a mechanism, an isolated node,
        // or a genuinely degenerate element. Find the DOFs with no stiffness at all — the
        // commonest cause and the one that is cheap to name.
        var dead = new List<string>();
        var inverse = new int[matrix.Rows];
        for (int dof = 0; dof < reduced.Length; dof++)
        {
            if (reduced[dof] >= 0)
                inverse[reduced[dof]] = dof;
        }
        int deadCount = 0;
        for (int r = 0; r < matrix.Rows; r++)
        {
            // Exact-zero test, deliberately: a stiffness matrix's diagonal entry is a
            // strain energy and is strictly positive wherever anything resists motion, so
            // "not positive" is a structural fact rather than a tolerance question.
            if (matrix[r, r] > 0)
                continue;
            deadCount++;
            if (dead.Count >= 8)
                continue;
            int dof = inverse[r];
            dead.Add($"node {dof / 3} {"XYZ"[dof % 3]} at {model.Mesh.Position(dof / 3)}");
        }

        string detail = dead.Count > 0
            ? $" Degrees of freedom with no stiffness: {string.Join("; ", dead)}"
                + (deadCount > dead.Count ? " (and more)" : "") + "."
            : " Every degree of freedom has stiffness, so the cause is either a mechanism"
                + " or a mesh too ill-conditioned to factor.";

        // Element shape is the commonest cause once the restraint check has passed, and a
        // count of near-flat elements is the number that says so. A regular tetrahedron
        // measures 0.1179 on this scale; below about 1e-4 an element's stiffness spans
        // enough orders of magnitude to cost the factorization its definiteness to
        // round-off alone.
        string quality = FeaGuards.DescribeElementShape(model.Mesh);

        return new FeaException(
            "The assembled stiffness matrix is not positive definite, so the model is not "
            + "fully restrained even though no whole-body rigid motion survives its supports."
            + detail + quality
            + $" (Underlying: {inner.Message})", inner);
    }

    /// <summary>
    /// Refuses an under-restrained model by name, per connected body.
    /// <para>The six rigid-body modes of a body are built over its own nodes, normalised,
    /// and restricted to the constrained degrees of freedom. A combination of them that
    /// vanishes on every constrained DOF is a motion the supports permit at zero energy —
    /// exactly a null vector of the stiffness matrix. Those combinations are the null
    /// space of the modes' Gram matrix over the constrained degrees of freedom, taken from
    /// a Jacobi eigen-decomposition.</para>
    /// <para><b>The null space and not merely its dimension.</b> A rank count answers "how
    /// many motions survive" but not "which", and the difference is not cosmetic: an
    /// earlier version reported which candidate modes a pivoted Cholesky had failed to
    /// eliminate, which for a model pinned at a single node named three TRANSLATIONS when
    /// the surviving motions were three ROTATIONS about that node.</para>
    /// <para>Per BODY rather than globally, because a fully fixed part beside a floating
    /// one is singular in a way no whole-model rigid mode describes.</para>
    /// </summary>
    private static void RequireRestraint(StructuralModel model)
    {
        var mesh = model.Mesh;
        var (component, componentCount) = mesh.ConnectedComponents();

        var nodesOf = new List<int>[componentCount];
        for (int c = 0; c < componentCount; c++)
            nodesOf[c] = [];
        for (int v = 0; v < mesh.NodeCount; v++)
            nodesOf[component[v]].Add(v);

        // Hoisted out of the loops below: six-element scratch, reused per component and
        // per node (CA2014 — a stackalloc inside a loop grows the frame every pass).
        Span<double> norm = stackalloc double[6];
        Span<int> candidates = stackalloc int[6];
        Span<Vector3d> value = stackalloc Vector3d[6];

        for (int c = 0; c < componentCount; c++)
        {
            var nodes = nodesOf[c];
            var centroid = Vector3d.Zero;
            foreach (int v in nodes)
                centroid += mesh.Position(v);
            centroid /= nodes.Count;

            // Mode k evaluated at node v: three translations, three rotations about the
            // body's own centroid.
            static Vector3d Mode(int k, Vector3d p, Vector3d centre) => k switch
            {
                0 => Vector3d.UnitX,
                1 => Vector3d.UnitY,
                2 => Vector3d.UnitZ,
                3 => Vector3d.UnitX.Cross(p - centre),
                4 => Vector3d.UnitY.Cross(p - centre),
                _ => Vector3d.UnitZ.Cross(p - centre),
            };

            for (int k = 0; k < 6; k++)
            {
                double sum = 0;
                foreach (int v in nodes)
                    sum += Mode(k, mesh.Position(v), centroid).LengthSquared;
                norm[k] = Math.Sqrt(sum);
            }

            // A mode whose norm is negligible against the largest is no motion at all
            // (a single-node body has no rotations, a collinear one has one degenerate
            // rotation); drop it rather than dividing by it. Relative, not exact-zero,
            // because the norms carry the model's own scale.
            int candidateCount = 0;
            double largest = 0;
            for (int k = 0; k < 6; k++)
                largest = Math.Max(largest, norm[k]);
            for (int k = 0; k < 6; k++)
            {
                if (norm[k] > largest * 1e-12)
                    candidates[candidateCount++] = k;
            }
            if (candidateCount == 0)
                continue;

            var gram = new double[candidateCount * candidateCount];
            foreach (int v in nodes)
            {
                var restraint = model.RestraintOf(v);
                if (restraint == Dof.None)
                    continue;
                var p = mesh.Position(v);
                for (int i = 0; i < candidateCount; i++)
                    value[i] = Mode(candidates[i], p, centroid) / norm[candidates[i]];
                for (int axis = 0; axis < 3; axis++)
                {
                    if (((int)restraint & (1 << axis)) == 0)
                        continue;
                    for (int i = 0; i < candidateCount; i++)
                    {
                        for (int j = 0; j < candidateCount; j++)
                            gram[i * candidateCount + j] += value[i][axis] * value[j][axis];
                    }
                }
            }

            var (eigenvalues, eigenvectors) = JacobiEigen(gram, candidateCount);
            double dominant = eigenvalues.Max();
            var nullVectors = new List<double[]>();
            for (int i = 0; i < candidateCount; i++)
            {
                if (eigenvalues[i] <= dominant * RankEigenvalueFloor)
                {
                    var vector = new double[candidateCount];
                    for (int j = 0; j < candidateCount; j++)
                        vector[j] = eigenvectors[j * candidateCount + i];
                    nullVectors.Add(vector);
                }
            }
            int surviving = nullVectors.Count;
            if (surviving == 0)
                continue;

            // THIS BODY's extent, not the whole model's: the translation-versus-rotation
            // verdict below weighs |w|·extent against |t|, so on a model holding one large
            // part and one small one the whole-model extent would decide the small part's
            // description by the large part's size. Computed once per body rather than
            // once per surviving mode.
            var bodyBounds = Aabb.Empty;
            foreach (int v in nodes)
                bodyBounds = bodyBounds.Union(mesh.Position(v));
            double bodyExtent = bodyBounds.Size.Length;

            // Each null vector IS a surviving motion, so it can be described rather than
            // guessed at: unpack it back into a translation and a rotation about the
            // body's centroid, then name the axis it turns about.
            var names = new List<string>();
            foreach (var vector in nullVectors)
            {
                var translation = Vector3d.Zero;
                var rotation = Vector3d.Zero;
                for (int i = 0; i < candidateCount; i++)
                {
                    int mode = candidates[i];
                    double coefficient = vector[i] / norm[mode];
                    var unit = mode % 3 == 0 ? Vector3d.UnitX
                             : mode % 3 == 1 ? Vector3d.UnitY
                             : Vector3d.UnitZ;
                    if (mode < 3)
                        translation += unit * coefficient;
                    else
                        rotation += unit * coefficient;
                }
                names.Add(DescribeMotion(translation, rotation, centroid, bodyExtent));
            }

            string body = componentCount == 1
                ? "The model"
                : $"Body {c + 1} of {componentCount} ({nodes.Count:N0} nodes near {centroid})";
            throw new FeaException(
                $"{body} is not restrained: {surviving} rigid-body "
                + $"mode{(surviving == 1 ? "" : "s")} survive{(surviving == 1 ? "s" : "")} the supports "
                + $"({string.Join("; ", names)})."
                + $" {model.RestrainedNodeCount:N0} node{(model.RestrainedNodeCount == 1 ? " is" : "s are")} "
                + "restrained in all. A linear static solve of an unrestrained body has no unique "
                + "answer; add supports, or restrain the six rigid-body degrees of freedom "
                + "statically (a 3-2-1 scheme) if the loads are self-equilibrated.");
        }
    }

    /// <summary>
    /// Names a rigid motion given its translation and its rotation about
    /// <paramref name="centre"/>. A pure translation is reported as one; anything with a
    /// rotation is reported as a turn about a located AXIS, since "rotation about Y" is
    /// useless without saying where the axis is.
    /// <para>An axis is a LINE, so which point on it to quote is a choice: this reports
    /// <c>centre + (w x t)/|w|^2</c>, the point where the motion's translation
    /// perpendicular to the axis vanishes — equivalently the axis's closest approach to
    /// the body's own centroid. A model pinned at one node therefore reports axes through
    /// that node only when the node IS the centroid; otherwise it reports the same lines
    /// through a different point on each.</para>
    /// </summary>
    private static string DescribeMotion(
        Vector3d translation, Vector3d rotation, Vector3d centre, double extent)
    {
        // Scale-free comparison: an angle times a length is a length, so the rotation's
        // contribution over the body is |w|·extent and can be weighed against |t|.
        double turning = rotation.Length * extent;
        if (turning <= 1e-9 * (translation.Length + turning))
        {
            var direction = translation.Length > 0 ? translation / translation.Length : Vector3d.UnitX;
            return $"translation along {Format(direction)}";
        }

        var axis = rotation / rotation.Length;
        var point = centre + rotation.Cross(translation) / rotation.LengthSquared;
        double slide = translation.Dot(axis);
        string turn = $"rotation about the axis through {Format(point)} along {Format(axis)}";
        return Math.Abs(slide) > 1e-9 * (Math.Abs(slide) + turning)
            ? turn + " combined with a slide along it"
            : turn;
    }

    private static string Format(Vector3d v) => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})";

    /// <summary>
    /// Eigen-decomposition of a small symmetric matrix by cyclic Jacobi rotations,
    /// returning eigenvalues and the eigenvectors as columns of an n-by-n row-major
    /// matrix. Unconditionally convergent, and used here for its NULL SPACE: the columns
    /// whose eigenvalue is negligible are the surviving rigid motions, which a rank count
    /// alone cannot name.
    /// <para>Core's <c>SymmetricEigen3</c> does the same job for 3x3; this is the same
    /// algorithm at n &lt;= 6 and deliberately local rather than a generalization of a
    /// type whose whole API is three-dimensional.</para>
    /// </summary>
    private static (double[] Values, double[] Vectors) JacobiEigen(double[] a, int n)
    {
        var m = (double[])a.Clone();
        var v = new double[n * n];
        for (int i = 0; i < n; i++)
            v[i * n + i] = 1.0;

        for (int sweep = 0; sweep < 60; sweep++)
        {
            // Converged when the off-diagonal mass is negligible against the diagonal's
            // — a RELATIVE test, the form SymmetricEigen3 uses. An exact-zero test would
            // essentially never fire (round-off keeps a sum of squares positive), leaving
            // the sweep cap as the real termination rule, which the loop does not say.
            double off = 0, diagonal = 0;
            for (int p = 0; p < n; p++)
            {
                diagonal += m[p * n + p] * m[p * n + p];
                for (int q = p + 1; q < n; q++)
                    off += m[p * n + q] * m[p * n + q];
            }
            if (off <= 1e-30 * diagonal)
                break;

            for (int p = 0; p < n; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = m[p * n + q];
                    // Exact-zero semantic test: an entry that is already zero needs no
                    // rotation to annihilate it.
                    if (apq == 0)
                        continue;
                    double theta = (m[q * n + q] - m[p * n + p]) / (2 * apq);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0)
                        t = 1;
                    double cos = 1.0 / Math.Sqrt(t * t + 1);
                    double sin = t * cos;

                    for (int k = 0; k < n; k++)
                    {
                        double akp = m[k * n + p], akq = m[k * n + q];
                        m[k * n + p] = cos * akp - sin * akq;
                        m[k * n + q] = sin * akp + cos * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = m[p * n + k], aqk = m[q * n + k];
                        m[p * n + k] = cos * apk - sin * aqk;
                        m[q * n + k] = sin * apk + cos * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k * n + p], vkq = v[k * n + q];
                        v[k * n + p] = cos * vkp - sin * vkq;
                        v[k * n + q] = sin * vkp + cos * vkq;
                    }
                }
            }
        }

        var values = new double[n];
        for (int i = 0; i < n; i++)
            values[i] = m[i * n + i];
        return (values, v);
    }
}
