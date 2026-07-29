using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>How the mass matrix is built.</summary>
public enum MassLumping
{
    /// <summary>
    /// The Galerkin (consistent) mass matrix <c>integral(rho·N_i·N_j dV)</c> — the default.
    /// It is what makes a frequency converge at the element's own order, and it converges
    /// from ABOVE: a consistent-mass finite element model is stiffer than the continuum, so
    /// every computed frequency is an upper bound on the true one.
    /// </summary>
    Consistent,

    /// <summary>
    /// Row-sum lumping, <c>M_ii = rho·integral(N_i dV)</c> — the textbook diagonal mass,
    /// available for LINEAR elements only.
    ///
    /// <para><b>It is refused by name for 10-node elements, because there it is not an
    /// approximation but a wrong one</b>: a quadratic tetrahedron's row sums are
    /// <c>-V/20</c> at every CORNER node, i.e. a negative mass — a node that accelerates
    /// towards a force pushing it away. That is the same surprising integral
    /// <c>TetElement.BodyLoadWeights</c> already documents for a quadratic element's gravity
    /// load, and no sign convention rescues it. Use <see cref="Hrz"/>, which is the standard
    /// answer to exactly this problem.</para>
    ///
    /// <para>For a 4-node tetrahedron the row sums are <c>rho·V/4</c> at every node, all
    /// positive, and identical to what <see cref="Hrz"/> produces — asserted, not
    /// assumed.</para>
    /// </summary>
    RowSum,

    /// <summary>
    /// HRZ (Hinton-Rock-Zienkiewicz) lumping: take the consistent matrix's own DIAGONAL and
    /// scale it so the element's total mass is preserved. Every entry is
    /// <c>rho·integral(N_i² dV)</c>, which is strictly positive for every shape function of
    /// every order, so the scheme has none of <see cref="RowSum"/>'s trouble — and the
    /// rescaling is what keeps the body's mass exact.
    ///
    /// <para>A lumped mass converges from BELOW where <see cref="Consistent"/> converges
    /// from above, so running both brackets the true frequency. That is the reason to offer
    /// it at all; it is not more accurate, it is wrong the other way.</para>
    /// </summary>
    Hrz,
}

/// <summary>Options for <see cref="ModalSolver.Solve(StructuralModel, ModalSolveOptions?)"/>.</summary>
public sealed record ModalSolveOptions
{
    /// <summary>How many ELASTIC modes to extract, lowest first (default 6). Rigid-body
    /// modes are found separately and do not count against it.</summary>
    public int ModeCount
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 6;

    /// <summary>How the mass matrix is built (default <see cref="MassLumping.Consistent"/>).</summary>
    public MassLumping Lumping { get; init; } = MassLumping.Consistent;

    /// <summary>Elimination ordering for the factorization (default
    /// <see cref="SparseOrdering.Amd"/>, for the same reason the static solver defaults to
    /// it — a stiffness matrix is exactly where fill dominates).</summary>
    public SparseOrdering Ordering { get; init; } = SparseOrdering.Amd;

    /// <summary>
    /// The relative residual <c>|K phi - lambda M phi| / (|K phi| + |lambda||M phi|)</c> a
    /// mode must reach before it is accepted (default 1e-9). An eigenvalue is accurate to
    /// roughly the SQUARE of this over the spectral gap, so the default buys frequencies far
    /// tighter than any mesh they are computed on.
    /// </summary>
    public double Tolerance
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 1e-9;

    /// <summary>
    /// Cap on the Krylov dimension of one Lanczos run. Null sizes it from
    /// <see cref="ModeCount"/> as <c>max(3k + 30, 40)</c>, which is the usual rule and has
    /// never been the binding constraint on the cases in this project's tests. Memory is
    /// this many vectors of the free DOF count, so it is the knob to turn down on a very
    /// large model.
    /// </summary>
    public int? MaxKrylovDimension { get; init; }

    /// <summary>Cap on Lanczos restarts (default 8). A restart is what finds the SECOND
    /// member of a degenerate pair, so a model full of symmetry needs a few.</summary>
    public int MaxRestarts
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 8;

    /// <summary>
    /// A completed static solve whose stress field STIFFENS the model — the guitar string,
    /// the spinning blade, the bolted joint. Null (the default) is the ordinary unstressed
    /// modal analysis, and the solve is then bit-for-bit what it always was.
    ///
    /// <para>The eigenproblem becomes <c>(K + s·Kg) phi = lambda M phi</c> with <c>s</c>
    /// <see cref="PrestressScale"/> and <c>Kg</c> the same
    /// <see cref="TetElement.GeometricStiffness"/> <see cref="BucklingSolver"/> uses, from
    /// the same recovered stress field. Tension raises every frequency, compression lowers
    /// them, and the two features meet at the critical load: at
    /// <c>s = </c><see cref="BucklingResults.CriticalLoadFactor"/> the matrix
    /// <c>K + s·Kg</c> is SINGULAR, its null vector is the buckling shape, and the lowest
    /// natural frequency is exactly zero. Past it there is no vibration problem left to
    /// solve, and the factorization refuses by name rather than returning imaginary
    /// frequencies as small positive ones.</para>
    ///
    /// <para>The prestress must have been solved on the SAME
    /// <see cref="AnalysisMesh"/> instance — node indices are what the two assemblies share
    /// — but not necessarily under the same supports, since a part is often preloaded in one
    /// configuration and made to vibrate in another.</para>
    /// </summary>
    public StructuralResults? Prestress { get; init; }

    /// <summary>
    /// Multiplier on <see cref="Prestress"/>' stress field (default 1). A linear solve's
    /// stress is homogeneous of degree one in its loads, so this scales the preload without
    /// re-solving — which is what makes a frequency-versus-load curve one static solve and N
    /// eigen-solves rather than 2N solves.
    /// </summary>
    public double PrestressScale { get; init; } = 1.0;

    /// <summary>Quadrature rule override for the GEOMETRIC stiffness, for the same reason
    /// the other two exist. Null uses <see cref="TetQuadrature.ForGeometric"/>.</summary>
    internal int? GeometricQuadratureDegree { get; init; }

    /// <summary>
    /// Quadrature rule override for the STIFFNESS, for tests that check the production rule
    /// is exact. Null uses the cheapest exact rule for the element order.
    /// </summary>
    internal int? StiffnessQuadratureDegree { get; init; }

    /// <summary>
    /// Quadrature rule override for the MASS matrix — the seam a negative control needs, to
    /// show that a rule which is exact only for the stiffness gives a DIFFERENT mass matrix.
    /// Null uses <see cref="TetQuadrature.ForMass"/>.
    /// </summary>
    internal int? MassQuadratureDegree { get; init; }
}

/// <summary>
/// What a modal solve did — sizes, the shift it used, what the eigensolver spent, and the
/// worst residual it accepted. A return value, never a log line.
/// </summary>
public sealed record ModalSolveReport
{
    /// <summary>Nodes in the analysis mesh.</summary>
    public required int NodeCount { get; init; }

    /// <summary>Elements in the analysis mesh.</summary>
    public required int ElementCount { get; init; }

    /// <summary>Linear or quadratic elements.</summary>
    public required ElementOrder Order { get; init; }

    /// <summary>Degrees of freedom actually solved for.</summary>
    public required int FreeDofs { get; init; }

    /// <summary>How the mass matrix was built.</summary>
    public required MassLumping Lumping { get; init; }

    /// <summary>Stored entries of the reduced stiffness matrix (upper triangle).</summary>
    public required int StiffnessNonZeros { get; init; }

    /// <summary>Stored entries of the reduced mass matrix (upper triangle). Equal to the
    /// free DOF count for a lumped matrix, which is the whole point of one.</summary>
    public required int MassNonZeros { get; init; }

    /// <summary>Stored entries of the Cholesky factor — the fill diagnostic.</summary>
    public required int FactorNonZeros { get; init; }

    /// <summary>The elimination ordering.</summary>
    public required SparseOrdering Ordering { get; init; }

    /// <summary>
    /// The spectral shift sigma the factorization used. Exactly <b>zero</b> when the model
    /// is fully restrained, so the factorization is literally the static solver's; strictly
    /// negative when rigid-body modes survive, because K is then singular and
    /// <c>K - sigma·M = K + |sigma|·M</c> is what restores definiteness.
    /// </summary>
    public required double Shift { get; init; }

    /// <summary>How many rigid-body motions were found and deflated.</summary>
    public required int RigidBodyModeCount { get; init; }

    /// <summary>
    /// The stress-stiffening multiplier that was applied, or <b>exactly zero</b> when the
    /// solve carried no prestress. Reported because a frequency is meaningless without it: a
    /// preloaded model's frequencies are not the part's, they are that load case's.
    /// </summary>
    public double PrestressScale { get; init; }

    /// <summary>
    /// Total Lanczos steps, each one a back-substitution through the SINGLE factorization.
    /// The number that makes the factor-once-solve-many argument concrete: this is how many
    /// solves one factorization served.
    /// </summary>
    public required int Iterations { get; init; }

    /// <summary>How many times the Krylov space was rebuilt from a fresh start vector.</summary>
    public required int Restarts { get; init; }

    /// <summary>False when the eigensolver hit its caps with fewer modes than asked for.</summary>
    public required bool Converged { get; init; }

    /// <summary>How many elastic modes came back.</summary>
    public required int ModeCount { get; init; }

    /// <summary>The largest MEASURED residual among the accepted modes.</summary>
    public required double WorstResidual { get; init; }

    /// <summary>Milliseconds spent assembling K and M.</summary>
    public required double AssembleMs { get; init; }

    /// <summary>Milliseconds spent in the one factorization.</summary>
    public required double FactorMs { get; init; }

    /// <summary>Milliseconds spent in the eigensolver.</summary>
    public required double EigenMs { get; init; }

    /// <summary>A readable summary.</summary>
    public string ToText() => string.Join(Environment.NewLine,
        $"{ElementCount:N0} {(Order == ElementOrder.Linear ? "linear" : "quadratic")} elements, "
            + $"{NodeCount:N0} nodes, {FreeDofs:N0} free DOF",
        $"K {StiffnessNonZeros:N0} nnz, M {MassNonZeros:N0} nnz ({Lumping}), "
            + $"factor {FactorNonZeros:N0} nnz ({Ordering})",
        $"shift {Shift:G6}, {RigidBodyModeCount} rigid-body mode"
            + $"{(RigidBodyModeCount == 1 ? "" : "s")} deflated"
            + (PrestressScale != 0 ? $", stress-stiffened by {PrestressScale:G6}·Kg" : ""),
        $"{(Converged ? "converged" : "NOT CONVERGED")}: {ModeCount} modes from "
            + $"{Iterations} Lanczos steps through ONE factorization"
            + $"{(Restarts > 0 ? $" and {Restarts} restart{(Restarts == 1 ? "" : "s")}" : "")}, "
            + $"worst residual {WorstResidual:E2}",
        $"assemble {AssembleMs:F1} ms, factor {FactorMs:F1} ms, eigen {EigenMs:F1} ms");

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>
/// Natural frequencies and mode shapes: the generalized symmetric eigenproblem
/// <c>K·phi = lambda·M·phi</c> over the same <see cref="AnalysisMesh"/>, the same materials
/// and the same supports a <see cref="StructuralModel"/> already carries.
///
/// <para><b>The mass matrix is the thermal capacity matrix under another name</b> — both are
/// <c>integral(constant·N_i·N_j dV)</c> — so it is
/// <see cref="TetElement.ConsistentMass"/>'s single implementation with <c>rho</c> in place
/// of <c>rho·c</c>, replicated onto the 3x3 identity block per node pair because an
/// isotropic inertia couples no two axes. Crucially it inherits the quadrature rule
/// <see cref="TetQuadrature.ForMass"/>: <b>two degrees above the stiffness's</b>, because
/// <c>N·N</c> is degree 2p where <c>grad N · grad N</c> is 2(p-1). Getting that wrong is
/// silent — the cheap rule gives a 4-node mass matrix whose sixteen entries are all
/// <c>rho·V/16</c>, singular, and yet summing to exactly the right total mass.</para>
///
/// <para><b>This is the case where a direct factorization genuinely amortises.</b>
/// <see cref="FeaSolveMethod.Direct"/> records, honestly, that the usual "factor once, solve
/// many right-hand sides" argument does NOT apply to the static solver, which factors and
/// discards. Here it does: shift-and-invert Lanczos factors <c>K - sigma·M</c> ONCE and
/// spends one back-substitution per Lanczos step —
/// <see cref="ModalSolveReport.Iterations"/> of them, typically forty to a hundred for a
/// handful of modes — so the exact default is also the only practical one. An iterative
/// linear solver is not offered here for that reason: it would have to converge afresh for
/// every one of those steps.</para>
///
/// <para><b>Rigid-body modes are separated, not refused.</b>
/// <see cref="StructuralSolver"/> refuses an unrestrained model because a linear static
/// solve of one has no unique answer; a modal analysis of one is perfectly well posed, and
/// its six zero-frequency modes are part of the answer. The SAME machinery
/// (<see cref="RigidBodyModes"/>) finds and describes them either way — they are deflated
/// out of the Krylov space so they cannot be reported as the first six structural modes, and
/// listed on their own with the Rayleigh quotient each one actually measures.</para>
/// </summary>
public static class ModalSolver
{
    /// <summary>
    /// The shift, as a fraction of the largest diagonal Rayleigh quotient
    /// <c>max_i K_ii/M_ii</c>, used when rigid-body modes make K singular.
    ///
    /// <para><b>It has to be small and it has to be non-zero, for two different
    /// reasons.</b> Non-zero, because <c>K</c> alone cannot be factored when a rigid motion
    /// survives — Cholesky meets a non-positive pivot at whatever column the ordering
    /// happens to reach the null space in. Small, because with the rigid modes deflated the
    /// eigensolver's separation is <c>1/(lambda_i + |sigma|)</c>, which degrades towards a
    /// useless constant as <c>|sigma|</c> grows past the first elastic eigenvalue. A
    /// fraction of a quantity bounded ABOVE by the largest eigenvalue is the scale-free way
    /// to say "small": the diagonal Rayleigh quotients are lower bounds on
    /// <c>lambda_max</c>, so this is a fraction of the spectrum's own top rather than of
    /// anything with units.</para>
    /// </summary>
    private const double ShiftFraction = 1e-8;

    /// <summary>How many times to multiply the shift by <see cref="ShiftEscalation"/> and
    /// retry when the factorization refuses. A shift that is too small for a badly
    /// conditioned model is a real possibility, and escalating beats refusing — but the
    /// shift actually used is REPORTED, because it is part of the answer's provenance.</summary>
    private const int ShiftRetries = 5;

    /// <summary>Factor the shift grows by on each retry.</summary>
    private const double ShiftEscalation = 100.0;

    /// <summary>Solves for the lowest natural frequencies and their mode shapes.</summary>
    /// <param name="model">The model to solve.</param>
    /// <param name="options">Solve options, or null for the defaults.</param>
    /// <param name="progress">Optional cooperative cancellation and progress; see
    /// <see cref="StructuralSolver.Solve"/> for why the reported fraction is the
    /// factorization's own. The Lanczos run after it polls at each restart rather than each
    /// step, since a step is one back-substitution and a restart is where the run can
    /// legitimately be abandoned.</param>
    public static ModalResults Solve(
        StructuralModel model, ModalSolveOptions? options = null, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new ModalSolveOptions();
        var mesh = model.Mesh;

        var stiffnessRule = SelectRule(TetQuadrature.For(mesh.Order), options.StiffnessQuadratureDegree);
        var massRule = SelectRule(TetQuadrature.ForMass(mesh.Order), options.MassQuadratureDegree);

        FeaGuards.RequireUsableElements(mesh, stiffnessRule, "stiffness");
        RequireMass(model);
        RequireHomogeneousSupports(model);
        if (options.Lumping == MassLumping.RowSum && mesh.Order == ElementOrder.Quadratic)
            throw new FeaException(
                "MassLumping.RowSum is not available for 10-node (quadratic) elements. A "
                + "quadratic tetrahedron's row sums are -V/20 at every CORNER node, so the "
                + "lumped matrix would carry a NEGATIVE mass at four nodes out of ten - a node "
                + "that accelerates towards a force pushing it away, which is not an "
                + "approximation but a wrong answer with no sign convention to rescue it. (It "
                + "is the same integral that makes a quadratic element's consistent gravity "
                + "load negative at the corners.) Use MassLumping.Hrz, which scales the "
                + "consistent matrix's own strictly positive diagonal to preserve the mass, "
                + "or MassLumping.Consistent.");

        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        if (freeCount == 0)
            throw new FeaException(
                "Every degree of freedom is restrained; there are no modes to find.");

        var stopwatch = Stopwatch.StartNew();
        var fullStiffness = FeaAssembly.Stiffness(model, stiffnessRule);
        var (fullMass, totalMass) = FeaAssembly.Mass(model, massRule, options.Lumping);
        double prestressScale = 0;
        if (options.Prestress is { } prestress)
        {
            RequireCompatiblePrestress(mesh, prestress);
            prestressScale = options.PrestressScale;
            // Exact-zero semantic test: a scale of exactly zero means "no preload", and
            // adding a zero matrix would still change the summation order. Skipping it keeps
            // the unprestressed answer bit-identical whichever way it was asked for.
            if (prestressScale != 0)
            {
                var geometricRule = SelectRule(
                    TetQuadrature.ForGeometric(mesh.Order), options.GeometricQuadratureDegree);
                fullStiffness = FeaAssembly.Combine(
                    fullStiffness,
                    FeaAssembly.Geometric(prestress, geometricRule, 1.0),
                    prestressScale);
            }
        }
        var k = FeaAssembly.Reduce(fullStiffness, reduced, freeCount);
        var m = FeaAssembly.Reduce(fullMass, reduced, freeCount);
        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        // The rigid motions the supports permit — the SAME computation the static solver
        // refuses on, asked here because they are a legitimate part of the answer.
        var motions = RigidBodyModes.Surviving(mesh, model.RestraintOf);
        var (rigidModes, deflation) = PrepareRigidModes(mesh, motions, reduced, freeCount, k, m);

        double shift = rigidModes.Length == 0 ? 0 : -ShiftFraction * DiagonalScale(k, m);
        stopwatch.Restart();
        var (factor, usedShift) = Factorize(
            k, m, shift, options.Ordering, model, prestressScale, progress);
        double factorMs = stopwatch.Elapsed.TotalMilliseconds;

        int krylov = options.MaxKrylovDimension
            ?? Math.Max(3 * options.ModeCount + 30, 40);
        stopwatch.Restart();
        // m for both the operator's right-hand matrix and the metric: the mass matrix is the
        // positive semi-definite one here, so the iteration runs in ITS inner product. (The
        // buckling solver passes k as the metric instead, for the reason BucklingSolver
        // documents at length — its right-hand matrix is indefinite.)
        var eigen = LanczosEigen.Solve(
            k, m, m, factor, usedShift, deflation, options.ModeCount,
            options.Tolerance, krylov, options.MaxRestarts, progress);
        double eigenMs = stopwatch.Elapsed.TotalMilliseconds;

        if (eigen.Pairs.Count == 0)
            throw new FeaException(
                $"The eigensolver found no modes in {eigen.Iterations} Lanczos steps across "
                + $"{eigen.Restarts + 1} runs at a tolerance of {options.Tolerance:E1}. The free "
                + $"system has {freeCount:N0} degrees of freedom and {rigidModes.Length} rigid-body "
                + "mode(s) were deflated; if that number is unexpectedly large the supports are "
                + "not doing what they were meant to. "
                + (eigen.Candidate is { } candidate
                    ? $"A candidate eigenvalue of {candidate.Eigenvalue:G6} "
                      + $"({Math.Sqrt(Math.Max(0, candidate.Eigenvalue)) / (2 * Math.PI):G6} Hz) "
                      + $"was found but stalled at a measured residual of {candidate.Residual:E2}, "
                      + "so the mode is there and the tolerance is what is in the way: raise "
                      + "ModalSolveOptions.Tolerance above that figure."
                    : "No candidate ever appeared. Raising ModalSolveOptions.Tolerance or "
                      + "MaxKrylovDimension is the next thing to try."));

        var participating = ParticipatingMass(m, reduced, mesh.NodeCount);
        var modes = BuildModes(mesh, eigen.Pairs, reduced, m);

        var report = new ModalSolveReport
        {
            NodeCount = mesh.NodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            FreeDofs = freeCount,
            Lumping = options.Lumping,
            StiffnessNonZeros = k.NonZeroCount,
            MassNonZeros = m.NonZeroCount,
            FactorNonZeros = factor.FactorNonZeroCount,
            Ordering = options.Ordering,
            Shift = usedShift,
            RigidBodyModeCount = rigidModes.Length,
            PrestressScale = prestressScale,
            Iterations = eigen.Iterations,
            Restarts = eigen.Restarts,
            Converged = eigen.Converged,
            ModeCount = modes.Length,
            WorstResidual = eigen.WorstResidual,
            AssembleMs = assembleMs,
            FactorMs = factorMs,
            EigenMs = eigenMs,
        };

        return new ModalResults(model, modes, rigidModes, totalMass, participating, report);
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
    /// Refuses a model whose materials carry no density, by name. Without mass there is no
    /// eigenproblem at all — <c>M</c> would be the zero matrix and every "frequency"
    /// infinite — and the mistake is easy to make, because a static solve of the same model
    /// works perfectly (density only enters a static solve through gravity).
    /// <para>The listing and the unit warning are <see cref="FeaGuards.RequireDensity"/>'s,
    /// shared with the transient solver: only the consequence differs between the two.</para>
    /// </summary>
    private static void RequireMass(StructuralModel model) =>
        FeaGuards.RequireDensity(
            model,
            "Modal analysis needs mass: without it the eigenproblem K phi = lambda M phi has "
            + "a zero right-hand side and every frequency is infinite.");

    /// <summary>
    /// Refuses a non-zero prescribed displacement, by name. A modal analysis linearises
    /// about the supported configuration and its supports are homogeneous by definition: a
    /// prescribed deflection has nowhere to enter <c>K phi = lambda M phi</c>, so accepting
    /// one would silently ignore it.
    /// </summary>
    private static void RequireHomogeneousSupports(StructuralModel model)
    {
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            var restraint = model.RestraintOf(node);
            if (restraint == Dof.None)
                continue;
            var prescribed = model.PrescribedOf(node);
            // Exact-zero test, and deliberately so: Fix() writes exactly 0, so anything
            // non-zero on a restrained axis was prescribed on purpose.
            bool nonZero =
                (restraint.HasFlag(Dof.X) && prescribed.X != 0)
                || (restraint.HasFlag(Dof.Y) && prescribed.Y != 0)
                || (restraint.HasFlag(Dof.Z) && prescribed.Z != 0);
            if (!nonZero)
                continue;

            throw new FeaException(
                $"Node {node} at {model.Mesh.Position(node)} has a prescribed displacement of "
                + $"{prescribed}, and a modal analysis has nowhere to put it: the eigenproblem "
                + "K phi = lambda M phi is homogeneous, so a support either removes a degree of "
                + "freedom or does not, and an enforced deflection would be silently ignored. "
                + "Use Fix(...) for the supports, and a static solve for the enforced "
                + "displacement.");
        }
    }

    /// <summary>
    /// Refuses a prestress solved on a different analysis mesh.
    ///
    /// <para><b>The test is REFERENCE identity of the mesh, not a comparison of node counts
    /// or positions</b>, and deliberately: what the two assemblies share is a node NUMBERING,
    /// and two meshes of the same body with the same node count can number them differently
    /// — which would silently stiffen each element with another element's stress. An
    /// identical-looking wrong answer is exactly the failure mode a count check would let
    /// through.</para>
    /// </summary>
    private static void RequireCompatiblePrestress(AnalysisMesh mesh, StructuralResults prestress)
    {
        if (ReferenceEquals(prestress.Mesh, mesh))
            return;
        throw new FeaException(
            "ModalSolveOptions.Prestress was solved on a different AnalysisMesh instance "
            + $"({prestress.Mesh.NodeCount:N0} nodes, {prestress.Mesh.ElementCount:N0} elements) "
            + $"from the model being solved ({mesh.NodeCount:N0} nodes, "
            + $"{mesh.ElementCount:N0} elements). Stress stiffening adds the prestress's "
            + "geometric stiffness element by element, so the two must share a node numbering "
            + "and not merely a shape: build one AnalysisMesh, solve the preload case on it, "
            + "and pass those results. (The supports may differ - only the mesh may not.)");
    }

    // ---- rigid-body modes and the shift ------------------------------------------------

    /// <summary>
    /// Turns the surviving rigid motions into two different things, and the distinction
    /// matters.
    ///
    /// <para>The REPORTED modes keep each motion exactly as
    /// <see cref="RigidBodyModes"/> described it — a translation along a named direction, a
    /// rotation about a located axis — so the description and the vector agree, and the
    /// Rayleigh quotient measured from it is the honest "how close to zero is this model's
    /// zero" figure. The DEFLATION set is the same vectors M-orthonormalised, which mixes
    /// them: an orthonormal basis of the null space is what the eigensolver needs and is no
    /// longer describable as "the rotation about X". Producing one object for both jobs
    /// would mean either an eigensolver working with a non-orthogonal basis or a report
    /// naming motions nobody asked about.</para>
    /// </summary>
    private static (RigidBodyMode[] Modes, List<double[]> Deflation) PrepareRigidModes(
        AnalysisMesh mesh,
        List<RigidMotion> motions,
        int[] reduced,
        int freeCount,
        PackedSparseMatrix k,
        PackedSparseMatrix m)
    {
        if (motions.Count == 0)
            return ([], []);

        var modes = new RigidBodyMode[motions.Count];
        var deflation = new List<double[]>(motions.Count);
        var scratch = new double[freeCount];
        var kv = new double[freeCount];
        var mv = new double[freeCount];

        for (int i = 0; i < motions.Count; i++)
        {
            var motion = motions[i];
            var free = new double[freeCount];
            double peak = 0;
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                var u = motion.Field[node];
                peak = Math.Max(peak, u.Length);
                for (int axis = 0; axis < 3; axis++)
                {
                    int r = reduced[3 * node + axis];
                    if (r >= 0)
                        free[r] = u[axis];
                }
            }

            // The Rayleigh quotient of the motion AS DESCRIBED — the measurement the report
            // carries. Zero in exact arithmetic; what it reads is the round-off the
            // assembled stiffness carries on an exactly rigid field.
            k.Multiply(free, kv);
            m.Multiply(free, mv);
            double numerator = 0, denominator = 0;
            for (int j = 0; j < freeCount; j++)
            {
                numerator += free[j] * kv[j];
                denominator += free[j] * mv[j];
            }
            double lambda = denominator > 0 ? numerator / denominator : 0;

            var shape = new Vector3d[mesh.NodeCount];
            double inverse = peak > 0 ? 1.0 / peak : 1.0;
            for (int node = 0; node < mesh.NodeCount; node++)
                shape[node] = motion.Field[node] * inverse;
            modes[i] = new RigidBodyMode(motion.Body, motion.Description, lambda, shape);

            // The deflation copy, M-orthonormalised against the ones already accepted.
            var orthonormal = (double[])free.Clone();
            if (LanczosEigen.Purge(m, deflation, orthonormal, scratch))
                deflation.Add(orthonormal);
        }

        return (modes, deflation);
    }

    /// <summary>
    /// <c>max_i K_ii / M_ii</c> over the free degrees of freedom — a cheap LOWER bound on
    /// the largest eigenvalue (each ratio is the Rayleigh quotient of a coordinate vector),
    /// and therefore a legitimate scale for the spectrum in which to express a small shift.
    /// </summary>
    private static double DiagonalScale(PackedSparseMatrix k, PackedSparseMatrix m)
    {
        double best = 0;
        for (int i = 0; i < k.Rows; i++)
        {
            double mass = m[i, i];
            // Exact-zero division guard: a free DOF with no mass cannot contribute a
            // Rayleigh quotient. RequireMass has already refused a massless model, so this
            // is a guard rather than a case.
            if (mass > 0)
                best = Math.Max(best, k[i, i] / mass);
        }
        return best;
    }

    /// <summary>
    /// Factorizes <c>K - sigma·M</c>, escalating the shift if it will not factor, and
    /// reporting the shift it settled on.
    /// </summary>
    private static (SparseCholesky Factor, double Shift) Factorize(
        PackedSparseMatrix k, PackedSparseMatrix m, double shift,
        SparseOrdering ordering, StructuralModel model, double prestressScale,
        ProgressCancel? progress = null)
    {
        Exception? last = null;
        double current = shift;
        for (int attempt = 0; attempt <= ShiftRetries; attempt++)
        {
            var a = current == 0 ? k : FeaAssembly.Combine(k, m, -current);
            try
            {
                return (SparseCholesky.Factorize(a, ordering, progress), current);
            }
            catch (InvalidOperationException ex)
            {
                last = ex;
                // A zero shift means the model was reported fully restrained, so K should be
                // positive definite; there is no shift to escalate from and the honest thing
                // is a refusal naming what a singular stiffness actually means.
                if (current == 0)
                    break;
                current *= ShiftEscalation;
            }
        }

        if (shift == 0)
        {
            // A prestressed model has one more way to lose definiteness, and it is the
            // physically interesting one: past the critical load K + s·Kg is indefinite,
            // which is not an ill-conditioned mesh but a structure that has already buckled.
            // Naming it first is what stops the message sending someone to look at slivers.
            string cause = prestressScale != 0
                ? $"The prestressed stiffness K + {prestressScale:G6}·Kg is not positive "
                  + "definite. The most likely reason is that the preload is at or past the "
                  + "critical buckling load, where K + s·Kg is singular by definition and there "
                  + "is no vibration problem left to solve - run BucklingSolver on the same "
                  + "reference case to find the factor, and stay below it. "
                : "The stiffness matrix is not positive definite even though no rigid-body "
                  + "motion survives the supports, so the model is a mechanism or its elements "
                  + "are too ill-conditioned to factor.";
            throw new FeaException(
                cause
                + FeaGuards.DescribeElementShape(model.Mesh)
                + $" (Underlying: {last?.Message})", last!);
        }

        throw new FeaException(
            $"K - sigma·M would not factor at any shift between {shift:G6} and "
            + $"{shift * Math.Pow(ShiftEscalation, ShiftRetries):G6}. The shift exists to restore "
            + "definiteness when rigid-body modes make K singular, and a model that refuses every "
            + "one of them is ill-conditioned rather than merely unrestrained."
            + FeaGuards.DescribeElementShape(model.Mesh)
            + $" (Underlying: {last?.Message})", last!);
    }

    // ---- results ---------------------------------------------------------------------

    /// <summary><c>iota_d' M_ff iota_d</c> per axis — the mass a modal sum can account
    /// for.</summary>
    private static Vector3d ParticipatingMass(PackedSparseMatrix m, int[] reduced, int nodeCount)
    {
        Span<double> total = stackalloc double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var influence = FeaAssembly.InfluenceVector(m.Rows, reduced, nodeCount, axis);
            var product = m.Multiply(influence);
            double sum = 0;
            for (int i = 0; i < influence.Length; i++)
                sum += influence[i] * product[i];
            total[axis] = sum;
        }
        return new Vector3d(total[0], total[1], total[2]);
    }

    private static VibrationMode[] BuildModes(
        AnalysisMesh mesh, IReadOnlyList<EigenPair> pairs, int[] reduced, PackedSparseMatrix m)
    {
        var influence = new double[3][];
        for (int axis = 0; axis < 3; axis++)
            influence[axis] = FeaAssembly.InfluenceVector(m.Rows, reduced, mesh.NodeCount, axis);
        var massInfluence = new double[3][];
        for (int axis = 0; axis < 3; axis++)
            massInfluence[axis] = m.Multiply(influence[axis]);

        var modes = new VibrationMode[pairs.Count];
        // Hoisted out of the loop (CA2014 - a stackalloc inside one grows the frame every
        // pass), and reused per mode.
        Span<double> gamma = stackalloc double[3];
        for (int i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var free = pair.Vector;

            // The sign of an eigenvector is arbitrary, so it is PINNED rather than left to
            // whichever start vector the run used: the component of largest magnitude is
            // made positive, ties going to the lowest index. A convention for
            // reproducibility, not physics - the negated vector is an equally valid mode.
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

            for (int axis = 0; axis < 3; axis++)
            {
                double sum = 0;
                var mi = massInfluence[axis];
                for (int j = 0; j < free.Length; j++)
                    sum += free[j] * mi[j];
                gamma[axis] = sum * sign;
            }
            var participation = new Vector3d(gamma[0], gamma[1], gamma[2]);

            modes[i] = new VibrationMode(
                i + 1, pair.Eigenvalue, shape, pair.Residual, participation, peak);
        }
        return modes;
    }

    private static double Component(double[] free, int[] reduced, int node, int axis)
    {
        int r = reduced[3 * node + axis];
        // A restrained degree of freedom does not move in ANY mode - that is what the
        // support means - so it is exactly zero rather than interpolated from anything.
        return r >= 0 ? free[r] : 0;
    }
}
