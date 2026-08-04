using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.InteropServices;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// N-ary set operations and falloff-kernel blends over SDF nodes.
// geometry3Sharp counterparts: ImplicitNaryUnion3d / ImplicitNaryIntersection3d
// (flat min/max loops), ImplicitBlend3d + FalloffFunctions (bounded blend bumps).

/// <summary>
/// Falloff kernels for <see cref="Sdf.Blend"/>. A kernel K maps the normalized distance
/// t = |d| / blendDistance to a weight: K(0) = 1 on a surface, decaying toward 0 at the
/// edge of the blend band (t = 1).
/// </summary>
public enum Falloff
{
    /// <summary>
    /// Wyvill polynomial (1 - t^2)^3 - compact support: exactly zero for t >= 1, so the
    /// blend reduces <em>exactly</em> to the plain union wherever either operand is at
    /// least the blend distance from its surface. Smooth (C2) across both ends.
    /// </summary>
    Wyvill,

    /// <summary>
    /// Blinn-style Gaussian exp(-4 t^2) - infinitely smooth, but with an infinite tail:
    /// about 1.8% of the bump survives at the band edge, so the result converges to
    /// (never exactly equals) the plain union away from the seam.
    /// </summary>
    Exponential,
}

internal static class FalloffKernels
{
    /// <summary>Evaluates kernel K at normalized distance t >= 0.</summary>
    public static double Evaluate(Falloff kernel, double t)
    {
        switch (kernel)
        {
            case Falloff.Wyvill:
                if (t >= 1)
                    return 0;
                double s = 1 - t * t;
                return s * s * s;
            case Falloff.Exponential:
                return Math.Exp(-4 * t * t);
            default:
                throw new ArgumentOutOfRangeException(nameof(kernel));
        }
    }
}

internal static class NaryChildren
{
    /// <summary>Validates and defensively copies an operand list (AST nodes are immutable).</summary>
    public static Sdf[] Copy(IReadOnlyList<Sdf> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count == 0)
            throw new ArgumentException("At least one operand is required.", nameof(children));
        var copy = new Sdf[children.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = children[i] ?? throw new ArgumentException(
                "Operands must not be null.", nameof(children));
        return copy;
    }

    /// <summary>The n-ary form of the min/max rule: a fold that picks one operand's value at
    /// each point is no steeper than its steepest operand.</summary>
    public static double MaxBound(Sdf[] children, in Aabb region)
    {
        double bound = children[0].LipschitzBound(region);
        for (int i = 1; i < children.Length; i++)
            bound = Math.Max(bound, children[i].LipschitzBound(region));
        return bound;
    }
}

/// <summary>
/// Exact N-ary union: min over all children in one flat loop (each child evaluated once
/// per query). Identical field to a chain of binary unions, without the tree depth.
/// </summary>
internal sealed class NaryUnionSdf(Sdf[] children) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double f = children[0].Evaluate(p);
        for (int i = 1; i < children.Length; i++)
            f = Math.Min(f, children[i].Evaluate(p));
        return f;
    }

    public override Aabb Bounds
    {
        get
        {
            var b = children[0].Bounds;
            for (int i = 1; i < children.Length; i++)
                b = b.Union(children[i].Bounds);
            return b;
        }
    }

    /// <inheritdoc cref="UnionSdf.LipschitzBound"/>
    public override double LipschitzBound(in Aabb region) => NaryChildren.MaxBound(children, region);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        children[0].EvaluateBatch(x, y, z, distances);
        if (children.Length == 1)
            return;
        using var other = new BatchScratch(distances.Length);
        for (int i = 1; i < children.Length; i++)
        {
            children[i].EvaluateBatch(x, y, z, other.Span);
            SdfBatch.Min(distances, other.Span);
        }
    }

    internal override Expression BuildExpression(SdfExpression e)
    {
        var body = e.Build(children[0]);
        for (int i = 1; i < children.Length; i++)
            body = SdfExpression.Min(body, e.Build(children[i]));
        return body;
    }
}

/// <summary>
/// Exact N-ary intersection: max over all children in one flat loop (each child evaluated
/// once per query). Identical field to a chain of binary intersections.
/// </summary>
internal sealed class NaryIntersectionSdf(Sdf[] children) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double f = children[0].Evaluate(p);
        for (int i = 1; i < children.Length; i++)
            f = Math.Max(f, children[i].Evaluate(p));
        return f;
    }

    public override Aabb Bounds
    {
        get
        {
            var b = children[0].Bounds;
            for (int i = 1; i < children.Length; i++)
                b = b.Intersection(children[i].Bounds);
            return b;
        }
    }

    /// <inheritdoc cref="UnionSdf.LipschitzBound"/>
    public override double LipschitzBound(in Aabb region) => NaryChildren.MaxBound(children, region);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        children[0].EvaluateBatch(x, y, z, distances);
        if (children.Length == 1)
            return;
        using var other = new BatchScratch(distances.Length);
        for (int i = 1; i < children.Length; i++)
        {
            children[i].EvaluateBatch(x, y, z, other.Span);
            SdfBatch.Max(distances, other.Span);
        }
    }

    internal override Expression BuildExpression(SdfExpression e)
    {
        var body = e.Build(children[0]);
        for (int i = 1; i < children.Length; i++)
            body = SdfExpression.Max(body, e.Build(children[i]));
        return body;
    }
}

/// <summary>
/// N-ary smooth union: the polynomial smooth minimum folded pairwise over the children in
/// order (each child evaluated once per query).
/// <para>
/// Formulation choice: the iterative pairwise polynomial (Quilez) rather than a
/// log-sum-exp generalization, because it (a) coincides bit-for-bit with the binary
/// <see cref="Sdf.SmoothUnion(Sdf, double)"/> for two children, (b) reduces
/// <em>exactly</em> to the hard min once running values differ by more than the blend
/// radius (log-sum-exp only converges, and needs overflow guards for |d| much larger
/// than k), and (c) stays transcendental-free and branch-light for future SIMD batching.
/// The cost is mild order dependence confined to the blend band.
/// </para>
/// <para>
/// Distance fidelity (same contract as the binary smooth operators): correct sign
/// everywhere relative to the blended solid, which contains the exact union and
/// coincides with it away from blend regions; magnitude is a lower bound near blends.
/// Each fold can deepen the field by at most k/4, so the cumulative dip is bounded by
/// (n - 1) * k/4 and <see cref="Bounds"/> expands by max(k, (n - 1) * k/4). For k &lt;= 0
/// the fold degrades to the exact hard min and the expansion clamps at 0 (a negative
/// blend never shrinks conservative bounds — same policy as the binary operators).
/// </para>
/// </summary>
internal sealed class NarySmoothUnionSdf(Sdf[] children, double k) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double f = children[0].Evaluate(p);
        for (int i = 1; i < children.Length; i++)
            f = BlendMath.SmoothMin(f, children[i].Evaluate(p), k);
        return f;
    }

    public override Aabb Bounds
    {
        get
        {
            var b = children[0].Bounds;
            for (int i = 1; i < children.Length; i++)
                b = b.Union(children[i].Bounds);
            return b.Expanded(Math.Max(k, 0) * Math.Max(1, 0.25 * (children.Length - 1)));
        }
    }

    /// <inheritdoc cref="SmoothUnionSdf.LipschitzBound"/>
    public override double LipschitzBound(in Aabb region) => NaryChildren.MaxBound(children, region);

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        children[0].EvaluateBatch(x, y, z, distances);
        if (children.Length == 1)
            return;
        using var other = new BatchScratch(distances.Length);
        for (int i = 1; i < children.Length; i++)
        {
            children[i].EvaluateBatch(x, y, z, other.Span);
            SdfBatch.SmoothMin(distances, other.Span, k);
        }
    }

    internal override Expression BuildExpression(SdfExpression e)
    {
        var body = e.Build(children[0]);
        for (int i = 1; i < children.Length; i++)
        {
            // The fold's running value is used twice inside SmoothMin, so bind it.
            body = BlendMath.SmoothMin(e, e.Let(body), e.Build(children[i]), k);
        }
        return body;
    }
}

/// <summary>
/// Union of two solids with a fillet-style blend bump driven by a falloff kernel
/// (geometry3Sharp's ImplicitBlend3d in spirit, formulated on distances):
/// d = min(fA, fB) - blendDistance * K(|fA|/blendDistance) * K(|fB|/blendDistance).
/// Material is added only where <em>both</em> surfaces are within the blend distance -
/// i.e. around the seam where the surfaces meet - with the bump magnitude bounded by
/// blendDistance (so the result converges to the plain union as blendDistance goes to 0).
/// <para>
/// Distance fidelity: the field is everywhere at most the plain union's (correct sign -
/// the blended solid contains the exact union), and with the compact-support
/// <see cref="Falloff.Wyvill"/> kernel it equals the plain union exactly wherever either
/// operand is at least blendDistance from its surface. Magnitude is only a bound near
/// the seam, as with the smooth operators.
/// </para>
/// </summary>
internal sealed class FalloffBlendSdf(Sdf a, Sdf b, double blendDistance, Falloff kernel) : Sdf
{
    public override double Evaluate(in Vector3d p)
    {
        double fa = a.Evaluate(p);
        double fb = b.Evaluate(p);
        double bump = blendDistance
            * FalloffKernels.Evaluate(kernel, Math.Abs(fa) / blendDistance)
            * FalloffKernels.Evaluate(kernel, Math.Abs(fb) / blendDistance);
        return Math.Min(fa, fb) - bump;
    }

    // The bump is at most blendDistance anywhere.
    public override Aabb Bounds => a.Bounds.Union(b.Bounds).Expanded(blendDistance);

    /// <summary>
    /// Propagates the OPERANDS' bounds, which is what a domain operator underneath this one
    /// needs. It deliberately does not add the falloff bump's own steepness: that can reach
    /// about 4.4× an operand's where both kernels sit near their steepest, but only where
    /// BOTH surfaces are within the blend distance — a region in which |d| is itself under
    /// twice that distance, so a cull test never clears a block there on the strength of a
    /// large |d|. This is the pre-existing "approximately Lipschitz" case
    /// <c>SurfaceCull.SafetyCells</c> was written to cushion, carried over unchanged rather
    /// than re-decided here.
    /// </summary>
    public override double LipschitzBound(in Aabb region) =>
        Math.Max(a.LipschitzBound(region), b.LipschitzBound(region));

    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        a.EvaluateBatch(x, y, z, distances);
        using var other = new BatchScratch(distances.Length);
        b.EvaluateBatch(x, y, z, other.Span);
        Combine(distances, other.Span);
    }

    /// <summary>
    /// Folds the two operand fields in place. Only the polynomial <see cref="Falloff.Wyvill"/>
    /// kernel vectorizes; <see cref="Falloff.Exponential"/> needs <see cref="Math.Exp"/>,
    /// which has no bit-identical vector counterpart, so it takes the scalar loop (both
    /// operands were still evaluated in batch, which is where the time goes).
    /// </summary>
    private void Combine(Span<double> fa, ReadOnlySpan<double> fb)
    {
        int n = fa.Length;
        int i = 0;
        if (kernel == Falloff.Wyvill && SdfBatch.Accelerated)
        {
            int w = Vector<double>.Count;
            var vd = new Vector<double>(blendDistance);
            ref double ar = ref MemoryMarshal.GetReference(fa);
            ref double br = ref MemoryMarshal.GetReference(fb);
            for (; i <= n - w; i += w)
            {
                var va = Vector.LoadUnsafe(ref ar, (nuint)i);
                var vb = Vector.LoadUnsafe(ref br, (nuint)i);
                var bump = vd * Wyvill(Vector.Abs(va) / vd) * Wyvill(Vector.Abs(vb) / vd);
                (Vector.Min(va, vb) - bump).StoreUnsafe(ref ar, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            double bump = blendDistance
                * FalloffKernels.Evaluate(kernel, Math.Abs(fa[i]) / blendDistance)
                * FalloffKernels.Evaluate(kernel, Math.Abs(fb[i]) / blendDistance);
            fa[i] = Math.Min(fa[i], fb[i]) - bump;
        }
    }

    internal override Expression BuildExpression(SdfExpression e)
    {
        var fa = e.Let(e.Build(a));
        var fb = e.Let(e.Build(b));
        var d = SdfExpression.Const(blendDistance);
        var bump = Expression.Multiply(
            Expression.Multiply(d, Kernel(e, Expression.Divide(SdfExpression.Abs(fa), d))),
            Kernel(e, Expression.Divide(SdfExpression.Abs(fb), d)));
        return Expression.Subtract(SdfExpression.Min(fa, fb), bump);
    }

    /// <summary>The falloff, term for term with <see cref="FalloffKernels.Evaluate"/>. Both
    /// branches compile: an expression tree calls <see cref="Math.Exp"/> itself, so the
    /// exponential kernel is exact here even though it is deliberately not vectorized.</summary>
    private Expression Kernel(SdfExpression e, Expression t)
    {
        if (kernel == Falloff.Exponential)
        {
            var tt = e.Let(t);
            return SdfExpression.Exp(Expression.Multiply(
                Expression.Multiply(SdfExpression.Const(-4), tt), tt));
        }
        var bound = e.Let(t);
        var s = e.Let(Expression.Subtract(
            SdfExpression.Const(1), Expression.Multiply(bound, bound)));
        return Expression.Condition(
            Expression.GreaterThanOrEqual(bound, SdfExpression.Const(1)),
            SdfExpression.Const(0),
            Expression.Multiply(Expression.Multiply(s, s), s));
    }

    /// <summary>Lane-wise (1 − t²)³ with compact support — mirrors
    /// <see cref="FalloffKernels.Evaluate"/>'s Wyvill branch term for term.</summary>
    private static Vector<double> Wyvill(Vector<double> t)
    {
        var one = Vector<double>.One;
        var s = one - t * t;
        return Vector.ConditionalSelect(Vector.GreaterThanOrEqual(t, one), Vector<double>.Zero, s * s * s);
    }
}
