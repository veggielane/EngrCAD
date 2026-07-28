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

/// <summary>One sampled pose of a driven mechanism: the driver value and the flattened
/// part instances at that pose — pure poses, no geometry, which is what lets a viewer
/// animate a study with matrices only.</summary>
public sealed record MotionFrame(double Value, IReadOnlyList<PartInstance> Instances);

/// <summary>
/// The result of sweeping a driver across a range: poses per sampled frame, and an
/// honest account of how far the sweep got. A sweep that fails reports the last good
/// parameter and leaves the assembly AT that last good pose (a failed step writes
/// nothing — the solver's own contract); a sweep that hits a singular configuration
/// says so and names what lost rank rather than guessing a branch.
/// </summary>
public sealed class MotionStudy
{
    internal MotionStudy(
        Mechanism mechanism, MechanismDriver driver, IReadOnlyList<MotionFrame> frames,
        bool completed, double? failedAt, bool singular, IReadOnlyList<string> diagnostics)
    {
        Mechanism = mechanism;
        Driver = driver;
        Frames = frames;
        Completed = completed;
        FailedAt = failedAt;
        Singular = singular;
        Diagnostics = diagnostics;
    }

    /// <summary>The mechanism the study swept.</summary>
    public Mechanism Mechanism { get; }

    /// <summary>The driver that was swept.</summary>
    public MechanismDriver Driver { get; }

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
public sealed class Mechanism
{
    private readonly List<Joint> _joints = [];
    private readonly List<AuxiliaryConstraint> _couplings = [];

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
    public MateSolveResult TryAssemble(MateSolverSettings? settings = null)
    {
        var result = Mates.TrySolve(settings ?? new MateSolverSettings(), _couplings);
        if (result.Converged)
            CommitJointStates();
        return result;
    }

    /// <summary>
    /// Solves with <paramref name="driver"/> pinned at <paramref name="value"/> —
    /// continuation-seeded from the CURRENT frames. Throws on failure (frames left at
    /// the previous pose); on success the joints' unwrap states advance. Sweeping by
    /// repeated calls must step below a half turn per call, or the angle encoding may
    /// pick the nearer branch — <see cref="Sweep"/> handles that for you.
    /// </summary>
    public MateSolveResult SolveAt(MechanismDriver driver, double value, MateSolverSettings? settings = null)
    {
        var result = TrySolveAt(driver, value, settings);
        if (!result.Converged)
            throw new MateSolveException(result);
        return result;
    }

    /// <summary><see cref="SolveAt"/> without the exception.</summary>
    public MateSolveResult TrySolveAt(MechanismDriver driver, double value, MateSolverSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        driver.Constraint.Target = value;
        var extras = new List<AuxiliaryConstraint>(_couplings.Count + 1);
        extras.AddRange(_couplings);
        extras.Add(driver.Constraint);
        var result = Mates.TrySolve(settings ?? new MateSolverSettings(), extras);
        if (result.Converged)
            CommitJointStates();
        return result;
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
        MechanismDriver driver, double from, double to, int frames = 61, MateSolverSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (frames < 2)
            throw new ArgumentOutOfRangeException(nameof(frames), "A sweep needs at least two frames.");
        if (from == to)   // exact-zero semantic test: an empty range is a request error
            throw new ArgumentException("A sweep needs a non-empty driver range.", nameof(to));

        var recorded = new List<MotionFrame>(frames);
        var diagnostics = new List<string>();
        double span = to - from;
        double nominalStep = span / (frames - 1);
        double minStep = Math.Abs(span) / 4096;

        var first = TrySolveAt(driver, from, settings);
        if (!first.Converged)
        {
            diagnostics.Add($"the sweep could not start: driving to {from:g6} does not solve");
            diagnostics.AddRange(first.Diagnostics);
            return new MotionStudy(this, driver, recorded, completed: false, failedAt: null,
                singular: false, diagnostics);
        }
        recorded.Add(Snapshot(from));
        int baselineRank = first.ConstrainedDegreesOfFreedom;
        // The dead-centre detector's yardstick: the driven system's WIDE-threshold
        // rank at a healthy pose. Compared against the same probe at a stall, so a
        // direction that is merely weak throughout the sweep never trips it.
        var looseBaseline = RankProbe(driver, from, settings);

        double current = from;
        double stepLimit = Math.Abs(nominalStep);
        for (int i = 1; i < frames; i++)
        {
            double target = from + span * i / (frames - 1);
            while (Math.Abs(target - current) > 0)
            {
                double remaining = target - current;
                double attempt = Math.Abs(remaining) <= stepLimit
                    ? target
                    : current + Math.Sign(remaining) * stepLimit;
                var result = TrySolveAt(driver, attempt, settings);
                if (result.Converged)
                {
                    if (result.ConstrainedDegreesOfFreedom < baselineRank)
                    {
                        Singularity(driver, attempt, result, baselineRank, first, diagnostics);
                        return new MotionStudy(this, driver, recorded, completed: false,
                            failedAt: attempt, singular: true, diagnostics);
                    }
                    current = attempt;
                    stepLimit = Math.Min(stepLimit * 2, Math.Abs(nominalStep));
                }
                else
                {
                    stepLimit /= 2;
                    if (stepLimit < minStep)
                    {
                        bool singular = Failure(
                            driver, current, target, result, looseBaseline, settings, diagnostics);
                        return new MotionStudy(this, driver, recorded, completed: false,
                            failedAt: current, singular, diagnostics);
                    }
                }
            }
            recorded.Add(Snapshot(target));
        }
        return new MotionStudy(this, driver, recorded, completed: true, failedAt: null,
            singular: false, diagnostics);
    }

    private MotionFrame Snapshot(double value) => new(value, Assembly.Flatten());

    /// <summary>A converged step whose Jacobian rank DROPPED below the sweep's
    /// baseline: the mechanism is at (or crossing) a singular configuration. Name the
    /// joints whose bodies lost constrained directions and refuse to continue —
    /// beyond a toggle point the branch is a modeling decision, not a solver's guess.</summary>
    private void Singularity(
        MechanismDriver driver, double at, MateSolveResult result, int baselineRank,
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
    private MateSolveResult RankProbe(MechanismDriver driver, double at, MateSolverSettings? settings)
    {
        driver.Constraint.Target = at;
        var extras = new List<AuxiliaryConstraint>(_couplings.Count + 1);
        extras.AddRange(_couplings);
        extras.Add(driver.Constraint);
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
        MechanismDriver driver, double current, double target, MateSolveResult result,
        MateSolveResult looseBaseline, MateSolverSettings? settings, List<string> diagnostics)
    {
        bool stationary = result.Diagnostics.Any(d => d.Contains("stationary", StringComparison.Ordinal));
        var atStall = RankProbe(driver, current, settings);
        bool nearSingular =
            atStall.ConstrainedDegreesOfFreedom < looseBaseline.ConstrainedDegreesOfFreedom;
        bool singular = stationary || nearSingular;

        diagnostics.Add(
            $"the sweep stopped at driver value {current:g6}: stepping toward {target:g6} stopped " +
            "converging even at 1/4096 of the range" +
            (singular
                ? $" — a dead centre for joint '{driver.Joint.Name}': the driven variable is " +
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

    private void CommitJointStates()
    {
        foreach (var joint in _joints)
        {
            if (joint is AxisJoint axis)
                axis.CommitState();
        }
    }
}
