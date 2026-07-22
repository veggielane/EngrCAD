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
}

/// <summary>
/// Infinite plane through <paramref name="origin"/> spanned by the unit orthogonal
/// directions <paramref name="xDirection"/> and <paramref name="yDirection"/>;
/// normal = x × y. Faces bound it via their loops.
/// </summary>
public sealed class PlaneSurface(Vector3d origin, Vector3d xDirection, Vector3d yDirection) : Surface
{
    private static readonly Interval Infinite = new(double.NegativeInfinity, double.PositiveInfinity);

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
