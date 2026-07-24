using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>Parametric surface, evaluated over <see cref="DomainU"/> × <see cref="DomainV"/>.</summary>
public abstract class Surface
{
    public abstract Interval DomainU { get; }
    public abstract Interval DomainV { get; }
    public abstract Vector3d PointAt(double u, double v);

    /// <summary>Unit normal by central differences; subclasses override with exact normals.</summary>
    public virtual Vector3d NormalAt(double u, double v)
    {
        double hu = double.IsFinite(DomainU.Length) ? Math.Max(1e-7, DomainU.Length * 1e-7) : 1e-7;
        double hv = double.IsFinite(DomainV.Length) ? Math.Max(1e-7, DomainV.Length * 1e-7) : 1e-7;
        double u0 = DomainU.Clamp(u - hu), u1 = DomainU.Clamp(u + hu);
        double v0 = DomainV.Clamp(v - hv), v1 = DomainV.Clamp(v + hv);
        var du = (PointAt(u1, v) - PointAt(u0, v)) / (u1 - u0);
        var dv = (PointAt(u, v1) - PointAt(u, v0)) / (v1 - v0);
        return du.Cross(dv).Normalized();
    }

    /// <summary>
    /// Inverse evaluation: parameters of a point lying on (or near) the surface. The base
    /// implementation seeds from a coarse grid and runs damped Gauss–Newton (finite
    /// domains only); plane/cylinder/sphere override with exact formulas. Returns false
    /// when the point cannot be brought within <paramref name="tolerance"/> of the surface.
    /// The 1e-8 default suits exact overrides; pullback of traced/sampled geometry passes
    /// the looser <see cref="FaceGeometry.InverseEvaluationTolerance"/> explicitly.
    /// </summary>
    public virtual bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        uv = default;
        var domainU = DomainU;
        var domainV = DomainV;
        if (!double.IsFinite(domainU.Length) || !double.IsFinite(domainV.Length))
            return false;

        const int seedResolution = 16;
        double bestU = domainU.Mid, bestV = domainV.Mid, bestDistance = double.PositiveInfinity;
        for (int i = 0; i <= seedResolution; i++)
        {
            for (int j = 0; j <= seedResolution; j++)
            {
                double su = domainU.ParameterAt((double)i / seedResolution);
                double sv = domainV.ParameterAt((double)j / seedResolution);
                double d = PointAt(su, sv).DistanceSquaredTo(point);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestU = su;
                    bestV = sv;
                }
            }
        }

        // Periodic-u surfaces must wrap rather than clamp: the seed can land on the
        // wrong side of the seam, and a clamped Newton step then pins at the domain
        // edge forever instead of walking across it.
        double periodU = FaceGeometry.PeriodU(this);
        double WrapU(double x) => periodU > 0
            ? domainU.Start + (((x - domainU.Start) % periodU) + periodU) % periodU
            : domainU.Clamp(x);

        double u = bestU, v = bestV;
        double hu = Math.Max(1e-7, domainU.Length * 1e-7);
        double hv = Math.Max(1e-7, domainV.Length * 1e-7);
        for (int iteration = 0; iteration < 25; iteration++)
        {
            var residual = PointAt(u, v) - point;
            if (residual.Length < tolerance)
            {
                uv = new Vector2d(u, v);
                return true;
            }
            var du = (PointAt(WrapU(u + hu), v) - PointAt(WrapU(u - hu), v)) / (2 * hu);
            var dv = (PointAt(u, domainV.Clamp(v + hv)) - PointAt(u, domainV.Clamp(v - hv))) / (2 * hv);

            // Normal equations for the 2-unknown least squares step, lightly damped.
            double a11 = du.Dot(du) + 1e-12, a12 = du.Dot(dv), a22 = dv.Dot(dv) + 1e-12;
            double b1 = -du.Dot(residual), b2 = -dv.Dot(residual);
            double det = a11 * a22 - a12 * a12;
            // Near-underflow degenerate-Jacobian guard, not a geometric tolerance.
            if (Math.Abs(det) < 1e-30)
                return false;
            u = WrapU(u + (b1 * a22 - b2 * a12) / det);
            v = domainV.Clamp(v + (b2 * a11 - b1 * a12) / det);
        }
        if ((PointAt(u, v) - point).Length < tolerance)
        {
            uv = new Vector2d(u, v);
            return true;
        }
        return false;
    }
}

/// <summary>
/// Infinite plane through <paramref name="origin"/> spanned by the unit orthogonal
/// directions <paramref name="xDirection"/> and <paramref name="yDirection"/>;
/// normal = x × y. Faces bound it via their loops.
/// </summary>
public sealed class PlaneSurface(Vector3d origin, Vector3d xDirection, Vector3d yDirection) : Surface
{
    private static readonly Interval Infinite = new(double.NegativeInfinity, double.PositiveInfinity);

    /// <summary>Plane spanned by the frame's X/Y through its origin (normal = frame Z).</summary>
    public PlaneSurface(in Frame3d frame) : this(frame.Origin, frame.X, frame.Y) { }

    public Vector3d Origin => origin;
    public Vector3d XDirection => xDirection;
    public Vector3d YDirection => yDirection;
    public Vector3d Normal => xDirection.Cross(yDirection);

    public override Interval DomainU => Infinite;
    public override Interval DomainV => Infinite;

    public override Vector3d PointAt(double u, double v) => origin + xDirection * u + yDirection * v;
    public override Vector3d NormalAt(double u, double v) => Normal;

    /// <summary>Plane coordinates of a 3D point (assumed on or near the plane).</summary>
    public Vector2d Project(in Vector3d point)
    {
        var d = point - origin;
        return new Vector2d(d.Dot(xDirection), d.Dot(yDirection));
    }

    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        uv = Project(point);
        return Math.Abs((point - origin).Dot(Normal)) < tolerance;
    }
}

/// <summary>
/// Infinite cylinder about the axis z = x × y through <paramref name="origin"/>;
/// u is the angle [0, 2π] (closed), v the axial coordinate. Normal points outward.
/// </summary>
public sealed class CylinderSurface(Vector3d origin, Vector3d xDirection, Vector3d yDirection, double radius) : Surface
{
    public Vector3d Origin => origin;
    public Vector3d XDirection => xDirection;
    public Vector3d YDirection => yDirection;
    public Vector3d Axis => xDirection.Cross(yDirection);
    public double Radius => radius;

    public override Interval DomainU => new(0, 2 * Math.PI);
    public override Interval DomainV => new(double.NegativeInfinity, double.PositiveInfinity);

    public override Vector3d PointAt(double u, double v) =>
        origin + xDirection * (radius * Math.Cos(u)) + yDirection * (radius * Math.Sin(u)) + Axis * v;

    public override Vector3d NormalAt(double u, double v) =>
        xDirection * Math.Cos(u) + yDirection * Math.Sin(u);

    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        var d = point - origin;
        double u = Math.Atan2(d.Dot(yDirection), d.Dot(xDirection));
        if (u < 0)
            u += 2 * Math.PI;
        uv = new Vector2d(u, d.Dot(Axis));
        return Math.Abs((d - Axis * d.Dot(Axis)).Length - radius) < tolerance;
    }
}

/// <summary>Sphere; u is azimuth [0, 2π], v is latitude [−π/2, π/2]. Normal points outward.</summary>
public sealed class SphereSurface(Vector3d center, double radius) : Surface
{
    public Vector3d Center => center;
    public double Radius => radius;

    public override Interval DomainU => new(0, 2 * Math.PI);
    public override Interval DomainV => new(-Math.PI / 2, Math.PI / 2);

    public override Vector3d PointAt(double u, double v) => center + NormalAt(u, v) * radius;

    public override Vector3d NormalAt(double u, double v) => new(
        Math.Cos(v) * Math.Cos(u),
        Math.Cos(v) * Math.Sin(u),
        Math.Sin(v));

    public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
    {
        var d = point - center;
        double u = Math.Atan2(d.Y, d.X);
        if (u < 0)
            u += 2 * Math.PI;
        uv = new Vector2d(u, Math.Asin(Math.Clamp(d.Z / Math.Max(d.Length, 1e-300), -1, 1)));
        return Math.Abs(d.Length - radius) < tolerance;
    }
}

/// <summary>Tensor-product rational B-spline surface.</summary>
public sealed class NurbsSurface : Surface
{
    public int DegreeU { get; }
    public int DegreeV { get; }

    /// <summary>[countU, countV] grids.</summary>
    public Vector3d[,] ControlPoints { get; }

    public double[,] Weights { get; }
    public IReadOnlyList<double> KnotsU { get; }
    public IReadOnlyList<double> KnotsV { get; }

    public NurbsSurface(
        int degreeU, int degreeV,
        Vector3d[,] controlPoints, double[,]? weights,
        IReadOnlyList<double> knotsU, IReadOnlyList<double> knotsV)
    {
        int countU = controlPoints.GetLength(0);
        int countV = controlPoints.GetLength(1);
        if (degreeU < 1 || degreeV < 1)
            throw new ArgumentOutOfRangeException(nameof(degreeU));
        if (countU < degreeU + 1 || countV < degreeV + 1)
            throw new ArgumentException("Not enough control points for the requested degrees.");
        if (knotsU.Count != countU + degreeU + 1 || knotsV.Count != countV + degreeV + 1)
            throw new ArgumentException("Knot count does not match control net and degree.");
        if (weights is not null &&
            (weights.GetLength(0) != countU || weights.GetLength(1) != countV))
            throw new ArgumentException("Weight grid must match the control net.");

        DegreeU = degreeU;
        DegreeV = degreeV;
        ControlPoints = controlPoints;
        KnotsU = knotsU;
        KnotsV = knotsV;
        if (weights is null)
        {
            Weights = new double[countU, countV];
            for (int i = 0; i < countU; i++)
            {
                for (int j = 0; j < countV; j++)
                    Weights[i, j] = 1;
            }
        }
        else
        {
            Weights = weights;
        }
    }

    public override Interval DomainU => new(KnotsU[DegreeU], KnotsU[ControlPoints.GetLength(0)]);
    public override Interval DomainV => new(KnotsV[DegreeV], KnotsV[ControlPoints.GetLength(1)]);

    public override Vector3d PointAt(double u, double v)
    {
        u = DomainU.Clamp(u);
        v = DomainV.Clamp(v);
        int spanU = NurbsBasis.FindSpan(u, DegreeU, ControlPoints.GetLength(0), KnotsU);
        int spanV = NurbsBasis.FindSpan(v, DegreeV, ControlPoints.GetLength(1), KnotsV);
        Span<double> basisU = stackalloc double[DegreeU + 1];
        Span<double> basisV = stackalloc double[DegreeV + 1];
        NurbsBasis.Evaluate(spanU, u, DegreeU, KnotsU, basisU);
        NurbsBasis.Evaluate(spanV, v, DegreeV, KnotsV, basisV);

        var numerator = Vector3d.Zero;
        double denominator = 0;
        for (int i = 0; i <= DegreeU; i++)
        {
            for (int j = 0; j <= DegreeV; j++)
            {
                int iu = spanU - DegreeU + i;
                int jv = spanV - DegreeV + j;
                double bw = basisU[i] * basisV[j] * Weights[iu, jv];
                numerator += ControlPoints[iu, jv] * bw;
                denominator += bw;
            }
        }
        return numerator / denominator;
    }
}
