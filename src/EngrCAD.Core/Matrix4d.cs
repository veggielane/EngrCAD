namespace EngrCAD.Core;

/// <summary>
/// Double-precision 4×4 matrix, row-major storage (M[row][column]), column-vector
/// convention: points transform as p' = M · [p, 1]ᵀ, so A * B applies B first, then A.
/// </summary>
public readonly struct Matrix4d : IEquatable<Matrix4d>
{
    public double M11 { get; }
    public double M12 { get; }
    public double M13 { get; }
    public double M14 { get; }
    public double M21 { get; }
    public double M22 { get; }
    public double M23 { get; }
    public double M24 { get; }
    public double M31 { get; }
    public double M32 { get; }
    public double M33 { get; }
    public double M34 { get; }
    public double M41 { get; }
    public double M42 { get; }
    public double M43 { get; }
    public double M44 { get; }

    public Matrix4d(
        double m11, double m12, double m13, double m14,
        double m21, double m22, double m23, double m24,
        double m31, double m32, double m33, double m34,
        double m41, double m42, double m43, double m44)
    {
        M11 = m11; M12 = m12; M13 = m13; M14 = m14;
        M21 = m21; M22 = m22; M23 = m23; M24 = m24;
        M31 = m31; M32 = m32; M33 = m33; M34 = m34;
        M41 = m41; M42 = m42; M43 = m43; M44 = m44;
    }

    public static readonly Matrix4d Identity = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    public static Matrix4d CreateTranslation(in Vector3d t) => new(
        1, 0, 0, t.X,
        0, 1, 0, t.Y,
        0, 0, 1, t.Z,
        0, 0, 0, 1);

    public static Matrix4d CreateScale(double s) => CreateScale(new Vector3d(s, s, s));

    public static Matrix4d CreateScale(in Vector3d s) => new(
        s.X, 0, 0, 0,
        0, s.Y, 0, 0,
        0, 0, s.Z, 0,
        0, 0, 0, 1);

    public static Matrix4d CreateRotationX(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(
            1, 0, 0, 0,
            0, c, -s, 0,
            0, s, c, 0,
            0, 0, 0, 1);
    }

    public static Matrix4d CreateRotationY(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(
            c, 0, s, 0,
            0, 1, 0, 0,
            -s, 0, c, 0,
            0, 0, 0, 1);
    }

    public static Matrix4d CreateRotationZ(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(
            c, -s, 0, 0,
            s, c, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);
    }

    /// <summary>Rotation about a unit <paramref name="axis"/> through the origin (Rodrigues).</summary>
    public static Matrix4d CreateFromAxisAngle(in Vector3d axis, double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians), t = 1 - c;
        double x = axis.X, y = axis.Y, z = axis.Z;
        return new(
            t * x * x + c, t * x * y - s * z, t * x * z + s * y, 0,
            t * x * y + s * z, t * y * y + c, t * y * z - s * x, 0,
            t * x * z - s * y, t * y * z + s * x, t * z * z + c, 0,
            0, 0, 0, 1);
    }

    public static Matrix4d operator *(in Matrix4d a, in Matrix4d b) => new(
        a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
        a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
        a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
        a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
        a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
        a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
        a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
        a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
        a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
        a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
        a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
        a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
        a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
        a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
        a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
        a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);

    /// <summary>Transforms a point (w = 1), performing the perspective divide when w' ≠ 1.</summary>
    public Vector3d TransformPoint(in Vector3d p)
    {
        double x = M11 * p.X + M12 * p.Y + M13 * p.Z + M14;
        double y = M21 * p.X + M22 * p.Y + M23 * p.Z + M24;
        double z = M31 * p.X + M32 * p.Y + M33 * p.Z + M34;
        double w = M41 * p.X + M42 * p.Y + M43 * p.Z + M44;
        return w == 1.0 ? new Vector3d(x, y, z) : new Vector3d(x / w, y / w, z / w);
    }

    /// <summary>Transforms a direction (w = 0): rotation/scale/shear only, no translation.</summary>
    public Vector3d TransformVector(in Vector3d v) => new(
        M11 * v.X + M12 * v.Y + M13 * v.Z,
        M21 * v.X + M22 * v.Y + M23 * v.Z,
        M31 * v.X + M32 * v.Y + M33 * v.Z);

    /// <summary>
    /// Splits an affine transform into translation · rotation · uniform scale, when it
    /// has exactly that form (orthogonal columns of equal length, no reflection, no
    /// perspective). Returns false for shear, non-uniform scale, or mirroring.
    /// </summary>
    public bool TryDecomposeRigidUniformScale(out Quaterniond rotation, out Vector3d translation, out double scale)
    {
        rotation = default;
        translation = new Vector3d(M14, M24, M34);
        scale = 0;

        // Dimensionless slack on matrix entries (the perspective row and the normalized
        // scale/orthogonality residuals are unit-scale, not model units); the value
        // matches Tolerance.Default.Linear but is not a length comparison.
        const double eps = 1e-9;
        if (Math.Abs(M41) > eps || Math.Abs(M42) > eps || Math.Abs(M43) > eps || Math.Abs(M44 - 1) > eps)
            return false;

        var c0 = new Vector3d(M11, M21, M31);
        var c1 = new Vector3d(M12, M22, M32);
        var c2 = new Vector3d(M13, M23, M33);
        double s0 = c0.Length, s1 = c1.Length, s2 = c2.Length;
        double s = (s0 + s1 + s2) / 3;
        if (s < eps)
            return false;
        if (Math.Abs(s0 - s) > eps * s || Math.Abs(s1 - s) > eps * s || Math.Abs(s2 - s) > eps * s)
            return false;
        double s2sq = s * s;
        if (Math.Abs(c0.Dot(c1)) > eps * s2sq || Math.Abs(c1.Dot(c2)) > eps * s2sq || Math.Abs(c0.Dot(c2)) > eps * s2sq)
            return false;
        if (c0.Cross(c1).Dot(c2) < 0)
            return false; // reflection

        // Shepperd's method on the normalized rotation part.
        double r11 = M11 / s, r12 = M12 / s, r13 = M13 / s;
        double r21 = M21 / s, r22 = M22 / s, r23 = M23 / s;
        double r31 = M31 / s, r32 = M32 / s, r33 = M33 / s;
        double trace = r11 + r22 + r33;
        double qx, qy, qz, qw;
        if (trace > 0)
        {
            double t = Math.Sqrt(trace + 1) * 2;
            qw = 0.25 * t;
            qx = (r32 - r23) / t;
            qy = (r13 - r31) / t;
            qz = (r21 - r12) / t;
        }
        else if (r11 > r22 && r11 > r33)
        {
            double t = Math.Sqrt(1 + r11 - r22 - r33) * 2;
            qw = (r32 - r23) / t;
            qx = 0.25 * t;
            qy = (r12 + r21) / t;
            qz = (r13 + r31) / t;
        }
        else if (r22 > r33)
        {
            double t = Math.Sqrt(1 + r22 - r11 - r33) * 2;
            qw = (r13 - r31) / t;
            qx = (r12 + r21) / t;
            qy = 0.25 * t;
            qz = (r23 + r32) / t;
        }
        else
        {
            double t = Math.Sqrt(1 + r33 - r11 - r22) * 2;
            qw = (r21 - r12) / t;
            qx = (r13 + r31) / t;
            qy = (r23 + r32) / t;
            qz = 0.25 * t;
        }

        rotation = new Quaterniond(qx, qy, qz, qw).Normalized();
        scale = s;
        return true;
    }

    public Matrix4d Transposed() => new(
        M11, M21, M31, M41,
        M12, M22, M32, M42,
        M13, M23, M33, M43,
        M14, M24, M34, M44);

    public double Determinant
    {
        get
        {
            ComputeCofactorColumn(out double c0, out double c4, out double c8, out double c12);
            return M11 * c0 + M12 * c4 + M13 * c8 + M14 * c12;
        }
    }

    public bool TryInvert(out Matrix4d inverse) => TryInvert(Tolerance.Default, out inverse);

    public bool TryInvert(in Tolerance tolerance, out Matrix4d inverse)
    {
        // Adjugate / determinant (MESA gluInvertMatrix), flat index i = row * 4 + column.
        double m0 = M11, m1 = M12, m2 = M13, m3 = M14;
        double m4 = M21, m5 = M22, m6 = M23, m7 = M24;
        double m8 = M31, m9 = M32, m10 = M33, m11 = M34;
        double m12 = M41, m13 = M42, m14 = M43, m15 = M44;

        double inv0 = m5 * m10 * m15 - m5 * m11 * m14 - m9 * m6 * m15 + m9 * m7 * m14 + m13 * m6 * m11 - m13 * m7 * m10;
        double inv4 = -m4 * m10 * m15 + m4 * m11 * m14 + m8 * m6 * m15 - m8 * m7 * m14 - m12 * m6 * m11 + m12 * m7 * m10;
        double inv8 = m4 * m9 * m15 - m4 * m11 * m13 - m8 * m5 * m15 + m8 * m7 * m13 + m12 * m5 * m11 - m12 * m7 * m9;
        double inv12 = -m4 * m9 * m14 + m4 * m10 * m13 + m8 * m5 * m14 - m8 * m6 * m13 - m12 * m5 * m10 + m12 * m6 * m9;
        double inv1 = -m1 * m10 * m15 + m1 * m11 * m14 + m9 * m2 * m15 - m9 * m3 * m14 - m13 * m2 * m11 + m13 * m3 * m10;
        double inv5 = m0 * m10 * m15 - m0 * m11 * m14 - m8 * m2 * m15 + m8 * m3 * m14 + m12 * m2 * m11 - m12 * m3 * m10;
        double inv9 = -m0 * m9 * m15 + m0 * m11 * m13 + m8 * m1 * m15 - m8 * m3 * m13 - m12 * m1 * m11 + m12 * m3 * m9;
        double inv13 = m0 * m9 * m14 - m0 * m10 * m13 - m8 * m1 * m14 + m8 * m2 * m13 + m12 * m1 * m10 - m12 * m2 * m9;
        double inv2 = m1 * m6 * m15 - m1 * m7 * m14 - m5 * m2 * m15 + m5 * m3 * m14 + m13 * m2 * m7 - m13 * m3 * m6;
        double inv6 = -m0 * m6 * m15 + m0 * m7 * m14 + m4 * m2 * m15 - m4 * m3 * m14 - m12 * m2 * m7 + m12 * m3 * m6;
        double inv10 = m0 * m5 * m15 - m0 * m7 * m13 - m4 * m1 * m15 + m4 * m3 * m13 + m12 * m1 * m7 - m12 * m3 * m5;
        double inv14 = -m0 * m5 * m14 + m0 * m6 * m13 + m4 * m1 * m14 - m4 * m2 * m13 - m12 * m1 * m6 + m12 * m2 * m5;
        double inv3 = -m1 * m6 * m11 + m1 * m7 * m10 + m5 * m2 * m11 - m5 * m3 * m10 - m9 * m2 * m7 + m9 * m3 * m6;
        double inv7 = m0 * m6 * m11 - m0 * m7 * m10 - m4 * m2 * m11 + m4 * m3 * m10 + m8 * m2 * m7 - m8 * m3 * m6;
        double inv11 = -m0 * m5 * m11 + m0 * m7 * m9 + m4 * m1 * m11 - m4 * m3 * m9 - m8 * m1 * m7 + m8 * m3 * m5;
        double inv15 = m0 * m5 * m10 - m0 * m6 * m9 - m4 * m1 * m10 + m4 * m2 * m9 + m8 * m1 * m6 - m8 * m2 * m5;

        double det = m0 * inv0 + m1 * inv4 + m2 * inv8 + m3 * inv12;
        if (Math.Abs(det) <= tolerance.Linear)
        {
            inverse = Identity;
            return false;
        }

        double d = 1.0 / det;
        inverse = new Matrix4d(
            inv0 * d, inv1 * d, inv2 * d, inv3 * d,
            inv4 * d, inv5 * d, inv6 * d, inv7 * d,
            inv8 * d, inv9 * d, inv10 * d, inv11 * d,
            inv12 * d, inv13 * d, inv14 * d, inv15 * d);
        return true;
    }

    public Matrix4d Inverse()
    {
        if (!TryInvert(out var inverse))
            throw new InvalidOperationException("Matrix is singular.");
        return inverse;
    }

    private void ComputeCofactorColumn(out double c0, out double c4, out double c8, out double c12)
    {
        double m4 = M21, m5 = M22, m6 = M23, m7 = M24;
        double m8 = M31, m9 = M32, m10 = M33, m11 = M34;
        double m12 = M41, m13 = M42, m14 = M43, m15 = M44;

        c0 = m5 * m10 * m15 - m5 * m11 * m14 - m9 * m6 * m15 + m9 * m7 * m14 + m13 * m6 * m11 - m13 * m7 * m10;
        c4 = -m4 * m10 * m15 + m4 * m11 * m14 + m8 * m6 * m15 - m8 * m7 * m14 - m12 * m6 * m11 + m12 * m7 * m10;
        c8 = m4 * m9 * m15 - m4 * m11 * m13 - m8 * m5 * m15 + m8 * m7 * m13 + m12 * m5 * m11 - m12 * m7 * m9;
        c12 = -m4 * m9 * m14 + m4 * m10 * m13 + m8 * m5 * m14 - m8 * m6 * m13 - m12 * m5 * m10 + m12 * m6 * m9;
    }

    public bool Equals(Matrix4d o) =>
        M11.Equals(o.M11) && M12.Equals(o.M12) && M13.Equals(o.M13) && M14.Equals(o.M14) &&
        M21.Equals(o.M21) && M22.Equals(o.M22) && M23.Equals(o.M23) && M24.Equals(o.M24) &&
        M31.Equals(o.M31) && M32.Equals(o.M32) && M33.Equals(o.M33) && M34.Equals(o.M34) &&
        M41.Equals(o.M41) && M42.Equals(o.M42) && M43.Equals(o.M43) && M44.Equals(o.M44);

    public override bool Equals(object? obj) => obj is Matrix4d m && Equals(m);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(M11); hash.Add(M12); hash.Add(M13); hash.Add(M14);
        hash.Add(M21); hash.Add(M22); hash.Add(M23); hash.Add(M24);
        hash.Add(M31); hash.Add(M32); hash.Add(M33); hash.Add(M34);
        hash.Add(M41); hash.Add(M42); hash.Add(M43); hash.Add(M44);
        return hash.ToHashCode();
    }

    public static bool operator ==(in Matrix4d a, in Matrix4d b) => a.Equals(b);
    public static bool operator !=(in Matrix4d a, in Matrix4d b) => !a.Equals(b);

    public override string ToString() =>
        $"[{M11} {M12} {M13} {M14}; {M21} {M22} {M23} {M24}; {M31} {M32} {M33} {M34}; {M41} {M42} {M43} {M44}]";
}
