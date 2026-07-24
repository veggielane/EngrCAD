namespace EngrCAD.Core;

/// <summary>
/// Axis-aligned integer index box with **inclusive** Min and Max corners — a 3D index
/// range over a grid (both endpoints are valid indices), the integer sibling of
/// <see cref="Aabb"/>. geometry3Sharp: AxisAlignedBox3i.
/// </summary>
public readonly struct AxisAlignedBox3i : IEquatable<AxisAlignedBox3i>
{
    /// <summary>Smallest contained index (inclusive).</summary>
    public Vector3i Min { get; }

    /// <summary>Largest contained index (inclusive).</summary>
    public Vector3i Max { get; }

    public AxisAlignedBox3i(Vector3i min, Vector3i max)
    {
        if (max.X < min.X || max.Y < min.Y || max.Z < min.Z)
            throw new ArgumentException($"Box max {max} precedes min {min} on some axis.");
        Min = min;
        Max = max;
    }

    /// <summary>The index range [0, counts) per axis as an inclusive box.</summary>
    public static AxisAlignedBox3i FromCounts(Vector3i counts)
    {
        if (counts.X < 1 || counts.Y < 1 || counts.Z < 1)
            throw new ArgumentOutOfRangeException(nameof(counts), "Need at least one index per axis.");
        return new AxisAlignedBox3i(Vector3i.Zero, counts - Vector3i.One);
    }

    /// <summary>Indices per axis (Max − Min + 1 componentwise).</summary>
    public Vector3i Counts => Max - Min + Vector3i.One;

    /// <summary>Total number of indices in the box.</summary>
    public long Count => Counts.ComponentProduct;

    /// <summary>Per-axis index ranges.</summary>
    public Interval1i RangeX => new(Min.X, Max.X);
    public Interval1i RangeY => new(Min.Y, Max.Y);
    public Interval1i RangeZ => new(Min.Z, Max.Z);

    public bool Contains(Vector3i index) =>
        index.X >= Min.X && index.X <= Max.X &&
        index.Y >= Min.Y && index.Y <= Max.Y &&
        index.Z >= Min.Z && index.Z <= Max.Z;

    public bool Contains(AxisAlignedBox3i other) => Contains(other.Min) && Contains(other.Max);

    public bool Overlaps(AxisAlignedBox3i other) =>
        Min.X <= other.Max.X && other.Min.X <= Max.X &&
        Min.Y <= other.Max.Y && other.Min.Y <= Max.Y &&
        Min.Z <= other.Max.Z && other.Min.Z <= Max.Z;

    public AxisAlignedBox3i Intersect(AxisAlignedBox3i other)
    {
        if (!Overlaps(other))
            throw new InvalidOperationException($"Boxes {this} and {other} do not overlap.");
        return new AxisAlignedBox3i(Vector3i.Max(Min, other.Min), Vector3i.Min(Max, other.Max));
    }

    /// <summary>Grows (or shrinks, negative) the box by <paramref name="amount"/> on every side.</summary>
    public AxisAlignedBox3i Expanded(int amount) =>
        new(Min - Vector3i.One * amount, Max + Vector3i.One * amount);

    public bool Equals(AxisAlignedBox3i other) => Min == other.Min && Max == other.Max;
    public override bool Equals(object? obj) => obj is AxisAlignedBox3i b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(Min, Max);
    public static bool operator ==(AxisAlignedBox3i a, AxisAlignedBox3i b) => a.Equals(b);
    public static bool operator !=(AxisAlignedBox3i a, AxisAlignedBox3i b) => !a.Equals(b);

    public override string ToString() => $"[{Min} .. {Max}]";
}
