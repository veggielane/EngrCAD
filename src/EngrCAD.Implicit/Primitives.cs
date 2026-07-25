using System.Numerics;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Exact primitive distance functions (Inigo Quilez's canonical forms), Z-up.
//
// Each primitive pairs its scalar Evaluate with an ISdfKernel that mirrors it term for
// term for the batch/SIMD seam (see BatchEvaluation.cs for the layout and exactness
// contract). Keep both in sync: the kernel is the same expression with Math.* replaced by
// Vector.*, in the same association order, so the two agree bit for bit.

internal sealed class SphereSdf(double radius) : Sdf
{
    public override double Evaluate(in Vector3d p) => p.Length - radius;

    public override Aabb Bounds => new((-radius, -radius, -radius), (radius, radius, radius));

    private readonly struct Kernel(double radius) : ISdfKernel
    {
        private readonly Vector<double> _radius = new(radius);

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z) =>
            Vector.SquareRoot(x * x + y * y + z * z) - _radius;
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(radius), x, y, z, distances, this);
}

internal sealed class BoxSdf(Vector3d halfExtents) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double qx = Math.Abs(p.X) - halfExtents.X;
        double qy = Math.Abs(p.Y) - halfExtents.Y;
        double qz = Math.Abs(p.Z) - halfExtents.Z;
        double outside = new Vector3d(Math.Max(qx, 0), Math.Max(qy, 0), Math.Max(qz, 0)).Length;
        double inside = Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
        return outside + inside;
    }

    public override Aabb Bounds => new(-halfExtents, halfExtents);

    private readonly struct Kernel(Vector3d halfExtents) : ISdfKernel
    {
        private readonly Vector<double> _hx = new(halfExtents.X);
        private readonly Vector<double> _hy = new(halfExtents.Y);
        private readonly Vector<double> _hz = new(halfExtents.Z);

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z)
        {
            var zero = Vector<double>.Zero;
            var qx = Vector.Abs(x) - _hx;
            var qy = Vector.Abs(y) - _hy;
            var qz = Vector.Abs(z) - _hz;
            var ox = Vector.Max(qx, zero);
            var oy = Vector.Max(qy, zero);
            var oz = Vector.Max(qz, zero);
            var outside = Vector.SquareRoot(ox * ox + oy * oy + oz * oz);
            var inside = Vector.Min(Vector.Max(qx, Vector.Max(qy, qz)), zero);
            return outside + inside;
        }
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(halfExtents), x, y, z, distances, this);
}

internal sealed class CylinderSdf(double radius, double halfHeight) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double dRadial = Math.Sqrt(p.X * p.X + p.Y * p.Y) - radius;
        double dAxial = Math.Abs(p.Z) - halfHeight;
        double outside = Math.Sqrt(
            Math.Max(dRadial, 0) * Math.Max(dRadial, 0) +
            Math.Max(dAxial, 0) * Math.Max(dAxial, 0));
        double inside = Math.Min(Math.Max(dRadial, dAxial), 0);
        return outside + inside;
    }

    public override Aabb Bounds => new((-radius, -radius, -halfHeight), (radius, radius, halfHeight));

    private readonly struct Kernel(double radius, double halfHeight) : ISdfKernel
    {
        private readonly Vector<double> _radius = new(radius);
        private readonly Vector<double> _halfHeight = new(halfHeight);

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z)
        {
            var zero = Vector<double>.Zero;
            var dRadial = Vector.SquareRoot(x * x + y * y) - _radius;
            var dAxial = Vector.Abs(z) - _halfHeight;
            var mr = Vector.Max(dRadial, zero);
            var ma = Vector.Max(dAxial, zero);
            var outside = Vector.SquareRoot(mr * mr + ma * ma);
            var inside = Vector.Min(Vector.Max(dRadial, dAxial), zero);
            return outside + inside;
        }
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(radius, halfHeight), x, y, z, distances, this);
}

/// <summary>
/// Capped cone (frustum) along Z: radius <c>bottomRadius</c> at z = −h, <c>topRadius</c>
/// at z = +h (Quilez's exact sdCappedCone, converted to Z-up). Exact for any radii,
/// including a zero radius (pointed apex) and equal radii (a cylinder).
/// </summary>
internal sealed class ConeSdf(double bottomRadius, double topRadius, double halfHeight) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double qx = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        double qy = p.Z;
        double h = halfHeight;
        // ca: distance to the nearer cap disk (radially clamped); cb: distance to the
        // slanted side segment from (bottomRadius, −h) to (topRadius, +h).
        double cax = qx - Math.Min(qx, qy < 0 ? bottomRadius : topRadius);
        double cay = Math.Abs(qy) - h;
        double k2x = topRadius - bottomRadius;
        double k2y = 2 * h;
        double t = Math.Clamp(
            ((topRadius - qx) * k2x + (h - qy) * k2y) / (k2x * k2x + k2y * k2y), 0, 1);
        double cbx = qx - topRadius + k2x * t;
        double cby = qy - h + k2y * t;
        double s = cbx < 0 && cay < 0 ? -1 : 1;
        return s * Math.Sqrt(Math.Min(cax * cax + cay * cay, cbx * cbx + cby * cby));
    }

    public override Aabb Bounds
    {
        get
        {
            double r = Math.Max(bottomRadius, topRadius);
            return new Aabb((-r, -r, -halfHeight), (r, r, halfHeight));
        }
    }

    private readonly struct Kernel : ISdfKernel
    {
        private readonly Vector<double> _bottomRadius, _topRadius, _h, _k2x, _k2y, _denominator;

        public Kernel(double bottomRadius, double topRadius, double halfHeight)
        {
            double k2x = topRadius - bottomRadius;
            double k2y = 2 * halfHeight;
            _bottomRadius = new Vector<double>(bottomRadius);
            _topRadius = new Vector<double>(topRadius);
            _h = new Vector<double>(halfHeight);
            _k2x = new Vector<double>(k2x);
            _k2y = new Vector<double>(k2y);
            // The scalar path divides by this exact sum, so keep the division (not a
            // reciprocal multiply) to stay bit-identical.
            _denominator = new Vector<double>(k2x * k2x + k2y * k2y);
        }

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z)
        {
            var zero = Vector<double>.Zero;
            var qx = Vector.SquareRoot(x * x + y * y);
            var qy = z;
            var radius = Vector.ConditionalSelect(Vector.LessThan(qy, zero), _bottomRadius, _topRadius);
            var cax = qx - Vector.Min(qx, radius);
            var cay = Vector.Abs(qy) - _h;
            var t = SdfBatch.Clamp01(((_topRadius - qx) * _k2x + (_h - qy) * _k2y) / _denominator);
            var cbx = qx - _topRadius + _k2x * t;
            var cby = qy - _h + _k2y * t;
            var negative = Vector.BitwiseAnd(Vector.LessThan(cbx, zero), Vector.LessThan(cay, zero));
            var s = Vector.ConditionalSelect(negative, new Vector<double>(-1.0), Vector<double>.One);
            return s * Vector.SquareRoot(Vector.Min(cax * cax + cay * cay, cbx * cbx + cby * cby));
        }
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(bottomRadius, topRadius, halfHeight), x, y, z, distances, this);
}

internal sealed class TorusSdf(double majorRadius, double minorRadius) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double ring = Math.Sqrt(p.X * p.X + p.Y * p.Y) - majorRadius;
        return Math.Sqrt(ring * ring + p.Z * p.Z) - minorRadius;
    }

    public override Aabb Bounds
    {
        get
        {
            double r = majorRadius + minorRadius;
            return new Aabb((-r, -r, -minorRadius), (r, r, minorRadius));
        }
    }

    private readonly struct Kernel(double majorRadius, double minorRadius) : ISdfKernel
    {
        private readonly Vector<double> _major = new(majorRadius);
        private readonly Vector<double> _minor = new(minorRadius);

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z)
        {
            var ring = Vector.SquareRoot(x * x + y * y) - _major;
            return Vector.SquareRoot(ring * ring + z * z) - _minor;
        }
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(majorRadius, minorRadius), x, y, z, distances, this);
}

internal sealed class CapsuleSdf(Vector3d a, Vector3d b, double radius) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        var pa = p - a;
        var ba = b - a;
        double h = Math.Clamp(pa.Dot(ba) / ba.LengthSquared, 0, 1);
        return (pa - ba * h).Length - radius;
    }

    public override Aabb Bounds =>
        new Aabb(Vector3d.Min(a, b), Vector3d.Max(a, b)).Expanded(radius);

    private readonly struct Kernel : ISdfKernel
    {
        private readonly Vector<double> _ax, _ay, _az, _bax, _bay, _baz, _baLengthSquared, _radius;

        public Kernel(Vector3d a, Vector3d b, double radius)
        {
            var ba = b - a;
            _ax = new Vector<double>(a.X);
            _ay = new Vector<double>(a.Y);
            _az = new Vector<double>(a.Z);
            _bax = new Vector<double>(ba.X);
            _bay = new Vector<double>(ba.Y);
            _baz = new Vector<double>(ba.Z);
            _baLengthSquared = new Vector<double>(ba.LengthSquared);
            _radius = new Vector<double>(radius);
        }

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z)
        {
            var px = x - _ax;
            var py = y - _ay;
            var pz = z - _az;
            var h = SdfBatch.Clamp01((px * _bax + py * _bay + pz * _baz) / _baLengthSquared);
            var dx = px - _bax * h;
            var dy = py - _bay * h;
            var dz = pz - _baz * h;
            return Vector.SquareRoot(dx * dx + dy * dy + dz * dz) - _radius;
        }
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(a, b, radius), x, y, z, distances, this);
}

internal sealed class HalfSpaceSdf(Vector3d unitNormal, double offset) : Sdf
{
    public override double Evaluate(in Vector3d p) => unitNormal.Dot(p) - offset;

    public override Aabb Bounds => InfiniteBounds;

    private readonly struct Kernel(Vector3d unitNormal, double offset) : ISdfKernel
    {
        private readonly Vector<double> _nx = new(unitNormal.X);
        private readonly Vector<double> _ny = new(unitNormal.Y);
        private readonly Vector<double> _nz = new(unitNormal.Z);
        private readonly Vector<double> _offset = new(offset);

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z) =>
            _nx * x + _ny * y + _nz * z - _offset;
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(unitNormal, offset), x, y, z, distances, this);
}

/// <remarks>
/// Deliberately NOT vectorized: the field is built from <see cref="Math.Sin"/>/
/// <see cref="Math.Cos"/> and no vector transcendental reproduces them bit for bit, so a
/// SIMD gyroid would silently disagree with the scalar path. It still batches (the base
/// <see cref="Sdf.EvaluateBatch"/> loop) and still benefits from vectorized operands
/// wherever it is intersected with a finite solid.
/// </remarks>
internal sealed class GyroidSdf(double cellSize, double thickness) : Sdf
{
    // g has gradient magnitude ≤ √3·ω, so |g|/(√3·ω) is a conservative distance bound.
    private readonly double _omega = 2 * Math.PI / cellSize;

    public override double Evaluate(in Vector3d p)
    {
        double x = p.X * _omega, y = p.Y * _omega, z = p.Z * _omega;
        double g = Math.Sin(x) * Math.Cos(y) + Math.Sin(y) * Math.Cos(z) + Math.Sin(z) * Math.Cos(x);
        return Math.Abs(g) / (Math.Sqrt(3) * _omega) - thickness / 2;
    }

    public override Aabb Bounds => InfiniteBounds;
}
