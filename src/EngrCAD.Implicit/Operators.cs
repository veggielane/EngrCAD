using System.Linq.Expressions;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Set operations, blends, modifiers, and rigid/uniform transforms over SDF nodes.
//
// Batch path (see BatchEvaluation.cs): combinators evaluate their first operand straight
// into the destination, the second into pooled scratch, and fold with a vectorized
// elementwise combine; modifiers and transforms rewrite the coordinate spans (or the
// result span) in place. The structure-of-arrays coordinates are forwarded unchanged
// wherever the node does not move points, so the AoS→SoA transpose stays a once-per-batch
// cost at the root.

/// <summary>
/// Corner-hull helpers for the transform nodes: a rigid map's image of a box, and the box's
/// own Lipschitz-bound query region. Kept in one place because <see cref="Sdf.Bounds"/> and
/// <see cref="Sdf.LipschitzBound"/> must agree about which region the child actually sees —
/// a wrapper that maps one and not the other would report a bound for the wrong part of
/// space.
/// </summary>
internal static class TransformHull
{
    /// <summary>The axis-aligned hull of a box's eight corners under a point map. Infinite
    /// boxes stay infinite: an unbounded field has no finite image to take.</summary>
    public static Aabb Map(in Aabb box, Func<Vector3d, Vector3d> map)
    {
        if (!Sdf.IsFinite(box))
            return Sdf.InfiniteBounds;
        var result = Aabb.Empty;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3d(
                (i & 1) == 0 ? box.Min.X : box.Max.X,
                (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                (i & 4) == 0 ? box.Min.Z : box.Max.Z);
            result = result.Union(map(corner));
        }
        return result;
    }
}

internal sealed class UnionSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Min(a.Evaluate(p), b.Evaluate(p));

    public override Aabb Bounds => a.Bounds.Union(b.Bounds);

    /// <summary>A min (or max) picks one operand's value at every point, so its gradient is
    /// one operand's gradient — the larger of the two bounds covers it.</summary>
    public override double LipschitzBound(in Aabb region) =>
        Math.Max(a.LipschitzBound(region), b.LipschitzBound(region));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.Min(distances, other.Span);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        SdfExpression.Min(e.Build(a), e.Build(b));
}

internal sealed class IntersectionSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Max(a.Evaluate(p), b.Evaluate(p));

    public override Aabb Bounds => a.Bounds.Intersection(b.Bounds);

    /// <inheritdoc cref="UnionSdf.LipschitzBound"/>
    public override double LipschitzBound(in Aabb region) =>
        Math.Max(a.LipschitzBound(region), b.LipschitzBound(region));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.Max(distances, other.Span);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        SdfExpression.Max(e.Build(a), e.Build(b));
}

internal sealed class DifferenceSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Max(a.Evaluate(p), -b.Evaluate(p));

    public override Aabb Bounds => a.Bounds;

    /// <summary>Negating an operand leaves its Lipschitz constant alone, so the difference is
    /// the union's rule.</summary>
    public override double LipschitzBound(in Aabb region) =>
        Math.Max(a.LipschitzBound(region), b.LipschitzBound(region));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.MaxNegated(distances, other.Span);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        SdfExpression.Max(e.Build(a), Expression.Negate(e.Build(b)));
}

internal static class BlendMath
{
    /// <summary>Polynomial smooth minimum (Quilez); equals min outside the blend band k.</summary>
    public static double SmoothMin(double a, double b, double k)
    {
        if (k <= 0)
            return Math.Min(a, b);
        double h = Math.Clamp(0.5 + 0.5 * (b - a) / k, 0, 1);
        return b + (a - b) * h - k * h * (1 - h);
    }

    /// <summary>
    /// <see cref="SmoothMin(double, double, double)"/> as an expression, term for term and in
    /// the same association order — including the k &lt;= 0 branch, which is resolved here at
    /// build time because k is a construction constant, not a query value.
    /// </summary>
    public static Expression SmoothMin(SdfExpression e, Expression a, Expression b, double k)
    {
        if (k <= 0)
            return SdfExpression.Min(a, b);
        var h = e.Let(SdfExpression.Clamp(
            Expression.Add(
                SdfExpression.Const(0.5),
                Expression.Divide(
                    Expression.Multiply(SdfExpression.Const(0.5), Expression.Subtract(b, a)),
                    SdfExpression.Const(k))),
            0, 1));
        return Expression.Subtract(
            Expression.Add(b, Expression.Multiply(Expression.Subtract(a, b), h)),
            Expression.Multiply(
                Expression.Multiply(SdfExpression.Const(k), h),
                Expression.Subtract(SdfExpression.Const(1), h)));
    }
}

// Negative-blend policy (binary and n-ary alike): SmoothMin already degrades to the
// exact hard min for k <= 0, so the bounds expansion clamps at 0 — a negative "blend"
// must never shrink conservative bounds. Same degrade-gracefully policy as Sdf.Blend.

internal sealed class SmoothUnionSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => BlendMath.SmoothMin(a.Evaluate(p), b.Evaluate(p), k);

    // The blend bulges outward by at most k/4 in the seam region.
    public override Aabb Bounds => a.Bounds.Union(b.Bounds).Expanded(Math.Max(k, 0));

    /// <summary>
    /// The polynomial smooth minimum's gradient is <c>h·∇a + (1−h)·∇b</c> with h ∈ [0, 1] —
    /// a CONVEX COMBINATION, which falls out of the formula once the derivative of h is
    /// carried through and the two h-terms cancel exactly. So a smooth blend can never be
    /// steeper than its steepest operand, and the union's rule applies verbatim.
    /// </summary>
    public override double LipschitzBound(in Aabb region) =>
        Math.Max(a.LipschitzBound(region), b.LipschitzBound(region));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.SmoothMin(distances, other.Span, k);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        BlendMath.SmoothMin(e, e.Build(a), e.Build(b), k);
}

internal sealed class SmoothIntersectionSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => -BlendMath.SmoothMin(-a.Evaluate(p), -b.Evaluate(p), k);

    public override Aabb Bounds => a.Bounds.Intersection(b.Bounds).Expanded(Math.Max(k, 0));

    /// <inheritdoc cref="SmoothUnionSdf.LipschitzBound"/>
    public override double LipschitzBound(in Aabb region) =>
        Math.Max(a.LipschitzBound(region), b.LipschitzBound(region));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.SmoothMax(distances, other.Span, k);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        Expression.Negate(BlendMath.SmoothMin(
            e, Expression.Negate(e.Build(a)), Expression.Negate(e.Build(b)), k));
}

internal sealed class SmoothDifferenceSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => -BlendMath.SmoothMin(-a.Evaluate(p), b.Evaluate(p), k);

    public override Aabb Bounds => a.Bounds.Expanded(Math.Max(k, 0));

    /// <inheritdoc cref="SmoothUnionSdf.LipschitzBound"/>
    public override double LipschitzBound(in Aabb region) =>
        Math.Max(a.LipschitzBound(region), b.LipschitzBound(region));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.SmoothSubtract(distances, other.Span, k);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        Expression.Negate(BlendMath.SmoothMin(e, Expression.Negate(e.Build(a)), e.Build(b), k));
}

internal sealed class OffsetSdf(Sdf source, double distance) : Sdf
{
    public override double Evaluate(in Vector3d p) => source.Evaluate(p) - distance;

    public override Aabb Bounds => distance > 0 ? source.Bounds.Expanded(distance) : source.Bounds;

    /// <summary>Subtracting a constant does not move the gradient.</summary>
    public override double LipschitzBound(in Aabb region) => source.LipschitzBound(region);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        source.EvaluateBatch(x, y, z, distances);
        SdfBatch.Subtract(distances, distance);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        Expression.Subtract(e.Build(source), SdfExpression.Const(distance));
}

internal sealed class ShellSdf(Sdf source, double thickness) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Abs(source.Evaluate(p)) - thickness / 2;

    public override Aabb Bounds => source.Bounds.Expanded(thickness / 2);

    /// <summary>|·| reflects the value about zero, which leaves |∇| alone.</summary>
    public override double LipschitzBound(in Aabb region) => source.LipschitzBound(region);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        source.EvaluateBatch(x, y, z, distances);
        SdfBatch.AbsSubtract(distances, thickness / 2);
    }

    internal override Expression BuildExpression(SdfExpression e) =>
        Expression.Subtract(
            SdfExpression.Abs(e.Build(source)), SdfExpression.Const(thickness / 2));
}

internal sealed class TranslateSdf(Sdf source, Vector3d translation) : Sdf
{
    public override double Evaluate(in Vector3d p) => source.Evaluate(p - translation);

    public override Aabb Bounds
    {
        get
        {
            var b = source.Bounds;
            return new Aabb(b.Min + translation, b.Max + translation);
        }
    }

    /// <summary>A translation is an isometry, so the bound is the child's — over the region
    /// the child actually sees, which is this one moved back.</summary>
    public override double LipschitzBound(in Aabb region) =>
        source.LipschitzBound(new Aabb(region.Min - translation, region.Max - translation));

    internal override Expression BuildExpression(SdfExpression e) =>
        e.BuildAt(source,
            Expression.Subtract(e.X, SdfExpression.Const(translation.X)),
            Expression.Subtract(e.Y, SdfExpression.Const(translation.Y)),
            Expression.Subtract(e.Z, SdfExpression.Const(translation.Z)));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        using var moved = new BatchScratch3(x.Length);
        SdfBatch.Subtract(x, translation.X, moved.X);
        SdfBatch.Subtract(y, translation.Y, moved.Y);
        SdfBatch.Subtract(z, translation.Z, moved.Z);
        source.EvaluateBatch(moved.X, moved.Y, moved.Z, distances);
    }
}

internal sealed class RotateSdf : Sdf
{
    private readonly Sdf _source;
    private readonly Quaterniond _inverse;
    private readonly Quaterniond _rotation;

    public RotateSdf(Sdf source, in Quaterniond rotation)
    {
        _source = source;
        _rotation = rotation;
        _inverse = rotation.Conjugate;
    }

    public override double Evaluate(in Vector3d p) => _source.Evaluate(_inverse.Rotate(p));

    public override Aabb Bounds
    {
        get
        {
            var b = _source.Bounds;
            if (!IsFinite(b))
                return InfiniteBounds;
            var result = Aabb.Empty;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3d(
                    (i & 1) == 0 ? b.Min.X : b.Max.X,
                    (i & 2) == 0 ? b.Min.Y : b.Max.Y,
                    (i & 4) == 0 ? b.Min.Z : b.Max.Z);
                result = result.Union(_rotation.Rotate(corner));
            }
            return result;
        }
    }

    /// <summary>A rotation is an isometry; the child sees the region rotated back, hulled to
    /// an axis-aligned box (conservative, which is the right direction).</summary>
    public override double LipschitzBound(in Aabb region) =>
        _source.LipschitzBound(TransformHull.Map(region, p => _inverse.Rotate(p)));

    /// <summary>
    /// Mirrors <c>Quaterniond.Rotate</c> term for term — t = 2·(u × v), result = v + w·t +
    /// u × t — exactly as the SIMD kernel does, and for the same reason: re-deriving the
    /// rotation as a matrix would round differently.
    /// </summary>
    internal override Expression BuildExpression(SdfExpression e)
    {
        double ux = _inverse.X, uy = _inverse.Y, uz = _inverse.Z, w = _inverse.W;
        var vx = e.X;
        var vy = e.Y;
        var vz = e.Z;
        var tx = e.Let(Doubled(Cross(uy, vz, uz, vy)));
        var ty = e.Let(Doubled(Cross(uz, vx, ux, vz)));
        var tz = e.Let(Doubled(Cross(ux, vy, uy, vx)));
        return e.BuildAt(_source,
            Expression.Add(Expression.Add(vx, Scale(tx, w)), Cross(uy, tz, uz, ty)),
            Expression.Add(Expression.Add(vy, Scale(ty, w)), Cross(uz, tx, ux, tz)),
            Expression.Add(Expression.Add(vz, Scale(tz, w)), Cross(ux, ty, uy, tx)));

        // One component of u × v: a*p − b*q. Doubled only for t, exactly as the scalar path
        // doubles u × v and leaves u × t alone.
        static Expression Cross(double a, Expression p, double b, Expression q) =>
            Expression.Subtract(
                Expression.Multiply(SdfExpression.Const(a), p),
                Expression.Multiply(SdfExpression.Const(b), q));

        static Expression Doubled(Expression v) => Expression.Multiply(SdfExpression.Const(2.0), v);

        static Expression Scale(Expression v, double s) =>
            Expression.Multiply(SdfExpression.Const(s), v);
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        using var rotated = new BatchScratch3(x.Length);
        SdfBatch.Rotate(_inverse, x, y, z, rotated.X, rotated.Y, rotated.Z);
        _source.EvaluateBatch(rotated.X, rotated.Y, rotated.Z, distances);
    }
}

/// <summary>Reflection across a plane: evaluates the source at the mirrored query
/// point. Reflection is an isometry, so distances stay exact.</summary>
internal sealed class MirrorSdf(Sdf source, Vector3d point, Vector3d unitNormal) : Sdf
{
    public override double Evaluate(in Vector3d p) => source.Evaluate(Reflect(p));

    private Vector3d Reflect(in Vector3d p) => p - unitNormal * (2 * unitNormal.Dot(p - point));

    public override Aabb Bounds
    {
        get
        {
            var b = source.Bounds;
            if (!IsFinite(b))
                return InfiniteBounds;
            var result = Aabb.Empty;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3d(
                    (i & 1) == 0 ? b.Min.X : b.Max.X,
                    (i & 2) == 0 ? b.Min.Y : b.Max.Y,
                    (i & 4) == 0 ? b.Min.Z : b.Max.Z);
                result = result.Union(Reflect(corner));
            }
            return result;
        }
    }

    /// <summary>A reflection is an isometry and its own inverse.</summary>
    public override double LipschitzBound(in Aabb region) =>
        source.LipschitzBound(TransformHull.Map(region, p => Reflect(p)));

    internal override Expression BuildExpression(SdfExpression e)
    {
        // p − n·(2·(n · (p − point))), term for term with Reflect.
        var dot = SdfExpression.Add(
            Expression.Multiply(
                SdfExpression.Const(unitNormal.X),
                Expression.Subtract(e.X, SdfExpression.Const(point.X))),
            Expression.Multiply(
                SdfExpression.Const(unitNormal.Y),
                Expression.Subtract(e.Y, SdfExpression.Const(point.Y))),
            Expression.Multiply(
                SdfExpression.Const(unitNormal.Z),
                Expression.Subtract(e.Z, SdfExpression.Const(point.Z))));
        var scale = e.Let(Expression.Multiply(SdfExpression.Const(2), dot));
        return e.BuildAt(source,
            Expression.Subtract(e.X, Expression.Multiply(SdfExpression.Const(unitNormal.X), scale)),
            Expression.Subtract(e.Y, Expression.Multiply(SdfExpression.Const(unitNormal.Y), scale)),
            Expression.Subtract(e.Z, Expression.Multiply(SdfExpression.Const(unitNormal.Z), scale)));
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        using var reflected = new BatchScratch3(x.Length);
        SdfBatch.Reflect(point, unitNormal, x, y, z, reflected.X, reflected.Y, reflected.Z);
        source.EvaluateBatch(reflected.X, reflected.Y, reflected.Z, distances);
    }
}

internal sealed class ScaleSdf : Sdf
{
    private readonly Sdf _source;
    private readonly double _factor;

    public ScaleSdf(Sdf source, double factor)
    {
        if (factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "Scale factor must be positive.");
        _source = source;
        _factor = factor;
    }

    public override double Evaluate(in Vector3d p) => _source.Evaluate(p / _factor) * _factor;

    public override Aabb Bounds
    {
        get
        {
            var b = _source.Bounds;
            return new Aabb(b.Min * _factor, b.Max * _factor);
        }
    }

    /// <summary>A uniform scale divides the point and multiplies the value by the same
    /// factor, so the two cancel and the gradient is exactly the child's.</summary>
    public override double LipschitzBound(in Aabb region) =>
        _source.LipschitzBound(new Aabb(region.Min / _factor, region.Max / _factor));

    internal override Expression BuildExpression(SdfExpression e)
    {
        var f = SdfExpression.Const(_factor);
        var inner = e.BuildAt(_source,
            Expression.Divide(e.X, f), Expression.Divide(e.Y, f), Expression.Divide(e.Z, f));
        return Expression.Multiply(inner, f);
    }

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        using var scaled = new BatchScratch3(x.Length);
        // Divide (not multiply by a reciprocal) to match the scalar path exactly.
        SdfBatch.Divide(x, _factor, scaled.X);
        SdfBatch.Divide(y, _factor, scaled.Y);
        SdfBatch.Divide(z, _factor, scaled.Z);
        _source.EvaluateBatch(scaled.X, scaled.Y, scaled.Z, distances);
        SdfBatch.Multiply(distances, _factor);
    }
}
