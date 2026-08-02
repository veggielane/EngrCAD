using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A cycloidal drive (pin-wheel reducer) disc: a lobed plate riding an eccentric inside a
/// ring of pins, which rolls backwards one lobe per input revolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>The roller-centre curve is derived, not transcribed.</b> With N pins fixed at
/// <c>P_j = R·(cos α_j, sin α_j)</c>, the disc centre orbiting at <c>e·(cos φ, sin φ)</c> and
/// the disc turning at <c>ξ = λφ</c>, pin j sits in the disc's own frame at
/// <c>Rot(−λφ)(P_j − O(φ)) = R(cos s, sin s) − e(cos((N/1)(s − α_j)), …)</c> with
/// <c>s = α_j − λφ</c>. Taking <c>λ = −1/(N−1)</c> collapses that to
/// <c>C(s) = R(cos s, sin s) − e(cos Ns, sin Ns)</c> for EVERY pin at once, because
/// <c>N·α_j = 2πj</c> is a whole number of turns. Three things fall straight out of the
/// derivation rather than being asserted: the curve has <c>N − 1</c> lobes (its radius is
/// <c>√(R² + e² − 2Re·cos((N−1)s))</c>), its peak-to-valley depth is exactly <b>2e</b>, and
/// the disc <b>counter-rotates</b> at one revolution per <c>N − 1</c> input revolutions.
/// </para>
/// <para>
/// <b>The lobe difference must be 1, structurally.</b> Repeating the derivation for a general
/// difference d puts <c>(N/d)·α_j = 2πj/d</c> in the phase, which is a whole number of turns
/// for every pin only when d = 1 — otherwise the pins do not all ride one curve and no single
/// disc profile exists. A difference other than 1 is therefore refused by name rather than
/// approximated.
/// </para>
/// <para>
/// <b>The cut profile is the roller-centre curve offset by the pin radius</b>, and the offset
/// is taken through the curve's own exact normal rather than re-derived parametrically —
/// which also makes it free: an offset curve's unit tangent IS the base curve's
/// (<c>D′ = (1 − R_r·κ)·C′</c>), so the biarc fit gets exact tangents for nothing, and the
/// same factor states the validity condition — <c>R_r·κ_max &lt; 1</c>, i.e. the pin must be
/// smaller than the lobe tip's radius of curvature <c>(R + eN)²/(R + eN²)</c>, or the offset
/// cusps and the disc self-intersects.
/// </para>
/// <para>
/// v1 draws the lobe profile and an optional central bore. Output roller holes, clearance
/// (real drives cut the profile a few hundredths under to leave a running fit) and the
/// eccentric shaft are the caller's, and are filed follow-ups.
/// </para>
/// </remarks>
public sealed record CycloidalDiscSpec
{
    public CycloidalDiscSpec(int pins, double pinCircleRadius, double pinRadius,
        double eccentricity, int? lobes = null)
    {
        if (pins < 3)
            throw new ArgumentOutOfRangeException(nameof(pins), "A pin ring needs at least 3 pins.");
        if (!(pinCircleRadius > 0))
            throw new ArgumentOutOfRangeException(nameof(pinCircleRadius));
        if (!(pinRadius > 0))
            throw new ArgumentOutOfRangeException(nameof(pinRadius));
        if (!(eccentricity > 0))
            throw new ArgumentOutOfRangeException(nameof(eccentricity));
        int lobeCount = lobes ?? pins - 1;
        if (lobeCount != pins - 1)
            throw new ArgumentOutOfRangeException(nameof(lobes),
                $"A single cycloidal disc carries exactly one lobe fewer than the pin count "
                + $"({pins - 1} for {pins} pins), and {lobeCount} is not that. The restriction is "
                + "structural, not a v1 limit: with a lobe difference d the pin phase in the disc's "
                + "frame is 2*pi*j/d, which is a whole number of turns for every pin only at d = 1, "
                + "so for d > 1 the pins do not all ride one profile.");
        Pins = pins;
        PinCircleRadius = pinCircleRadius;
        PinRadius = pinRadius;
        Eccentricity = eccentricity;
    }

    /// <summary>Number of ring pins, N.</summary>
    public int Pins { get; }

    /// <summary>Radius R of the circle the pin CENTRES sit on.</summary>
    public double PinCircleRadius { get; }

    /// <summary>Radius R_r of a pin (roller).</summary>
    public double PinRadius { get; }

    /// <summary>Eccentricity e of the input shaft's offset.</summary>
    public double Eccentricity { get; }

    /// <summary>Lobes on the disc, N − 1.</summary>
    public int Lobes => Pins - 1;

    /// <summary>Angular pitch of one lobe in the curve's own parameter, 2π/(N−1).</summary>
    public double LobePeriod => 2 * Math.PI / Lobes;

    /// <summary>
    /// Reduction ratio with the PIN RING held and the output taken from the disc's own
    /// rotation (the usual single-stage arrangement): z_lobes/(z_pins − z_lobes) = N − 1. The
    /// output <b>counter-rotates</b> — see <see cref="DiscTurnsPerInputTurn"/>, which carries
    /// the sign so a caller cannot lose it.
    /// </summary>
    public double ReductionRatio => (double)Lobes / (Pins - Lobes);

    /// <summary>
    /// Reduction ratio of the other classic arrangement — the disc held against rotation and
    /// the output taken from the pin RING: z_pins/(z_pins − z_lobes) = N, co-rotating. Named
    /// separately because the two configurations give different numbers off the same geometry.
    /// </summary>
    public double RingOutputRatio => (double)Pins / (Pins - Lobes);

    /// <summary>Signed disc revolutions per input revolution: −1/(N−1). The sign is the trap —
    /// the disc turns BACKWARDS.</summary>
    public double DiscTurnsPerInputTurn => -1.0 / Lobes;

    /// <summary>The disc's rotation at input angle <paramref name="inputAngle"/> (radians).</summary>
    public double DiscRotation(double inputAngle) => -inputAngle / Lobes;

    /// <summary>The disc centre's position at input angle <paramref name="inputAngle"/>.</summary>
    public Vector2d DiscCentre(double inputAngle) =>
        new(Eccentricity * Math.Cos(inputAngle), Eccentricity * Math.Sin(inputAngle));

    /// <summary>Centre of pin <paramref name="index"/> (0 ≤ index &lt; <see cref="Pins"/>).</summary>
    public Vector2d PinCentre(int index)
    {
        double a = 2 * Math.PI * index / Pins;
        return new Vector2d(PinCircleRadius * Math.Cos(a), PinCircleRadius * Math.Sin(a));
    }

    /// <summary>
    /// Maps a world point into the disc's own frame at input angle
    /// <paramref name="inputAngle"/> — the one place the pose convention lives, so a clash
    /// sweep and a render cannot disagree about where the disc is.
    /// </summary>
    public Vector2d WorldToDisc(in Vector2d world, double inputAngle)
    {
        var local = world - DiscCentre(inputAngle);
        double xi = DiscRotation(inputAngle);
        double cos = Math.Cos(-xi), sin = Math.Sin(-xi);
        return new Vector2d(local.X * cos - local.Y * sin, local.X * sin + local.Y * cos);
    }

    /// <summary>Peak-to-valley depth of the lobes, exactly 2e — the eccentricity identity, and
    /// it survives the roller offset unchanged (both extremes move radially by R_r).</summary>
    public double LobeDepth => 2 * Eccentricity;

    /// <summary>Greatest radius of the cut profile, R + e − R_r.</summary>
    public double MaximumRadius => PinCircleRadius + Eccentricity - PinRadius;

    /// <summary>Least radius of the cut profile (the lobe valleys), R − e − R_r.</summary>
    public double MinimumRadius => PinCircleRadius - Eccentricity - PinRadius;

    /// <summary>Area enclosed by the roller-centre curve, π(R² + e²N) — closed form, since
    /// Green's integrand reduces to R² + e²N − eR(N+1)·cos((N−1)s) whose cosine integrates
    /// away over a full turn.</summary>
    public double RollerCentreCurveArea =>
        Math.PI * (PinCircleRadius * PinCircleRadius + Eccentricity * Eccentricity * Pins);

    /// <summary>
    /// Curvature of the roller-centre curve at a lobe TIP, (R + eN²)/(R + eN)² — where the
    /// curve is most convex, so its reciprocal is the largest pin the profile can take before
    /// the offset cusps.
    /// </summary>
    public double LobeTipCurvature
    {
        get
        {
            double s = PinCircleRadius + Eccentricity * Pins;
            return (PinCircleRadius + Eccentricity * Pins * Pins) / (s * s);
        }
    }

    /// <summary>
    /// The greatest curvature anywhere on the roller-centre curve. Both the curvature
    /// numerator and |C′|² depend on the parameter only through u = cos((N−1)s), so
    /// κ(u) = (A − Bu)/(P − Qu)^{3/2} is a smooth function of ONE variable with a single
    /// critical point in closed form — the maximum is exactly the largest of the two endpoints
    /// and that interior root, with no scan.
    /// </summary>
    public double MaximumCurvature
    {
        get
        {
            double r = PinCircleRadius, e = Eccentricity;
            int n = Pins;
            double a = r * r + e * e * n * n * n;
            double b = r * e * n * (n + 1);
            double p = r * r + e * e * n * n;
            double q = 2 * r * e * n;
            double best = Math.Max(Kappa(-1), Kappa(1));
            // dκ/du = 0 at u* = (3QA − 2BP)/(BQ); only an interior root can beat an endpoint.
            double critical = (3 * q * a - 2 * b * p) / (b * q);
            if (critical > -1 && critical < 1)
                best = Math.Max(best, Kappa(critical));
            return best;

            double Kappa(double u)
            {
                double denominator = p - q * u;
                return denominator > 0 ? (a - b * u) / (denominator * Math.Sqrt(denominator)) : double.NegativeInfinity;
            }
        }
    }

    /// <summary>The smallest radius of curvature on the roller-centre curve — the ceiling on
    /// <see cref="PinRadius"/>.</summary>
    public double MinimumCurvatureRadius => 1 / MaximumCurvature;
}

/// <summary>
/// A generated cycloidal disc outline: the <see cref="Sketch"/> (lines and circular arcs only,
/// so the disc is exact in all three representations downstream) plus the fit contract — the
/// tolerance that was asked for and the deviation that was measured.
/// </summary>
public sealed class CycloidalDiscProfile
{
    internal CycloidalDiscProfile(CycloidalDiscSpec spec, double offset, Sketch sketch,
        double fitTolerance, double maxFitDeviation, int curvesPerLobe,
        double rollerCentreCurveLength, double closedFormArea)
    {
        Spec = spec;
        Offset = offset;
        Sketch = sketch;
        FitTolerance = fitTolerance;
        MaxFitDeviation = maxFitDeviation;
        CurvesPerLobe = curvesPerLobe;
        RollerCentreCurveLength = rollerCentreCurveLength;
        ClosedFormArea = closedFormArea;
    }

    /// <summary>The drive this outline was generated from.</summary>
    public CycloidalDiscSpec Spec { get; }

    /// <summary>How far inside the roller-centre curve this outline sits: the pin radius for
    /// the cut disc, and exactly 0 for the roller-centre curve itself.</summary>
    public double Offset { get; }

    /// <summary>The outline as a closed sketch (CCW), centred on the disc's own axis.</summary>
    public Sketch Sketch { get; }

    /// <summary>The fit tolerance that was requested (mm).</summary>
    public double FitTolerance { get; }

    /// <summary>The measured maximum deviation of the fitted outline from the closed form (mm).</summary>
    public double MaxFitDeviation { get; }

    /// <summary>Curves the biarc chain spent on one lobe.</summary>
    public int CurvesPerLobe { get; }

    /// <summary>
    /// Arc length of the roller-centre curve. It is an elliptic integral with no elementary
    /// closed form, so it is quadrature — but the integrand is smooth and PERIODIC, where the
    /// uniform trapezoid rule converges exponentially, so 4096 samples reach round-off.
    /// </summary>
    public double RollerCentreCurveLength { get; }

    /// <summary>
    /// The EXACT area the ideal (un-fitted) outline encloses:
    /// <c>π(R² + e²N) − offset·L + π·offset²</c>. The first term is the roller-centre curve's
    /// own closed form and the rest is the standard inward-offset identity, which holds for any
    /// simple closed curve because its total turning is one full revolution.
    /// </summary>
    public double ClosedFormArea { get; }
}

/// <summary>
/// Cycloidal drive factory: <see cref="Disc"/> generates the lobe profile,
/// <see cref="RollerCentreCurve"/> the curve the pin CENTRES ride, and <see cref="DiscShape"/>
/// / <see cref="PinShapes"/> the solids.
/// </summary>
public static class CycloidalDrives
{
    /// <summary>
    /// The cut disc outline — the roller-centre curve offset inward by the pin radius — as a
    /// closed <see cref="Sketch"/> centred on the disc's own axis.
    /// </summary>
    /// <param name="spec">Pins, pin circle, pin radius and eccentricity.</param>
    /// <param name="fitTolerance">Maximum allowed deviation of the fitted outline from the
    /// closed form (mm). Defaults to (R + e)·1e-5.</param>
    /// <exception cref="ArgumentException">Refused by name: a pin circle too small for the
    /// eccentricity (R ≤ eN, where the curve's own tangent reverses), a pin larger than the
    /// lobe tip's radius of curvature (the offset would cusp and the disc self-intersect), and
    /// valleys that reach the disc's axis.</exception>
    public static CycloidalDiscProfile Disc(CycloidalDiscSpec spec, double? fitTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Build(spec, spec.PinRadius, fitTolerance);
    }

    /// <summary>
    /// The curve the pin CENTRES ride in the disc's own frame — the disc profile before the
    /// roller offset. Useful as the analysis curve: every pin lies on it at every input angle,
    /// which is the identity a clash sweep checks.
    /// </summary>
    public static CycloidalDiscProfile RollerCentreCurve(CycloidalDiscSpec spec, double? fitTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Build(spec, 0, fitTolerance);
    }

    /// <summary>
    /// The disc as a solid: the <see cref="Disc"/> outline extruded to
    /// <paramref name="thickness"/>, with an optional central bore for the eccentric bearing.
    /// </summary>
    public static Shape DiscShape(CycloidalDiscSpec spec, double thickness, double boreDiameter = 0,
        double? fitTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(thickness));
        var profile = Disc(spec, fitTolerance);
        var sketch = profile.Sketch;
        if (boreDiameter > 0)
        {
            if (boreDiameter >= 2 * spec.MinimumRadius)
                throw new ArgumentOutOfRangeException(nameof(boreDiameter),
                    $"Bore diameter {boreDiameter:0.###} reaches the lobe valleys "
                    + $"(diameter {2 * spec.MinimumRadius:0.###}).");
            sketch = sketch.WithHole(Sketch.Circle(boreDiameter / 2));
        }
        return Shape.Extrude(sketch, thickness);
    }

    /// <summary>The ring pins as solids, one cylinder per pin, in pin index order.</summary>
    public static IReadOnlyList<Shape> PinShapes(CycloidalDiscSpec spec, double length)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(length > 0))
            throw new ArgumentOutOfRangeException(nameof(length));
        var pins = new List<Shape>(spec.Pins);
        for (int j = 0; j < spec.Pins; j++)
        {
            var centre = spec.PinCentre(j);
            pins.Add(Shape.Cylinder(spec.PinRadius, length).Translate(centre.X, centre.Y, 0));
        }
        return pins;
    }

    // ------------------------------------------------------------------ construction

    private static CycloidalDiscProfile Build(CycloidalDiscSpec spec, double offset, double? fitTolerance)
    {
        double r = spec.PinCircleRadius, e = spec.Eccentricity;
        int n = spec.Pins;
        double tolerance = fitTolerance ?? (r + e) * 1e-5;
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(fitTolerance));

        // Regularity: |C'|^2 = R^2 + e^2 N^2 - 2ReN cos((N-1)s) vanishes exactly at R = eN, and
        // for R < eN the curve runs clockwise instead - both are outside what a disc means.
        if (!(r > e * n))
            throw new ArgumentException(
                $"The pin circle radius {r:0.####} must exceed e*N = {e * n:0.####}: at or below it "
                + "the roller-centre curve's own tangent stalls and reverses, so there is no disc "
                + "profile. Reduce the eccentricity or enlarge the pin circle.", nameof(spec));
        if (offset > 0)
        {
            double limit = spec.MinimumCurvatureRadius;
            if (!(offset < limit))
                throw new ArgumentException(
                    $"The pin radius {offset:0.####} is not under the roller-centre curve's smallest "
                    + $"radius of curvature {limit:0.####}: the offset would cusp and the disc "
                    + "profile self-intersect. Reduce the pin radius or the eccentricity.", nameof(spec));
            if (!(spec.MinimumRadius > 0))
                throw new ArgumentException(
                    $"The lobe valleys reach the disc axis (R - e - R_r = {spec.MinimumRadius:0.####}). "
                    + "Enlarge the pin circle or reduce the pin radius or the eccentricity.", nameof(spec));
        }

        Vector2d Point(double s)
        {
            var c = Centre(r, e, n, s);
            if (offset == 0)   // exact-zero semantic test: the roller-centre curve IS the offset at 0
                return c;
            var t = Tangent(r, e, n, s);
            return new Vector2d(c.X - offset * t.Y, c.Y + offset * t.X);
        }

        // An offset curve's unit tangent IS the base curve's, exactly - D' = (1 - R_r*k)*C' -
        // which the cusp guard above has already shown to be a positive multiple.
        Vector2d PointTangent(double s) => Tangent(r, e, n, s);

        double period = spec.LobePeriod;
        var lobe = Curve2dChains.Fit(Point, PointTangent, 0, period, tolerance, out double deviation);
        var outline = new List<Curve2d>(lobe.Count * spec.Lobes);
        for (int k = 0; k < spec.Lobes; k++)
        {
            double beta = k * period;
            double cos = Math.Cos(beta), sin = Math.Sin(beta);
            foreach (var piece in lobe)
                outline.Add(Curve2dChains.Rotate(piece, cos, sin, beta));
        }
        var sketch = Sketch.FromCurves(outline);

        double length = RollerCentreLength(r, e, n);
        double area = spec.RollerCentreCurveArea - offset * length + Math.PI * offset * offset;
        return new CycloidalDiscProfile(spec, offset, sketch, tolerance, deviation, lobe.Count,
            length, area);
    }

    /// <summary>The roller-centre curve C(s) = R(cos s, sin s) − e(cos Ns, sin Ns).</summary>
    private static Vector2d Centre(double r, double e, int n, double s) =>
        new(r * Math.Cos(s) - e * Math.Cos(n * s), r * Math.Sin(s) - e * Math.Sin(n * s));

    /// <summary>Exact unit tangent of the roller-centre curve.</summary>
    private static Vector2d Tangent(double r, double e, int n, double s)
    {
        var d = new Vector2d(
            -r * Math.Sin(s) + e * n * Math.Sin(n * s),
            r * Math.Cos(s) - e * n * Math.Cos(n * s));
        return d / d.Length;
    }

    /// <summary>
    /// Arc length of the roller-centre curve by the uniform trapezoid rule over a full turn.
    /// The integrand √(R² + e²N² − 2ReN·cos((N−1)s)) is smooth and periodic, where that rule
    /// converges EXPONENTIALLY rather than at second order, so 4096 samples reach round-off —
    /// which is why no adaptive quadrature is reached for.
    /// </summary>
    private static double RollerCentreLength(double r, double e, int n)
    {
        const int samples = 4096;
        double a = r * r + e * e * n * n;
        double b = 2 * r * e * n;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            double s = 2 * Math.PI * i / samples;
            sum += Math.Sqrt(a - b * Math.Cos((n - 1) * s));
        }
        return sum * 2 * Math.PI / samples;
    }
}
