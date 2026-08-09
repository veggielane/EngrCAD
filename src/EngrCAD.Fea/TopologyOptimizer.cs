using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>
/// What <see cref="TopologyOptimizer.Minimize"/> is asked for. Two of these have no default
/// and say so in the type, because neither is a numerical knob.
/// </summary>
public sealed record TopologyOptions
{
    /// <summary>
    /// The fraction of the mesh's volume the answer may use, in (0, 1) — the constraint, met
    /// EXACTLY at every iteration by the optimality-criteria bisection rather than
    /// approached.
    /// </summary>
    public required double VolumeFraction { get; init; }

    /// <summary>
    /// The filter radius <c>r_min</c>, in MODEL UNITS.
    ///
    /// <para><b>An engineering input, not a numerical knob, and there is deliberately no
    /// default.</b> A filter is what makes the answer mesh-independent and checkerboard-free,
    /// and its radius is simultaneously the MINIMUM MEMBER SIZE the result can contain — so it
    /// is a statement about the manufacturing route (the smallest wall a printer holds, the
    /// diameter of the cutter) and not something to turn until the picture looks right. A
    /// library cannot choose it; a default would be a manufacturing decision made by
    /// software.</para>
    ///
    /// <para>It must exceed an element, or the neighbourhood is the element itself and the
    /// filter does nothing — <see cref="TopologyResult.MeanNeighbours"/> is the measurement of
    /// how much neighbourhood it found, and a radius smaller than the mesh can express is
    /// refused by name.</para>
    /// </summary>
    public required double FilterRadius { get; init; }

    /// <summary>
    /// The SIMP penalisation exponent <c>p</c> in <c>E(rho) = rho^p·E0</c>, at least 1.
    ///
    /// <para>3 is the standard choice and the default. The exponent's whole job is to make
    /// intermediate densities uneconomic — at <c>p = 1</c> half-dense material is exactly as
    /// efficient per unit volume as solid material, so nothing pushes the answer toward
    /// solid-or-void, and the problem becomes the convex variable-thickness-sheet problem
    /// instead. That is worth knowing because it is also the one case with a closed-form
    /// answer to check against.</para>
    /// </summary>
    public double Penalty { get; init; } = 3.0;

    /// <summary>
    /// Ramp the penalty from 1 up to <see cref="Penalty"/> over the run rather than holding it
    /// at the target from the start — OFF by default, because it CHANGES the answer.
    ///
    /// <para>At <c>p = 1</c> the compliance problem is CONVEX (a half-dense element is exactly
    /// as efficient per unit volume as a solid one, so nothing pushes toward solid-or-void and
    /// the problem is the variable-thickness-sheet problem), so it has a unique optimum that
    /// does not depend on the starting design. Starting there and increasing the penalty
    /// gradually carries that independence into the non-convex regime, which is the standard way
    /// to reduce how much the answer depends on where the search began — a local minimum
    /// reached from a good, start-independent state rather than from an arbitrary one.</para>
    ///
    /// <para><b>The schedule holds at each penalty level before stepping up</b> (start at 1,
    /// add <see cref="TopologyOptimizer.PenaltyStep"/> every
    /// <see cref="TopologyOptimizer.PenaltyHoldIterations"/> iterations up to
    /// <see cref="Penalty"/>) — the dwell is what makes the convex phase actually settle, and a
    /// ramp with no dwell was measured to reduce start dependence not at all. The
    /// change-tolerance convergence stop is deferred until the target penalty is reached, so a
    /// run cannot stop early at a low penalty. It is off by default because it makes the answer
    /// — and every committed number — different, so it is a deliberate choice with a measured
    /// before/after (see the docs), not a silent improvement, and a run that turns it on wants
    /// <see cref="MaxIterations"/> large enough to reach the target and converge past it.</para>
    /// </summary>
    public bool PenaltyContinuation { get; init; }

    /// <summary>
    /// The lower bound on a density, in (0, 1) — a void element keeps <c>rho_min^p</c> of the
    /// solid stiffness so the system stays non-singular. At the default 1e-3 with
    /// <c>p = 3</c> that is 1e-9 of solid, which is small enough to be void and large enough
    /// that the factorization is still comfortable in double precision.
    /// </summary>
    public double MinimumDensity { get; init; } = 1e-3;

    /// <summary>Which filter to apply — <see cref="TopologyFilter.Density"/> by default; see
    /// that type for why, and why <see cref="TopologyFilter.None"/> exists.</summary>
    public TopologyFilter Filter { get; init; } = TopologyFilter.Density;

    /// <summary>
    /// The most iterations to run. The answer is the last iterate whether or not it converged,
    /// and <see cref="TopologyResult.Stop"/> says which.
    /// <para>150 rather than the classical 100, because a default that hits its own limit is
    /// the wrong default: measured on the docs cantilever, the default
    /// <see cref="TopologyFilter.Density"/> converges in 89 iterations and
    /// <see cref="TopologyFilter.Sensitivity"/> in 22, so 100 leaves no margin at all for the
    /// slower of the two.</para>
    /// </summary>
    public int MaxIterations { get; init; } = 150;

    /// <summary>Stop once no design variable moved further than this in one update.</summary>
    public double ChangeTolerance { get; init; } = 0.01;

    /// <summary>The largest change one update may make to a design variable, in (0, 1] —
    /// the optimality-criteria move limit, which is what keeps the heuristic update
    /// stable.</summary>
    public double MoveLimit { get; init; } = 0.2;

    /// <summary>The optimality-criteria damping exponent <c>eta</c> in (0, 1]; 0.5 is the
    /// classical value and the default.</summary>
    public double Damping { get; init; } = 0.5;

    /// <summary>The fill-reducing ordering for the per-iteration factorization. AMD by
    /// default, because a topology run pays for one factorization per iteration and the
    /// ordering is 4.6-13.4x of it — the argument that makes AMD opt-in elsewhere (a
    /// permutation is not bit-identical arithmetic, and committed numbers were measured
    /// natural) does not apply to numbers this run produces for the first time.</summary>
    public SparseOrdering Ordering { get; init; } = SparseOrdering.Amd;

    /// <summary>The starting design, or null for the uniform field at
    /// <see cref="VolumeFraction"/> — which is the classical start and is FEASIBLE by
    /// construction, so the first update has a volume to redistribute rather than one to
    /// reach.</summary>
    public IReadOnlyList<double>? InitialDensity { get; init; }

    /// <summary>
    /// Elements whose centroid this predicate accepts are PINNED SOLID — material that must
    /// stay, whatever the optimiser would prefer: under a bolt head, around a bearing bore, a
    /// mounting boss. Null (the default) leaves every element free.
    ///
    /// <para><b>It is a per-element bound, not a new algorithm.</b> A matching element keeps
    /// density 1 and is excluded from the optimality-criteria redistribution; the bisection
    /// then shares the remaining volume budget among the FREE elements, so the constraint still
    /// holds exactly and the passive material counts toward it. It is a <c>Func&lt;Vector3d,
    /// bool&gt;</c> over element centroids — a VOLUME selector, matching
    /// <c>TetMeshOptions.SizingField</c>'s shape — because <c>Facets</c> selects boundary
    /// facets and this wants the interior.</para>
    ///
    /// <para>A solid region whose own volume already exceeds the budget is refused by name, and
    /// an element accepted by both this and <see cref="VoidRegion"/> is a contradiction refused
    /// by name.</para>
    /// </summary>
    public Func<Vector3d, bool>? SolidRegion { get; init; }

    /// <summary>
    /// Elements whose centroid this predicate accepts are PINNED VOID — material that must go:
    /// a clearance channel, a keep-out for a cable, a window. They keep
    /// <see cref="MinimumDensity"/> (not exactly zero, so the stiffness stays non-singular) and
    /// are excluded from the redistribution. Null (the default) leaves every element free.
    /// </summary>
    public Func<Vector3d, bool>? VoidRegion { get; init; }
}

/// <summary>
/// One load case for the multi-load-case <see cref="TopologyOptimizer.Minimize(IReadOnlyList{TopologyLoadCase}, TopologyOptions, ProgressCancel?)"/>:
/// a model that states this case's LOADS (its mesh, supports and materials shared with every
/// other case) and the weight it carries in the objective.
/// </summary>
/// <param name="Model">The model for this case — only its loads may differ from the others'.</param>
/// <param name="Weight">This case's weight in the weighted-sum compliance; positive.</param>
public readonly record struct TopologyLoadCase(StructuralModel Model, double Weight);

/// <summary>
/// Compliance minimisation by SIMP (Solid Isotropic Material with Penalisation) over a
/// tetrahedral mesh: <b>where should the material go</b>, answered as a density per element.
///
/// <para><b>This is the LOOP, not a new solver.</b> Everything under it already existed — the
/// stiffness assembly, the factorization, the element strain energy. What the feature adds is
/// the iteration and the decisions inside it, and every one of those is about what the loop
/// may assume.</para>
///
/// <para><b>Gradients are MANDATORY here, which is the exact opposite of
/// <c>DesignStudy</c>.</b> A design study drives a handful of <c>[Param]</c> values
/// DERIVATIVE-FREE precisely because a parameter change can alter TOPOLOGY — a hole breaks
/// through, a fillet stops fitting — so the two sides of such a step are different solids and
/// a finite-difference gradient across it is meaningless rather than merely noisy. Here the
/// topology changing IS the answer: there is one variable per element, tens of thousands of
/// them, and no derivative-free method reaches that dimension. The two are complements. Use a
/// design study to size a part you have drawn; use this to find out what to draw.</para>
///
/// <para><b>The sensitivity is free, because it is the element strain energy.</b> With
/// <c>E(rho) = rho^p E0</c> and a design-INDEPENDENT load, the compliance <c>c = u'Ku = f'u</c>
/// is self-adjoint and
/// <c>dc/drho_e = -p·rho_e^(p-1)·u_e' k0 u_e</c> — the quantity a solve has already produced,
/// needing no adjoint system and no second factorization. That self-adjointness is exactly
/// what the refusals below protect: it is a property of the load, not of the formulation.</para>
///
/// <para><b>Optimality criteria (OC), and the API says so rather than implying generality.</b>
/// OC is the standard update for a SINGLE volume constraint and is a few lines: a Lagrange
/// multiplier bisected until the volume is met, and a fixed-point step in each variable. It
/// does not generalise — a second constraint, or a different objective, wants MMA (the Method
/// of Moving Asymptotes), which is a much larger dependency-free build and is filed rather
/// than pretended at.</para>
///
/// <para><b>Refused by name</b>, because each would make the sensitivity above simply wrong
/// rather than approximate:
/// <list type="bullet">
/// <item><b>Design-dependent loads</b> — <c>Gravity</c> and <c>BodyForce</c>. Self-weight is
/// the case that matters: this model integrates it over the FULL-density body once, so a run
/// would optimise a structure against the weight of a body it is not, and compliance stops
/// being self-adjoint anyway. Refused on <c>StructuralModel.HasVolumeLoad</c>.</item>
/// <item><b>A prescribed non-zero displacement</b> — the load then moves with the stiffness,
/// so minimising <c>f'u</c> maximises stiffness in one case and minimises it in the other. The
/// sign of the whole problem depends on which, so it is a different formulation rather than a
/// harder one.</item>
/// <item><b>A thermal load</b> — <c>alpha·dT</c> enters through <c>D</c>, so the load scales
/// with <c>rho^p</c> and is design-dependent by construction.</item>
/// <item><b>Local stress constraints</b> — they need aggregation (p-norm or Kreisselmeier-
/// Steinhauser), and the aggregation parameter CHANGES THE ANSWER, so it is a separate feature
/// with its own verification rather than a flag on this one. Nothing here publishes a stress:
/// the stiffness carries a penalised modulus, so an element's stress would be the SIMP stress
/// rather than a physical one.</item>
/// </list></para>
/// </summary>
public static class TopologyOptimizer
{
    private const byte PassiveFree = 0;
    private const byte PassiveSolid = 1;
    private const byte PassiveVoid = 2;

    /// <summary>
    /// The number of iterations a penalty continuation HOLDS at each level before stepping up.
    /// The convex <c>p = 1</c> problem (and each intermediate level) is given this long to
    /// settle toward its own optimum before the penalisation is raised, which is what makes the
    /// continuation actually establish the start-independent convex state rather than merely
    /// increasing <c>p</c> slowly — a ramp with no dwell does not reduce start dependence at
    /// all (measured). A stated schedule rather than a second knob.
    /// </summary>
    public const int PenaltyHoldIterations = 20;

    /// <summary>The increment a penalty continuation adds at each step, from 1 up to
    /// <see cref="TopologyOptions.Penalty"/>.</summary>
    public const double PenaltyStep = 0.5;

    /// <summary>
    /// The penalty in force at <paramref name="iteration"/> (1-based). Constant at
    /// <see cref="TopologyOptions.Penalty"/> unless
    /// <see cref="TopologyOptions.PenaltyContinuation"/> is on, in which case it starts at 1 and
    /// steps up by <see cref="PenaltyStep"/> every <see cref="PenaltyHoldIterations"/> iterations
    /// until it reaches the target. Internal so the off-path bit-identity and the schedule
    /// itself can be asserted directly.
    /// </summary>
    internal static double EffectivePenalty(TopologyOptions options, int iteration)
    {
        if (!options.PenaltyContinuation)
            return options.Penalty;
        double p = 1.0 + PenaltyStep * ((iteration - 1) / PenaltyHoldIterations);
        return Math.Min(options.Penalty, p);
    }

    /// <summary>Whether the change-tolerance convergence stop is allowed at
    /// <paramref name="iteration"/>: always, unless a continuation is still stepping up to the
    /// target penalty (stopping there would freeze a low-penalty, blurred design).</summary>
    internal static bool RampComplete(TopologyOptions options, int iteration) =>
        EffectivePenalty(options, iteration) >= options.Penalty;

    /// <summary>
    /// Minimises <c>c = u'Ku</c> subject to a volume fraction, over one density per element.
    /// </summary>
    /// <param name="model">The model — mesh, materials, supports and design-independent
    /// loads. It is not modified.</param>
    /// <param name="options">What to optimise for.</param>
    /// <param name="progress">Optional cooperative cancellation and progress, honoured per
    /// iteration and inside each factorization.</param>
    public static TopologyResult Minimize(
        StructuralModel model, TopologyOptions options, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        // The single-model form IS the one-case form, exactly as StructuralSolver.Solve is
        // SolveAll([model])[0] — one implementation, and the single-case answer is bit-identical
        // (a weight of exactly 1.0 folds into the arithmetic as itself).
        return Minimize([new TopologyLoadCase(model, 1.0)], options, progress);
    }

    /// <summary>
    /// Minimises the WEIGHTED-sum compliance <c>sum_i w_i·u_i'Ku_i</c> over several load cases,
    /// subject to a single volume fraction — the multi-load-case form, where the weighting IS
    /// the design decision.
    ///
    /// <para><b>One factorization serves every case, because the stiffness is the same.</b> All
    /// cases share the mesh, the supports and the materials (only the loads differ), so
    /// <c>K(rho)</c> is one matrix and each case is a back-substitution against it — exactly
    /// <c>StructuralSolver.SolveAll</c>'s contract. The compliance is <c>sum_i w_i·c_i</c> and
    /// the sensitivity is the weighted sum of the per-case strain energies, so the loop is
    /// unchanged and the only cost is N solves per iteration.</para>
    ///
    /// <para><b>The weighting is not something a library can pick.</b> Minimising a weighted sum
    /// is a design decision: a structure stiff against A twice as much as against B is not the
    /// same part as the min-max worst-case one, which is a genuinely different problem (filed).
    /// So the weights are the caller's, and they must be positive — a zero-weight case has no
    /// effect and a negative one rewards compliance, inverting the sign of the problem.</para>
    /// </summary>
    /// <param name="cases">The load cases and their weights. Every case's model must share one
    /// <see cref="AnalysisMesh"/> instance, one restraint pattern and one material assignment
    /// with the first — only the loads may differ.</param>
    /// <param name="options">What to optimise for.</param>
    /// <param name="progress">Optional cooperative cancellation and progress.</param>
    public static TopologyResult Minimize(
        IReadOnlyList<TopologyLoadCase> cases, TopologyOptions options,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(options);
        if (cases.Count == 0)
            throw new ArgumentException("At least one load case is needed.", nameof(cases));
        foreach (var lc in cases)
        {
            ArgumentNullException.ThrowIfNull(lc.Model);
            if (!(lc.Weight > 0) || double.IsInfinity(lc.Weight))
                throw new FeaException(
                    $"A load-case weight must be finite and positive; {lc.Weight} was given. A "
                    + "zero-weight case has no effect and a negative one rewards compliance, "
                    + "inverting the problem for that case.");
        }

        var model = cases[0].Model;
        var mesh = model.Mesh;
        int count = mesh.ElementCount;
        foreach (var lc in cases)
            RequireUsableModel(lc.Model, options);
        RequireOneOperator(cases);

        // ONE rule for the run: the assembly and the element energies integrate at the same
        // points, which is what makes `sum rho^p·E_e` and `f'u` the SAME number rather than
        // two nearly equal ones — and that identity is the run's own self-check.
        var rule = TetQuadrature.For(mesh.Order);
        FeaGuards.RequireUsableElements(mesh, rule, "stiffness");

        var volumes = new double[count];
        for (int e = 0; e < count; e++)
            volumes[e] = mesh.ElementVolume(e);
        double totalVolume = 0;
        foreach (double v in volumes)
            totalVolume += v;

        // Passive (non-design) regions, pinned SOLID or VOID by a centroid predicate and left
        // out of the redistribution. Null on both leaves `passive` all-free, and every branch
        // below then reduces to the incumbent code path bit for bit.
        var passive = ComputePassive(
            mesh, options, volumes, options.VolumeFraction * totalVolume, out _);

        var filter = options.Filter == TopologyFilter.None
            ? null
            : DensityFilter.Build(mesh, options.FilterRadius, volumes, progress);
        if (filter is not null && filter.MaxNeighbours <= 1)
            throw new FeaException(
                $"The filter radius {options.FilterRadius:G6} reaches no element beyond each "
                + "element's own centroid, so the filter does nothing and the answer would be "
                + "mesh-dependent. A filter radius is a minimum MEMBER size, so it has to be "
                + "several elements wide: refine the mesh, or state a radius the mesh can "
                + "express.");

        var design = new double[count];
        if (options.InitialDensity is { } seed)
        {
            if (seed.Count != count)
                throw new ArgumentException(
                    $"InitialDensity has {seed.Count} values for {count} elements.",
                    nameof(options));
            for (int e = 0; e < count; e++)
                design[e] = Math.Clamp(seed[e], options.MinimumDensity, 1.0);
        }
        else
        {
            Array.Fill(design, options.VolumeFraction);
        }
        // Pin passive elements in the initial design. Solid -> 1, void -> the floor. Free
        // elements keep their seed / uniform value.
        for (int e = 0; e < count; e++)
        {
            if (passive[e] == PassiveSolid)
                design[e] = 1.0;
            else if (passive[e] == PassiveVoid)
                design[e] = options.MinimumDensity;
        }

        var updated = new double[count];
        var evaluator = new Evaluator(cases, options, filter, rule, progress);
        var volumeGradient = VolumeGradient(filter, options.Filter, volumes);

        var history = new List<TopologyIteration>();
        var stop = TopologyStop.IterationLimit;
        double targetVolume = options.VolumeFraction * totalVolume;

        for (int iteration = 1; iteration <= options.MaxIterations; iteration++)
        {
            progress?.ThrowIfCancelled();
            progress?.Report((iteration - 1) / (double)options.MaxIterations);

            double compliance = evaluator.Evaluate(design, iteration);

            bool met = OptimalityCriteriaStep(
                design, evaluator.DesignSensitivity, volumeGradient, targetVolume, options,
                passive, updated);

            double change = 0;
            for (int e = 0; e < count; e++)
            {
                change = Math.Max(change, Math.Abs(updated[e] - design[e]));
                design[e] = updated[e];
            }

            double fraction = 0;
            for (int e = 0; e < count; e++)
                fraction += volumeGradient[e] * design[e];
            fraction /= totalVolume;

            history.Add(new TopologyIteration(iteration, compliance, fraction, change, met));

            // Under a penalty continuation the change-tolerance stop is deferred until the ramp
            // has reached the target penalty; without continuation RampComplete is always true,
            // so this is the incumbent condition bit for bit.
            if (change < options.ChangeTolerance && RampComplete(options, iteration))
            {
                stop = TopologyStop.Converged;
                break;
            }
        }

        // The reported PHYSICAL field is the one belonging to the design being returned, so it
        // is recomputed after the final update rather than left as the field the last solve
        // used — otherwise a caller thresholding `Density` would be thresholding the
        // second-to-last iterate.
        var physical = new double[count];
        if (filter is not null && options.Filter == TopologyFilter.Density)
            filter.Apply(design, physical);
        else
            Array.Copy(design, physical, count);

        progress?.Report(1);
        return new TopologyResult(
            model, options, design, physical, volumes, history, stop,
            (int)Math.Round(filter?.MeanNeighbours ?? 1), filter?.MaxNeighbours ?? 1);
    }

    /// <summary>
    /// <c>dV/dx</c>, where <c>V</c> is the volume of MATERIAL the design contains.
    ///
    /// <para><b>The constraint is on the PHYSICAL volume, and getting that wrong is a defect
    /// rather than a convention.</b> A row-normalised filter is not volume-preserving — near a
    /// boundary an element's neighbourhood is truncated, so the column sums of <c>W</c> are
    /// not the element volumes — and constraining the DESIGN variables instead therefore lets
    /// the structure hold more material than was asked for. Measured on the uniform-bar
    /// fixture at <c>p = 1</c>, whose convex optimum is exactly <c>c0/f</c>: constraining the
    /// design variables returned <b>1.79·c0</b> against the closed form's <b>2.00·c0</b> — a
    /// compliance BELOW the true optimum, which is only possible by spending volume the
    /// constraint said was not there.</para>
    ///
    /// <para>It costs nothing, because the filter is LINEAR: <c>V(x) = v'(Wx) = (W'v)·x</c> is
    /// exactly linear in the design variables, so one transpose at the start gives both the
    /// gradient and an exact evaluator, and the bisection never applies the filter at all.
    /// Without a density filter <c>W</c> is the identity and this IS the element volume
    /// array.</para>
    /// </summary>
    private static double[] VolumeGradient(
        DensityFilter? filter, TopologyFilter kind, double[] volumes)
    {
        if (filter is null || kind != TopologyFilter.Density)
            return volumes;
        var gradient = new double[volumes.Length];
        filter.ApplyTranspose(volumes, gradient);
        return gradient;
    }

    /// <summary>
    /// The optimality-criteria update: <c>x_new = clamp(x·B^eta)</c> with
    /// <c>B_e = -(dc/dx_e) / (lambda·dV/dx_e)</c>, and <c>lambda</c> bisected until the volume
    /// constraint is met.
    ///
    /// <para><b>The bisection is GEOMETRIC, not arithmetic.</b> The multiplier's plausible
    /// range spans dozens of decades — it carries the units of an energy over a volume, which
    /// depends on the load, the modulus and the mesh size all at once — so halving the
    /// interval converges on its LARGEST end and would need hundreds of steps to resolve a
    /// small root. Bisecting the ratio converges geometrically wherever the root is, and the
    /// stopping rule is then relative, which is what makes the volume land at round-off
    /// rather than at a tolerance.</para>
    ///
    /// <para>Returns whether the target was reachable at all: with every element clamped
    /// against its move limit the achievable volume is a bounded interval, and if the target
    /// is outside it the update goes as far as it can and SAYS so. Reporting the miss beats
    /// meeting the constraint by violating the move limit, which is what keeps the update
    /// stable.</para>
    /// </summary>
    private static bool OptimalityCriteriaStep(
        double[] design,
        double[] sensitivity,
        double[] volumeGradient,
        double targetVolume,
        TopologyOptions options,
        byte[] passive,
        double[] updated)
    {
        int count = design.Length;
        double hi = 0;
        for (int e = 0; e < count; e++)
        {
            // A pinned element never moves, so it neither needs nor should influence the
            // multiplier bracket — its sensitivity is a load-carrying number the design does
            // not act on.
            if (passive[e] != PassiveFree)
                continue;
            hi = Math.Max(hi, -sensitivity[e] / volumeGradient[e]);
        }
        // Every B_e is at most 1/2 here, so every element moves DOWN and the volume is the
        // least the move limits allow; the same argument the other way makes the low end the
        // most they allow. A sensitivity that is identically zero (nothing carries load)
        // leaves no scale to bracket with, so any positive one serves.
        if (!(hi > 0))
            hi = 1;
        hi *= 2;
        double lo = hi * 1e-40;

        double VolumeAt(double lambda)
        {
            double sum = 0;
            for (int e = 0; e < count; e++)
                sum += volumeGradient[e] * Candidate(e, lambda);
            return sum;
        }

        double Candidate(int e, double lambda)
        {
            // Pinned elements return their fixed density at every multiplier, so they add a
            // constant to VolumeAt and never move under the update.
            if (passive[e] == PassiveSolid)
                return 1.0;
            if (passive[e] == PassiveVoid)
                return options.MinimumDensity;
            double lower = Math.Max(options.MinimumDensity, design[e] - options.MoveLimit);
            double upper = Math.Min(1.0, design[e] + options.MoveLimit);
            double b = -sensitivity[e] / (lambda * volumeGradient[e]);
            double candidate = b > 0 ? design[e] * Math.Pow(b, options.Damping) : lower;
            return Math.Clamp(candidate, lower, upper);
        }

        double most = VolumeAt(lo);
        double least = VolumeAt(hi);
        bool met = targetVolume <= most && targetVolume >= least;
        if (!met)
        {
            double lambda = targetVolume > most ? lo : hi;
            for (int e = 0; e < count; e++)
                updated[e] = Candidate(e, lambda);
            return false;
        }

        // 200 geometric halvings take the ratio hi/lo from 1e40 to 1 + 1e-90, so the loop
        // always exits on the relative test; the count is a backstop, not the criterion.
        for (int step = 0; step < 200 && hi - lo > 1e-15 * (hi + lo); step++)
        {
            double mid = Math.Sqrt(lo * hi);
            if (VolumeAt(mid) > targetVolume)
                lo = mid;
            else
                hi = mid;
        }

        double settled = Math.Sqrt(lo * hi);
        for (int e = 0; e < count; e++)
            updated[e] = Candidate(e, settled);
        return true;
    }

    /// <summary>
    /// One design evaluation — filter, assemble, factorize, solve, and the sensitivity — and
    /// the ONE place the loop's arithmetic lives.
    ///
    /// <para>It is a class rather than a step inside the loop so a test can drive exactly what
    /// the optimiser drives: the finite-difference check of the sensitivity re-evaluates
    /// through THIS object, so it compares the production gradient against the production
    /// compliance rather than against a second implementation that could share a mistake with
    /// it. Everything else it buys is incidental (buffers allocated once, the load vector
    /// assembled once).</para>
    /// </summary>
    internal sealed class Evaluator
    {
        private readonly StructuralModel _model;
        private readonly TopologyOptions _options;
        private readonly DensityFilter? _filter;
        private readonly TetQuadrature _rule;
        private readonly ProgressCancel? _progress;
        private readonly int[] _reduced;
        private readonly int _freeCount;
        private readonly double[][] _loads;
        private readonly double[] _weights;
        private readonly double[] _free;
        private readonly Vector3d[] _displacement;
        private readonly double[] _scale;
        private readonly double[] _energy;
        private readonly double[] _sensitivity;
        // The reduced stiffness has an IDENTICAL sparsity pattern every iteration (the mesh
        // connectivity and the eliminated DOFs are fixed; only the per-element scales change),
        // so the ordering, elimination tree and column counts are analysed ONCE and every
        // iteration reuses them — the numeric pass is where the time is. Built on the first
        // factorization so it needs no separate assembly of the pattern.
        private SparseCholeskySymbolic? _symbolic;

        internal Evaluator(
            IReadOnlyList<TopologyLoadCase> cases, TopologyOptions options, DensityFilter? filter,
            in TetQuadrature rule, ProgressCancel? progress)
        {
            _model = cases[0].Model;
            _options = options;
            _filter = filter;
            _rule = rule;
            _progress = progress;
            int count = _model.Mesh.ElementCount;
            _reduced = FeaAssembly.ReducedIndices(_model, out _freeCount);
            if (_freeCount == 0)
                throw new FeaException(
                    "Every degree of freedom is restrained; there is nothing to solve for.");
            _loads = new double[cases.Count][];
            _weights = new double[cases.Count];
            for (int c = 0; c < cases.Count; c++)
            {
                _loads[c] = ReducedLoad(cases[c].Model, _reduced, _freeCount);
                _weights[c] = cases[c].Weight;
            }
            _free = new double[_freeCount];
            _displacement = new Vector3d[_model.Mesh.NodeCount];
            _scale = new double[count];
            _energy = new double[count];
            _sensitivity = new double[count];
            PhysicalDensity = new double[count];
            DesignSensitivity = new double[count];
        }

        /// <summary>The physical density the last <see cref="Evaluate"/> built the stiffness
        /// from.</summary>
        internal double[] PhysicalDensity { get; }

        /// <summary><c>dc/dx</c> from the last <see cref="Evaluate"/> — the gradient with
        /// respect to the DESIGN variable, filter included.</summary>
        internal double[] DesignSensitivity { get; }

        /// <summary>The compliance <c>c = f'u</c> of one design, leaving
        /// <see cref="DesignSensitivity"/> and <see cref="PhysicalDensity"/> filled.</summary>
        internal double Evaluate(IReadOnlyList<double> design, int iteration = 1)
        {
            var mesh = _model.Mesh;
            int count = mesh.ElementCount;

            if (_filter is not null && _options.Filter == TopologyFilter.Density)
                _filter.Apply(design, PhysicalDensity);
            else
                for (int e = 0; e < count; e++)
                    PhysicalDensity[e] = design[e];

            // The penalty in force this iteration — the target unless a continuation ramp is on
            // (EffectivePenalty is the target constant otherwise, so this is unchanged).
            double penalty = EffectivePenalty(_options, iteration);
            for (int e = 0; e < count; e++)
                _scale[e] = Math.Pow(PhysicalDensity[e], penalty);

            var full = FeaAssembly.Stiffness(_model, _rule, _scale);
            var reducedMatrix = FeaAssembly.Reduce(full, _reduced, _freeCount);
            SparseCholesky factor;
            try
            {
                // Analyse the pattern once (its symbolic factorization is shared across every
                // iteration), then factorize this iteration's values into it. `symbolic.Factorize`
                // is bit-identical to `SparseCholesky.Factorize` of the same matrix, so this is a
                // pure speedup that moves no number.
                _symbolic ??= SparseCholesky.AnalyzePattern(reducedMatrix, _options.Ordering);
                factor = _symbolic.Factorize(reducedMatrix, _progress);
            }
            catch (InvalidOperationException ex)
            {
                // WHICH cause it is, is decided by the design rather than guessed at: while
                // the softest element is still well above the void floor, nothing the
                // optimiser has done can have disconnected anything, so the mesh is the
                // suspect — and at iteration 1 the design is uniform, which makes this a solve
                // an ordinary static run would have failed too. Naming one cause when the
                // other is at fault is how a message sends a reader to the wrong knob.
                double softest = 1;
                foreach (double rho in PhysicalDensity)
                    softest = Math.Min(softest, rho);
                bool emptied = softest < 10 * _options.MinimumDensity;
                throw new FeaException(
                    $"The stiffness could not be factorized at iteration {iteration} "
                    + $"(softest element density {softest:G4}). "
                    + (emptied
                        ? "A SIMP void element keeps rho_min^p = "
                          + $"{Math.Pow(_options.MinimumDensity, _options.Penalty):G3} of the "
                          + "solid stiffness, so a region that has emptied out is soft rather "
                          + "than absent — but a part of the mesh joined to the supports only "
                          + "through such a region can still be effectively free. Raise "
                          + $"{nameof(TopologyOptions.MinimumDensity)}, or check that the "
                          + "supports hold every body in the mesh."
                        : "No element is near the void floor, so this is not the optimiser "
                          + "emptying a region out: the same system would have failed as an "
                          + "ordinary static solve. The usual cause is mesh quality — a sliver "
                          + "whose Jacobian is positive but tiny leaves the assembled matrix "
                          + "not numerically positive definite. Check TetQuality.Analyze and "
                          + "run TetSmoothing.Smooth, or mesh at a coarser target size."),
                    ex);
            }
            // ONE factorization, N back-substitutions — the whole point of the multi-case
            // form, exactly as StructuralSolver.SolveAll. The compliance and the per-element
            // strain energy are WEIGHTED sums over the cases, so `_energy[e]` accumulates
            // `sum_c w_c·(u_c,e' k0 u_c,e)` and the sensitivity below reads it directly.
            double compliance = 0;
            Array.Clear(_energy, 0, count);
            for (int c = 0; c < _loads.Length; c++)
            {
                var load = _loads[c];
                double weight = _weights[c];
                factor.Solve(load, _free);

                for (int node = 0; node < mesh.NodeCount; node++)
                {
                    int x = _reduced[3 * node], y = _reduced[3 * node + 1], z = _reduced[3 * node + 2];
                    _displacement[node] = new Vector3d(
                        x >= 0 ? _free[x] : 0, y >= 0 ? _free[y] : 0, z >= 0 ? _free[z] : 0);
                }

                // c_i = f_i'u_i, the DEFINITION. The identity `sum w_i·c_i = sum rho^p·E_e` (with
                // E_e the weighted energy) is asserted in the tests, two constructions checking
                // each other rather than one checking its own arithmetic.
                double ci = 0;
                for (int i = 0; i < _freeCount; i++)
                    ci += load[i] * _free[i];
                compliance += weight * ci;

                AccumulateElementEnergies(weight);
            }

            // dc/drho~ = -p·rho~^(p-1)·(weighted energy). Non-positive everywhere, since every
            // per-case energy is non-negative and every weight positive — which is what makes
            // the OC step's B_e non-negative and needs no sign guard.
            for (int e = 0; e < count; e++)
            {
                _sensitivity[e] = -penalty
                    * Math.Pow(PhysicalDensity[e], penalty - 1) * _energy[e];
            }

            switch (_options.Filter)
            {
                case TopologyFilter.Density:
                    _filter!.ApplyTranspose(_sensitivity, DesignSensitivity);
                    break;
                case TopologyFilter.Sensitivity:
                    _filter!.ApplySensitivity(
                        design, _sensitivity, DesignSensitivity, _options.MinimumDensity);
                    break;
                default:
                    Array.Copy(_sensitivity, DesignSensitivity, count);
                    break;
            }
            return compliance;
        }

        /// <summary><c>sum_e rho_e^p·E_e</c> — the compliance by its OTHER route, from the
        /// element energies the sensitivity is built on. Equal to
        /// <see cref="Evaluate"/>'s <c>f'u</c> to round-off, which is what makes asserting it
        /// two constructions checking each other.</summary>
        internal double ComplianceFromEnergies()
        {
            double sum = 0;
            for (int e = 0; e < _energy.Length; e++)
                sum += _scale[e] * _energy[e];
            return sum;
        }

        /// <summary>
        /// ADDS <c>weight·(u_e' k0 u_e)</c> to <see cref="_energy"/> for every element, at the
        /// SAME quadrature rule the stiffness is assembled with — which is what makes it that
        /// quadratic form exactly rather than a nearby integral. <c>k0</c> is the UNPENALISED
        /// element stiffness, so the SIMP factor appears once, in the sensitivity, and never
        /// twice. Called once per load case, so <c>_energy</c> ends the loop holding the
        /// weighted sum; the caller clears it first.
        ///
        /// <para>Computed as <c>sum_q w·detJ·(eps' sigma)</c> rather than by forming the
        /// element stiffness and taking a quadratic form: the two are the same number (the
        /// quadratic form of <c>B'DB</c> IS <c>(Bu)'D(Bu)</c>), and this way costs six strain
        /// components and a 6x6 product per point instead of a full <c>12x12</c> or
        /// <c>30x30</c> matrix. The Voigt strain carries ENGINEERING shear, which is exactly
        /// the convention that makes <c>eps'·sigma</c> the correct double contraction with no
        /// factor of two to remember.</para>
        /// </summary>
        private void AccumulateElementEnergies(double weight)
        {
            var mesh = _model.Mesh;
            int perElement = mesh.NodesPerElement;
            Span<Vector3d> positions = stackalloc Vector3d[10];
            Span<double> ue = stackalloc double[30];
            Span<double> strain = stackalloc double[6];
            Span<double> stress = stackalloc double[6];
            Span<Vector3d> gradients = stackalloc Vector3d[10];

            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if ((e & 1023) == 0)
                    _progress?.ThrowIfCancelled();
                var nodes = mesh.Element(e);
                for (int i = 0; i < perElement; i++)
                {
                    positions[i] = mesh.Position(nodes[i]);
                    var u = _displacement[nodes[i]];
                    ue[3 * i] = u.X;
                    ue[3 * i + 1] = u.Y;
                    ue[3 * i + 2] = u.Z;
                }
                var law = _model.ElasticityOf(e);
                double sum = 0;
                for (int q = 0; q < _rule.Count; q++)
                {
                    var (r, s, t) = _rule.Point(q);
                    if (!TetElement.ShapeGradients(
                            mesh.Order, positions[..perElement], r, s, t, gradients,
                            out double detJ))
                        continue;
                    TetElement.StrainAt(
                        mesh.Order, positions[..perElement], ue[..(3 * perElement)],
                        r, s, t, strain);
                    law.Stress(strain, stress);
                    double density = 0;
                    for (int i = 0; i < 6; i++)
                        density += strain[i] * stress[i];
                    sum += _rule.Weight(q) * detJ * density;
                }
                // An energy is non-negative by construction; a negative one would be a defect
                // rather than a small number, so it is clamped at exactly zero rather than
                // being allowed to make the OC step's B_e negative and the answer silently
                // strange. Accumulated (weighted) across load cases, having been cleared once.
                _energy[e] += weight * Math.Max(0, sum);
            }
        }
    }

    /// <summary>The pieces a test needs to drive one evaluation through the production
    /// path — the same construction <see cref="Minimize"/> makes.</summary>
    internal static (Evaluator Evaluator, double[] Volumes) BuildEvaluator(
        StructuralModel model, TopologyOptions options)
    {
        RequireUsableModel(model, options);
        var rule = TetQuadrature.For(model.Mesh.Order);
        FeaGuards.RequireUsableElements(model.Mesh, rule, "stiffness");
        var volumes = new double[model.Mesh.ElementCount];
        for (int e = 0; e < volumes.Length; e++)
            volumes[e] = model.Mesh.ElementVolume(e);
        var filter = options.Filter == TopologyFilter.None
            ? null
            : DensityFilter.Build(model.Mesh, options.FilterRadius, volumes);
        return (new Evaluator([new TopologyLoadCase(model, 1.0)], options, filter, rule, null), volumes);
    }

    /// <summary>
    /// Every case must share one operator with the first: the same <see cref="AnalysisMesh"/>
    /// instance (a value comparison that said two node numberings matched would scramble the
    /// answer by permutation), the same restraint MASK (supports are eliminated, so a different
    /// mask is a different matrix), and the same material per element. Only the loads may differ
    /// — the mirror of <c>StructuralSolver.SolveAll</c>'s own contract, for the same reason.
    /// </summary>
    private static void RequireOneOperator(IReadOnlyList<TopologyLoadCase> cases)
    {
        var first = cases[0].Model;
        var mesh = first.Mesh;
        for (int c = 1; c < cases.Count; c++)
        {
            var other = cases[c].Model;
            if (!ReferenceEquals(other.Mesh, mesh))
                throw new FeaException(
                    $"Load case {c} was built on a different AnalysisMesh instance from case 0. "
                    + "Load cases share one factorization, which is a statement about one node "
                    + "numbering; build every case over the same mesh object.");
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                if (other.RestraintOf(node) != first.RestraintOf(node))
                    throw new FeaException(
                        $"Load case {c} restrains node {node} differently from case 0 "
                        + $"({other.RestraintOf(node)} against {first.RestraintOf(node)}). "
                        + "Supports are ELIMINATED, so a different restraint pattern is a "
                        + "different matrix and cannot share a factorization.");
            }
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if (!Equals(other.MaterialOf(e), first.MaterialOf(e)))
                    throw new FeaException(
                        $"Load case {c} gives element {e} a different material from case 0 "
                        + $"({other.MaterialOf(e).Name} against {first.MaterialOf(e).Name}). The "
                        + "stiffness is a function of the materials, so the cases do not share "
                        + "one.");
            }
        }
    }

    /// <summary>
    /// Classifies every element as free, pinned solid or pinned void from its centroid, and
    /// refuses the two contradictions: an element accepted by both selectors, and a solid
    /// region whose own volume already exceeds the budget.
    /// </summary>
    private static byte[] ComputePassive(
        AnalysisMesh mesh, TopologyOptions options, double[] volumes, double targetVolume,
        out double passiveSolidVolume)
    {
        int count = mesh.ElementCount;
        var passive = new byte[count];
        passiveSolidVolume = 0;
        var solid = options.SolidRegion;
        var @void = options.VoidRegion;
        if (solid is null && @void is null)
            return passive;

        int solidCount = 0;
        for (int e = 0; e < count; e++)
        {
            var nodes = mesh.Element(e);
            // The centroid over the four CORNER nodes — the same measure the fixtures and the
            // release binning use, so a caller's selector sees the element where they expect it.
            var c = 0.25 * (mesh.Position(nodes[0]) + mesh.Position(nodes[1])
                + mesh.Position(nodes[2]) + mesh.Position(nodes[3]));
            bool isSolid = solid is not null && solid(c);
            bool isVoid = @void is not null && @void(c);
            if (isSolid && isVoid)
                throw new FeaException(
                    $"Element {e} (centroid {c}) is accepted by BOTH SolidRegion and VoidRegion, "
                    + "so it is told to stay solid and to go at once. The two selectors must not "
                    + "overlap.");
            if (isSolid)
            {
                passive[e] = PassiveSolid;
                passiveSolidVolume += volumes[e];
                solidCount++;
            }
            else if (isVoid)
            {
                passive[e] = PassiveVoid;
            }
        }

        if (passiveSolidVolume > targetVolume)
            throw new FeaException(
                $"The SolidRegion holds {passiveSolidVolume:G6} of volume ({solidCount:N0} "
                + $"elements), which already exceeds the budget {targetVolume:G6} "
                + $"(VolumeFraction {options.VolumeFraction:G6} of the domain). The pinned-solid "
                + "material alone uses more than the constraint allows, so no design can be "
                + "feasible — raise VolumeFraction or shrink the solid region.");
        return passive;
    }

    /// <summary>
    /// The reduced load vector. With no prescribed displacement (refused above) every load has
    /// already been reduced to nodal forces on the model, so this is a gather rather than an
    /// assembly — and it is assembled ONCE for the whole run, which is the practical
    /// consequence of the design-independent-load rule.
    /// </summary>
    private static double[] ReducedLoad(StructuralModel model, int[] reduced, int freeCount)
    {
        var load = new double[freeCount];
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            var force = model.ForceOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                int r = reduced[3 * node + axis];
                if (r >= 0)
                    load[r] = force[axis];
            }
        }
        double magnitude = 0;
        foreach (double v in load)
            magnitude += Math.Abs(v);
        if (!(magnitude > 0))
            throw new FeaException(
                "No load reaches a free degree of freedom, so the compliance is zero for every "
                + "design and there is nothing to minimise. Check that the loaded facets are "
                + "not also fixed.");
        return load;
    }

    /// <summary>Every refusal, fired before a single matrix is assembled and naming what to
    /// change — the design-dependent loads that would make the sensitivity wrong, and the
    /// settings that have no legal value.</summary>
    private static void RequireUsableModel(StructuralModel model, TopologyOptions options)
    {
        if (!(options.VolumeFraction > 0) || !(options.VolumeFraction < 1))
            throw new ArgumentOutOfRangeException(
                nameof(options), options.VolumeFraction,
                "VolumeFraction lies strictly between 0 and 1.");
        if (!(options.FilterRadius > 0))
            throw new ArgumentOutOfRangeException(
                nameof(options), options.FilterRadius,
                "FilterRadius is a length in model units and must be positive. It sets the "
                + "minimum member size, so it is an engineering input rather than a numerical "
                + "knob and has no default.");
        if (!(options.Penalty >= 1))
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Penalty,
                "Penalty is at least 1; below it, dense material would be LESS efficient per "
                + "unit volume than sparse material and the optimiser would spread rather than "
                + "concentrate.");
        if (!(options.MinimumDensity > 0) || !(options.MinimumDensity < 1))
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MinimumDensity,
                "MinimumDensity lies strictly between 0 and 1; exactly 0 makes the stiffness "
                + "singular wherever the design empties out.");
        if (options.MinimumDensity >= options.VolumeFraction)
            throw new FeaException(
                $"MinimumDensity {options.MinimumDensity:G6} is not below the volume fraction "
                + $"{options.VolumeFraction:G6}, so even an entirely void design uses more "
                + "volume than the constraint allows and it can never be met.");
        if (!(options.MoveLimit > 0) || !(options.MoveLimit <= 1))
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MoveLimit, "MoveLimit lies in (0, 1].");
        if (!(options.Damping > 0) || !(options.Damping <= 1))
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Damping, "Damping lies in (0, 1].");
        if (options.MaxIterations < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxIterations, "At least one iteration is needed.");

        if (model.HasVolumeLoad)
            throw new FeaException(
                "A gravity or body load makes this a DESIGN-DEPENDENT load problem, which this "
                + "formulation cannot carry. Self-weight is integrated over the full-density "
                + "body once, so an optimisation would be minimising the compliance of one "
                + "structure under the weight of a different one; and with a load that moves "
                + "with the design, compliance stops being self-adjoint, so the strain-energy "
                + "sensitivity this uses is not merely inaccurate but wrong. Remove the body "
                + "load, or state the loads it produces as surface loads that do not change.");
        if (model.ThermalDeltaT is not null)
            throw new FeaException(
                "A thermal load enters through the constitutive matrix, so it scales with "
                + "rho^p and is design-dependent by construction — the same reason a body load "
                + "is refused. Remove the thermal load.");
        for (int node = 0; node < model.Mesh.NodeCount; node++)
        {
            if (model.PrescribedOf(node) == Vector3d.Zero)
                continue;
            throw new FeaException(
                $"Node {node} carries a prescribed non-zero displacement. Under a prescribed "
                + "displacement the applied force moves WITH the stiffness, so minimising "
                + "f'u makes the structure as compliant as possible rather than as stiff as "
                + "possible — the sign of the whole problem flips. That is a different "
                + "formulation, not a harder one; drive the model with forces instead.");
        }

        bool restrained = false;
        for (int node = 0; node < model.Mesh.NodeCount && !restrained; node++)
            restrained = model.RestraintOf(node) != Dof.None;
        if (!restrained)
            throw new FeaException(
                "The model has no supports, so the stiffness is singular for every design. "
                + "Fix the facets the structure reacts against.");
    }
}
