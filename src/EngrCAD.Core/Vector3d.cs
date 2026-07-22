using System.Runtime.CompilerServices;

namespace EngrCAD.Core;

/// <summary>
/// Double-precision 3D vector. Also used for points; a distinct point type may be
/// introduced later if the affine distinction earns its keep.
/// Operators and <see cref="Equals(Vector3d)"/> are exact (bitwise on components);
/// geometric comparisons go through <see cref="Tolerance"/>-taking methods.
/// </summary>
public readonly struct Vector3d : IEquatable<Vector3d>
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3d(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly Vector3d Zero = new(0, 0, 0);
    public static readonly Vector3d One = new(1, 1, 1);
    public static readonly Vector3d UnitX = new(1, 0, 0);
    public static readonly Vector3d UnitY = new(0, 1, 0);
    public static readonly Vector3d UnitZ = new(0, 0, 1);

    public double this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator Vector3d((double X, double Y, double Z) t) => new(t.X, t.Y, t.Z);

    public static Vector3d operator +(in Vector3d a, in Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(in Vector3d a, in Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator -(in Vector3d v) => new(-v.X, -v.Y, -v.Z);
    public static Vector3d operator *(in Vector3d v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3d operator *(double s, in Vector3d v) => v * s;
    public static Vector3d operator /(in Vector3d v, double s) => new(v.X / s, v.Y / s, v.Z / s);

    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Dot(in Vector3d other) => X * other.X + Y * other.Y + Z * other.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3d Cross(in Vector3d other) => new(
        Y * other.Z - Z * other.Y,
        Z * other.X - X * other.Z,
        X * other.Y - Y * other.X);

    public double DistanceTo(in Vector3d other) => (this - other).Length;
    public double DistanceSquaredTo(in Vector3d other) => (this - other).LengthSquared;

    /// <summary>Returns the unit vector, or throws if the length is zero within <paramref name="tolerance"/>.</summary>
    public Vector3d Normalized(in Tolerance tolerance)
    {
        if (!TryNormalize(tolerance, out var result))
            throw new InvalidOperationException("Cannot normalize a zero-length vector.");
        return result;
    }

    public Vector3d Normalized() => Normalized(Tolerance.Default);

    public bool TryNormalize(in Tolerance tolerance, out Vector3d result)
    {
        double length = Length;
        if (length <= tolerance.Linear)
        {
            result = Zero;
            return false;
        }
        result = this / length;
        return true;
    }

    /// <summary>Angle to another vector in [0, π], computed via atan2 for stability near 0 and π.</summary>
    public double AngleTo(in Vector3d other) => Math.Atan2(Cross(other).Length, Dot(other));

    public bool IsParallelTo(in Vector3d other, in Tolerance tolerance)
    {
        double angle = AngleTo(other);
        return angle <= tolerance.Angular || Math.PI - angle <= tolerance.Angular;
    }

    public bool IsPerpendicularTo(in Vector3d other, in Tolerance tolerance) =>
        Math.Abs(AngleTo(other) - Math.PI / 2) <= tolerance.Angular;

    public bool IsZero(in Tolerance tolerance) => LengthSquared <= tolerance.Linear * tolerance.Linear;

    public static Vector3d Min(in Vector3d a, in Vector3d b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    public static Vector3d Max(in Vector3d a, in Vector3d b) =>
        new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public static Vector3d Lerp(in Vector3d a, in Vector3d b, double t) => a + (b - a) * t;

    /// <summary>Any unit vector perpendicular to this one (this need not be unit length).</summary>
    public Vector3d ArbitraryPerpendicular(in Tolerance tolerance)
    {
        // Cross with the axis this vector is least aligned with to avoid degeneracy.
        var axis = Math.Abs(X) <= Math.Abs(Y) && Math.Abs(X) <= Math.Abs(Z) ? UnitX
                 : Math.Abs(Y) <= Math.Abs(Z) ? UnitY
                 : UnitZ;
        return Cross(axis).Normalized(tolerance);
    }

    public bool AreEqual(in Vector3d other, in Tolerance tolerance) =>
        DistanceSquaredTo(other) <= tolerance.Linear * tolerance.Linear;

    public bool Equals(Vector3d other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
    public override bool Equals(object? obj) => obj is Vector3d v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(in Vector3d a, in Vector3d b) => a.Equals(b);
    public static bool operator !=(in Vector3d a, in Vector3d b) => !a.Equals(b);

    public override string ToString() => $"({X:G17}, {Y:G17}, {Z:G17})";

    public void Deconstruct(out double x, out double y, out double z)
    {
        x = X;
        y = Y;
        z = Z;
    }
}
