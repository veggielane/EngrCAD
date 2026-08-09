using EngrCAD.Core;
using EngrCAD.Core.Solvers;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// One stored instant of a transient run: the displacement (with everything
/// <see cref="StructuralResults"/> derives from it), the velocity and acceleration that go
/// with it, and the energies at that moment.
///
/// <para><b>Velocity and acceleration are here rather than on
/// <see cref="StructuralResults"/></b> because that type is the answer to a STATIC problem
/// and has no room for them; a transient state is a static result plus the two derivatives
/// the integrator carries. Everything a static solve publishes — stress, von Mises,
/// <c>MeshField</c>s, <c>.vtu</c> — is therefore available per instant through
/// <see cref="Results"/> with nothing restated.</para>
/// </summary>
public sealed class TransientState
{
    private readonly Vector3d[] _velocity;
    private readonly Vector3d[] _acceleration;

    internal TransientState(
        int step, double time, double loadFactor,
        StructuralResults results, Vector3d[] velocity, Vector3d[] acceleration,
        double kineticEnergy, double strainEnergy,
        Vector3d inertiaForce, Vector3d dampingForce)
    {
        Step = step;
        Time = time;
        LoadFactor = loadFactor;
        Results = results;
        _velocity = velocity;
        _acceleration = acceleration;
        KineticEnergy = kineticEnergy;
        StrainEnergy = strainEnergy;
        InertiaForce = inertiaForce;
        DampingForce = dampingForce;
    }

    /// <summary>Which step this is; 0 is the initial condition.</summary>
    public int Step { get; }

    /// <summary>The time, <c>Step · TimeStep</c>.</summary>
    public double Time { get; }

    /// <summary>The load history's multiplier at this instant.</summary>
    public double LoadFactor { get; }

    /// <summary>
    /// The displacement field and everything derived from it — stress, von Mises,
    /// <c>Fields()</c>, <c>WriteVtu</c>.
    ///
    /// <para><b>Its report's <see cref="FeaSolveReport.EquilibriumResidual"/> is d'Alembert's
    /// balance</b>, applied + reaction - inertia - damping, which is the same statement the
    /// static field makes with the two dynamic terms carried: a moving body's applied and
    /// support forces do NOT sum to zero, they sum to what is accelerating it. The
    /// resultants are on this object as <see cref="InertiaForce"/> and
    /// <see cref="DampingForce"/> so the check can be read term by term.</para>
    /// </summary>
    public StructuralResults Results { get; }

    /// <summary>Nodal displacements, indexed by node.</summary>
    public IReadOnlyList<Vector3d> Displacement => Results.Displacement;

    /// <summary>Nodal velocities, indexed by node.</summary>
    public IReadOnlyList<Vector3d> Velocity => _velocity;

    /// <summary>Nodal accelerations, indexed by node.</summary>
    public IReadOnlyList<Vector3d> Acceleration => _acceleration;

    /// <summary>Displacement of one node.</summary>
    public Vector3d DisplacementAt(int node) => Results.DisplacementAt(node);

    /// <summary>Velocity of one node.</summary>
    public Vector3d VelocityAt(int node)
    {
        RequireNode(node);
        return _velocity[node];
    }

    /// <summary>Acceleration of one node.</summary>
    public Vector3d AccelerationAt(int node)
    {
        RequireNode(node);
        return _acceleration[node];
    }

    private void RequireNode(int node)
    {
        if ((uint)node >= (uint)_velocity.Length)
            throw new ArgumentOutOfRangeException(
                nameof(node), node, $"The analysis mesh has {_velocity.Length} nodes.");
    }

    /// <summary>Kinetic energy <c>½·v'·M·v</c>, from the assembled consistent mass matrix.</summary>
    public double KineticEnergy { get; }

    /// <summary>Strain energy <c>½·u'·K·u</c>, from the assembled stiffness matrix.
    /// <para>Computed from the SAME matrix the step was solved with rather than by a second
    /// element pass, so it cannot disagree with the integration in the last bits — which
    /// matters here because the energy balance is checked as an identity.</para></summary>
    public double StrainEnergy { get; }

    /// <summary>Mechanical energy, <c>KineticEnergy + StrainEnergy</c>.</summary>
    public double TotalEnergy => KineticEnergy + StrainEnergy;

    /// <summary>Resultant inertia force <c>sum(M·a)</c> — what the applied and support forces
    /// are net accelerating.</summary>
    public Vector3d InertiaForce { get; }

    /// <summary>Resultant viscous force <c>sum(C·v)</c>. Exactly zero without damping, and
    /// with stiffness-proportional damping alone (the stiffness matrix annihilates a rigid
    /// translation, so <c>K·v</c> can carry no net force).</summary>
    public Vector3d DampingForce { get; }

    /// <summary>The largest displacement magnitude at this instant.</summary>
    public double MaxDisplacement => Results.MaxDisplacement;

    /// <summary>The largest velocity magnitude at this instant.</summary>
    public double MaxVelocity
    {
        get
        {
            double max = 0;
            foreach (var v in _velocity)
                max = Math.Max(max, v.Length);
            return max;
        }
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"t = {Time:G6}: |u| <= {MaxDisplacement:G6}, |v| <= {MaxVelocity:G6}, "
        + $"energy {TotalEnergy:G6} (kinetic {KineticEnergy:G6}, strain {StrainEnergy:G6})";
}

/// <summary>
/// What a transient run did, beside the per-step states: how it stepped, what it factored,
/// and the ENERGY BALANCE, which is this solver's analogue of the thermal first law and the
/// static equilibrium check.
/// </summary>
public sealed record TransientSolveReport
{
    /// <summary>Nodes in the analysis mesh.</summary>
    public required int NodeCount { get; init; }

    /// <summary>Elements in the analysis mesh.</summary>
    public required int ElementCount { get; init; }

    /// <summary>Linear or quadratic elements.</summary>
    public required ElementOrder Order { get; init; }

    /// <summary>Total degrees of freedom, <c>3 · nodes</c>.</summary>
    public required int TotalDofs { get; init; }

    /// <summary>Degrees of freedom actually stepped.</summary>
    public required int FreeDofs { get; init; }

    /// <summary>The constant time step.</summary>
    public required double TimeStep { get; init; }

    /// <summary>Steps taken.</summary>
    public required int Steps { get; init; }

    /// <summary>Total time spanned.</summary>
    public required double Duration { get; init; }

    /// <summary>Which scheme stepped.</summary>
    public required TimeIntegration Integration { get; init; }

    /// <summary>The damping model, as text.</summary>
    public required string Damping { get; init; }

    /// <summary>Stored entries of the effective stiffness (upper triangle).</summary>
    public required int MatrixNonZeros { get; init; }

    /// <summary>Stored entries of its Cholesky factor, or 0 for an iterative solve.</summary>
    public required int FactorNonZeros { get; init; }

    /// <summary>Which linear solver ran.</summary>
    public required FeaSolveMethod Method { get; init; }

    /// <summary>The elimination ordering, for a direct solve.</summary>
    public required SparseOrdering Ordering { get; init; }

    /// <summary>
    /// How many matrix factorizations the whole run needed.
    ///
    /// <para><b>One for the stepping, plus one for the mass matrix when the initial
    /// acceleration has to be solved for.</b> The stepping matrix
    /// <c>a0·M + (1+alpha)·a1·C + (1+alpha)·K</c> depends only on the step, the scheme and the
    /// model, so it is factored once and every step is a back-substitution — the same
    /// amortisation <c>ThermalSolver.SolveTransient</c> makes. The second is
    /// <c>M·a(0) = f(0) - C·v(0) - K·u(0)</c>, which is a solve against the MASS matrix and
    /// is skipped only when that right-hand side is exactly zero. Assuming <c>a(0) = 0</c>
    /// instead is a silent modelling error that looks like a startup transient, so it is
    /// never assumed.</para>
    /// </summary>
    public required int Factorizations { get; init; }

    /// <summary>
    /// For an ADAPTIVE run (<see cref="TransientSolver.SolveAdaptive"/>), how many steps were
    /// actually taken — fewer than the finest uniform grid's when coarse steps carried the
    /// quiet stretches, which is the efficiency the adaptive path buys. Null for a constant
    /// run, whose step count is <see cref="Steps"/>.
    /// </summary>
    public int? AdaptiveSteps { get; init; }

    /// <summary>
    /// For an adaptive run, how many steps were taken at each size level (index 0 the coarsest),
    /// so the split between the impact and the ring-down is visible. Its length is the size-set
    /// size, and the number of NON-zero entries is how many distinct sizes were factored — the
    /// factorization count minus any mass factorization. Null for a constant run.
    /// </summary>
    public IReadOnlyList<int>? StepsPerLevel { get; init; }

    /// <summary>Worst relative linear-system residual over all steps.</summary>
    public required double WorstRelativeResidual { get; init; }

    /// <summary>True unless some step's iterative solve hit its cap.</summary>
    public required bool Converged { get; init; }

    /// <summary>Mechanical energy at <c>t = 0</c>.</summary>
    public required double InitialEnergy { get; init; }

    /// <summary>Mechanical energy at the final step.</summary>
    public required double FinalEnergy { get; init; }

    /// <summary>The largest mechanical energy reached at any step — the scale the balance
    /// residual is measured against, so that a run with no external work still reports a
    /// meaningful relative figure.</summary>
    public required double PeakEnergy { get; init; }

    /// <summary>Work done by the applied loads over the run, accumulated step by step as
    /// <c>½·(u(n+1) - u(n))'·(f(n) + f(n+1))</c>.</summary>
    public required double WorkDone { get; init; }

    /// <summary>Energy dissipated by viscous damping, accumulated as
    /// <c>dt·w'·C·w</c> with <c>w = (v(n) + v(n+1))/2</c>.</summary>
    public required double Dissipated { get; init; }

    /// <summary>
    /// <c>|(E_final - E_initial) - WorkDone + Dissipated|</c> over
    /// <c>|WorkDone| + |Dissipated| + PeakEnergy</c> — the mechanical energy balance.
    ///
    /// <para><b>For <see cref="TimeIntegration.AverageAcceleration"/> this is an IDENTITY and
    /// holds to round-off</b>, damped and loaded cases included. The trapezoidal update
    /// relations give <c>u(n+1) - u(n) = (dt/2)(v(n) + v(n+1))</c> and
    /// <c>v(n+1) - v(n) = (dt/2)(a(n) + a(n+1))</c>, from which
    /// <c>E(n+1) - E(n) = (dt/4)(v(n)+v(n+1))'[M(a(n)+a(n+1)) + K(u(n)+u(n+1))]</c>; putting
    /// the equation of motion at both ends into the bracket turns it into exactly the work
    /// and dissipation terms above. Nothing is approximated, so a drift here is a defect and
    /// not a tolerance.</para>
    ///
    /// <para><b>For a dissipative scheme it is the measurement rather than the check</b>: the
    /// residual IS the energy the algorithm removed, which is the number a user of HHT or of
    /// a numerically damped Newmark member wants to see. It is reported the same way in both
    /// cases because the quantity is the same; what changes is whether zero is the expected
    /// answer, and <see cref="TimeIntegration.IsSecondOrder"/> together with
    /// <see cref="TimeIntegration.SpectralRadiusAtInfinity"/> says which case a run is in.</para>
    ///
    /// <para>The denominator is a sum of magnitudes plus the peak energy, deliberately: a
    /// free vibration does no work and dissipates nothing, so a denominator built only from
    /// the flows would be zero for the run that most wants the check. Adding the peak energy
    /// makes the free-vibration reading exactly the relative energy drift.</para>
    /// </summary>
    public required double EnergyBalanceResidual { get; init; }

    /// <summary>The largest displacement magnitude at any STORED step, and when it
    /// occurred.</summary>
    public required double PeakDisplacement { get; init; }

    /// <summary>The time of <see cref="PeakDisplacement"/>.</summary>
    public required double PeakDisplacementTime { get; init; }

    /// <summary>Worst dynamic equilibrium residual over the stored steps (see
    /// <see cref="TransientState.Results"/>).</summary>
    public required double WorstEquilibriumResidual { get; init; }

    /// <summary>Milliseconds spent assembling.</summary>
    public required double AssembleMs { get; init; }

    /// <summary>Milliseconds spent factoring.</summary>
    public required double FactorMs { get; init; }

    /// <summary>Milliseconds spent stepping (all steps together).</summary>
    public required double StepMs { get; init; }

    /// <summary>A note about this run worth acting on, or null — produced by the SAME rule
    /// the structural and thermal solvers use, with the step count in the load-case slot
    /// because a transient amortises its one factorization over exactly that many
    /// substitutions.</summary>
    public string? Advisory { get; init; }

    /// <summary>A readable summary.</summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            $"{ElementCount:N0} {(Order == ElementOrder.Linear ? "linear" : "quadratic")} elements, "
                + $"{NodeCount:N0} nodes, {FreeDofs:N0} of {TotalDofs:N0} DOF free",
            $"{Integration}, {Damping}",
            $"{Steps:N0} steps of {TimeStep:G6} to t = {Duration:G6}",
            $"{Factorizations} factorization{(Factorizations == 1 ? "" : "s")} for {Steps:N0} steps"
                + (FactorNonZeros > 0
                    ? $", factor {FactorNonZeros:N0} nnz (fill "
                      + $"{(double)FactorNonZeros / Math.Max(1, MatrixNonZeros):F1}x, {Ordering})"
                    : ""),
            $"{(Converged ? "converged" : "NOT CONVERGED")}, worst |Au-b|/|b| = {WorstRelativeResidual:E2}",
            $"peak |u| {PeakDisplacement:G6} at t = {PeakDisplacementTime:G6}, "
                + $"worst equilibrium {WorstEquilibriumResidual:E2}",
            $"energy {InitialEnergy:G6} -> {FinalEnergy:G6} (peak {PeakEnergy:G6}), "
                + $"work {WorkDone:G6}, dissipated {Dissipated:G6}, "
                + $"balance {EnergyBalanceResidual:E2}",
            $"assemble {AssembleMs:F1} ms, factor {FactorMs:F1} ms, step {StepMs:F1} ms",
        };
        if (Advisory is { } advisory)
            lines.Add(advisory);
        return string.Join(Environment.NewLine, lines);
    }

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>
/// A transient dynamic run: every stored instant, and what the run did.
///
/// <para>Shaped after <c>ThermalTransientResults</c> — a list of states at their times plus
/// one run report — because the two are the same kind of answer and a caller who has driven
/// one should not have to learn a second vocabulary for the other.</para>
/// </summary>
public sealed class TransientResults
{
    private readonly TransientState[] _states;
    private readonly double[] _times;

    internal TransientResults(
        StructuralModel model, TransientState[] states, TransientSolveReport report)
    {
        Model = model;
        _states = states;
        _times = [.. states.Select(s => s.Time)];
        Report = report;
    }

    /// <summary>The model that was stepped.</summary>
    public StructuralModel Model { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>What the run did.</summary>
    public TransientSolveReport Report { get; }

    /// <summary>
    /// True when the run was driven by a <see cref="TransientSolveOptions.BaseMotion"/>, in which
    /// case every displacement here is RELATIVE to the moving base — the right quantity for
    /// stress, since a rigid ground motion carries none. The absolute displacement is this plus
    /// the rigid ground translation <c>iota_d·u_g(t)</c>, which the caller adds if they need
    /// total motion. False (the default) for an ordinary force-driven run, whose displacement is
    /// absolute.
    /// </summary>
    public bool IsRelativeToBase { get; init; }

    /// <summary>The base motion's direction when <see cref="IsRelativeToBase"/> is true (the
    /// rigid translation axis the relative displacement is measured against), else null.</summary>
    public Vector3d? BaseDirection { get; init; }

    /// <summary>Every stored state, in time order. The initial condition is always stored,
    /// and so is the final step.</summary>
    public IReadOnlyList<TransientState> States => _states;

    /// <summary>The stored times, matching <see cref="States"/>.</summary>
    public IReadOnlyList<double> Times => _times;

    /// <summary>The initial condition.</summary>
    public TransientState Initial => _states[0];

    /// <summary>The final stored state.</summary>
    public TransientState Final => _states[^1];

    /// <summary>
    /// The stored state NEAREST the given time.
    ///
    /// <para>Nearest rather than interpolated, deliberately: an interpolated displacement is
    /// not a solution of anything — it satisfies neither the equation of motion nor the
    /// scheme's own update relations — so returning one would hand back a plausible field
    /// with no provenance. <see cref="TransientState.Time"/> says which instant came back.</para>
    /// </summary>
    public TransientState At(double time)
    {
        int best = 0;
        double bestGap = Math.Abs(_times[0] - time);
        for (int i = 1; i < _times.Length; i++)
        {
            double gap = Math.Abs(_times[i] - time);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = i;
            }
        }
        return _states[best];
    }

    /// <summary>One node's displacement history over the stored states.</summary>
    public IReadOnlyList<Vector3d> DisplacementHistory(int node) =>
        [.. _states.Select(s => s.DisplacementAt(node))];

    /// <summary>One node's velocity history over the stored states.</summary>
    public IReadOnlyList<Vector3d> VelocityHistory(int node) =>
        [.. _states.Select(s => s.VelocityAt(node))];

    /// <summary>The mechanical energy history over the stored states.</summary>
    public IReadOnlyList<double> EnergyHistory => [.. _states.Select(s => s.TotalEnergy)];

    /// <summary>
    /// The displacement, velocity and acceleration of one stored instant as
    /// <see cref="MeshField"/>s on the analysis mesh — the same publishing seam a static
    /// result uses, so a viewer or a <c>.vtu</c> writer sees no difference.
    /// </summary>
    public IReadOnlyList<MeshField> Fields(int index, string lengthUnits = "mm")
    {
        if ((uint)index >= (uint)_states.Length)
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"The run stored {_states.Length} states.");
        var state = _states[index];
        return
        [
            .. state.Results.Fields(lengthUnits),
            MeshField.Vector(FieldNames.Velocity, $"{lengthUnits}/s", state.Velocity),
            MeshField.Vector(FieldNames.Acceleration, $"{lengthUnits}/s^2", state.Acceleration),
        ];
    }

    /// <summary>The field names this class adds beyond a static result's.</summary>
    public static class FieldNames
    {
        /// <summary>Nodal velocity.</summary>
        public const string Velocity = "Velocity";

        /// <summary>Nodal acceleration.</summary>
        public const string Acceleration = "Acceleration";
    }

    /// <inheritdoc/>
    public override string ToString() => Report.ToText();
}
