using System.Linq.Expressions;
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

    internal override Expression BuildExpression(SdfExpression b) =>
        Expression.Subtract(SdfExpression.Length3(b.X, b.Y, b.Z), SdfExpression.Const(radius));
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

    internal override Expression BuildExpression(SdfExpression b) =>
        BoxBody(b, halfExtents, 0);

    /// <summary>The box body, shared with <see cref="RoundedBoxSdf"/> — the rounded form is
    /// this minus a constant, so one expression serves both and cannot drift from the other.</summary>
    internal static Expression BoxBody(SdfExpression b, in Vector3d half, double radius)
    {
        var qx = b.Let(Expression.Subtract(SdfExpression.Abs(b.X), SdfExpression.Const(half.X)));
        var qy = b.Let(Expression.Subtract(SdfExpression.Abs(b.Y), SdfExpression.Const(half.Y)));
        var qz = b.Let(Expression.Subtract(SdfExpression.Abs(b.Z), SdfExpression.Const(half.Z)));
        var zero = SdfExpression.Const(0);
        var outside = SdfExpression.Length3(
            SdfExpression.Max(qx, zero), SdfExpression.Max(qy, zero), SdfExpression.Max(qz, zero));
        var inside = SdfExpression.Min(
            SdfExpression.Max(qx, SdfExpression.Max(qy, qz)), zero);
        var sum = Expression.Add(outside, inside);
        return radius == 0 ? sum : Expression.Subtract(sum, SdfExpression.Const(radius));
    }
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

    internal override Expression BuildExpression(SdfExpression b)
    {
        var zero = SdfExpression.Const(0);
        var dRadial = b.Let(Expression.Subtract(
            SdfExpression.Length2(b.X, b.Y), SdfExpression.Const(radius)));
        var dAxial = b.Let(Expression.Subtract(
            SdfExpression.Abs(b.Z), SdfExpression.Const(halfHeight)));
        var outside = SdfExpression.Sqrt(Expression.Add(
            SdfExpression.Square(SdfExpression.Max(dRadial, zero)),
            SdfExpression.Square(SdfExpression.Max(dAxial, zero))));
        var inside = SdfExpression.Min(SdfExpression.Max(dRadial, dAxial), zero);
        return Expression.Add(outside, inside);
    }
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

    internal override Expression BuildExpression(SdfExpression b)
    {
        double k2x = topRadius - bottomRadius;
        double k2y = 2 * halfHeight;
        var zero = SdfExpression.Const(0);
        var qx = b.Let(SdfExpression.Length2(b.X, b.Y));
        var qy = b.Z;
        var radius = Expression.Condition(
            Expression.LessThan(qy, zero),
            SdfExpression.Const(bottomRadius), SdfExpression.Const(topRadius));
        var cax = b.Let(Expression.Subtract(qx, SdfExpression.Min(qx, radius)));
        var cay = b.Let(Expression.Subtract(SdfExpression.Abs(qy), SdfExpression.Const(halfHeight)));
        var t = b.Let(SdfExpression.Clamp(
            Expression.Divide(
                Expression.Add(
                    Expression.Multiply(
                        Expression.Subtract(SdfExpression.Const(topRadius), qx), SdfExpression.Const(k2x)),
                    Expression.Multiply(
                        Expression.Subtract(SdfExpression.Const(halfHeight), qy), SdfExpression.Const(k2y))),
                SdfExpression.Const(k2x * k2x + k2y * k2y)),
            0, 1));
        var cbx = b.Let(Expression.Add(
            Expression.Subtract(qx, SdfExpression.Const(topRadius)),
            Expression.Multiply(SdfExpression.Const(k2x), t)));
        var cby = b.Let(Expression.Add(
            Expression.Subtract(qy, SdfExpression.Const(halfHeight)),
            Expression.Multiply(SdfExpression.Const(k2y), t)));
        var s = Expression.Condition(
            Expression.AndAlso(Expression.LessThan(cbx, zero), Expression.LessThan(cay, zero)),
            SdfExpression.Const(-1), SdfExpression.Const(1));
        return Expression.Multiply(s, SdfExpression.Sqrt(SdfExpression.Min(
            Expression.Add(SdfExpression.Square(cax), SdfExpression.Square(cay)),
            Expression.Add(SdfExpression.Square(cbx), SdfExpression.Square(cby)))));
    }
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

    internal override Expression BuildExpression(SdfExpression b)
    {
        var ring = b.Let(Expression.Subtract(
            SdfExpression.Length2(b.X, b.Y), SdfExpression.Const(majorRadius)));
        return Expression.Subtract(
            SdfExpression.Length2(ring, b.Z), SdfExpression.Const(minorRadius));
    }
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

    internal override Expression BuildExpression(SdfExpression e)
    {
        var ba = b - a;
        var px = e.Let(Expression.Subtract(e.X, SdfExpression.Const(a.X)));
        var py = e.Let(Expression.Subtract(e.Y, SdfExpression.Const(a.Y)));
        var pz = e.Let(Expression.Subtract(e.Z, SdfExpression.Const(a.Z)));
        // Dot's own association: (x*bx + y*by) + z*bz.
        var dot = SdfExpression.Add(
            Expression.Multiply(px, SdfExpression.Const(ba.X)),
            Expression.Multiply(py, SdfExpression.Const(ba.Y)),
            Expression.Multiply(pz, SdfExpression.Const(ba.Z)));
        var h = e.Let(SdfExpression.Clamp(
            Expression.Divide(dot, SdfExpression.Const(ba.LengthSquared)), 0, 1));
        return Expression.Subtract(
            SdfExpression.Length3(
                Expression.Subtract(px, Expression.Multiply(SdfExpression.Const(ba.X), h)),
                Expression.Subtract(py, Expression.Multiply(SdfExpression.Const(ba.Y), h)),
                Expression.Subtract(pz, Expression.Multiply(SdfExpression.Const(ba.Z), h))),
            SdfExpression.Const(radius));
    }
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

    internal override Expression BuildExpression(SdfExpression b) =>
        Expression.Subtract(
            SdfExpression.Add(
                Expression.Multiply(SdfExpression.Const(unitNormal.X), b.X),
                Expression.Multiply(SdfExpression.Const(unitNormal.Y), b.Y),
                Expression.Multiply(SdfExpression.Const(unitNormal.Z), b.Z)),
            SdfExpression.Const(offset));
}

/// <summary>
/// Box with rounded edges and corners: the box of half-extents
/// <paramref name="innerHalfExtents"/> grown outward by <paramref name="radius"/>. Exact,
/// because an outward offset of an exact field is exact and the inner box's field is exact.
/// </summary>
internal sealed class RoundedBoxSdf(Vector3d innerHalfExtents, double radius) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double qx = Math.Abs(p.X) - innerHalfExtents.X;
        double qy = Math.Abs(p.Y) - innerHalfExtents.Y;
        double qz = Math.Abs(p.Z) - innerHalfExtents.Z;
        double outside = new Vector3d(Math.Max(qx, 0), Math.Max(qy, 0), Math.Max(qz, 0)).Length;
        double inside = Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
        return outside + inside - radius;
    }

    public override Aabb Bounds
    {
        get
        {
            var half = innerHalfExtents + new Vector3d(radius, radius, radius);
            return new Aabb(-half, half);
        }
    }

    private readonly struct Kernel(Vector3d halfExtents, double radius) : ISdfKernel
    {
        private readonly Vector<double> _hx = new(halfExtents.X);
        private readonly Vector<double> _hy = new(halfExtents.Y);
        private readonly Vector<double> _hz = new(halfExtents.Z);
        private readonly Vector<double> _radius = new(radius);

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
            return outside + inside - _radius;
        }
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(innerHalfExtents, radius), x, y, z, distances, this);

    internal override Expression BuildExpression(SdfExpression b) =>
        BoxSdf.BoxBody(b, innerHalfExtents, radius);
}

/// <summary>
/// Axis-aligned ellipsoid, by Quilez's scaled-implicit approximation
/// <c>k0·(k0 − 1)/k1</c> with <c>k0 = |p/r|</c> and <c>k1 = |p/r²|</c>.
/// <para>
/// <b>Why an approximation at all.</b> The distance from a point to an ellipsoid is the root
/// of a degree-6 polynomial in the Lagrange multiplier (degree 4 in 2D), with no closed
/// form; every practical ellipsoid SDF is therefore a bound. This one is the implicit
/// function scaled by its own gradient — a first-order estimate of the distance to the level
/// set — and it is exact in two regimes for a structural reason rather than by luck: with
/// equal semi-axes the expression reduces algebraically to <c>|p| − r</c>, the sphere's exact
/// distance (agreeing to round-off, since the value still travels through two divisions), and
/// far from the body the level sets round out so the ratio tends to the true distance.
/// </para>
/// <para>
/// <b>The error IS one-sided outside and is not small at high eccentricity</b> — measured
/// against an exact oracle (the Lagrange-multiplier solve, bisected to machine precision)
/// rather than quoted as folklore. Reported over true distance, at aspect ratios 1 / 1.25 /
/// 1.67 / 3 / 4 / 10:
/// <list type="bullet">
///   <item><description><b>Outside: 1.000 / 0.983 / 0.916 / 0.675 / 0.544 / 0.238</b> — always
///   at or below 1, so outside the solid this is a genuine <em>lower bound</em>, the engine's
///   standing contract, and it under-reports by a factor that tracks the eccentricity.</description></item>
///   <item><description><b>Inside: 1.000 / 1.081 / 1.369 / 2.076 / 2.673 / 6.585</b> — it
///   over-reports DEPTH, so it is not a lower bound there. Harmless for meshing and for the
///   block cull (both argue from the Lipschitz bound, not from one-sidedness) and the reason
///   the projection target divides its step by that bound.</description></item>
/// </list>
/// The sign is exact everywhere, which is what boolean classification and polygonization need.
/// </para>
/// <para>
/// One genuine singularity: at the exact centre both k0 and k1 vanish and the limit is
/// direction-dependent, ranging over the semi-axes. The value returned there is
/// <c>−min(semi-axis)</c>, which is the true distance to the boundary from the centre.
/// </para>
/// </summary>
internal sealed class EllipsoidSdf(Vector3d radii) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double k0 = new Vector3d(p.X / radii.X, p.Y / radii.Y, p.Z / radii.Z).Length;
        double k1 = new Vector3d(
            p.X / (radii.X * radii.X),
            p.Y / (radii.Y * radii.Y),
            p.Z / (radii.Z * radii.Z)).Length;
        if (k1 == 0)
            return -Math.Min(radii.X, Math.Min(radii.Y, radii.Z));
        return k0 * (k0 - 1) / k1;
    }

    public override Aabb Bounds => new(-radii, radii);

    /// <summary>
    /// <c>2 + (rmax/rmin)²</c>, and it is derived rather than fitted. Writing t = 1/k0, the
    /// product and quotient rules give
    /// <c>|∇V| ≤ |2 − t| + |1 − t|·|∇k1|·k0²/k1²</c>, and two substitutions close it: every
    /// term of ∇k1 carries a factor <c>1/r_i² ≤ 1/rmin²</c> so <c>|∇k1| ≤ k1/rmin²</c>, while
    /// <c>k1 ≥ k0/rmax</c> gives <c>k0²/k1² ≤ rmax²</c>. For <c>k0 ≥ ½</c> — everywhere but
    /// the inner half of the solid, where the field is anyway approaching its documented
    /// centre singularity — that leaves <c>2 + (rmax/rmin)²</c>.
    /// <para>
    /// The slack is real and stated: measured against it, the true supremum runs 0.26–0.55 of
    /// the bound over aspect ratios 1 to 10 (1.00 against 3, 1.26 against 3.56, 41.4 against
    /// 102), because both substitutions are worst-case simultaneously only at points that do
    /// not exist. A tighter bound would narrow the cull shell for eccentric ellipsoids; it is
    /// filed rather than guessed at, since a Lipschitz bound that is too small drops geometry.
    /// </para>
    /// </summary>
    public override double LipschitzBound(in Aabb region)
    {
        double aspect =
            Math.Max(radii.X, Math.Max(radii.Y, radii.Z)) / Math.Min(radii.X, Math.Min(radii.Y, radii.Z));
        return 2 + aspect * aspect;
    }

    private readonly struct Kernel(Vector3d radii) : ISdfKernel
    {
        private readonly Vector<double> _rx = new(radii.X);
        private readonly Vector<double> _ry = new(radii.Y);
        private readonly Vector<double> _rz = new(radii.Z);
        private readonly Vector<double> _sx = new(radii.X * radii.X);
        private readonly Vector<double> _sy = new(radii.Y * radii.Y);
        private readonly Vector<double> _sz = new(radii.Z * radii.Z);
        private readonly Vector<double> _centre =
            new(-Math.Min(radii.X, Math.Min(radii.Y, radii.Z)));

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z)
        {
            var ax = x / _rx;
            var ay = y / _ry;
            var az = z / _rz;
            var k0 = Vector.SquareRoot(ax * ax + ay * ay + az * az);
            var bx = x / _sx;
            var by = y / _sy;
            var bz = z / _sz;
            var k1 = Vector.SquareRoot(bx * bx + by * by + bz * bz);
            var value = k0 * (k0 - Vector<double>.One) / k1;
            // Mirrors the scalar guard exactly, including which comparison it uses.
            return Vector.ConditionalSelect(
                Vector.Equals(k1, Vector<double>.Zero), _centre, value);
        }
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(radii), x, y, z, distances, this);

    internal override Expression BuildExpression(SdfExpression b)
    {
        var k0 = b.Let(SdfExpression.Length3(
            Expression.Divide(b.X, SdfExpression.Const(radii.X)),
            Expression.Divide(b.Y, SdfExpression.Const(radii.Y)),
            Expression.Divide(b.Z, SdfExpression.Const(radii.Z))));
        var k1 = b.Let(SdfExpression.Length3(
            Expression.Divide(b.X, SdfExpression.Const(radii.X * radii.X)),
            Expression.Divide(b.Y, SdfExpression.Const(radii.Y * radii.Y)),
            Expression.Divide(b.Z, SdfExpression.Const(radii.Z * radii.Z))));
        return Expression.Condition(
            Expression.Equal(k1, SdfExpression.Const(0)),
            SdfExpression.Const(-Math.Min(radii.X, Math.Min(radii.Y, radii.Z))),
            Expression.Divide(
                Expression.Multiply(k0, Expression.Subtract(k0, SdfExpression.Const(1))), k1));
    }
}

/// <summary>
/// Square-based pyramid along +Z: base <c>size</c> square in the z = 0 plane, apex at
/// (0, 0, height). <b>Exact everywhere.</b>
/// <para>
/// <b>The closed form usually quoted for this shape is NOT usable here, and the measurement
/// is why.</b> Quilez's <c>sdPyramid</c> computes the distance to the pyramid's LATERAL
/// surface and uses the base plane only to decide the sign — so wherever the base FACE is the
/// nearest feature it reports the lateral distance instead. Measured on a 10-wide, 12-tall
/// pyramid: directly below the base centre at (0, 0, −3) it returns <b>5.831 against a true
/// 3.0</b>, and just inside the base at (0, 0, 0.5) it returns −4.423 against a true −0.5.
/// Both are OVER-estimates, which is the one direction this engine cannot absorb: the
/// polygonizer's cull, the narrow-band octree and the projection step all read |d| as a
/// promise that no surface is nearer than that, so an over-estimate drops geometry silently.
/// (It is a correct field for a different solid — the same pyramid with its base removed and
/// its lateral faces extended downward, which is what the two figures above measure exactly.)
/// </para>
/// <para>
/// So the distance is taken over the boundary itself: the minimum over the six triangles of
/// the pyramid's surface, through <see cref="Distance3d.ClosestPointOnTriangle"/> — Core's
/// one nearest-point-on-triangle rule, asked rather than restated — with the sign from the
/// five bounding half-planes, which for a convex solid is exact. That costs six Voronoi-region
/// tests per query where a closed form would cost one, and it is the price of a field that
/// means what the rest of the engine assumes it means.
/// </para>
/// </summary>
internal sealed class PyramidSdf : Sdf
{
    private readonly double _baseSize, _height;
    private readonly (Vector3d A, Vector3d B, Vector3d C)[] _triangles;
    private readonly (Vector3d Normal, double Offset)[] _planes;

    public PyramidSdf(double baseSize, double height)
    {
        _baseSize = baseSize;
        _height = height;
        double h = baseSize / 2;
        Vector3d apex = (0, 0, height);
        Vector3d[] corners = [(-h, -h, 0), (h, -h, 0), (h, h, 0), (-h, h, 0)];

        _triangles =
        [
            (corners[0], corners[2], corners[1]),
            (corners[0], corners[3], corners[2]),
            (corners[0], corners[1], apex),
            (corners[1], corners[2], apex),
            (corners[2], corners[3], apex),
            (corners[3], corners[0], apex),
        ];

        var planes = new List<(Vector3d, double)> { ((0, 0, -1), 0) };
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            var n = (b - a).Cross(apex - a).Normalized();
            if (n.Dot(Vector3d.Zero - a) > 0)
                n = -n;    // outward: the centroid side must be negative
            planes.Add((n, n.Dot(a)));
        }
        _planes = [.. planes];
    }

    public override double Evaluate(in Vector3d p)
    {
        double best = double.PositiveInfinity;
        foreach (var (a, b, c) in _triangles)
            best = Math.Min(best, (Distance3d.ClosestPointOnTriangle(p, a, b, c) - p).LengthSquared);

        // Convex, so "inside" is "on the inner side of every face plane" — exact, no parity.
        bool inside = true;
        foreach (var (n, d) in _planes)
        {
            if (n.Dot(p) - d > 0)
            {
                inside = false;
                break;
            }
        }
        double distance = Math.Sqrt(best);
        return inside ? -distance : distance;
    }

    public override Aabb Bounds =>
        new((-_baseSize / 2, -_baseSize / 2, 0), (_baseSize / 2, _baseSize / 2, _height));
}

/// <summary>
/// The convex hull of two spheres along Z — a capsule whose ends differ in radius, so the
/// side is the cone tangent to both. Exact (Quilez's <c>sdRoundCone</c>): the field is the
/// distance to whichever of the three features (either cap or the tangent side) is nearest,
/// and the branch between them is decided by one dot product against the tangency direction.
/// </summary>
internal sealed class RoundConeSdf(double bottomRadius, double topRadius, double height) : Sdf
{
    // The tangent line's direction in the (radius, axial) half-plane.
    private readonly double _b = (bottomRadius - topRadius) / height;

    public override double Evaluate(in Vector3d p)
    {
        double qx = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        double qy = p.Z;
        double b = _b;
        double a = Math.Sqrt(1 - b * b);
        double k = qx * -b + qy * a;

        if (k < 0)
            return Math.Sqrt(qx * qx + qy * qy) - bottomRadius;
        if (k > a * height)
            return Math.Sqrt(qx * qx + (qy - height) * (qy - height)) - topRadius;
        return qx * a + qy * b - bottomRadius;
    }

    public override Aabb Bounds
    {
        get
        {
            double lo = -bottomRadius, hi = height + topRadius;
            double r = Math.Max(bottomRadius, topRadius);
            return new Aabb((-r, -r, lo), (r, r, hi));
        }
    }
}

/// <summary>
/// A chain link: the torus's exact field elongated along Y, which is exact because the
/// elongation map is a translation on each side of the split (Quilez's <c>sdLink</c>).
/// </summary>
internal sealed class LinkSdf(double majorRadius, double minorRadius, double halfLength) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double qy = Math.Max(Math.Abs(p.Y) - halfLength, 0);
        double ring = Math.Sqrt(p.X * p.X + qy * qy) - majorRadius;
        return Math.Sqrt(ring * ring + p.Z * p.Z) - minorRadius;
    }

    public override Aabb Bounds
    {
        get
        {
            double r = majorRadius + minorRadius;
            return new Aabb((-r, -(r + halfLength), -minorRadius), (r, r + halfLength, minorRadius));
        }
    }

    private readonly struct Kernel(double majorRadius, double minorRadius, double halfLength) : ISdfKernel
    {
        private readonly Vector<double> _major = new(majorRadius);
        private readonly Vector<double> _minor = new(minorRadius);
        private readonly Vector<double> _half = new(halfLength);

        public Vector<double> Evaluate(Vector<double> x, Vector<double> y, Vector<double> z)
        {
            var qy = Vector.Max(Vector.Abs(y) - _half, Vector<double>.Zero);
            var ring = Vector.SquareRoot(x * x + qy * qy) - _major;
            return Vector.SquareRoot(ring * ring + z * z) - _minor;
        }
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances) =>
        SdfBatch.Map(new Kernel(majorRadius, minorRadius, halfLength), x, y, z, distances, this);

    internal override Expression BuildExpression(SdfExpression b)
    {
        var qy = b.Let(SdfExpression.Max(
            Expression.Subtract(SdfExpression.Abs(b.Y), SdfExpression.Const(halfLength)),
            SdfExpression.Const(0)));
        var ring = b.Let(Expression.Subtract(
            SdfExpression.Length2(b.X, qy), SdfExpression.Const(majorRadius)));
        return Expression.Subtract(
            SdfExpression.Length2(ring, b.Z), SdfExpression.Const(minorRadius));
    }
}

// The gyroid moved to Tpms.cs, where it is one member of the triply periodic minimal
// surface family rather than a lone special case. Sdf.Gyroid still names it, and its field
// is bit-for-bit what it always was (asserted against a transcription of the closed form
// this file used to carry).
