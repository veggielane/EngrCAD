namespace EngrCAD.Core;

/// <summary>
/// A symmetric 3×3 tensor, stored as its six independent entries. Used for both the
/// inertia tensor and the volume-weighted second-moment matrix (the two differ only by
/// the standard identity I = tr(S)·Id − S, so one type serves both), and generally
/// wherever a symmetric bilinear form appears (covariance, quadric accumulation).
/// Moved here from EngrCAD.Mesh: a symmetric 3×3 type belongs in the dependency-free
/// foundation, beside <see cref="SymmetricEigen3"/> which diagonalizes it.
/// </summary>
public readonly struct SymmetricTensor3 : IEquatable<SymmetricTensor3>
{
    public double Xx { get; }
    public double Yy { get; }
    public double Zz { get; }
    public double Xy { get; }
    public double Xz { get; }
    public double Yz { get; }

    public SymmetricTensor3(double xx, double yy, double zz, double xy, double xz, double yz)
    {
        Xx = xx;
        Yy = yy;
        Zz = zz;
        Xy = xy;
        Xz = xz;
        Yz = yz;
    }

    public static readonly SymmetricTensor3 Zero = default;
    public static readonly SymmetricTensor3 Identity = new(1, 1, 1, 0, 0, 0);

    public double Trace => Xx + Yy + Zz;

    /// <summary>Row/column access, 0-based (symmetric, so order does not matter).</summary>
    public double this[int row, int column] => (row, column) switch
    {
        (0, 0) => Xx,
        (1, 1) => Yy,
        (2, 2) => Zz,
        (0, 1) or (1, 0) => Xy,
        (0, 2) or (2, 0) => Xz,
        (1, 2) or (2, 1) => Yz,
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    /// <summary>The outer product v·vᵀ.</summary>
    public static SymmetricTensor3 OuterProduct(in Vector3d v) =>
        new(v.X * v.X, v.Y * v.Y, v.Z * v.Z, v.X * v.Y, v.X * v.Z, v.Y * v.Z);

    public Vector3d Multiply(in Vector3d v) => new(
        Xx * v.X + Xy * v.Y + Xz * v.Z,
        Xy * v.X + Yy * v.Y + Yz * v.Z,
        Xz * v.X + Yz * v.Y + Zz * v.Z);

    public static SymmetricTensor3 operator +(in SymmetricTensor3 a, in SymmetricTensor3 b) =>
        new(a.Xx + b.Xx, a.Yy + b.Yy, a.Zz + b.Zz, a.Xy + b.Xy, a.Xz + b.Xz, a.Yz + b.Yz);

    public static SymmetricTensor3 operator -(in SymmetricTensor3 a, in SymmetricTensor3 b) =>
        new(a.Xx - b.Xx, a.Yy - b.Yy, a.Zz - b.Zz, a.Xy - b.Xy, a.Xz - b.Xz, a.Yz - b.Yz);

    public static SymmetricTensor3 operator *(in SymmetricTensor3 t, double s) =>
        new(t.Xx * s, t.Yy * s, t.Zz * s, t.Xy * s, t.Xz * s, t.Yz * s);

    public static SymmetricTensor3 operator *(double s, in SymmetricTensor3 t) => t * s;

    /// <summary>tr(T)·Id − T — the map between a second-moment matrix and an inertia
    /// tensor (it is its own inverse in 3D up to the factor ½ on the trace, so it is
    /// spelled out at both call sites rather than reused blindly). Public since the move
    /// to Core: it was internal to the mesh assembly, but the mesh mass-property code
    /// still needs it from here.</summary>
    public SymmetricTensor3 TraceComplement()
    {
        double t = Trace;
        return new SymmetricTensor3(t - Xx, t - Yy, t - Zz, -Xy, -Xz, -Yz);
    }

    /// <summary>
    /// The congruence M·T·Mᵀ using the linear (upper-left 3×3) part of
    /// <paramref name="transform"/>. Symmetric by construction.
    /// </summary>
    public SymmetricTensor3 Congruence(in Matrix4d transform)
    {
        var r0 = new Vector3d(transform.M11, transform.M12, transform.M13);
        var r1 = new Vector3d(transform.M21, transform.M22, transform.M23);
        var r2 = new Vector3d(transform.M31, transform.M32, transform.M33);
        var t0 = Multiply(r0);
        var t1 = Multiply(r1);
        var t2 = Multiply(r2);
        return new SymmetricTensor3(
            r0.Dot(t0), r1.Dot(t1), r2.Dot(t2),
            r0.Dot(t1), r0.Dot(t2), r1.Dot(t2));
    }

    public bool Equals(SymmetricTensor3 other) =>
        Xx.Equals(other.Xx) && Yy.Equals(other.Yy) && Zz.Equals(other.Zz) &&
        Xy.Equals(other.Xy) && Xz.Equals(other.Xz) && Yz.Equals(other.Yz);

    public override bool Equals(object? obj) => obj is SymmetricTensor3 t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(Xx, Yy, Zz, Xy, Xz, Yz);
    public static bool operator ==(in SymmetricTensor3 a, in SymmetricTensor3 b) => a.Equals(b);
    public static bool operator !=(in SymmetricTensor3 a, in SymmetricTensor3 b) => !a.Equals(b);

    public override string ToString() =>
        $"[{Xx:G6} {Xy:G6} {Xz:G6}; {Xy:G6} {Yy:G6} {Yz:G6}; {Xz:G6} {Yz:G6} {Zz:G6}]";
}
