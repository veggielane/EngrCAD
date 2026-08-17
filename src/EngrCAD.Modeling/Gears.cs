using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// An involute spur gear definition: module, tooth count, pressure angle and profile
/// shift, with the tooth proportions of the ISO 53 basic rack (profile A) as
/// overridable coefficients. All linear dimensions derive from the module in
/// millimetres; angles are degrees at the API surface (radians internally).
/// </summary>
/// <remarks>
/// The derived properties are the base-circle identities stated as arithmetic —
/// <see cref="BasePitch"/> = π·m·cos α, <see cref="BaseDiameter"/> = z·m·cos α,
/// <see cref="ToothThicknessAtPitch"/> = m·(π/2 + 2x·tan α) — so a caller (and a test)
/// can ask the spec rather than restate the formulas. The coefficient defaults
/// (addendum 1.00·m, dedendum 1.25·m, root fillet 0.38·m) are transcribed from the
/// ISO 53 basic rack profile A: VERIFY against the current standard before production
/// use — profiles B–D carry different clearance/fillet pairs.
/// </remarks>
public sealed record GearSpec
{
    public GearSpec(double module, int teeth, double pressureAngleDegrees = 20, double profileShift = 0)
    {
        if (!(module > 0))
            throw new ArgumentOutOfRangeException(nameof(module), "Module must be positive.");
        if (teeth < 3)
            throw new ArgumentOutOfRangeException(nameof(teeth), "A gear needs at least 3 teeth.");
        if (!(pressureAngleDegrees > 0) || !(pressureAngleDegrees < 45))
            throw new ArgumentOutOfRangeException(nameof(pressureAngleDegrees),
                "Pressure angle must lie strictly between 0° and 45°.");
        if (!double.IsFinite(profileShift))
            throw new ArgumentOutOfRangeException(nameof(profileShift));
        Module = module;
        Teeth = teeth;
        PressureAngleDegrees = pressureAngleDegrees;
        ProfileShift = profileShift;
    }

    /// <summary>Module m (mm) — pitch diameter over tooth count.</summary>
    public double Module { get; }

    /// <summary>Tooth count z.</summary>
    public int Teeth { get; }

    /// <summary>Pressure angle α in degrees (20° default, the ISO 53 standard).</summary>
    public double PressureAngleDegrees { get; }

    /// <summary>Profile shift coefficient x (dimensionless; the rack datum shifts by x·m).</summary>
    public double ProfileShift { get; }

    /// <summary>Addendum coefficient h_a* (of module). ISO 53 profile A: 1.00.</summary>
    public double AddendumCoefficient { get; init; } = 1.00;

    /// <summary>Dedendum coefficient h_f* (of module), including the clearance. ISO 53 profile A: 1.25.</summary>
    public double DedendumCoefficient { get; init; } = 1.25;

    /// <summary>Root fillet radius coefficient ρ_f* (of module). ISO 53 profile A: 0.38.</summary>
    public double RootFilletCoefficient { get; init; } = 0.38;

    /// <summary>Circumferential backlash allowance j (mm, at the pitch circle): THIS
    /// gear's teeth are thinned by j, so a pair at standard centre distance runs with
    /// clearance equal to the SUM of the two members' allowances. 0 (the default) is
    /// the zero-backlash nominal every existing gear draws — an exact-zero branch in
    /// the generator, so a spec stating nothing is bit-identical. The thinning rotates
    /// each flank toward the tooth centre by j/(2·r_pitch); it is exact, because a
    /// cycloid this is not — an involute rotated about its own centre is the same
    /// involute at another phase.</summary>
    public double Backlash { get; init; }

    internal double PressureAngleRadians => PressureAngleDegrees * Math.PI / 180;

    /// <summary>Pitch (reference) diameter d = m·z.</summary>
    public double PitchDiameter => Module * Teeth;

    /// <summary>Base circle diameter d_b = m·z·cos α — the circle every flank tangent touches.</summary>
    public double BaseDiameter => PitchDiameter * Math.Cos(PressureAngleRadians);

    /// <summary>Circular pitch p = π·m (tooth spacing along the pitch circle).</summary>
    public double CircularPitch => Math.PI * Module;

    /// <summary>Base pitch p_b = π·m·cos α (flank spacing along any line of action —
    /// what two meshing gears must agree on for conjugate handover).</summary>
    public double BasePitch => Math.PI * Module * Math.Cos(PressureAngleRadians);

    /// <summary>Tip (addendum) diameter d_a = m·(z + 2·(h_a* + x)).</summary>
    public double TipDiameter => Module * (Teeth + 2 * (AddendumCoefficient + ProfileShift));

    /// <summary>Root diameter d_f = m·(z − 2·(h_f* − x)).</summary>
    public double RootDiameter => Module * (Teeth - 2 * (DedendumCoefficient - ProfileShift));

    /// <summary>Tooth thickness along the pitch circle s = m·(π/2 + 2x·tan α) − j
    /// (the <see cref="Backlash"/> allowance thins it; subtracting an exact 0 is the
    /// identity, so a backlash-free spec reads the incumbent value bit for bit).</summary>
    public double ToothThicknessAtPitch =>
        Module * (Math.PI / 2 + 2 * ProfileShift * Math.Tan(PressureAngleRadians)) - Backlash;

    /// <summary>The involute function inv α = tan α − α, of an angle in RADIANS — the
    /// arithmetic every measurement identity below leans on.</summary>
    public static double InvoluteFunction(double radians) => Math.Tan(radians) - radians;

    /// <summary>
    /// The span (base tangent) measurement over <paramref name="k"/> teeth — the
    /// caliper dimension W = (k − 1)·p_b + cos α·(s + m·z·inv α), which REDUCES to the
    /// textbook m·cos α·((k − ½)π + z·inv α) + 2x·m·sin α at zero backlash and drops by
    /// exactly j·cos α with the allowance (a pitch-circle thinning is a base-circle
    /// thinning times cos α). Refused when the caliper's contact would miss the
    /// involute flank: the contact radius √(r_b² + (W/2)²) must lie between the base
    /// and tip circles, which is what bounds k for a given tooth count.
    /// </summary>
    public double SpanOverTeeth(int k)
    {
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "The span must cover at least one tooth.");
        double alpha = PressureAngleRadians;
        double w = (k - 1) * BasePitch
            + Math.Cos(alpha) * (ToothThicknessAtPitch + Module * Teeth * InvoluteFunction(alpha));
        double rb = BaseDiameter / 2;
        double contactRadius = Math.Sqrt(rb * rb + w * w / 4);
        if (contactRadius <= rb || contactRadius >= TipDiameter / 2)
            throw new ArgumentOutOfRangeException(nameof(k),
                $"A span over {k} teeth puts the caliper contact at radius {contactRadius:G6}, " +
                $"outside the involute flank (base {rb:G6} to tip {TipDiameter / 2:G6}); " +
                "choose k so the contact lands on the flank.");
        return w;
    }

    /// <summary>
    /// The measurement over two pins (balls) of <paramref name="pinDiameter"/> seated
    /// in opposite tooth spaces — even tooth counts measure across a diameter, odd ones
    /// across the nearest-to-opposite pair (the centre distance times cos(90°/z)). The
    /// contact pressure angle solves inv α_M = d_pin/(m·z·cos α) − π/z + s/(m·z) + inv α
    /// (the <see cref="Backlash"/>-thinned tooth thickness s widens the space and drops
    /// the measurement, exactly as a real allowance does), inverted by Newton on the
    /// involute function. A pin too small to reach the flank (inv α_M ≤ 0 — it would
    /// seat on the root fillet) or so large its contact leaves the tip is refused by
    /// name.
    /// </summary>
    public double MeasurementOverPins(double pinDiameter)
    {
        if (!(pinDiameter > 0) || !double.IsFinite(pinDiameter))
            throw new ArgumentOutOfRangeException(nameof(pinDiameter));
        double alpha = PressureAngleRadians;
        double rb = BaseDiameter / 2;
        double rp = PitchDiameter / 2;
        // inv α_M = r_pin/r_b − (half space angle at base), the pin centre on the
        // space centreline with its contact normal tangent to the base circle.
        double halfSpaceAtBase = Math.PI / Teeth
            - (ToothThicknessAtPitch / (2 * rp) + InvoluteFunction(alpha));
        double invM = pinDiameter / (2 * rb) - halfSpaceAtBase;
        if (invM <= 0)
            throw new ArgumentOutOfRangeException(nameof(pinDiameter),
                $"A Ø{pinDiameter:G6} pin seats below the base circle (inv α_M = {invM:G4} ≤ 0) — " +
                "it would rest on the root fillet, not the involute flank; use a larger pin.");
        // The pin centre cannot sit past the tip circle plus its own radius, which
        // bounds the contact pressure angle: α_M ≤ acos(r_b/(r_tip + r_pin)). Checked
        // in inv-space BEFORE the Newton inversion — the involute function has a
        // second branch past π/2 the iteration would otherwise land on, returning a
        // confidently wrong measurement instead of a refusal.
        double maxAlpha = Math.Acos(Math.Min(1.0, rb / (TipDiameter / 2 + pinDiameter / 2)));
        if (invM >= InvoluteFunction(maxAlpha))
            throw new ArgumentOutOfRangeException(nameof(pinDiameter),
                $"A Ø{pinDiameter:G6} pin stands clear of the tooth tips and cannot seat; " +
                "use a smaller pin.");
        double alphaM = InverseInvolute(invM);
        double centreRadius = rb / Math.Cos(alphaM);
        if (centreRadius - pinDiameter / 2 >= TipDiameter / 2)
            throw new ArgumentOutOfRangeException(nameof(pinDiameter),
                $"A Ø{pinDiameter:G6} pin stands clear of the tooth tips and cannot seat; " +
                "use a smaller pin.");
        return Teeth % 2 == 0
            ? 2 * centreRadius + pinDiameter
            : 2 * centreRadius * Math.Cos(Math.PI / (2 * Teeth)) + pinDiameter;
    }

    /// <summary>Newton inversion of inv α = tan α − α (derivative tan²α), seeded by the
    /// classical cube-root estimate α ≈ (3·inv)^⅓ — quadratic from there, and the
    /// involute function is convex increasing on (0, π/2) so the iteration is safe.</summary>
    private static double InverseInvolute(double value)
    {
        double a = Math.Cbrt(3 * value);
        for (int i = 0; i < 32; i++)
        {
            double f = Math.Tan(a) - a - value;
            double slope = Math.Tan(a) * Math.Tan(a);
            if (!(slope > 0))
                break;
            double step = f / slope;
            a -= step;
            if (Math.Abs(step) < 1e-15)
                break;
        }
        return a;
    }

    /// <summary>
    /// The rack-generation undercut limit z_min = 2·(h_a* − x)/sin²α: below this tooth
    /// count a generating cutter trims the root trochoid into the involute, and a mating
    /// tooth would interfere with the drawn flank. <see cref="Gears.Spur"/> refuses below it.
    /// </summary>
    public double MinimumTeethWithoutUndercut =>
        2 * (AddendumCoefficient - ProfileShift) / Sin2(PressureAngleRadians);

    /// <summary>The smallest profile shift avoiding undercut at this tooth count:
    /// x_min = h_a* − z·sin²α/2.</summary>
    public double MinimumProfileShift =>
        AddendumCoefficient - Teeth * Sin2(PressureAngleRadians) / 2;

    private static double Sin2(double a) => Math.Sin(a) * Math.Sin(a);
}

/// <summary>
/// A generated involute gear tooth profile: the <see cref="Sketch"/> (lines and circular
/// arcs only, so it is exact in all three representations downstream) plus the fit
/// contract — the tolerance that was asked for and the deviation that was measured.
/// </summary>
/// <remarks>
/// The flank is the closed-form involute of the base circle approximated by a
/// tangent-continuous biarc chain (<c>BiArcFit</c>'s convention: the deviation is
/// REPORTED, never silent). <see cref="MaxFitDeviation"/> is measured against the
/// closed form at 512 samples per flank after the fit; everything else in the outline —
/// tip arc, root arc, root fillets, the radial flank stretch below the base circle —
/// is exact by construction, so the fit deviation is the profile's entire error.
/// </remarks>
public sealed class GearProfile
{
    internal GearProfile(GearSpec spec, Sketch sketch, double fitTolerance, double maxFitDeviation,
        int curvesPerFlank, double closedFormArea, in GearToothGeometry geometry)
    {
        Spec = spec;
        Sketch = sketch;
        FitTolerance = fitTolerance;
        MaxFitDeviation = maxFitDeviation;
        CurvesPerFlank = curvesPerFlank;
        ClosedFormArea = closedFormArea;
        Geometry = geometry;
    }

    /// <summary>The definition this profile was generated from.</summary>
    public GearSpec Spec { get; }

    /// <summary>The tooth profile as a closed sketch (CCW outer loop, no holes),
    /// centred at the origin with one tooth centred on +X.</summary>
    public Sketch Sketch { get; }

    /// <summary>The flank fit tolerance that was requested (mm).</summary>
    public double FitTolerance { get; }

    /// <summary>The measured maximum deviation of the fitted flank from the closed-form
    /// involute (mm) — at most <see cref="FitTolerance"/>.</summary>
    public double MaxFitDeviation { get; }

    /// <summary>Number of curves the biarc chain spent on one flank.</summary>
    public int CurvesPerFlank { get; }

    /// <summary>
    /// The EXACT area of the ideal (un-fitted) outline, from closed forms: the involute's
    /// Green's-theorem term is r_b²·t³/6, every other piece is a line or arc. The
    /// sketch's own <see cref="Modeling.Sketch.Area"/> differs from this by at most the
    /// fit deviation times the flank length — an identity a test can hold the profile to.
    /// </summary>
    public double ClosedFormArea { get; }

    /// <summary>Construction values (radii, roll angles, fillet case) for verification.</summary>
    internal GearToothGeometry Geometry { get; }
}

/// <summary>Closed-form construction values for one canonical tooth (internal test seam).</summary>
internal readonly record struct GearToothGeometry(
    double PitchRadius, double BaseRadius, double TipRadius, double RootRadius,
    double FilletRadius, double HalfToothAngle, double InvoluteOrigin,
    double RollAtRoot, double RollAtTip, bool FilletTangentToInvolute,
    double TipArcSweep, double RootArcSweep);

/// <summary>
/// Involute gear factory: <see cref="Spur"/> generates the tooth profile as a
/// <see cref="Sketch"/>, <see cref="SpurGear"/> and <see cref="HelicalGear"/> wrap it
/// into solids. The geometry counterpart of <c>Coupling.Gear</c>, which constrains the
/// ratio but draws nothing.
/// <para>Also here: <see cref="Rack"/>/<see cref="RackBar"/> (the z→∞ limit, whose
/// straight flanks make it the DEFINITION of the tooth system rather than another
/// member of it) and <see cref="Worm"/>/<see cref="WormWheel"/> (the worm IS a thread,
/// so it rides the helical-sweep machinery; the wheel is a crossed-helical
/// approximation with its point-contact caveat stated).</para>
/// </summary>
public static partial class Gears
{
    /// <summary>
    /// Generates the involute spur gear profile for <paramref name="spec"/> as a closed
    /// <see cref="Sketch"/> centred at the origin with a tooth centred on +X.
    /// </summary>
    /// <param name="spec">Module, tooth count, pressure angle, profile shift, rack coefficients.</param>
    /// <param name="fitTolerance">Maximum allowed deviation of the fitted flank from the
    /// closed-form involute (mm). Defaults to module·1e-4 — an order tighter than the
    /// finest ISO 1328 profile form tolerances, at a cost of roughly a dozen arcs per
    /// flank. The deviation actually achieved is reported on
    /// <see cref="GearProfile.MaxFitDeviation"/>.</param>
    /// <exception cref="ArgumentException">
    /// The geometry refuses by name rather than drawing a flank it cannot stand behind:
    /// tooth counts below the rack undercut limit z_min = 2·(h_a* − x)/sin²α (the drawn
    /// involute would interfere with a conjugate tooth where a generating cutter would
    /// have trochoid-trimmed it), pointed teeth (tip thickness below weld tolerance),
    /// a root fillet that does not fit its gap or consumes the whole flank, and
    /// degenerate radii (root ≤ 0, tip ≤ base).
    /// </exception>
    public static GearProfile Spur(GearSpec spec, double? fitTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        double tolerance = fitTolerance ?? spec.Module * 1e-4;
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(fitTolerance));
        if (!(spec.AddendumCoefficient > 0))
            throw new ArgumentException("Addendum coefficient must be positive.", nameof(spec));
        if (!(spec.DedendumCoefficient > 0))
            throw new ArgumentException("Dedendum coefficient must be positive.", nameof(spec));
        if (spec.RootFilletCoefficient < 0)
            throw new ArgumentException("Root fillet coefficient cannot be negative.", nameof(spec));

        double alpha = spec.PressureAngleRadians;
        double m = spec.Module;
        int z = spec.Teeth;
        double x = spec.ProfileShift;

        // Undercut: the classical rack-generation limit, using the WORKING addendum
        // coefficient (the straight portion of the cutter flank; the fillet/clearance
        // band below it cuts no involute). Refused rather than trochoid-trimmed: an
        // honest refusal beats an unverified flank.
        double sinAlpha = Math.Sin(alpha);
        if (z * sinAlpha * sinAlpha / 2 + x < spec.AddendumCoefficient)
            throw new ArgumentException(
                $"A z={z} gear at {spec.PressureAngleDegrees:0.#} deg pressure angle with profile shift "
                + $"x={x:0.###} would be undercut: undercut begins below z_min = 2(h_a* - x)/sin^2(a) = "
                + $"{spec.MinimumTeethWithoutUndercut:0.##} teeth (equivalently below x_min = "
                + $"{spec.MinimumProfileShift:0.###} at z={z}). A generating cutter would trochoid-trim "
                + "the root there, which this factory does not model; add teeth, raise the pressure "
                + "angle, or increase the profile shift.");

        double r = m * z / 2;                          // pitch radius
        double rb = r * Math.Cos(alpha);               // base radius
        double ra = r + m * (spec.AddendumCoefficient + x);
        double rf = r - m * (spec.DedendumCoefficient - x);
        double rho = m * spec.RootFilletCoefficient;

        if (!(rf > 0))
            throw new ArgumentException(
                $"Root diameter {2 * rf:0.###} is not positive (dedendum {spec.DedendumCoefficient}*m "
                + $"with profile shift {x:0.###} exceeds the pitch radius).", nameof(spec));
        if (!(ra > rb))
            throw new ArgumentException(
                $"Tip radius {ra:0.###} does not clear the base radius {rb:0.###} - there is no "
                + "involute to draw. Increase the addendum or the profile shift.", nameof(spec));
        if (!(ra > rf))
            throw new ArgumentException(
                $"Tip radius {ra:0.###} does not clear the root radius {rf:0.###}.", nameof(spec));

        double invAlpha = Math.Tan(alpha) - alpha;
        double psi = (Math.PI / 2 + 2 * x * Math.Tan(alpha)) / z;   // half tooth angle at pitch
        if (spec.Backlash != 0)
        {
            // The allowance thins the tooth by j at the pitch circle — each flank
            // rotates j/(2·r_pitch) toward the tooth centre. An exact-zero branch, so a
            // spec stating no backlash generates bit-identical geometry.
            if (!(spec.Backlash > 0) || !double.IsFinite(spec.Backlash))
                throw new ArgumentOutOfRangeException(nameof(spec),
                    "Backlash must be a finite, non-negative allowance.");
            double nominal = spec.Module * (Math.PI / 2 + 2 * x * Math.Tan(alpha));
            if (spec.Backlash >= nominal)
                throw new ArgumentOutOfRangeException(nameof(spec),
                    $"A backlash of {spec.Backlash:G6} eats the whole {nominal:G6} tooth " +
                    "thickness at the pitch circle.");
            psi -= spec.Backlash / (spec.Module * z);
        }
        double theta0 = -psi - invAlpha;               // right-flank involute origin ray
        double tTip = Math.Sqrt(ra * ra / (rb * rb) - 1);

        // Root fillet tangency, CLOSED FORM: with the involute P(t), gap-side normal n
        // and fillet centre C = P + rho*n, |C|^2 = (r_b*t + rho)^2 + r_b^2 - so
        // |C| = r_f + rho solves to t* = (sqrt((r_f+rho)^2 - r_b^2) - rho)/r_b, monotone
        // in t. t* >= 0 means the fillet is tangent to the involute itself; otherwise the
        // tangency would fall below the base circle and the flank continues as a RADIAL
        // line (the involute's own cusp tangent, so the joint is tangent-continuous)
        // down to a fillet tangent to that line.
        double q = (rf + rho) * (rf + rho) - rb * rb;
        bool filletOnInvolute = q >= rho * rho;
        double tLow = filletOnInvolute ? (Math.Sqrt(q) - rho) / rb : 0;
        if (tLow >= tTip)
            throw new ArgumentException(
                $"The root fillet (rho = {spec.RootFilletCoefficient}*m = {rho:0.###}) consumes the whole "
                + $"flank: its involute tangency at roll {tLow:0.###} is past the tip at roll {tTip:0.###}. "
                + "Reduce the fillet coefficient or the dedendum.", nameof(spec));

        // Tip arc: sweep 2*(psi + inv(a) - inv(a_tip)); a tip THICKNESS below the weld
        // tier (expressed as a length - angular epsilons scale with radius) is a pointed
        // tooth and is refused by name.
        double invAlphaTip = tTip - Math.Atan(tTip);
        double tipHalfAngle = psi + invAlpha - invAlphaTip;
        double tipThickness = 2 * tipHalfAngle * ra;
        if (tipThickness < 1e-9)
            throw new ArgumentException(
                $"The tooth comes to a point: tip thickness {tipThickness:0.###e0} at profile shift "
                + $"x={x:0.###}. Reduce the profile shift or the addendum, or add teeth.", nameof(spec));

        // ---- canonical right-side pieces (root -> tip), tooth centred on +X ----
        var right = new List<Curve2d>();
        Vector2d filletCentre, filletRootPoint, filletFlankPoint;
        if (filletOnInvolute)
        {
            var p0 = InvolutePoint(rb, theta0, tLow);
            double u0 = theta0 + tLow;
            var gapNormal = new Vector2d(Math.Sin(u0), -Math.Cos(u0));
            filletCentre = p0 + gapNormal * rho;
            filletFlankPoint = p0;
            filletRootPoint = filletCentre * (rf / filletCentre.Length);
        }
        else
        {
            // Fillet tangent to the radial stretch: centre at distance r_f+rho from the
            // origin, perpendicular distance rho from the flank ray, on the gap side.
            double gamma = Math.Asin(rho / (rf + rho));
            double thetaC = theta0 - gamma;
            filletCentre = new Vector2d(Math.Cos(thetaC), Math.Sin(thetaC)) * (rf + rho);
            filletRootPoint = new Vector2d(Math.Cos(thetaC), Math.Sin(thetaC)) * rf;
            double dt = Math.Sqrt((rf + rho) * (rf + rho) - rho * rho);
            filletFlankPoint = new Vector2d(Math.Cos(theta0), Math.Sin(theta0)) * dt;
        }

        double filletSweep = 0;
        if (rho > 1e-9) // weld tier: a fillet below weld tolerance is no segment at all
        {
            var fillet = ArcBetween(filletCentre, rho, filletRootPoint, filletFlankPoint);
            filletSweep = fillet.SweepAngle;
            right.Add(fillet);
        }
        if (!filletOnInvolute)
        {
            var basePoint = new Vector2d(Math.Cos(theta0), Math.Sin(theta0)) * rb;
            // Skip the radial stretch when shorter than weld tolerance (the fillet then
            // reaches the base circle itself).
            if (rb - filletFlankPoint.Length > 1e-9)
                right.Add(new Line2d(filletFlankPoint, basePoint));
        }

        var flank = FitFlank(rb, theta0, tLow, tTip, tolerance, out double deviation);
        right.AddRange(flank);

        // Tip arc from the right flank's end across the tooth top.
        double tipStart = theta0 + tTip - Math.Atan(tTip);
        var tipArc = new Arc2d(Vector2d.Zero, ra, tipStart, 2 * tipHalfAngle);

        // Left side: mirror of the right side across +X, traversed tip -> root. Exact
        // parameter transforms (the left-hand-thread lesson: mirrored geometry must be
        // the arithmetic mirror, not a re-fit).
        var left = new List<Curve2d>(right.Count);
        for (int i = right.Count - 1; i >= 0; i--)
            left.Add(ReverseCurve(MirrorX(right[i])));

        // Root arc across the gap to the next tooth. Its angular reach is the fillet
        // root point's; refusal/skip thresholds are LENGTHS (weld tier).
        double rootPointAngle = -Math.Atan2(filletCentre.Y, filletCentre.X);
        double pitchAngle = 2 * Math.PI / z;
        double rootSweep = pitchAngle - 2 * rootPointAngle;
        double rootLength = rootSweep * rf;
        if (rootLength < -1e-9)
            throw new ArgumentException(
                $"The root fillet (rho = {spec.RootFilletCoefficient}*m = {rho:0.###}) does not fit the "
                + $"root gap: adjacent fillets overlap by {-rootSweep:0.###} rad at the root circle. "
                + "Reduce the fillet coefficient or the profile shift, or add teeth.", nameof(spec));
        Arc2d? rootArc = rootLength < 1e-9
            ? null
            : new Arc2d(Vector2d.Zero, rf, rootPointAngle, rootSweep);

        // ---- assemble all teeth by exact parameter rotation ----
        var tooth = new List<Curve2d>(right.Count + left.Count + 2);
        tooth.AddRange(right);
        tooth.Add(tipArc);
        tooth.AddRange(left);
        if (rootArc is not null)
            tooth.Add(rootArc);

        var outline = new List<Curve2d>(tooth.Count * z);
        for (int k = 0; k < z; k++)
        {
            double beta = k * pitchAngle;
            double cos = Math.Cos(beta), sin = Math.Sin(beta);
            foreach (var piece in tooth)
                outline.Add(Rotate(piece, cos, sin, beta));
        }
        var sketch = Sketch.FromCurves(outline);

        // Exact area of the ideal outline by Green's theorem: involute term r_b^2*t^3/6
        // per flank, r^2*sweep/2 for origin-centred arcs, [C x (B-A) + rho^2*sweep]/2
        // for fillets, exactly zero for the radial stretches (lines through the origin).
        double filletTerm = rho > 1e-9
            ? 0.5 * (filletCentre.Cross(filletFlankPoint - filletRootPoint) + rho * rho * filletSweep)
            : 0;
        double areaPerTooth =
            rb * rb * (tTip * tTip * tTip - tLow * tLow * tLow) / 3
            + 0.5 * ra * ra * tipArc.SweepAngle
            + (rootArc is not null ? 0.5 * rf * rf * rootSweep : 0)
            + 2 * filletTerm;
        double closedFormArea = z * areaPerTooth;

        var geometry = new GearToothGeometry(
            r, rb, ra, rf, rho, psi, theta0, tLow, tTip, filletOnInvolute,
            tipArc.SweepAngle, rootArc?.SweepAngle ?? 0);
        return new GearProfile(spec, sketch, tolerance, deviation, flank.Count, closedFormArea, geometry);
    }

    /// <summary>
    /// A spur gear solid: the <see cref="Spur"/> profile extruded to
    /// <paramref name="faceWidth"/>, with an optional plain bore. Exact in all three
    /// representations (the profile is lines and circular arcs). An optional
    /// <paramref name="hub"/> adds a set-screw boss — a cylinder proud of the +Z web
    /// face carrying the bore (keyway included) through it, with an optional radial
    /// set-screw pilot when the bore is plain (see <see cref="GearHubSpec"/>).
    /// </summary>
    public static Shape SpurGear(GearSpec spec, double faceWidth, double boreDiameter = 0,
        KeywaySpec? keyway = null, LighteningSpec? lightening = null,
        double? fitTolerance = null, GearHubSpec? hub = null)
    {
        if (hub is not { } boss)
        {
            var sketch = GearBlank(spec, faceWidth, boreDiameter, keyway, lightening, fitTolerance);
            return Shape.Extrude(sketch, faceWidth);
        }

        // A HUB (set-screw boss): a cylinder proud of the web. The construction order is
        // what makes every boolean legal — the gear WITHOUT its bore is unioned with the
        // hub DISC (their interface is a flush planar ring, the coplanar-fusion tier's
        // own case; a hub extruded WITH the bore would put two coaxial equal bore walls
        // in one union, the refused coincident-curved configuration), and the bore prism
        // is subtracted LAST through both levels, overshooting both ends so every cut is
        // transversal (the Drill doctrine). The set-screw pilot is a radial cylinder cut
        // BEFORE the bore, while the hub's centre is still solid — an ordinary blind
        // flat-bottom hole whose floor the bore prism then removes, opening it into the
        // bore without its cap ever meeting a face.
        if (boreDiameter <= 0)
            throw new ArgumentOutOfRangeException(nameof(hub),
                "A hub is a boss gripping a shaft; state a boreDiameter for it to grip.");
        double rootR = spec.RootDiameter / 2;
        double hubR = boss.Diameter / 2;
        double innerReach = boreDiameter / 2 + (keyway?.HubDepth ?? 0);
        if (hubR <= innerReach)
            throw new ArgumentOutOfRangeException(nameof(hub),
                $"Hub Ø{boss.Diameter:0.###} does not clear the bore" +
                (keyway is null ? "" : "'s keyway") + $" (reach radius {innerReach:0.###}).");
        if (hubR >= rootR)
            throw new ArgumentOutOfRangeException(nameof(hub),
                $"Hub Ø{boss.Diameter:0.###} reaches the root circle (diameter {spec.RootDiameter:0.###}); " +
                "a boss that swallows the tooth roots is a blank redesign, not a hub.");
        if (!(boss.Projection > 0))
            throw new ArgumentOutOfRangeException(nameof(hub), "The hub projection must be positive.");
        if (lightening is { } ringCheck)
        {
            double ringR = (ringCheck.CircleDiameter ?? innerReach + rootR) / 2;
            if (ringR - ringCheck.HoleDiameter / 2 <= hubR)
                throw new ArgumentOutOfRangeException(nameof(hub),
                    $"Ø{ringCheck.HoleDiameter:0.###} lightening holes on a Ø{2 * ringR:0.###} circle " +
                    $"reach the hub wall (radius {hubR:0.###}) — the boss would blind them.");
        }
        if (keyway is not null && boss.SetScrewDiameter is not null)
            throw new ArgumentOutOfRangeException(nameof(hub),
                "A set screw and a keyway together are refused: the keyed bore's partial-ARC " +
                "extruded wall against the radial pilot is a surface pair the B-Rep boolean " +
                "measurably misclassifies — the result was closed, Validate-clean and genus-correct " +
                "with 69 of the pilot's 158 mm³ of wall removal silently retained (wrong-but-closed; " +
                "the reproducible fixture is filed in todo.md). Use the keyway alone and drill the " +
                "pilot in a second setup, or use the set screw with a plain bore.");
        double screwOffset = 0;
        if (boss.SetScrewDiameter is { } pilot)
        {
            if (!(pilot > 0) || pilot >= boss.Projection)
                throw new ArgumentOutOfRangeException(nameof(hub),
                    $"Set screw Ø{pilot:0.###} must be positive and smaller than the hub projection " +
                    $"({boss.Projection:0.###}).");
            screwOffset = boss.SetScrewOffset ?? boss.Projection / 2;
            if (screwOffset - pilot / 2 <= 0 || screwOffset + pilot / 2 >= boss.Projection)
                throw new ArgumentOutOfRangeException(nameof(hub),
                    $"Set screw Ø{pilot:0.###} at offset {screwOffset:0.###} leaves the hub band " +
                    $"(0..{boss.Projection:0.###}).");
        }

        var blank = GearBlank(spec, faceWidth, boreDiameter, keyway, lightening, fitTolerance,
            includeBore: false);
        var body = Shape.Extrude(blank, faceWidth)
            .Union(Shape.Cylinder(hubR, boss.Projection)
                .Translate((0, 0, faceWidth + boss.Projection / 2)));

        double over = 0.05 * (faceWidth + boss.Projection);
        if (boss.SetScrewDiameter is { } screw)
        {
            // Radial, along +X — cut BEFORE the bore, while the hub's centre is still
            // solid: the tool ends in material as an ordinary blind flat-bottom hole,
            // and the bore prism below then removes the metal holding its bottom,
            // opening the pilot into the bore. Cutting after the bore instead runs the
            // pilot wall into the finished bore wall, a perpendicular-cylinder pair
            // whose traced window measurably truncates at this scale (Ø5 through Ø16).
            // The keyed combination is refused above, so the bore here is a full
            // circle whose wall promotes to an exact cylinder.
            double reach = hubR - boreDiameter / 2;
            var tool = Shape.Cylinder(screw / 2, reach + 2 * over)
                .RotateY(Math.PI / 2)
                .Translate(((boreDiameter / 2 + hubR) / 2, 0, faceWidth + screwOffset));
            body = body.Subtract(tool);
        }

        // The bore, once, through both levels — overshooting each end so every cut is
        // transversal. ONE tool: the plain circle, or the whole keyed profile as one
        // prism (exact — the keyed prism's partial-arc wall only ever meets planes,
        // since the set-screw combination is refused above; splitting it into a circle
        // prism plus a rectangle notch was tried and FAILS differently, the notch's
        // vertical corner line against the bore cylinder clipping to the TOOL's own
        // extent and stranding a traced curve inside the face — see todo.md).
        double height = faceWidth + boss.Projection + 2 * over;
        var basePlane = new SketchPlane(Frame3d.FromXY((0, 0, -over), Vector3d.UnitX, Vector3d.UnitY));
        var boreProfile = keyway is { } seat ? KeyedBore(boreDiameter, seat) : Sketch.Circle(boreDiameter / 2);
        return body.Subtract(Shape.Extrude(boreProfile, height, basePlane));
    }

    /// <summary>
    /// A helical gear solid via the twisted extrusion: the <see cref="Spur"/> profile is
    /// the TRANSVERSE section (so <see cref="GearSpec.Module"/> and the pressure angle
    /// are transverse values m_t, α_t; the normal module is m_t·cos β), twisted by
    /// faceWidth·tan β / r_pitch over the face width. Positive
    /// <paramref name="helixAngleDegrees"/> is a right-hand helix; mesh a pair with
    /// opposite hands on parallel axes.
    /// </summary>
    /// <remarks>
    /// Representation support follows the twisted extrusion: the mesh lowering is the
    /// section sweep (with the twist-matched profile subdivision), implicit wraps that
    /// mesh, and B-Rep is honestly Impossible — <c>Explain</c> reports it.
    /// </remarks>
    public static Shape HelicalGear(GearSpec spec, double faceWidth, double helixAngleDegrees,
        double boreDiameter = 0, KeywaySpec? keyway = null, LighteningSpec? lightening = null,
        double? fitTolerance = null, int? slices = null)
    {
        if (!(Math.Abs(helixAngleDegrees) < 60))
            throw new ArgumentOutOfRangeException(nameof(helixAngleDegrees),
                "Helix angle must lie strictly between -60 and 60 degrees.");
        var sketch = GearBlank(spec, faceWidth, boreDiameter, keyway, lightening, fitTolerance);
        if (helixAngleDegrees == 0)
            return Shape.Extrude(sketch, faceWidth);
        double twist = faceWidth * Math.Tan(helixAngleDegrees * Math.PI / 180) / (spec.PitchDiameter / 2);
        return Shape.Extrude(sketch, faceWidth, twist, scale: 1, plane: null, slices: slices);
    }

    private static Sketch GearBlank(GearSpec spec, double faceWidth, double boreDiameter,
        KeywaySpec? keyway, LighteningSpec? lightening, double? fitTolerance,
        bool includeBore = true)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(faceWidth > 0))
            throw new ArgumentOutOfRangeException(nameof(faceWidth));
        var profile = Spur(spec, fitTolerance);
        if (boreDiameter <= 0)
        {
            if (keyway is not null)
                throw new ArgumentOutOfRangeException(nameof(keyway),
                    "A keyway needs a bore to sit in; state a boreDiameter.");
            return profile.Sketch;
        }
        if (boreDiameter >= spec.RootDiameter)
            throw new ArgumentOutOfRangeException(nameof(boreDiameter),
                $"Bore diameter {boreDiameter:0.###} reaches the root circle (diameter {spec.RootDiameter:0.###}).");
        double innerClear = boreDiameter / 2;
        var blank = profile.Sketch;
        if (keyway is { } seat)
        {
            if (boreDiameter / 2 + seat.HubDepth >= spec.RootDiameter / 2)
                throw new ArgumentOutOfRangeException(nameof(keyway),
                    $"The keyway reaches radius {boreDiameter / 2 + seat.HubDepth:0.###}, into the " +
                    $"root circle (radius {spec.RootDiameter / 2:0.###}).");
            innerClear = boreDiameter / 2 + seat.HubDepth;
            if (includeBore)
                blank = blank.WithHole(KeyedBore(boreDiameter, seat));
        }
        else if (includeBore)
        {
            blank = blank.WithHole(Sketch.Circle(boreDiameter / 2));
        }
        if (lightening is { } holes)
        {
            double rootR = spec.RootDiameter / 2;
            // Null centres the ring midway between the bore's reach and the root.
            double circleR = (holes.CircleDiameter ?? innerClear + rootR) / 2;
            double d = holes.HoleDiameter;
            if (circleR - d / 2 <= innerClear)
                throw new ArgumentOutOfRangeException(nameof(lightening),
                    $"Ø{d:0.###} lightening holes on a Ø{2 * circleR:0.###} circle reach the bore " +
                    $"(clear radius {innerClear:0.###}).");
            if (circleR + d / 2 >= rootR)
                throw new ArgumentOutOfRangeException(nameof(lightening),
                    $"Ø{d:0.###} lightening holes on a Ø{2 * circleR:0.###} circle reach the root " +
                    $"circle (radius {rootR:0.###}).");
            double neighbourChord = 2 * circleR * Math.Sin(Math.PI / holes.Count);
            if (holes.Count > 1 && neighbourChord <= d)
                throw new ArgumentOutOfRangeException(nameof(lightening),
                    $"{holes.Count} Ø{d:0.###} holes on a Ø{2 * circleR:0.###} circle overlap each " +
                    $"other (neighbour spacing {neighbourChord:0.###}).");
            for (int k = 0; k < holes.Count; k++)
            {
                double angle = 2 * Math.PI * k / holes.Count;
                blank = blank.WithHole(Sketch.Circle(
                    new Vector2d(circleR * Math.Cos(angle), circleR * Math.Sin(angle)), d / 2));
            }
        }
        return blank;
    }



    /// <summary>
    /// A bore with a DIN 6885 parallel-key seat, as the hole profile a gear (or any hub)
    /// subtracts: the bore circle with a rectangular notch of the keyway's width reaching
    /// its hub depth t2 above the bore wall on the +Y centreline. The notch corners sit
    /// exactly ON the circle (at x = ±b/2, y = √(r² − b²/4)), so the profile is one major
    /// arc plus three lines — lines and an arc, exact in all three representations, and
    /// its area is closed form: πr² + b·(r + t2) − b·y_c/2 − r²·asin(b/(2r)).
    /// </summary>
    public static Sketch KeyedBore(double boreDiameter, KeywaySpec keyway)
    {
        double r = boreDiameter / 2;
        if (!(r > 0) || !double.IsFinite(r))
            throw new ArgumentOutOfRangeException(nameof(boreDiameter));
        if (keyway.Width >= boreDiameter)
            throw new ArgumentOutOfRangeException(nameof(keyway),
                $"A {keyway.Width:0.###} wide keyway does not fit a Ø{boreDiameter:0.###} bore.");
        double half = keyway.Width / 2;
        double chord = Math.Sqrt(r * r - half * half);
        double top = r + keyway.HubDepth;
        return Sketch.Start(half, chord)
            .LineTo((half, top))
            .LineTo((-half, top))
            .LineTo((-half, chord))
            .ArcThrough((0, -r), (half, chord))
            .Close();
    }

    // ------------------------------------------------------------------ involute math

    /// <summary>Point of the base-circle involute anchored at ray <paramref name="theta0"/>
    /// (unwrapping counter-clockwise) at roll angle <paramref name="t"/>.</summary>
    private static Vector2d InvolutePoint(double rb, double theta0, double t)
    {
        double u = theta0 + t;
        double cos = Math.Cos(u), sin = Math.Sin(u);
        return new Vector2d(rb * (cos + t * sin), rb * (sin - t * cos));
    }

    /// <summary>Unit tangent of the same involute (direction of increasing roll) — the
    /// radial direction of the base tangent point, exactly.</summary>
    private static Vector2d InvoluteTangent(double theta0, double t)
    {
        double u = theta0 + t;
        return new Vector2d(Math.Cos(u), Math.Sin(u));
    }

    /// <summary>
    /// Fits a tangent-continuous biarc chain to the involute over roll
    /// [<paramref name="tLow"/>, <paramref name="tTip"/>] by recursive bisection —
    /// the <c>BiArcFit</c> convention with EXACT endpoint tangents from the closed form
    /// instead of estimates. <paramref name="deviation"/> is measured against the
    /// closed form at 512 samples after the fit.
    /// </summary>
    private static List<Curve2d> FitFlank(double rb, double theta0, double tLow, double tTip,
        double tolerance, out double deviation)
    {
        var curves = new List<Curve2d>();
        Fit(tLow, tTip, 0);

        // Independent verification pass - the reported figure, denser than the
        // per-span acceptance samples.
        double worst = 0;
        for (int i = 0; i <= 512; i++)
        {
            var p = InvolutePoint(rb, theta0, tLow + (tTip - tLow) * i / 512);
            double best = double.PositiveInfinity;
            foreach (var curve in curves)
                best = Math.Min(best, curve.DistanceTo(p));
            worst = Math.Max(worst, best);
        }
        deviation = worst;
        return curves;

        void Fit(double a, double b, int depth)
        {
            if (depth > 48)
                throw new InvalidOperationException(
                    "Involute biarc fit did not converge - this is a bug, not a modelling error.");
            if (BiArcFit.TryFit(
                    InvolutePoint(rb, theta0, a), InvoluteTangent(theta0, a),
                    InvolutePoint(rb, theta0, b), InvoluteTangent(theta0, b),
                    out var biarc) == BiArcFitStatus.Success)
            {
                double worstInterior = -1;
                double worstT = (a + b) / 2;
                for (int i = 1; i < 32; i++)
                {
                    double t = a + (b - a) * i / 32;
                    double d = biarc!.DistanceTo(InvolutePoint(rb, theta0, t));
                    if (d > worstInterior)
                    {
                        worstInterior = d;
                        worstT = t;
                    }
                }
                if (worstInterior <= tolerance)
                {
                    AddPiece(biarc!.First);
                    AddPiece(biarc.Second);
                    return;
                }
                Fit(a, worstT, depth + 1);
                Fit(worstT, b, depth + 1);
                return;
            }

            double mid = (a + b) / 2;
            Fit(a, mid, depth + 1);
            Fit(mid, b, depth + 1);
        }

        void AddPiece(Curve2d piece)
        {
            // A biarc half can degenerate to (near) zero length at the joint; below the
            // weld tier it is no segment at all.
            double length = piece switch
            {
                Line2d line => (line.End - line.Start).Length,
                Arc2d arc => arc.Length,
                _ => double.PositiveInfinity,
            };
            if (length > 1e-9)
                curves.Add(piece);
        }
    }

    // ------------------------------------------------------------------ curve helpers

    /// <summary>Arc from <paramref name="from"/> to <paramref name="to"/> about
    /// <paramref name="center"/>, taking the shorter way round (every fillet here spans
    /// well under a half turn).</summary>
    private static Arc2d ArcBetween(in Vector2d center, double radius, in Vector2d from, in Vector2d to)
    {
        double a0 = Math.Atan2(from.Y - center.Y, from.X - center.X);
        double a1 = Math.Atan2(to.Y - center.Y, to.X - center.X);
        double sweep = a1 - a0;
        if (sweep > Math.PI)
            sweep -= 2 * Math.PI;
        else if (sweep < -Math.PI)
            sweep += 2 * Math.PI;
        return new Arc2d(center, radius, a0, sweep);
    }

    private static Curve2d MirrorX(Curve2d curve) => curve switch
    {
        Line2d line => new Line2d(new(line.Start.X, -line.Start.Y), new(line.End.X, -line.End.Y)),
        Arc2d arc => new Arc2d(new(arc.Center.X, -arc.Center.Y), arc.Radius, -arc.StartAngle, -arc.SweepAngle),
        _ => throw new InvalidOperationException($"Unexpected gear outline curve {curve.GetType().Name}."),
    };

    private static Curve2d ReverseCurve(Curve2d curve) => curve switch
    {
        Line2d line => new Line2d(line.End, line.Start),
        Arc2d arc => arc.Reversed(),
        _ => throw new InvalidOperationException($"Unexpected gear outline curve {curve.GetType().Name}."),
    };

    private static Curve2d Rotate(Curve2d curve, double cos, double sin, double angle)
    {
        Vector2d Rot(in Vector2d p) => new(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
        return curve switch
        {
            Line2d line => new Line2d(Rot(line.Start), Rot(line.End)),
            Arc2d arc => new Arc2d(Rot(arc.Center), arc.Radius, arc.StartAngle + angle, arc.SweepAngle),
            _ => throw new InvalidOperationException($"Unexpected gear outline curve {curve.GetType().Name}."),
        };
    }
}

/// <summary>
/// Web lightening: <see cref="Count"/> circular holes of <see cref="HoleDiameter"/>
/// evenly spaced on a bolt circle in a gear's web, between the bore (or the keyway's
/// reach) and the root circle. <see cref="CircleDiameter"/> null centres the ring
/// midway between the two — the web's own middle. Each hole removes exactly π·d²/4 of
/// blank area, which is what the tests hold the sketch to; holes that reach the bore,
/// the root circle or each other are refused by name where the blank is built.
/// </summary>
public readonly record struct LighteningSpec
{
    public LighteningSpec(int count, double holeDiameter, double? circleDiameter = null)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!(holeDiameter > 0) || !double.IsFinite(holeDiameter))
            throw new ArgumentOutOfRangeException(nameof(holeDiameter));
        if (circleDiameter is { } c && (!(c > 0) || !double.IsFinite(c)))
            throw new ArgumentOutOfRangeException(nameof(circleDiameter));
        Count = count;
        HoleDiameter = holeDiameter;
        CircleDiameter = circleDiameter;
    }

    /// <summary>How many holes ring the web.</summary>
    public int Count { get; }

    /// <summary>Each hole's diameter, mm.</summary>
    public double HoleDiameter { get; }

    /// <summary>The bolt circle's diameter, or null for the web's own middle.</summary>
    public double? CircleDiameter { get; }
}

/// <summary>
/// A gear HUB (set-screw boss): a cylinder proud of the web on the +Z face, gripping the
/// shaft the bore carries. The bore (keyway included) continues through it, and an
/// optional radial set-screw pilot crosses the hub wall on the +X side into the bore —
/// with a PLAIN bore only: a set screw beside a keyway is refused by name, since the
/// keyed bore's partial-arc wall against the pilot is a boolean pair the kernel
/// measurably misclassifies (wrong-but-closed; see todo.md). The pilot diameter is the
/// caller's (typically a tap drill from <see cref="StandardThreads"/>' chart); the
/// thread itself is not modelled, the <see cref="StandardHoles"/> convention.
/// </summary>
/// <param name="Diameter">The hub cylinder's diameter — must clear the bore (and its
/// keyway's reach) and stay inside the root circle.</param>
/// <param name="Projection">How far the boss stands proud of the web face.</param>
/// <param name="SetScrewDiameter">Radial pilot diameter, null = no set screw.</param>
/// <param name="SetScrewOffset">The pilot's axial position from the web face; null =
/// mid-projection.</param>
public readonly record struct GearHubSpec(
    double Diameter, double Projection, double? SetScrewDiameter = null, double? SetScrewOffset = null);
