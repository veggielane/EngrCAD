using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A cam's displacement law: follower lift as a function of cam angle, with exact
/// first and second derivatives — the solver's Jacobian and the acceleration analysis
/// consume the slope and curvature, so a law must know its own calculus (finite
/// differences are banned here for the mate solver's usual reason).
/// </summary>
public abstract class CamLaw
{
    /// <summary>The lift and its first two derivatives at a cam angle (radians;
    /// laws are 2π-periodic unless documented otherwise).</summary>
    public abstract void Evaluate(double angle, out double lift, out double slope, out double curvature);

    /// <summary>Simple harmonic motion: lift = (amplitude/2)·(1 − cos(lobes·θ)) —
    /// zero at θ = 0, peak amplitude at the lobe crest. The classic smooth cam law.</summary>
    public static CamLaw Harmonic(double amplitude, int lobes = 1)
    {
        if (lobes < 1)
            throw new ArgumentOutOfRangeException(nameof(lobes));
        return new FunctionCamLaw(
            angle => amplitude / 2 * (1 - Math.Cos(lobes * angle)),
            angle => amplitude / 2 * lobes * Math.Sin(lobes * angle),
            angle => amplitude / 2 * lobes * lobes * Math.Cos(lobes * angle))
        { Identity = ("harmonic", [amplitude, lobes]) };
    }

    /// <summary>No motion: the follower dwells. The building block a
    /// <see cref="Segments"/> profile spends most of its cycle in.</summary>
    public static CamLaw Dwell(double lift = 0) =>
        new FunctionCamLaw(_ => lift, static _ => 0, static _ => 0)
        { Identity = ("dwell", [lift]) };

    /// <summary>
    /// Constant velocity: lift = rate·θ. Not periodic, and that is the point — it is
    /// what makes <see cref="Coupling.RackAndPinion"/> a cam pair with a straight law.
    /// </summary>
    public static CamLaw Linear(double rate) =>
        new FunctionCamLaw(angle => rate * angle, _ => rate, static _ => 0)
        { Identity = ("linear", [rate]) };

    // ---- the dwell–rise–dwell catalogue ----
    //
    // Each of these is a RISE law over [0, span]: h(0) = 0, h(span) = rise, and the
    // catalogue exists because choosing between them is an engineering decision with a
    // known answer, not because the formulas are hard. What separates them is what
    // happens at the ENDS, where a rise meets a dwell:
    //
    //   * harmonic     — velocity is C0 but ACCELERATION jumps at both ends (a finite
    //                    step from 0), the classic source of cam noise. Kept because it
    //                    is the smallest peak velocity of the three.
    //   * cycloidal    — acceleration is zero at both ends, so it joins a dwell C2. The
    //                    default choice for high speed; the price is the highest peak
    //                    acceleration (2*pi*h/span^2 = 6.28 h/span^2).
    //   * mod-trapezoid— the standard compromise: sinusoidal ramps either side of a
    //                    constant-acceleration plateau, C2 at the dwells like the
    //                    cycloidal but with peak acceleration 4.888 h/span^2, ~22% lower.
    //
    // The peak accelerations above are what the tests assert, because they are the
    // reason to pick one, and a catalogue whose members are not actually different is
    // decoration.

    /// <summary>
    /// Cycloidal rise over <paramref name="span"/> radians of cam angle:
    /// h(θ) = rise·(θ/span − sin(2πθ/span)/2π). Acceleration is exactly ZERO at both
    /// ends, so it joins a dwell with C2 continuity — the high-speed default. Peak
    /// acceleration 2π·rise/span². Outside [0, span] the law holds its end values, so it
    /// composes into <see cref="Segments"/> directly.
    /// </summary>
    public static CamLaw Cycloidal(double rise, double span = Math.Tau)
    {
        RequireSpan(span);
        double w = Math.Tau / span;
        var law = Clamped(span, rise,
            angle => rise * (angle / span - Math.Sin(w * angle) / Math.Tau),
            angle => rise / span * (1 - Math.Cos(w * angle)),
            angle => rise / span * w * Math.Sin(w * angle));
        law.Identity = ("cycloidal", [rise, span]);
        return law;
    }

    /// <summary>
    /// Harmonic (simple-harmonic) rise over <paramref name="span"/>:
    /// h(θ) = (rise/2)(1 − cos(πθ/span)). The lowest peak velocity of the catalogue
    /// (π·rise/2span) — and the one whose ACCELERATION steps at both ends, so it belongs
    /// on slow or lightly loaded cams. Peak acceleration π²·rise/2span².
    /// </summary>
    public static CamLaw HarmonicRise(double rise, double span = Math.Tau)
    {
        RequireSpan(span);
        double w = Math.PI / span;
        var law = Clamped(span, rise,
            angle => rise / 2 * (1 - Math.Cos(w * angle)),
            angle => rise / 2 * w * Math.Sin(w * angle),
            angle => rise / 2 * w * w * Math.Cos(w * angle));
        law.Identity = ("harmonicRise", [rise, span]);
        return law;
    }

    /// <summary>
    /// Modified-trapezoid rise over <paramref name="span"/> — the industry compromise:
    /// the acceleration profile is a sine ramp up over the first eighth, a constant
    /// plateau over the middle half (split around the midpoint), and a sine ramp down
    /// over the last eighth, mirrored negative through the second half. C2 at both
    /// dwells like the cycloidal, with peak acceleration 4.8881·rise/span² against the
    /// cycloidal's 6.2832 — about 22% less, which is the whole reason it exists.
    /// </summary>
    public static CamLaw ModifiedTrapezoid(double rise, double span = Math.Tau)
    {
        RequireSpan(span);
        // Everything below is in the normalized angle x = θ/span at unit rise, scaled
        // at the end. The acceleration profile is the standard five pieces —
        //   [0, 1/8]   A·sin(4πx)          ramp up
        //   [1/8, 3/8] A                   plateau
        //   [3/8, 5/8] A·sin(4π(x − 1/4))  ramp through zero to −A
        //   [5/8, 7/8] −A
        //   [7/8, 1]   A·sin(4π(x − 1))    ramp back to 0
        // written here as its half plus the antisymmetry a(x) = −a(1 − x), which is
        // exactly the same function with three cases instead of five.
        //
        // A is NOT a tuning constant: it is fixed by requiring h(1) = 1, and integrating
        // the profile twice gives A(1/4π + 1/8) = 1, i.e. A = 8π/(2 + π) = 4.888124...
        // — the number the literature quotes as the modified trapezoid's peak
        // acceleration factor, derived rather than transcribed.
        const double A = 8 * Math.PI / (2 + Math.PI);
        const double C = A / (4 * Math.PI);                       // velocity at x = 1/8
        const double H1 = C * (0.125 - 1.0 / (4 * Math.PI));      // lift at x = 1/8
        const double H2 = H1 + C * 0.25 + A / 32;                 // lift at x = 3/8
        const double V3 = C + A * 0.25;                           // velocity at x = 3/8

        static double Accel(double x)
        {
            if (x > 0.5)
                return -Accel(1 - x);
            if (x < 0.125)
                return A * Math.Sin(4 * Math.PI * x);
            return x < 0.375 ? A : A * Math.Sin(4 * Math.PI * (x - 0.25));
        }

        static double Velocity(double x)
        {
            if (x > 0.5)
                return Velocity(1 - x);                            // v is symmetric
            if (x < 0.125)
                return C * (1 - Math.Cos(4 * Math.PI * x));
            if (x < 0.375)
                return C + A * (x - 0.125);
            return V3 - C * Math.Cos(4 * Math.PI * (x - 0.25));
        }

        static double Lift(double x)
        {
            if (x > 0.5)
                return 1 - Lift(1 - x);                            // point-symmetric about (1/2, 1/2)
            if (x < 0.125)
                return C * (x - Math.Sin(4 * Math.PI * x) / (4 * Math.PI));
            if (x < 0.375)
            {
                double d = x - 0.125;
                return H1 + C * d + A * d * d / 2;
            }
            return H2 + V3 * (x - 0.375)
                 - C * (Math.Sin(4 * Math.PI * (x - 0.25)) - 1) / (4 * Math.PI);
        }

        var law = Clamped(span, rise,
            angle => rise * Lift(angle / span),
            angle => rise / span * Velocity(angle / span),
            angle => rise / (span * span) * Accel(angle / span));
        law.Identity = ("modifiedTrapezoid", [rise, span]);
        return law;
    }

    /// <summary>
    /// Chains rise/dwell segments into one periodic cycle. Each segment spans an angle
    /// and starts where the previous one ended, so a dwell–rise–dwell–fall profile is
    /// written the way it is drawn:
    /// <code>
    /// CamLaw.Segments(
    ///     (Math.PI / 2, CamLaw.Dwell()),                    // 90 deg low dwell
    ///     (Math.PI / 2, CamLaw.Cycloidal(10, Math.PI / 2)), // rise 10
    ///     (Math.PI / 2, CamLaw.Dwell()),                    // 90 deg high dwell
    ///     (Math.PI / 2, CamLaw.Cycloidal(-10, Math.PI / 2)))// return
    /// </code>
    /// The result is 2π-periodic in the cam angle (spans need not add to 2π — they are
    /// SCALED to it, so a profile stated in degrees of its own cycle keeps its shape),
    /// and each segment is evaluated at its own local angle with the running lift added,
    /// so the chain's slope and curvature are the segments' own — no numerical
    /// differentiation anywhere.
    /// </summary>
    public static CamLaw Segments(params (double Span, CamLaw Law)[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0)
            throw new ArgumentException("A segmented cam law needs at least one segment.", nameof(segments));
        foreach (var (span, law) in segments)
        {
            ArgumentNullException.ThrowIfNull(law);
            RequireSpan(span);
        }
        return new SegmentedCamLaw(segments);
    }

    private static void RequireSpan(double span)
    {
        if (!(span > 0) || !double.IsFinite(span))
            throw new ArgumentOutOfRangeException(nameof(span), "A cam segment needs a positive angular span.");
    }

    /// <summary>A rise law clamped outside its own span: it holds 0 before and
    /// <paramref name="rise"/> after, with zero slope and curvature there. That is what
    /// makes a rise composable — a segment asked for its value past its end returns the
    /// dwell it becomes, rather than continuing a sine off into the next segment.</summary>
    private static FunctionCamLaw Clamped(
        double span, double rise,
        Func<double, double> lift, Func<double, double> slope, Func<double, double> curvature) =>
        new FunctionCamLaw(
            angle => angle <= 0 ? 0 : angle >= span ? rise : lift(angle),
            angle => angle <= 0 || angle >= span ? 0 : slope(angle),
            angle => angle <= 0 || angle >= span ? 0 : curvature(angle));

    /// <summary>A law from explicit lift/slope/curvature functions. The caller vouches
    /// that the derivatives are the lift's true calculus.</summary>
    public static CamLaw FromFunction(
        Func<double, double> lift, Func<double, double> slope, Func<double, double> curvature)
    {
        ArgumentNullException.ThrowIfNull(lift);
        ArgumentNullException.ThrowIfNull(slope);
        ArgumentNullException.ThrowIfNull(curvature);
        return new FunctionCamLaw(lift, slope, curvature);
    }

    /// <summary>
    /// The displacement law of a radial cam whose profile is a <see cref="Sketch"/>
    /// drawn about the cam's pivot (sketch origin), read by a radial point follower
    /// sitting at <paramref name="followerAngle"/> in the cam's construction pose.
    /// Radii are EXACT — the outermost boundary crossing of the sketch's exact signed
    /// distance along each ray, bisected to machine precision — and the law between
    /// samples is a C² periodic cubic spline in the cam angle, so slope and curvature
    /// are the interpolant's own calculus. <paramref name="samples"/> is the fidelity
    /// knob (error O(h⁴) in the sample spacing).
    /// </summary>
    public static CamLaw FromSketch(Sketch profile, double followerAngle = 0, int samples = 720) =>
        FromSketch(profile, CamFollower.Point(followerAngle), samples);

    /// <summary>
    /// <see cref="FromSketch(Sketch, double, int)"/> for a general translating
    /// follower: a roller of <see cref="CamFollower.RollerRadius"/> and/or a travel
    /// line offset from the pivot by <see cref="CamFollower.Offset"/>.
    /// <para><b>The roller compensation is exact, not the radial shortcut.</b> A roller
    /// centre traces the profile's PLANAR offset at the roller radius, and a planar
    /// offset is not a radial one — r(θ) + R is wrong by O(R·r′²/r²), worst exactly
    /// where the cam is steepest. No parametric offset curve is ever built: the sketch's
    /// signed distance is a true planar distance outside the profile, so the offset
    /// curve IS the isolevel sd = R, and the outermost crossing of that isolevel along
    /// the follower's travel line — the same outside-in march and bisection the point
    /// follower gets — is the roller centre exactly.</para>
    /// <para>The law's value is the follower centre's distance along its travel line
    /// from the foot of the pivot perpendicular (for the default radial line, the
    /// centre's distance from the pivot) — the same absolute convention as the point
    /// overload, so <see cref="Coupling.Cam"/> consumes either interchangeably.</para>
    /// </summary>
    public static CamLaw FromSketch(Sketch profile, CamFollower follower, int samples = 720)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(follower);
        if (samples < 8)
            throw new ArgumentOutOfRangeException(nameof(samples), "A sketch cam law needs at least 8 samples.");
        var region = new SketchRegion(profile);
        if (region.SignedDistance(Vector2d.Zero) >= 0)
            throw new ArgumentException(
                "A radial cam profile must contain its pivot (the sketch origin) — the origin is not " +
                "inside this sketch.", nameof(profile));

        var bounds = region.Bounds;
        double reach = 0;
        foreach (var corner in new[]
        {
            new Vector2d(bounds.Min.X, bounds.Min.Y), new Vector2d(bounds.Min.X, bounds.Max.Y),
            new Vector2d(bounds.Max.X, bounds.Min.Y), new Vector2d(bounds.Max.X, bounds.Max.Y),
        })
            reach = Math.Max(reach, corner.Length);

        var lifts = new double[samples];
        double h = 2 * Math.PI / samples;
        for (int k = 0; k < samples; k++)
        {
            // The profile rotates with the cam: at cam angle θ the follower reads the
            // profile's local direction followerAngle − θ. Exact-zero semantic test on
            // the offset: a radial follower keeps the incumbent ray arithmetic
            // bit-for-bit (reach + 0.0 and an isolevel of 0.0 change no bits).
            double local = follower.Angle - k * h;
            lifts[k] = follower.Offset == 0
                ? Radius(region, local, reach + follower.RollerRadius, follower.RollerRadius)
                : OffsetLift(region, local, follower, reach);
        }
        return new SplineCamLaw(lifts);
    }

    /// <summary>
    /// The pressure angle of a translating follower riding this law at
    /// <paramref name="angle"/> — the angle between the follower's travel direction and
    /// the contact normal, the number a designer offsets the follower to improve.
    /// Signed: tan φ = (slope − offset) / (value + <paramref name="baseDistance"/>),
    /// from the instant-centre construction with the cam angle increasing
    /// counterclockwise, so a POSITIVE <see cref="CamFollower.Offset"/> reduces the
    /// rise-side pressure angle (the textbook relation; the sign flips on the return).
    /// <para>The denominator must be the follower centre's distance along its travel
    /// line from the foot of the pivot perpendicular. A <see cref="FromSketch(Sketch,
    /// CamFollower, int)"/> law already IS that distance (leave
    /// <paramref name="baseDistance"/> at 0); a zero-based rise law from the catalogue
    /// needs the prime-circle geometry added: baseDistance = √(Rp² − offset²).</para>
    /// <para>The roller radius does not enter: the contact normal passes through the
    /// roller's centre whatever its radius, so φ is the centre's, exactly.</para>
    /// </summary>
    public double PressureAngle(double angle, CamFollower follower, double baseDistance = 0)
    {
        ArgumentNullException.ThrowIfNull(follower);
        Evaluate(angle, out double lift, out double slope, out _);
        double distance = lift + baseDistance;
        if (!(distance > 0))
            throw new InvalidOperationException(
                $"The pressure angle needs the follower centre's distance from the pivot foot, but this " +
                $"law's value plus baseDistance is {distance:g4} at angle {angle:g4}. For a zero-based " +
                "rise law over a prime circle, pass baseDistance = sqrt(primeRadius^2 - offset^2).");
        return Math.Atan2(slope - follower.Offset, distance);
    }

    /// <summary>The OUTERMOST crossing of the isolevel sd = <paramref name="isolevel"/>
    /// along a ray from the pivot: a coarse outside-in march finds the first sample
    /// inside the (dilated) profile, then bisection on the exact signed distance pins
    /// the crossing. Isolevel 0 is the profile itself (a point follower); a roller
    /// radius dilates it exactly, since the signed distance is a true planar distance
    /// outside the profile.</summary>
    private static double Radius(SketchRegion region, double angle, double reach, double isolevel)
    {
        var direction = new Vector2d(Math.Cos(angle), Math.Sin(angle));
        const int coarse = 1024;
        double step = reach / coarse;
        double outside = reach;
        double inside = double.NaN;
        for (int i = 1; i <= coarse; i++)
        {
            double r = reach - i * step;
            if (region.SignedDistance(direction * r) < isolevel)
            {
                inside = r;
                outside = r + step;
                break;
            }
        }
        if (double.IsNaN(inside))
            throw new ArgumentException(
                $"The cam profile has no material along the ray at {angle:g4} rad — a radial cam " +
                "profile must surround its pivot at every angle." +
                (isolevel > 0 ? $" (The profile is read grown by the roller radius {isolevel:g4}.)" : ""));
        for (int i = 0; i < 100; i++)
        {
            double middle = 0.5 * (inside + outside);
            if (region.SignedDistance(direction * middle) < isolevel)
                inside = middle;
            else
                outside = middle;
        }
        return 0.5 * (inside + outside);
    }

    /// <summary>
    /// <see cref="Radius"/> for an OFFSET travel line: the follower centre moves along
    /// p(s) = s·t̂ + offset·n̂ (n̂ the right normal of the travel direction), mapped
    /// into the cam's local frame at this angle, and the outermost crossing of
    /// sd = roller radius along it is the centre's distance s from the perpendicular
    /// foot. Same march, same bisection — a different line, not a different algorithm.
    /// </summary>
    private static double OffsetLift(SketchRegion region, double localAngle, CamFollower follower, double reach)
    {
        var travel = new Vector2d(Math.Cos(localAngle), Math.Sin(localAngle));
        // Offset is positive to the RIGHT of the travel direction (see CamFollower) —
        // the convention that makes a positive offset reduce the rise-side pressure
        // angle for a counterclockwise cam, i.e. the textbook one.
        var origin = new Vector2d(travel.Y, -travel.X) * follower.Offset;
        double start = reach + follower.RollerRadius;
        const int coarse = 1024;
        double step = start / coarse;
        double outside = start;
        double inside = double.NaN;
        for (int i = 1; i <= coarse; i++)
        {
            double s = start - i * step;
            if (region.SignedDistance(origin + travel * s) < follower.RollerRadius)
            {
                inside = s;
                outside = s + step;
                break;
            }
        }
        if (double.IsNaN(inside))
            throw new ArgumentException(
                $"The cam profile has no material along the follower's travel line at cam-local angle " +
                $"{localAngle:g4} rad (offset {follower.Offset:g4}" +
                (follower.RollerRadius > 0 ? $", roller radius {follower.RollerRadius:g4}" : "") +
                ") — an offset follower's line must meet the profile at every cam angle.");
        for (int i = 0; i < 100; i++)
        {
            double middle = 0.5 * (inside + outside);
            if (region.SignedDistance(origin + travel * middle) < follower.RollerRadius)
                inside = middle;
            else
                outside = middle;
        }
        return 0.5 * (inside + outside);
    }
}

/// <summary>
/// A translating cam follower's geometry, for <see cref="CamLaw.FromSketch(Sketch,
/// CamFollower, int)"/> and <see cref="CamLaw.PressureAngle"/>: the travel direction's
/// angle in the cam's construction pose, the roller radius (0 = a point/knife-edge
/// follower), and the travel line's signed perpendicular offset from the cam pivot.
/// <para><b>Offset sign convention, stated once and shared by placement and
/// analysis</b>: positive is to the RIGHT of the travel direction (looking along it,
/// with the cam angle increasing counterclockwise) — the choice that makes the
/// textbook pressure-angle relation tan φ = (v − offset)/s hold with a positive
/// offset reducing the rise-side pressure angle.</para>
/// </summary>
public sealed class CamFollower
{
    private CamFollower(double angle, double rollerRadius, double offset)
    {
        Angle = angle;
        RollerRadius = rollerRadius;
        Offset = offset;
    }

    /// <summary>The travel direction's angle (radians) in the cam's construction pose.</summary>
    public double Angle { get; }

    /// <summary>The roller radius; 0 is a point (knife-edge) follower.</summary>
    public double RollerRadius { get; }

    /// <summary>The travel line's signed perpendicular offset from the pivot
    /// (positive = right of the travel direction; see the class notes).</summary>
    public double Offset { get; }

    /// <summary>A point (knife-edge) follower, optionally on an offset travel line.</summary>
    public static CamFollower Point(double angle = 0, double offset = 0) =>
        Create(angle, 0, offset);

    /// <summary>A roller follower of <paramref name="radius"/>, optionally on an
    /// offset travel line. Radius 0 is exactly <see cref="Point"/>.</summary>
    public static CamFollower Roller(double radius, double angle = 0, double offset = 0) =>
        Create(angle, radius, offset);

    private static CamFollower Create(double angle, double rollerRadius, double offset)
    {
        if (!double.IsFinite(angle))
            throw new ArgumentOutOfRangeException(nameof(angle), "A follower needs a finite travel angle.");
        if (!(rollerRadius >= 0) || !double.IsFinite(rollerRadius))
            throw new ArgumentOutOfRangeException(nameof(rollerRadius),
                "A roller radius must be finite and non-negative (0 = a point follower).");
        if (!double.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset), "A follower offset must be finite.");
        return new CamFollower(angle, rollerRadius, offset);
    }

    public override string ToString() =>
        (RollerRadius > 0 ? $"roller r={RollerRadius:g4}" : "point follower") +
        (Offset != 0 ? $" offset {Offset:g4}" : "") +
        (Angle != 0 ? $" at {Angle:g4} rad" : "");
}

internal sealed class FunctionCamLaw(
    Func<double, double> lift, Func<double, double> slope, Func<double, double> curvature) : CamLaw
{
    /// <summary>Which catalogue factory built this law and with what arguments — the
    /// <c>Feature.SaveInputs</c> convention, so mechanism persistence re-runs the same
    /// deterministic constructor. Null = an opaque lambda law
    /// (<see cref="CamLaw.FromFunction"/>), which saves a marker and loads as a warning.</summary>
    public (string Kind, double[] Args)? Identity { get; set; }

    public override void Evaluate(double angle, out double liftValue, out double slopeValue, out double curvatureValue)
    {
        liftValue = lift(angle);
        slopeValue = slope(angle);
        curvatureValue = curvature(angle);
    }
}

/// <summary>
/// A cycle assembled from rise/dwell segments. The segments' spans are SCALED to fill
/// 2π (a profile stated in degrees of its own cycle keeps its shape), the cam angle is
/// wrapped into the cycle, and each segment is evaluated at its own local angle with the
/// preceding segments' total lift added — so the chain's slope and curvature are the
/// segments' own analytic derivatives, never a difference of the assembled lift.
/// <para>Continuity across a joint is the SEGMENTS' business, not this class's: a
/// cycloidal or modified-trapezoid rise ends with zero slope and curvature, so it meets
/// a dwell C2, while a harmonic rise leaves an acceleration step. Smoothing that over
/// here would hide the very property the catalogue exists to let a designer choose.</para>
/// </summary>
internal sealed class SegmentedCamLaw : CamLaw
{
    private readonly CamLaw[] _laws;
    private readonly double[] _declared;   // the span each law was authored against
    private readonly double[] _start;      // cycle angle each segment begins at (length n + 1)
    private readonly double[] _offset;     // accumulated lift each segment begins at
    private readonly double _scale;        // cycle angle per declared angle
    private readonly double _cycleLift;

    public SegmentedCamLaw((double Span, CamLaw Law)[] segments)
    {
        int n = segments.Length;
        _laws = new CamLaw[n];
        _declared = new double[n];
        _start = new double[n + 1];
        _offset = new double[n + 1];
        _scale = Math.Tau / segments.Sum(s => s.Span);
        for (int i = 0; i < n; i++)
        {
            _laws[i] = segments[i].Law;
            _declared[i] = segments[i].Span;
            _start[i + 1] = _start[i] + segments[i].Span * _scale;
            // A segment contributes its own net rise; laws are written from a zero
            // start, so the delta across the declared span is what accumulates.
            segments[i].Law.Evaluate(0, out double begin, out _, out _);
            segments[i].Law.Evaluate(segments[i].Span, out double end, out _, out _);
            _offset[i + 1] = _offset[i] + (end - begin);
        }
        _start[n] = Math.Tau;              // exactly, whatever the sum rounded to
        _cycleLift = _offset[n];
    }

    /// <summary>The segments as authored (declared spans + laws) — persistence re-runs
    /// <see cref="CamLaw.Segments"/> with exactly these, so the chain rebuilds through
    /// the same deterministic constructor.</summary>
    public (double Span, CamLaw Law)[] SavedSegments
    {
        get
        {
            var segments = new (double, CamLaw)[_laws.Length];
            for (int i = 0; i < _laws.Length; i++)
                segments[i] = (_declared[i], _laws[i]);
            return segments;
        }
    }

    public override void Evaluate(double angle, out double lift, out double slope, out double curvature)
    {
        // Whole cycles carry their net lift, so a chain that does NOT return to its
        // start stays monotone in the UNWRAPPED cam angle instead of sawtoothing — the
        // cam coupling drives on the unwrapped angle, so this is the honest reading.
        double turns = Math.Floor(angle / Math.Tau);
        double local = angle - turns * Math.Tau;

        int index = _laws.Length - 1;
        for (int i = 0; i < _laws.Length; i++)
        {
            if (local < _start[i + 1])
            {
                index = i;
                break;
            }
        }

        _laws[index].Evaluate((local - _start[index]) / _scale, out double value, out slope, out curvature);
        _laws[index].Evaluate(0, out double begin, out _, out _);
        lift = turns * _cycleLift + _offset[index] + (value - begin);
        slope /= _scale;
        curvature /= _scale * _scale;
    }
}

/// <summary>A C² periodic cubic spline on uniform angle knots over [0, 2π) — the
/// second-derivative (M) form, with the cyclic tridiagonal moment system solved once
/// at construction by Thomas plus a Sherman–Morrison corner correction.</summary>
internal sealed class SplineCamLaw : CamLaw
{
    private readonly double[] _values;
    private readonly double[] _moments;
    private readonly double _spacing;

    /// <summary>The sampled lifts the spline interpolates — the law's DATA, which is
    /// how a <see cref="CamLaw.FromSketch(Sketch, CamFollower, int)"/> law persists
    /// (the sketch itself need not round-trip: the law IS the samples, and rebuilding
    /// the spline from them is deterministic, so the moments come back bit-identical).</summary>
    public IReadOnlyList<double> Values => _values;

    public SplineCamLaw(double[] values)
    {
        _values = values;
        int n = values.Length;
        _spacing = 2 * Math.PI / n;
        double h = _spacing;

        // Moment system: (h/6)M_{i−1} + (2h/3)M_i + (h/6)M_{i+1} = Δ²y_i / h, cyclic.
        var rhs = new double[n];
        for (int i = 0; i < n; i++)
        {
            double previous = values[(i + n - 1) % n];
            double next = values[(i + 1) % n];
            rhs[i] = (next - 2 * values[i] + previous) / h;
        }
        _moments = SolveCyclic(h / 6, 2 * h / 3, h / 6, rhs);
    }

    public override void Evaluate(double angle, out double lift, out double slope, out double curvature)
    {
        int n = _values.Length;
        double h = _spacing;
        double wrapped = angle % (2 * Math.PI);
        if (wrapped < 0)
            wrapped += 2 * Math.PI;
        int span = Math.Min((int)(wrapped / h), n - 1);
        double t = wrapped - span * h;           // distance into the span
        double u = h - t;                        // distance to the span's far knot
        double y0 = _values[span];
        double y1 = _values[(span + 1) % n];
        double m0 = _moments[span];
        double m1 = _moments[(span + 1) % n];

        lift = m0 * u * u * u / (6 * h) + m1 * t * t * t / (6 * h)
             + (y0 / h - m0 * h / 6) * u + (y1 / h - m1 * h / 6) * t;
        slope = -m0 * u * u / (2 * h) + m1 * t * t / (2 * h)
              - (y0 / h - m0 * h / 6) + (y1 / h - m1 * h / 6);
        curvature = m0 * u / h + m1 * t / h;
    }

    /// <summary>Cyclic constant-coefficient tridiagonal solve (sub a, diag b, super c,
    /// wrap corners a and c) — Thomas on a rank-one-modified system plus the
    /// Sherman–Morrison correction.</summary>
    private static double[] SolveCyclic(double a, double b, double c, double[] rhs)
    {
        int n = rhs.Length;
        double gamma = -b;
        var diagonal = new double[n];
        Array.Fill(diagonal, b);
        diagonal[0] = b - gamma;
        diagonal[n - 1] = b - a * c / gamma;

        var x = SolveTridiagonal(a, diagonal, c, rhs);
        var u = new double[n];
        u[0] = gamma;
        u[n - 1] = c;
        var z = SolveTridiagonal(a, diagonal, c, u);

        double factor = (x[0] + a * x[n - 1] / gamma) / (1 + z[0] + a * z[n - 1] / gamma);
        for (int i = 0; i < n; i++)
            x[i] -= factor * z[i];
        return x;
    }

    private static double[] SolveTridiagonal(double a, double[] diagonal, double c, double[] rhs)
    {
        int n = rhs.Length;
        var scratch = new double[n];
        var x = new double[n];
        double beta = diagonal[0];
        x[0] = rhs[0] / beta;
        for (int i = 1; i < n; i++)
        {
            scratch[i] = c / beta;
            beta = diagonal[i] - a * scratch[i];
            x[i] = (rhs[i] - a * x[i - 1]) / beta;
        }
        for (int i = n - 2; i >= 0; i--)
            x[i] -= scratch[i + 1] * x[i + 1];
        return x;
    }
}

/// <summary>
/// A scalar coupling between two joints' coordinates — the higher pairs. A gear ratio
/// is θ₂ = ∓(N₁/N₂)·θ₁ between two spin coordinates, a belt the same with pitch
/// radii, a cam a displacement law between a spin and a slide. Each is ONE residual
/// row beside the geometric mates (no new solver machinery), always expressed on the
/// coordinates' CHANGE since the coupling was built, so arbitrary construction poses
/// never enter.
/// </summary>
public sealed class Coupling
{
    private Coupling(AuxiliaryConstraint constraint, IReadOnlyList<AxisJoint> joints)
    {
        Constraint = constraint;
        Joints = joints;
    }

    internal AuxiliaryConstraint Constraint { get; }

    /// <summary>Which factory built this coupling and with what arguments — the
    /// <c>Feature.SaveInputs</c> convention for mechanism persistence. Each public
    /// factory stamps its OWN record (a gear is saved as a gear, not as the ratio it
    /// computed; a rack and pinion as itself, not the straight-law cam it is built as),
    /// so a load re-runs the same factory with the same arguments.</summary>
    internal CouplingData? SaveData { get; set; }

    /// <summary>The joints this coupling ties together (validation and reporting).</summary>
    public IReadOnlyList<AxisJoint> Joints { get; }

    /// <summary>Diagnostic label.</summary>
    public string Name => Constraint.Name;

    /// <summary>An external gear mesh: Δθ_b = −(teethA/teethB)·Δθ_a (meshed gears
    /// counter-rotate); <paramref name="internalMesh"/> (a ring gear) makes the ratio
    /// positive. Both joints must have free spin.</summary>
    public static Coupling Gear(
        AxisJoint a, AxisJoint b, double teethA, double teethB, bool internalMesh = false, string? name = null)
    {
        if (teethA <= 0 || teethB <= 0)
            throw new ArgumentOutOfRangeException(nameof(teethA), "Gear tooth counts must be positive.");
        double ratio = (internalMesh ? 1 : -1) * teethA / teethB;
        var coupling = Ratio(a, b, ratio, name ?? $"gear {teethA:g0}:{teethB:g0}");
        coupling.SaveData = new CouplingData("gear", [teethA, teethB, internalMesh ? 1 : 0], null);
        return coupling;
    }

    /// <summary>An open belt or chain drive: Δθ_b = (radiusA/radiusB)·Δθ_a (both
    /// pulleys turn the same way); <paramref name="crossed"/> flips the sense.</summary>
    public static Coupling Belt(
        AxisJoint a, AxisJoint b, double radiusA, double radiusB, bool crossed = false, string? name = null)
    {
        if (radiusA <= 0 || radiusB <= 0)
            throw new ArgumentOutOfRangeException(nameof(radiusA), "Belt pitch radii must be positive.");
        double ratio = (crossed ? -1 : 1) * radiusA / radiusB;
        var coupling = Ratio(a, b, ratio, name ?? $"belt {radiusA:g4}:{radiusB:g4}");
        coupling.SaveData = new CouplingData("belt", [radiusA, radiusB, crossed ? 1 : 0], null);
        return coupling;
    }

    /// <summary>The raw scalar spin coupling Δθ_b = ratio·Δθ_a.</summary>
    public static Coupling Ratio(AxisJoint a, AxisJoint b, double ratio, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        RequireFreeSpin(a);
        RequireFreeSpin(b);
        if (ReferenceEquals(a, b))
            throw new ArgumentException("A ratio coupling needs two different joints.", nameof(b));
        return new Coupling(new RatioCoupling(a, b, ratio, name ?? $"ratio {ratio:g4}"), [a, b])
        { SaveData = new CouplingData("ratio", [ratio], null) };
    }

    /// <summary>
    /// A rack and pinion: Δz_rack = pitchRadius·Δθ_pinion. Sign follows the joints'
    /// own axes — reverse the rack's prismatic axis (or negate the radius) to flip it.
    /// <para>It IS a cam pair with a straight law, and is built as one deliberately
    /// rather than as a fourth constraint class: <see cref="CamCoupling"/> already ties
    /// a slide to an UNWRAPPED spin through a law's exact slope and curvature, which is
    /// precisely the rack relation with a constant slope. The screw joint's own pitch
    /// row is the same relation once more, but confined to ONE joint's two coordinates;
    /// what a rack needs is that row spanning two joints, which is what this is.</para>
    /// </summary>
    public static Coupling RackAndPinion(
        AxisJoint pinion, AxisJoint rack, double pitchRadius, string? name = null)
    {
        if (!double.IsFinite(pitchRadius) || pitchRadius == 0)   // exact-zero: a zero radius transmits nothing
            throw new ArgumentOutOfRangeException(nameof(pitchRadius),
                "A rack and pinion needs a non-zero pitch radius.");
        var coupling = Cam(pinion, rack, CamLaw.Linear(pitchRadius),
            name ?? $"rack and pinion r={pitchRadius:g4}");
        coupling.SaveData = new CouplingData("rackAndPinion", [pitchRadius], null);
        return coupling;
    }

    /// <summary>A cam-follower pair: the follower's slide tracks
    /// <paramref name="law"/> of the cam's spin — Δz_follower = law(θ̂_cam) −
    /// law(θ̂_cam at construction).</summary>
    public static Coupling Cam(AxisJoint cam, AxisJoint follower, CamLaw law, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(cam);
        ArgumentNullException.ThrowIfNull(follower);
        ArgumentNullException.ThrowIfNull(law);
        RequireFreeSpin(cam);
        if (follower is RevoluteJoint or FixedJoint)
            throw new ArgumentException(
                $"Joint '{follower.Name}' pins its slide, so it cannot be a cam follower — use a " +
                "prismatic or cylindrical joint.", nameof(follower));
        return new Coupling(new CamCoupling(cam, follower, law, name ?? "cam"), [cam, follower])
        { SaveData = new CouplingData("cam", [], law) };
    }

    private static void RequireFreeSpin(AxisJoint joint)
    {
        if (joint is PrismaticJoint or FixedJoint)
            throw new ArgumentException(
                $"Joint '{joint.Name}' locks its spin, so a spin coupling cannot drive it — use a " +
                "revolute, cylindrical, or screw joint.");
    }
}

/// <summary>A coupling's construction record: which factory, its scalar arguments, and
/// (for a cam) the law. What mechanism persistence writes and re-runs.</summary>
internal sealed record CouplingData(string Kind, double[] Args, CamLaw? Law);

/// <summary>Δθ_b = ratio·Δθ_a as one residual row, angles unwrapped through each
/// joint's sweep state and measured from the coupling's OWN construction pose.</summary>
internal sealed class RatioCoupling : AuxiliaryConstraint
{
    private readonly AxisJoint _a;
    private readonly AxisJoint _b;
    private readonly double _ratio;
    private readonly double _zeroA;
    private readonly double _zeroB;

    public RatioCoupling(AxisJoint a, AxisJoint b, double ratio, string name)
    {
        _a = a;
        _b = b;
        _ratio = ratio;
        Name = name;
        _zeroA = a.Angle;
        _zeroB = b.Angle;
        Ends = [a.A, a.ReferenceA, a.ReferenceB, b.A, b.ReferenceA, b.ReferenceB];
    }

    public override string Name { get; }

    public override int RowCount => 1;

    public override IReadOnlyList<MateRef> Ends { get; }

    public override void Residual(ReadOnlySpan<EndValue> ends, double length, Span<double> rows)
    {
        double thetaA = _a.State.Unwrapped(JointArithmetic.Angle(ends[0], ends[1], ends[2]));
        double thetaB = _b.State.Unwrapped(JointArithmetic.Angle(ends[3], ends[4], ends[5]));
        rows[0] = ((thetaB - _zeroB) - _ratio * (thetaA - _zeroA)) * length;
    }

    public override void Derivative(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndDelta> deltas, double length, Span<double> rows)
    {
        double dThetaA = JointArithmetic.AngleDelta(ends[0], ends[1], ends[2], deltas[0], deltas[1], deltas[2]);
        double dThetaB = JointArithmetic.AngleDelta(ends[3], ends[4], ends[5], deltas[3], deltas[4], deltas[5]);
        rows[0] = (dThetaB - _ratio * dThetaA) * length;
    }

    public override void SecondOrder(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndMotion> motion, double length, Span<double> rows) =>
        rows[0] = (JointArithmetic.AngleSecond(motion[3], motion[4], motion[5])
                 - _ratio * JointArithmetic.AngleSecond(motion[0], motion[1], motion[2])) * length;
}

/// <summary>Δz_follower = law(θ̂_cam) − law(θ̂₀) as one residual row; the Jacobian
/// carries the law's slope and the acceleration term its curvature.</summary>
internal sealed class CamCoupling : AuxiliaryConstraint
{
    private readonly AxisJoint _cam;
    private readonly AxisJoint _follower;
    private readonly CamLaw _law;
    private readonly double _zeroLift;
    private readonly double _zeroDisplacement;

    public CamCoupling(AxisJoint cam, AxisJoint follower, CamLaw law, string name)
    {
        _cam = cam;
        _follower = follower;
        _law = law;
        Name = name;
        law.Evaluate(cam.Angle, out _zeroLift, out _, out _);
        _zeroDisplacement = follower.Displacement;
        Ends = [cam.A, cam.ReferenceA, cam.ReferenceB, follower.A, follower.B];
    }

    public override string Name { get; }

    public override int RowCount => 1;

    public override IReadOnlyList<MateRef> Ends { get; }

    public override void Residual(ReadOnlySpan<EndValue> ends, double length, Span<double> rows)
    {
        double theta = _cam.State.Unwrapped(JointArithmetic.Angle(ends[0], ends[1], ends[2]));
        double displacement =
            JointArithmetic.Slide(ends[3], ends[4]) - _follower.State.ReferenceSlide - _zeroDisplacement;
        _law.Evaluate(theta, out double lift, out _, out _);
        rows[0] = displacement - (lift - _zeroLift);
    }

    public override void Derivative(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndDelta> deltas, double length, Span<double> rows)
    {
        double theta = _cam.State.Unwrapped(JointArithmetic.Angle(ends[0], ends[1], ends[2]));
        _law.Evaluate(theta, out _, out double slope, out _);
        double dTheta = JointArithmetic.AngleDelta(ends[0], ends[1], ends[2], deltas[0], deltas[1], deltas[2]);
        double dz = JointArithmetic.SlideDelta(ends[3], ends[4], deltas[3], deltas[4]);
        rows[0] = dz - slope * dTheta;
    }

    public override void SecondOrder(
        ReadOnlySpan<EndValue> ends, ReadOnlySpan<EndMotion> motion, double length, Span<double> rows)
    {
        double theta = _cam.State.Unwrapped(JointArithmetic.Angle(ends[0], ends[1], ends[2]));
        _law.Evaluate(theta, out _, out double slope, out double curvature);
        double thetaRate = JointArithmetic.AngleRate(motion[0], motion[1], motion[2]);
        double thetaSecond = JointArithmetic.AngleSecond(motion[0], motion[1], motion[2]);
        double slideSecond = JointArithmetic.SlideSecond(motion[3], motion[4]);
        // d²/dt² law(θ) with q̈ = 0: curvature·θ̇² + slope·θ̈₀.
        rows[0] = slideSecond - (curvature * thetaRate * thetaRate + slope * thetaSecond);
    }
}
