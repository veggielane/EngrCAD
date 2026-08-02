using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>
/// Options for <see cref="TransientSolver.Solve"/>: a constant step, a count, a scheme, a
/// damping model, a load history and an initial state.
///
/// <para><b>The step is constant, and that is a design decision rather than a
/// simplification</b> — the same one <c>ThermalTransientOptions</c> records. It is what lets
/// ONE factorization serve the whole run, because the stepping matrix
/// <c>a0·M + (1+alpha)·a1·C + (1+alpha)·K</c> depends on the step, the scheme and the model
/// and on nothing else. Adaptive stepping would refactor at every change, which is the whole
/// cost of the method, and is filed rather than half-built.</para>
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
    /// Rayleigh damping <c>C = alpha·M + beta·K</c> (default
    /// <see cref="RayleighDamping.None"/>).
    ///
    /// <para><b>The matrix C is never assembled, and the backlog entry that predicted it
    /// would be is wrong on a point worth keeping.</b> A transient solve was filed as "the
    /// ONE consumer that genuinely wants C as a matrix rather than as per-mode ratios" —
    /// but proportional damping means every appearance of C is either a product
    /// <c>C·x = alpha·(M·x) + beta·(K·x)</c>, which is two matrix-vector products this solver
    /// already performs, or a scalar multiple folded into the effective stiffness, which
    /// collects as <c>(...)·M + (...)·K</c>. Forming C would cost a third sparse matrix with
    /// the stiffness's sparsity, and buy an operation that is strictly more expensive than
    /// the two products it replaces (the mass matrix's blocks are scalar multiples of the
    /// identity, so M has far fewer stored entries than K). So no damping matrix exists here
    /// either, and <see cref="ModalDamping"/>'s statement that none exists in this project
    /// stands unqualified.</para>
    ///
    /// <para>A NON-proportional damping model — a discrete dashpot, two loss factors, joint
    /// friction — would genuinely need an assembled C in the effective stiffness. The
    /// vocabulary for stating one now exists on <see cref="StructuralModel"/> (dashpots,
    /// per-region coefficients), and this solver REFUSES a model that carries it rather than
    /// silently ignoring it: today only <see cref="DirectHarmonicSolver"/> integrates
    /// model-carried damping, and transient support is filed. See
    /// <see cref="RayleighDamping"/> for what proportionality excludes.</para>
    /// </summary>
    public RayleighDamping Damping { get; init; } = RayleighDamping.None;

    /// <summary>
    /// The load history: the multiplier on the model's whole load pattern at time <c>t</c>.
    /// Null is a constant 1, so a model's loads are simply held.
    ///
    /// <para><b>One spatial pattern scaled by one scalar law</b>, which covers a step, an
    /// impulse, a ramp, a harmonic drive and a measured trace. What it does NOT cover is a
    /// superposition of patterns with independent histories — gravity held while a shaker
    /// runs — because that needs a LIST of load patterns proven to share one stiffness
    /// matrix, which is exactly <c>StructuralSolver.SolveAll</c>'s contract and is filed as
    /// the shape it would take rather than approximated here.</para>
    ///
    /// <para><b>A prescribed displacement is not a load and is not scaled by this.</b> It is
    /// held at its stated value for the whole run, which is what a displaced support does;
    /// a support whose motion is a history of its own is base excitation, and is filed.</para>
    ///
    /// <para>The value at <c>t = 0</c> is the caller's, and it decides the initial
    /// acceleration. A step load written with <c>g(0) = 1</c> starts the body accelerating at
    /// <c>t = 0</c>; written with <c>g(0) = 0</c> it starts at rest and the first step applies
    /// the load. Both are legitimate readings of "suddenly applied" and neither is imposed.</para>
    /// </summary>
    public Func<double, double>? LoadFactor { get; init; }

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
/// different solver rather than an option on this one. Non-proportional damping is refused for
/// the reason <see cref="RayleighDamping"/> states. Base excitation (a support whose motion is
/// a history) and several load patterns with independent histories are filed; see
/// <see cref="TransientSolveOptions.LoadFactor"/>. Explicit integration is refused with its
/// own reason on <see cref="TimeIntegration.CentralDifference"/>.</para>
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

        if (model.HasDamping)
            throw new FeaException(
                $"The model carries its own damping ({model.DampingDescription}), which this "
                + "transient would silently ignore: its damping statement is "
                + "TransientSolveOptions.Damping, integrated as products against M and K, and "
                + "a model-carried C — dashpots, per-region coefficients — is non-proportional "
                + "in general and needs an assembled matrix in the effective stiffness. Only "
                + "DirectHarmonicSolver consumes model-carried damping today; transient "
                + "support for it is filed. For proportional damping, state it on the options "
                + "and leave the model clean.");

        int nodeCount = mesh.NodeCount;
        int totalDofs = 3 * nodeCount;
        var reduced = FeaAssembly.ReducedIndices(model, out int freeCount);
        if (freeCount == 0)
            throw new FeaException(
                "Every degree of freedom is restrained; there is nothing to step.");

        var scheme = transient.Integration;
        double dt = transient.TimeStep;
        double beta = scheme.Beta, gamma = scheme.Gamma, alpha = scheme.Alpha;

        // The Newmark coefficients, named c0..c5 so that `a` stays the acceleration:
        //   a(n+1) = c0·(u(n+1) - u(n)) - c2·v(n) - c3·a(n)
        //   v(n+1) = c1·(u(n+1) - u(n)) - c4·v(n) - c5·a(n)
        double c0 = 1.0 / (beta * dt * dt);
        double c1 = gamma / (beta * dt);
        double c2 = 1.0 / (beta * dt);
        double c3 = 1.0 / (2.0 * beta) - 1.0;
        double c4 = gamma / beta - 1.0;
        double c5 = dt * (gamma / (2.0 * beta) - 1.0);

        var stopwatch = Stopwatch.StartNew();
        var fullStiffness = FeaAssembly.Stiffness(model, stiffnessRule);
        // Consistent only, and not a parameter: a lumped mass buys a diagonal matrix, and a
        // diagonal matrix buys nothing here because the effective stiffness carries K and has
        // to be factored whatever M looks like. MassLumping exists for the modal solver, where
        // consistent and lumped BRACKET the truth and the comparison is worth having.
        var (fullMass, _) = FeaAssembly.Mass(model, massRule);

        // C = dampA·M + dampB·K, never assembled: every use is either a product, taken as
        // dampA·(M·x) + dampB·(K·x), or a scalar multiple folded into the coefficients below.
        double dampA = transient.Damping.Alpha, dampB = transient.Damping.Beta;

        // Aeff = c0·M + (1+alpha)·c1·C + (1+alpha)·K
        //      = [c0 + (1+alpha)·c1·dampA]·M + [(1+alpha)·(1 + c1·dampB)]·K
        double massCoefficient = c0 + (1.0 + alpha) * c1 * dampA;
        double stiffnessCoefficient = (1.0 + alpha) * (1.0 + c1 * dampB);
        var fullEffective = FeaAssembly.Combine(
            fullStiffness, stiffnessCoefficient, fullMass, massCoefficient);

        // The reduced mass is needed for the initial acceleration's own solve; the reduced
        // STIFFNESS is not needed at all, because every use of K here is a full-vector
        // product (the right-hand side terms and the reaction residual) and the reduction
        // would be a whole matrix built to be read by nobody.
        var m = FeaAssembly.Reduce(fullMass, reduced, freeCount);
        var effective = FeaAssembly.Reduce(fullEffective, reduced, freeCount);

        var prescribed = PrescribedVector(model);
        bool anyPrescribed = false;
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
        // The constant correction the prescribed columns contribute to every step's
        // right-hand side. Computed against the FULL effective operator so that a
        // time-varying prescribed value would be a change of one line rather than a rewrite
        // (the reasoning ThermalSolver.SolveTransient records), and skipped entirely when
        // nothing is prescribed so the common case stays bit-clean.
        var prescribedCorrection = anyPrescribed ? fullEffective.Multiply(prescribed) : null;

        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        // ---- the initial state -----------------------------------------------------
        var displacement = InitialVector(
            model, transient.InitialDisplacement, nameof(TransientSolveOptions.InitialDisplacement),
            applyPrescribed: true);
        var velocity = InitialVector(
            model, transient.InitialVelocity, nameof(TransientSolveOptions.InitialVelocity),
            applyPrescribed: false);

        var loadFactor = transient.LoadFactor;
        var pattern = LoadPattern(model);
        double factor0 = loadFactor?.Invoke(0) ?? 1.0;
        var load = new double[totalDofs];

        stopwatch.Restart();
        int factorizations = 0;
        var acceleration = InitialAcceleration(
            model, m, fullMass, fullStiffness, reduced, freeCount,
            pattern, factor0, displacement, velocity, dampA, dampB, options, progress,
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

        Scale(pattern, factor0, load);
        var initial = BuildState(
            model, 0, 0, factor0, displacement, velocity, acceleration, load,
            fullMass, fullStiffness, dampA, dampB, alpha,
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
            // HHT evaluates the load at the same weighted instant as the internal forces:
            //   t(n+1+alpha) = (1+alpha)·t(n+1) - alpha·t(n) = t(n+1) + alpha·dt.
            // With alpha = 0 that is t(n+1) exactly, so the Newmark path is untouched.
            double weightedTime = time + alpha * dt;
            double stepFactor = loadFactor?.Invoke(weightedTime) ?? 1.0;
            Scale(pattern, stepFactor, nextLoad);

            // rhs = f(t+alpha·dt)
            //     + M·(c0·u + c2·v + c3·a)
            //     + (1+alpha)·C·(c1·u + c4·v + c5·a)
            //     + alpha·C·v + alpha·K·u
            //     - Aeff·u_prescribed
            Combine3(displacement, c0, velocity, c2, acceleration, c3, scratch.W);
            fullMass.Multiply(scratch.W, scratch.MassProduct);
            for (int i = 0; i < totalDofs; i++)
                scratch.Full[i] = nextLoad[i] + scratch.MassProduct[i];

            if (dampA != 0 || dampB != 0)
            {
                Combine3(displacement, c1, velocity, c4, acceleration, c5, scratch.W);
                ApplyDamping(fullMass, fullStiffness, dampA, dampB, scratch.W, scratch, scratch.C1);
                for (int i = 0; i < totalDofs; i++)
                    scratch.Full[i] += (1.0 + alpha) * scratch.C1[i];

                // Exact-zero test: alpha is exactly 0 for every Newmark member, so this whole
                // branch is dead there and the Newmark path costs no extra products.
                if (alpha != 0)
                {
                    ApplyDamping(fullMass, fullStiffness, dampA, dampB, velocity, scratch, scratch.C1);
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
                converged &= cg.Converged;
            }
            worstResidual = Math.Max(worstResidual, Residual(effective, freeSolution, rhs));

            for (int dof = 0; dof < totalDofs; dof++)
            {
                int r = reduced[dof];
                nextDisplacement[dof] = r >= 0 ? freeSolution[r] : prescribed[dof];
            }
            for (int i = 0; i < totalDofs; i++)
            {
                double delta = nextDisplacement[i] - displacement[i];
                nextAcceleration[i] = c0 * delta - c2 * velocity[i] - c3 * acceleration[i];
                nextVelocity[i] = c1 * delta - c4 * velocity[i] - c5 * acceleration[i];
            }

            // The discrete energy balance, accumulated at the point where the trapezoidal
            // member asserts it holds exactly: the work of the load over the step's own
            // displacement increment, and the dissipation at the mean velocity. See
            // TransientSolveReport.EnergyBalanceResidual for the derivation.
            for (int i = 0; i < totalDofs; i++)
            {
                work += 0.5 * (nextDisplacement[i] - displacement[i]) * (load[i] + nextLoad[i]);
                scratch.W[i] = 0.5 * (velocity[i] + nextVelocity[i]);
            }
            if (dampA != 0 || dampB != 0)
            {
                ApplyDamping(fullMass, fullStiffness, dampA, dampB, scratch.W, scratch, scratch.C1);
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
                fullMass, fullStiffness, dampA, dampB, alpha,
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
            Damping = dampA == 0 && dampB == 0 ? "undamped" : transient.Damping.ToString(),
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

        return new TransientResults(model, [.. states], report);
    }

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
        int[] reduced, int freeCount,
        double[] pattern, double factor0, double[] displacement, double[] velocity,
        double dampA, double dampB,
        StructuralSolveOptions options, ProgressCancel? progress,
        ref int factorizations)
    {
        int totalDofs = 3 * model.Mesh.NodeCount;
        var acceleration = new double[totalDofs];

        var stiffProduct = fullStiffness.Multiply(displacement);
        double[]? massVelocity = null, stiffVelocity = null;
        if (dampA != 0)
            massVelocity = fullMass.Multiply(velocity);
        if (dampB != 0)
            stiffVelocity = fullStiffness.Multiply(velocity);

        var rhs = new double[freeCount];
        bool anything = false;
        for (int dof = 0; dof < totalDofs; dof++)
        {
            int r = reduced[dof];
            if (r < 0)
                continue;
            double value = factor0 * pattern[dof] - stiffProduct[dof];
            if (massVelocity is not null)
                value -= dampA * massVelocity[dof];
            if (stiffVelocity is not null)
                value -= dampB * stiffVelocity[dof];
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

    // ---- per-step arithmetic ---------------------------------------------------------

    private sealed class Scratch(int totalDofs, int freeCount)
    {
        public readonly double[] W = new double[totalDofs];
        public readonly double[] Full = new double[totalDofs];
        public readonly double[] MassProduct = new double[totalDofs];
        public readonly double[] StiffProduct = new double[totalDofs];
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

    /// <summary><c>C·x = dampA·(M·x) + dampB·(K·x)</c> — the only form C ever takes here.</summary>
    private static void ApplyDamping(
        PackedSparseMatrix mass, PackedSparseMatrix stiffness,
        double dampA, double dampB, double[] x, Scratch scratch, double[] into)
    {
        if (dampA != 0)
            mass.Multiply(x, scratch.MassProduct);
        if (dampB != 0)
            stiffness.Multiply(x, scratch.StiffProduct);
        for (int i = 0; i < into.Length; i++)
        {
            double value = 0;
            if (dampA != 0)
                value += dampA * scratch.MassProduct[i];
            if (dampB != 0)
                value += dampB * scratch.StiffProduct[i];
            into[i] = value;
        }
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
        PackedSparseMatrix fullMass, PackedSparseMatrix fullStiffness,
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

        bool damped = dampA != 0 || dampB != 0;
        if (damped)
            ApplyDamping(fullMass, fullStiffness, dampA, dampB, velocity, scratch, scratch.C1);
        // Exact-zero test: alpha is 0 for every Newmark member, so a Newmark run never forms
        // the previous step's terms at all and its reaction is the plain residual.
        bool weighted = alpha != 0;
        if (weighted && damped)
            ApplyDamping(fullMass, fullStiffness, dampA, dampB, previousVelocity, scratch, scratch.C2);
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
