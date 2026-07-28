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
            angle => amplitude / 2 * lobes * lobes * Math.Cos(lobes * angle));
    }

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
    public static CamLaw FromSketch(Sketch profile, double followerAngle = 0, int samples = 720)
    {
        ArgumentNullException.ThrowIfNull(profile);
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
            // profile's local direction followerAngle − θ.
            lifts[k] = Radius(region, followerAngle - k * h, reach);
        }
        return new SplineCamLaw(lifts);
    }

    /// <summary>The OUTERMOST boundary crossing along a ray from the pivot: a coarse
    /// outside-in march finds the first inside sample, then bisection on the exact
    /// signed distance pins the crossing.</summary>
    private static double Radius(SketchRegion region, double angle, double reach)
    {
        var direction = new Vector2d(Math.Cos(angle), Math.Sin(angle));
        const int coarse = 1024;
        double step = reach / coarse;
        double outside = reach;
        double inside = double.NaN;
        for (int i = 1; i <= coarse; i++)
        {
            double r = reach - i * step;
            if (region.SignedDistance(direction * r) < 0)
            {
                inside = r;
                outside = r + step;
                break;
            }
        }
        if (double.IsNaN(inside))
            throw new ArgumentException(
                $"The cam profile has no material along the ray at {angle:g4} rad — a radial cam " +
                "profile must surround its pivot at every angle.");
        for (int i = 0; i < 100; i++)
        {
            double middle = 0.5 * (inside + outside);
            if (region.SignedDistance(direction * middle) < 0)
                inside = middle;
            else
                outside = middle;
        }
        return 0.5 * (inside + outside);
    }
}

internal sealed class FunctionCamLaw(
    Func<double, double> lift, Func<double, double> slope, Func<double, double> curvature) : CamLaw
{
    public override void Evaluate(double angle, out double liftValue, out double slopeValue, out double curvatureValue)
    {
        liftValue = lift(angle);
        slopeValue = slope(angle);
        curvatureValue = curvature(angle);
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
        return Ratio(a, b, ratio, name ?? $"gear {teethA:g0}:{teethB:g0}");
    }

    /// <summary>An open belt or chain drive: Δθ_b = (radiusA/radiusB)·Δθ_a (both
    /// pulleys turn the same way); <paramref name="crossed"/> flips the sense.</summary>
    public static Coupling Belt(
        AxisJoint a, AxisJoint b, double radiusA, double radiusB, bool crossed = false, string? name = null)
    {
        if (radiusA <= 0 || radiusB <= 0)
            throw new ArgumentOutOfRangeException(nameof(radiusA), "Belt pitch radii must be positive.");
        double ratio = (crossed ? -1 : 1) * radiusA / radiusB;
        return Ratio(a, b, ratio, name ?? $"belt {radiusA:g4}:{radiusB:g4}");
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
        return new Coupling(new RatioCoupling(a, b, ratio, name ?? $"ratio {ratio:g4}"), [a, b]);
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
        return new Coupling(new CamCoupling(cam, follower, law, name ?? "cam"), [cam, follower]);
    }

    private static void RequireFreeSpin(AxisJoint joint)
    {
        if (joint is PrismaticJoint or FixedJoint)
            throw new ArgumentException(
                $"Joint '{joint.Name}' locks its spin, so a spin coupling cannot drive it — use a " +
                "revolute, cylindrical, or screw joint.");
    }
}

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
