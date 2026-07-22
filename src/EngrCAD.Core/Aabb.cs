namespace EngrCAD.Core;

/// <summary>
/// Axis-aligned bounding box. <see cref="Empty"/> has inverted infinite bounds so that
/// unioning anything into it yields that thing; test <see cref="IsEmpty"/> before using
/// extents of a possibly-empty box.
/// </summary>
public readonly struct Aabb : IEquatable<Aabb>
{
    public Vector3d Min { get; }
    public Vector3d Max { get; }

    public Aabb(in Vector3d min, in Vector3d max)
    {
        Min = min;
        Max = max;
    }

    public static readonly Aabb Empty = new(
        new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    public static Aabb FromPoints(ReadOnlySpan<Vector3d> points)
    {
        var box = Empty;
        foreach (ref readonly var p in points)
            box = box.Union(p);
        return box;
    }

    public static Aabb FromCenterAndSize(in Vector3d center, in Vector3d size)
    {
        var half = size * 0.5;
        return new Aabb(center - half, center + half);
    }

    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public Vector3d Center => (Min + Max) * 0.5;
    public Vector3d Size => Max - Min;
    public Vector3d HalfSize => Size * 0.5;

    public double Volume
    {
        get
        {
            if (IsEmpty) return 0;
            var s = Size;
            return s.X * s.Y * s.Z;
        }
    }

    /// <summary>Total surface area; used by SAH-style build heuristics.</summary>
    public double SurfaceArea
    {
        get
        {
            if (IsEmpty) return 0;
            var s = Size;
            return 2 * (s.X * s.Y + s.Y * s.Z + s.Z * s.X);
        }
    }

    /// <summary>0 = X, 1 = Y, 2 = Z.</summary>
    public int LongestAxis
    {
        get
        {
            var s = Size;
            return s.X >= s.Y ? (s.X >= s.Z ? 0 : 2) : (s.Y >= s.Z ? 1 : 2);
        }
    }

    public Aabb Union(in Aabb other) => new(Vector3d.Min(Min, other.Min), Vector3d.Max(Max, other.Max));

    public Aabb Union(in Vector3d point) => new(Vector3d.Min(Min, point), Vector3d.Max(Max, point));

    /// <summary>Grows the box by <paramref name="margin"/> on all six sides.</summary>
    public Aabb Expanded(double margin) => new(
        Min - new Vector3d(margin, margin, margin),
        Max + new Vector3d(margin, margin, margin));

    /// <summary>Closed-interval overlap test: boxes sharing only a face still intersect.</summary>
    public bool Intersects(in Aabb other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public bool Intersects(in Aabb other, in Tolerance tolerance) =>
        Expanded(tolerance.Linear).Intersects(other);

    public bool Contains(in Vector3d point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    public bool Contains(in Aabb other) =>
        other.Min.X >= Min.X && other.Max.X <= Max.X &&
        other.Min.Y >= Min.Y && other.Max.Y <= Max.Y &&
        other.Min.Z >= Min.Z && other.Max.Z <= Max.Z;

    /// <summary>The overlapping region, or <see cref="Empty"/>-like inverted box when disjoint.</summary>
    public Aabb Intersection(in Aabb other) => new(Vector3d.Max(Min, other.Min), Vector3d.Min(Max, other.Max));

    /// <summary>Closest point inside the box to <paramref name="point"/> (the point itself if contained).</summary>
    public Vector3d ClosestPoint(in Vector3d point) => Vector3d.Max(Min, Vector3d.Min(Max, point));

    public double DistanceTo(in Vector3d point) => ClosestPoint(point).DistanceTo(point);

    public bool Equals(Aabb other) => Min.Equals(other.Min) && Max.Equals(other.Max);
    public override bool Equals(object? obj) => obj is Aabb b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(Min, Max);
    public static bool operator ==(in Aabb a, in Aabb b) => a.Equals(b);
    public static bool operator !=(in Aabb a, in Aabb b) => !a.Equals(b);

    public override string ToString() => $"[{Min} .. {Max}]";
}
