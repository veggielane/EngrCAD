namespace EngrCAD.Core;

/// <summary>
/// Double-precision quaternion for representing rotations. (X, Y, Z) is the vector part,
/// W the scalar part. Rotation quaternions are expected to be unit length.
/// </summary>
public readonly struct Quaterniond : IEquatable<Quaterniond>
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public double W { get; }

    public Quaterniond(double x, double y, double z, double w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public static readonly Quaterniond Identity = new(0, 0, 0, 1);

    /// <summary>Rotation of <paramref name="radians"/> about a unit <paramref name="axis"/>.</summary>
    public static Quaterniond FromAxisAngle(in Vector3d axis, double radians)
    {
        double half = radians * 0.5;
        double s = Math.Sin(half);
        return new Quaterniond(axis.X * s, axis.Y * s, axis.Z * s, Math.Cos(half));
    }

    public double LengthSquared => X * X + Y * Y + Z * Z + W * W;
    public double Length => Math.Sqrt(LengthSquared);

    public Quaterniond Conjugate => new(-X, -Y, -Z, W);

    public double Dot(in Quaterniond other) => X * other.X + Y * other.Y + Z * other.Z + W * other.W;

    public Quaterniond Normalized()
    {
        double length = Length;
        if (length <= Tolerance.Default.Linear)
            throw new InvalidOperationException("Cannot normalize a zero-length quaternion.");
        double inv = 1.0 / length;
        return new Quaterniond(X * inv, Y * inv, Z * inv, W * inv);
    }

    /// <summary>Hamilton product; a * b applies b first, then a (matching matrix convention).</summary>
    public static Quaterniond operator *(in Quaterniond a, in Quaterniond b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>Rotates a vector: q · v · q⁻¹, assuming this quaternion is unit length.</summary>
    public Vector3d Rotate(in Vector3d v)
    {
        var u = new Vector3d(X, Y, Z);
        var t = 2.0 * u.Cross(v);
        return v + W * t + u.Cross(t);
    }

    /// <summary>
    /// The unit rotation quaternion of <paramref name="m"/>'s upper-left 3×3, which must
    /// be a pure rotation (orthonormal, det +1) up to round-off. Shepperd's method:
    /// branch on the largest of the trace and the three diagonal entries so the divisor
    /// is never small — every branch's square root argument is at least 1 for the branch
    /// that wins, so no orientation loses precision. The result is normalized, so a
    /// nearly-rigid input yields the nearest unit quaternion rather than a drifting one.
    /// </summary>
    public static Quaterniond FromRotationMatrix(in Matrix4d m)
    {
        double trace = m.M11 + m.M22 + m.M33;
        double x, y, z, w;
        if (trace > m.M11 && trace > m.M22 && trace > m.M33)
        {
            double s = Math.Sqrt(trace + 1.0) * 2; // 4w
            w = 0.25 * s;
            x = (m.M32 - m.M23) / s;
            y = (m.M13 - m.M31) / s;
            z = (m.M21 - m.M12) / s;
        }
        else if (m.M11 >= m.M22 && m.M11 >= m.M33)
        {
            double s = Math.Sqrt(1.0 + m.M11 - m.M22 - m.M33) * 2; // 4x
            w = (m.M32 - m.M23) / s;
            x = 0.25 * s;
            y = (m.M12 + m.M21) / s;
            z = (m.M13 + m.M31) / s;
        }
        else if (m.M22 >= m.M33)
        {
            double s = Math.Sqrt(1.0 + m.M22 - m.M11 - m.M33) * 2; // 4y
            w = (m.M13 - m.M31) / s;
            x = (m.M12 + m.M21) / s;
            y = 0.25 * s;
            z = (m.M23 + m.M32) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + m.M33 - m.M11 - m.M22) * 2; // 4z
            w = (m.M21 - m.M12) / s;
            x = (m.M13 + m.M31) / s;
            y = (m.M23 + m.M32) / s;
            z = 0.25 * s;
        }
        return new Quaterniond(x, y, z, w).Normalized();
    }

    /// <summary>Rotation matrix equivalent, assuming this quaternion is unit length.</summary>
    public Matrix4d ToMatrix()
    {
        double xx = X * X, yy = Y * Y, zz = Z * Z;
        double xy = X * Y, xz = X * Z, yz = Y * Z;
        double wx = W * X, wy = W * Y, wz = W * Z;
        return new Matrix4d(
            1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy), 0,
            2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx), 0,
            2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy), 0,
            0, 0, 0, 1);
    }

    /// <summary>Spherical linear interpolation along the shortest arc.</summary>
    public static Quaterniond Slerp(in Quaterniond a, in Quaterniond b, double t)
    {
        double dot = a.Dot(b);
        var end = b;
        if (dot < 0)
        {
            // Negate to take the shorter of the two arcs.
            end = new Quaterniond(-b.X, -b.Y, -b.Z, -b.W);
            dot = -dot;
        }

        double s0, s1;
        if (dot > 1 - 1e-12)
        {
            // Nearly identical: fall back to lerp to avoid division by sin(0).
            s0 = 1 - t;
            s1 = t;
        }
        else
        {
            double theta = Math.Acos(dot);
            double invSin = 1.0 / Math.Sin(theta);
            s0 = Math.Sin((1 - t) * theta) * invSin;
            s1 = Math.Sin(t * theta) * invSin;
        }

        return new Quaterniond(
            s0 * a.X + s1 * end.X,
            s0 * a.Y + s1 * end.Y,
            s0 * a.Z + s1 * end.Z,
            s0 * a.W + s1 * end.W).Normalized();
    }

    public bool Equals(Quaterniond other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);

    public override bool Equals(object? obj) => obj is Quaterniond q && Equals(q);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    public static bool operator ==(in Quaterniond a, in Quaterniond b) => a.Equals(b);
    public static bool operator !=(in Quaterniond a, in Quaterniond b) => !a.Equals(b);

    public override string ToString() => $"({X:G17}, {Y:G17}, {Z:G17}; {W:G17})";
}
