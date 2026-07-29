using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>Which implicit scheme a transient conduction solve steps with.</summary>
public enum ThermalTimeScheme
{
    /// <summary>
    /// Backward Euler (theta = 1) — first-order accurate, and the default.
    ///
    /// <para><b>Chosen for L-stability, not for accuracy.</b> Both schemes here are
    /// unconditionally stable in the A-stable sense (no step size makes them blow up), but
    /// that is not the property a thermal transient needs. The eigenvalues of a conduction
    /// system span many decades — the finest element sets the stiffest mode, at roughly
    /// <c>alpha/h²</c> — and Crank-Nicolson's amplification factor tends to <b>-1</b> for
    /// those modes, so a sharp initial transient does not decay: it alternates sign and
    /// rings. Backward Euler's tends to 0, which is what the physics does.</para>
    ///
    /// <para><b>Measured on this project's quenched-bar fixture</b> (40 mm steel bar,
    /// 1 mm elements, face stepped from 20 to 100 C; a backward step in temperature is
    /// numerical, since heat only enters). Where the step is long against the stiffest
    /// mode's own time <c>h²/alpha = 0.1 s</c>, backward Euler makes <b>no backward move at
    /// all</b> while Crank-Nicolson swings back by up to <b>106% of the temperature step</b>
    /// and is still swinging 65% after five steps:
    /// <code>
    ///   dt      lambda.dt   backward Euler        Crank-Nicolson
    ///   2.0 s        20      0 moves, 0.000%      190 moves, 105.9% (64.8% after step 5)
    ///   1.0 s        10      0 moves, 0.000%      143 moves,  81.5% (42.6% after step 5)
    ///   0.5 s         5      0 moves, 0.000%      242 moves,  56.3% (24.0% after step 5)
    /// </code></para>
    ///
    /// <para><b>The honest counterweight, because it is easy to over-claim here:</b> at a
    /// SHORT step both schemes move backwards, and that is the CONSISTENT capacity matrix
    /// rather than the time integration. At <c>dt = 0.005 s</c> backward Euler undershoots
    /// the initial temperature by 5.8% and Crank-Nicolson by 7.1%. "Backward Euler is
    /// monotone" is true of a lumped capacity, not of this one.</para>
    ///
    /// <para>The accuracy cost is real and is stated rather than hidden: first order in the
    /// step, so halving the step halves the error, where Crank-Nicolson quarters it
    /// (measured 1.05 and 2.00 against theory 1 and 2). For a smooth transient with a step
    /// chosen on accuracy grounds rather than stability ones,
    /// <see cref="CrankNicolson"/> is the better tool.</para>
    /// </summary>
    BackwardEuler,

    /// <summary>
    /// Crank-Nicolson (theta = 1/2) — second-order accurate in the time step, and the
    /// trapezoidal rule by another name.
    ///
    /// <para>A-stable but <b>not</b> L-stable: the amplification factor of a mode with
    /// eigenvalue <c>lambda</c> is <c>(1 - lambda·dt/2)/(1 + lambda·dt/2)</c>, which
    /// approaches -1 rather than 0 as <c>lambda·dt</c> grows. A step change in a boundary
    /// temperature excites exactly those modes, and the result is an oscillation that
    /// decays slowly — see <see cref="BackwardEuler"/> for the measured table, where it
    /// swings back by up to 106% of the temperature step.</para>
    ///
    /// <para>Use it when the transient is smooth (a slow ramp, a periodic drive, a
    /// convergence study), or when the step is short against the mesh's own diffusion time
    /// <c>h²/alpha</c> — there the ringing does not arise and the second order is free.
    /// Prefer <see cref="BackwardEuler"/> when the initial condition disagrees with the
    /// boundary condition and the step is long.</para>
    /// </summary>
    CrankNicolson,
}

/// <summary>Options for <see cref="ThermalSolver.Solve(ThermalModel, ThermalSolveOptions?)"/>.</summary>
public sealed record ThermalSolveOptions
{
    /// <summary>
    /// Which linear solver (default <see cref="FeaSolveMethod.Direct"/>).
    ///
    /// <para><b>And here the direct default's second argument actually applies.</b>
    /// <see cref="FeaSolveMethod.Direct"/> records that the usual "factor once, solve many
    /// right-hand sides" case does not arise in the structural solver, which factors and
    /// discards. A transient conduction solve is precisely that case:
    /// <see cref="ThermalSolver.SolveTransient"/> at a CONSTANT time step factors
    /// <c>C/dt + theta·K</c> exactly once and re-uses it for every step, so a
    /// thousand-step run pays one factorization and a thousand back-substitutions. The
    /// exactness argument for the default is unchanged; this is the amortisation argument
    /// finally being true somewhere.</para>
    /// </summary>
    public FeaSolveMethod Method { get; init; } = FeaSolveMethod.Direct;

    /// <summary>Elimination ordering for the direct solver (default
    /// <see cref="SparseOrdering.Amd"/>).</summary>
    public SparseOrdering Ordering { get; init; } = SparseOrdering.Amd;

    /// <summary>Iterative-solver settings (ignored for the direct method).</summary>
    public CgOptions Cg { get; init; } = new();

    /// <summary>
    /// Quadrature rule override for the CONDUCTIVITY matrix, for tests that check the
    /// production rule is exact. Null uses the cheapest exact rule for the element order.
    /// The capacity matrix has its own rule (two degrees higher) and its own override.
    /// </summary>
    internal int? ConductivityQuadratureDegree { get; init; }

    /// <summary>Quadrature rule override for the CAPACITY matrix (see
    /// <see cref="ConductivityQuadratureDegree"/>).</summary>
    internal int? CapacityQuadratureDegree { get; init; }
}

/// <summary>
/// Options for <see cref="ThermalSolver.SolveTransient"/>: a constant step, a count, a
/// scheme and an initial condition.
///
/// <para><b>The step is constant, and that is a design decision rather than a
/// simplification.</b> It is what lets ONE factorization serve the whole run — the matrix
/// <c>C/dt + theta·K</c> depends on the step and on nothing else — which is the entire
/// performance argument for the direct default here. Adaptive stepping would refactor at
/// every change and is filed rather than half-built.</para>
/// </summary>
public sealed record ThermalTransientOptions
{
    /// <summary>A run of <paramref name="steps"/> steps of size
    /// <paramref name="timeStep"/>.</summary>
    public ThermalTransientOptions(double timeStep, int steps)
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

    /// <summary>Which implicit scheme (default <see cref="ThermalTimeScheme.BackwardEuler"/>).</summary>
    public ThermalTimeScheme Scheme { get; init; } = ThermalTimeScheme.BackwardEuler;

    /// <summary>A uniform initial temperature (ignored when
    /// <see cref="InitialField"/> is set).</summary>
    public double InitialTemperature { get; init; }

    /// <summary>
    /// A per-node initial temperature, overriding <see cref="InitialTemperature"/> — how a
    /// steady solution becomes a transient's starting point.
    ///
    /// <para><b>A prescribed temperature wins over the initial condition at t = 0</b>, so
    /// the stored initial state already shows the boundary value at those nodes. This is
    /// the standard finite-element treatment and it is a real choice, so here is the
    /// reasoning. "The surface is suddenly held at Ts" means Ts for every t &gt; 0, and the
    /// value AT t = 0 is a single instant that no solution depends on — the semi-infinite
    /// erfc profile is derived under exactly that reading. The alternative, letting the
    /// initial value stand and transitioning inside the first step, was built and measured:
    /// it charges the surface node's whole heat-up to the first step, and because the
    /// CONSISTENT capacity matrix couples that node to its neighbours, they are dragged
    /// down with it — a quenched bar undershot its own initial temperature by <b>49% of the
    /// step</b>, against 5.8% under this reading. The discontinuity is not deleted by
    /// snapping; it is placed where the analytic problem puts it, between the initial
    /// condition and the first instant.</para>
    /// </summary>
    public IReadOnlyList<double>? InitialField { get; init; }

    /// <summary>Store every n-th step's temperature field (default 1 = every step). Step 0
    /// and the final step are always stored.</summary>
    public int StoreEvery
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 1;
}

/// <summary>
/// What a conduction solve did — sizes, timings, the residual, and the ENERGY BALANCE,
/// which is this solver's analogue of the structural equilibrium check and just as cheap.
/// A return value, never a log line.
/// </summary>
public sealed record ThermalSolveReport
{
    /// <summary>Nodes in the analysis mesh.</summary>
    public required int NodeCount { get; init; }

    /// <summary>Elements in the analysis mesh.</summary>
    public required int ElementCount { get; init; }

    /// <summary>Linear or quadratic elements.</summary>
    public required ElementOrder Order { get; init; }

    /// <summary>Total degrees of freedom (ONE per node, not three).</summary>
    public required int TotalDofs { get; init; }

    /// <summary>Degrees of freedom actually solved for.</summary>
    public required int FreeDofs { get; init; }

    /// <summary>Degrees of freedom removed by prescribed temperatures.</summary>
    public int ConstrainedDofs => TotalDofs - FreeDofs;

    /// <summary>Stored entries of the reduced matrix (upper triangle).</summary>
    public required int MatrixNonZeros { get; init; }

    /// <summary>Stored entries of the Cholesky factor, or 0 for an iterative solve.</summary>
    public required int FactorNonZeros { get; init; }

    /// <summary>Which solver ran.</summary>
    public required FeaSolveMethod Method { get; init; }

    /// <summary>The elimination ordering, for a direct solve.</summary>
    public required SparseOrdering Ordering { get; init; }

    /// <summary>False only for an iterative solve that hit its iteration cap.</summary>
    public required bool Converged { get; init; }

    /// <summary>Iterations, for an iterative solve.</summary>
    public required int Iterations { get; init; }

    /// <summary>Relative residual of the reduced linear system.</summary>
    public required double RelativeResidual { get; init; }

    /// <summary>Lowest nodal temperature.</summary>
    public required double MinTemperature { get; init; }

    /// <summary>Highest nodal temperature.</summary>
    public required double MaxTemperature { get; init; }

    /// <summary>Total applied heat: nodal sources, surface fluxes and volumetric
    /// generation. Convection is excluded — its supply depends on the answer.</summary>
    public required double AppliedHeat { get; init; }

    /// <summary>Net heat entering through prescribed-temperature (Dirichlet) boundaries.
    /// Negative means heat is leaving there.</summary>
    public required double PrescribedHeat { get; init; }

    /// <summary>Net heat LEAVING through convective surfaces,
    /// <c>sum(integral(h·(T - T_inf) dA))</c>.</summary>
    public required double ConvectiveHeat { get; init; }

    /// <summary>Rate of change of stored energy, <c>1' · C · dT/dt</c> — exactly zero for
    /// a steady solve, and the term that closes the balance for a transient step.</summary>
    public required double StorageRate { get; init; }

    /// <summary>
    /// <c>|applied + prescribed - convective - storage|</c> over the SUM OF THE MAGNITUDES
    /// of the individual contributions — the first law, discretely, which must hold to
    /// round-off for ANY correct answer. The cheapest end-to-end check there is: an error
    /// in the consistent load weights, in the convection split, in assembly or in the solve
    /// all show up here and none of them is visible in a temperature plot.
    /// <para>The denominator is deliberately the sum of magnitudes rather than the net: a
    /// slab held hot at one end and cold at the other has a net prescribed heat of exactly
    /// zero while each end carries a large flow, and dividing one by the other would report
    /// a relative error of 1 for a perfect answer. (The structural solver's equilibrium
    /// residual takes the same care for the same reason.)</para>
    /// </summary>
    public required double EnergyBalanceResidual { get; init; }

    /// <summary>Milliseconds spent assembling.</summary>
    public required double AssembleMs { get; init; }

    /// <summary>Milliseconds spent factoring (0 for an iterative solve, and 0 on a
    /// transient step, which re-uses the run's single factorization).</summary>
    public required double FactorMs { get; init; }

    /// <summary>Milliseconds spent in the solve itself.</summary>
    public required double SolveMs { get; init; }

    /// <summary>A note about this solve worth acting on, or null. Produced by the SAME
    /// rule the structural solver uses (<c>StructuralSolver.AdvisoryFor</c>) rather than a
    /// second copy of it — a direct factorization that both took real time and dominated
    /// its own solve.</summary>
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
            $"temperature {MinTemperature:G6} to {MaxTemperature:G6}",
            $"heat: applied {AppliedHeat:G4}, prescribed {PrescribedHeat:G4}, "
                + $"convective {ConvectiveHeat:G4}, storage {StorageRate:G4}, "
                + $"balance {EnergyBalanceResidual:E2}",
            $"assemble {AssembleMs:F1} ms, factor {FactorMs:F1} ms, solve {SolveMs:F1} ms",
        };
        if (Advisory is { } advisory)
            lines.Add(advisory);
        return string.Join(Environment.NewLine, lines);
    }

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>What a transient run did, beside the per-step reports.</summary>
public sealed record ThermalTransientReport
{
    /// <summary>The constant time step.</summary>
    public required double TimeStep { get; init; }

    /// <summary>Steps taken.</summary>
    public required int Steps { get; init; }

    /// <summary>Total time spanned.</summary>
    public required double Duration { get; init; }

    /// <summary>Which scheme stepped.</summary>
    public required ThermalTimeScheme Scheme { get; init; }

    /// <summary>
    /// How many matrix factorizations the whole run needed. <b>One</b>, for a direct
    /// solve at a constant step — the property that makes the direct default pay for
    /// itself here rather than merely being exact.
    /// </summary>
    public required int Factorizations { get; init; }

    /// <summary>Worst relative linear-system residual over all steps.</summary>
    public required double WorstRelativeResidual { get; init; }

    /// <summary>True unless some step's iterative solve hit its cap.</summary>
    public required bool Converged { get; init; }

    /// <summary>
    /// The whole run's first law: <c>|stored(end) - stored(0) - integrated net heat|</c>
    /// relative to the magnitudes involved, accumulated step by step at each step's own
    /// theta-point. It checks the TIME INTEGRATION as well as the assembly — a wrong
    /// theta, a capacity matrix off by a factor, or a load applied at the wrong point in
    /// the step all break it while leaving the temperature field superficially plausible.
    /// </summary>
    public required double EnergyBalanceResidual { get; init; }

    /// <summary>Stored energy at t = 0, <c>1' · C · T</c>, measured from absolute zero of
    /// the caller's temperature scale (so it is a difference that means something, not an
    /// absolute).</summary>
    public required double InitialStoredEnergy { get; init; }

    /// <summary>Stored energy at the final step.</summary>
    public required double FinalStoredEnergy { get; init; }

    /// <summary>Milliseconds spent assembling.</summary>
    public required double AssembleMs { get; init; }

    /// <summary>Milliseconds spent in the single factorization.</summary>
    public required double FactorMs { get; init; }

    /// <summary>Milliseconds spent stepping (all steps together).</summary>
    public required double StepMs { get; init; }

    /// <summary>A readable summary.</summary>
    public string ToText() => string.Join(Environment.NewLine,
        $"{Scheme}: {Steps:N0} steps of {TimeStep:G6} to t = {Duration:G6}",
        $"{Factorizations} factorization{(Factorizations == 1 ? "" : "s")} for {Steps:N0} steps",
        $"{(Converged ? "converged" : "NOT CONVERGED")}, worst |Ku-f|/|f| = {WorstRelativeResidual:E2}",
        $"stored energy {InitialStoredEnergy:G6} -> {FinalStoredEnergy:G6}, "
            + $"first-law residual {EnergyBalanceResidual:E2}",
        $"assemble {AssembleMs:F1} ms, factor {FactorMs:F1} ms, step {StepMs:F1} ms");

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>
/// Assembles and solves a <see cref="ThermalModel"/>: Fourier conduction with ONE
/// temperature degree of freedom per node, steady or transient, on
/// <see cref="SparseCholesky"/> (AMD-ordered) or <see cref="SparseSymmetricCG"/>.
///
/// <para><b>Prescribed temperatures are eliminated, not penalised</b> — the structural
/// solver's rule, for the same reasons: the reduced matrix is genuinely positive definite,
/// its conditioning is the model's own, and a prescribed value moves cleanly to the
/// right-hand side as <c>f_free -= K_fc · T_c</c>.</para>
///
/// <para><b>An undriven body is refused BEFORE the factorization, by name</b>, and the
/// thermal case is both simpler and sharper than the structural one. Pure conduction has
/// exactly ONE zero-energy mode — add a constant to T everywhere and every gradient, hence
/// every flux, is unchanged — and on a connected body it either survives the boundary
/// conditions entirely or not at all. So where the structural check needs an
/// eigen-decomposition to find WHICH of six rigid modes partly survive, this one is a
/// boolean per connected body: does it carry a prescribed temperature or a convective
/// facet anywhere? Either kills the constant (a convective surface matrix is strictly
/// positive on constants, since <c>integral(h dA) &gt; 0</c>), and neither present means
/// the matrix is singular and any answer differs from the truth by an arbitrary offset.
/// A solver that returned one anyway would return a temperature field that looks
/// completely reasonable and is wrong by an unknowable constant.</para>
///
/// <para><b>The time integration is a theta scheme</b>,
/// <c>(C/dt + theta·K)·T_next = (C/dt - (1-theta)·K)·T_now + f</c>, with theta = 1 for
/// <see cref="ThermalTimeScheme.BackwardEuler"/> and 1/2 for
/// <see cref="ThermalTimeScheme.CrankNicolson"/>. The capacity matrix C is CONSISTENT —
/// see <see cref="SolveTransient"/> for what lumping would change and why row-sum lumping
/// is not simply available for 10-node elements.</para>
/// </summary>
public static class ThermalSolver
{
    /// <summary>Solves for the steady temperature field.</summary>
    public static ThermalResults Solve(ThermalModel model, ThermalSolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new ThermalSolveOptions();
        var mesh = model.Mesh;

        var conductivityRule = SelectRule(
            ThermalElement.ConductivityRule(mesh.Order), options.ConductivityQuadratureDegree);

        FeaGuards.RequireUsableElements(mesh, conductivityRule, "conductivity");
        RequireConductivity(model);
        RequireDriven(model);

        var reduced = ReducedIndices(model, out int freeCount);
        if (freeCount == 0)
            throw new FeaException(
                "Every node's temperature is prescribed; there is nothing to solve for.");

        var stopwatch = Stopwatch.StartNew();
        var conduction = AssembleConduction(model, conductivityRule);
        var load = AssembleLoad(model);
        var prescribed = PrescribedVector(model);
        var reducedMatrix = Reduce(conduction, 1.0, null, 0.0, reduced, freeCount);
        var rhs = ReduceRhs(load, conduction, 1.0, prescribed, reduced, freeCount);
        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        var free = new double[freeCount];
        var solved = RunLinearSolve(reducedMatrix, rhs, free, options, model, reduced);

        var temperature = Expand(model, reduced, free);
        var balance = EnergyBalance(model, conduction, load, temperature, capacityRate: null);

        var report = BuildReport(
            model, reducedMatrix, rhs, free, freeCount, options, solved, balance,
            assembleMs, solved.FactorMs, solved.SolveMs);

        return new ThermalResults(model, temperature, report, time: 0);
    }

    /// <summary>
    /// Steps the transient conduction equation <c>C·dT/dt + K·T = f</c> forward at a
    /// constant step, returning every stored state.
    ///
    /// <para><b>One factorization serves the whole run.</b> The stepping matrix
    /// <c>C/dt + theta·K</c> depends only on the step, the scheme and the model, so it is
    /// factored once before the loop and every step is a back-substitution. That is the
    /// "factor once, solve many right-hand sides" case
    /// <see cref="FeaSolveMethod.Direct"/> notes does not yet arise structurally — here it
    /// does, and it is what makes the exact default also the fast one.</para>
    ///
    /// <para><b>The capacity matrix is CONSISTENT, and the alternative is worth
    /// stating.</b> The consistent form <c>integral(rho·c·N_i·N_j dV)</c> is the Galerkin
    /// one: it is what makes the measured time-integration orders come out at theory, and
    /// what makes a spatially converged transient converge at the element's own spatial
    /// order rather than at a lumped approximation's. A LUMPED (diagonal) capacity would
    /// buy two things — a monotone answer under backward Euler, since a diagonal capacity
    /// with a positive conduction matrix gives a discrete maximum principle and cannot
    /// undershoot at a sharp front, and the possibility of explicit stepping, where a
    /// diagonal matrix means no solve at all. It costs accuracy: a node's temperature
    /// becomes an average over its patch rather than a point value, and the classic result
    /// is that lumping and consistency bracket the true answer at a moving front.</para>
    ///
    /// <para><b>What is NOT available is the obvious way to lump.</b> Row-sum lumping sets
    /// <c>C_ii = sum_j C_ij = rho·c·integral(N_i dV)</c>, which for a 10-node tetrahedron
    /// is <b>-V/20 at every corner node</b> — a negative heat capacity, i.e. a node that
    /// cools when heated. It is the same integral, and the same surprising number, that
    /// <c>TetElement.BodyLoadWeights</c> already documents for a quadratic element's
    /// gravity load. Any lumping for quadratic elements has to be a scaled-diagonal scheme
    /// instead, which is a different approximation with its own error, so it is filed
    /// rather than smuggled in under one name.</para>
    /// </summary>
    public static ThermalTransientResults SolveTransient(
        ThermalModel model,
        ThermalTransientOptions transient,
        ThermalSolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(transient);
        options ??= new ThermalSolveOptions();
        var mesh = model.Mesh;

        var conductivityRule = SelectRule(
            ThermalElement.ConductivityRule(mesh.Order), options.ConductivityQuadratureDegree);
        var capacityRule = SelectRule(
            ThermalElement.CapacityRule(mesh.Order), options.CapacityQuadratureDegree);

        FeaGuards.RequireUsableElements(mesh, conductivityRule, "conductivity");
        RequireConductivity(model);
        RequireHeatCapacity(model);
        // NOT RequireDriven: a transient of a perfectly insulated body is a legitimate
        // problem (it simply holds its initial energy for ever), because the capacity term
        // C/dt is positive definite on its own and removes the constant null space the
        // steady operator has. The refusal exists for the steady case, where nothing else
        // pins the level.

        var reduced = ReducedIndices(model, out int freeCount);
        if (freeCount == 0)
            throw new FeaException(
                "Every node's temperature is prescribed; there is nothing to step.");

        double theta = transient.Scheme == ThermalTimeScheme.CrankNicolson ? 0.5 : 1.0;
        double dt = transient.TimeStep;

        var stopwatch = Stopwatch.StartNew();
        var conduction = AssembleConduction(model, conductivityRule);
        var capacity = AssembleCapacity(model, capacityRule);
        var load = AssembleLoad(model);
        var prescribed = PrescribedVector(model);

        // A = C/dt + theta·K, factored once. The step is
        //   A_ff · T_next,f = f_f + (B · T_now)_f - (A · T_c)_f
        // with B = C/dt - (1-theta)·K, and both products taken over the FULL node set.
        //
        // The obvious shortcut is to collapse the two prescribed-column terms: if the
        // prescribed values never change then (B_fc - A_fc)·T_c = -K_fc·T_c, exactly the
        // steady solve's correction, and the step needs only a reduced matvec. That is
        // wrong on the FIRST step of a step-change problem, which is the case transients
        // are usually run for: at t = 0 a prescribed node still holds its INITIAL value,
        // not its boundary value, and the shortcut charges nobody for the energy that
        // drags it there. Measured on a quenched bar, the whole-run first law came out
        // 1.1e-2 instead of round-off while every temperature in the field looked right.
        // Keeping the previous state whole costs one extra full matvec per step and makes
        // time-varying prescribed values a change of one line rather than a rewrite.
        var a = Reduce(conduction, theta, capacity, 1.0 / dt, reduced, freeCount);
        var conductionPrescribed = conduction.Multiply(prescribed);
        var capacityPrescribed = capacity.Multiply(prescribed);
        double assembleMs = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        SparseCholesky? factor = null;
        if (options.Method == FeaSolveMethod.Direct)
        {
            try
            {
                factor = SparseCholesky.Factorize(a, options.Ordering);
            }
            catch (InvalidOperationException ex)
            {
                throw SingularSystem(model, a, reduced, ex);
            }
        }
        double factorMs = stopwatch.Elapsed.TotalMilliseconds;

        var current = InitialState(model, transient);
        var states = new List<ThermalResults>();
        var times = new List<double>();

        double storedInitial = Quadratic(capacity, current);
        double integratedHeat = 0;
        double integratedScale = 0;
        double worstResidual = 0;
        bool converged = true;

        // The t = 0 state is an INITIAL CONDITION, not a solution, so its balance is
        // evaluated with no storage rate and reports how far that condition is from steady
        // — informative rather than a correctness check, and documented as such on
        // ThermalTransientResults.Initial.
        states.Add(new ThermalResults(
            model, (double[])current.Clone(),
            StepReport(
                model, current, EnergyBalance(model, conduction, load, current, null),
                options, a, 0, true),
            0));
        times.Add(0);

        var freeCurrent = new double[freeCount];
        var freeNext = new double[freeCount];
        var rhs = new double[freeCount];
        var conductionNow = new double[mesh.NodeCount];
        var capacityNow = new double[mesh.NodeCount];

        stopwatch.Restart();
        for (int step = 1; step <= transient.Steps; step++)
        {
            conduction.Multiply(current, conductionNow);
            capacity.Multiply(current, capacityNow);
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                int r = reduced[node];
                if (r < 0)
                    continue;
                freeCurrent[r] = current[node];
                rhs[r] = load[node]
                    + capacityNow[node] / dt - (1.0 - theta) * conductionNow[node]
                    - (capacityPrescribed[node] / dt + theta * conductionPrescribed[node]);
            }

            if (factor is not null)
            {
                factor.Solve(rhs, freeNext);
            }
            else
            {
                freeCurrent.CopyTo(freeNext, 0);   // a warm start: the previous step
                var cg = SparseSymmetricCG.Solve(a, rhs, freeNext, options.Cg);
                converged &= cg.Converged;
            }
            worstResidual = Math.Max(worstResidual, Residual(a, freeNext, rhs));

            var next = Expand(model, reduced, freeNext);

            // The step's own first law, at its own theta-point — which is where the scheme
            // asserts the balance holds, so checking it anywhere else would measure the
            // scheme's truncation error rather than the assembly's correctness.
            var delta = new double[next.Length];
            for (int i = 0; i < next.Length; i++)
                delta[i] = (next[i] - current[i]) / dt;
            var capacityRate = capacity.Multiply(delta);
            var thetaState = new double[next.Length];
            for (int i = 0; i < next.Length; i++)
                thetaState[i] = theta * next[i] + (1.0 - theta) * current[i];
            var stepBalance = EnergyBalance(model, conduction, load, thetaState, capacityRate);
            integratedHeat += dt * (stepBalance.Applied + stepBalance.Prescribed - stepBalance.Convective);
            integratedScale += dt * stepBalance.Scale;

            current = next;
            bool store = step % transient.StoreEvery == 0 || step == transient.Steps;
            if (store)
            {
                states.Add(new ThermalResults(
                    model, (double[])current.Clone(),
                    StepReport(model, current, stepBalance, options, a, worstResidual, converged),
                    step * dt));
                times.Add(step * dt);
            }
        }
        double stepMs = stopwatch.Elapsed.TotalMilliseconds;

        double storedFinal = Quadratic(capacity, current);
        // The first law over the WHOLE run: the energy that arrived must be the energy
        // that is now stored. Scale is the accumulated magnitude of what flowed, so a run
        // whose net flow cancels does not report a relative error of 1.
        double denominator = integratedScale + Math.Abs(storedFinal - storedInitial);
        double balanceResidual = denominator > 0
            ? Math.Abs(storedFinal - storedInitial - integratedHeat) / denominator
            : 0;

        var report = new ThermalTransientReport
        {
            TimeStep = dt,
            Steps = transient.Steps,
            Duration = transient.Duration,
            Scheme = transient.Scheme,
            Factorizations = factor is not null ? 1 : 0,
            WorstRelativeResidual = worstResidual,
            Converged = converged,
            EnergyBalanceResidual = balanceResidual,
            InitialStoredEnergy = storedInitial,
            FinalStoredEnergy = storedFinal,
            AssembleMs = assembleMs,
            FactorMs = factorMs,
            StepMs = stepMs,
        };

        return new ThermalTransientResults(states, times, report);
    }

    // ---- assembly --------------------------------------------------------------------

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
    /// The FULL conduction matrix over every node: element conductivity plus the surface
    /// matrix every convective facet contributes.
    /// <para>Assembled over all nodes rather than only the free ones because the
    /// prescribed columns, the reaction pass and the energy balance all need it, and
    /// deriving the reduced system from it by filtering (see <see cref="Reduce"/>) is
    /// cheaper and far less error-prone than assembling the same physics twice with two
    /// different index maps.</para>
    /// </summary>
    private static PackedSparseMatrix AssembleConduction(
        ThermalModel model, in TetQuadrature rule)
    {
        var mesh = model.Mesh;
        int perElement = mesh.NodesPerElement;
        var builder = new SparseMatrixBuilder(mesh.NodeCount, mesh.NodeCount);
        var ke = new double[perElement * perElement];
        var positions = new Vector3d[perElement];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = mesh.Position(nodes[i]);
            ThermalElement.Conductivity(
                mesh.Order, positions, model.MaterialOf(e).ThermalConductivity, rule, ke);
            AddSymmetric(builder, nodes, ke, perElement);
        }

        int perFacet = mesh.NodesPerFacet;
        var he = new double[perFacet * perFacet];
        var facetNodes = new Vector3d[perFacet];
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            double h = model.FilmCoefficientOf(f);
            // Exact-zero test: no film is no condition. ThermalModel.Convection has
            // already refused a negative or non-finite one.
            if (h == 0)
                continue;
            var nodes = mesh.Facet(f);
            for (int i = 0; i < perFacet; i++)
                facetNodes[i] = mesh.Position(nodes[i]);
            ThermalElement.FacetConvection(
                mesh.Order, facetNodes, h, TriangleQuadrature.Degree4, he);
            AddSymmetric(builder, nodes, he, perFacet);
        }

        return builder.ToSymmetricUpper();
    }

    /// <summary>The FULL consistent capacity matrix over every node.</summary>
    private static PackedSparseMatrix AssembleCapacity(ThermalModel model, in TetQuadrature rule)
    {
        var mesh = model.Mesh;
        int perElement = mesh.NodesPerElement;
        var builder = new SparseMatrixBuilder(mesh.NodeCount, mesh.NodeCount);
        var ce = new double[perElement * perElement];
        var positions = new Vector3d[perElement];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = mesh.Position(nodes[i]);
            ThermalElement.Capacity(
                mesh.Order, positions, model.MaterialOf(e).VolumetricHeatCapacity, rule, ce);
            AddSymmetric(builder, nodes, ce, perElement);
        }
        return builder.ToSymmetricUpper();
    }

    private static void AddSymmetric(
        SparseMatrixBuilder builder, ReadOnlySpan<int> nodes, double[] local, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int ri = nodes[i];
            int row = i * count;
            for (int j = 0; j < count; j++)
            {
                double v = local[row + j];
                // Exact-zero skip: a structurally absent entry is absent from the sparsity
                // pattern too, which is what CSR means. It decides the pattern, so it is
                // not a tolerance.
                if (v == 0)
                    continue;
                int rj = nodes[j];
                // Symmetric-upper storage: the local matrix is symmetric, so exactly one of
                // the ordered pairs contributes each undirected entry.
                if (ri <= rj)
                    builder.Add(ri, rj, v);
            }
        }
    }

    /// <summary>Nodal heat loads plus every convective facet's KNOWN supply,
    /// <c>integral(h·T_inf·N_i dA)</c>.</summary>
    private static double[] AssembleLoad(ThermalModel model)
    {
        var mesh = model.Mesh;
        var load = new double[mesh.NodeCount];
        for (int node = 0; node < mesh.NodeCount; node++)
            load[node] = model.HeatOf(node);

        int perFacet = mesh.NodesPerFacet;
        Span<Vector3d> positions = stackalloc Vector3d[6];
        Span<double> weights = stackalloc double[6];
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            double supply = model.FilmSupplyOf(f);
            // Exact-zero test: h·T_inf is exactly zero when there is no film AND when the
            // ambient is exactly the scale's zero, and in both cases the supply term
            // contributes nothing — the matrix half of the condition is assembled
            // separately and is not affected by this skip.
            if (supply == 0)
                continue;
            var nodes = mesh.Facet(f);
            for (int i = 0; i < perFacet; i++)
                positions[i] = mesh.Position(nodes[i]);
            ThermalElement.FacetLoad(mesh.Order, positions[..perFacet], supply, weights);
            for (int i = 0; i < perFacet; i++)
                load[nodes[i]] += weights[i];
        }
        return load;
    }

    private static int[] ReducedIndices(ThermalModel model, out int freeCount)
    {
        var reduced = new int[model.Mesh.NodeCount];
        int free = 0;
        for (int node = 0; node < reduced.Length; node++)
            reduced[node] = model.IsPrescribed(node) ? -1 : free++;
        freeCount = free;
        return reduced;
    }

    private static double[] PrescribedVector(ThermalModel model)
    {
        // Prescribed values at constrained nodes and exactly zero elsewhere, so that
        // K · this gives K_fc · T_c in the free rows and nothing else.
        var v = new double[model.Mesh.NodeCount];
        for (int node = 0; node < v.Length; node++)
            v[node] = model.IsPrescribed(node) ? model.PrescribedTemperature(node) : 0;
        return v;
    }

    /// <summary>
    /// The reduced (free-free) combination <c>kCoefficient·K + cCoefficient·C</c>.
    /// <para>The reduced index map is MONOTONE in the node index — free nodes take
    /// increasing slots — so an upper-triangle entry of the full matrix maps to an
    /// upper-triangle entry of the reduced one, which is what lets this feed
    /// <see cref="SparseMatrixBuilder.ToSymmetricUpper"/> directly.</para>
    /// </summary>
    private static PackedSparseMatrix Reduce(
        PackedSparseMatrix k, double kCoefficient,
        PackedSparseMatrix? c, double cCoefficient,
        int[] reduced, int freeCount)
    {
        var builder = new SparseMatrixBuilder(freeCount, freeCount);
        AddReduced(builder, k, kCoefficient, reduced);
        if (c is not null)
            AddReduced(builder, c, cCoefficient, reduced);
        return builder.ToSymmetricUpper();
    }

    private static void AddReduced(
        SparseMatrixBuilder builder, PackedSparseMatrix m, double coefficient, int[] reduced)
    {
        for (int row = 0; row < m.Rows; row++)
        {
            int ri = reduced[row];
            if (ri < 0)
                continue;
            var columns = m.RowColumns(row);
            var values = m.RowValues(row);
            for (int e = 0; e < columns.Length; e++)
            {
                int rj = reduced[columns[e]];
                if (rj < 0)
                    continue;
                builder.Add(ri, rj, coefficient * values[e]);
            }
        }
    }

    /// <summary>The reduced right-hand side <c>f_f - K_fc · T_c</c>.</summary>
    private static double[] ReduceRhs(
        double[] load, PackedSparseMatrix k, double kCoefficient,
        double[] prescribed, int[] reduced, int freeCount)
    {
        var correction = k.Multiply(prescribed);
        var rhs = new double[freeCount];
        for (int node = 0; node < reduced.Length; node++)
        {
            int r = reduced[node];
            if (r >= 0)
                rhs[r] = load[node] - kCoefficient * correction[node];
        }
        return rhs;
    }

    private static double[] Expand(ThermalModel model, int[] reduced, double[] free)
    {
        var temperature = new double[model.Mesh.NodeCount];
        for (int node = 0; node < temperature.Length; node++)
        {
            int r = reduced[node];
            temperature[node] = r >= 0 ? free[r] : model.PrescribedTemperature(node);
        }
        return temperature;
    }

    private static double[] InitialState(ThermalModel model, ThermalTransientOptions transient)
    {
        int n = model.Mesh.NodeCount;
        var state = new double[n];
        if (transient.InitialField is { } field)
        {
            if (field.Count != n)
                throw new FeaException(
                    $"The initial field has {field.Count:N0} values but the analysis mesh has "
                    + $"{n:N0} nodes. A transient's initial condition is per NODE of the analysis "
                    + "mesh, which for quadratic elements includes the mid-edge nodes.");
            for (int i = 0; i < n; i++)
                state[i] = field[i];
        }
        else
        {
            Array.Fill(state, transient.InitialTemperature);
        }

        // A prescribed temperature wins over the initial condition at t = 0 — see
        // ThermalTransientOptions.InitialField for why, and for what the alternative
        // measured. Doing it HERE rather than only inside the stepping is what keeps the
        // stored t = 0 state and the arithmetic that steps away from it consistent, so the
        // whole-run first law closes at round-off instead of at 1.1e-2.
        for (int i = 0; i < n; i++)
        {
            if (model.IsPrescribed(i))
                state[i] = model.PrescribedTemperature(i);
        }
        return state;
    }

    // ---- diagnostics -----------------------------------------------------------------

    private readonly record struct Balance(
        double Applied, double Prescribed, double Convective, double Storage,
        double Scale, double Residual);

    /// <summary>
    /// The discrete first law at one temperature state: heat in through sources and
    /// through prescribed boundaries, out through convection, and into storage.
    ///
    /// <para><b>The prescribed-boundary term is what the equations say it is</b>, not a
    /// separately modelled flux: the residual <c>r = C·dT/dt + K·T - f</c> is exactly zero
    /// at a free node (that IS the equation) and at a prescribed node is the heat the
    /// boundary must inject to hold the temperature. Summing it needs no new integration
    /// and cannot disagree with the solve.</para>
    ///
    /// <para><b>The capacity term has to be in that residual, and leaving it out is a
    /// convincing near-miss.</b> A first version summed only <c>K·T - f</c> at prescribed
    /// nodes — right for a steady solve, where there is no capacity — and the whole-run
    /// first law then failed by <b>4.4%</b> on a quenched bar while every temperature in it
    /// was correct. The energy a prescribed node stores as it is dragged to its boundary
    /// value is real, is charged to that boundary, and is invisible in the temperature
    /// field.</para>
    /// </summary>
    /// <param name="capacityRate">Per-node <c>C·dT/dt</c>, or null for a steady state.</param>
    private static Balance EnergyBalance(
        ThermalModel model, PackedSparseMatrix conduction, double[] load,
        double[] temperature, double[]? capacityRate)
    {
        var mesh = model.Mesh;
        var product = conduction.Multiply(temperature);

        double applied = 0, prescribedHeat = 0, scale = 0, storageRate = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            applied += model.HeatOf(node);
            scale += Math.Abs(model.HeatOf(node));
            double stored = capacityRate is null ? 0 : capacityRate[node];
            storageRate += stored;
            if (!model.IsPrescribed(node))
                continue;
            double r = product[node] + stored - load[node];
            prescribedHeat += r;
            scale += Math.Abs(r);
        }

        // Convective loss, integral(h·(T - T_inf) dA), from the facet load weights rather
        // than from a second surface-matrix pass: summing the surface matrix over its rows
        // gives integral(h·N_j dA) by the partition of unity, which IS the load weight — so
        // asking FacetLoadWeights is asking the same integral the assembly used.
        double convective = 0;
        int perFacet = mesh.NodesPerFacet;
        Span<Vector3d> positions = stackalloc Vector3d[6];
        Span<double> weights = stackalloc double[6];
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            double h = model.FilmCoefficientOf(f);
            if (h == 0)
                continue;
            var nodes = mesh.Facet(f);
            for (int i = 0; i < perFacet; i++)
                positions[i] = mesh.Position(nodes[i]);
            TetElement.FacetLoadWeights(mesh.Order, positions[..perFacet], weights);
            double weighted = 0, area = 0;
            for (int i = 0; i < perFacet; i++)
            {
                weighted += weights[i] * temperature[nodes[i]];
                area += weights[i];
            }
            double loss = h * weighted - model.FilmSupplyOf(f) * area;
            convective += loss;
            scale += Math.Abs(loss);
        }
        scale += Math.Abs(storageRate);

        double residual = scale > 0
            ? Math.Abs(applied + prescribedHeat - convective - storageRate) / scale
            : 0;
        return new Balance(applied, prescribedHeat, convective, storageRate, scale, residual);
    }

    private readonly record struct LinearSolve(
        bool Converged, int Iterations, int FactorNonZeros, double FactorMs, double SolveMs);

    private static LinearSolve RunLinearSolve(
        PackedSparseMatrix matrix, double[] rhs, double[] free,
        ThermalSolveOptions options, ThermalModel model, int[] reduced)
    {
        var stopwatch = Stopwatch.StartNew();
        if (options.Method == FeaSolveMethod.Direct)
        {
            SparseCholesky factor;
            try
            {
                factor = SparseCholesky.Factorize(matrix, options.Ordering);
            }
            catch (InvalidOperationException ex)
            {
                throw SingularSystem(model, matrix, reduced, ex);
            }
            double factorMs = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            factor.Solve(rhs, free);
            return new LinearSolve(
                true, 0, factor.FactorNonZeroCount, factorMs, stopwatch.Elapsed.TotalMilliseconds);
        }

        var report = SparseSymmetricCG.Solve(matrix, rhs, free, options.Cg);
        return new LinearSolve(
            report.Converged, report.Iterations, 0, 0, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static ThermalSolveReport BuildReport(
        ThermalModel model, PackedSparseMatrix matrix, double[] rhs, double[] free,
        int freeCount, ThermalSolveOptions options, LinearSolve solved, Balance balance,
        double assembleMs, double factorMs, double solveMs)
    {
        var mesh = model.Mesh;
        var temperature = Expand(model, ReducedIndices(model, out _), free);
        double min = double.MaxValue, max = double.MinValue;
        foreach (double t in temperature)
        {
            min = Math.Min(min, t);
            max = Math.Max(max, t);
        }

        return new ThermalSolveReport
        {
            NodeCount = mesh.NodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            TotalDofs = mesh.NodeCount,
            FreeDofs = freeCount,
            MatrixNonZeros = matrix.NonZeroCount,
            FactorNonZeros = solved.FactorNonZeros,
            Method = options.Method,
            Ordering = options.Method == FeaSolveMethod.Direct
                ? options.Ordering
                : SparseOrdering.Natural,
            Converged = solved.Converged,
            Iterations = solved.Iterations,
            RelativeResidual = Residual(matrix, free, rhs),
            MinTemperature = min,
            MaxTemperature = max,
            AppliedHeat = balance.Applied,
            PrescribedHeat = balance.Prescribed,
            ConvectiveHeat = balance.Convective,
            StorageRate = balance.Storage,
            EnergyBalanceResidual = balance.Residual,
            AssembleMs = assembleMs,
            FactorMs = factorMs,
            SolveMs = solveMs,
            // The SAME advisory rule the structural solver uses, asked rather than
            // restated: a heuristic stated twice is a heuristic that will drift.
            Advisory = StructuralSolver.AdvisoryFor(
                options.Method, freeCount, factorMs, assembleMs + factorMs + solveMs),
        };
    }

    /// <summary>A per-step report for a stored transient state. Timings live on the
    /// TRANSIENT report: a step has no assembly and no factorization of its own, which is
    /// the whole point of a constant step, so reporting zeros here is the honest number
    /// rather than a missing one.
    /// <para>The balance is the one measured at the step's THETA-POINT, which is where the
    /// scheme asserts the first law holds; evaluating it at the stored end-of-step state
    /// instead would measure the scheme's truncation error and report it as an assembly
    /// error.</para></summary>
    private static ThermalSolveReport StepReport(
        ThermalModel model, double[] temperature, in Balance balance,
        ThermalSolveOptions options, PackedSparseMatrix matrix,
        double residual, bool converged)
    {
        var mesh = model.Mesh;
        double min = double.MaxValue, max = double.MinValue;
        foreach (double t in temperature)
        {
            min = Math.Min(min, t);
            max = Math.Max(max, t);
        }

        return new ThermalSolveReport
        {
            NodeCount = mesh.NodeCount,
            ElementCount = mesh.ElementCount,
            Order = mesh.Order,
            TotalDofs = mesh.NodeCount,
            FreeDofs = matrix.Rows,
            MatrixNonZeros = matrix.NonZeroCount,
            FactorNonZeros = 0,
            Method = options.Method,
            Ordering = options.Method == FeaSolveMethod.Direct
                ? options.Ordering
                : SparseOrdering.Natural,
            Converged = converged,
            Iterations = 0,
            RelativeResidual = residual,
            MinTemperature = min,
            MaxTemperature = max,
            AppliedHeat = balance.Applied,
            PrescribedHeat = balance.Prescribed,
            ConvectiveHeat = balance.Convective,
            StorageRate = balance.Storage,
            EnergyBalanceResidual = balance.Residual,
            AssembleMs = 0,
            FactorMs = 0,
            SolveMs = 0,
        };
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

    /// <summary><c>x' · M · x</c> — the stored energy when M is the capacity matrix and x
    /// the temperature field, measured from the caller's zero.</summary>
    private static double Quadratic(PackedSparseMatrix m, double[] x)
    {
        // Not x'Mx: the stored energy of a temperature FIELD is integral(rho·c·T dV),
        // which is 1'·C·x, a linear functional. Squaring it would be an energy-like number
        // with no physical meaning and would break the first-law check.
        var product = m.Multiply(x);
        double sum = 0;
        foreach (double v in product)
            sum += v;
        return sum;
    }

    // ---- refusals --------------------------------------------------------------------

    private static void RequireConductivity(ThermalModel model)
    {
        var offenders = new List<string>();
        var seen = new HashSet<string>();
        for (int e = 0; e < model.Mesh.ElementCount; e++)
        {
            var material = model.MaterialOf(e);
            if (material.ThermalConductivity > 0)
                continue;
            if (seen.Add(material.Name))
                offenders.Add($"'{material.Name}' (region {model.Mesh.RegionOf(e)})");
        }
        if (offenders.Count == 0)
            return;

        throw new FeaException(
            $"A conduction solve needs a positive thermal conductivity, and "
            + $"{string.Join(", ", offenders)} state none. Conductivity is the only property "
            + "the conduction matrix is built from, so a zero makes it identically zero — the "
            + "answer would not be inaccurate, it would not exist. Build the material with its "
            + "thermalConductivity, or call Material.WithThermal(k, c, alpha); the catalogue in "
            + "Materials states k in mW/(mm.K), which is numerically the SI W/(m.K).");
    }

    private static void RequireHeatCapacity(ThermalModel model)
    {
        var offenders = new List<string>();
        var seen = new HashSet<string>();
        for (int e = 0; e < model.Mesh.ElementCount; e++)
        {
            var material = model.MaterialOf(e);
            if (material.VolumetricHeatCapacity > 0)
                continue;
            if (seen.Add(material.Name))
            {
                offenders.Add(
                    $"'{material.Name}' (density {material.Density:G4}, "
                    + $"specific heat {material.SpecificHeat:G4})");
            }
        }
        if (offenders.Count == 0)
            return;

        throw new FeaException(
            $"A transient solve needs a positive volumetric heat capacity rho.c, and "
            + $"{string.Join(", ", offenders)} give zero. A body with no capacity reaches its "
            + "steady state instantly, so the transient is not a slower version of the steady "
            + "problem — it is the steady problem, and ThermalSolver.Solve is the call for it. "
            + "Specific heat in the mm/N/tonne/s system is the SI J/(kg.K) times 1e6.");
    }

    /// <summary>
    /// Refuses a steady model whose body has nothing setting its temperature LEVEL, per
    /// connected body.
    ///
    /// <para>Conduction's null space on a connected body is exactly the constants — add
    /// any constant to T and every gradient, hence every flux, is unchanged — so the check
    /// is a boolean rather than the eigen-decomposition the structural restraint check
    /// needs for its six partially-surviving rigid modes. A prescribed temperature
    /// anywhere kills the constant by elimination; a convective facet kills it because its
    /// surface matrix is strictly positive on constants
    /// (<c>integral(h dA) &gt; 0</c>). A pure heat flux does NOT: it is a Neumann
    /// condition and says nothing about the level.</para>
    /// </summary>
    private static void RequireDriven(ThermalModel model)
    {
        var mesh = model.Mesh;
        var (component, componentCount) = mesh.ConnectedComponents();

        var driven = new bool[componentCount];
        var nodeCount = new int[componentCount];
        var centroid = new Vector3d[componentCount];
        var appliedHeat = new double[componentCount];

        for (int v = 0; v < mesh.NodeCount; v++)
        {
            int c = component[v];
            nodeCount[c]++;
            centroid[c] += mesh.Position(v);
            appliedHeat[c] += model.HeatOf(v);
            if (model.IsPrescribed(v))
                driven[c] = true;
        }
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            if (model.FilmCoefficientOf(f) > 0)
                driven[component[mesh.Facet(f)[0]]] = true;
        }

        for (int c = 0; c < componentCount; c++)
        {
            if (driven[c] || nodeCount[c] == 0)
                continue;

            var centre = centroid[c] / nodeCount[c];
            string body = componentCount == 1
                ? "The model"
                : $"Body {c + 1} of {componentCount} ({nodeCount[c]:N0} nodes near {centre})";
            throw new FeaException(
                $"{body} has no prescribed temperature and no convective surface, so its "
                + "conduction matrix is singular: adding any constant to the temperature "
                + "everywhere leaves every gradient, and therefore every heat flux and every "
                + "boundary condition, exactly as it was. There is no unique answer to return, "
                + "and a field that merely looked reasonable would be wrong by an unknowable "
                + "offset. Prescribe a temperature somewhere (one node is enough to set the "
                + "datum), or give a surface a convective condition."
                + (Math.Abs(appliedHeat[c]) > 0
                    ? $" Note that {appliedHeat[c]:G4} of heat is applied to this body with "
                      + "nowhere to leave, so no steady state exists for it either — a "
                      + "transient solve is the honest model of that."
                    : ""));
        }
    }

    private static FeaException SingularSystem(
        ThermalModel model, PackedSparseMatrix matrix, int[] reduced, Exception inner)
    {
        // The driven check has already passed, so this is an isolated node, a body joined
        // only through a degenerate element, or a mesh too ill-conditioned to factor.
        var dead = new List<string>();
        var inverse = new int[matrix.Rows];
        for (int node = 0; node < reduced.Length; node++)
        {
            if (reduced[node] >= 0)
                inverse[reduced[node]] = node;
        }
        int deadCount = 0;
        for (int r = 0; r < matrix.Rows; r++)
        {
            // Exact-zero test, deliberately: a conduction matrix's diagonal entry is
            // integral(k·|grad N|^2 dV), strictly positive wherever a node influences the
            // field at all, so "not positive" is a structural fact rather than a tolerance
            // question.
            if (matrix[r, r] > 0)
                continue;
            deadCount++;
            if (dead.Count >= 8)
                continue;
            int node = inverse[r];
            dead.Add($"node {node} at {model.Mesh.Position(node)}");
        }

        string detail = dead.Count > 0
            ? $" Nodes with no conductivity: {string.Join("; ", dead)}"
                + (deadCount > dead.Count ? " (and more)" : "") + "."
            : " Every node has conductivity, so the cause is a mesh too ill-conditioned to"
                + " factor.";

        return new FeaException(
            "The assembled conduction matrix is not positive definite, even though every "
            + "connected body has something setting its temperature level."
            + detail + FeaGuards.DescribeElementShape(model.Mesh)
            + $" (Underlying: {inner.Message})", inner);
    }
}
