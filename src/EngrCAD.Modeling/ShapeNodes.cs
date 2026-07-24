using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

internal enum BooleanOp
{
    Union,
    Intersection,
    Difference,
}

internal sealed class BoxShape(Aabb bounds) : Shape
{
    public Aabb Bounds => bounds;
    internal override string Describe() => $"Box({bounds.Size.X:g4}×{bounds.Size.Y:g4}×{bounds.Size.Z:g4})";
}

internal sealed class SphereShape(double radius) : Shape
{
    public double Radius => radius;
    internal override string Describe() => $"Sphere(r={radius:g4})";
}

internal sealed class CylinderShape(double radius, double height) : Shape
{
    public double Radius => radius;
    public double Height => height;
    internal override string Describe() => $"Cylinder(r={radius:g4}, h={height:g4})";
}

internal sealed class TorusShape(double majorRadius, double minorRadius) : Shape
{
    public double MajorRadius => majorRadius;
    public double MinorRadius => minorRadius;
    internal override string Describe() => $"Torus(R={majorRadius:g4}, r={minorRadius:g4})";
}

internal sealed class ExtrudeShape : Shape
{
    public Profile? Profile { get; }
    public Vector3d Direction { get; }
    public IReadOnlyList<Profile>? Holes { get; }
    public Sketch? Sketch { get; }
    public Matrix4d PlaneMatrix { get; }
    public double Height { get; }

    public ExtrudeShape(Profile profile, Vector3d direction, IReadOnlyList<Profile>? holes)
    {
        Profile = profile;
        Direction = direction;
        Holes = holes;
    }

    public ExtrudeShape(Sketch sketch, SketchPlane plane, double height)
    {
        Sketch = sketch;
        PlaneMatrix = plane.ToMatrix();
        Height = height;
    }

    internal override string Describe() => Sketch is null ? "Extrude" : "Extrude(sketch)";
}

internal sealed class RevolveShape : Shape
{
    public Profile? Profile { get; }
    public Vector3d AxisOrigin { get; }
    public Vector3d AxisDirection { get; }
    public double Angle { get; }
    public IReadOnlyList<Profile>? Holes { get; }
    public Sketch? Sketch { get; }
    public Matrix4d PlaneMatrix { get; }

    public bool IsFullTurn => Math.Abs(Angle - 2 * Math.PI) < 1e-9;

    public RevolveShape(
        Profile profile, Vector3d axisOrigin, Vector3d axisDirection, double angle, IReadOnlyList<Profile>? holes)
    {
        Profile = profile;
        AxisOrigin = axisOrigin;
        AxisDirection = axisDirection;
        Angle = angle;
        Holes = holes;
    }

    public RevolveShape(Sketch sketch, SketchPlane plane, double angle)
    {
        Sketch = sketch;
        PlaneMatrix = plane.ToMatrix();
        Angle = angle;
    }

    internal override string Describe() => Sketch is null ? "Revolve" : "Revolve(sketch)";
}

internal sealed class SweepShape : Shape
{
    public Profile? Profile { get; }
    public Curve3d Path { get; }
    public IReadOnlyList<Profile>? Holes { get; }
    public Sketch? Sketch { get; }
    public Matrix4d PlaneMatrix { get; }

    public SweepShape(Profile profile, Curve3d path, IReadOnlyList<Profile>? holes)
    {
        Profile = profile;
        Path = path;
        Holes = holes;
    }

    public SweepShape(Sketch sketch, SketchPlane plane, Curve3d path)
    {
        Sketch = sketch;
        PlaneMatrix = plane.ToMatrix();
        Path = path;
    }

    internal override string Describe() => Sketch is null ? "Sweep" : "Sweep(sketch)";
}

internal sealed class BooleanShape(BooleanOp op, Shape a, Shape b) : Shape
{
    public BooleanOp Op => op;
    public Shape A => a;
    public Shape B => b;
    internal override string Describe() => op.ToString();
}

internal sealed class SmoothShape(BooleanOp op, Shape a, Shape b, double blend) : Shape
{
    public BooleanOp Op => op;
    public Shape A => a;
    public Shape B => b;
    public double Blend => blend;
    internal override string Describe() => $"Smooth{Op}(k={blend:g4})";
}

internal sealed class OffsetShape(Shape child, double distance) : Shape
{
    public Shape Child => child;
    public double Distance => distance;
    internal override string Describe() => $"Offset(d={distance:g4})";
}

internal sealed class ShellShape(Shape child, double thickness) : Shape
{
    public Shape Child => child;
    public double Thickness => thickness;
    internal override string Describe() => $"Shell(t={thickness:g4})";
}

internal sealed class LatticeShape(Shape child, Sdf pattern) : Shape
{
    public Shape Child => child;
    public Sdf Pattern => pattern;
    internal override string Describe() => "Lattice";
}

/// <summary>Rim chamfer/fillet applied to faces selected by a query on the lowered solid.</summary>
internal sealed class RimShape(
    Shape child, bool fillet, double amount, double sideAmount,
    Func<BrepSolid, IEnumerable<BrepFace>> selector) : Shape
{
    public Shape Child => child;
    public bool IsFillet => fillet;
    public double Amount => amount;
    public double SideAmount => sideAmount;
    public Func<BrepSolid, IEnumerable<BrepFace>> Selector => selector;
    internal override string Describe() => fillet ? $"Fillet(r={amount:g4})" : $"Chamfer({amount:g4})";
}

/// <summary>
/// Drilled holes: the body minus one revolved hole tool per point. A dedicated node
/// (rather than the bare difference chain in <see cref="Expanded"/>) so B-Rep lowering
/// can validate the configuration against the lowered body — a tool whose flat bottom
/// is coplanar with a planar body face is degenerate boolean input that would otherwise
/// fail deep inside tessellation.
/// </summary>
internal sealed class DrillShape(
    Shape child, Shape expanded, IReadOnlyList<Vector2d> points, double depth, Matrix4d planeMatrix) : Shape
{
    public Shape Child => child;
    public Shape Expanded => expanded;
    public IReadOnlyList<Vector2d> Points => points;
    public double Depth => depth;
    public Matrix4d PlaneMatrix => planeMatrix;
    internal override string Describe() => $"Drill({points.Count} holes)";
}

/// <summary>
/// A helical thread solid (external form: threaded rod along +Z, z ∈ [0, length]) built
/// from the ISO 68-1 basic profile of <see cref="ThreadSpec"/>. Used directly by
/// <see cref="Shape.ExternalThread(ThreadSpec, double, double, bool)"/> (with a negative
/// <paramref name="profileOffset"/> = printing clearance) and, flipped, as the cutting
/// tool of <see cref="Shape.ThreadedHole"/> (positive offset grows the void). Implicit-
/// native via <see cref="Sdf.Thread"/>; no B-Rep lowering yet (a true helical sweep is
/// future work), so B-Rep is Impossible and meshes bridge through Surface Nets.
/// </summary>
internal sealed class ThreadShape(ThreadSpec spec, double length, double profileOffset, double chamferLength) : Shape
{
    public ThreadSpec Spec => spec;
    public double Length => length;
    public double ProfileOffset => profileOffset;
    public double ChamferLength => chamferLength;

    internal Sdf ToSdf() => Sdf.Thread(
        spec.MajorDiameter / 2, spec.MinorDiameter / 2, spec.Pitch,
        spec.CrestFlatWidth, spec.RootFlatWidth, length,
        profileOffset, startChamfer: chamferLength, endChamfer: chamferLength);

    internal override string Describe() => $"Thread({spec.Designation}, L={length:g4})";
}

internal sealed class TransformShape(Shape child, Matrix4d matrix) : Shape
{
    public Shape Child => child;
    public Matrix4d Matrix => matrix;
    internal override string Describe() => "Transform";
}

/// <summary>Wraps an existing BrepSolid, HalfEdgeMesh, or Sdf as a graph leaf.</summary>
internal sealed class SourceShape(object geometry) : Shape
{
    public object Geometry => geometry;
    internal override string Describe() => geometry switch
    {
        BrepSolid => "From(BrepSolid)",
        HalfEdgeMesh => "From(Mesh)",
        Sdf => "From(Sdf)",
        _ => "From(?)",
    };
}
