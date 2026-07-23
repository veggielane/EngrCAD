using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>The three representations a <see cref="Shape"/> can be lowered to.</summary>
public enum TargetRep
{
    Brep,
    Implicit,
    Mesh,
}

/// <summary>Discretization quality used when a lowering tessellates or polygonizes.</summary>
public sealed class MeshQuality
{
    public int SegmentsPerCircle { get; set; } = 32;
    public int CurveSamples { get; set; } = 24;
    public int SdfResolution { get; set; } = 64;

    internal static readonly MeshQuality Default = new();
}

/// <summary>
/// A representation-agnostic model: an immutable operation graph built with one
/// vocabulary (primitives, booleans, extrude/revolve/sweep, blends, transforms) and
/// lowered at the end to whichever engine the design needs — <see cref="ToBrep"/>,
/// <see cref="ToImplicit"/>, or <see cref="ToMesh"/>. Each lowering uses the target
/// engine's native operations where it can and bridges through another representation
/// where it can't; <see cref="Explain"/> reports the per-node plan, and conversions
/// with no bridge at all throw <see cref="ShapeConversionException"/>.
/// </summary>
public abstract class Shape
{
    private protected Shape() { }

    // ---- Primitives (centered at the origin, axes along +Z) ----

    /// <summary>Axis-aligned box of the given size, centered at the origin.</summary>
    public static Shape Box(double sizeX, double sizeY, double sizeZ) =>
        new BoxShape(new Aabb(
            (-sizeX / 2, -sizeY / 2, -sizeZ / 2),
            (sizeX / 2, sizeY / 2, sizeZ / 2)));

    public static Shape Box(in Aabb bounds) => new BoxShape(bounds);

    public static Shape Sphere(double radius) => new SphereShape(radius);

    /// <summary>Cylinder along +Z, centered at the origin.</summary>
    public static Shape Cylinder(double radius, double height) => new CylinderShape(radius, height);

    /// <summary>Torus about +Z, centered at the origin.</summary>
    public static Shape Torus(double majorRadius, double minorRadius) => new TorusShape(majorRadius, minorRadius);

    // ---- Modeling operations ----

    public static Shape Extrude(Profile profile, in Vector3d direction, IReadOnlyList<Profile>? holes = null) =>
        new ExtrudeShape(profile, direction, holes);

    public static Shape Revolve(
        Profile profile, in Vector3d axisOrigin, in Vector3d axisDirection,
        double angle = 2 * Math.PI, IReadOnlyList<Profile>? holes = null) =>
        new RevolveShape(profile, axisOrigin, axisDirection, angle, holes);

    public static Shape Sweep(Profile profile, Curve3d path, IReadOnlyList<Profile>? holes = null) =>
        new SweepShape(profile, path, holes);

    // ---- Sketch-based modeling operations (implicit lowerings become exact) ----

    /// <summary>Extrudes a sketch along its plane normal (default plane: world XY).</summary>
    public static Shape Extrude(Sketch sketch, double height, SketchPlane? plane = null)
    {
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        return new ExtrudeShape(sketch, plane ?? SketchPlane.XY, height);
    }

    /// <summary>
    /// Revolves a sketch about its plane's y axis (the sketch's x = 0 line); sketch x
    /// is the radial direction and must be ≥ 0. The default plane (XZ) puts the axis on
    /// world Z. Sketches may touch the axis on full turns in every representation:
    /// on-axis stretches revolve to nothing and become poles (partial revolves still
    /// need axis clearance).
    /// </summary>
    public static Shape Revolve(Sketch sketch, double angle = 2 * Math.PI, SketchPlane? plane = null)
    {
        double minX = sketch.Bounds.Min.X;
        if (minX < -1e-9)
            throw new ArgumentException("A revolved sketch must lie in x ≥ 0 (x is the radial direction).", nameof(sketch));
        bool fullTurn = Math.Abs(angle - 2 * Math.PI) < 1e-9;
        if (!fullTurn && minX < 1e-9)
            throw new NotSupportedException(
                "A partial revolve of an axis-touching sketch is not representable yet; keep the sketch off the axis or revolve a full turn.");
        return new RevolveShape(sketch, plane ?? SketchPlane.XZ, angle);
    }

    /// <summary>Sweeps a sketch (placed on <paramref name="plane"/>, default world XY,
    /// which must sit at the path start, perpendicular to its tangent) along a path.</summary>
    public static Shape Sweep(Sketch sketch, Curve3d path, SketchPlane? plane = null) =>
        new SweepShape(sketch, plane ?? SketchPlane.XY, path);

    // ---- Escape hatches: wrap existing engine geometry as leaves ----

    public static Shape From(BrepSolid solid) => new SourceShape(solid);
    public static Shape From(HalfEdgeMesh mesh) => new SourceShape(mesh);
    public static Shape From(Sdf sdf) => new SourceShape(sdf);

    // ---- Booleans ----

    public Shape Union(Shape other) => new BooleanShape(BooleanOp.Union, this, other);
    public Shape Intersect(Shape other) => new BooleanShape(BooleanOp.Intersection, this, other);
    public Shape Subtract(Shape other) => new BooleanShape(BooleanOp.Difference, this, other);

    public static Shape operator |(Shape a, Shape b) => a.Union(b);
    public static Shape operator &(Shape a, Shape b) => a.Intersect(b);
    public static Shape operator -(Shape a, Shape b) => a.Subtract(b);

    // ---- Implicit-flavored operations (native only as SDFs) ----

    public Shape SmoothUnion(Shape other, double blend) => new SmoothShape(BooleanOp.Union, this, other, blend);
    public Shape SmoothIntersect(Shape other, double blend) => new SmoothShape(BooleanOp.Intersection, this, other, blend);
    public Shape SmoothSubtract(Shape other, double blend) => new SmoothShape(BooleanOp.Difference, this, other, blend);

    public Shape Offset(double distance) => new OffsetShape(this, distance);
    public Shape Shell(double thickness) => new ShellShape(this, thickness);

    /// <summary>Intersects the shape with an infill pattern (e.g. <c>Sdf.Gyroid</c>).</summary>
    public Shape Lattice(Sdf pattern) => new LatticeShape(this, pattern);

    // ---- Placement ----

    public Shape Transform(in Matrix4d matrix) =>
        this is TransformShape t
            ? new TransformShape(t.Child, matrix * t.Matrix) // compose instead of nesting
            : new TransformShape(this, matrix);

    public Shape Translate(in Vector3d translation) => Transform(Matrix4d.CreateTranslation(translation));
    public Shape Translate(double x, double y, double z) => Translate(new Vector3d(x, y, z));
    public Shape RotateX(double radians) => Transform(Matrix4d.CreateRotationX(radians));
    public Shape RotateY(double radians) => Transform(Matrix4d.CreateRotationY(radians));
    public Shape RotateZ(double radians) => Transform(Matrix4d.CreateRotationZ(radians));
    public Shape Rotate(in Vector3d axis, double radians) => Transform(Matrix4d.CreateFromAxisAngle(axis, radians));
    public Shape Scale(double factor) => Transform(Matrix4d.CreateScale(factor));

    // ---- Lowering ----

    /// <summary>Lowers to an exact B-Rep solid. Throws <see cref="ShapeConversionException"/>
    /// when the graph contains operations with no B-Rep form (see <see cref="Explain"/>).</summary>
    public BrepSolid ToBrep()
    {
        ThrowIfImpossible(TargetRep.Brep);
        return ShapeCompiler.LowerBrep(this, Matrix4d.Identity);
    }

    /// <summary>
    /// Lowers to a signed distance field. Always succeeds: nodes without an exact SDF
    /// (extrusions, sweeps, imported B-Reps) are bridged through a tessellated mesh SDF
    /// at the given <paramref name="quality"/>.
    /// </summary>
    public Sdf ToImplicit(MeshQuality? quality = null) =>
        ShapeCompiler.LowerImplicit(this, Matrix4d.Identity, quality ?? MeshQuality.Default);

    /// <summary>
    /// Lowers to a half-edge mesh via the highest-fidelity route: an exact B-Rep
    /// tessellated once when possible, otherwise the SDF polygonized, otherwise
    /// per-node mesh operations.
    /// </summary>
    public HalfEdgeMesh ToMesh(MeshQuality? quality = null) =>
        ShapeCompiler.ToMesh(this, quality ?? MeshQuality.Default);

    /// <summary>Reports, per node, how a conversion to <paramref name="target"/> would
    /// be performed: natively, bridged through another representation, or not at all.</summary>
    public ConversionReport Explain(TargetRep target) => ShapeCompiler.Classify(this, target);

    public bool CanConvertTo(TargetRep target) => Explain(target).IsConvertible;

    private void ThrowIfImpossible(TargetRep target)
    {
        var report = Explain(target);
        if (!report.IsConvertible)
            throw new ShapeConversionException(report);
    }

    internal abstract string Describe();
}
