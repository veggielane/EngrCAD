using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>A mate end's evaluated world geometry: the point and unit direction the
/// solver composed through the occurrence chain.</summary>
internal readonly record struct EndValue(Vector3d Point, Vector3d Direction);

/// <summary>The derivative of an <see cref="EndValue"/> with respect to one solver
/// variable (the same analytic columns the mate Jacobian uses).</summary>
internal readonly record struct EndDelta(Vector3d Point, Vector3d Direction);

/// <summary>An end's state under the constant-rate flow q(t) = q* + t·q̇: value, first
/// and second time derivatives (the second with q̈ = 0 — the term the acceleration
/// solve moves to the right-hand side). All world-space, all analytic.</summary>
internal readonly record struct EndMotion(
    Vector3d Point, Vector3d Direction,
    Vector3d Velocity, Vector3d DirectionRate,
    Vector3d Acceleration, Vector3d DirectionAcceleration);

/// <summary>
/// A scalar constraint that slots into the mate solver's residual vector beside the
/// geometric mates: screw pitch couplings, gear ratios, cams, and drivers. It speaks
/// the solver's own language — residual rows plus their ANALYTIC derivatives, given the
/// evaluated world geometry of the <see cref="MateRef"/> ends it references — so no
/// second solver exists and the rank/DOF machinery sees these rows like any others.
/// (Finite differences are banned here for the same reason they are banned in the mate
/// Jacobian: they cap accuracy near 1e-8, an order worse than the weld tier.)
/// </summary>
internal abstract class AuxiliaryConstraint
{
    /// <summary>Label for diagnostics, in the mates' naming style.</summary>
    public abstract string Name { get; }

    /// <summary>How many residual rows this constraint contributes.</summary>
    public abstract int RowCount { get; }

    /// <summary>The mate ends whose world geometry the rows are functions of. The
    /// solver evaluates them (composing occurrence chains) and registers their
    /// occurrences as unknowns exactly as it does for mates.</summary>
    public abstract IReadOnlyList<MateRef> Ends { get; }

    /// <summary>Fills this constraint's residual rows. Every row must be a LENGTH in
    /// model units (angular quantities scaled by <paramref name="length"/>, the
    /// assembly's characteristic length), so the solver's one linear tolerance stays
    /// meaningful.</summary>
    public abstract void Residual(ReadOnlySpan<EndValue> ends, double length, Span<double> rows);

    /// <summary>Fills the rows' derivatives with respect to one solver variable, given
    /// the ends' derivatives for that variable.</summary>
    public abstract void Derivative(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndDelta> deltas, double length, Span<double> rows);

    /// <summary>∂rows/∂t — nonzero only for drivers, whose target moves with the sweep
    /// parameter. Feeds the velocity solve J·q̇ = −∂C/∂t.</summary>
    public virtual void TimeDerivative(ReadOnlySpan<EndValue> ends, double length, Span<double> rows) =>
        rows.Clear();

    /// <summary>The rows' second time derivative under the constant-rate flow with
    /// q̈ = 0 — the r̈₀ term the acceleration solve moves to the right-hand side
    /// (J·q̈ = −r̈₀ row by row). Includes any explicit-time second-order terms.</summary>
    public virtual void SecondOrder(ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndMotion> motion, double length, Span<double> rows) =>
        throw new NotSupportedException($"'{Name}' does not support velocity/acceleration analysis.");
}

/// <summary>
/// A joint's swept-motion bookkeeping: the unwrapped angle. The measured angle comes
/// from atan2 and lives on (−π, π]; a crank swept through full turns needs the TOTAL
/// angle, so the state accumulates increments — each solve's iterate is measured
/// relative to the last <b>committed</b> pose, which continuation keeps within a half
/// turn, and the accumulated total moves only when a converged solve commits. Inside
/// one solve the state is read-only, so the residual stays a continuous function of
/// the poses however many LM iterations probe it.
/// </summary>
internal sealed class JointSweepState
{
    /// <summary>The raw measured angle at the last commit.</summary>
    public double LastMeasuredAngle { get; private set; }

    /// <summary>The unwrapped total angle at the last commit (0 at construction).</summary>
    public double AccumulatedAngle { get; private set; }

    /// <summary>The slide coordinate at construction — displacements are measured from
    /// here, so arbitrary axis-origin choices never enter a residual.</summary>
    public double ReferenceSlide { get; set; }

    /// <summary>The unwrapped total angle for a raw measurement, WITHOUT committing:
    /// continuous in the measurement as long as it stays within a half turn of the last
    /// committed one, which continuation guarantees.</summary>
    public double Unwrapped(double measured) =>
        AccumulatedAngle + WrapPi(measured - LastMeasuredAngle);

    /// <summary>Folds a converged pose's measurement into the running total. Called
    /// only after a solve CONVERGED — a failed solve leaves poses and state alike.</summary>
    public void Commit(double measured)
    {
        AccumulatedAngle += WrapPi(measured - LastMeasuredAngle);
        LastMeasuredAngle = measured;
    }

    /// <summary>Re-zeroes the accumulated angle at the current measurement (the slide
    /// reference is the joint's to reset alongside).</summary>
    public void Rebase(double measured)
    {
        AccumulatedAngle = 0;
        LastMeasuredAngle = measured;
    }

    /// <summary>Restores a saved unwrap history verbatim — mechanism persistence's
    /// seam. The accumulated angle is a HISTORY (how many turns the crank has taken),
    /// which no re-derivation from the current pose can recover, so it round-trips as
    /// data.</summary>
    public void Restore(double accumulatedAngle, double lastMeasuredAngle)
    {
        AccumulatedAngle = accumulatedAngle;
        LastMeasuredAngle = lastMeasuredAngle;
    }

    /// <summary>Wraps an angle to (−π, π].</summary>
    public static double WrapPi(double angle)
    {
        double wrapped = Math.IEEERemainder(angle, 2 * Math.PI);
        // IEEERemainder lands on [−π, π]; fold the closed lower end onto +π so the
        // result is unique on the half-open interval.
        return wrapped <= -Math.PI ? wrapped + 2 * Math.PI : wrapped;
    }
}

/// <summary>
/// The joint-coordinate arithmetic shared by every coupling, driver and limit check:
/// the slide z and spin θ of one axis joint, with exact first and second derivatives.
/// Conventions: z = (p_B − p_A)·d_A (B's origin along A's axis), and θ is the angle
/// from the A-side reference perpendicular to the B-side one, measured right-handed
/// about A's axis — atan2 of s = (r_A × r_B)·d_A over c = r_A·r_B.
/// </summary>
internal static class JointArithmetic
{
    public static double Slide(in EndValue axisA, in EndValue axisB) =>
        (axisB.Point - axisA.Point).Dot(axisA.Direction);

    public static double SlideDelta(
        in EndValue axisA, in EndValue axisB, in EndDelta dAxisA, in EndDelta dAxisB) =>
        (dAxisB.Point - dAxisA.Point).Dot(axisA.Direction) +
        (axisB.Point - axisA.Point).Dot(dAxisA.Direction);

    /// <summary>d²z/dt² with q̈ = 0, by the product rule on z = (p_B − p_A)·d_A.</summary>
    public static double SlideSecond(in EndMotion axisA, in EndMotion axisB) =>
        (axisB.Acceleration - axisA.Acceleration).Dot(axisA.Direction) +
        2 * (axisB.Velocity - axisA.Velocity).Dot(axisA.DirectionRate) +
        (axisB.Point - axisA.Point).Dot(axisA.DirectionAcceleration);

    /// <summary>The raw measured spin on (−π, π]. Degenerate references (a reference
    /// collapsed onto the axis) read 0 rather than noise — atan2(0, 0) is 0.</summary>
    public static double Angle(in EndValue axisA, in EndValue refA, in EndValue refB)
    {
        double s = refA.Direction.Cross(refB.Direction).Dot(axisA.Direction);
        double c = refA.Direction.Dot(refB.Direction);
        return Math.Atan2(s, c);
    }

    public static double AngleDelta(
        in EndValue axisA, in EndValue refA, in EndValue refB,
        in EndDelta dAxisA, in EndDelta dRefA, in EndDelta dRefB)
    {
        var d = axisA.Direction;
        var ra = refA.Direction;
        var rb = refB.Direction;
        double s = ra.Cross(rb).Dot(d);
        double c = ra.Dot(rb);
        double ds = (dRefA.Direction.Cross(rb) + ra.Cross(dRefB.Direction)).Dot(d)
                  + ra.Cross(rb).Dot(dAxisA.Direction);
        double dc = dRefA.Direction.Dot(rb) + ra.Dot(dRefB.Direction);
        double n = s * s + c * c;   // ≈ 1 for unit perpendicular references
        return n > 0 ? (c * ds - s * dc) / n : 0;   // exact-zero guard: degenerate refs
    }

    /// <summary>dθ/dt from end velocities (the delta form fed with rates).</summary>
    public static double AngleRate(in EndMotion axisA, in EndMotion refA, in EndMotion refB) =>
        AngleDelta(
            Value(axisA), Value(refA), Value(refB),
            Rate(axisA), Rate(refA), Rate(refB));

    /// <summary>d²θ/dt² with q̈ = 0: the quotient rule on θ̇ = (c·ṡ − s·ċ)/(s² + c²)
    /// with s, c differentiated by the product rule on cross/dot forms.</summary>
    public static double AngleSecond(in EndMotion axisA, in EndMotion refA, in EndMotion refB)
    {
        var (s, c, sd, cd, sdd, cdd) = PhaseMotion(axisA, refA, refB);
        double n = s * s + c * c;
        if (n <= 0)   // exact-zero guard: degenerate refs
            return 0;
        double nd = 2 * (s * sd + c * cd);
        return ((c * sdd - s * cdd) * n - (c * sd - s * cd) * nd) / (n * n);
    }

    public static EndValue Value(in EndMotion motion) => new(motion.Point, motion.Direction);

    public static EndDelta Rate(in EndMotion motion) => new(motion.Velocity, motion.DirectionRate);

    /// <summary>The spin's phase pair s = (r_A × r_B)·d, c = r_A·r_B with first and
    /// second time derivatives under the constant-rate flow (q̈ = 0) — the raw
    /// ingredients drivers and <see cref="AngleSecond"/> share.</summary>
    public static (double S, double C, double Sd, double Cd, double Sdd, double Cdd) PhaseMotion(
        in EndMotion axisA, in EndMotion refA, in EndMotion refB)
    {
        var d = axisA.Direction;
        var dd = axisA.DirectionRate;
        var ddd = axisA.DirectionAcceleration;
        var ra = refA.Direction;
        var rb = refB.Direction;
        var vra = refA.DirectionRate;
        var vrb = refB.DirectionRate;
        var ara = refA.DirectionAcceleration;
        var arb = refB.DirectionAcceleration;

        double s = ra.Cross(rb).Dot(d);
        double c = ra.Dot(rb);
        double sd = (vra.Cross(rb) + ra.Cross(vrb)).Dot(d) + ra.Cross(rb).Dot(dd);
        double cd = vra.Dot(rb) + ra.Dot(vrb);
        double sdd = (ara.Cross(rb) + 2 * vra.Cross(vrb) + ra.Cross(arb)).Dot(d)
                   + 2 * (vra.Cross(rb) + ra.Cross(vrb)).Dot(dd)
                   + ra.Cross(rb).Dot(ddd);
        double cdd = ara.Dot(rb) + 2 * vra.Dot(vrb) + ra.Dot(arb);
        return (s, c, sd, cd, sdd, cdd);
    }
}

/// <summary>
/// The residual a <see cref="MechanismDriver"/> pins one joint variable with. An angle
/// target is encoded as the PAIR [c − cos τ, s − sin τ] (scaled to lengths): two rows
/// carrying one constraint — the solver's usual redundant rotational encoding — chosen
/// because it is continuous for ANY target and iterate, where a θ̂ − τ row would jump
/// by 2π when an LM iterate crossed the wrap seam and stall the solve there. The
/// branch (τ vs τ ± 2π) is picked by proximity, which is exactly what continuation
/// guarantees: sweep steps stay below a half turn. A slide target is the plain length
/// row z − z₀ − τ.
/// </summary>
internal sealed class DriverConstraint(AxisJoint joint, bool drivesAngle) : AuxiliaryConstraint
{
    /// <summary>The driven joint.</summary>
    public AxisJoint Joint => joint;

    /// <summary>True when the driven variable is the spin; false for the slide.</summary>
    public bool DrivesAngle => drivesAngle;

    /// <summary>The target value (unwrapped radians for an angle, model units for a
    /// slide), measured from the joint's construction zero.</summary>
    public double Target { get; set; }

    /// <summary>dTarget/dt for velocity analysis (the driver is the only residual with
    /// explicit time dependence).</summary>
    public double TargetRate { get; set; }

    /// <summary>d²Target/dt² for acceleration analysis.</summary>
    public double TargetAcceleration { get; set; }

    public override string Name =>
        $"{joint.Name} drive {(drivesAngle ? "angle" : "slide")}";

    public override int RowCount => drivesAngle ? 2 : 1;

    public override IReadOnlyList<MateRef> Ends { get; } =
        [joint.A, joint.B, joint.ReferenceA, joint.ReferenceB];

    public override void Residual(ReadOnlySpan<EndValue> ends, double length, Span<double> rows)
    {
        if (drivesAngle)
        {
            double s = ends[2].Direction.Cross(ends[3].Direction).Dot(ends[0].Direction);
            double c = ends[2].Direction.Dot(ends[3].Direction);
            // The target is the unwrapped coordinate; the measured phase carries the
            // reference offset the construction rebase absorbed, so aim for the
            // measured value the target corresponds to.
            double aim = joint.State.LastMeasuredAngle + (Target - joint.State.AccumulatedAngle);
            rows[0] = (c - Math.Cos(aim)) * length;
            rows[1] = (s - Math.Sin(aim)) * length;
        }
        else
        {
            rows[0] = JointArithmetic.Slide(ends[0], ends[1]) - joint.State.ReferenceSlide - Target;
        }
    }

    public override void Derivative(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndDelta> deltas, double length, Span<double> rows)
    {
        if (drivesAngle)
        {
            var d = ends[0].Direction;
            var ra = ends[2].Direction;
            var rb = ends[3].Direction;
            double dc = deltas[2].Direction.Dot(rb) + ra.Dot(deltas[3].Direction);
            double ds = (deltas[2].Direction.Cross(rb) + ra.Cross(deltas[3].Direction)).Dot(d)
                      + ra.Cross(rb).Dot(deltas[0].Direction);
            rows[0] = dc * length;
            rows[1] = ds * length;
        }
        else
        {
            rows[0] = JointArithmetic.SlideDelta(ends[0], ends[1], deltas[0], deltas[1]);
        }
    }

    public override void TimeDerivative(ReadOnlySpan<EndValue> ends, double length, Span<double> rows)
    {
        if (drivesAngle)
        {
            double aim = joint.State.LastMeasuredAngle + (Target - joint.State.AccumulatedAngle);
            rows[0] = Math.Sin(aim) * TargetRate * length;
            rows[1] = -Math.Cos(aim) * TargetRate * length;
        }
        else
        {
            rows[0] = -TargetRate;
        }
    }

    public override void SecondOrder(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndMotion> motion, double length, Span<double> rows)
    {
        if (drivesAngle)
        {
            var (_, _, _, _, sdd, cdd) = JointArithmetic.PhaseMotion(motion[0], motion[2], motion[3]);
            double aim = joint.State.LastMeasuredAngle + (Target - joint.State.AccumulatedAngle);
            // d²/dt²(−cos aim) = cos·τ̇² + sin·τ̈; d²/dt²(−sin aim) = sin·τ̇² − cos·τ̈.
            rows[0] = (cdd + Math.Cos(aim) * TargetRate * TargetRate + Math.Sin(aim) * TargetAcceleration) * length;
            rows[1] = (sdd + Math.Sin(aim) * TargetRate * TargetRate - Math.Cos(aim) * TargetAcceleration) * length;
        }
        else
        {
            rows[0] = JointArithmetic.SlideSecond(motion[0], motion[1]) - TargetAcceleration;
        }
    }
}

/// <summary>
/// The screw pitch coupling — the higher pair that makes a <see cref="ScrewJoint"/>
/// 1-DOF: (z − z₀) − (pitch/2π)·θ̂ = 0, where θ̂ is the joint's unwrapped spin, so a
/// screw driven through many turns keeps advancing instead of snapping back each
/// revolution. Already a length; no scaling needed.
/// </summary>
internal sealed class ScrewCoupling(ScrewJoint joint) : AuxiliaryConstraint
{
    public override string Name => $"{joint.Name} pitch";

    public override int RowCount => 1;

    public override IReadOnlyList<MateRef> Ends { get; } =
        [joint.A, joint.B, joint.ReferenceA, joint.ReferenceB];

    public override void Residual(ReadOnlySpan<EndValue> ends, double length, Span<double> rows)
    {
        double z = JointArithmetic.Slide(ends[0], ends[1]);
        double theta = joint.State.Unwrapped(JointArithmetic.Angle(ends[0], ends[2], ends[3]));
        rows[0] = z - joint.State.ReferenceSlide - joint.AdvancePerRadian * theta;
    }

    public override void Derivative(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndDelta> deltas, double length, Span<double> rows)
    {
        double dz = JointArithmetic.SlideDelta(ends[0], ends[1], deltas[0], deltas[1]);
        double dTheta = JointArithmetic.AngleDelta(ends[0], ends[2], ends[3], deltas[0], deltas[2], deltas[3]);
        rows[0] = dz - joint.AdvancePerRadian * dTheta;
    }

    public override void SecondOrder(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndMotion> motion, double length, Span<double> rows) =>
        rows[0] = JointArithmetic.SlideSecond(motion[0], motion[1])
                - joint.AdvancePerRadian * JointArithmetic.AngleSecond(motion[0], motion[2], motion[3]);
}
