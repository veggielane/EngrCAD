using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>Knobs for <see cref="MateSet.Solve"/>.</summary>
public sealed class MateSolverSettings
{
    /// <summary>Convergence tolerance, in model units. Every residual is a LENGTH (see
    /// <see cref="MateSet"/> on how angular residuals are scaled), so the default is the
    /// 1e-9 absolute weld tier: geometry a mate places must weld with geometry the
    /// kernel builds.</summary>
    public double Tolerance { get; init; } = EngrCAD.Core.Tolerance.Default.Linear;

    /// <summary>Maximum Levenberg–Marquardt iterations before the solve is declared
    /// failed. Rigid-body mate systems converge quadratically near a solution; 100 is
    /// generous and exists so a hopeless system fails in milliseconds, not forever.</summary>
    public int MaxIterations { get; init; } = 100;

    /// <summary>When true, a solve that converges but leaves free degrees of freedom is
    /// still reported as a failure (<see cref="MateSet.Solve"/> throws). Off by default:
    /// an under-constrained assembly is legitimate CAD — a hinge is SUPPOSED to keep a
    /// rotation — and the count is always reported either way.</summary>
    public bool RequireFullyConstrained { get; init; }
}

/// <summary>
/// One movable occurrence's share of the DOF report: how many of its own six degrees of
/// freedom the mate system's Jacobian SEES — the rank of the system restricted to that
/// occurrence's six variables. An upper bound on how pinned it is: a mate coupling two
/// occurrences constrains their RELATIVE motion, so block ranks can sum to more than
/// the global rank (which stays the authoritative total).
/// </summary>
/// <param name="Path">The occurrence's path relative to the mate set's assembly
/// ("clamp.2/bolt"; a direct occurrence is just its name).</param>
/// <param name="ConstrainedDegreesOfFreedom">Rank of the occurrence's own 6-variable
/// block of JᵀJ, via the same diagonally pivoted Cholesky as the global rank.</param>
public sealed record MateOccurrenceFreedom(string Path, int ConstrainedDegreesOfFreedom)
{
    /// <summary>Degrees of freedom of this occurrence no mate can see (6 − constrained).</summary>
    public int RemainingDegreesOfFreedom => 6 - ConstrainedDegreesOfFreedom;

    public override string ToString() => $"{Path}: {ConstrainedDegreesOfFreedom}/6";
}

/// <summary>What a mate solve did, and what it could not do.</summary>
/// <param name="Converged">Every residual fell below the tolerance.</param>
/// <param name="Iterations">Levenberg–Marquardt steps taken.</param>
/// <param name="Residual">The largest remaining residual, in model units.</param>
/// <param name="FreeDegreesOfFreedom">6 × the number of occurrences the mates actually
/// move (grounded ones, and ones no mate mentions, are not counted).</param>
/// <param name="ConstrainedDegreesOfFreedom">The rank of the constraint Jacobian at the
/// solution — how many of those degrees of freedom the mates actually pin down.</param>
/// <param name="Diagnostics">Human-readable notes: which mates are worst, which
/// occurrences no mate mentions, what is still free.</param>
public sealed record MateSolveResult(
    bool Converged,
    int Iterations,
    double Residual,
    int FreeDegreesOfFreedom,
    int ConstrainedDegreesOfFreedom,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>The DOF report per movable occurrence — see
    /// <see cref="MateOccurrenceFreedom"/> for what the per-occurrence number can and
    /// cannot claim. Empty when nothing was free.</summary>
    public IReadOnlyList<MateOccurrenceFreedom> OccurrenceFreedoms { get; init; } = [];

    /// <summary>Degrees of freedom the mates leave open (0 = fully constrained).</summary>
    public int RemainingDegreesOfFreedom => FreeDegreesOfFreedom - ConstrainedDegreesOfFreedom;

    /// <summary>True when the mates do not pin the assembly completely. Not an error by
    /// itself — see <see cref="MateSolverSettings.RequireFullyConstrained"/>.</summary>
    public bool IsUnderConstrained => RemainingDegreesOfFreedom > 0;

    /// <summary>
    /// True when the solve did not converge. For a rigid-body mate system that means the
    /// constraints cannot all be satisfied at once — usually genuinely contradictory
    /// mates (over-constrained), occasionally a start pose too far from any solution.
    /// <see cref="Diagnostics"/> names the mates carrying the residual, which is the
    /// difference between the two cases in practice.
    /// </summary>
    public bool IsOverConstrained => !Converged;

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(Converged ? "solved" : "FAILED")
            .Append($" in {Iterations} iteration{(Iterations == 1 ? "" : "s")}; ")
            .Append($"worst residual {Residual:g3}; ")
            .Append($"{ConstrainedDegreesOfFreedom} of {FreeDegreesOfFreedom} DOF constrained");
        if (RemainingDegreesOfFreedom > 0)
            text.Append($" ({RemainingDegreesOfFreedom} free)");
        foreach (string note in Diagnostics)
            text.Append("\n  · ").Append(note);
        return text.ToString();
    }
}

/// <summary>A mate solve that could not be satisfied; carries the full
/// <see cref="MateSolveResult"/>.</summary>
public sealed class MateSolveException(MateSolveResult result)
    : InvalidOperationException($"The mates could not be solved.\n{result}")
{
    /// <summary>The failed solve's report.</summary>
    public MateSolveResult Result { get; } = result;
}

/// <summary>
/// The constraints on one <see cref="Assembly"/>, and the solver that turns them into
/// occurrence poses.
///
/// <code>
/// var mates = new MateSet(rig)
///     .Ground(baseOccurrence)
///     .Add(Mate.Planar(
///         MateGeometry.PlanarFace(baseOccurrence, s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First()),
///         MateGeometry.PlanarFace(lidOccurrence,  s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First())))
///     .Add(Mate.Concentric(
///         MateGeometry.CylindricalFace(baseOccurrence, s => Bore(s, 0)),
///         MateGeometry.CylindricalFace(lidOccurrence,  s => Bore(s, 0))));
///
/// var report = mates.Solve();          // writes Occurrence.Frame; throws if unsatisfiable
/// </code>
///
/// <para><b>What it solves.</b> <see cref="Assembly.Flatten(double)"/> composes
/// <see cref="Occurrence.Frame"/>s, and those frames are mutable — so mating is exactly
/// "solve for the frames". The unknowns are the occurrences the mates actually
/// <em>target</em> (the deepest link of each reference's chain) that are not
/// <see cref="Ground(Occurrence)">grounded</see>: six numbers each (translation plus a
/// rotation increment about the occurrence's own origin). A nested sub-assembly no
/// mate reaches into is one rigid body and needs no special case; a mate built from an
/// occurrence path (<see cref="MateGeometry"/>'s <c>(Assembly, path, …)</c> overloads)
/// reaches <b>across levels</b> — the deep occurrence's local frame becomes the
/// unknown, its ancestors' frames compose as inputs, and any ancestor that is itself a
/// mate target contributes its own chain-rule columns to the Jacobian. One refusal
/// keeps that honest: a deep occurrence whose owning sub-assembly is placed more than
/// once is ONE shared frame — moving it would move every placement — so the solve
/// rejects it by name instead of quietly moving geometry the mate never mentioned.</para>
///
/// <para><b>How.</b> Levenberg–Marquardt on the constraint residuals with an
/// <b>analytic</b> Jacobian (finite differences would cap accuracy near 1e-8, an order
/// worse than the weld tier this aims at). A variable is a world-space rigid
/// perturbation of one occurrence (rotation taken about its composed world origin), so
/// the chain rule through a frame chain stays the same two formulas — translation
/// columns are the unit axes, rotation columns are <c>axis × (point − origin)</c> with
/// the origin READ OFF THE CHAIN — rather than a product of derivative matrices.
/// Angular residuals are multiplied by the assembly's characteristic length so every
/// residual is a length and one linear tolerance is meaningful; the rotation variables
/// are divided by the same length so every Jacobian column is O(1). Rank comes from a
/// diagonally pivoted Cholesky of JᵀJ — a rank-revealing factorization for the
/// symmetric positive-semidefinite matrix it is — which is what lets the deliberately
/// redundant rotational residual encodings (3 rows carrying 2 constraints) be counted
/// correctly; the same factorization on each occurrence's own 6×6 block yields the
/// per-occurrence DOF report (<see cref="MateSolveResult.OccurrenceFreedoms"/>).</para>
///
/// <para><b>Honesty rules.</b> A solve that does not converge writes NOTHING: the
/// occurrence frames are restored, so a failed mate never leaves an assembly half-moved.
/// The result always reports how many degrees of freedom the mates actually pinned, so
/// an under-constrained assembly says so rather than pretending.</para>
/// </summary>
public sealed partial class MateSet
{
    private readonly List<Mate> _mates = [];
    private readonly HashSet<Occurrence> _grounded = [];

    /// <summary>Grounds as occurrence PATHS, the spelling the solver's unknowns are keyed
    /// by. It matters only once a placement is
    /// <see cref="Occurrence.IsFlexible">flexible</see>, where grounding one placement's
    /// inner occurrence must leave the other placement's free; for a rigid tree a deep
    /// occurrence has exactly one legal path, so this and <see cref="Grounded"/> say the
    /// same thing.</summary>
    private readonly HashSet<string> _groundedPaths = [];

    /// <param name="assembly">The assembly whose occurrence frames are solved for.</param>
    public MateSet(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Assembly = assembly;
    }

    /// <summary>The assembly being constrained.</summary>
    public Assembly Assembly { get; }

    /// <summary>The mates, in the order they were added.</summary>
    public IReadOnlyList<Mate> Mates => _mates;

    /// <summary>Occurrences pinned in place (their frames are inputs, not unknowns).</summary>
    public IReadOnlyCollection<Occurrence> Grounded => _grounded;

    /// <summary>Adds a mate (chainable). Both ends must reference direct occurrences of
    /// <see cref="Assembly"/>, or be world-fixed.</summary>
    public MateSet Add(Mate mate)
    {
        ArgumentNullException.ThrowIfNull(mate);
        Validate(mate.A, mate);
        Validate(mate.B, mate);
        _mates.Add(mate);
        return this;
    }

    /// <summary>Pins an occurrence in place — the assembly's datum (chainable). Without
    /// at least one ground (or a world-fixed mate end), the whole assembly can drift as
    /// a rigid body and the solve reports 6 free degrees of freedom.</summary>
    public MateSet Ground(Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!Assembly.Occurrences.Contains(occurrence))
            throw new ArgumentException(
                $"Occurrence '{occurrence.Name}' is not a direct occurrence of assembly " +
                $"'{Assembly.Name}'. Ground a nested occurrence by its path: Ground(\"sub/{occurrence.Name}\").",
                nameof(occurrence));
        _grounded.Add(occurrence);
        _groundedPaths.Add(occurrence.Name);
        return this;
    }

    /// <summary>Pins the occurrence an occurrence path names ("clamp.2/bolt") — the
    /// cross-level form of <see cref="Ground(Occurrence)"/>. A grounded deep
    /// occurrence's local frame is an input, so a chain through it still moves with any
    /// free ancestors. Under a <see cref="Occurrence.IsFlexible">flexible</see>
    /// placement the ground is per-PLACEMENT, so grounding "clamp/bolt" leaves
    /// "clamp.2/bolt" free.</summary>
    public MateSet Ground(string path)
    {
        var chain = Assembly.ResolvePath(path);
        _grounded.Add(chain[^1]);
        _groundedPaths.Add(PathOf(chain));
        return this;
    }

    /// <summary>The canonical path spelling of a resolved chain — the same one
    /// <see cref="MateRef.Path"/> produces, so grounds and mate targets are keyed
    /// alike whichever way the caller spelled the path.</summary>
    internal static string PathOf(IReadOnlyList<Occurrence> chain) =>
        string.Join("/", chain.Select(o => o.Name));

    /// <summary>True when the occurrence a path names is pinned.</summary>
    internal bool IsGrounded(string path) => _groundedPaths.Contains(path);

    /// <summary>Removes a mate; false when it is not in this set. Note that removing a
    /// mate does not move anything — the frames it produced stay where the last solve put
    /// them until <see cref="Solve"/> runs again.</summary>
    public bool Remove(Mate mate)
    {
        ArgumentNullException.ThrowIfNull(mate);
        return _mates.Remove(mate);
    }

    /// <summary>The position of a mate in this set, or −1.</summary>
    public int IndexOf(Mate mate) => _mates.IndexOf(mate);

    /// <summary>Re-inserts a mate at a given position — the undo counterpart of
    /// <see cref="Remove"/>. Order is observable (it is the save order, and it is the row
    /// order a non-converged solve reports residuals in), so restoring it exactly is what
    /// makes an undone removal indistinguishable from never having happened.</summary>
    internal void Insert(int index, Mate mate)
    {
        Validate(mate.A, mate);
        Validate(mate.B, mate);
        _mates.Insert(index, mate);
    }

    /// <summary>Un-pins a grounded occurrence; false when it was not grounded.</summary>
    public bool Unground(Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        _groundedPaths.RemoveWhere(path => Resolves(path, occurrence));
        return _grounded.Remove(occurrence);
    }

    /// <summary>Whether a stored ground path still names this occurrence. A path that no
    /// longer resolves (the tree changed under the set) simply does not match — the
    /// load-warnings culture rather than an exception from an un-ground.</summary>
    private bool Resolves(string path, Occurrence occurrence)
    {
        try
        {
            return ReferenceEquals(Assembly.ResolvePath(path)[^1], occurrence);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void Validate(in MateRef reference, Mate mate)
    {
        if (reference.Occurrence is not { } occurrence)
            return;
        if (reference.Ancestors is { } ancestors)
        {
            // A chained reference must descend from THIS assembly, link by link — a
            // chain resolved against some other assembly would compose the wrong frames.
            var expected = Assembly.Occurrences;
            string walked = "";
            foreach (var link in ancestors.Append(occurrence))
            {
                if (!expected.Contains(link))
                    throw new ArgumentException(
                        $"Mate '{mate.Name}' references occurrence '{link.Name}'" +
                        (walked.Length == 0 ? "" : $" (below '{walked}')") +
                        $", which does not descend from assembly '{Assembly.Name}' along that chain. " +
                        "Build the reference from this assembly's own occurrence path " +
                        "(Assembly.ResolvePath).", nameof(mate));
                walked = walked.Length == 0 ? link.Name : $"{walked}/{link.Name}";
                expected = link.SubAssembly?.Occurrences ?? [];
            }
            return;
        }
        if (!Assembly.Occurrences.Contains(occurrence))
            throw new ArgumentException(
                $"Mate '{mate.Name}' references occurrence '{occurrence.Name}', which is not a direct " +
                $"occurrence of assembly '{Assembly.Name}'. Reference nested geometry by its occurrence " +
                $"path — MateGeometry.Point(assembly, \"sub/{occurrence.Name}\", …) — so the solver knows " +
                "which placement it belongs to, or build a MateSet on the sub-assembly itself.", nameof(mate));
    }

    /// <summary>
    /// Solves the mates and writes the occurrence frames. Throws
    /// <see cref="MateSolveException"/> when the constraints cannot be satisfied (and
    /// leaves every frame untouched), or when
    /// <see cref="MateSolverSettings.RequireFullyConstrained"/> is set and degrees of
    /// freedom remain.
    /// </summary>
    public MateSolveResult Solve(MateSolverSettings? settings = null)
    {
        settings ??= new MateSolverSettings();
        var result = TrySolve(settings);
        if (!result.Converged || (settings.RequireFullyConstrained && result.IsUnderConstrained))
            throw new MateSolveException(result);
        return result;
    }

    /// <summary>
    /// <see cref="Solve"/> without the exception: returns the report either way, and
    /// writes the occurrence frames only when it converged.
    /// </summary>
    public MateSolveResult TrySolve(MateSolverSettings? settings = null) =>
        new Solver(this, settings ?? new MateSolverSettings(), []).Run();

    /// <summary>
    /// <see cref="TrySolve(MateSolverSettings?)"/> with auxiliary constraints (screw
    /// pitch couplings, gear ratios, cams, drivers) appended to the residual vector —
    /// the seam <see cref="Mechanism"/> drives. With an empty list this is exactly the
    /// public solve.
    /// </summary>
    internal MateSolveResult TrySolve(MateSolverSettings settings, IReadOnlyList<AuxiliaryConstraint> extras) =>
        new Solver(this, settings, extras).Run();

    /// <summary>
    /// <see cref="TrySolve(MateSolverSettings, IReadOnlyList{AuxiliaryConstraint})"/>
    /// with the rank threshold widened for DIAGNOSIS — <see cref="Mechanism"/> probes
    /// "how close to rank-deficient is this pose" (a dead-centre detector) by
    /// comparing wide-threshold ranks between poses. Never used for the authoritative
    /// DOF count, which keeps the strict threshold.
    /// </summary>
    internal MateSolveResult TrySolve(
        MateSolverSettings settings, IReadOnlyList<AuxiliaryConstraint> extras, double rankTolerance) =>
        new Solver(this, settings, extras, rankTolerance).Run();

    // =====================================================================
    //  The solver
    // =====================================================================

    /// <summary>
    /// Rank threshold on SINGULAR values, relative to the largest — dimensionless, so it
    /// deliberately sits outside the linear <see cref="Tolerance"/> ladder. The pivots of
    /// the JᵀJ factorization are squared singular values, so it is applied SQUARED
    /// (comparing a squared quantity against an unsquared epsilon is the mistake this
    /// codebase has paid for repeatedly).
    /// </summary>
    private const double RankRelativeTolerance = 1e-8;

    private sealed partial class Solver(
        MateSet set, MateSolverSettings settings, IReadOnlyList<AuxiliaryConstraint> extras,
        double? rankTolerance = null)
    {
        private readonly double _rankTolerance = rankTolerance ?? RankRelativeTolerance;
        private readonly List<Variable> _free = [];

        /// <summary>Variable block per occurrence PATH, not per occurrence object: a
        /// sub-assembly placed twice reaches ONE occurrence object down two paths, and a
        /// flexible placement gives each of those its own pose. Keying by path is what
        /// makes the two independent unknowns; for a rigid tree an occurrence has exactly
        /// one legal path, so this is the object keying by another name.</summary>
        private readonly Dictionary<string, int> _block = [];

        /// <summary>One link of a mate reference's chain, resolved: the occurrence, its
        /// path from the set's assembly, and the POSE SLOT its frame lives in (its own
        /// <see cref="Occurrence.Frame"/>, or a flexible placement's overlay entry).</summary>
        private readonly record struct Link(Occurrence Occurrence, string Path, PoseSlot Slot);

        /// <summary>One solver unknown: where its pose is read and written, its path for
        /// reports, and the chain it composes through (outermost first; empty for a
        /// direct occurrence) — what <see cref="Apply"/> pulls the world-space step back
        /// through.</summary>
        private readonly record struct Variable(string Path, PoseSlot Slot, Link[] Ancestors);

        private Frame3d[] _poses = [];
        private double _length = 1;     // characteristic length: residual/variable scaling
        private int _rows;
        private int _columns;

        public MateSolveResult Run()
        {
            var mates = set.Mates;
            var diagnostics = new List<string>();

            foreach (var mate in mates)
            {
                Register(mate.A);
                Register(mate.B);
            }
            foreach (var extra in extras)
            {
                foreach (var end in extra.Ends)
                    Register(end);
            }

            _columns = _free.Count * 6;
            _rows = mates.Sum(m => m.RowCount) + extras.Sum(x => x.RowCount);
            _poses = [.. _free.Select(v => v.Slot.Read())];
            _length = CharacteristicLength();

            if (_rows == 0)
            {
                Note(diagnostics);
                return new MateSolveResult(true, 0, 0, _columns, 0, diagnostics);
            }

            var original = (Frame3d[])_poses.Clone();
            var residual = new double[_rows];
            var jacobian = new double[_rows * _columns];

            double worst = Evaluate(residual);
            int iteration = 0;
            int steps = 0;
            double lambda = 1e-3;

            // _columns == 0 means every mated occurrence is grounded: there is nothing to
            // move, so the residual below IS the answer (and says so if the mates are
            // violated) rather than a vacuous success.
            while (_columns > 0 && worst > settings.Tolerance && iteration < settings.MaxIterations)
            {
                iteration++;
                Jacobian(jacobian);
                var normal = new double[_columns * _columns];
                var gradient = new double[_columns];
                NormalEquations(jacobian, residual, normal, gradient);

                double maxDiagonal = 0;
                for (int i = 0; i < _columns; i++)
                    maxDiagonal = Math.Max(maxDiagonal, normal[i * _columns + i]);
                // Exact-zero semantic test: a Jacobian with no entries at all means the
                // mates cannot move anything, so there is no step to take.
                if (maxDiagonal <= 0)
                    break;

                var before = (Frame3d[])_poses.Clone();
                bool accepted = false;
                for (int attempt = 0; attempt < 12 && !accepted; attempt++)
                {
                    var damped = (double[])normal.Clone();
                    for (int i = 0; i < _columns; i++)
                        damped[i * _columns + i] += lambda * maxDiagonal;

                    if (!SolveSpd(damped, gradient, out var step))
                    {
                        lambda *= 8;
                        continue;
                    }

                    Array.Copy(before, _poses, before.Length);
                    Apply(step);                       // step solves A δ = g, so move by −δ
                    double candidate = Evaluate(residual);
                    if (candidate < worst)
                    {
                        worst = candidate;
                        lambda = Math.Max(lambda / 3, 1e-12);
                        accepted = true;
                        steps++;
                    }
                    else
                    {
                        lambda *= 8;
                    }
                }

                if (!accepted)
                {
                    Array.Copy(before, _poses, before.Length);
                    worst = Evaluate(residual);
                    break;                              // no damping value improves it
                }
            }

            bool converged = worst <= settings.Tolerance;

            // Rank at the final pose: how much of the free motion the mates actually pin.
            Jacobian(jacobian);
            var final = new double[_columns * _columns];
            var unusedGradient = new double[_columns];
            NormalEquations(jacobian, residual, final, unusedGradient);
            int rank = Rank(final, _columns, _rankTolerance);

            // Per-occurrence report: the rank of each occurrence's own 6×6 block of JᵀJ —
            // how many of ITS motions the mates can see (an upper bound on pinning;
            // the global rank above stays the authoritative total).
            var freedoms = new List<MateOccurrenceFreedom>(_free.Count);
            for (int b = 0; b < _free.Count; b++)
            {
                var block = new double[36];
                for (int r = 0; r < 6; r++)
                {
                    for (int c = 0; c < 6; c++)
                        block[r * 6 + c] = final[(b * 6 + r) * _columns + (b * 6 + c)];
                }
                freedoms.Add(new MateOccurrenceFreedom(_free[b].Path, Rank(block, 6, _rankTolerance)));
            }

            if (converged)
            {
                // Each unknown writes back to ITS OWN slot: a direct occurrence's frame,
                // or the flexible placement's overlay entry the path names.
                for (int i = 0; i < _free.Count; i++)
                    _free[i].Slot.Write(_poses[i]);
            }
            else
            {
                // Refuse loudly AND cleanly. The frames were never written during
                // iteration — only here, on success — so "restore" is really "discard the
                // working poses": the assembly is left exactly as the caller left it.
                Array.Copy(original, _poses, original.Length);
                WorstMates(residual, diagnostics);
                if (steps == 0 && _columns > 0)
                {
                    // The residual is nonzero but its gradient is not: a STATIONARY start.
                    // The textbook case is an Angle or Perpendicular mate whose directions
                    // begin exactly parallel — d/dθ cos θ is zero at θ = 0, so no
                    // first-order step exists. Randomly nudging and retrying would
                    // "sometimes converge", which is worse than saying so.
                    diagnostics.Add(
                        "no first-order motion improves the residual: the starting pose is a stationary " +
                        "configuration for these mates (an Angle or Perpendicular mate whose directions " +
                        "start exactly parallel is the usual cause). Place the occurrence roughly where it " +
                        "belongs and solve again.");
                }
            }

            Note(diagnostics);
            if (_columns - rank > 0)
            {
                diagnostics.Add(FreedomNote(_columns - rank));
                if (_free.Count > 1)
                    diagnostics.Add(
                        $"per occurrence, the mates see: {string.Join(", ", freedoms)} degrees of " +
                        "freedom (an upper bound — a mate between two occurrences pins relative motion)");
            }

            return new MateSolveResult(converged, iteration, worst, _columns, rank, diagnostics)
                { OccurrenceFreedoms = freedoms };
        }

        private void Register(in MateRef reference)
        {
            if (reference.Occurrence is not { } occurrence)
                return;
            string path = reference.Path!;
            if (set.IsGrounded(path) || _block.ContainsKey(path))
                return;
            if (reference.Ancestors is null)
            {
                _block[path] = _free.Count;
                _free.Add(new Variable(path, PoseSlot.Shared(occurrence), []));
                return;
            }

            var links = Chain(reference);
            RequireAddressable(reference, links);
            _block[path] = _free.Count;
            _free.Add(new Variable(path, links[^1].Slot, links[..^1]));
        }

        /// <summary>
        /// Resolves a cross-level reference's chain: each link's occurrence, its path
        /// from the set's assembly, and the pose slot its frame lives in. The walk tracks
        /// the <see cref="FlexiblePlacement"/> scope, so a link beneath a flexible
        /// placement is addressed in THAT placement's overlay — which is what lets two
        /// placements of one sub-assembly carry two independent unknowns.
        /// </summary>
        private static Link[] Chain(in MateRef reference)
        {
            var ancestors = reference.Ancestors!;
            var links = new Link[ancestors.Count + 1];
            var scope = FlexiblePlacement.None;
            string path = "";
            for (int i = 0; i < links.Length; i++)
            {
                var link = i < ancestors.Count ? ancestors[i] : reference.Occurrence!;
                path = i == 0 ? link.Name : $"{path}/{link.Name}";
                var slot = scope.IsActive
                    ? new PoseSlot(link, scope.Placement, scope.PathOf(link))
                    : PoseSlot.Shared(link);
                links[i] = new Link(link, path, slot);
                scope = scope.Below(link);
            }
            return links;
        }

        /// <summary>
        /// Refuses a reference whose target pose is not addressable: a sub-assembly's
        /// internal frames are ONE object however many times it is placed, so moving this
        /// occurrence would move geometry in EVERY placement, most of which the mate never
        /// mentioned. A FLEXIBLE placement is the way out — its overlay is keyed by the
        /// relative path, so each placement carries its own pose — and once a link opens
        /// that scope everything below it is per-placement and needs no further check.
        /// </summary>
        private void RequireAddressable(in MateRef reference, Link[] links)
        {
            bool flexible = false;
            for (int i = 0; i < links.Length; i++)
            {
                var owner = i == 0 ? set.Assembly : links[i - 1].Occurrence.SubAssembly!;
                if (!flexible)
                {
                    var placements = set.Assembly.PlacementsOf(owner);
                    if (placements.Count > 1)
                        throw new InvalidOperationException(
                            $"A mate targets '{reference.Path}', but assembly '{owner.Name}' is placed " +
                            $"{placements.Count} times ({string.Join(", ", placements)}), and " +
                            $"'{links[i].Occurrence.Name}' is one shared frame across all of them — " +
                            "solving would move every placement. Make the placement FLEXIBLE " +
                            "(occurrence.IsFlexible = true) so it carries its own internal poses, ground " +
                            "it, mate the sub-assembly occurrence itself, or give each placement its own " +
                            "Assembly.");
                }
                flexible |= links[i].Occurrence.IsFlexible;
            }
        }

        private void Note(List<string> diagnostics)
        {
            var unmated = set.Assembly.Occurrences
                .Where(o => !_block.ContainsKey(o.Name) && !set.IsGrounded(o.Name))
                .Select(o => o.Name)
                .ToList();
            if (unmated.Count > 0)
            {
                string names = string.Join(", ", unmated.Take(6));
                if (unmated.Count > 6)
                    names += $", +{unmated.Count - 6} more";
                diagnostics.Add($"no mate mentions {names} — left where they were placed");
            }
        }

        private static string FreedomNote(int remaining) =>
            $"{remaining} degree{(remaining == 1 ? "" : "s")} of freedom remain: the mates do not " +
            "pin the assembly completely (add a mate, or ground an occurrence)";

        private void WorstMates(double[] residual, List<string> diagnostics)
        {
            var offenders = new List<(string Name, double Residual)>();
            int row = 0;
            foreach (var mate in set.Mates)
            {
                double peak = 0;
                for (int i = 0; i < mate.RowCount; i++)
                    peak = Math.Max(peak, Math.Abs(residual[row + i]));
                row += mate.RowCount;
                offenders.Add((mate.Name, peak));
            }
            foreach (var extra in extras)
            {
                double peak = 0;
                for (int i = 0; i < extra.RowCount; i++)
                    peak = Math.Max(peak, Math.Abs(residual[row + i]));
                row += extra.RowCount;
                offenders.Add((extra.Name, peak));
            }
            foreach (var (name, peak) in offenders.OrderByDescending(o => o.Residual).Take(3))
                diagnostics.Add($"'{name}' is off by {peak:g4}");
            diagnostics.Add("nothing was moved — the occurrence frames are unchanged");
        }

        /// <summary>The model's own scale, from the geometry the mates reference: mate
        /// points and occurrence origins. Used to convert angular residuals to lengths and
        /// to normalize the rotation variables. Falls back to 1 for a mate set whose
        /// references all sit at the origin, where no scale is observable.</summary>
        private double CharacteristicLength()
        {
            double length = 0;
            foreach (var mate in set.Mates)
            {
                length = Math.Max(length, mate.A.Point.Length);
                length = Math.Max(length, mate.B.Point.Length);
                // A deep reference's local point can be small while its chain carries it
                // far out — the composed starting world point sees that scale.
                if (mate.A.Ancestors is not null)
                    length = Math.Max(length, Evaluate(mate.A).Point.Length);
                if (mate.B.Ancestors is not null)
                    length = Math.Max(length, Evaluate(mate.B).Point.Length);
            }
            foreach (var extra in extras)
            {
                foreach (var end in extra.Ends)
                {
                    length = Math.Max(length, end.Point.Length);
                    if (end.Ancestors is not null)
                        length = Math.Max(length, Evaluate(end).Point.Length);
                }
            }
            foreach (var occurrence in set.Assembly.Occurrences)
                length = Math.Max(length, occurrence.Frame.Origin.Length);
            return length > 0 ? length : 1;   // exact-zero semantic test
        }

        // ---- residuals ----

        /// <summary>One free occurrence a mate end's pose composes through: its variable
        /// block and its composed WORLD origin — the moment arm of its rotation columns.</summary>
        private readonly record struct Contribution(int Block, Vector3d Origin);

        private readonly record struct End(Contribution[] Contributions, Vector3d Point, Vector3d Direction);

        private static readonly Contribution[] NoContributions = [];

        private End Evaluate(in MateRef reference)
        {
            if (reference.Occurrence is not { } occurrence)
                return new End(NoContributions, reference.Point, reference.Direction);
            if (reference.Ancestors is null)
            {
                // Direct occurrence: its local frame IS its world frame, and the root
                // assembly's own occurrences are never inside a flexible placement. Kept
                // as its own arithmetic (no identity composition) so one-level solves stay
                // bit-identical to the original single-level solver.
                bool free = _block.TryGetValue(occurrence.Name, out int block);
                var frame = free ? _poses[block] : occurrence.Frame;   // grounded: an input
                return new End(
                    free ? [new Contribution(block, frame.Origin)] : NoContributions,
                    frame.ToWorld(reference.Point),
                    frame.ToWorldVector(reference.Direction));
            }

            // Cross-level: compose the chain outermost-first, collecting a Jacobian
            // contribution for every link that is a solver variable — the chain rule
            // through the frame chain, expressed as "which world origins can this end
            // rotate about".
            var contributions = new List<Contribution>(2);
            var links = Chain(reference);
            Frame3d world = default;
            for (int i = 0; i < links.Length; i++)
                world = Compose(links[i], world, i == 0, contributions);
            return new End(
                [.. contributions],
                world.ToWorld(reference.Point),
                world.ToWorldVector(reference.Direction));
        }

        /// <summary>Composes one chain link onto the running world frame, using the
        /// working pose when the link is a variable and its own slot's pose otherwise,
        /// and records its contribution.</summary>
        private Frame3d Compose(in Link link, in Frame3d parent, bool first, List<Contribution> contributions)
        {
            bool free = _block.TryGetValue(link.Path, out int block);
            var local = free ? _poses[block] : link.Slot.Read();
            var world = first ? local : local.Then(parent);
            if (free)
                contributions.Add(new Contribution(block, world.Origin));
            return world;
        }

        /// <summary>A free unknown's ancestor world frame from the CURRENT working
        /// poses, or null for a direct occurrence.</summary>
        private Frame3d? AncestorWorld(in Variable variable)
        {
            var ancestors = variable.Ancestors;
            if (ancestors.Length == 0)
                return null;
            var world = LocalPose(ancestors[0]);
            for (int i = 1; i < ancestors.Length; i++)
                world = LocalPose(ancestors[i]).Then(world);
            return world;
        }

        private Frame3d LocalPose(in Link link) =>
            _block.TryGetValue(link.Path, out int block) ? _poses[block] : link.Slot.Read();

        /// <summary>Fills the residual vector and returns the largest absolute entry.</summary>
        private double Evaluate(double[] residual)
        {
            int row = 0;
            foreach (var mate in set.Mates)
            {
                var a = Evaluate(mate.A);
                var b = Evaluate(mate.B);
                var w = b.Point - a.Point;
                switch (mate.Kind)
                {
                    case MateKind.Coincident:
                        Write(residual, ref row, w);
                        break;

                    case MateKind.Planar:
                        // Normals oppose (3 rows, rank 2) and the faces sit `gap` apart
                        // along A's normal (1 row).
                        Write(residual, ref row, (a.Direction + b.Direction) * _length);
                        residual[row++] = w.Dot(a.Direction) - mate.Value;
                        break;

                    case MateKind.Concentric:
                        // Axes parallel (3 rows, rank 2) and A's axis passes through B's
                        // reference point (3 rows, rank 2).
                        Write(residual, ref row, a.Direction.Cross(b.Direction) * _length);
                        Write(residual, ref row, w - a.Direction * w.Dot(a.Direction));
                        break;

                    case MateKind.Parallel:
                        Write(residual, ref row, a.Direction.Cross(b.Direction) * _length);
                        break;

                    case MateKind.Perpendicular:
                        residual[row++] = a.Direction.Dot(b.Direction) * _length;
                        break;

                    case MateKind.Angle:
                        residual[row++] = (a.Direction.Dot(b.Direction) - Math.Cos(mate.Value)) * _length;
                        break;

                    default:   // Distance
                        residual[row++] = w.Length - mate.Value;
                        break;
                }
            }

            foreach (var extra in extras)
            {
                var ends = EvaluateEnds(extra);
                extra.Residual(ends, _length, residual.AsSpan(row, extra.RowCount));
                row += extra.RowCount;
            }

            double worst = 0;
            for (int i = 0; i < residual.Length; i++)
                worst = Math.Max(worst, Math.Abs(residual[i]));
            return worst;
        }

        private static void Write(double[] residual, ref int row, in Vector3d value)
        {
            residual[row++] = value.X;
            residual[row++] = value.Y;
            residual[row++] = value.Z;
        }

        // ---- analytic Jacobian ----

        /// <summary>Derivative of an end's world point and direction with respect to
        /// variable <paramref name="column"/>. Translation variables move the point and
        /// leave the direction; rotation variables (scaled by the characteristic length so
        /// the columns are O(1)) rotate both about the owning occurrence's composed WORLD
        /// origin — for a cross-level end that is the chain rule: a perturbation of any
        /// free link in the chain is a world-space rigid motion of everything below it,
        /// so the formulas are the one-level ones with the moment arm read off the chain.
        /// At most one contribution owns a given column (an occurrence appears once per
        /// chain — cycles are rejected at Assembly.Add).</summary>
        private (Vector3d Point, Vector3d Direction) Derivative(in End end, int column)
        {
            foreach (var contribution in end.Contributions)
            {
                int local = column - contribution.Block * 6;
                if (local < 0 || local >= 6)
                    continue;
                if (local < 3)
                    return (Unit(local), Vector3d.Zero);
                var axis = Unit(local - 3);
                return (axis.Cross(end.Point - contribution.Origin) / _length,
                        axis.Cross(end.Direction) / _length);
            }
            return (Vector3d.Zero, Vector3d.Zero);
        }

        private static Vector3d Unit(int index) => index switch
        {
            0 => Vector3d.UnitX,
            1 => Vector3d.UnitY,
            _ => Vector3d.UnitZ,
        };

        private void Jacobian(double[] jacobian)
        {
            Array.Clear(jacobian);
            int row = 0;
            foreach (var mate in set.Mates)
            {
                var a = Evaluate(mate.A);
                var b = Evaluate(mate.B);
                var w = b.Point - a.Point;
                int start = row;
                for (int column = 0; column < _columns; column++)
                {
                    var (dPa, dDa) = Derivative(a, column);
                    var (dPb, dDb) = Derivative(b, column);
                    var dw = dPb - dPa;
                    row = start;
                    switch (mate.Kind)
                    {
                        case MateKind.Coincident:
                            Set(jacobian, ref row, column, dw);
                            break;

                        case MateKind.Planar:
                            Set(jacobian, ref row, column, (dDa + dDb) * _length);
                            jacobian[row++ * _columns + column] = dw.Dot(a.Direction) + w.Dot(dDa);
                            break;

                        case MateKind.Concentric:
                            Set(jacobian, ref row, column,
                                (dDa.Cross(b.Direction) + a.Direction.Cross(dDb)) * _length);
                            // d/dx of  w − (w·n)n   with both w and n varying.
                            Set(jacobian, ref row, column,
                                dw - dDa * w.Dot(a.Direction)
                                   - a.Direction * (dw.Dot(a.Direction) + w.Dot(dDa)));
                            break;

                        case MateKind.Parallel:
                            Set(jacobian, ref row, column,
                                (dDa.Cross(b.Direction) + a.Direction.Cross(dDb)) * _length);
                            break;

                        case MateKind.Perpendicular:
                        case MateKind.Angle:
                            jacobian[row++ * _columns + column] =
                                (dDa.Dot(b.Direction) + a.Direction.Dot(dDb)) * _length;
                            break;

                        default:   // Distance
                        {
                            // |w| is not differentiable at w = 0; any unit direction is a
                            // valid descent direction there, and one step moves the points
                            // apart so the true gradient takes over.
                            double norm = w.Length;
                            var unit = norm > 0 ? w / norm : Vector3d.UnitX;
                            jacobian[row++ * _columns + column] = unit.Dot(dw);
                            break;
                        }
                    }
                }
                row = start + mate.RowCount;
            }

            foreach (var extra in extras)
            {
                int count = extra.Ends.Count;
                var ends = new End[count];
                var values = new EndValue[count];
                for (int i = 0; i < count; i++)
                {
                    ends[i] = Evaluate(extra.Ends[i]);
                    values[i] = new EndValue(ends[i].Point, ends[i].Direction);
                }
                var deltas = new EndDelta[count];
                // Heap, not stackalloc: this sits in a loop, and stackalloc in a loop
                // grows the frame per extra until the method returns.
                Span<double> rows = new double[extra.RowCount];
                for (int column = 0; column < _columns; column++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var (dPoint, dDirection) = Derivative(ends[i], column);
                        deltas[i] = new EndDelta(dPoint, dDirection);
                    }
                    extra.Derivative(values, deltas, _length, rows);
                    for (int r = 0; r < extra.RowCount; r++)
                        jacobian[(row + r) * _columns + column] = rows[r];
                }
                row += extra.RowCount;
            }
        }

        /// <summary>The world geometry of an auxiliary constraint's ends at the current
        /// working poses.</summary>
        private EndValue[] EvaluateEnds(AuxiliaryConstraint extra)
        {
            var ends = new EndValue[extra.Ends.Count];
            for (int i = 0; i < ends.Length; i++)
            {
                var end = Evaluate(extra.Ends[i]);
                ends[i] = new EndValue(end.Point, end.Direction);
            }
            return ends;
        }

        private void Set(double[] jacobian, ref int row, int column, in Vector3d value)
        {
            jacobian[row++ * _columns + column] = value.X;
            jacobian[row++ * _columns + column] = value.Y;
            jacobian[row++ * _columns + column] = value.Z;
        }

        private void NormalEquations(double[] jacobian, double[] residual, double[] normal, double[] gradient)
        {
            Array.Clear(normal);
            Array.Clear(gradient);
            for (int r = 0; r < _rows; r++)
            {
                int offset = r * _columns;
                double rr = residual[r];
                for (int i = 0; i < _columns; i++)
                {
                    double ji = jacobian[offset + i];
                    // Exact-zero skip: the Jacobian is very sparse (each mate touches at
                    // most 12 of the columns), so this is the difference between an O(n²)
                    // and an O(12n) inner loop, not a tolerance decision.
                    if (ji == 0)
                        continue;
                    gradient[i] += ji * rr;
                    int row = i * _columns;
                    for (int j = 0; j < _columns; j++)
                        normal[row + j] += ji * jacobian[offset + j];
                }
            }
        }

        /// <summary>Moves the poses by −<paramref name="step"/> (the step solves
        /// <c>A δ = Jᵀr</c>, and Gauss–Newton descends against the gradient). A variable
        /// is a WORLD-space perturbation, so a deep occurrence's step is applied to its
        /// composed world frame and pulled back through its ancestors into the parent's
        /// coordinates — where <see cref="Occurrence.Frame"/> lives. Ancestor frames are
        /// snapshotted BEFORE any pose is written: an occurrence and its free ancestor
        /// update in the same step, and both pullbacks must read one consistent pre-step
        /// configuration (the linearization the Jacobian described).</summary>
        private void Apply(double[] step)
        {
            var ancestorWorlds = new Frame3d?[_free.Count];
            for (int i = 0; i < _free.Count; i++)
                ancestorWorlds[i] = AncestorWorld(_free[i]);

            for (int i = 0; i < _free.Count; i++)
            {
                var translation = new Vector3d(-step[i * 6], -step[i * 6 + 1], -step[i * 6 + 2]);
                // Rotation variables were scaled by the characteristic length; undo that
                // to get the true rotation vector.
                var rotation = new Vector3d(-step[i * 6 + 3], -step[i * 6 + 4], -step[i * 6 + 5]) / _length;

                if (ancestorWorlds[i] is not { } ancestor)
                {
                    // Direct occurrence: local == world. Kept verbatim (no identity
                    // composition) so one-level solves stay bit-identical.
                    var frame = _poses[i];
                    _poses[i] = Frame3d.FromXY(
                        frame.Origin + translation, Rotate(frame.X, rotation), Rotate(frame.Y, rotation));
                    continue;
                }

                var world = _poses[i].Then(ancestor);
                var moved = Frame3d.FromXY(
                    world.Origin + translation, Rotate(world.X, rotation), Rotate(world.Y, rotation));
                _poses[i] = moved.Then(ancestor.Inverse());
            }
        }

        /// <summary>Rodrigues rotation of <paramref name="v"/> by the rotation vector
        /// <paramref name="w"/> (axis × angle).</summary>
        private static Vector3d Rotate(in Vector3d v, in Vector3d w)
        {
            double theta = w.Length;
            if (theta <= 0)          // exact-zero semantic test: no rotation requested
                return v;
            var k = w / theta;
            double c = Math.Cos(theta), s = Math.Sin(theta);
            return v * c + k.Cross(v) * s + k * (k.Dot(v) * (1 - c));
        }

        // ---- dense linear algebra (small, dense, symmetric positive definite) ----

        /// <summary>Cholesky solve of a symmetric positive-definite system; false when the
        /// matrix is not positive definite (the caller raises the damping and retries).</summary>
        private bool SolveSpd(double[] a, double[] b, out double[] x)
        {
            int n = _columns;
            var l = new double[n * n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double sum = a[i * n + j];
                    for (int k = 0; k < j; k++)
                        sum -= l[i * n + k] * l[j * n + k];
                    if (i == j)
                    {
                        if (sum <= 0)   // exact-zero/negative pivot: not positive definite
                        {
                            x = [];
                            return false;
                        }
                        l[i * n + i] = Math.Sqrt(sum);
                    }
                    else
                    {
                        l[i * n + j] = sum / l[j * n + j];
                    }
                }
            }

            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = b[i];
                for (int k = 0; k < i; k++)
                    sum -= l[i * n + k] * y[k];
                y[i] = sum / l[i * n + i];
            }
            x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = y[i];
                for (int k = i + 1; k < n; k++)
                    sum -= l[k * n + i] * x[k];
                x[i] = sum / l[i * n + i];
            }
            return true;
        }

        /// <summary>
        /// Rank of a symmetric positive-semidefinite matrix by diagonally pivoted
        /// Cholesky (symmetric Gaussian elimination on the largest remaining diagonal) —
        /// the standard rank-revealing factorization for this class of matrix, and the
        /// reason the redundant rotational residual rows do not inflate the DOF count.
        /// </summary>
        private static int Rank(double[] a, int n, double relativeTolerance)
        {
            if (n == 0)
                return 0;
            var m = (double[])a.Clone();
            var live = new bool[n];
            Array.Fill(live, true);

            double first = 0;
            for (int i = 0; i < n; i++)
                first = Math.Max(first, m[i * n + i]);
            if (first <= 0)          // exact-zero semantic test: nothing is constrained
                return 0;
            // Pivots are SQUARED singular values, so the relative singular-value threshold
            // enters squared.
            double floor = first * relativeTolerance * relativeTolerance;

            int rank = 0;
            for (int step = 0; step < n; step++)
            {
                int pivot = -1;
                double best = floor;
                for (int i = 0; i < n; i++)
                {
                    if (live[i] && m[i * n + i] > best)
                    {
                        best = m[i * n + i];
                        pivot = i;
                    }
                }
                if (pivot < 0)
                    break;

                rank++;
                live[pivot] = false;
                double d = m[pivot * n + pivot];
                for (int i = 0; i < n; i++)
                {
                    if (!live[i])
                        continue;
                    double factor = m[i * n + pivot] / d;
                    if (factor == 0)
                        continue;
                    for (int j = 0; j < n; j++)
                    {
                        if (live[j])
                            m[i * n + j] -= factor * m[pivot * n + j];
                    }
                }
            }
            return rank;
        }
    }
}
