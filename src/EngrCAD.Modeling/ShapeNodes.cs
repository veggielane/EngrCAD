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

/// <summary>
/// Rectangular wedge / prismoid (OCCT's <c>BRepPrimAPI_MakeWedge</c>): a box whose top
/// face is narrowed in x and optionally shifted along it. It IS an extrusion — the XZ
/// cross-section is a trapezoid (a triangle when the top collapses), swept along y — so it
/// lowers by expanding to a sketch extrusion rather than by a fourth code path per target.
/// The node exists so the construction tree and <c>Explain</c> call it a wedge.
/// </summary>
internal sealed class WedgeShape : Shape
{
    public double SizeX { get; }
    public double SizeY { get; }
    public double SizeZ { get; }
    public double TopX { get; }
    public double TopOffsetX { get; }

    public WedgeShape(double sizeX, double sizeY, double sizeZ, double topX, double topOffsetX)
    {
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        TopX = topX;
        TopOffsetX = topOffsetX;
    }

    /// <summary>
    /// The equivalent sketch extrusion. The sketch plane's x is world Z and its y is world
    /// X (that order makes the plane normal +Y, so the extrusion grows the way the sketch
    /// plane faces), and it sits at y = −sizeY/2 so the result is centred like every other
    /// primitive. The trapezoid is wound counter-clockwise in sketch coordinates.
    /// </summary>
    public Shape Expanded => _expanded ??= Build();

    private Shape? _expanded;

    private Shape Build()
    {
        double halfX = SizeX / 2, halfZ = SizeZ / 2, halfTop = TopX / 2;
        // Exact-zero test: a zero top width is a sharp edge, i.e. a triangle, not a
        // trapezoid with two coincident corners (which would be a degenerate loop).
        var corners = TopX == 0
            ? new List<Vector2d>
            {
                new(-halfZ, -halfX), new(halfZ, TopOffsetX), new(-halfZ, halfX),
            }
            : [
                new(-halfZ, -halfX),
                new(halfZ, TopOffsetX - halfTop),
                new(halfZ, TopOffsetX + halfTop),
                new(-halfZ, halfX),
            ];
        var plane = SketchPlane.At((0, -SizeY / 2, 0), Vector3d.UnitZ, Vector3d.UnitX);
        return Extrude(Sketch.Polygon(corners), SizeY, plane);
    }

    internal override string Describe() =>
        $"Wedge({SizeX:g4}×{SizeY:g4}×{SizeZ:g4}, topX={TopX:g4}, offset={TopOffsetX:g4})";
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

    /// <summary>The tool's (axial, radius) silhouette — see <c>HoleSpec.ToolSilhouette</c>.</summary>
    public (double Axial, double Radius)[] ToolSilhouette { get; }

    public DrillShape(
        Shape child, Shape expanded, IReadOnlyList<Vector2d> points, double depth,
        Matrix4d planeMatrix, double surfaceDiameter, (double Axial, double Radius)[] silhouette)
    {
        Child = child;
        Expanded = expanded;
        Points = points;
        Depth = depth;
        PlaneMatrix = planeMatrix;
        SurfaceDiameter = surfaceDiameter;
        ToolSilhouette = silhouette;
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
    /// Drills sharing a placement plane are compared in the plane's own 2D coordinates —
    /// the surface circles are coplanar, so centre distance against summed surface radii
    /// is the exact test. Drills on DIFFERENT planes (opposing bores on the two faces of
    /// a plate, a side bore crossing a top bore) go through the 3D tool-vs-tool test
    /// below instead.
    /// </remarks>
    private void ValidateAgainstEarlierDrills()
    {
        // Same absolute 1e-9 weld-tier guard the within-call check uses: these are
        // distances between exactly-constructed tool axes.
        const double tolerance = 1e-9;
        for (var node = Child; node is DrillShape earlier; node = earlier.Child)
        {
            if (SamePlane(earlier.PlaneMatrix, PlaneMatrix))
                ValidateSamePlane(earlier, tolerance);
            else
                ValidateAcrossPlanes(earlier, tolerance);
        }
    }

    private void ValidateSamePlane(DrillShape earlier, double tolerance)
    {
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

    /// <summary>
    /// Tools placed on two different planes: a 3D interference test between the two
    /// solids of revolution. Reject when they may touch — intersecting or tangent cutting
    /// tools are the degenerate boolean input this node exists to catch, and they fail
    /// deep inside tessellation rather than at the call that created them.
    /// </summary>
    /// <remarks>
    /// <para>Cheap analytic pre-test first: each tool is contained in the cylinder of its
    /// largest radius about its axis segment, and two such cylinders are disjoint whenever
    /// the distance between the AXIS SEGMENTS exceeds the summed radii (any shared point
    /// would be within its own radius of each segment). One segment–segment distance per
    /// hole pair clears the overwhelming majority of layouts.</para>
    /// <para>Only when that is ambiguous does the pair get refined, by re-running the same
    /// separation test slab by slab over the tools' silhouette breakpoints. The refinement
    /// is <b>sound in the accept direction at any subdivision</b> — every slab pair
    /// separating still proves the tools disjoint — and it is EXACT wherever a slab's
    /// radius is constant, which covers simple and counterbored tools entirely; only a
    /// countersink's cone is over-approximated, and it is subdivided. So the residual
    /// error is a conservative refusal in a near-tangent band, which is precisely the
    /// configuration a boolean cannot survive anyway.</para>
    /// </remarks>
    private void ValidateAcrossPlanes(DrillShape earlier, double tolerance)
    {
        var mineAxis = PlaneMatrix.TransformVector(Vector3d.UnitZ);
        var theirsAxis = earlier.PlaneMatrix.TransformVector(Vector3d.UnitZ);
        foreach (var mine in Points)
        {
            var mineOrigin = PlaneMatrix.TransformPoint(new Vector3d(mine.X, mine.Y, 0));
            foreach (var theirs in earlier.Points)
            {
                var theirsOrigin = earlier.PlaneMatrix.TransformPoint(new Vector3d(theirs.X, theirs.Y, 0));
                if (Separated(
                        Slabs(mineOrigin, mineAxis, ToolSilhouette, 1),
                        Slabs(theirsOrigin, theirsAxis, earlier.ToolSilhouette, 1), tolerance))
                    continue;
                if (Separated(
                        Slabs(mineOrigin, mineAxis, ToolSilhouette, ConeSlabs),
                        Slabs(theirsOrigin, theirsAxis, earlier.ToolSilhouette, ConeSlabs), tolerance))
                    continue;
                throw new ArgumentException(
                    $"The hole at {mine} on this Drill's plane (axis through {mineOrigin:g6}) " +
                    $"intersects or touches the hole at {theirs} drilled by an earlier Drill call on a " +
                    $"different plane (axis through {theirsOrigin:g6}); their cutting tools overlap, which " +
                    $"is degenerate boolean input. Move one hole, or shorten its depth so the tools clear.");
            }
        }
    }

    /// <summary>
    /// Sub-slabs per sloped silhouette segment. Constant-radius segments are exact at any
    /// count and are never subdivided; only a countersink's cone needs this.
    /// </summary>
    private const int ConeSlabs = 16;

    /// <summary>A bounding cylinder: the axis segment plus the largest radius over it.</summary>
    private readonly record struct ToolSlab(Vector3d Start, Vector3d End, double Radius);

    /// <summary>
    /// Bounding cylinders covering one tool. <paramref name="subdivisions"/> = 1 gives the
    /// whole-tool pre-test bound (one slab per silhouette segment, or a single slab when
    /// the caller wants the coarsest form); each slab's radius is the largest the
    /// silhouette reaches over it, so the cover is always conservative.
    /// </summary>
    private static List<ToolSlab> Slabs(
        in Vector3d origin, in Vector3d axis, (double Axial, double Radius)[] silhouette, int subdivisions)
    {
        var slabs = new List<ToolSlab>();
        if (subdivisions <= 1)
        {
            // The coarse bound: one cylinder over the whole axial run.
            double radius = 0;
            foreach (var (_, r) in silhouette)
                radius = Math.Max(radius, r);
            slabs.Add(new ToolSlab(
                origin + axis * silhouette[0].Axial, origin + axis * silhouette[^1].Axial, radius));
            return slabs;
        }
        for (int i = 0; i + 1 < silhouette.Length; i++)
        {
            var (a0, r0) = silhouette[i];
            var (a1, r1) = silhouette[i + 1];
            if (a1 <= a0)
                continue; // a vertical silhouette step covers no axial extent
            // A constant-radius segment IS its bounding cylinder — subdividing it would
            // only cost pairs. Deliberate exact comparison: the radii come from the same
            // arithmetic, so equality here is a structural fact, not a measurement.
            int steps = r0 == r1 ? 1 : subdivisions;
            for (int k = 0; k < steps; k++)
            {
                double t0 = (double)k / steps, t1 = (double)(k + 1) / steps;
                double b0 = a0 + (a1 - a0) * t0, b1 = a0 + (a1 - a0) * t1;
                slabs.Add(new ToolSlab(
                    origin + axis * b0, origin + axis * b1,
                    Math.Max(r0 + (r1 - r0) * t0, r0 + (r1 - r0) * t1)));
            }
        }
        return slabs;
    }

    /// <summary>Every slab pair provably clear of every other: the tools cannot touch.</summary>
    private static bool Separated(List<ToolSlab> a, List<ToolSlab> b, double tolerance)
    {
        foreach (var slabA in a)
        {
            foreach (var slabB in b)
            {
                if (!Separated(slabA, slabB, tolerance))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Two bounding cylinders proved disjoint. Any ONE sufficient condition settles it, so
    /// two are tried:
    /// <list type="number">
    /// <item>the distance between the AXIS SEGMENTS exceeding the summed radii (any shared
    /// point would lie within its own radius of each segment) — the condition that decides
    /// skew and offset-parallel layouts; and</item>
    /// <item>a separating axis. A finite solid cylinder is CONVEX, so a direction whose
    /// projections do not overlap proves disjointness outright, and its support extent
    /// along a unit d is exactly |n·d|·halfLength + r·√(1 − (n·d)²).</item>
    /// </list>
    /// The second is what the first cannot see: two COLLINEAR tools drilled towards each
    /// other from opposite faces have axis segments at zero radial distance however much
    /// web is left between them, and only the axial projection separates them.
    /// </summary>
    private static bool Separated(in ToolSlab a, in ToolSlab b, double tolerance)
    {
        if (SegmentDistance(a.Start, a.End, b.Start, b.End) > a.Radius + b.Radius + tolerance)
            return true;

        var axisA = a.End - a.Start;
        var axisB = b.End - b.Start;
        var centres = (b.Start + b.End) / 2 - (a.Start + a.End) / 2;
        if (!axisA.TryNormalize(Tolerance.Default, out var nA) ||
            !axisB.TryNormalize(Tolerance.Default, out var nB))
            return false; // a zero-length slab: leave it to the segment test above
        double halfA = axisA.Length / 2, halfB = axisB.Length / 2;

        // The face normals plus their cross product are the SAT candidates two cylinders
        // share with two boxes; the centre offset and its radial components cover the
        // side-by-side layouts whose separating direction is neither axis.
        Span<Vector3d> candidates =
        [
            nA, nB, nA.Cross(nB),
            centres, centres - nA * centres.Dot(nA), centres - nB * centres.Dot(nB),
        ];
        foreach (var candidate in candidates)
        {
            if (!candidate.TryNormalize(Tolerance.Default, out var d))
                continue; // parallel axes, or coincident centres: nothing to project on
            if (Math.Abs(centres.Dot(d)) >
                Extent(nA, halfA, a.Radius, d) + Extent(nB, halfB, b.Radius, d) + tolerance)
                return true;
        }
        return false;
    }

    /// <summary>A solid cylinder's support extent about its centre along a unit direction.</summary>
    private static double Extent(in Vector3d axis, double halfLength, double radius, in Vector3d direction)
    {
        double along = axis.Dot(direction);
        return Math.Abs(along) * halfLength + radius * Math.Sqrt(Math.Max(0, 1 - along * along));
    }

    /// <summary>
    /// Distance between two closed segments (Ericson, <i>Real-Time Collision Detection</i>
    /// §5.1.9). The exact-zero guards are structural — a zero-length segment is a POINT,
    /// which is a different formula, not a tolerance question — and the parallel case
    /// (a zero denominator) pins s = 0 and lets the clamping below find the answer.
    /// </summary>
    private static double SegmentDistance(
        in Vector3d p1, in Vector3d q1, in Vector3d p2, in Vector3d q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        double a = d1.Dot(d1), e = d2.Dot(d2), f = d2.Dot(r);
        double s, t;
        if (a <= 0 && e <= 0)
            return r.Length;
        if (a <= 0)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            double c = d1.Dot(r);
            if (e <= 0)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                double b = d1.Dot(d2);
                double denominator = a * e - b * b;
                s = denominator > 0 ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((b - c) / a, 0, 1);
                }
            }
        }
        return (p1 + d1 * s - (p2 + d2 * t)).Length;
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
        profileOffset, startChamfer: chamferLength, endChamfer: chamferLength,
        leftHand: spec.LeftHand);

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

/// <summary>Isotropic remesh of the child's mesh lowering (mesh-native only).</summary>
internal sealed class RemeshShape(Shape child, RemeshOptions options) : Shape
{
    public Shape Child => child;
    public RemeshOptions Options => options;

    internal override string Describe() =>
        $"Remeshed(edge {options.TargetEdgeLength:0.###}, {options.Iterations} passes)";
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
