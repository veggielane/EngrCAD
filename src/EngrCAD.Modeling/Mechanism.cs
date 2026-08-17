using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// Pins one joint variable so a mechanism can be DRIVEN: solving with a driver at
/// target t is the ordinary mate solve with that variable fixed, consuming one degree
/// of freedom. Targets are measured from the joint's construction zero — unwrapped
/// radians for an angle (a crank swept twice is driven to 4π), model units for a
/// slide.
/// </summary>
public sealed class MechanismDriver
{
    private MechanismDriver(AxisJoint joint, bool drivesAngle) =>
        Constraint = new DriverConstraint(joint, drivesAngle);

    internal DriverConstraint Constraint { get; }

    /// <summary>The driven joint.</summary>
    public AxisJoint Joint => Constraint.Joint;

    /// <summary>True when the driven variable is the spin angle; false for the slide.</summary>
    public bool DrivesAngle => Constraint.DrivesAngle;

    /// <summary>Drives a joint's spin angle (revolute, cylindrical, or screw — a
    /// prismatic or fixed joint's spin is already locked by its mates, so driving it
    /// would fight them and is refused).</summary>
    public static MechanismDriver Angle(AxisJoint joint)
    {
        ArgumentNullException.ThrowIfNull(joint);
        if (joint is PrismaticJoint or FixedJoint)
            throw new ArgumentException(
                $"Joint '{joint.Name}' locks its spin (its mates pin the angle), so an angle driver " +
                "would fight the joint's own constraints. Drive the slide, or use a revolute/cylindrical/" +
                "screw joint.", nameof(joint));
        return new MechanismDriver(joint, drivesAngle: true);
    }

    /// <summary>Drives a joint's slide (prismatic, cylindrical, or screw — a revolute
    /// or fixed joint's slide is already pinned, so driving it is refused).</summary>
    public static MechanismDriver Slide(AxisJoint joint)
    {
        ArgumentNullException.ThrowIfNull(joint);
        if (joint is RevoluteJoint or FixedJoint)
            throw new ArgumentException(
                $"Joint '{joint.Name}' pins its slide (its mates fix the axial position), so a slide " +
                "driver would fight the joint's own constraints. Drive the angle, or use a prismatic/" +
                "cylindrical/screw joint.", nameof(joint));
        return new MechanismDriver(joint, drivesAngle: false);
    }

    public override string ToString() => Constraint.Name;
}

/// <summary>One driver pinned at one value — the unit a multi-driver solve takes.</summary>
/// <param name="Driver">Which joint variable is pinned.</param>
/// <param name="Value">Its target, from the joint's construction zero.</param>
public readonly record struct DriverTarget(MechanismDriver Driver, double Value)
{
    /// <summary>Sugar so a call site can write <c>(driver, 1.2)</c>.</summary>
    public static implicit operator DriverTarget((MechanismDriver Driver, double Value) pair) =>
        new(pair.Driver, pair.Value);
}

/// <summary>One driver's range for a multi-driver sweep. Every driver runs its own
/// From→To over the SAME normalized parameter, so a sweep traces a straight line
/// through driver space — a coordinated motion, not a grid.</summary>
public readonly record struct DriverRange(MechanismDriver Driver, double From, double To)
{
    /// <summary>Sugar so a call site can write <c>(driver, 0, Math.Tau)</c>.</summary>
    public static implicit operator DriverRange((MechanismDriver Driver, double From, double To) triple) =>
        new(triple.Driver, triple.From, triple.To);

    /// <summary>This driver's target at normalized sweep position <paramref name="s"/> ∈ [0, 1].</summary>
    public DriverTarget At(double s) => new(Driver, From + (To - From) * s);
}

/// <summary>One driver's motion for a rates query: where it is, how fast it is moving,
/// and how fast that is changing.</summary>
public readonly record struct DriverMotion(
    MechanismDriver Driver, double Value, double Rate = 1, double Acceleration = 0)
{
    /// <summary>Sugar for the common (driver, value, rate) spelling.</summary>
    public static implicit operator DriverMotion((MechanismDriver Driver, double Value, double Rate) triple) =>
        new(triple.Driver, triple.Value, triple.Rate);
}

/// <summary>One sampled pose of a driven mechanism: the driver value and the flattened
/// part instances at that pose — pure poses, no geometry, which is what lets a viewer
/// animate a study with matrices only.</summary>
public sealed record MotionFrame(double Value, IReadOnlyList<PartInstance> Instances)
{
    private readonly IReadOnlyList<double>? _values;

    /// <summary>Every driver's value at this frame, in the sweep's driver order. A
    /// single-driver sweep reports <c>[Value]</c>, so a consumer written against
    /// <see cref="Value"/> keeps working and one written against this is general.</summary>
    public IReadOnlyList<double> Values
    {
        get => _values ?? [Value];
        init => _values = value;
    }
}

/// <summary>
/// The result of sweeping a driver across a range: poses per sampled frame, and an
/// honest account of how far the sweep got. A sweep that fails reports the last good
/// parameter and leaves the assembly AT that last good pose (a failed step writes
/// nothing — the solver's own contract); a sweep that hits a singular configuration
/// says so and names what lost rank rather than guessing a branch.
/// </summary>
public sealed partial class MotionStudy
{
    internal MotionStudy(
        Mechanism mechanism, IReadOnlyList<MechanismDriver> drivers, IReadOnlyList<MotionFrame> frames,
        bool completed, double? failedAt, bool singular, IReadOnlyList<string> diagnostics)
    {
        Mechanism = mechanism;
        Drivers = drivers;
        Frames = frames;
        Completed = completed;
        FailedAt = failedAt;
        Singular = singular;
        Diagnostics = diagnostics;
    }

    /// <summary>The mechanism the study swept.</summary>
    public Mechanism Mechanism { get; }

    /// <summary>Every driver the sweep moved, in the order their values appear in
    /// <see cref="MotionFrame.Values"/>.</summary>
    public IReadOnlyList<MechanismDriver> Drivers { get; }

    /// <summary>The first driver — the whole story for a one-driver sweep, which is
    /// what <see cref="MotionFrame.Value"/> and <see cref="FailedAt"/> report against.</summary>
    public MechanismDriver Driver => Drivers[0];

    /// <summary>The sampled frames, in sweep order. A failed sweep keeps every frame
    /// up to the failure.</summary>
    public IReadOnlyList<MotionFrame> Frames { get; }

    /// <summary>True when the sweep reached the end of its range.</summary>
    public bool Completed { get; }

    /// <summary>The last driver value that solved, when the sweep did not complete.</summary>
    public double? FailedAt { get; }

    /// <summary>True when the sweep stopped because the Jacobian lost rank — a dead
    /// centre / toggle point, where the mechanism can branch or lock and guessing a
    /// branch would be dishonest.</summary>
    public bool Singular { get; }

    /// <summary>What went wrong (and where), in the solver's diagnostic style.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    public override string ToString() =>
        (Completed
            ? $"swept {Frames.Count} frames"
            : $"FAILED after {Frames.Count} frames at driver value {FailedAt:g6}" +
              (Singular ? " (singular configuration)" : ""))
        + string.Concat(Diagnostics.Select(d => "\n  · " + d));
}

/// <summary>A mechanism operation that could not proceed (a sweep that cannot start, a
/// joint driven past a limit, a rates request on an under-constrained pose).</summary>
public sealed class MechanismException(string message) : InvalidOperationException(message);

/// <summary>
/// The Grübler/Kutzbach mobility formula beside the solver's measured rank — a
/// CROSS-CHECK, not a source of truth. The formula predicts DOF from joint counts
/// (M = 6·moving − Σ(6 − fᵢ) − couplings); the solver measures the actual rank of the
/// constraint Jacobian. <b>Disagreement is informative rather than an error</b>:
/// overconstrained-but-mobile linkages — a planar four-bar built in space, Bennett's
/// linkage, the Sarrus linkage — are precisely where the formula lies and the rank is
/// right, because redundant constraints consume no motion the rank can see.
/// </summary>
/// <param name="MovingBodies">Occurrences the mates actually move.</param>
/// <param name="JointCount">Joints in the mechanism (raw mates are outside the
/// formula's vocabulary and are flagged in <see cref="Notes"/>).</param>
/// <param name="JointFreedoms">Σ fᵢ over the joints' nominal DOF.</param>
/// <param name="CouplingCount">Higher-pair couplings, one predicted DOF each.</param>
/// <param name="PredictedDegreesOfFreedom">Kutzbach's answer.</param>
/// <param name="MeasuredDegreesOfFreedom">The solver's rank-based answer at the
/// current pose (meaningful at an assembled pose).</param>
public sealed record MobilityReport(
    int MovingBodies,
    int JointCount,
    int JointFreedoms,
    int CouplingCount,
    int PredictedDegreesOfFreedom,
    int MeasuredDegreesOfFreedom)
{
    /// <summary>Caveats: raw mates the formula cannot count, and the standard reading
    /// of a disagreement.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when formula and measurement coincide.</summary>
    public bool Agrees => PredictedDegreesOfFreedom == MeasuredDegreesOfFreedom;

    public override string ToString() =>
        $"mobility: Grübler/Kutzbach predicts {PredictedDegreesOfFreedom} DOF " +
        $"({MovingBodies} moving bodies, {JointCount} joints carrying {JointFreedoms} freedoms" +
        (CouplingCount > 0 ? $", {CouplingCount} couplings" : "") +
        $"); the solver measures {MeasuredDegreesOfFreedom}" +
        (Agrees ? " — they agree" : " — the RANK is the truth (redundant constraints fool the formula)") +
        string.Concat(Notes.Select(n => "\n  · " + n));
}

/// <summary>
/// An <see cref="Assembly"/> with <see cref="Joint"/>s: the same mate system, driven.
/// A fully-constrained assembly is static; a mechanism is a mate system with DOF &gt; 0
/// plus a <see cref="MechanismDriver"/> consuming them — so this class owns no second
/// solver, just a vocabulary (joints), a residual extension (couplings, drivers) and a
/// continuation loop around <see cref="MateSet"/>.
///
/// <code>
/// var mechanism = new Mechanism(linkage)
///     .Ground(frame)
///     .Add(Joint.Revolute(MateGeometry.Axis(frame, (0,0,0), Vector3d.UnitZ),
///                         MateGeometry.Axis(crank, (0,0,0), Vector3d.UnitZ), "crank pin"))
///     .Add(...);
/// mechanism.Assemble();
/// var study = mechanism.Sweep(MechanismDriver.Angle(crankPin), 0, 2 * Math.PI);
/// </code>
///
/// <para><b>Continuation is load-bearing.</b> Every solve seeds from the CURRENT
/// occurrence frames — for a sweep, the previous converged pose, never the originally
/// assembled one — because reseeding from the assembled pose lets the solver change
/// branch mid-sweep (a four-bar flips elbow-up to elbow-down and the motion tears).
/// The solver writes nothing on failure, so a failed step leaves the previous pose
/// intact and the sweep can halve its step and retry from exactly where it stood.</para>
/// </summary>
public sealed partial class Mechanism
{
    private readonly List<Joint> _joints = [];
    private readonly List<AuxiliaryConstraint> _couplings = [];
    private readonly List<Coupling> _pairCouplings = [];

    public Mechanism(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Assembly = assembly;
        Mates = new MateSet(assembly);
    }

    /// <summary>The assembly whose occurrence frames the mechanism poses.</summary>
    public Assembly Assembly { get; }

    /// <summary>The underlying mate set — joints add their mates here, and raw mates
    /// can be mixed in beside them.</summary>
    public MateSet Mates { get; }

    /// <summary>The joints, in the order added.</summary>
    public IReadOnlyList<Joint> Joints => _joints;

    /// <summary>The higher-pair couplings (gears, belts, cams), in the order added.</summary>
    public IReadOnlyList<Coupling> Couplings => _pairCouplings;

    /// <summary>Pins an occurrence — the mechanism's frame/datum (chainable).</summary>
    public Mechanism Ground(Occurrence occurrence)
    {
        Mates.Ground(occurrence);
        return this;
    }

    /// <inheritdoc cref="MateSet.Ground(string)"/>
    public Mechanism Ground(string path)
    {
        Mates.Ground(path);
        return this;
    }

    /// <summary>
    /// Adds a joint: its mates join the mate set, its couplings (a screw's pitch) join
    /// the residual vector, and — unless <paramref name="verifyDegreesOfFreedom"/> is
    /// off — <see cref="Joint.VerifyDegreesOfFreedom"/> asserts the joint's nominal
    /// DOF against the solver's measured rank first, so a wrong definition fails here,
    /// by name, not three sweeps later.
    /// </summary>
    public Mechanism Add(Joint joint, bool verifyDegreesOfFreedom = true)
    {
        ArgumentNullException.ThrowIfNull(joint);
        if (verifyDegreesOfFreedom)
            joint.VerifyDegreesOfFreedom(Assembly);
        foreach (var mate in joint.Mates)
            Mates.Add(mate);
        _couplings.AddRange(joint.Couplings);
        _joints.Add(joint);
        return this;
    }

    /// <summary>Adds a raw mate beside the joints (chainable).</summary>
    public Mechanism Add(Mate mate)
    {
        Mates.Add(mate);
        return this;
    }

    /// <summary>Adds a higher-pair coupling (gear, belt, cam) between joints already
    /// in this mechanism — one more residual row, no new solver.</summary>
    public Mechanism Add(Coupling coupling)
    {
        ArgumentNullException.ThrowIfNull(coupling);
        foreach (var joint in coupling.Joints)
        {
            if (!_joints.Contains(joint))
                throw new ArgumentException(
                    $"Coupling '{coupling.Name}' references joint '{joint.Name}', which has not been " +
                    "added to this mechanism. Add the joint first.", nameof(coupling));
        }
        _couplings.Add(coupling.Constraint);
        _pairCouplings.Add(coupling);
        return this;
    }

    /// <summary>
    /// Solves the undriven mate system — the initial assembly. Throws
    /// <see cref="MateSolveException"/> when it cannot be satisfied (frames untouched);
    /// on success the joints' unwrap states are committed at the assembled pose. A
    /// mechanism is EXPECTED to report remaining DOF here — that is what makes it a
    /// mechanism.
    /// </summary>
    public MateSolveResult Assemble(MateSolverSettings? settings = null)
    {
        var result = TryAssemble(settings);
        if (!result.Converged)
            throw new MateSolveException(result);
        return result;
    }

    /// <summary><see cref="Assemble"/> without the exception.</summary>
    public MateSolveResult TryAssemble(MateSolverSettings? settings = null) =>
        Solved(() => Mates.TrySolve(settings ?? new MateSolverSettings(), _couplings));

    /// <summary>Runs a solve under the joint-limit contract: a converged pose that
    /// drives any joint past a stop is ROLLED BACK (every frame restored) and reported
    /// as a failure naming the joint — the refuse-loudly style; a clean converged pose
    /// commits the joints' unwrap states.</summary>
    private MateSolveResult Solved(Func<MateSolveResult> solve)
    {
        bool anyLimits = _joints.Any(j => j is AxisJoint { AngleLimits: not null } or AxisJoint { SlideLimits: not null });
        var snapshot = anyLimits ? OccurrencePoses.Capture(Assembly) : null;
        var result = solve();
        if (!result.Converged)
            return result;
        if (LimitViolation() is { } violation)
        {
            snapshot!.Restore();
            return new MateSolveResult(
                false, result.Iterations, result.Residual,
                result.FreeDegreesOfFreedom, result.ConstrainedDegreesOfFreedom,
                [violation, "nothing was moved — the occurrence frames are unchanged"])
            { OccurrenceFreedoms = result.OccurrenceFreedoms };
        }
        CommitJointStates();
        return result;
    }

    private string? LimitViolation()
    {
        // The stop tolerance is the weld tier: a pose ON the stop to solver precision
        // is legal, one measurably past it is not.
        double slack = Core.Tolerance.Default.Linear;
        foreach (var joint in _joints)
        {
            if (joint is not AxisJoint axis)
                continue;
            if (axis.AngleLimits is { } angle)
            {
                double value = axis.Angle;
                if (value < angle.Min - slack || value > angle.Max + slack)
                    return $"joint '{axis.Name}' is driven past its stop: angle " +
                           $"{value * 180 / Math.PI:g6}° outside " +
                           $"[{angle.Min * 180 / Math.PI:g6}°, {angle.Max * 180 / Math.PI:g6}°]";
            }
            if (axis.SlideLimits is { } slide)
            {
                double value = axis.Displacement;
                if (value < slide.Min - slack || value > slide.Max + slack)
                    return $"joint '{axis.Name}' is driven past its stop: slide " +
                           $"{value:g6} outside [{slide.Min:g6}, {slide.Max:g6}]";
            }
        }
        return null;
    }


    /// <summary>
    /// Solves with <paramref name="driver"/> pinned at <paramref name="value"/> —
    /// continuation-seeded from the CURRENT frames. Throws on failure (frames left at
    /// the previous pose); on success the joints' unwrap states advance. Sweeping by
    /// repeated calls must step below a half turn per call, or the angle encoding may
    /// pick the nearer branch — <see cref="Sweep"/> handles that for you.
    /// </summary>
    public MateSolveResult SolveAt(MechanismDriver driver, double value, MateSolverSettings? settings = null) =>
        SolveAt([new DriverTarget(driver, value)], settings);

    /// <summary><see cref="SolveAt(MechanismDriver, double, MateSolverSettings?)"/>
    /// without the exception.</summary>
    public MateSolveResult TrySolveAt(MechanismDriver driver, double value, MateSolverSettings? settings = null) =>
        TrySolveAt([new DriverTarget(driver, value)], settings);

    /// <summary>
    /// Solves with SEVERAL drivers pinned at once — a 2-DOF mechanism (a cylindrical
    /// joint driven in both spin and slide, a two-actuator arm) needs a target per
    /// actuated variable, and one driver would leave the pose a family rather than an
    /// answer. Each driver contributes its own constraint rows exactly as the single
    /// driver does, so this is the general form and the one-driver overload is sugar
    /// over it, not a second implementation. Throws on failure (frames unchanged).
    /// </summary>
    public MateSolveResult SolveAt(IReadOnlyList<DriverTarget> targets, MateSolverSettings? settings = null)
    {
        var result = TrySolveAt(targets, settings);
        if (!result.Converged)
            throw new MateSolveException(result);
        return result;
    }

    /// <summary><see cref="SolveAt(IReadOnlyList{DriverTarget}, MateSolverSettings?)"/>
    /// without the exception.</summary>
    public MateSolveResult TrySolveAt(
        IReadOnlyList<DriverTarget> targets, MateSolverSettings? settings = null)
    {
        var extras = DriverExtras(targets, out _);
        return Solved(() => Mates.TrySolve(settings ?? new MateSolverSettings(), extras));
    }

    /// <summary>
    /// Validates a driver set and builds the solver's extra-constraint list: the
    /// couplings, then one row group per driver with its target written in.
    /// <para>The one thing worth refusing here is the SAME joint variable driven twice:
    /// two rows demanding different values of one coordinate make the system
    /// inconsistent, and LM would report a residual rather than the modelling mistake it
    /// is. Two drivers on the same JOINT are fine when they drive different variables (a
    /// cylindrical joint's spin and slide — exactly the 2-DOF case this exists for).</para>
    /// </summary>
    private List<AuxiliaryConstraint> DriverExtras(
        IReadOnlyList<DriverTarget> targets, out List<MechanismDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            throw new ArgumentException("A driven solve needs at least one driver.", nameof(targets));

        drivers = new List<MechanismDriver>(targets.Count);
        var extras = new List<AuxiliaryConstraint>(_couplings.Count + targets.Count);
        extras.AddRange(_couplings);
        foreach (var (driver, value) in targets)
        {
            ArgumentNullException.ThrowIfNull(driver);
            foreach (var already in drivers)
            {
                if (ReferenceEquals(already.Joint, driver.Joint) && already.DrivesAngle == driver.DrivesAngle)
                    throw new ArgumentException(
                        $"Joint '{driver.Joint.Name}' is driven twice in the same solve " +
                        $"({(driver.DrivesAngle ? "angle" : "slide")}). One coordinate cannot hold two " +
                        "targets; drive it once, or drive the joint's other variable.", nameof(targets));
            }
            drivers.Add(driver);
            driver.Constraint.Target = value;
            extras.Add(driver.Constraint);
        }
        return extras;
    }

    /// <summary>
    /// Solves at the driven pose, then computes every moving body's velocity and
    /// acceleration (and, via <see cref="MechanismRates.For(AxisJoint)"/>, joint
    /// coordinate rates) for the driver moving at <paramref name="rate"/> with
    /// <paramref name="acceleration"/> — from the ANALYTIC Jacobian: velocities solve
    /// J·q̇ = −∂C/∂t, accelerations J·q̈ = −r̈₀ with the second-order terms assembled
    /// exactly (finite-differencing sampled poses would cap accuracy near 1e-8, the
    /// mate solver's own lesson). Requires the driven system fully constrained —
    /// otherwise the rates are a family, not an answer, and it refuses saying so.
    /// </summary>
    public MechanismRates RatesAt(
        MechanismDriver driver, double value, double rate = 1, double acceleration = 0,
        MateSolverSettings? settings = null) =>
        RatesAt([new DriverMotion(driver, value, rate, acceleration)], settings);

    /// <summary>
    /// <see cref="RatesAt(MechanismDriver, double, double, double, MateSolverSettings?)"/>
    /// with several drivers moving at once — the rates counterpart of the multi-driver
    /// solve, and the only honest way to ask a 2-DOF mechanism how fast anything is
    /// going (with one actuator pinned and the other free the answer is a family, which
    /// the solver refuses to call an answer).
    /// </summary>
    public MechanismRates RatesAt(
        IReadOnlyList<DriverMotion> motions, MateSolverSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(motions);
        var targets = new DriverTarget[motions.Count];
        for (int i = 0; i < motions.Count; i++)
            targets[i] = new DriverTarget(motions[i].Driver, motions[i].Value);
        SolveAt(targets, settings);

        var extras = DriverExtras(targets, out _);
        foreach (var motion in motions)
        {
            motion.Driver.Constraint.TargetRate = motion.Rate;
            motion.Driver.Constraint.TargetAcceleration = motion.Acceleration;
        }
        try
        {
            return Mates.Rates(settings ?? new MateSolverSettings(), extras);
        }
        finally
        {
            // Rates are a per-query input, not driver state: leaving them set would make
            // the NEXT plain solve carry a velocity nobody asked for.
            foreach (var motion in motions)
            {
                motion.Driver.Constraint.TargetRate = 0;
                motion.Driver.Constraint.TargetAcceleration = 0;
            }
        }
    }

    /// <summary>
    /// Sweeps the driver from <paramref name="from"/> to <paramref name="to"/>,
    /// recording <paramref name="frames"/> uniformly spaced poses. Between samples the
    /// step adapts: a failed step halves and retries from the last converged pose
    /// (continuation), and a step that still fails at 1/4096 of the range stops the
    /// sweep honestly — the study reports the parameter, the assembly stays at the
    /// last good pose, and a rank loss is called out as a singular configuration.
    /// </summary>
    public MotionStudy Sweep(
        MechanismDriver driver, double from, double to, int frames = 61,
        MateSolverSettings? settings = null) =>
        Sweep([new DriverRange(driver, from, to)], frames, settings);

    /// <summary>
    /// Sweeps SEVERAL drivers together: every driver runs its own From→To over the same
    /// normalized position s ∈ [0, 1], so the study traces a straight line through
    /// driver space — a coordinated motion (both actuators of a 2-DOF arm moving to a
    /// pose), not a grid of every combination. That choice matters for continuation:
    /// one parameter means one step to halve, so the retry logic, the dead-centre probe
    /// and the "leave the assembly at the last good pose" contract are the single-driver
    /// ones unchanged rather than a second scheme.
    /// <para>Each frame records every driver's value in <see cref="MotionFrame.Values"/>,
    /// with <see cref="MotionFrame.Value"/> (and <see cref="MotionStudy.FailedAt"/>)
    /// staying the FIRST driver's, so existing consumers read the same numbers.</para>
    /// </summary>
    public MotionStudy Sweep(
        IReadOnlyList<DriverRange> ranges, int frames = 61, MateSolverSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count == 0)
            throw new ArgumentException("A sweep needs at least one driver range.", nameof(ranges));
        if (frames < 2)
            throw new ArgumentOutOfRangeException(nameof(frames), "A sweep needs at least two frames.");
        // Exact-zero semantic test: a range that moves nothing at all is a request error.
        if (ranges.All(r => r.From == r.To))
            throw new ArgumentException("A sweep needs a non-empty driver range.", nameof(ranges));

        // Validate the driver set once, up front — a duplicate coordinate must fail
        // before the first frame is recorded, not halfway through a study.
        DriverExtras([.. ranges.Select(r => r.At(0))], out var drivers);

        var recorded = new List<MotionFrame>(frames);
        var diagnostics = new List<string>();
        double nominalStep = 1.0 / (frames - 1);
        const double minStep = 1.0 / 4096;
        // FailedAt and MotionFrame.Value stay the FIRST driver's coordinate, so a
        // single-driver sweep reports exactly what it always did.
        double Report(double s) => ranges[0].From + (ranges[0].To - ranges[0].From) * s;

        var first = TrySolveAt([.. ranges.Select(r => r.At(0))], settings);
        if (!first.Converged)
        {
            diagnostics.Add($"the sweep could not start: driving to {Report(0):g6} does not solve");
            diagnostics.AddRange(first.Diagnostics);
            return new MotionStudy(this, drivers, recorded, completed: false, failedAt: null,
                singular: false, diagnostics);
        }
        recorded.Add(Snapshot(ranges, 0));
        int baselineRank = first.ConstrainedDegreesOfFreedom;
        // The dead-centre detector's yardstick: the driven system's WIDE-threshold
        // rank at a healthy pose. Compared against the same probe at a stall, so a
        // direction that is merely weak throughout the sweep never trips it.
        var looseBaseline = RankProbe(ranges, 0, settings);

        double current = 0;
        double stepLimit = nominalStep;
        for (int i = 1; i < frames; i++)
        {
            double target = i / (double)(frames - 1);
            while (Math.Abs(target - current) > 0)
            {
                double remaining = target - current;
                double attempt = Math.Abs(remaining) <= stepLimit
                    ? target
                    : current + Math.Sign(remaining) * stepLimit;
                var result = TrySolveAt([.. ranges.Select(r => r.At(attempt))], settings);
                if (result.Converged)
                {
                    if (result.ConstrainedDegreesOfFreedom < baselineRank)
                    {
                        Singularity(Report(attempt), result, baselineRank, first, diagnostics);
                        return new MotionStudy(this, drivers, recorded, completed: false,
                            failedAt: Report(attempt), singular: true, diagnostics);
                    }
                    current = attempt;
                    stepLimit = Math.Min(stepLimit * 2, nominalStep);
                }
                else
                {
                    stepLimit /= 2;
                    if (stepLimit < minStep)
                    {
                        bool singular = Failure(
                            ranges, drivers, current, Report(current), Report(target), result,
                            looseBaseline, settings, diagnostics);
                        return new MotionStudy(this, drivers, recorded, completed: false,
                            failedAt: Report(current), singular, diagnostics);
                    }
                }
            }
            recorded.Add(Snapshot(ranges, target));
        }
        return new MotionStudy(this, drivers, recorded, completed: true, failedAt: null,
            singular: false, diagnostics);
    }

    private MotionFrame Snapshot(IReadOnlyList<DriverRange> ranges, double s)
    {
        var values = new double[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
            values[i] = ranges[i].At(s).Value;
        return new MotionFrame(values[0], Assembly.Flatten()) { Values = values };
    }

    /// <summary>A converged step whose Jacobian rank DROPPED below the sweep's
    /// baseline: the mechanism is at (or crossing) a singular configuration. Name the
    /// joints whose bodies lost constrained directions and refuse to continue —
    /// beyond a toggle point the branch is a modeling decision, not a solver's guess.</summary>
    private void Singularity(
        double at, MateSolveResult result, int baselineRank,
        MateSolveResult baseline, List<string> diagnostics)
    {
        diagnostics.Add(
            $"singular configuration at driver value {at:g6}: the constraint Jacobian lost rank " +
            $"({result.ConstrainedDegreesOfFreedom} of a baseline {baselineRank}) — a dead centre or " +
            "toggle point, where the mechanism can branch or lock. Refusing to guess a branch; drive " +
            "through this region from the other end, or add a joint that decides the branch.");
        foreach (string name in SuspectJoints(result, baseline))
            diagnostics.Add($"joint '{name}' spans the rank loss");
    }

    /// <summary>
    /// The wide-threshold rank tolerance behind the dead-centre DIAGNOSIS: a driven
    /// direction whose singular value has fallen below this fraction of the spectrum
    /// reads as lost. Deliberately far above the solver's strict 1e-8 rank threshold —
    /// a sweep stalls NEAR a dead centre (the minimum step keeps it a finite distance
    /// away), where the Jacobian is almost, not exactly, deficient. The diagnosis only
    /// names WHY a sweep already stopped; the hard stop itself never depends on this
    /// number.
    /// </summary>
    private const double DeadCentreRankTolerance = 0.03;

    /// <summary>The wide-threshold rank of the DRIVEN system at the current
    /// (converged) pose — zero iterations, nothing moves, nothing commits.</summary>
    private MateSolveResult RankProbe(
        IReadOnlyList<DriverRange> ranges, double s, MateSolverSettings? settings)
    {
        var extras = DriverExtras([.. ranges.Select(r => r.At(s))], out _);
        var probe = new MateSolverSettings
        {
            MaxIterations = 0,
            Tolerance = (settings ?? new MateSolverSettings()).Tolerance,
        };
        return Mates.TrySolve(probe, extras, DeadCentreRankTolerance);
    }

    /// <summary>A step that kept failing down to the minimum subdivision. Distinguish
    /// a dead centre — the Jacobian near-singular along the driven direction at the
    /// last good pose, or the solver's own stationary-configuration diagnosis — from a
    /// target genuinely outside the linkage's reach. Returns whether the stop was
    /// singular.</summary>
    private bool Failure(
        IReadOnlyList<DriverRange> ranges, IReadOnlyList<MechanismDriver> drivers,
        double s, double current, double target, MateSolveResult result,
        MateSolveResult looseBaseline, MateSolverSettings? settings, List<string> diagnostics)
    {
        // A joint stop is its own story: the sweep walked up to the stop (step
        // halving got it within 1/4096 of the range) and the next nudge violates a
        // limit — no singularity probe needed, the joint is simply out of travel.
        if (result.Diagnostics.FirstOrDefault(d => d.Contains("past its stop", StringComparison.Ordinal))
            is { } stop)
        {
            diagnostics.Add(
                $"the sweep stopped at driver value {current:g6}: {stop}");
            diagnostics.Add("the assembly is left at the last good pose, on the stop");
            return false;
        }

        bool stationary = result.Diagnostics.Any(d => d.Contains("stationary", StringComparison.Ordinal));
        var atStall = RankProbe(ranges, s, settings);
        bool nearSingular =
            atStall.ConstrainedDegreesOfFreedom < looseBaseline.ConstrainedDegreesOfFreedom;
        bool singular = stationary || nearSingular;
        string driven = string.Join("', '", drivers.Select(d => d.Joint.Name).Distinct());

        diagnostics.Add(
            $"the sweep stopped at driver value {current:g6}: stepping toward {target:g6} stopped " +
            "converging even at 1/4096 of the range" +
            (singular
                ? $" — a dead centre for joint '{driven}': the driven variable is " +
                  "first-order stationary along the mechanism's remaining motion, so the linkage can " +
                  "toggle or lock here. Refusing to guess a branch; drive a different joint through " +
                  "this region, or approach from the other end."
                : " — the target is outside what the linkage can reach from here"));
        if (nearSingular)
        {
            diagnostics.Add(
                $"the constraint Jacobian at the last good pose is within {DeadCentreRankTolerance:p0} of " +
                $"rank-deficient ({atStall.ConstrainedDegreesOfFreedom} of the healthy " +
                $"{looseBaseline.ConstrainedDegreesOfFreedom})");
            foreach (string name in SuspectJoints(atStall, looseBaseline))
                diagnostics.Add($"joint '{name}' spans the rank loss");
        }
        diagnostics.AddRange(result.Diagnostics);
        diagnostics.Add("the assembly is left at the last good pose");
        return singular;
    }

    /// <summary>The joints touching an occurrence whose per-body constrained DOF fell
    /// below the sweep baseline's — the bodies that went slack or locked.</summary>
    private IEnumerable<string> SuspectJoints(MateSolveResult result, MateSolveResult baseline)
    {
        var baselineByPath = baseline.OccurrenceFreedoms.ToDictionary(f => f.Path, f => f.ConstrainedDegreesOfFreedom);
        var deficient = new HashSet<string>();
        foreach (var freedom in result.OccurrenceFreedoms)
        {
            if (baselineByPath.TryGetValue(freedom.Path, out int was) &&
                freedom.ConstrainedDegreesOfFreedom < was)
                deficient.Add(freedom.Path);
        }
        if (deficient.Count == 0)
            yield break;
        foreach (var joint in _joints)
        {
            if ((joint.A.Path is { } a && deficient.Contains(a)) ||
                (joint.B.Path is { } b && deficient.Contains(b)))
                yield return joint.Name;
        }
    }

    /// <summary>
    /// The Grübler/Kutzbach mobility count beside the solver's measured rank at the
    /// CURRENT pose (call after <see cref="Assemble"/> — rank is a property of a
    /// configuration). Nothing is moved. Disagreement is reported, not thrown — see
    /// <see cref="MobilityReport"/> for why the rank is the truthful side.
    /// </summary>
    public MobilityReport Mobility(MateSolverSettings? settings = null)
    {
        var probe = Mates.TrySolve(new MateSolverSettings
        {
            MaxIterations = 0,
            Tolerance = (settings ?? new MateSolverSettings()).Tolerance,
        }, _couplings);

        int moving = probe.FreeDegreesOfFreedom / 6;
        int jointFreedoms = _joints.Sum(j => j.NominalDegreesOfFreedom);
        // Kutzbach: each joint removes (6 − f); each higher-pair coupling removes 1.
        // A screw's pitch coupling is already inside its nominal f = 1, so only the
        // explicit pair couplings count here.
        int predicted = 6 * moving
            - _joints.Sum(j => 6 - j.NominalDegreesOfFreedom)
            - _pairCouplings.Count;

        var notes = new List<string>();
        int rawMates = Mates.Mates.Count - _joints.Sum(j => j.Mates.Count);
        if (rawMates > 0)
            notes.Add($"{rawMates} raw mate{(rawMates == 1 ? "" : "s")} outside the joints are invisible " +
                      "to the formula (it counts joints, not constraint rows)");
        if (predicted != probe.RemainingDegreesOfFreedom)
            notes.Add("overconstrained-but-mobile linkages (a planar linkage built in space, Bennett, " +
                      "Sarrus) are exactly where the formula lies and the measured rank is right");

        return new MobilityReport(
            moving, _joints.Count, jointFreedoms, _pairCouplings.Count,
            predicted, probe.RemainingDegreesOfFreedom)
        { Notes = notes };
    }

    private void CommitJointStates()
    {
        foreach (var joint in _joints)
        {
            if (joint is AxisJoint axis)
                axis.CommitState();
        }
    }
}
