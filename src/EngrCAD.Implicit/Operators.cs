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

internal sealed class UnionSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Min(a.Evaluate(p), b.Evaluate(p));

    public override Aabb Bounds => a.Bounds.Union(b.Bounds);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.Min(distances, other.Span);
    }
}

internal sealed class IntersectionSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Max(a.Evaluate(p), b.Evaluate(p));

    public override Aabb Bounds => a.Bounds.Intersection(b.Bounds);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.Max(distances, other.Span);
    }
}

internal sealed class DifferenceSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Max(a.Evaluate(p), -b.Evaluate(p));

    public override Aabb Bounds => a.Bounds;

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.MaxNegated(distances, other.Span);
    }
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
}

// Negative-blend policy (binary and n-ary alike): SmoothMin already degrades to the
// exact hard min for k <= 0, so the bounds expansion clamps at 0 — a negative "blend"
// must never shrink conservative bounds. Same degrade-gracefully policy as Sdf.Blend.

internal sealed class SmoothUnionSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => BlendMath.SmoothMin(a.Evaluate(p), b.Evaluate(p), k);

    // The blend bulges outward by at most k/4 in the seam region.
    public override Aabb Bounds => a.Bounds.Union(b.Bounds).Expanded(Math.Max(k, 0));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.SmoothMin(distances, other.Span, k);
    }
}

internal sealed class SmoothIntersectionSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => -BlendMath.SmoothMin(-a.Evaluate(p), -b.Evaluate(p), k);

    public override Aabb Bounds => a.Bounds.Intersection(b.Bounds).Expanded(Math.Max(k, 0));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.SmoothMax(distances, other.Span, k);
    }
}

internal sealed class SmoothDifferenceSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => -BlendMath.SmoothMin(-a.Evaluate(p), b.Evaluate(p), k);

    public override Aabb Bounds => a.Bounds.Expanded(Math.Max(k, 0));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        SdfBatch.SmoothSubtract(distances, other.Span, k);
    }
}

internal sealed class OffsetSdf(Sdf source, double distance) : Sdf
{
    public override double Evaluate(in Vector3d p) => source.Evaluate(p) - distance;

    public override Aabb Bounds => distance > 0 ? source.Bounds.Expanded(distance) : source.Bounds;

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        source.EvaluateBatch(x, y, z, distances);
        SdfBatch.Subtract(distances, distance);
    }
}

internal sealed class ShellSdf(Sdf source, double thickness) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Abs(source.Evaluate(p)) - thickness / 2;

    public override Aabb Bounds => source.Bounds.Expanded(thickness / 2);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        source.EvaluateBatch(x, y, z, distances);
        SdfBatch.AbsSubtract(distances, thickness / 2);
    }
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
