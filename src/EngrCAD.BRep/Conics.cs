using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// Parabola in the plane spanned by <see cref="XDirection"/>/<see cref="YDirection"/>
/// (unit, orthogonal), using the standard focal parameterization
/// P(t) = Apex + (t²/(4f))·X + t·Y with focal length f = <see cref="FocalLength"/>.
/// In local coordinates that is y² = 4·f·x: the apex sits at the origin, the
/// <see cref="Focus"/> at (f, 0), and the directrix is the line x = −f. The parameter
/// t is the local y coordinate, so the curve is regular everywhere (|P′| ≥ 1 for unit
/// axes). The underlying curve is unbounded, so a finite <see cref="Domain"/> must be
/// supplied at construction (mirroring OCCT, where Geom_Parabola is trimmed for use).
/// </summary>
public sealed class Parabola3d : Curve3d
{
    private readonly Interval _domain;

    public Parabola3d(Vector3d apex, Vector3d xDirection, Vector3d yDirection, double focalLength, Interval domain)
    {
        if (!(focalLength > 0))
            throw new ArgumentOutOfRangeException(nameof(focalLength), "Focal length must be positive.");
        if (!double.IsFinite(domain.Start) || !double.IsFinite(domain.End) || domain.Length <= 0)
            throw new ArgumentException("Parabola domain must be a finite, non-empty interval.", nameof(domain));
        Apex = apex;
        XDirection = xDirection;
        YDirection = yDirection;
        FocalLength = focalLength;
        _domain = domain;
    }

    public Vector3d Apex { get; }
    public Vector3d XDirection { get; }
    public Vector3d YDirection { get; }
    public double FocalLength { get; }
    public Vector3d Focus => Apex + XDirection * FocalLength;
    /// <summary>Plane normal X × Y (the parabola's symmetry axis is <see cref="XDirection"/>).</summary>
    public Vector3d Normal => XDirection.Cross(YDirection);

    public override Interval Domain => _domain;
    public override bool IsClosed => false;

    public override Vector3d PointAt(double t) =>
        Apex + XDirection * (t * t / (4 * FocalLength)) + YDirection * t;

    public override Vector3d DerivativeAt(double t) =>
        XDirection * (t / (2 * FocalLength)) + YDirection;

    public override Vector3d SecondDerivativeAt(double t) =>
        XDirection * (1 / (2 * FocalLength));

    public override Vector3d TangentAt(double t) => DerivativeAt(t).Normalized();

    /// <summary>
    /// Exact arc length over <see cref="Domain"/>. With s = t/(2f) the speed is
    /// |P′| = √(1 + s²), whose antiderivative is f·(s·√(1 + s²) + asinh s).
    /// </summary>
    public double Length()
    {
        double Antiderivative(double t)
        {
            double s = t / (2 * FocalLength);
            return FocalLength * (s * Math.Sqrt(1 + s * s) + Math.Asinh(s));
        }
        return Antiderivative(_domain.End) - Antiderivative(_domain.Start);
    }
}

/// <summary>
/// One branch of a hyperbola: P(t) = Center + A·cosh t + B·sinh t for orthogonal
/// semi-axis vectors A = <see cref="SemiAxisX"/> (toward the branch apex at t = 0) and
/// B = <see cref="SemiAxisY"/>. In local coordinates (x/|A|)² − (y/|B|)² = 1. The
/// parameterization is regular (|P′| ≥ |B| since cosh t ≥ 1), but cosh grows
/// exponentially, so a finite <see cref="Domain"/> must be supplied at construction.
/// </summary>
public sealed class Hyperbola3d : Curve3d
{
    private readonly Interval _domain;

    public Hyperbola3d(Vector3d center, Vector3d semiAxisX, Vector3d semiAxisY, Interval domain)
    {
        if (!double.IsFinite(domain.Start) || !double.IsFinite(domain.End) || domain.Length <= 0)
            throw new ArgumentException("Hyperbola domain must be a finite, non-empty interval.", nameof(domain));
        Center = center;
        SemiAxisX = semiAxisX;
        SemiAxisY = semiAxisY;
        _domain = domain;
    }

    public Vector3d Center { get; }
    public Vector3d SemiAxisX { get; }
    public Vector3d SemiAxisY { get; }

    public override Interval Domain => _domain;
    public override bool IsClosed => false;

    public override Vector3d PointAt(double t) =>
        Center + SemiAxisX * Math.Cosh(t) + SemiAxisY * Math.Sinh(t);

    public override Vector3d DerivativeAt(double t) =>
        SemiAxisX * Math.Sinh(t) + SemiAxisY * Math.Cosh(t);

    /// <summary>Exactly P(t) − Center: the hyperbola satisfies P″ = P − C.</summary>
    public override Vector3d SecondDerivativeAt(double t) =>
        SemiAxisX * Math.Cosh(t) + SemiAxisY * Math.Sinh(t);

    public override Vector3d TangentAt(double t) => DerivativeAt(t).Normalized();

    /// <summary>
    /// Arc length over <see cref="Domain"/>. Hyperbolic arc length has no elementary
    /// closed form (it is an incomplete elliptic integral of the second kind), so this
    /// integrates the exact speed |P′| by adaptive Simpson quadrature to ~1e-12 relative.
    /// </summary>
    public double Length() =>
        AdaptiveQuadrature.Integrate(t => DerivativeAt(t).Length, _domain.Start, _domain.End);
}

/// <summary>Adaptive Simpson quadrature for smooth positive integrands (arc lengths).</summary>
internal static class AdaptiveQuadrature
{
    public static double Integrate(Func<double, double> f, double a, double b)
    {
        double fa = f(a), fb = f(b);
        double m = 0.5 * (a + b), fm = f(m);
        double whole = Simpson(a, b, fa, fm, fb);
        double tolerance = Math.Max(1e-15, 1e-12 * Math.Abs(whole));
        return Refine(f, a, b, fa, fm, fb, whole, tolerance, depth: 40);
    }

    private static double Simpson(double a, double b, double fa, double fm, double fb) =>
        (b - a) / 6 * (fa + 4 * fm + fb);

    private static double Refine(
        Func<double, double> f, double a, double b,
        double fa, double fm, double fb, double whole, double tolerance, int depth)
    {
        double m = 0.5 * (a + b);
        double lm = 0.5 * (a + m), rm = 0.5 * (m + b);
        double flm = f(lm), frm = f(rm);
        double left = Simpson(a, m, fa, flm, fm);
        double right = Simpson(m, b, fm, frm, fb);
        double delta = left + right - whole;
        if (depth <= 0 || Math.Abs(delta) <= 15 * tolerance)
            return left + right + delta / 15; // Richardson extrapolation
        return Refine(f, a, m, fa, flm, fm, left, tolerance / 2, depth - 1)
             + Refine(f, m, b, fm, frm, fb, right, tolerance / 2, depth - 1);
    }
}
