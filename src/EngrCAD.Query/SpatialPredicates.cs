using EngrCAD.Core;

namespace EngrCAD.Query;

/// <summary>
/// Spatial predicate vocabulary for LINQ queries over <see cref="SpatialCollection{T}"/>.
/// These are ordinary boolean methods — queries stay correct anywhere — but when they
/// appear in a Where clause against a spatial collection's registered bounds, the query
/// provider recognizes them and answers from the BVH index instead of scanning.
/// (All parameters are by-value: expression trees cannot contain calls with 'in'
/// parameters, which is why these wrap the kernel's 'in'-based API.)
/// </summary>
public static class SpatialPredicates
{
    /// <summary>True when <paramref name="bounds"/> intersects <paramref name="region"/> (closed intervals).</summary>
    public static bool Within(this Aabb bounds, Aabb region) => bounds.Intersects(region);

    /// <summary>True when <paramref name="bounds"/> lies within <paramref name="distance"/> of <paramref name="point"/>.</summary>
    public static bool WithinDistance(this Aabb bounds, Vector3d point, double distance) =>
        bounds.DistanceTo(point) <= distance;

    /// <summary>True when <paramref name="ray"/> passes through <paramref name="bounds"/>.</summary>
    public static bool HitBy(this Aabb bounds, Ray3d ray) => ray.Intersects(bounds);
}
