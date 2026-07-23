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
/// (n - 1) * k/4 and <see cref="Bounds"/> expands by max(k, (n - 1) * k/4).
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
            return b.Expanded(Math.Max(k, 0.25 * (children.Length - 1) * k));
        }
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
}
