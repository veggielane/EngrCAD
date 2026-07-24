using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Exact primitive distance functions (Inigo Quilez's canonical forms), Z-up.

internal sealed class SphereSdf(double radius) : Sdf
{
    public override double Evaluate(in Vector3d p) => p.Length - radius;

    public override Aabb Bounds => new((-radius, -radius, -radius), (radius, radius, radius));
}

internal sealed class BoxSdf(Vector3d halfExtents) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double qx = Math.Abs(p.X) - halfExtents.X;
        double qy = Math.Abs(p.Y) - halfExtents.Y;
        double qz = Math.Abs(p.Z) - halfExtents.Z;
        double outside = new Vector3d(Math.Max(qx, 0), Math.Max(qy, 0), Math.Max(qz, 0)).Length;
        double inside = Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
        return outside + inside;
    }

    public override Aabb Bounds => new(-halfExtents, halfExtents);
}

internal sealed class CylinderSdf(double radius, double halfHeight) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double dRadial = Math.Sqrt(p.X * p.X + p.Y * p.Y) - radius;
        double dAxial = Math.Abs(p.Z) - halfHeight;
        double outside = Math.Sqrt(
            Math.Max(dRadial, 0) * Math.Max(dRadial, 0) +
            Math.Max(dAxial, 0) * Math.Max(dAxial, 0));
        double inside = Math.Min(Math.Max(dRadial, dAxial), 0);
        return outside + inside;
    }

    public override Aabb Bounds => new((-radius, -radius, -halfHeight), (radius, radius, halfHeight));
}

/// <summary>
/// Capped cone (frustum) along Z: radius <c>bottomRadius</c> at z = −h, <c>topRadius</c>
/// at z = +h (Quilez's exact sdCappedCone, converted to Z-up). Exact for any radii,
/// including a zero radius (pointed apex) and equal radii (a cylinder).
/// </summary>
internal sealed class ConeSdf(double bottomRadius, double topRadius, double halfHeight) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double qx = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        double qy = p.Z;
        double h = halfHeight;
        // ca: distance to the nearer cap disk (radially clamped); cb: distance to the
        // slanted side segment from (bottomRadius, −h) to (topRadius, +h).
        double cax = qx - Math.Min(qx, qy < 0 ? bottomRadius : topRadius);
        double cay = Math.Abs(qy) - h;
        double k2x = topRadius - bottomRadius;
        double k2y = 2 * h;
        double t = Math.Clamp(
            ((topRadius - qx) * k2x + (h - qy) * k2y) / (k2x * k2x + k2y * k2y), 0, 1);
        double cbx = qx - topRadius + k2x * t;
        double cby = qy - h + k2y * t;
        double s = cbx < 0 && cay < 0 ? -1 : 1;
        return s * Math.Sqrt(Math.Min(cax * cax + cay * cay, cbx * cbx + cby * cby));
    }

    public override Aabb Bounds
    {
        get
        {
            double r = Math.Max(bottomRadius, topRadius);
            return new Aabb((-r, -r, -halfHeight), (r, r, halfHeight));
        }
    }
}

internal sealed class TorusSdf(double majorRadius, double minorRadius) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double ring = Math.Sqrt(p.X * p.X + p.Y * p.Y) - majorRadius;
        return Math.Sqrt(ring * ring + p.Z * p.Z) - minorRadius;
    }

    public override Aabb Bounds
    {
        get
        {
            double r = majorRadius + minorRadius;
            return new Aabb((-r, -r, -minorRadius), (r, r, minorRadius));
        }
    }
}

internal sealed class CapsuleSdf(Vector3d a, Vector3d b, double radius) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        var pa = p - a;
        var ba = b - a;
        double h = Math.Clamp(pa.Dot(ba) / ba.LengthSquared, 0, 1);
        return (pa - ba * h).Length - radius;
    }

    public override Aabb Bounds =>
        new Aabb(Vector3d.Min(a, b), Vector3d.Max(a, b)).Expanded(radius);
}

internal sealed class HalfSpaceSdf(Vector3d unitNormal, double offset) : Sdf
{
    public override double Evaluate(in Vector3d p) => unitNormal.Dot(p) - offset;

    public override Aabb Bounds => InfiniteBounds;
}

internal sealed class GyroidSdf(double cellSize, double thickness) : Sdf
{
    // g has gradient magnitude ≤ √3·ω, so |g|/(√3·ω) is a conservative distance bound.
    private readonly double _omega = 2 * Math.PI / cellSize;

    public override double Evaluate(in Vector3d p)
    {
        double x = p.X * _omega, y = p.Y * _omega, z = p.Z * _omega;
        double g = Math.Sin(x) * Math.Cos(y) + Math.Sin(y) * Math.Cos(z) + Math.Sin(z) * Math.Cos(x);
        return Math.Abs(g) / (Math.Sqrt(3) * _omega) - thickness / 2;
    }

    public override Aabb Bounds => InfiniteBounds;
}
