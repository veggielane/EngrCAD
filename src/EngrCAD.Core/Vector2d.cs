using System.Runtime.CompilerServices;

namespace EngrCAD.Core;

/// <summary>
/// Double-precision 2D vector, primarily for parametric (u, v) surface space.
/// </summary>
public readonly struct Vector2d : IEquatable<Vector2d>
{
    public double X { get; }
    public double Y { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2d(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static readonly Vector2d Zero = new(0, 0);
    public static readonly Vector2d One = new(1, 1);
    public static readonly Vector2d UnitX = new(1, 0);
    public static readonly Vector2d UnitY = new(0, 1);

    public static implicit operator Vector2d((double X, double Y) t) => new(t.X, t.Y);

    public static Vector2d operator +(in Vector2d a, in Vector2d b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2d operator -(in Vector2d a, in Vector2d b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2d operator -(in Vector2d v) => new(-v.X, -v.Y);
    public static Vector2d operator *(in Vector2d v, double s) => new(v.X * s, v.Y * s);
    public static Vector2d operator *(double s, in Vector2d v) => v * s;
    public static Vector2d operator /(in Vector2d v, double s) => new(v.X / s, v.Y / s);

    public double LengthSquared => X * X + Y * Y;
    public double Length => Math.Sqrt(LengthSquared);

    public double Dot(in Vector2d other) => X * other.X + Y * other.Y;

    /// <summary>Z component of the 3D cross product; positive when <paramref name="other"/> is counter-clockwise from this.</summary>
    public double Cross(in Vector2d other) => X * other.Y - Y * other.X;

    public Vector2d Perpendicular => new(-Y, X);

    public double DistanceTo(in Vector2d other) => (this - other).Length;

    public bool TryNormalize(in Tolerance tolerance, out Vector2d result)
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

    public Vector2d Normalized(in Tolerance tolerance)
    {
        if (!TryNormalize(tolerance, out var result))
            throw new InvalidOperationException("Cannot normalize a zero-length vector.");
        return result;
    }

    public Vector2d Normalized() => Normalized(Tolerance.Default);

    public static Vector2d Min(in Vector2d a, in Vector2d b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
    public static Vector2d Max(in Vector2d a, in Vector2d b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    public static Vector2d Lerp(in Vector2d a, in Vector2d b, double t) => a + (b - a) * t;

    public bool AreEqual(in Vector2d other, in Tolerance tolerance) =>
        (this - other).LengthSquared <= tolerance.Linear * tolerance.Linear;

    public bool Equals(Vector2d other) => X.Equals(other.X) && Y.Equals(other.Y);
    public override bool Equals(object? obj) => obj is Vector2d v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(in Vector2d a, in Vector2d b) => a.Equals(b);
    public static bool operator !=(in Vector2d a, in Vector2d b) => !a.Equals(b);

    public override string ToString() => $"({X:G17}, {Y:G17})";

    public void Deconstruct(out double x, out double y)
    {
        x = X;
        y = Y;
    }
}
