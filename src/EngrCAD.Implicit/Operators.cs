using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Set operations, blends, modifiers, and rigid/uniform transforms over SDF nodes.

internal sealed class UnionSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Min(a.Evaluate(p), b.Evaluate(p));

    public override Aabb Bounds => a.Bounds.Union(b.Bounds);
}

internal sealed class IntersectionSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Max(a.Evaluate(p), b.Evaluate(p));

    public override Aabb Bounds => a.Bounds.Intersection(b.Bounds);
}

internal sealed class DifferenceSdf(Sdf a, Sdf b) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Max(a.Evaluate(p), -b.Evaluate(p));

    public override Aabb Bounds => a.Bounds;
}

internal static class Blend
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

internal sealed class SmoothUnionSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => Blend.SmoothMin(a.Evaluate(p), b.Evaluate(p), k);

    // The blend bulges outward by at most k/4 in the seam region.
    public override Aabb Bounds => a.Bounds.Union(b.Bounds).Expanded(k);
}

internal sealed class SmoothIntersectionSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => -Blend.SmoothMin(-a.Evaluate(p), -b.Evaluate(p), k);

    public override Aabb Bounds => a.Bounds.Intersection(b.Bounds).Expanded(k);
}

internal sealed class SmoothDifferenceSdf(Sdf a, Sdf b, double k) : Sdf
{
    public override double Evaluate(in Vector3d p) => -Blend.SmoothMin(-a.Evaluate(p), b.Evaluate(p), k);

    public override Aabb Bounds => a.Bounds.Expanded(k);
}

internal sealed class OffsetSdf(Sdf source, double distance) : Sdf
{
    public override double Evaluate(in Vector3d p) => source.Evaluate(p) - distance;

    public override Aabb Bounds => distance > 0 ? source.Bounds.Expanded(distance) : source.Bounds;
}

internal sealed class ShellSdf(Sdf source, double thickness) : Sdf
{
    public override double Evaluate(in Vector3d p) => Math.Abs(source.Evaluate(p)) - thickness / 2;

    public override Aabb Bounds => source.Bounds.Expanded(thickness / 2);
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
}
