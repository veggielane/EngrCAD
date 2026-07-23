using EngrCAD.Core;

namespace EngrCAD.Implicit;

/// <summary>
/// A closed 2D region with an exact signed distance (negative inside). Implemented by
/// higher layers (sketches in EngrCAD.Modeling); this project only needs to evaluate
/// it to build exact extruded/revolved solids from 2D profiles.
/// </summary>
public interface IPlanarRegion
{
    double SignedDistance(in Vector2d point);

    /// <summary>Conservative 2D bounds of the region (x/y; z is ignored).</summary>
    Aabb Bounds { get; }
}

/// <summary>Prism: the region extruded along +Z from z = 0 to z = height. Exact
/// wherever the region's 2D distance is exact.</summary>
internal sealed class ExtrudedRegionSdf(IPlanarRegion region, double height) : Sdf
{
    public override double Evaluate(in Vector3d point)
    {
        double d2 = region.SignedDistance(new Vector2d(point.X, point.Y));
        double dz = Math.Max(-point.Z, point.Z - height);
        double ox = Math.Max(d2, 0);
        double oz = Math.Max(dz, 0);
        return Math.Min(Math.Max(d2, dz), 0) + Math.Sqrt(ox * ox + oz * oz);
    }

    public override Aabb Bounds
    {
        get
        {
            var b = region.Bounds;
            return new Aabb((b.Min.X, b.Min.Y, Math.Min(0, height)), (b.Max.X, b.Max.Y, Math.Max(0, height)));
        }
    }
}

/// <summary>
/// Solid of revolution: the region — read as (radius, height) coordinates, x ≥ 0 —
/// swept a full turn about the Z axis. A true 3D distance wherever the region's 2D
/// distance is exact (full turns only; the map p → (√(x²+y²), z) is an isometry
/// transverse to the revolution).
/// </summary>
internal sealed class RevolvedRegionSdf(IPlanarRegion region) : Sdf
{
    public override double Evaluate(in Vector3d point) =>
        region.SignedDistance(new Vector2d(
            Math.Sqrt(point.X * point.X + point.Y * point.Y),
            point.Z));

    public override Aabb Bounds
    {
        get
        {
            var b = region.Bounds;
            double r = Math.Max(0, b.Max.X);
            return new Aabb((-r, -r, b.Min.Y), (r, r, b.Max.Y));
        }
    }
}
