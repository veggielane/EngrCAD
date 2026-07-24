using System.Runtime.CompilerServices;

namespace EngrCAD.Core;

/// <summary>
/// Integer 2D vector for grid indexing (pixel/cell coordinates), following
/// <see cref="Vector2d"/>'s idioms (tuple conversion, exact equality).
/// </summary>
public readonly struct Vector2i : IEquatable<Vector2i>
{
    public int X { get; }
    public int Y { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2i(int x, int y)
    {
        X = x;
        Y = y;
    }

    public static readonly Vector2i Zero = new(0, 0);
    public static readonly Vector2i One = new(1, 1);
    public static readonly Vector2i UnitX = new(1, 0);
    public static readonly Vector2i UnitY = new(0, 1);

    public int this[int index] => index switch
    {
        0 => X,
        1 => Y,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator Vector2i((int X, int Y) t) => new(t.X, t.Y);

    public static Vector2i operator +(Vector2i a, Vector2i b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2i operator -(Vector2i a, Vector2i b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2i operator -(Vector2i v) => new(-v.X, -v.Y);
    public static Vector2i operator *(Vector2i v, int s) => new(v.X * s, v.Y * s);
    public static Vector2i operator *(int s, Vector2i v) => v * s;

    /// <summary>X · Y as a long — e.g. the sample count of an X-by-Y grid.</summary>
    public long ComponentProduct => (long)X * Y;

    public static Vector2i Min(Vector2i a, Vector2i b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
    public static Vector2i Max(Vector2i a, Vector2i b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>The equivalent double vector (exact for |values| ≤ 2⁵³, i.e. always).</summary>
    public Vector2d ToVector2d() => new(X, Y);

    public bool Equals(Vector2i other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is Vector2i v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Vector2i a, Vector2i b) => a.Equals(b);
    public static bool operator !=(Vector2i a, Vector2i b) => !a.Equals(b);

    public override string ToString() => $"({X}, {Y})";

    public void Deconstruct(out int x, out int y)
    {
        x = X;
        y = Y;
    }
}
