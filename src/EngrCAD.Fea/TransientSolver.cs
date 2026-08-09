using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>
/// One spatial load pattern and its own history, for the multi-pattern transient
/// (<see cref="TransientSolveOptions.LoadPatterns"/>): a model that states this pattern's
/// LOADS (its mesh, supports and materials shared with every other pattern and with the
/// operator model) and the scalar law that scales it over time.
/// </summary>
/// <param name="Pattern">The model carrying this pattern's loads — only its loads may differ
/// from the others'.</param>
/// <param name="Factor">The multiplier on this pattern at time <c>t</c> — a constant (gravity),
/// a harmonic drive, a step, a measured trace.</param>
public readonly record struct TransientLoadPattern(StructuralModel Pattern, Func<double, double> Factor);

/// <summary>
/// A time-domain BASE (support) motion: the whole set of supports moving together along one
/// direction over a stated history — a shaker table, a seismic accelerogram, a floor that
/// the structure sits on and that itself moves. It is the transient twin of
/// <c>HarmonicSolveOptions.BaseExcitation</c>, and like it the answer is RELATIVE displacement
/// (measured from the moving support), which is the right quantity for stress because a rigid
/// ground motion carries none.
///
/// <para><b>The natural time-domain input is ACCELERATION, and this states exactly that</b> —
/// a seismic record IS an accelerogram, and the relative-coordinate equation of motion is
/// <c>M·u_rel'' + C·u_rel' + K·u_rel = -M·iota_d·a_g(t)</c>, so the ground acceleration
/// <c>a_g(t)</c> is all the excitation needs. A displacement- or velocity-stated shaker input
/// is the caller's to differentiate to acceleration (or to drive as an absolute prescribed
/// support motion, the alternative formulation design.md §3g weighs and declines): stating
/// velocity or displacement here would make the solver numerically differentiate the law,
/// which introduces a step size the seismic form does not need.</para>
///
/// <para><b>The whole base moves TOGETHER.</b> The influence vector <c>iota_d</c> is a rigid
/// translation along <see cref="Direction"/>, which is the ground motion only when every
/// support shares one motion; supports on independent foundations need a quasi-static
/// response per group, a larger construction, so this takes the uniform case and states the
/// assumption rather than detecting it — the same boundary <c>BaseExcitation</c> draws.</para>
/// </summary>
/// <param name="Direction">The direction the base accelerates along (normalized at use).</param>
/// <param name="GroundAcceleration">The ground acceleration <c>a_g(t)</c> along
/// <paramref name="Direction"/>, in model acceleration units (mm/s²).</param>
public readonly record struct BaseMotion(Vector3d Direction, Func<double, double> GroundAcceleration);


/// <summary>
/// Options for <see cref="TransientSolver.Solve"/>: a constant step, a count, a scheme, a
/// damping model, a load history and an initial state.
///
/// <para><b>The step is constant, and that is a design decision rather than a
/// simplification</b> — the same one <c>ThermalTransientOptions</c> records. It is what lets
/// ONE factorization serve the whole run, because the stepping matrix
/// <c>a0·M + (1+alpha)·a1·C + (1+alpha)·K</c> depends on the step, the scheme and the model
/// and on nothing else. A CONTINUOUSLY varying adaptive step would refactor at every change,
/// which is the whole cost of the method; the supported adaptive form
/// (<see cref="TransientSolver.SolveAdaptive"/>) is a small fixed set of sizes, each factored
/// once and cached.</para>
/// </summary>
public sealed record TransientSolveOptions
{
    /// <summary>A run of <paramref name="steps"/> steps of size
    /// <paramref name="timeStep"/>.</summary>
    public TransientSolveOptions(double timeStep, int steps)
    {
        if (!(timeStep > 0) || double.IsInfinity(timeStep))
            throw new ArgumentOutOfRangeException(
                nameof(timeStep), timeStep, "The time step must be finite and positive.");
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        TimeStep = timeStep;
        Steps = steps;
    }

    /// <summary>The constant time step.</summary>
    public double TimeStep { get; }

    /// <summary>How many steps to take.</summary>
    public int Steps { get; }

    /// <summary>The total time spanned, <c>TimeStep · Steps</c>.</summary>
    public double Duration => TimeStep * Steps;

    /// <summary>Which scheme steps (default
    /// <see cref="TimeIntegration.AverageAcceleration"/>).</summary>
    public TimeIntegration Integration { get; init; } = TimeIntegration.AverageAcceleration;

    /// <summary>
    /// Proportional (Rayleigh) damping <c>C = alpha·M + beta·K</c> stated as a RUN option
    /// (default <see cref="RayleighDamping.None"/>), which composes additively with any
    /// damping the model itself carries.
    ///
    /// <para><b>For this proportional statement the matrix C is never assembled, and the
    /// backlog entry that predicted it would be is wrong on a point worth keeping.</b> A
    /// transient solve was filed as "the ONE consumer that genuinely wants C as a matrix
    /// rather than as per-mode ratios" — but proportional damping means every appearance of
    /// C is either a product <c>C·x = alpha·(M·x) + beta·(K·x)</c>, which is two matrix-vector
    /// products this solver already performs, or a scalar multiple folded into the effective
    /// stiffness, which collects as <c>(...)·M + (...)·K</c>. Forming C would cost a third
    /// sparse matrix with the stiffness's sparsity, and buy an operation that is strictly more
    /// expensive than the two products it replaces (the mass matrix's blocks are scalar
    /// multiples of the identity, so M has far fewer stored entries than K). So the Rayleigh
    /// path assembles no damping matrix, and the common case stays on it bit for bit.</para>
    ///
    /// <para><b>NON-proportional damping — a discrete dashpot, per-region coefficients that
    /// differ — is genuinely a matrix, and this solver now integrates it.</b> The vocabulary
    /// lives on <see cref="StructuralModel"/> (<see cref="StructuralModel.Dashpot(int, Vector3d, double)"/>,
    /// <see cref="StructuralModel.SetDamping(int, RayleighDamping)"/>), because it is
    /// geometry-attached data no run option can carry, and when a model states it the solver
    /// assembles the one damping matrix the project builds (<c>FeaAssembly.Damping</c>) and
    /// folds it into the effective stiffness and every right-hand side exactly as
    /// <see cref="DirectHarmonicSolver"/> does. A model that states no damping still assembles
    /// no matrix, so <see cref="ModalDamping"/>'s statement is unchanged for that case.</para>
    /// </summary>
    public RayleighDamping Damping { get; init; } = RayleighDamping.None;

    /// <summary>
    /// The load history: the multiplier on the model's whole load pattern at time <c>t</c>.
    /// Null is a constant 1, so a model's loads are simply held.
    ///
    /// <para><b>One spatial pattern scaled by one scalar law</b>, which covers a step, an
    /// impulse, a ramp, a harmonic drive and a measured trace. A superposition of patterns
    /// with independent histories — gravity held while a shaker runs — is
    /// <see cref="LoadPatterns"/>, and this single-pattern form is exactly that with a
    /// one-entry list.</para>
    ///
    /// <para><b>A prescribed displacement is not a load and is not scaled by this.</b> It is
    /// held at its stated value for the whole run, which is what a displaced support does;
    /// a support whose motion is a history of its own is base excitation
    /// (<see cref="BaseMotion"/>).</para>
    ///
    /// <para>The value at <c>t = 0</c> is the caller's, and it decides the initial
    /// acceleration. A step load written with <c>g(0) = 1</c> starts the body accelerating at
    /// <c>t = 0</c>; written with <c>g(0) = 0</c> it starts at rest and the first step applies
    /// the load. Both are legitimate readings of "suddenly applied" and neither is imposed.</para>
    /// </summary>
    public Func<double, double>? LoadFactor { get; init; }

    /// <summary>
    /// Several spatial load patterns with INDEPENDENT histories — the archetypal case of gravity
    /// held constant while a shaker runs: <c>f(t) = sum_i g_i(t)·f_i</c>. Null (the default) uses
    /// the single-pattern form (the solve model's own loads scaled by <see cref="LoadFactor"/>).
    ///
    /// <para><b>All patterns share one operator</b> — the same mesh, supports and materials as
    /// the solve model, only the loads differing — so the stiffness is one matrix and the run
    /// factors once, exactly <c>StructuralSolver.SolveAll</c>'s contract. When this is set the
    /// solve model provides only the operator and the initial conditions; its OWN loads and
    /// <see cref="LoadFactor"/> are refused (the loads live on the patterns, and one law spec is
    /// enough), so the total load is the superposition over the patterns and nothing else.</para>
    /// </summary>
    public IReadOnlyList<TransientLoadPattern>? LoadPatterns { get; init; }

    /// <summary>
    /// A time-domain base (support) motion — a shaker or seismic input driving the model
    /// through its supports. Null (the default) leaves the supports still. See
    /// <see cref="BaseMotion"/>: the excitation is the ground acceleration <c>a_g(t)</c>, and
    /// the response is RELATIVE displacement (<see cref="TransientResults.IsRelativeToBase"/>),
    /// which is the right quantity for stress.
    ///
    /// <para><b>It is the relative-coordinate formulation — a load pattern, not a moving
    /// support</b> — because that is the cleaner of the two ways to realize a base motion, and
    /// design.md §3g records the measurement that chose it: an inertial load
    /// <c>-M·iota_d·a_g(t)</c> over fixed supports needs no per-step operator change and gives
    /// the stress-correct relative displacement, where prescribing the absolute support motion
    /// recomputes a correction each step, needs the input double-integrated to displacement,
    /// and gives absolute displacement that is only relative + ground. It COMPOSES with the
    /// model's own loads and with <see cref="LoadPatterns"/> (gravity held while a shaker
    /// runs), since the inertial load is one more superposed pattern.</para>
    /// </summary>
    public BaseMotion? BaseMotion { get; init; }

    /// <summary>
    /// The absolute-formulation seam, for the measurement design.md §3g reports (the relative
    /// form is the public default). When set the supports are PRESCRIBED this full-length
    /// motion at each time <c>t</c> — displacement at every restrained degree of freedom, zero
    /// elsewhere — instead of the base motion being applied as an inertial load, so the solve
    /// returns ABSOLUTE displacement. It is internal because it is a reference the
    /// relative-vs-absolute agreement test drives, and because a general moving-support
    /// capability is a larger feature than base excitation; the two formulations are proved to
    /// agree to round-off through it.
    /// </summary>
    internal Func<double, double[]>? AbsolutePrescribedMotion { get; init; }

    /// <summary>
    /// A per-node initial displacement (null is rest). A prescribed support value wins over
    /// it, the same rule <c>ThermalTransientOptions.InitialField</c> follows and for the same
    /// reason: the support's value is what holds for every <c>t &gt; 0</c>, and the single
    /// instant at <c>t = 0</c> is where the analytic problem puts the discontinuity.
    /// </summary>
    public IReadOnlyList<Vector3d>? InitialDisplacement { get; init; }

    /// <summary>A per-node initial velocity (null is rest). Zero at every restrained degree
    /// of freedom, since a support held at a constant value does not move.</summary>
    public IReadOnlyList<Vector3d>? InitialVelocity { get; init; }

    /// <summary>Store every n-th step (default 1 = every step). Step 0 and the final step are
    /// always stored.</summary>
    public int StoreEvery
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 1;

    /// <summary>Quadrature rule override for the STIFFNESS matrix, for tests that check the
    /// production rule. Null uses the cheapest exact rule for the element order.</summary>
    internal int? StiffnessQuadratureDegree { get; init; }

    /// <summary>Quadrature rule override for the MASS matrix (two degrees above the
    /// stiffness's; see <see cref="TetQuadrature.ForMass"/>).</summary>
    internal int? MassQuadratureDegree { get; init; }
}

/// <summary>
/// The extra settings for <see cref="TransientSolver.SolveAdaptive"/>: a SMALL fixed set of
/// step sizes and a local-error tolerance that chooses among them.
///
/// <para><b>Why a fixed set rather than a continuously varying step</b> — this is the whole
/// design. A constant step lets ONE factorization serve the run, which is the transient
/// solver's performance argument; a step that varies every time it changes refactors the
/// effective stiffness, which is the method's largest cost. So the sizes are DYADIC
/// (<c>TimeStep / 2^L</c> for <c>L</c> in <c>0..Levels-1</c>) and each is factored at most
/// ONCE and cached, so a genuinely multi-scale run — a sharp impact then a long ring-down —
/// spends the fine step only where the local error demands it while paying for at most
/// <see cref="Levels"/> factorizations, not one per step change. The base
/// <see cref="TransientSolveOptions.TimeStep"/> is the COARSEST size and
/// <see cref="TransientSolveOptions.Steps"/> counts coarsest steps, so the total time spanned
/// is the same as a constant run's, and the times land on the finest dyadic grid.</para>
///
/// <para><see cref="Levels"/> == 1 is the constant-step run reproduced bit for bit through the
/// same step arithmetic (asserted), which is what makes the adaptive path a strict extension
/// rather than a second integrator.</para>
/// </summary>
public sealed record TransientAdaptiveOptions
{
    /// <summary>How many step sizes the set holds — the sizes are
    /// <c>TimeStep / 2^L</c> for <c>L</c> in <c>0..Levels-1</c>, so 1 is the constant step and 3
    /// gives <c>{dt, dt/2, dt/4}</c>.</summary>
    public required int Levels
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    }

    /// <summary>
    /// The local displacement-error tolerance a step must meet, in model length units. The
    /// per-step estimate is <c>dt²·|a(n+1) - a(n)|</c> — the third-order local error the
    /// scheme carries, read from the change in acceleration over the step — and a step whose
    /// estimate exceeds this is REJECTED and retaken at the next finer size (until the finest);
    /// a step comfortably under it lets the next step try a coarser size. It is absolute rather
    /// than relative so the meaning does not drift with the response amplitude over a run whose
    /// whole point is that the amplitude changes by orders of magnitude.
    /// </summary>
    public required double Tolerance
    {
        get;
        init
        {
            if (!(value > 0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(
                    nameof(Tolerance), value, "The tolerance must be finite and positive.");
            field = value;
        }
    }

    /// <summary>The fraction of <see cref="Tolerance"/> below which a step is comfortable enough
    /// to try a coarser size next (default 1/8). Smaller keeps the fine step longer, which is
    /// safer and slower.</summary>
    public double CoarsenFraction { get; init; } = 0.125;
}

/// <summary>
/// Direct time integration of <c>M·a + C·v + K·u = f(t)</c> by the Newmark / HHT-alpha
/// family, at a constant step so ONE factorization serves the whole run.
///
/// <para><b>Different in kind from modal superposition, not a slower version of it.</b>
/// <see cref="HarmonicSolver"/> answers "what does this structure do under a steady sine at
/// each of these frequencies" by projecting onto a handful of modes; this answers "what does
/// it do next" for an arbitrary load history, needs no modes at all, and is the route a
/// nonlinear solve would eventually wrap. The price is that it computes every instant
/// whether or not anything is happening, where modal superposition computes a whole
/// frequency at a time.</para>
///
/// <para><b>One factorization for the run, plus one for the initial acceleration.</b> The
/// effective stiffness <c>a0·M + (1+alpha)·a1·C + (1+alpha)·K</c> is constant for a constant
/// step, so it is factored once before the loop and every step is a back-substitution — the
/// amortisation <c>FeaSolveMethod.Direct</c> records as the direct solver's second argument,
/// and the third place in this library where it is genuinely true (after
/// <c>ThermalSolver.SolveTransient</c> and <c>StructuralSolver.SolveAll</c>). The second
/// factorization is the mass matrix, needed because <c>a(0) = M⁻¹(f(0) - C·v(0) - K·u(0))</c>
/// is a solve against <b>M</b> and not against K; it is skipped only when that right-hand
/// side is exactly zero. Assuming <c>a(0) = 0</c> instead is a silent modelling error whose
/// symptom is an unexplained startup transient, so it is never assumed.</para>
///
/// <para><b>An unrestrained body is legal here, and the static solver's refusal does not
/// apply.</b> <c>K</c> alone is singular for a free body, but the effective stiffness carries
/// <c>a0·M</c> with <c>a0 = 1/(beta·dt²) &gt; 0</c> and a consistent mass matrix is positive
/// definite, so the stepping matrix is positive definite whatever the supports do. A free
/// body under a transient load flies away, which is the answer, and this is the same shape of
/// exemption <c>ThermalSolver.SolveTransient</c> makes for an insulated body.</para>
///
/// <para><b>What is refused, by name.</b> Everything here is LINEAR: the stiffness is
/// evaluated once about the undeformed configuration and never updated, so contact, plasticity,
/// large deformation and follower loads are outside it — each of those makes the problem a
/// nonlinear solve wrapping this one, with a residual iteration inside every step, which is a
/// different solver rather than an option on this one. Several load patterns with independent
/// histories are <see cref="TransientSolveOptions.LoadPatterns"/>; base excitation (a support
/// whose motion is a history) is <see cref="TransientSolveOptions.BaseMotion"/>. Explicit
/// integration is refused with its own reason on
/// <see cref="TimeIntegration.CentralDifference"/>.</para>
/// </summary>
public static class TransientSolver
{
    /// <summary>How many steps pass between progress reports. A transient's honest measure of
    /// progress is its step number — genuinely uniform in cost, unlike a factorization's
    /// columns — and the throttle is here for the same reason the factorization's is: a
    /// thousand-step run repainting a bar a thousand times costs more than the arithmetic it
    /// narrates.</summary>
    private const int ProgressSteps = 200;

    /// <summary>
    /// Steps the equation of motion forward at a constant step, returning every stored state.
    /// </summary>
    /// <param name="model">The model: mesh, materials, supports and the spatial load pattern.</param>
    /// <param name="transient">Step, count, scheme, damping, load history and initial state.</param>
    /// <param name="options">Linear-solver settings, or null for the defaults.</param>
    /// <param name="progress">
    /// Optional cooperative cancellation and progress. The reported fraction is the STEP
    /// number, not the factorization's own — a transient factors once and then spends the run
    /// in back-substitutions, so letting the factorization drive the bar would run it to 1
    /// before the first step. The factorizations in front of it report nothing and only poll.
    /// </param>
    public static TransientResults Solve(
        StructuralModel model,
        TransientSolveOptions transient,
        StructuralSolveOptions? options = null,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(transient);
        options ??= new StructuralSolveOptions();
        var mesh = model.Mesh;

        var stiffnessRule = SelectRule(
            TetQuadrature.For(mesh.Order), transient.StiffnessQuadratureDegree);
        var massRule = SelectRule(
            TetQuadrature.ForMass(mesh.Order), transient.MassQuadratureDegree);

        FeaGuards.RequireUsableElements(mesh, stiffnessRule, "stiffness");
        FeaGuards.RequireDensity(
            model,
            "A transient dynamic solve integrates M·a + C·v + K·u = f(t), so a body with no "
            + "mass has no equation to step: M would be the zero matrix and the effective "
            + "stiffness would collapse onto the static one, which is a different question "
            + "(StructuralSolver.Solve answers it).");

        // NOT RequireRestraint: see the class remarks. A free body is a legitimate transient
        // problem because the effective stiffness carries a0·M, which is positive definite on
        // its own, so nothing here is singular. The static solver's refusal exists because
        // K alone is not.

        // Structural (hysteretic) loss factors are refused by name. Viscous damping — dashpots,
        // per-region coefficients — is integrated (it is a real matrix in the time domain), but
        // hysteretic damping is a frequency-domain complex modulus K(1 + i·eta) with NO causal
        // time-domain form: a constant-magnitude, frequency-independent imaginary stiffness
        // cannot be written as any M·a + C·v + K·u the stepper can advance. DirectHarmonicSolver
        // integrates it, where a steady state at each frequency exists.
        if (model.HasLossFactor)
            throw new FeaException(
                $"The model carries a structural loss factor ({model.DampingDescription}), which "
                + "a transient cannot integrate: hysteretic damping i·eta·K is a frequency-domain "
                + "complex modulus with no causal time-domain form, so there is no M·a + C·v + "
                + "K·u to step. DirectHarmonicSolver answers a hysteretically-damped model per "
                + "frequency. For time integration state viscous damping instead — proportional "
                + "on the options, or a dashpot / per-region coefficients on the model.");

        int nodeCount = mesh.NodeCount;
        int totalDofs = 3 * nodeCount;
        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        if (freeCount == 0)
            throw new FeaException(
                "Every degree of freedom is restrained; there is nothing to step.");

        // Base (support) motion: the relative-coordinate formulation, an inertial load
        // -M·iota_d·a_g(t) over fixed supports (see BaseMotion). The absolute alternative — a
        // per-step prescribed support motion — lives on AbsolutePrescribedMotion, the seam the
        // relative-vs-absolute agreement measurement drives; the two are mutually exclusive
        // because a base motion is one formulation or the other, not both.
        Vector3d baseDirection = default;
        bool relativeToBase = false;
        if (transient.BaseMotion is { } baseMotion)
        {
            if (transient.AbsolutePrescribedMotion is not null)
                throw new FeaException(
                    "Both BaseMotion and the absolute prescribed-motion seam were set. A base "
                    + "motion is realized as EITHER the relative inertial load (BaseMotion) or "
                    + "the absolute prescribed support motion, not both at once.");
            ArgumentNullException.ThrowIfNull(
                baseMotion.GroundAcceleration, nameof(BaseMotion.GroundAcceleration));
            double length = baseMotion.Direction.Length;
            if (!(length > 0) || double.IsNaN(length))
                throw new FeaException(
                    "BaseMotion.Direction must be a non-zero vector; it names the direction the "
                    + "whole base accelerates along.");
            baseDirection = baseMotion.Direction / length;
            relativeToBase = true;
        }

        var scheme = transient.Integration;
        double dt = transient.TimeStep;
        double alpha = scheme.Alpha;

        // The Newmark coefficients, computed by the shared rule SolveAdaptive also uses (so a
        // single-size adaptive run is bit-identical), named c0..c5 so that `a` stays the
        // acceleration:
        //   a(n+1) = c0·(u(n+1) - u(n)) - c2·v(n) - c3·a(n)
        //   v(n+1) = c1·(u(n+1) - u(n)) - c4·v(n) - c5·a(n)
        var coefficients = NewmarkCoefficients.For(scheme, dt);
        double c0 = coefficients.C0, c1 = coefficients.C1;

        var stopwatch = Stopwatch.StartNew();
        var fullStiffness = FeaAssembly.Stiffness(model, stiffnessRule);
        // Consistent only, and not a parameter: a lumped mass buys a diagonal matrix, and a
        // diagonal matrix buys nothing here because the effective stiffness carries K and has
        // to be factored whatever M looks like. MassLumping exists for the modal solver, where
        // consistent and lumped BRACKET the truth and the comparison is worth having.
        var (fullMass, _) = FeaAssembly.Mass(model, massRule);

        // The total damping is C = dampA·M + dampB·K + C_model, and the two halves are handled
        // differently ON PURPOSE. The PROPORTIONAL part (the run option) is never assembled:
        // every use is a product dampA·(M·x) + dampB·(K·x) or a scalar folded into the
        // coefficients below, which is the finding TransientSolveOptions.Damping records. The
        // MODEL part — dashpots, per-region coefficients that differ — is non-proportional in
        // general, so it has no product form and is assembled ONCE, the same matrix
        // DirectHarmonicSolver factors. A model that states no damping assembles nothing, so
        // the common Rayleigh path is bit-identical to what it always was.
        double dampA = transient.Damping.Alpha, dampB = transient.Damping.Beta;
        var modelDamping = model.HasDamping
            ? FeaAssembly.Damping(model, stiffnessRule, massRule)
            : null;

        // The relative-coordinate base-motion load, -M·iota_d, where iota_d is the rigid
        // translation along the base direction (that direction at every node). Multiplying the
        // FULL mass matrix by it gives the consistent inertial nodal load; the base motion's
        // own history a_g(t) scales it, so it is one more load pattern. Null unless a base
        // motion is stated, so the incumbent path assembles nothing.
        double[]? baseLoad = null;
        if (relativeToBase)
        {
            var influence = new double[totalDofs];
            for (int node = 0; node < nodeCount; node++)
            {
                influence[3 * node] = baseDirection.X;
                influence[3 * node + 1] = baseDirection.Y;
                influence[3 * node + 2] = baseDirection.Z;
            }
            var massInfluence = fullMass.Multiply(influence);
            baseLoad = new double[totalDofs];
            for (int i = 0; i < totalDofs; i++)
                baseLoad[i] = -massInfluence[i];
        }

        // Aeff = c0·M + (1+alpha)·c1·C + (1+alpha)·K
        //      = [c0 + (1+alpha)·c1·dampA]·M + [(1+alpha)·(1 + c1·dampB)]·K
        //        + (1+alpha)·c1·C_model
        double massCoefficient = c0 + (1.0 + alpha) * c1 * dampA;
        double stiffnessCoefficient = (1.0 + alpha) * (1.0 + c1 * dampB);
        var fullEffective = FeaAssembly.Combine(
            fullStiffness, stiffnessCoefficient, fullMass, massCoefficient);
        if (modelDamping is not null)
        {
            fullEffective = FeaAssembly.Combine(
                fullEffective, 1.0, modelDamping, (1.0 + alpha) * c1);
        }
        bool anyDamping = dampA != 0 || dampB != 0 || modelDamping is not null;

        // The reduced mass is needed for the initial acceleration's own solve; the reduced
        // STIFFNESS is not needed at all, because every use of K here is a full-vector
        // product (the right-hand side terms and the reaction residual) and the reduction
        // would be a whole matrix built to be read by nobody.
        var m = FeaAssembly.Reduce(fullMass, reduced, freeCount);
        var effective = FeaAssembly.Reduce(fullEffective, reduced, freeCount);

        // The prescribed support motion. The incumbent path prescribes ONE constant vector (a
        // held support). The absolute base-motion seam makes it a function of time, so the
        // supports FOLLOW a history — the correction below is then recomputed each step, which
        // is exactly why the constant relative form is the cleaner one (design.md §3g).
        var motion = transient.AbsolutePrescribedMotion;
        var prescribed = motion is null ? PrescribedVector(model) : motion(0);
        if (motion is not null && prescribed.Length != totalDofs)
            throw new FeaException(
                $"AbsolutePrescribedMotion returned {prescribed.Length:N0} values; a prescribed "
                + $"motion is a full-length vector of {totalDofs:N0} degrees of freedom.");
        bool anyPrescribed = motion is not null;
        if (!anyPrescribed)
        {
            foreach (double p in prescribed)
            {
                // Exact-zero semantic test, as StructuralSolver.Assemble uses: "was a value
                // stated" is not a measurement, and a tolerance would decide that a 1e-12 mm
                // support settlement is no settlement.
                if (p != 0)
                {
                    anyPrescribed = true;
                    break;
                }
            }
        }
        // The correction the prescribed columns contribute to every step's right-hand side.
        // Computed against the FULL effective operator so that a time-varying prescribed value
        // is a change of one line rather than a rewrite (the reasoning
        // ThermalSolver.SolveTransient records — and the absolute seam is exactly that change),
        // and skipped entirely when nothing is prescribed so the common case stays bit-clean.
        var prescribedCorrection = anyPrescribed ? fullEffective.Multiply(prescribed) : null;

        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        // ---- the initial state -----------------------------------------------------
        var displacement = InitialVector(
            model, transient.InitialDisplacement, nameof(TransientSolveOptions.InitialDisplacement),
            applyPrescribed: true);
        var velocity = InitialVector(
            model, transient.InitialVelocity, nameof(TransientSolveOptions.InitialVelocity),
            applyPrescribed: false);
        // For the absolute seam the supports START on their history rather than at rest, so
        // seed the constrained degrees of freedom from motion(0). A history that starts from
        // rest (u_c(0) = 0) leaves this bit-identical to a plain fixed run's initial state.
        if (motion is not null)
        {
            for (int node = 0; node < nodeCount; node++)
            {
                var restraint = model.RestraintOf(node);
                for (int axis = 0; axis < 3; axis++)
                    if (((int)restraint & (1 << axis)) != 0)
                        displacement[3 * node + axis] = prescribed[3 * node + axis];
            }
        }

        // One spatial pattern scaled by one law, or several patterns superposed — the total
        // load is sum_i laws[i](t)·loadVectors[i]. The single-pattern list reduces to the
        // incumbent Scale(pattern, factor, .) bit for bit (see ComputeLoad). A base motion adds
        // its inertial load -M·iota_d as one more pattern scaled by a_g(t).
        var (loadVectors, laws) = BuildLoadPatterns(
            model, transient, baseLoad, relativeToBase ? transient.BaseMotion!.Value.GroundAcceleration : null);
        double factor0 = laws[0](0);
        var load = new double[totalDofs];
        var initialLoad = new double[totalDofs];
        ComputeLoad(loadVectors, laws, 0, initialLoad);

        stopwatch.Restart();
        int factorizations = 0;
        var acceleration = InitialAcceleration(
            model, m, fullMass, fullStiffness, modelDamping, reduced, freeCount,
            initialLoad, displacement, velocity, dampA, dampB, options, progress,
            ref factorizations);

        SparseCholesky? factor = null;
        if (options.Method == FeaSolveMethod.Direct)
        {
            try
            {
                // Cancellation only, no fraction: the run's progress is its step count.
                factor = SparseCholesky.Factorize(
                    effective, options.Ordering,
                    progress is null ? null : new ProgressCancel(() => progress.CancelRequested));
            }
            catch (InvalidOperationException ex)
            {
                throw new FeaException(
                    "The effective stiffness would not factor, even though it is the sum of a "
                    + "positive-definite mass term and a positive-semi-definite stiffness. That "
                    + "leaves a degenerate element or a mesh too ill-conditioned to factor as "
                    + "the cause." + FeaGuards.DescribeElementShape(mesh)
                    + $" (Underlying: {ex.Message})", ex);
            }
            factorizations++;
        }
        double factorMs = stopwatch.Elapsed.TotalMilliseconds;

        // ---- the run ---------------------------------------------------------------
        var states = new List<TransientState>();
        var scratch = new Scratch(totalDofs, freeCount);

        Array.Copy(initialLoad, load, totalDofs);
        var initial = BuildState(
            model, 0, 0, factor0, displacement, velocity, acceleration, load,
            fullMass, fullStiffness, modelDamping, dampA, dampB, alpha,
            velocity, displacement, effective, 0, true, options, scratch);
        states.Add(initial);

        double peakEnergy = initial.TotalEnergy;
        double peakDisplacement = initial.MaxDisplacement;
        double peakDisplacementTime = 0;
        double worstResidual = 0;
        double worstEquilibrium = initial.Results.Report.EquilibriumResidual;
        double work = 0, dissipated = 0;
        bool converged = true;

        var nextDisplacement = new double[totalDofs];
        var nextVelocity = new double[totalDofs];
        var nextAcceleration = new double[totalDofs];
        var nextLoad = new double[totalDofs];
        var freeSolution = new double[freeCount];
        var rhs = new double[freeCount];

        stopwatch.Restart();
        for (int step = 1; step <= transient.Steps; step++)
        {
            if (progress is not null)
            {
                progress.ThrowIfCancelled();
                if (step % Math.Max(1, transient.Steps / ProgressSteps) == 0)
                    progress.Report((double)(step - 1) / transient.Steps);
            }

            double time = step * dt;
            // The absolute seam moves the supports each step: the prescribed vector is the
            // history at t(n+1), and its column contribution -Aeff·u_c is recomputed. The
            // constant relative form does neither (motion is null), which is the whole reason
            // it is the cleaner formulation.
            if (motion is not null)
            {
                prescribed = motion(time);
                prescribedCorrection = fullEffective.Multiply(prescribed);
            }
            // HHT evaluates the load at the same weighted instant as the internal forces:
            //   t(n+1+alpha) = (1+alpha)·t(n+1) - alpha·t(n) = t(n+1) + alpha·dt.
            // With alpha = 0 that is t(n+1) exactly, so the Newmark path is untouched.
            double weightedTime = time + alpha * dt;
            // The reported per-state factor is the FIRST pattern's law value (the `Value`/
            // `FailedAt`-stays-first convention the mechanism sweep uses); the actual load is
            // the full superposition.
            double stepFactor = laws[0](weightedTime);
            ComputeLoad(loadVectors, laws, weightedTime, nextLoad);

            // ONE step of the scheme, shared with SolveAdaptive so a single-size adaptive run
            // is bit-identical to this constant-step one (asserted). The coefficients are
            // constant here and vary per level there; nothing else about the step differs.
            var (stepResidual, stepConverged) = NewmarkStep(
                coefficients, factor,
                fullMass, fullStiffness, modelDamping, effective,
                dampA, dampB, alpha, anyDamping, reduced, totalDofs,
                displacement, velocity, acceleration, nextLoad, prescribed, prescribedCorrection,
                nextDisplacement, nextVelocity, nextAcceleration,
                rhs, freeSolution, scratch, options, progress);
            worstResidual = Math.Max(worstResidual, stepResidual);
            converged &= stepConverged;

            // The discrete energy balance, accumulated at the point where the trapezoidal
            // member asserts it holds exactly: the work of the load over the step's own
            // displacement increment, and the dissipation at the mean velocity. See
            // TransientSolveReport.EnergyBalanceResidual for the derivation.
            for (int i = 0; i < totalDofs; i++)
            {
                work += 0.5 * (nextDisplacement[i] - displacement[i]) * (load[i] + nextLoad[i]);
                scratch.W[i] = 0.5 * (velocity[i] + nextVelocity[i]);
            }
            if (anyDamping)
            {
                ApplyDamping(fullMass, fullStiffness, modelDamping, dampA, dampB, scratch.W, scratch, scratch.C1);
                double quadratic = 0;
                for (int i = 0; i < totalDofs; i++)
                    quadratic += scratch.W[i] * scratch.C1[i];
                dissipated += dt * quadratic;
            }

            (displacement, nextDisplacement) = (nextDisplacement, displacement);
            (velocity, nextVelocity) = (nextVelocity, velocity);
            (acceleration, nextAcceleration) = (nextAcceleration, acceleration);
            (load, nextLoad) = (nextLoad, load);

            bool store = step % transient.StoreEvery == 0 || step == transient.Steps;
            if (!store)
                continue;

            var state = BuildState(
                model, step, time, stepFactor, displacement, velocity, acceleration, load,
                fullMass, fullStiffness, modelDamping, dampA, dampB, alpha,
                nextVelocity, nextDisplacement, effective, worstResidual, converged,
                options, scratch);
            states.Add(state);
            peakEnergy = Math.Max(peakEnergy, state.TotalEnergy);
            worstEquilibrium = Math.Max(
                worstEquilibrium, state.Results.Report.EquilibriumResidual);
            if (state.MaxDisplacement > peakDisplacement)
            {
                peakDisplacement = state.MaxDisplacement;
                peakDisplacementTime = time;
            }
        }
        progress?.Report(1);
        double stepMs = stopwatch.Elapsed.TotalMilliseconds;

        double initialEnergy = states[0].TotalEnergy;
        double finalEnergy = states[^1].TotalEnergy;
        double denominator = Math.Abs(work) + Math.Abs(dissipated) + peakEnergy;
        double balance = denominator > 0
            ? Math.Abs(finalEnergy - initialEnergy - work + dissipated) / denominator
            : 0;

        var report = new TransientSolveReport
        {
            NodeCount = nodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            TotalDofs = totalDofs,
            FreeDofs = freeCount,
            TimeStep = dt,
            Steps = transient.Steps,
            Duration = transient.Duration,
            Integration = scheme,
            Damping = DescribeDamping(transient.Damping, model),
            MatrixNonZeros = effective.NonZeroCount,
            FactorNonZeros = factor?.FactorNonZeroCount ?? 0,
            Method = options.Method,
            Ordering = options.Method == FeaSolveMethod.Direct
                ? options.Ordering
                : SparseOrdering.Natural,
            Factorizations = factorizations,
            WorstRelativeResidual = worstResidual,
            Converged = converged,
            InitialEnergy = initialEnergy,
            FinalEnergy = finalEnergy,
            PeakEnergy = peakEnergy,
            WorkDone = work,
            Dissipated = dissipated,
            EnergyBalanceResidual = balance,
            PeakDisplacement = peakDisplacement,
            PeakDisplacementTime = peakDisplacementTime,
            WorstEquilibriumResidual = worstEquilibrium,
            AssembleMs = assembleMs,
            FactorMs = factorMs,
            StepMs = stepMs,
            // The SAME advisory rule the static and thermal solvers use, with the step count
            // in the load-case slot: a transient amortises its one factorization over exactly
            // that many substitutions, which is the comparison the note has to make.
            Advisory = StructuralSolver.AdvisoryFor(
                options.Method, freeCount, factorMs, assembleMs + factorMs + stepMs,
                transient.Steps),
        };

        return new TransientResults(model, [.. states], report)
        {
            IsRelativeToBase = relativeToBase,
            BaseDirection = relativeToBase ? baseDirection : null,
        };
    }

    /// <summary>
    /// Steps the equation of motion with an ADAPTIVE step drawn from a small fixed dyadic set,
    /// so the fine step is spent only where the local error demands it while each size is
    /// factored at most once (see <see cref="TransientAdaptiveOptions"/>). The base
    /// <paramref name="transient"/> carries the scheme, damping, loads, base motion and initial
    /// state exactly as <see cref="Solve"/> reads them; its <c>TimeStep</c> is the COARSEST size
    /// and its <c>Steps</c> counts coarsest steps.
    /// </summary>
    /// <param name="model">The model: mesh, materials, supports and the spatial load pattern.</param>
    /// <param name="transient">Step, count, scheme, damping, load history and initial state; the
    /// step is the coarsest size.</param>
    /// <param name="adaptive">The size-set and the error tolerance that chooses among it.</param>
    /// <param name="options">Linear-solver settings, or null for the defaults.</param>
    /// <param name="progress">Optional cooperative cancellation and progress.</param>
    public static TransientResults SolveAdaptive(
        StructuralModel model,
        TransientSolveOptions transient,
        TransientAdaptiveOptions adaptive,
        StructuralSolveOptions? options = null,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(transient);
        ArgumentNullException.ThrowIfNull(adaptive);
        options ??= new StructuralSolveOptions();

        // Adaptive stepping exists to REUSE a factorization across the steps at one size, so an
        // iterative solve — which factors nothing — has nothing to reuse and is refused rather
        // than silently ignored.
        if (options.Method != FeaSolveMethod.Direct)
            throw new FeaException(
                "SolveAdaptive requires the Direct method: its whole purpose is to factor each "
                + "step size ONCE and reuse it, which an iterative solve has nothing to do.");
        // A moving support prescribes a time-varying correction against the effective operator,
        // which depends on the step size — so a held-support motion and per-size caching pull in
        // opposite directions. The multi-scale case adaptivity is for (an impact then a decay)
        // is force- or base-driven, so v1 refuses prescribed support motion by name.
        if (transient.AbsolutePrescribedMotion is not null)
            throw new FeaException(
                "SolveAdaptive does not take a prescribed support motion; the constant-step "
                + "Solve does. Use a base motion (BaseMotion) or applied loads instead.");

        var mesh = model.Mesh;
        var stiffnessRule = SelectRule(
            TetQuadrature.For(mesh.Order), transient.StiffnessQuadratureDegree);
        var massRule = SelectRule(
            TetQuadrature.ForMass(mesh.Order), transient.MassQuadratureDegree);
        FeaGuards.RequireUsableElements(mesh, stiffnessRule, "stiffness");
        FeaGuards.RequireDensity(
            model,
            "A transient dynamic solve integrates M·a + C·v + K·u = f(t), so a body with no "
            + "mass has no equation to step.");
        if (model.HasLossFactor)
            throw new FeaException(
                $"The model carries a structural loss factor ({model.DampingDescription}), which "
                + "a transient cannot integrate. State viscous damping instead.");

        int nodeCount = mesh.NodeCount;
        int totalDofs = 3 * nodeCount;
        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        if (freeCount == 0)
            throw new FeaException(
                "Every degree of freedom is restrained; there is nothing to step.");

        // A nonzero HELD support is a constant prescribed correction the caching would have to
        // recompute per size; refused for the same reason as a moving support.
        var prescribed = PrescribedVector(model);
        foreach (double p in prescribed)
            if (p != 0)
                throw new FeaException(
                    "SolveAdaptive does not take a prescribed (non-zero) support displacement; "
                    + "the constant-step Solve does.");

        Vector3d baseDirection = default;
        bool relativeToBase = false;
        if (transient.BaseMotion is { } baseMotion)
        {
            ArgumentNullException.ThrowIfNull(
                baseMotion.GroundAcceleration, nameof(BaseMotion.GroundAcceleration));
            double length = baseMotion.Direction.Length;
            if (!(length > 0) || double.IsNaN(length))
                throw new FeaException("BaseMotion.Direction must be a non-zero vector.");
            baseDirection = baseMotion.Direction / length;
            relativeToBase = true;
        }

        var scheme = transient.Integration;
        double dt0 = transient.TimeStep;
        double alpha = scheme.Alpha;
        int levels = adaptive.Levels;
        int coarsestStride = 1 << (levels - 1);      // in FINEST steps
        double h = dt0 / coarsestStride;             // the finest step size
        long stepsFine = (long)transient.Steps * coarsestStride;

        var stopwatch = Stopwatch.StartNew();
        var fullStiffness = FeaAssembly.Stiffness(model, stiffnessRule);
        var (fullMass, _) = FeaAssembly.Mass(model, massRule);
        double dampA = transient.Damping.Alpha, dampB = transient.Damping.Beta;
        var modelDamping = model.HasDamping
            ? FeaAssembly.Damping(model, stiffnessRule, massRule)
            : null;
        bool anyDamping = dampA != 0 || dampB != 0 || modelDamping is not null;

        double[]? baseLoad = null;
        if (relativeToBase)
        {
            var influence = new double[totalDofs];
            for (int node = 0; node < nodeCount; node++)
            {
                influence[3 * node] = baseDirection.X;
                influence[3 * node + 1] = baseDirection.Y;
                influence[3 * node + 2] = baseDirection.Z;
            }
            var massInfluence = fullMass.Multiply(influence);
            baseLoad = new double[totalDofs];
            for (int i = 0; i < totalDofs; i++)
                baseLoad[i] = -massInfluence[i];
        }

        var m = FeaAssembly.Reduce(fullMass, reduced, freeCount);
        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        var displacement = InitialVector(
            model, transient.InitialDisplacement,
            nameof(TransientSolveOptions.InitialDisplacement), applyPrescribed: true);
        var velocity = InitialVector(
            model, transient.InitialVelocity,
            nameof(TransientSolveOptions.InitialVelocity), applyPrescribed: false);

        var (loadVectors, laws) = BuildLoadPatterns(
            model, transient, baseLoad,
            relativeToBase ? transient.BaseMotion!.Value.GroundAcceleration : null);
        double factor0 = laws[0](0);
        var load = new double[totalDofs];
        var initialLoad = new double[totalDofs];
        ComputeLoad(loadVectors, laws, 0, initialLoad);

        stopwatch.Restart();
        int factorizations = 0;
        var acceleration = InitialAcceleration(
            model, m, fullMass, fullStiffness, modelDamping, reduced, freeCount,
            initialLoad, displacement, velocity, dampA, dampB, options, progress,
            ref factorizations);

        // The per-size cache: each level's Newmark coefficients, reduced effective stiffness and
        // its factorization, built the first time a level is used and reused thereafter. This is
        // the whole point — at most `levels` factorizations, never one per step change.
        var cache = new LevelData?[levels];
        LevelData Level(int level)
        {
            if (cache[level] is { } existing)
                return existing;
            double dtL = dt0 / (1 << level);
            var coeffs = NewmarkCoefficients.For(scheme, dtL);
            double massCoefficient = coeffs.C0 + (1.0 + alpha) * coeffs.C1 * dampA;
            double stiffnessCoefficient = (1.0 + alpha) * (1.0 + coeffs.C1 * dampB);
            var effective = FeaAssembly.Combine(
                fullStiffness, stiffnessCoefficient, fullMass, massCoefficient);
            if (modelDamping is not null)
                effective = FeaAssembly.Combine(
                    effective, 1.0, modelDamping, (1.0 + alpha) * coeffs.C1);
            var reducedEffective = FeaAssembly.Reduce(effective, reduced, freeCount);
            SparseCholesky factor;
            try
            {
                factor = SparseCholesky.Factorize(
                    reducedEffective, options.Ordering,
                    progress is null ? null : new ProgressCancel(() => progress.CancelRequested));
            }
            catch (InvalidOperationException ex)
            {
                throw new FeaException(
                    "The effective stiffness would not factor, even though it is a "
                    + "positive-definite mass term plus a positive-semi-definite stiffness."
                    + FeaGuards.DescribeElementShape(mesh) + $" (Underlying: {ex.Message})", ex);
            }
            factorizations++;
            var data = new LevelData(dtL, coeffs, reducedEffective, factor);
            cache[level] = data;
            return data;
        }

        double factorMs = 0;   // measured lazily inside the loop's first touches; kept simple

        // ---- the adaptive run -----------------------------------------------------------
        var states = new List<TransientState>();
        var scratch = new Scratch(totalDofs, freeCount);
        var stepsPerLevel = new int[levels];

        // The first step always tries the coarsest level (see the loop), so building it here is
        // free and gives the initial state's report its matrix; a run that stays coarse then
        // factors exactly once (matching the constant path's count for Levels == 1).
        var initial = BuildState(
            model, 0, 0, factor0, displacement, velocity, acceleration, load,
            fullMass, fullStiffness, modelDamping, dampA, dampB, alpha,
            velocity, displacement, Level(0).Effective, 0, true, options, scratch);
        states.Add(initial);

        double peakEnergy = initial.TotalEnergy;
        double peakDisplacement = initial.MaxDisplacement;
        double peakDisplacementTime = 0;
        double worstResidual = 0;
        double worstEquilibrium = initial.Results.Report.EquilibriumResidual;
        double work = 0, dissipated = 0;
        bool converged = true;

        var nextDisplacement = new double[totalDofs];
        var nextVelocity = new double[totalDofs];
        var nextAcceleration = new double[totalDofs];
        var nextLoad = new double[totalDofs];
        var freeSolution = new double[freeCount];
        var rhs = new double[freeCount];

        long stepIndex = 0;       // position on the finest dyadic grid
        int desiredLevel = 0;     // the level the error controller wants (0 = coarsest)
        int stepNumber = 0;       // accepted steps taken so far
        double coarsenTol = adaptive.Tolerance * adaptive.CoarsenFraction;

        stopwatch.Restart();
        while (stepIndex < stepsFine)
        {
            progress?.ThrowIfCancelled();

            // The grid decides the coarsest step available at this index: a level's stride must
            // divide the index (alignment) and fit before the end. The finest level is always
            // allowed, so this terminates; if the controller wants a coarser step than the grid
            // permits, it takes the coarsest permitted one.
            int level = desiredLevel;
            while (level < levels - 1 && !StepFits(level, levels, stepIndex, stepsFine))
                level++;
            var data = Level(level);
            long stride = 1L << (levels - 1 - level);

            double time = (stepIndex + stride) * h;
            double weightedTime = time + alpha * data.Dt;
            double stepFactor = laws[0](weightedTime);
            ComputeLoad(loadVectors, laws, weightedTime, nextLoad);

            var (stepResidual, stepConverged) = NewmarkStep(
                data.Coefficients, data.Factor,
                fullMass, fullStiffness, modelDamping, data.Effective,
                dampA, dampB, alpha, anyDamping, reduced, totalDofs,
                displacement, velocity, acceleration, nextLoad, prescribed, prescribedCorrection: null,
                nextDisplacement, nextVelocity, nextAcceleration,
                rhs, freeSolution, scratch, options, progress);

            // The local displacement-error estimate: dt²·|a(n+1) - a(n)|, the change in
            // acceleration over the step scaled by dt² — the third-order local error the scheme
            // carries. Over-tolerance and not yet finest: REJECT and retake at the next finer
            // size (no state advances).
            double error = 0;
            for (int i = 0; i < totalDofs; i++)
                error = Math.Max(error, Math.Abs(nextAcceleration[i] - acceleration[i]));
            error *= data.Dt * data.Dt;

            if (error > adaptive.Tolerance && level < levels - 1)
            {
                desiredLevel = level + 1;
                continue;
            }

            worstResidual = Math.Max(worstResidual, stepResidual);
            converged &= stepConverged;

            // Accept: energy accounting, then advance and swap (the constant path's arithmetic).
            for (int i = 0; i < totalDofs; i++)
            {
                work += 0.5 * (nextDisplacement[i] - displacement[i]) * (load[i] + nextLoad[i]);
                scratch.W[i] = 0.5 * (velocity[i] + nextVelocity[i]);
            }
            if (anyDamping)
            {
                ApplyDamping(fullMass, fullStiffness, modelDamping, dampA, dampB, scratch.W, scratch, scratch.C1);
                double quadratic = 0;
                for (int i = 0; i < totalDofs; i++)
                    quadratic += scratch.W[i] * scratch.C1[i];
                dissipated += data.Dt * quadratic;
            }

            (displacement, nextDisplacement) = (nextDisplacement, displacement);
            (velocity, nextVelocity) = (nextVelocity, velocity);
            (acceleration, nextAcceleration) = (nextAcceleration, acceleration);
            (load, nextLoad) = (nextLoad, load);

            stepIndex += stride;
            stepNumber++;
            stepsPerLevel[level]++;

            // The next step's desired size: comfortably under tolerance lets it try one coarser,
            // otherwise it stays at the size the grid just used (already refined if it had to).
            desiredLevel = error < coarsenTol && level > 0 ? level - 1 : level;

            bool store = stepNumber % transient.StoreEvery == 0 || stepIndex == stepsFine;
            if (!store)
                continue;

            var state = BuildState(
                model, stepNumber, time, stepFactor, displacement, velocity, acceleration, load,
                fullMass, fullStiffness, modelDamping, dampA, dampB, alpha,
                nextVelocity, nextDisplacement, data.Effective, worstResidual, converged,
                options, scratch);
            states.Add(state);
            peakEnergy = Math.Max(peakEnergy, state.TotalEnergy);
            worstEquilibrium = Math.Max(
                worstEquilibrium, state.Results.Report.EquilibriumResidual);
            if (state.MaxDisplacement > peakDisplacement)
            {
                peakDisplacement = state.MaxDisplacement;
                peakDisplacementTime = time;
            }
        }
        progress?.Report(1);
        double stepMs = stopwatch.Elapsed.TotalMilliseconds;

        double initialEnergy = states[0].TotalEnergy;
        double finalEnergy = states[^1].TotalEnergy;
        double denominator = Math.Abs(work) + Math.Abs(dissipated) + peakEnergy;
        double balance = denominator > 0
            ? Math.Abs(finalEnergy - initialEnergy - work + dissipated) / denominator
            : 0;

        // The finest effective matrix is the one to report nonzero counts against (a run may
        // never touch the coarsest); pick the finest level actually built.
        var reportLevel = cache.OfType<LevelData>().Last();
        var report = new TransientSolveReport
        {
            NodeCount = nodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            TotalDofs = totalDofs,
            FreeDofs = freeCount,
            TimeStep = h,
            Steps = stepNumber,
            Duration = transient.Duration,
            Integration = scheme,
            Damping = DescribeDamping(transient.Damping, model),
            MatrixNonZeros = reportLevel.Effective.NonZeroCount,
            FactorNonZeros = reportLevel.Factor.FactorNonZeroCount,
            Method = options.Method,
            Ordering = options.Ordering,
            Factorizations = factorizations,
            AdaptiveSteps = stepNumber,
            StepsPerLevel = stepsPerLevel,
            WorstRelativeResidual = worstResidual,
            Converged = converged,
            InitialEnergy = initialEnergy,
            FinalEnergy = finalEnergy,
            PeakEnergy = peakEnergy,
            WorkDone = work,
            Dissipated = dissipated,
            EnergyBalanceResidual = balance,
            PeakDisplacement = peakDisplacement,
            PeakDisplacementTime = peakDisplacementTime,
            WorstEquilibriumResidual = worstEquilibrium,
            AssembleMs = assembleMs,
            FactorMs = factorMs,
            StepMs = stepMs,
            Advisory = StructuralSolver.AdvisoryFor(
                options.Method, freeCount, factorMs, assembleMs + factorMs + stepMs, stepNumber),
        };

        return new TransientResults(model, [.. states], report)
        {
            IsRelativeToBase = relativeToBase,
            BaseDirection = relativeToBase ? baseDirection : null,
        };
    }

    /// <summary>Whether a step at <paramref name="level"/> aligns with the dyadic grid and fits
    /// before the end — its stride must divide the current index and not overshoot.</summary>
    private static bool StepFits(int level, int levels, long stepIndex, long stepsFine)
    {
        long stride = 1L << (levels - 1 - level);
        return stepIndex % stride == 0 && stepIndex + stride <= stepsFine;
    }

    /// <summary>One size level's cached Newmark coefficients, reduced effective stiffness and
    /// its factorization.</summary>
    private sealed record LevelData(
        double Dt, NewmarkCoefficients Coefficients,
        PackedSparseMatrix Effective, SparseCholesky Factor);

    // ---- the initial state -----------------------------------------------------------

    /// <summary>
    /// <c>a(0) = M⁻¹·(f(0) - C·v(0) - K·u(0))</c>, over the free degrees of freedom.
    ///
    /// <para><b>This is a solve against M, not against K, and skipping it is a real modelling
    /// error rather than a shortcut.</b> A body released from a displaced position, or one
    /// whose load is already on at <c>t = 0</c>, is accelerating at that instant; starting the
    /// integration from <c>a(0) = 0</c> puts a spurious first half-step into the answer and
    /// the symptom — a startup wobble that decays — looks exactly like physics. The mass
    /// matrix is positive definite, so this always factors.</para>
    ///
    /// <para>It is skipped only when the right-hand side is EXACTLY zero, which is the honest
    /// reading of a body at rest with nothing applied: no arithmetic can produce a non-zero
    /// acceleration from it, so the factorization would be paid for a vector of zeros.</para>
    /// </summary>
    private static double[] InitialAcceleration(
        StructuralModel model,
        PackedSparseMatrix reducedMass,
        PackedSparseMatrix fullMass, PackedSparseMatrix fullStiffness,
        PackedSparseMatrix? modelDamping,
        int[] reduced, int freeCount,
        double[] initialLoad, double[] displacement, double[] velocity,
        double dampA, double dampB,
        StructuralSolveOptions options, ProgressCancel? progress,
        ref int factorizations)
    {
        int totalDofs = 3 * model.Mesh.NodeCount;
        var acceleration = new double[totalDofs];

        var stiffProduct = fullStiffness.Multiply(displacement);
        double[]? massVelocity = null, stiffVelocity = null, modelVelocity = null;
        if (dampA != 0)
            massVelocity = fullMass.Multiply(velocity);
        if (dampB != 0)
            stiffVelocity = fullStiffness.Multiply(velocity);
        if (modelDamping is not null)
            modelVelocity = modelDamping.Multiply(velocity);

        var rhs = new double[freeCount];
        bool anything = false;
        for (int dof = 0; dof < totalDofs; dof++)
        {
            int r = reduced[dof];
            if (r < 0)
                continue;
            double value = initialLoad[dof] - stiffProduct[dof];
            if (massVelocity is not null)
                value -= dampA * massVelocity[dof];
            if (stiffVelocity is not null)
                value -= dampB * stiffVelocity[dof];
            if (modelVelocity is not null)
                value -= modelVelocity[dof];
            rhs[r] = value;
            // Exact-zero test: the acceleration is a linear image of this vector, so an
            // exactly zero right-hand side has an exactly zero solution and the factorization
            // has nothing to compute.
            if (value != 0)
                anything = true;
        }
        if (!anything)
            return acceleration;

        var free = new double[freeCount];
        if (options.Method == FeaSolveMethod.Direct)
        {
            SparseCholesky massFactor;
            try
            {
                massFactor = SparseCholesky.Factorize(
                    reducedMass, options.Ordering,
                    progress is null ? null : new ProgressCancel(() => progress.CancelRequested));
            }
            catch (InvalidOperationException ex)
            {
                throw new FeaException(
                    "The consistent mass matrix would not factor, which it must: it is "
                    + "positive definite for any mesh of positively oriented elements with a "
                    + "positive density. The initial acceleration a(0) = M-inverse·(f(0) - "
                    + "C·v(0) - K·u(0)) is a solve against M rather than against K, so a "
                    + "degenerate element shows up here even though the stiffness assembled."
                    + FeaGuards.DescribeElementShape(model.Mesh)
                    + $" (Underlying: {ex.Message})", ex);
            }
            factorizations++;
            massFactor.Solve(rhs, free);
        }
        else
        {
            SparseSymmetricCG.Solve(reducedMass, rhs, free, options.Cg, progress);
        }

        for (int dof = 0; dof < totalDofs; dof++)
        {
            int r = reduced[dof];
            if (r >= 0)
                acceleration[dof] = free[r];
        }
        return acceleration;
    }

    private static double[] InitialVector(
        StructuralModel model, IReadOnlyList<Vector3d>? stated, string name, bool applyPrescribed)
    {
        int nodeCount = model.Mesh.NodeCount;
        var values = new double[3 * nodeCount];
        if (stated is not null)
        {
            if (stated.Count != nodeCount)
                throw new FeaException(
                    $"{name} has {stated.Count:N0} values but the analysis mesh has "
                    + $"{nodeCount:N0} nodes. An initial state is per NODE of the analysis "
                    + "mesh, which for quadratic elements includes the mid-edge nodes.");
            for (int node = 0; node < nodeCount; node++)
            {
                var v = stated[node];
                values[3 * node] = v.X;
                values[3 * node + 1] = v.Y;
                values[3 * node + 2] = v.Z;
            }
        }

        for (int node = 0; node < nodeCount; node++)
        {
            var restraint = model.RestraintOf(node);
            if (restraint == Dof.None)
                continue;
            var held = applyPrescribed ? model.PrescribedOf(node) : Vector3d.Zero;
            for (int axis = 0; axis < 3; axis++)
            {
                if (((int)restraint & (1 << axis)) != 0)
                    values[3 * node + axis] = held[axis];
            }
        }
        return values;
    }

    private static double[] PrescribedVector(StructuralModel model)
    {
        // Prescribed values at restrained degrees of freedom and exactly zero elsewhere, so
        // that Aeff · this gives the free rows' correction and nothing else.
        int nodeCount = model.Mesh.NodeCount;
        var values = new double[3 * nodeCount];
        for (int node = 0; node < nodeCount; node++)
        {
            var restraint = model.RestraintOf(node);
            if (restraint == Dof.None)
                continue;
            var held = model.PrescribedOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                if (((int)restraint & (1 << axis)) != 0)
                    values[3 * node + axis] = held[axis];
            }
        }
        return values;
    }

    /// <summary>The model's whole nodal load pattern, which the history scales.</summary>
    private static double[] LoadPattern(StructuralModel model)
    {
        int nodeCount = model.Mesh.NodeCount;
        var pattern = new double[3 * nodeCount];
        for (int node = 0; node < nodeCount; node++)
        {
            var force = model.ForceOf(node);
            pattern[3 * node] = force.X;
            pattern[3 * node + 1] = force.Y;
            pattern[3 * node + 2] = force.Z;
        }
        return pattern;
    }

    /// <summary>
    /// The spatial load vectors and their scalar laws — one pattern (the model's own loads
    /// scaled by <see cref="TransientSolveOptions.LoadFactor"/>) or several
    /// (<see cref="TransientSolveOptions.LoadPatterns"/>), plus the base motion's inertial load
    /// <c>-M·iota_d</c> scaled by <c>a_g(t)</c> when one is stated. The single-pattern list is
    /// what keeps the incumbent run bit-identical, since <see cref="ComputeLoad"/> reduces to
    /// <c>Scale(pattern, law(t))</c> for it; a base motion appends one more pattern, so it
    /// superposes with the model's loads (gravity held while a shaker runs).
    /// </summary>
    private static (double[][] Vectors, Func<double, double>[] Laws) BuildLoadPatterns(
        StructuralModel model, TransientSolveOptions transient,
        double[]? baseLoad, Func<double, double>? baseLaw)
    {
        var vectors = new List<double[]>();
        var laws = new List<Func<double, double>>();

        if (transient.LoadPatterns is { } patterns)
        {
            if (patterns.Count == 0)
                throw new ArgumentException(
                    "LoadPatterns was set but empty; give at least one pattern, or leave it null "
                    + "and state the load on the model.");
            if (transient.LoadFactor is not null)
                throw new FeaException(
                    "Both LoadFactor and LoadPatterns were set. LoadPatterns carries a law per "
                    + "pattern, so a single LoadFactor has no pattern to scale — state the "
                    + "constant-load case as one pattern with a constant factor instead.");
            if (ModelHasLoad(model))
                throw new FeaException(
                    "The solve model carries its own loads AND LoadPatterns was set. When several "
                    + "patterns are given the loads live on the patterns and the solve model "
                    + "provides only the operator and the initial conditions — clear its loads "
                    + "(ClearLoads), or state its loads as one of the patterns.");
            RequireOneOperator(model, patterns);

            for (int i = 0; i < patterns.Count; i++)
            {
                ArgumentNullException.ThrowIfNull(patterns[i].Pattern);
                ArgumentNullException.ThrowIfNull(patterns[i].Factor);
                vectors.Add(LoadPattern(patterns[i].Pattern));
                laws.Add(patterns[i].Factor);
            }
        }
        // A pure base motion (no model loads, no LoadFactor, no patterns) is reported as the
        // FIRST pattern so its per-state factor IS the ground acceleration a_g(t), which is the
        // number a reader wants. Any other configuration keeps the model's own pattern first,
        // so LoadFactor and LoadPatterns keep their reported meaning.
        else if (baseLoad is null || transient.LoadFactor is not null || ModelHasLoad(model))
        {
            vectors.Add(LoadPattern(model));
            laws.Add(transient.LoadFactor ?? (_ => 1.0));
        }

        if (baseLoad is not null)
        {
            vectors.Add(baseLoad);
            laws.Add(baseLaw!);
        }

        return (vectors.ToArray(), laws.ToArray());
    }

    /// <summary><c>into = sum_i laws[i](t)·vectors[i]</c>. Overwrites with the first pattern
    /// then adds the rest, so ONE pattern is bit-identical to <c>Scale(vectors[0], laws[0](t),
    /// into)</c>.</summary>
    private static void ComputeLoad(
        double[][] vectors, Func<double, double>[] laws, double time, double[] into)
    {
        Scale(vectors[0], laws[0](time), into);
        for (int i = 1; i < vectors.Length; i++)
        {
            double g = laws[i](time);
            var v = vectors[i];
            for (int dof = 0; dof < into.Length; dof++)
                into[dof] += g * v[dof];
        }
    }

    private static bool ModelHasLoad(StructuralModel model)
    {
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            if (model.ForceOf(node) != Vector3d.Zero)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every pattern must share one operator with the solve model — the same
    /// <see cref="AnalysisMesh"/> instance (a value comparison would scramble the answer by
    /// permutation), restraint MASK and material per element. Only the loads may differ, the
    /// mirror of <c>StructuralSolver.SolveAll</c>'s contract.
    /// </summary>
    private static void RequireOneOperator(
        StructuralModel model, IReadOnlyList<TransientLoadPattern> patterns)
    {
        var mesh = model.Mesh;
        for (int c = 0; c < patterns.Count; c++)
        {
            var other = patterns[c].Pattern;
            if (!ReferenceEquals(other.Mesh, mesh))
                throw new FeaException(
                    $"Load pattern {c} was built on a different AnalysisMesh instance from the "
                    + "solve model. Patterns share one factorization, so build every one over the "
                    + "same mesh object.");
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                if (other.RestraintOf(node) != model.RestraintOf(node))
                    throw new FeaException(
                        $"Load pattern {c} restrains node {node} differently from the solve model. "
                        + "Supports are ELIMINATED, so a different restraint pattern is a "
                        + "different matrix and cannot share a factorization.");
            }
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if (!Equals(other.MaterialOf(e), model.MaterialOf(e)))
                    throw new FeaException(
                        $"Load pattern {c} gives element {e} a different material from the solve "
                        + "model. The stiffness is a function of the materials, so they do not "
                        + "share one.");
            }
        }
    }

    // ---- per-step arithmetic ---------------------------------------------------------

    /// <summary>
    /// The Newmark update coefficients for one step size, computed by ONE rule so that a
    /// constant-step run and a single-size adaptive run produce the same bits. They express
    /// <c>a(n+1) = C0·(u(n+1) - u(n)) - C2·v(n) - C3·a(n)</c> and
    /// <c>v(n+1) = C1·(u(n+1) - u(n)) - C4·v(n) - C5·a(n)</c>.
    /// </summary>
    internal readonly record struct NewmarkCoefficients(
        double C0, double C1, double C2, double C3, double C4, double C5)
    {
        /// <summary>The coefficients for <paramref name="scheme"/> at step
        /// <paramref name="dt"/>.</summary>
        public static NewmarkCoefficients For(TimeIntegration scheme, double dt)
        {
            double beta = scheme.Beta, gamma = scheme.Gamma;
            return new NewmarkCoefficients(
                1.0 / (beta * dt * dt),
                gamma / (beta * dt),
                1.0 / (beta * dt),
                1.0 / (2.0 * beta) - 1.0,
                gamma / beta - 1.0,
                dt * (gamma / (2.0 * beta) - 1.0));
        }
    }

    /// <summary>
    /// ONE step of the Newmark / HHT scheme: build the right-hand side, solve for the free
    /// displacements, and derive the acceleration and velocity from the update relations. It is
    /// the single source of the step arithmetic, shared by <see cref="Solve"/> (constant
    /// coefficients every step) and <c>SolveAdaptive</c> (coefficients and factorization that
    /// vary per level), which is what makes a single-size adaptive run bit-identical to a
    /// constant-step one. Returns the step's relative residual and whether an iterative solve
    /// converged; the energy accounting and the state swap stay in the caller's loop.
    /// </summary>
    private static (double Residual, bool Converged) NewmarkStep(
        NewmarkCoefficients c, SparseCholesky? factor,
        PackedSparseMatrix fullMass, PackedSparseMatrix fullStiffness,
        PackedSparseMatrix? modelDamping, PackedSparseMatrix effective,
        double dampA, double dampB, double alpha, bool anyDamping,
        int[] reduced, int totalDofs,
        double[] displacement, double[] velocity, double[] acceleration,
        double[] nextLoad, double[] prescribed, double[]? prescribedCorrection,
        double[] nextDisplacement, double[] nextVelocity, double[] nextAcceleration,
        double[] rhs, double[] freeSolution, Scratch scratch,
        StructuralSolveOptions options, ProgressCancel? progress)
    {
        // rhs = f(t+alpha·dt)
        //     + M·(c0·u + c2·v + c3·a)
        //     + (1+alpha)·C·(c1·u + c4·v + c5·a)
        //     + alpha·C·v + alpha·K·u
        //     - Aeff·u_prescribed
        Combine3(displacement, c.C0, velocity, c.C2, acceleration, c.C3, scratch.W);
        fullMass.Multiply(scratch.W, scratch.MassProduct);
        for (int i = 0; i < totalDofs; i++)
            scratch.Full[i] = nextLoad[i] + scratch.MassProduct[i];

        if (anyDamping)
        {
            Combine3(displacement, c.C1, velocity, c.C4, acceleration, c.C5, scratch.W);
            ApplyDamping(fullMass, fullStiffness, modelDamping, dampA, dampB, scratch.W, scratch, scratch.C1);
            for (int i = 0; i < totalDofs; i++)
                scratch.Full[i] += (1.0 + alpha) * scratch.C1[i];

            // Exact-zero test: alpha is exactly 0 for every Newmark member, so this whole
            // branch is dead there and the Newmark path costs no extra products.
            if (alpha != 0)
            {
                ApplyDamping(fullMass, fullStiffness, modelDamping, dampA, dampB, velocity, scratch, scratch.C1);
                for (int i = 0; i < totalDofs; i++)
                    scratch.Full[i] += alpha * scratch.C1[i];
            }
        }

        if (alpha != 0)
        {
            fullStiffness.Multiply(displacement, scratch.StiffProduct);
            for (int i = 0; i < totalDofs; i++)
                scratch.Full[i] += alpha * scratch.StiffProduct[i];
        }

        if (prescribedCorrection is not null)
        {
            for (int i = 0; i < totalDofs; i++)
                scratch.Full[i] -= prescribedCorrection[i];
        }

        for (int dof = 0; dof < totalDofs; dof++)
        {
            int r = reduced[dof];
            if (r >= 0)
                rhs[r] = scratch.Full[dof];
        }

        bool converged = true;
        if (factor is not null)
        {
            factor.Solve(rhs, freeSolution);
        }
        else
        {
            // A warm start from the previous step, exactly as the thermal transient does:
            // consecutive states differ by O(dt), so the previous one is the best seed
            // available and costs nothing to supply.
            for (int dof = 0; dof < totalDofs; dof++)
            {
                int r = reduced[dof];
                if (r >= 0)
                    freeSolution[r] = displacement[dof];
            }
            var cg = SparseSymmetricCG.Solve(effective, rhs, freeSolution, options.Cg, progress);
            converged = cg.Converged;
        }
        double residual = Residual(effective, freeSolution, rhs);

        for (int dof = 0; dof < totalDofs; dof++)
        {
            int r = reduced[dof];
            nextDisplacement[dof] = r >= 0 ? freeSolution[r] : prescribed[dof];
        }
        for (int i = 0; i < totalDofs; i++)
        {
            double delta = nextDisplacement[i] - displacement[i];
            nextAcceleration[i] = c.C0 * delta - c.C2 * velocity[i] - c.C3 * acceleration[i];
            nextVelocity[i] = c.C1 * delta - c.C4 * velocity[i] - c.C5 * acceleration[i];
        }
        return (residual, converged);
    }

    private sealed class Scratch(int totalDofs, int freeCount)
    {
        public readonly double[] W = new double[totalDofs];
        public readonly double[] Full = new double[totalDofs];
        public readonly double[] MassProduct = new double[totalDofs];
        public readonly double[] StiffProduct = new double[totalDofs];
        public readonly double[] ModelDampingProduct = new double[totalDofs];
        public readonly double[] C1 = new double[totalDofs];
        public readonly double[] C2 = new double[totalDofs];
        public readonly int FreeCount = freeCount;
    }

    private static void Combine3(
        double[] a, double sa, double[] b, double sb, double[] c, double sc, double[] into)
    {
        for (int i = 0; i < into.Length; i++)
            into[i] = sa * a[i] + sb * b[i] + sc * c[i];
    }

    private static void Scale(double[] source, double factor, double[] into)
    {
        for (int i = 0; i < into.Length; i++)
            into[i] = factor * source[i];
    }

    /// <summary>
    /// <c>C·x = dampA·(M·x) + dampB·(K·x) + C_model·x</c> — the total damping's action on a
    /// vector. The proportional halves stay products (the finding
    /// <see cref="TransientSolveOptions.Damping"/> records) while the model's own C, when it
    /// carries one, enters as the one matrix product there is no way around.
    /// </summary>
    private static void ApplyDamping(
        PackedSparseMatrix mass, PackedSparseMatrix stiffness, PackedSparseMatrix? modelC,
        double dampA, double dampB, double[] x, Scratch scratch, double[] into)
    {
        if (dampA != 0)
            mass.Multiply(x, scratch.MassProduct);
        if (dampB != 0)
            stiffness.Multiply(x, scratch.StiffProduct);
        if (modelC is not null)
            modelC.Multiply(x, scratch.ModelDampingProduct);
        for (int i = 0; i < into.Length; i++)
        {
            double value = 0;
            if (dampA != 0)
                value += dampA * scratch.MassProduct[i];
            if (dampB != 0)
                value += dampB * scratch.StiffProduct[i];
            if (modelC is not null)
                value += scratch.ModelDampingProduct[i];
            into[i] = value;
        }
    }

    /// <summary>The run's damping as text, both the proportional run option and any the model
    /// itself carries.</summary>
    private static string DescribeDamping(RayleighDamping option, StructuralModel model)
    {
        var parts = new List<string>();
        if (option != RayleighDamping.None)
            parts.Add(option.ToString());
        if (model.HasDamping)
            parts.Add(model.DampingDescription);
        return parts.Count == 0 ? "undamped" : string.Join("; ", parts);
    }

    /// <summary>
    /// Packages one instant: the displacement as a <see cref="StructuralResults"/>, the two
    /// derivatives, the energies and the dynamic equilibrium check.
    ///
    /// <para><b>The reaction is the residual of the equation the scheme actually enforced</b>,
    /// <c>M·a(n+1) + (1+alpha)·C·v(n+1) - alpha·C·v(n) + (1+alpha)·K·u(n+1) - alpha·K·u(n)
    /// - f</c>, which is exactly zero at every free degree of freedom by construction and is
    /// the support force at every restrained one. Evaluating the un-weighted equation at
    /// <c>t(n+1)</c> instead would leave an HHT run with a non-zero "reaction" at free nodes —
    /// not an error, but the scheme's own weighting reported as one.</para>
    /// </summary>
    private static TransientState BuildState(
        StructuralModel model, int step, double time, double loadFactor,
        double[] displacement, double[] velocity, double[] acceleration, double[] load,
        PackedSparseMatrix fullMass, PackedSparseMatrix fullStiffness, PackedSparseMatrix? modelC,
        double dampA, double dampB, double alpha,
        double[] previousVelocity, double[] previousDisplacement,
        PackedSparseMatrix effective, double worstResidual, bool converged,
        StructuralSolveOptions options, Scratch scratch)
    {
        var mesh = model.Mesh;
        int nodeCount = mesh.NodeCount;
        int totalDofs = 3 * nodeCount;

        var massAcceleration = fullMass.Multiply(acceleration);
        var stiffnessDisplacement = fullStiffness.Multiply(displacement);

        // Kinetic and strain energy from the SAME matrices the step was solved with.
        var massVelocity = fullMass.Multiply(velocity);
        double kinetic = 0, strain = 0;
        for (int i = 0; i < totalDofs; i++)
        {
            kinetic += velocity[i] * massVelocity[i];
            strain += displacement[i] * stiffnessDisplacement[i];
        }
        kinetic *= 0.5;
        strain *= 0.5;

        bool damped = dampA != 0 || dampB != 0 || modelC is not null;
        if (damped)
            ApplyDamping(fullMass, fullStiffness, modelC, dampA, dampB, velocity, scratch, scratch.C1);
        // Exact-zero test: alpha is 0 for every Newmark member, so a Newmark run never forms
        // the previous step's terms at all and its reaction is the plain residual.
        bool weighted = alpha != 0;
        if (weighted && damped)
            ApplyDamping(fullMass, fullStiffness, modelC, dampA, dampB, previousVelocity, scratch, scratch.C2);
        double[]? previousStiffness =
            weighted ? fullStiffness.Multiply(previousDisplacement) : null;

        var reaction = new Vector3d[nodeCount];
        var inertia = Vector3d.Zero;
        var damping = Vector3d.Zero;
        var applied = Vector3d.Zero;
        var reactionTotal = Vector3d.Zero;
        double scale = 0;

        Span<double> residual = stackalloc double[3];
        for (int node = 0; node < nodeCount; node++)
        {
            var restraint = model.RestraintOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                int dof = 3 * node + axis;
                // The load was already evaluated at the scheme's own weighted instant
                // t + alpha·dt when the step was formed, so it enters here unweighted: the
                // alpha carried by the internal and damping terms is the whole weighting.
                double value = massAcceleration[dof]
                    + (1.0 + alpha) * stiffnessDisplacement[dof]
                    - (weighted ? alpha * previousStiffness![dof] : 0)
                    - load[dof];
                if (damped)
                {
                    value += (1.0 + alpha) * scratch.C1[dof];
                    if (weighted)
                        value -= alpha * scratch.C2[dof];
                }
                residual[axis] = value;
            }
            reaction[node] = new Vector3d(residual[0], residual[1], residual[2]);

            var force = new Vector3d(load[3 * node], load[3 * node + 1], load[3 * node + 2]);
            applied += force;
            scale += force.Length;
            inertia += new Vector3d(
                massAcceleration[3 * node], massAcceleration[3 * node + 1],
                massAcceleration[3 * node + 2]);
            if (damped)
            {
                damping += new Vector3d(
                    scratch.C1[3 * node], scratch.C1[3 * node + 1], scratch.C1[3 * node + 2]);
            }

            if (restraint == Dof.None)
                continue;
            var held = new Vector3d(
                restraint.HasFlag(Dof.X) ? reaction[node].X : 0,
                restraint.HasFlag(Dof.Y) ? reaction[node].Y : 0,
                restraint.HasFlag(Dof.Z) ? reaction[node].Z : 0);
            reactionTotal += held;
            scale += held.Length;
        }
        scale += inertia.Length + damping.Length;

        // d'Alembert: the applied and support forces sum to what is accelerating the body.
        // Zero to round-off for any correct answer, exactly as the static form is.
        double equilibrium = scale > 0
            ? (applied + reactionTotal - inertia - damping).Length / scale
            : 0;

        var nodalDisplacement = ToNodal(displacement, nodeCount);
        var report = new FeaSolveReport
        {
            NodeCount = nodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            TotalDofs = totalDofs,
            FreeDofs = scratch.FreeCount,
            MatrixNonZeros = effective.NonZeroCount,
            // Zero on a step, the honest number rather than a missing one: a step has no
            // assembly and no factorization of its own, which is the whole point of a
            // constant step. ThermalSolver's per-step reports say the same.
            FactorNonZeros = 0,
            Method = options.Method,
            Ordering = options.Method == FeaSolveMethod.Direct
                ? options.Ordering
                : SparseOrdering.Natural,
            Converged = converged,
            Iterations = 0,
            RelativeResidual = worstResidual,
            StrainEnergy = strain,
            AppliedForce = applied,
            ReactionForce = reactionTotal,
            EquilibriumResidual = equilibrium,
            AssembleMs = 0,
            FactorMs = 0,
            SolveMs = 0,
        };

        var results = new StructuralResults(model, nodalDisplacement, reaction, report);
        return new TransientState(
            step, time, loadFactor, results,
            ToNodal(velocity, nodeCount), ToNodal(acceleration, nodeCount),
            kinetic, strain, inertia, damping);
    }

    private static Vector3d[] ToNodal(double[] values, int nodeCount)
    {
        var nodal = new Vector3d[nodeCount];
        for (int node = 0; node < nodeCount; node++)
        {
            nodal[node] = new Vector3d(
                values[3 * node], values[3 * node + 1], values[3 * node + 2]);
        }
        return nodal;
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
}
