using System.Runtime.CompilerServices;

namespace EngrCAD.Core;

/// <summary>
/// Integer 3D vector for grid indexing (voxel/cell/sample coordinates), following
/// <see cref="Vector3d"/>'s idioms (tuple conversion, indexer, exact equality).
/// </summary>
public readonly struct Vector3i : IEquatable<Vector3i>
{
    public int X { get; }
    public int Y { get; }
    public int Z { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3i(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly Vector3i Zero = new(0, 0, 0);
    public static readonly Vector3i One = new(1, 1, 1);
    public static readonly Vector3i UnitX = new(1, 0, 0);
    public static readonly Vector3i UnitY = new(0, 1, 0);
    public static readonly Vector3i UnitZ = new(0, 0, 1);

    public int this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator Vector3i((int X, int Y, int Z) t) => new(t.X, t.Y, t.Z);

    public static Vector3i operator +(Vector3i a, Vector3i b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3i operator -(Vector3i a, Vector3i b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3i operator -(Vector3i v) => new(-v.X, -v.Y, -v.Z);
    public static Vector3i operator *(Vector3i v, int s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3i operator *(int s, Vector3i v) => v * s;

    /// <summary>X · Y · Z as a long — e.g. the sample count of a grid this size.</summary>
    public long ComponentProduct => (long)X * Y * Z;

    public static Vector3i Min(Vector3i a, Vector3i b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    public static Vector3i Max(Vector3i a, Vector3i b) =>
        new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>The equivalent double vector (exact — ints are within 2⁵³).</summary>
    public Vector3d ToVector3d() => new(X, Y, Z);

    public bool Equals(Vector3i other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is Vector3i v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(Vector3i a, Vector3i b) => a.Equals(b);
    public static bool operator !=(Vector3i a, Vector3i b) => !a.Equals(b);

    public override string ToString() => $"({X}, {Y}, {Z})";

    public void Deconstruct(out int x, out int y, out int z)
    {
        x = X;
        y = Y;
        z = Z;
    }
}
