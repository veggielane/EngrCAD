using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>Composes a <see cref="MateRef"/>'s occurrence chain into world space —
/// the same outermost-first composition the solver uses, read from the CURRENT
/// occurrence frames (a world-fixed reference composes through the identity).</summary>
internal static class MateRefWorld
{
    public static Frame3d WorldFrame(in MateRef reference)
    {
        if (reference.Occurrence is not { } occurrence)
            return Frame3d.WorldXY;
        if (reference.Ancestors is not { } ancestors)
            return occurrence.Frame;
        var world = ancestors[0].Frame;
        for (int i = 1; i < ancestors.Count; i++)
            world = ancestors[i].Frame.Then(world);
        return occurrence.Frame.Then(world);
    }

    public static EndValue Evaluate(in MateRef reference)
    {
        var frame = WorldFrame(reference);
        return new EndValue(frame.ToWorld(reference.Point), frame.ToWorldVector(reference.Direction));
    }
}

/// <summary>
/// A kinematic joint: a NAMED combination of ordinary mates with a known nominal
/// degree-of-freedom count. Joints are the user's language for mechanisms — a hinge is
/// a <see cref="RevoluteJoint"/>, not "a concentric plus a coincident" — while mates
/// stay the implementation, so a joint survives regeneration exactly as its mates do
/// and the <see cref="MateSet"/> solver needs no new machinery.
/// <para>Both ends are the same <see cref="MateRef"/>s mates use, so they can come
/// from explicit coordinates, semantic <c>BrepQueries</c> selectors, typed
/// <see cref="FaceRef"/>/<see cref="AxisRef"/> queries, or occurrence paths across
/// assembly levels (<see cref="MateGeometry"/>'s builders cover all four).</para>
/// <para><b>The nominal DOF is asserted, not assumed</b>:
/// <see cref="VerifyDegreesOfFreedom"/> solves the joint's mates in isolation and
/// compares the solver's measured rank against the nominal count — a wrong joint
/// definition fails immediately and by name rather than corrupting a mechanism's DOF
/// budget. <see cref="Mechanism.Add(Joint)"/> runs it automatically.</para>
/// </summary>
public abstract class Joint
{
    private protected Joint(string name, int nominalDegreesOfFreedom, in MateRef a, in MateRef b, IReadOnlyList<Mate> mates)
    {
        if (a.IsWorld && b.IsWorld)
            throw new ArgumentException("A joint needs at least one movable end — both ends are world-fixed.");
        if (a.Occurrence is not null && ReferenceEquals(a.Occurrence, b.Occurrence))
            throw new ArgumentException(
                $"A joint connects two different bodies, but both ends of '{name}' target occurrence " +
                $"'{a.Occurrence.Name}'.");
        Name = name;
        NominalDegreesOfFreedom = nominalDegreesOfFreedom;
        A = a;
        B = b;
        Mates = mates;
    }

    /// <summary>Label for diagnostics (defaults to the joint kind).</summary>
    public string Name { get; }

    /// <summary>The relative degrees of freedom this joint leaves between its two
    /// bodies: 1 for revolute/prismatic/screw, 2 cylindrical, 3 spherical/planar,
    /// 0 fixed.</summary>
    public int NominalDegreesOfFreedom { get; }

    /// <summary>The first end (the "base" side: joint coordinates are measured on A's
    /// axis, and drivers move B relative to A).</summary>
    public MateRef A { get; }

    /// <summary>The second end (the moving side, by convention).</summary>
    public MateRef B { get; }

    /// <summary>The ordinary mates this joint is made of — the implementation the
    /// solver actually sees.</summary>
    public IReadOnlyList<Mate> Mates { get; }

    /// <summary>Auxiliary residuals beyond the geometric mates (a screw's pitch
    /// coupling). Empty for the lower pairs.</summary>
    internal virtual IReadOnlyList<AuxiliaryConstraint> Couplings => [];

    /// <summary>
    /// Solves this joint's mates in isolation — A's body grounded, B's free — and
    /// asserts the solver's measured remaining DOF equals
    /// <see cref="NominalDegreesOfFreedom"/>. Occurrence frames are restored
    /// afterwards, so the check is side-effect-free; it throws (naming the joint,
    /// the nominal count and the measured one) when the definition is wrong, and when
    /// the joint cannot be assembled from the current poses at all. Returns the probe
    /// report for inspection.
    /// </summary>
    public MateSolveResult VerifyDegreesOfFreedom(Assembly assembly, MateSolverSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var probe = new MateSet(assembly);
        foreach (var mate in Mates)
            probe.Add(mate);

        // Ground the A side (when it is a body); the B side stays the single free
        // occurrence whose remaining DOF is the joint's own. When B is world-fixed the
        // roles swap and A is the free side.
        var grounded = B.IsWorld ? B : A;
        var free = B.IsWorld ? A : B;
        if (grounded.Path is { } groundPath)
            probe.Ground(groundPath);

        var freeOccurrence = free.Occurrence!;
        var snapshot = freeOccurrence.Frame;
        MateSolveResult result;
        try
        {
            result = probe.TrySolve(settings ?? new MateSolverSettings(), Couplings);
        }
        finally
        {
            freeOccurrence.Frame = snapshot;
        }

        if (!result.Converged)
            throw new InvalidOperationException(
                $"Joint '{Name}' could not be assembled from the current poses, so its degrees of freedom " +
                $"cannot be verified.\n{result}");
        if (result.RemainingDegreesOfFreedom != NominalDegreesOfFreedom)
            throw new InvalidOperationException(
                $"Joint '{Name}' claims {NominalDegreesOfFreedom} degree{Plural(NominalDegreesOfFreedom)} of " +
                $"freedom, but the solver measures {result.RemainingDegreesOfFreedom} at the assembled pose — " +
                "the joint definition and its mates disagree.");
        return result;
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    /// <summary>Persistence's spelling of <see cref="WithDirection"/>: rebuilds a
    /// reference with a saved perpendicular-reference direction, keeping its occurrence
    /// chain and point. The <see cref="MateRef"/> constructor keeps an already-unit
    /// direction verbatim, so a saved direction round-trips bit-for-bit.</summary>
    internal static MateRef Redirect(in MateRef reference, in Vector3d direction) =>
        WithDirection(reference, direction);

    /// <summary>Rebuilds a reference with a different local direction, keeping its
    /// occurrence chain and point (used for derived perpendicular references, which
    /// are geometric bookkeeping rather than semantic queries).</summary>
    private protected static MateRef WithDirection(in MateRef reference, in Vector3d direction)
    {
        if (reference.Occurrence is null)
            return new MateRef((Occurrence?)null, reference.Point, direction);
        if (reference.Ancestors is not { } ancestors)
            return new MateRef(reference.Occurrence, reference.Point, direction);
        return new MateRef([.. ancestors, reference.Occurrence], reference.Point, direction);
    }

    // ---- the vocabulary ----

    /// <summary>A hinge: axes collinear, axis origins coincident — 1 DOF (spin about
    /// the shared axis). Angle 0 is the construction pose.</summary>
    public static RevoluteJoint Revolute(in MateRef axisA, in MateRef axisB, string? name = null) =>
        new(axisA, axisB, name ?? "Revolute");

    /// <summary>A slider: axes collinear, spin locked at the construction angle —
    /// 1 DOF (slide along the axis). Displacement 0 is the construction pose.</summary>
    public static PrismaticJoint Prismatic(in MateRef axisA, in MateRef axisB, string? name = null) =>
        new(axisA, axisB, name ?? "Prismatic");

    /// <summary>A pin in a bore: axes collinear — 2 DOF (spin and slide).</summary>
    public static CylindricalJoint Cylindrical(in MateRef axisA, in MateRef axisB, string? name = null) =>
        new(axisA, axisB, name ?? "Cylindrical");

    /// <summary>A ball joint: the two reference points coincide — 3 DOF (all
    /// rotations). Directions on the ends are ignored.</summary>
    public static SphericalJoint Spherical(in MateRef a, in MateRef b, string? name = null) =>
        new(a, b, name ?? "Spherical");

    /// <summary>A planar joint: the faces bear against each other (normals oppose,
    /// <paramref name="gap"/> apart) — 3 DOF (two slides and a spin in the plane).</summary>
    public static PlanarJoint Planar(in MateRef faceA, in MateRef faceB, double gap = 0, string? name = null) =>
        new(faceA, faceB, gap, name ?? "Planar");

    /// <summary>A screw pair: axes collinear with rotation and translation COUPLED by
    /// the pitch — 1 DOF. A positive pitch advances B along A's axis by
    /// <paramref name="pitch"/> per right-handed turn; a negative pitch is a left-hand
    /// thread.</summary>
    public static ScrewJoint Screw(in MateRef axisA, in MateRef axisB, double pitch, string? name = null) =>
        new(axisA, axisB, pitch, name ?? "Screw");

    /// <summary>A rigid connection — 0 DOF. The B body keeps exactly its construction
    /// pose relative to A.</summary>
    public static FixedJoint Fixed(in MateRef axisA, in MateRef axisB, string? name = null) =>
        new(axisA, axisB, name ?? "Fixed");
}

/// <summary>
/// A joint with a defined axis (A's direction), which is what gives it measurable
/// joint COORDINATES: the spin <see cref="Angle"/> (right-handed about A's axis, 0 at
/// construction, unwrapped so it keeps counting through full turns) and the slide
/// <see cref="Displacement"/> (along A's axis, 0 at construction). Both are measured
/// against perpendicular reference directions derived once, at construction, from the
/// ends' current world poses — geometric bookkeeping the solver carries like any other
/// direction, not a new constraint type.
/// </summary>
public abstract class AxisJoint : Joint
{
    private protected AxisJoint(
        string name, int nominalDegreesOfFreedom, in MateRef a, in MateRef b,
        IReadOnlyList<Mate> mates, in (MateRef RefA, MateRef RefB) references)
        : base(name, nominalDegreesOfFreedom, a, b, mates)
    {
        ReferenceA = references.RefA;
        ReferenceB = references.RefB;
        State = new JointSweepState();
        var (axisA, axisB, refA, refB) = EvaluateNow();
        // The construction pose DEFINES zero for both coordinates, even when the
        // reference derivation had to fall back (parallel-degenerate axes): rebase to
        // whatever the references measure right now.
        State.Rebase(JointArithmetic.Angle(axisA, refA, refB));
        State.ReferenceSlide = JointArithmetic.Slide(axisA, axisB);
    }

    /// <summary>The A-side perpendicular reference (θ's zero mark).</summary>
    internal MateRef ReferenceA { get; }

    /// <summary>The B-side perpendicular reference, aligned with A's at construction.</summary>
    internal MateRef ReferenceB { get; }

    /// <summary>Unwrap/zero bookkeeping for swept motion.</summary>
    internal JointSweepState State { get; }

    /// <summary>Hard stops on the spin coordinate (radians, measured from the
    /// construction zero), or null for a full-travel joint. Enforced by
    /// <see cref="Mechanism"/> after every converged solve.</summary>
    public (double Min, double Max)? AngleLimits { get; internal set; }

    /// <summary>Hard stops on the slide coordinate (model units from the construction
    /// zero), or null for a full-travel joint.</summary>
    public (double Min, double Max)? SlideLimits { get; internal set; }

    internal static (double Min, double Max) ValidatedLimits(double min, double max, string what)
    {
        if (!(min < max))
            throw new ArgumentException($"A joint's {what} limits need min < max (got [{min:g4}, {max:g4}]).");
        return (min, max);
    }

    /// <summary>The spin about the joint axis, in radians — 0 at construction,
    /// unwrapped (a crank swept twice reads 4π). Read from the CURRENT occurrence
    /// frames.</summary>
    public double Angle
    {
        get
        {
            var (axisA, _, refA, refB) = EvaluateNow();
            return State.Unwrapped(JointArithmetic.Angle(axisA, refA, refB));
        }
    }

    /// <summary><see cref="Angle"/> in degrees.</summary>
    public double AngleDegrees => Angle * 180 / Math.PI;

    /// <summary>The slide along the joint axis, in model units — 0 at construction.
    /// Read from the CURRENT occurrence frames.</summary>
    public double Displacement
    {
        get
        {
            var (axisA, axisB, _, _) = EvaluateNow();
            return JointArithmetic.Slide(axisA, axisB) - State.ReferenceSlide;
        }
    }

    /// <summary>Re-zeroes both coordinates at the current pose — call after an initial
    /// <see cref="Mechanism.Assemble"/> to measure motion from the assembled state
    /// rather than from wherever the bodies were authored.</summary>
    public void Rebase()
    {
        var (axisA, axisB, refA, refB) = EvaluateNow();
        State.Rebase(JointArithmetic.Angle(axisA, refA, refB));
        State.ReferenceSlide = JointArithmetic.Slide(axisA, axisB);
    }

    /// <summary>Folds the current pose into the unwrap state — called by
    /// <see cref="Mechanism"/> after every CONVERGED solve, so sweeps stay within a
    /// half turn of the last commit and the accumulated angle never tears.</summary>
    internal void CommitState()
    {
        var (axisA, _, refA, refB) = EvaluateNow();
        State.Commit(JointArithmetic.Angle(axisA, refA, refB));
    }

    /// <summary>Restores a saved sweep state verbatim (mechanism persistence): the
    /// unwrapped-angle history and the construction-slide reference, which together
    /// are what make the joint's coordinates mean what they meant when saved.</summary>
    internal void RestoreState(double accumulatedAngle, double lastMeasuredAngle, double referenceSlide)
    {
        State.Restore(accumulatedAngle, lastMeasuredAngle);
        State.ReferenceSlide = referenceSlide;
    }

    internal (EndValue AxisA, EndValue AxisB, EndValue RefA, EndValue RefB) EvaluateNow() => (
        MateRefWorld.Evaluate(A),
        MateRefWorld.Evaluate(B),
        MateRefWorld.Evaluate(ReferenceA),
        MateRefWorld.Evaluate(ReferenceB));

    /// <summary>
    /// Derives the perpendicular reference pair: an arbitrary perpendicular of A's
    /// axis, carried into world space and projected onto B's axis plane so the two
    /// references ALIGN at construction (θ starts at 0). When the projection
    /// degenerates (the reference lands along B's axis — axes wildly misaligned at
    /// construction) B falls back to its own arbitrary perpendicular and the state
    /// rebase in the constructor still defines construction as zero.
    /// </summary>
    private protected static (MateRef RefA, MateRef RefB) DeriveReferences(in MateRef a, in MateRef b)
    {
        if (a.Direction == Vector3d.Zero || b.Direction == Vector3d.Zero)
            throw new ArgumentException(
                "An axis joint needs a direction on both ends (use MateGeometry.Axis or a cylindrical " +
                "face selector).");
        var frameA = MateRefWorld.WorldFrame(a);
        var frameB = MateRefWorld.WorldFrame(b);
        var referenceALocal = a.Direction.ArbitraryPerpendicular(Tolerance.Default);
        var referenceAWorld = frameA.ToWorldVector(referenceALocal);
        var axisBWorld = frameB.ToWorldVector(b.Direction);
        var projected = referenceAWorld - axisBWorld * referenceAWorld.Dot(axisBWorld);
        if (!projected.TryNormalize(Tolerance.Default, out var referenceBWorld))
            referenceBWorld = axisBWorld.ArbitraryPerpendicular(Tolerance.Default);
        return (
            WithDirection(a, referenceALocal),
            WithDirection(b, frameB.ToLocalVector(referenceBWorld)));
    }
}

/// <summary>A hinge — see <see cref="Joint.Revolute"/>.</summary>
public sealed class RevoluteJoint : AxisJoint
{
    internal RevoluteJoint(in MateRef a, in MateRef b, string name)
        : this(a, b, name, DeriveReferences(a, b))
    {
    }

    /// <summary>The restore path: mechanism persistence supplies the SAVED perpendicular
    /// references instead of re-deriving them, so the angle's zero keeps its meaning.</summary>
    internal RevoluteJoint(in MateRef a, in MateRef b, string name, in (MateRef RefA, MateRef RefB) references)
        : base(name, 1, a, b,
            [
                Mate.Concentric(a, b, $"{name} axis"),
                Mate.Coincident(a, b, $"{name} origin"),
            ],
            references)
    {
    }

    /// <summary>Hard stops on the hinge angle, in degrees from the construction zero
    /// (chainable). A solve or sweep that drives past a stop refuses, naming this
    /// joint.</summary>
    public RevoluteJoint WithLimits(double minDegrees, double maxDegrees)
    {
        AngleLimits = ValidatedLimits(
            minDegrees * Math.PI / 180, maxDegrees * Math.PI / 180, "angle");
        return this;
    }
}

/// <summary>A slider — see <see cref="Joint.Prismatic"/>.</summary>
public sealed class PrismaticJoint : AxisJoint
{
    internal PrismaticJoint(in MateRef a, in MateRef b, string name)
        : this(a, b, name, DeriveReferences(a, b))
    {
    }

    internal PrismaticJoint(in MateRef a, in MateRef b, string name, in (MateRef RefA, MateRef RefB) references)
        : base(name, 1, a, b,
            [
                Mate.Concentric(a, b, $"{name} axis"),
                Mate.Parallel(references.RefA, references.RefB, $"{name} spin lock"),
            ],
            references)
    {
    }

    /// <summary>Hard stops on the slide, in model units from the construction zero
    /// (chainable). A solve or sweep that drives past a stop refuses, naming this
    /// joint.</summary>
    public PrismaticJoint WithLimits(double min, double max)
    {
        SlideLimits = ValidatedLimits(min, max, "slide");
        return this;
    }
}

/// <summary>A pin in a bore — see <see cref="Joint.Cylindrical"/>.</summary>
public sealed class CylindricalJoint : AxisJoint
{
    internal CylindricalJoint(in MateRef a, in MateRef b, string name)
        : this(a, b, name, DeriveReferences(a, b))
    {
    }

    internal CylindricalJoint(in MateRef a, in MateRef b, string name, in (MateRef RefA, MateRef RefB) references)
        : base(name, 2, a, b,
            [Mate.Concentric(a, b, $"{name} axis")],
            references)
    {
    }
}

/// <summary>A screw pair — see <see cref="Joint.Screw"/>.</summary>
public sealed class ScrewJoint : AxisJoint
{
    internal ScrewJoint(in MateRef a, in MateRef b, double pitch, string name)
        : this(a, b, pitch, name, DeriveReferences(a, b))
    {
    }

    internal ScrewJoint(
        in MateRef a, in MateRef b, double pitch, string name, in (MateRef RefA, MateRef RefB) references)
        : base(name, 1, a, b,
            [Mate.Concentric(a, b, $"{name} axis")],
            references)
    {
        if (pitch == 0)   // exact-zero semantic test: a zero pitch is a revolute, not a screw
            throw new ArgumentOutOfRangeException(nameof(pitch),
                "A screw pitch cannot be zero — use a revolute or cylindrical joint instead.");
        Pitch = pitch;
        Couplings = [new ScrewCoupling(this)];
    }

    /// <summary>Axial advance per full right-handed turn (negative = left-hand).</summary>
    public double Pitch { get; }

    /// <summary>Axial advance per radian of spin: pitch / 2π.</summary>
    public double AdvancePerRadian => Pitch / (2 * Math.PI);

    internal override IReadOnlyList<AuxiliaryConstraint> Couplings { get; }
}

/// <summary>A rigid connection — see <see cref="Joint.Fixed"/>.</summary>
public sealed class FixedJoint : AxisJoint
{
    internal FixedJoint(in MateRef a, in MateRef b, string name)
        : this(a, b, name, DeriveReferences(a, b))
    {
    }

    internal FixedJoint(in MateRef a, in MateRef b, string name, in (MateRef RefA, MateRef RefB) references)
        : base(name, 0, a, b,
            [
                Mate.Concentric(a, b, $"{name} axis"),
                Mate.Coincident(a, b, $"{name} origin"),
                Mate.Parallel(references.RefA, references.RefB, $"{name} spin lock"),
            ],
            references)
    {
    }
}

/// <summary>A ball joint — see <see cref="Joint.Spherical"/>.</summary>
public sealed class SphericalJoint : Joint
{
    internal SphericalJoint(in MateRef a, in MateRef b, string name)
        : base(name, 3, a, b, [Mate.Coincident(a, b, $"{name} centre")])
    {
    }
}

/// <summary>A planar joint — see <see cref="Joint.Planar"/>.</summary>
public sealed class PlanarJoint : Joint
{
    internal PlanarJoint(in MateRef a, in MateRef b, double gap, string name)
        : base(name, 3, a, b, [Mate.Planar(a, b, gap, $"{name} plane")])
    {
        Gap = gap;
    }

    /// <summary>The bearing gap between the two faces (0 = flush).</summary>
    public double Gap { get; }
}
