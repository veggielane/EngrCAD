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

internal sealed class ConeShape(double bottomRadius, double topRadius, double height) : Shape
{
    public double BottomRadius => bottomRadius;
    public double TopRadius => topRadius;
    public double Height => height;
    internal override string Describe() => $"Cone(r1={bottomRadius:g4}, r2={topRadius:g4}, h={height:g4})";
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
internal sealed class DrillShape : Shape
{
    public Shape Child { get; }
    public Shape Expanded { get; }
    public IReadOnlyList<Vector2d> Points { get; }
    public double Depth { get; }
    public Matrix4d PlaneMatrix { get; }

    /// <summary>The tool's diameter where it meets the drilled face (cbore/csk included).</summary>
    public double SurfaceDiameter { get; }

    public DrillShape(
        Shape child, Shape expanded, IReadOnlyList<Vector2d> points, double depth,
        Matrix4d planeMatrix, double surfaceDiameter)
    {
        Child = child;
        Expanded = expanded;
        Points = points;
        Depth = depth;
        PlaneMatrix = planeMatrix;
        SurfaceDiameter = surfaceDiameter;
        ValidateAgainstEarlierDrills();
    }

    internal override string Describe() => $"Drill({Points.Count} holes)";

    /// <summary>
    /// Cross-validates this drill's holes against every EARLIER drill placed on the same
    /// plane. <see cref="Shape.Drill"/> already rejects overlapping or tangent holes
    /// WITHIN one call, but two calls — the normal way to mix clearance holes with
    /// counterbores — could still place tools that touch, which is the same degenerate
    /// boolean input and fails just as deep inside tessellation.
    /// </summary>
    /// <remarks>
    /// Only drills sharing a placement plane are compared, and the walk stops at the
    /// first non-drill node. Two drills on DIFFERENT planes can still produce
    /// intersecting tools (opposing bores on the two faces of a plate, for instance);
    /// deciding that in general is a tool-vs-tool solid intersection test, not a 2D
    /// centre-distance test, and is deliberately left out rather than half-done.
    /// </remarks>
    private void ValidateAgainstEarlierDrills()
    {
        // Same absolute 1e-9 weld-tier guard the within-call check uses: these are
        // distances between exactly-constructed tool axes.
        const double tolerance = 1e-9;
        for (var node = Child; node is DrillShape earlier; node = earlier.Child)
        {
            if (!SamePlane(earlier.PlaneMatrix, PlaneMatrix))
                continue;
            double limit = (SurfaceDiameter + earlier.SurfaceDiameter) / 2;
            foreach (var mine in Points)
            {
                foreach (var theirs in earlier.Points)
                {
                    if (mine.DistanceTo(theirs) <= limit + tolerance)
                        throw new ArgumentException(
                            $"The hole at {mine} (surface diameter {SurfaceDiameter:g6}) overlaps or is " +
                            $"tangent to a hole at {theirs} (surface diameter {earlier.SurfaceDiameter:g6}) " +
                            $"drilled by an earlier Drill call on the same plane; centers must be more than " +
                            $"{limit:g6} apart.");
                }
            }
        }
    }

    /// <summary>
    /// Do two placement matrices describe the same plane and the same 2D coordinate
    /// system? Compared by mapping the frame's own probe points rather than by walking
    /// matrix entries, so the test is in model units and uses the weld tier.
    /// </summary>
    private static readonly Vector3d[] PlaneProbes = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

    private static bool SamePlane(in Matrix4d a, in Matrix4d b)
    {
        foreach (var probe in PlaneProbes)
        {
            if (!a.TransformPoint(probe).AreEqual(b.TransformPoint(probe), Tolerance.Default))
                return false;
        }
        return true;
    }
}

/// <summary>
/// A helical thread solid (external form: threaded rod along +Z, z ∈ [0, length]) built
/// from the ISO 68-1 basic profile of <see cref="ThreadSpec"/>. Used directly by
/// <see cref="Shape.ExternalThread(ThreadSpec, double, double, bool)"/> (with a negative
/// <paramref name="profileOffset"/> = printing clearance) and, flipped, as the cutting
/// tool of <see cref="Shape.ThreadedHole"/> (positive offset grows the void). Implicit-
/// native via <see cref="Sdf.Thread"/>. B-Rep-native via
/// <see cref="SolidFactory.MakeThreadedRod"/> (one boolean-free helical sweep, crest
/// phase-aligned with the SDF) when the profile is unmodified — zero offset, no end
/// chamfers — under proper rigid + uniform-scale placements; otherwise B-Rep is
/// Impossible with a per-cause report (chamfer cones and distance-field profile offsets
/// have no exact B-Rep counterpart yet, and a mirrored thread is left-handed).
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

/// <summary>
/// An internally threaded hole feature: the body with, per point, a tap-drill pilot
/// (via <see cref="Shape.Drill"/>) and the clearance-grown external thread form
/// subtracted. Captured as a node — rather than a raw boolean chain — so each target
/// takes its own route. Implicit and mesh lower <see cref="Expanded"/> (exact SDF
/// subtraction, no coplanarity concerns). B-Rep does NOT lower the expansion — the
/// pilot bore wall and the tool's root band would be coaxial (tangent, unsupported
/// boolean input) — and instead subtracts ONE combined tool per point: the thread form
/// clipped at the pilot radius, so the pilot volume is part of the same boolean-free
/// helical rod and the only intersections are exact spiral-arc chains on the drilled
/// plane(s). Nonzero clearance keeps B-Rep Impossible (distance-field profile offset,
/// as for <see cref="ThreadShape"/>).
/// </summary>
internal sealed class ThreadedHoleShape(
    Shape child, Shape expanded, ThreadSpec spec, IReadOnlyList<Vector2d> points,
    double depth, Matrix4d planeMatrix, double clearance) : Shape
{
    /// <summary>The body being threaded.</summary>
    public Shape Child => child;

    /// <summary>The equivalent pilot-drill + thread-tool subtraction chain.</summary>
    public Shape Expanded => expanded;

    public ThreadSpec Spec => spec;
    public IReadOnlyList<Vector2d> Points => points;
    public double Depth => depth;
    public Matrix4d PlaneMatrix => planeMatrix;
    public double Clearance => clearance;

    internal override string Describe() => $"ThreadedHole({spec.Designation}, {points.Count} holes)";
}

/// <summary>Convex hull of the operands' mesh vertices (quickhull; mesh-native only).</summary>
internal sealed class HullShape(IReadOnlyList<Shape> operands) : Shape
{
    public IReadOnlyList<Shape> Operands => operands;
    internal override string Describe() => $"Hull({operands.Count} operands)";
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
