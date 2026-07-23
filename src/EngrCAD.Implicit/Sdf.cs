using EngrCAD.Core;

namespace EngrCAD.Implicit;

/// <summary>
/// A signed distance field: negative inside, zero on the surface, positive outside.
/// Models compose as an AST of primitives and operators; every node reports conservative
/// <see cref="Bounds"/> (infinite for unbounded fields like half-spaces and lattices).
/// Set operators are overloaded for fluent composition: <c>a | b</c> union,
/// <c>a &amp; b</c> intersection, <c>a - b</c> difference.
/// Distances from smooth/blend operators are lower-bound approximations — correct sign
/// everywhere, exact magnitude only away from blend regions.
/// </summary>
public abstract class Sdf
{
    public abstract double Evaluate(in Vector3d point);

    /// <summary>Conservative bounds of the solid (the d &lt; 0 region).</summary>
    public abstract Aabb Bounds { get; }

    /// <summary>Batch evaluation; the default loops, subclasses may vectorize.</summary>
    public virtual void Evaluate(ReadOnlySpan<Vector3d> points, Span<double> distances)
    {
        if (distances.Length < points.Length)
            throw new ArgumentException("Distance span is shorter than the point span.");
        for (int i = 0; i < points.Length; i++)
            distances[i] = Evaluate(points[i]);
    }

    /// <summary>Outward surface normal by central differences.</summary>
    public Vector3d Normal(in Vector3d point, double epsilon = 1e-6)
    {
        var gradient = new Vector3d(
            Evaluate(point + (epsilon, 0, 0)) - Evaluate(point - (epsilon, 0, 0)),
            Evaluate(point + (0, epsilon, 0)) - Evaluate(point - (0, epsilon, 0)),
            Evaluate(point + (0, 0, epsilon)) - Evaluate(point - (0, 0, epsilon)));
        return gradient.TryNormalize(Tolerance.Default, out var n) ? n : Vector3d.UnitZ;
    }

    internal static readonly Aabb InfiniteBounds = new(
        (double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity),
        (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity));

    public static bool IsFinite(in Aabb bounds) =>
        double.IsFinite(bounds.Min.X) && double.IsFinite(bounds.Min.Y) && double.IsFinite(bounds.Min.Z) &&
        double.IsFinite(bounds.Max.X) && double.IsFinite(bounds.Max.Y) && double.IsFinite(bounds.Max.Z);

    // ---- primitive factories ----

    public static Sdf Sphere(double radius) => new SphereSdf(radius);

    /// <summary>Box centered at the origin with the given full side lengths.</summary>
    public static Sdf Box(double sizeX, double sizeY, double sizeZ) =>
        new BoxSdf(new Vector3d(sizeX / 2, sizeY / 2, sizeZ / 2));

    public static Sdf Box(in Vector3d halfExtents) => new BoxSdf(halfExtents);

    /// <summary>Capped cylinder along Z, centered at the origin.</summary>
    public static Sdf Cylinder(double radius, double height) => new CylinderSdf(radius, height / 2);

    /// <summary>Torus about the Z axis: ring of radius <paramref name="majorRadius"/> in the XY plane.</summary>
    public static Sdf Torus(double majorRadius, double minorRadius) => new TorusSdf(majorRadius, minorRadius);

    public static Sdf Capsule(in Vector3d a, in Vector3d b, double radius) => new CapsuleSdf(a, b, radius);

    /// <summary>Half-space: solid where dot(normal, p) ≤ offset. Unbounded.</summary>
    public static Sdf HalfSpace(in Vector3d normal, double offset) =>
        new HalfSpaceSdf(normal.Normalized(), offset);

    /// <summary>
    /// Gyroid lattice sheet (triply periodic minimal surface) with the given cell size and
    /// sheet thickness. Approximate distance, unbounded — intersect with a finite solid.
    /// </summary>
    public static Sdf Gyroid(double cellSize, double thickness) => new GyroidSdf(cellSize, thickness);

    // ---- combinators ----

    public Sdf Union(Sdf other) => new UnionSdf(this, other);
    public Sdf Intersect(Sdf other) => new IntersectionSdf(this, other);
    public Sdf Subtract(Sdf other) => new DifferenceSdf(this, other);

    /// <summary>Union with a fillet-like blend of radius ~<paramref name="blend"/>.</summary>
    public Sdf SmoothUnion(Sdf other, double blend) => new SmoothUnionSdf(this, other, blend);

    public Sdf SmoothIntersect(Sdf other, double blend) => new SmoothIntersectionSdf(this, other, blend);
    public Sdf SmoothSubtract(Sdf other, double blend) => new SmoothDifferenceSdf(this, other, blend);

    /// <summary>Positive distance grows (and rounds) the solid; negative shrinks it.</summary>
    public Sdf Offset(double distance) => new OffsetSdf(this, distance);

    /// <summary>Hollow skin of the surface with the given total wall thickness.</summary>
    public Sdf Shell(double thickness) => new ShellSdf(this, thickness);

    public Sdf Translate(in Vector3d translation) => new TranslateSdf(this, translation);
    public Sdf Rotate(in Quaterniond rotation) => new RotateSdf(this, rotation);

    /// <summary>Uniform scale about the origin (distances stay exact).</summary>
    public Sdf Scale(double factor) => new ScaleSdf(this, factor);

    public static Sdf operator |(Sdf a, Sdf b) => a.Union(b);
    public static Sdf operator &(Sdf a, Sdf b) => a.Intersect(b);
    public static Sdf operator -(Sdf a, Sdf b) => a.Subtract(b);

    // ---- n-ary combinators ----

    /// <summary>
    /// Exact union of any number of solids as a single flat AST node (min over children,
    /// each evaluated once per query) — use instead of deep chains of binary unions.
    /// A single operand is returned unchanged.
    /// </summary>
    public static Sdf Union(IReadOnlyList<Sdf> children)
    {
        var copy = NaryChildren.Copy(children);
        return copy.Length == 1 ? copy[0] : new NaryUnionSdf(copy);
    }

    /// <inheritdoc cref="Union(IReadOnlyList{Sdf})"/>
    public static Sdf Union(params Sdf[] children) => Union((IReadOnlyList<Sdf>)children);

    /// <summary>
    /// Exact intersection of any number of solids as a single flat AST node (max over
    /// children, each evaluated once per query). A single operand is returned unchanged.
    /// </summary>
    public static Sdf Intersection(IReadOnlyList<Sdf> children)
    {
        var copy = NaryChildren.Copy(children);
        return copy.Length == 1 ? copy[0] : new NaryIntersectionSdf(copy);
    }

    /// <inheritdoc cref="Intersection(IReadOnlyList{Sdf})"/>
    public static Sdf Intersection(params Sdf[] children) => Intersection((IReadOnlyList<Sdf>)children);

    /// <summary>
    /// N-ary smooth union with fillet-like blend radius ~<paramref name="blend"/>: the
    /// polynomial smooth minimum folded over the children (each evaluated once per
    /// query). Coincides exactly with chained binary <see cref="SmoothUnion(Sdf, double)"/>;
    /// see <see cref="NarySmoothUnionSdf"/> for the formulation rationale and the
    /// lower-bound distance caveat near blend regions.
    /// </summary>
    public static Sdf SmoothUnion(IReadOnlyList<Sdf> children, double blend)
    {
        var copy = NaryChildren.Copy(children);
        return copy.Length == 1 ? copy[0] : new NarySmoothUnionSdf(copy, blend);
    }

    /// <summary>
    /// Union of <paramref name="a"/> and <paramref name="b"/> with a fillet-style blend:
    /// material is added where both surfaces lie within <paramref name="blendDistance"/>,
    /// weighted by the falloff <paramref name="kernel"/> (see <see cref="Falloff"/>).
    /// Converges to the plain union as <paramref name="blendDistance"/> → 0 (and is the
    /// plain union for <paramref name="blendDistance"/> ≤ 0). Correct sign everywhere;
    /// distance magnitude is a lower bound near the seam.
    /// </summary>
    public static Sdf Blend(Sdf a, Sdf b, double blendDistance, Falloff kernel = Falloff.Wyvill)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return blendDistance <= 0 ? a.Union(b) : new FalloffBlendSdf(a, b, blendDistance, kernel);
    }
}
