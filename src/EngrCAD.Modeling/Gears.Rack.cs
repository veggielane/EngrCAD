using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A rack: the straight-line limit of the involute tooth. As the tooth count grows the
/// base circle recedes and the involute flattens into a LINE inclined at the pressure
/// angle, which is why the basic rack is not merely one more gear — it is the
/// DEFINITION of the tooth system, and every proportion here (addendum, dedendum, root
/// fillet) is the same ISO 53 profile A coefficient set <see cref="GearSpec"/> carries.
/// </summary>
/// <remarks>
/// <para>A rack has no profile shift of its own: shifting a rack is moving it, so the
/// datum-line offset a shifted gear needs is expressed by where the caller places the
/// bar. <see cref="MatingGear"/> and <see cref="For"/> carry the coefficients both ways,
/// so a rack and its pinion cannot drift apart in the tooth system they claim to share.
/// </para>
/// <para>⚠ The coefficient defaults (addendum 1.00·m, dedendum 1.25·m, root fillet
/// 0.38·m) are transcribed from ISO 53 basic rack profile A: verify against the current
/// standard before production use — profiles B–D carry different clearance/fillet pairs.
/// </para>
/// </remarks>
public sealed record RackSpec
{
    /// <param name="module">Module m (mm) — the circular pitch over π.</param>
    /// <param name="pressureAngleDegrees">Flank inclination α from the tooth's own
    /// centre-line, i.e. from the normal to the pitch line (20° default, ISO 53).</param>
    public RackSpec(double module, double pressureAngleDegrees = 20)
    {
        if (!(module > 0))
            throw new ArgumentOutOfRangeException(nameof(module), "Module must be positive.");
        if (!(pressureAngleDegrees > 0) || !(pressureAngleDegrees < 45))
            throw new ArgumentOutOfRangeException(nameof(pressureAngleDegrees),
                "Pressure angle must lie strictly between 0° and 45°.");
        Module = module;
        PressureAngleDegrees = pressureAngleDegrees;
    }

    /// <summary>Module m (mm).</summary>
    public double Module { get; }

    /// <summary>Pressure angle α in degrees — exactly the flank's inclination, because a
    /// rack flank is straight.</summary>
    public double PressureAngleDegrees { get; }

    /// <summary>Addendum coefficient h_a* (of module), above the pitch line. ISO 53 A: 1.00.</summary>
    public double AddendumCoefficient { get; init; } = 1.00;

    /// <summary>Dedendum coefficient h_f* (of module), below the pitch line, clearance
    /// included. ISO 53 A: 1.25.</summary>
    public double DedendumCoefficient { get; init; } = 1.25;

    /// <summary>Root fillet radius coefficient ρ_f* (of module). ISO 53 A: 0.38.</summary>
    public double RootFilletCoefficient { get; init; } = 0.38;

    internal double PressureAngleRadians => PressureAngleDegrees * Math.PI / 180;

    /// <summary>Circular pitch p = π·m — the tooth-to-tooth spacing along the pitch line,
    /// and the period of the whole profile.</summary>
    public double CircularPitch => Math.PI * Module;

    /// <summary>Tooth thickness at the pitch line, s = π·m/2 — exactly HALF the circular
    /// pitch, which is what makes a standard rack mesh backlash-free with a standard
    /// gear (whose pitch-circle thickness is the same π·m/2).</summary>
    public double ToothThicknessAtPitch => Math.PI * Module / 2;

    /// <summary>Addendum h_a = h_a*·m (pitch line to tip line).</summary>
    public double Addendum => Module * AddendumCoefficient;

    /// <summary>Dedendum h_f = h_f*·m (pitch line to root line).</summary>
    public double Dedendum => Module * DedendumCoefficient;

    /// <summary>Whole depth h = h_a + h_f.</summary>
    public double WholeDepth => Addendum + Dedendum;

    /// <summary>Root fillet radius ρ_f = ρ_f*·m.</summary>
    public double RootFilletRadius => Module * RootFilletCoefficient;

    /// <summary>Width of the flat tip land, s − 2·h_a·tan α. Non-positive means a pointed
    /// tooth and <see cref="Gears.Rack"/> refuses it by name.</summary>
    public double TipLandWidth =>
        ToothThicknessAtPitch - 2 * Addendum * Math.Tan(PressureAngleRadians);

    /// <summary>
    /// The largest root fillet the tooth space admits, ISO 53's
    /// ρ_fP,max = (π·m/4 − h_f·tan α)·cos α/(1 − sin α) — the radius at which the two
    /// fillets of one space meet and the root flat vanishes (0.4719·m for the standard
    /// 20°, h_f* = 1.25 pair, which is why 0.38·m fits).
    /// </summary>
    public double MaximumRootFilletRadius
    {
        get
        {
            double alpha = PressureAngleRadians;
            return (Math.PI * Module / 4 - Dedendum * Math.Tan(alpha))
                * Math.Cos(alpha) / (1 - Math.Sin(alpha));
        }
    }

    /// <summary>
    /// The gear of <paramref name="teeth"/> teeth in this rack's tooth system: same
    /// module, same pressure angle, same proportions. Stated as a conversion rather than
    /// left to the caller because "the rack IS the definition" only means something if
    /// the two objects cannot disagree.
    /// </summary>
    public GearSpec MatingGear(int teeth, double profileShift = 0) =>
        new(Module, teeth, PressureAngleDegrees, profileShift)
        {
            AddendumCoefficient = AddendumCoefficient,
            DedendumCoefficient = DedendumCoefficient,
            RootFilletCoefficient = RootFilletCoefficient,
        };

    /// <summary>The basic rack of <paramref name="gear"/>'s tooth system — the inverse of
    /// <see cref="MatingGear"/>. The profile shift does not travel: it is a property of
    /// where a GEAR sits relative to this rack, not of the rack.</summary>
    public static RackSpec For(GearSpec gear)
    {
        ArgumentNullException.ThrowIfNull(gear);
        return new RackSpec(gear.Module, gear.PressureAngleDegrees)
        {
            AddendumCoefficient = gear.AddendumCoefficient,
            DedendumCoefficient = gear.DedendumCoefficient,
            RootFilletCoefficient = gear.RootFilletCoefficient,
        };
    }
}

/// <summary>
/// A generated rack outline: the <see cref="Sketch"/> plus the construction values a
/// caller (or a test) needs to place and check it.
/// </summary>
/// <remarks>
/// There is deliberately no fit-deviation contract here, and its absence is the point:
/// a rack flank is a straight <c>Line2d</c> and the root fillets are exact
/// <c>Arc2d</c>s, so the profile is the geometry rather than an approximation of it —
/// where <see cref="GearProfile.MaxFitDeviation"/> reports what the involute's biarc
/// chain cost. <see cref="ClosedFormArea"/> is therefore an EQUALITY against
/// <see cref="Modeling.Sketch.Area"/>, not a bound.
/// </remarks>
public sealed class RackProfile
{
    internal RackProfile(RackSpec spec, int teeth, Sketch sketch, double length,
        double backFaceOffset, double closedFormArea)
    {
        Spec = spec;
        Teeth = teeth;
        Sketch = sketch;
        Length = length;
        BackFaceOffset = backFaceOffset;
        ClosedFormArea = closedFormArea;
    }

    /// <summary>The definition this outline was generated from.</summary>
    public RackSpec Spec { get; }

    /// <summary>Number of complete teeth.</summary>
    public int Teeth { get; }

    /// <summary>
    /// The outline as a closed CCW sketch. <b>The pitch line is y = 0</b> and the teeth
    /// point +Y; the bar spans x ∈ [0, <see cref="Length"/>], beginning and ending at a
    /// tooth-SPACE centre so two bars laid end to end at a <see cref="Length"/> offset
    /// form one continuous rack.
    /// </summary>
    public Sketch Sketch { get; }

    /// <summary>Overall length, <see cref="Teeth"/> × the circular pitch.</summary>
    public double Length { get; }

    /// <summary>y of the back face (negative): −(dedendum + the requested back height).</summary>
    public double BackFaceOffset { get; }

    /// <summary>
    /// The EXACT area of the outline: L·backHeight for the bar below the root line, plus
    /// per tooth the flank trapezoid (a + b)(h_a + h_f) and its two fillet corner fills
    /// ρ²[(1 − sin α)/cos α − (π/2 − α)/2], where a and b are the tooth's half-widths at
    /// the tip and root lines. A root fillet FILLS a reflex corner, so each contributes
    /// material rather than removing it.
    /// </summary>
    public double ClosedFormArea { get; }
}

public static partial class Gears
{
    /// <summary>
    /// Generates a rack outline for <paramref name="spec"/> as a closed
    /// <see cref="Sketch"/> with the pitch line on y = 0 and the teeth pointing +Y; see
    /// <see cref="RackProfile.Sketch"/> for the placement contract.
    /// </summary>
    /// <param name="spec">Module, pressure angle and the rack proportions.</param>
    /// <param name="teeth">Number of complete teeth; the bar is that many circular
    /// pitches long.</param>
    /// <param name="backHeight">Material depth BELOW the root line (so the back face sits
    /// at −(dedendum + backHeight)). Defaults to one module.</param>
    /// <exception cref="ArgumentException">Refused by name rather than drawn wrong:
    /// a pointed tooth (tip land at or below the weld tier), a root fillet larger than
    /// the space admits (naming <see cref="RackSpec.MaximumRootFilletRadius"/>), and a
    /// fillet whose flank tangency would climb past the tip line.</exception>
    public static RackProfile Rack(RackSpec spec, int teeth, double? backHeight = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (teeth < 1)
            throw new ArgumentOutOfRangeException(nameof(teeth), "A rack needs at least one tooth.");
        if (!(spec.AddendumCoefficient > 0))
            throw new ArgumentException("Addendum coefficient must be positive.", nameof(spec));
        if (!(spec.DedendumCoefficient > 0))
            throw new ArgumentException("Dedendum coefficient must be positive.", nameof(spec));
        if (spec.RootFilletCoefficient < 0)
            throw new ArgumentException("Root fillet coefficient cannot be negative.", nameof(spec));
        double back = backHeight ?? spec.Module;
        if (!(back > 0))
            throw new ArgumentOutOfRangeException(nameof(backHeight),
                "The back height must be positive (at a tooth-space centre the bar would otherwise have no thickness).");

        double alpha = spec.PressureAngleRadians;
        double sin = Math.Sin(alpha), cos = Math.Cos(alpha), tan = Math.Tan(alpha);
        double p = spec.CircularPitch;
        double ha = spec.Addendum, hf = spec.Dedendum, rho = spec.RootFilletRadius;

        // Half-widths of ONE tooth: πm/4 at the pitch line, narrowing by tan α per unit
        // of height. The flanks are straight, so these two numbers ARE the tooth.
        double half = p / 4;
        double a = half - ha * tan;   // at the tip line
        double b = half + hf * tan;   // at the root line
        // Tangent length from the sharp root corner along the root line to the fillet's
        // touch point: ρ/tan(θ/2) at the corner's interior angle θ = π/2 + α, which
        // reduces to ρ(1 − sin α)/cos α.
        double e = rho * (1 - sin) / cos;

        // Refusals are LENGTHS at the weld tier (an angular epsilon here would not be
        // scale-free), and each names the way out.
        if (!(2 * a > 1e-9))
            throw new ArgumentException(
                $"The rack tooth comes to a point: tip land {2 * a:0.###e0} at addendum "
                + $"{spec.AddendumCoefficient}*m and {spec.PressureAngleDegrees:0.#} deg pressure angle. "
                + "Reduce the addendum or the pressure angle.", nameof(spec));
        if (b + e > p / 2 + 1e-9)
            throw new ArgumentException(
                $"The root fillet (rho = {spec.RootFilletCoefficient}*m = {rho:0.###}) does not fit the "
                + $"tooth space: adjacent fillets would overlap by {2 * (b + e - p / 2):0.###}. The space "
                + $"admits at most rho = {spec.MaximumRootFilletRadius:0.###} "
                + $"(= (pi*m/4 - h_f*tan a)*cos a/(1 - sin a)).", nameof(spec));
        if (rho * (1 - sin) >= ha + hf)
            throw new ArgumentException(
                $"The root fillet (rho = {rho:0.###}) reaches past the tip line: its flank tangency sits "
                + $"{rho * (1 - sin):0.###} above the root line and the whole depth is only "
                + $"{ha + hf:0.###}. Reduce the fillet coefficient.", nameof(spec));

        double length = teeth * p;
        double yRoot = -hf, yTip = ha, yBack = -(hf + back);

        // CCW outer loop: back face left-to-right (material above), up the right end, the
        // toothed top RIGHT to LEFT, then down the left end. Both ends land on a space
        // centre, so the outline tiles at its own length.
        var curves = new List<Curve2d>();
        curves.Add(new Line2d(new Vector2d(0, yBack), new Vector2d(length, yBack)));
        curves.Add(new Line2d(new Vector2d(length, yBack), new Vector2d(length, yRoot)));

        var cursor = new Vector2d(length, yRoot);
        for (int k = teeth - 1; k >= 0; k--)
        {
            double xc = (k + 0.5) * p;
            var rootTouchR = new Vector2d(xc + b + e, yRoot);
            var filletCentreR = new Vector2d(xc + b + e, yRoot + rho);
            var flankTouchR = new Vector2d(xc + b + e - rho * cos, yRoot + rho * (1 - sin));
            var tipR = new Vector2d(xc + a, yTip);
            var tipL = new Vector2d(xc - a, yTip);
            var flankTouchL = new Vector2d(xc - (b + e) + rho * cos, yRoot + rho * (1 - sin));
            var filletCentreL = new Vector2d(xc - (b + e), yRoot + rho);
            var rootTouchL = new Vector2d(xc - (b + e), yRoot);

            // Weld-tier skips: a zero-length run is no segment at all (the root flat
            // vanishes at the maximum fillet, and the fillets themselves at rho = 0).
            if (cursor.X - rootTouchR.X > 1e-9)
                curves.Add(new Line2d(cursor, rootTouchR));
            if (rho > 1e-9)
                curves.Add(ArcBetween(filletCentreR, rho, rootTouchR, flankTouchR));
            curves.Add(new Line2d(flankTouchR, tipR));
            curves.Add(new Line2d(tipR, tipL));
            curves.Add(new Line2d(tipL, flankTouchL));
            if (rho > 1e-9)
                curves.Add(ArcBetween(filletCentreL, rho, flankTouchL, rootTouchL));
            cursor = rootTouchL;
        }
        if (cursor.X > 1e-9)
            curves.Add(new Line2d(cursor, new Vector2d(0, yRoot)));
        curves.Add(new Line2d(new Vector2d(0, yRoot), new Vector2d(0, yBack)));

        // Exact area: the bar below the root line, plus per tooth the flank trapezoid and
        // the two corner fills. The fill is the kite between the corner and the arc,
        // t*rho minus the sector: rho²[(1 − sin a)/cos a − (pi/2 − a)/2].
        double fill = rho * rho * ((1 - sin) / cos - (Math.PI / 2 - alpha) / 2);
        double area = length * back + teeth * ((a + b) * (ha + hf) + 2 * fill);

        return new RackProfile(spec, teeth, Sketch.FromCurves(curves), length, yBack, area);
    }

    /// <summary>
    /// A rack bar: the <see cref="Rack"/> outline extruded to
    /// <paramref name="faceWidth"/>. Exact in all three representations — the profile is
    /// lines and circular arcs, which is the whole dividend of the rack being the
    /// involute's straight-line limit rather than another curve to fit.
    /// </summary>
    public static Shape RackBar(RackSpec spec, int teeth, double faceWidth, double? backHeight = null)
    {
        if (!(faceWidth > 0))
            throw new ArgumentOutOfRangeException(nameof(faceWidth));
        return Shape.Extrude(Rack(spec, teeth, backHeight).Sketch, faceWidth);
    }
}
