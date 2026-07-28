using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Implicit;
using EngrCAD.Interop;
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

    /// <summary>
    /// Cone frustum along +Z, centered at the origin (OpenSCAD's <c>cylinder(r1, r2)</c>):
    /// radius <paramref name="bottomRadius"/> at z = −height/2 growing linearly to
    /// <paramref name="topRadius"/> at z = +height/2. A zero radius makes that end a
    /// pointed apex. Native in all three representations.
    /// </summary>
    public static Shape Cone(double bottomRadius, double topRadius, double height)
    {
        if (bottomRadius < 0 || topRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(bottomRadius), "Radii must be non-negative.");
        if (bottomRadius <= 0 && topRadius <= 0)
            throw new ArgumentException("At least one of the two radii must be positive.", nameof(bottomRadius));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        return new ConeShape(bottomRadius, topRadius, height);
    }

    /// <summary>
    /// Rectangular wedge along +Z, centred at the origin — OCCT's
    /// <c>BRepPrimAPI_MakeWedge</c>, and the remaining primitive OpenSCAD reaches for
    /// <c>polyhedron</c> to build. The base at z = −height/2 is
    /// <paramref name="sizeX"/> × <paramref name="sizeY"/>; the top at z = +height/2 keeps
    /// the same y but is <paramref name="topX"/> wide, centred at
    /// x = <paramref name="topOffsetX"/>. Native in all three representations.
    ///
    /// <para>The family it covers: <paramref name="topX"/> = 0 gives a sharp top edge (a
    /// symmetric chisel), and moving that edge over one side with
    /// <c>topOffsetX: ±sizeX/2</c> gives the classic RAMP — a right triangular prism.
    /// A positive <paramref name="topX"/> gives a truncated wedge (a dovetail rail, a
    /// draft-angled boss), and <c>topX: sizeX</c> with a nonzero offset gives a sheared
    /// box. The taper is in x only; a solid tapering in BOTH directions is a loft, not a
    /// wedge.</para>
    /// </summary>
    public static Shape Wedge(
        double sizeX, double sizeY, double sizeZ, double topX = 0, double topOffsetX = 0)
    {
        if (sizeX <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeX));
        if (sizeY <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeY));
        if (sizeZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeZ));
        if (topX < 0)
            throw new ArgumentOutOfRangeException(nameof(topX), "The top width cannot be negative.");
        if (!double.IsFinite(topOffsetX))
            throw new ArgumentOutOfRangeException(nameof(topOffsetX));
        return new WedgeShape(sizeX, sizeY, sizeZ, topX, topOffsetX);
    }

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

    /// <summary>
    /// Drills holes: one <paramref name="hole"/> at each 2D point on
    /// <paramref name="plane"/> (default world XY), cutting along −normal to
    /// <paramref name="depth"/> below the plane (give a depth past the far side for
    /// through-holes). Each tool is a revolved sketch, so drilling stays exact in
    /// every representation.
    /// </summary>
    public Shape Drill(HoleSpec hole, IReadOnlyList<Vector2d> points, double depth, SketchPlane? plane = null)
    {
        if (depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(depth));

        // Overlapping or tangent holes make the tools' surface circles intersect (or
        // touch) on the drilled plane — degenerate boolean input that fails deep in
        // tessellation. Reject up front, naming the offending pair.
        double surfaceDiameter = hole.SurfaceDiameter;
        const double tolerance = 1e-9;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                double distance = points[i].DistanceTo(points[j]);
                if (distance <= surfaceDiameter + tolerance)
                    throw new ArgumentException(
                        $"Holes at {points[i]} and {points[j]} (surface diameter {surfaceDiameter:g6} each) " +
                        $"overlap or are tangent; centers must be more than {surfaceDiameter:g6} apart.",
                        nameof(points));
            }
        }

        var placementPlane = plane ?? SketchPlane.XY;
        var toolProfile = hole.ToolProfile(depth);
        var result = this;
        foreach (var point in points)
        {
            var tool = Revolve(toolProfile).Transform(placementPlane.ToMatrixAt(point));
            result -= tool;
        }
        return new DrillShape(
            this, result, points, depth, placementPlane.ToMatrix(), surfaceDiameter,
            hole.ToolSilhouette(depth));
    }

    // ---- Threads (modeled helical geometry) ----

    /// <summary>
    /// An externally threaded stud along +Z, z ∈ [0, <paramref name="length"/>], with
    /// the ISO 68-1 basic profile of <paramref name="spec"/> (see <see cref="ThreadSpec"/>).
    /// <paramref name="clearance"/> is the 3D-printing fit allowance, applied normal to
    /// the flanks (the profile is eroded perpendicular to its boundary, so crests and
    /// roots also drop radially by the same amount) — the external thread <em>shrinks</em>;
    /// pair with the same clearance on <see cref="ThreadedHole"/>, whose void grows.
    /// Typical FDM values: 0.1–0.25 mm. <paramref name="chamferEnds"/> cuts 45° lead-in
    /// cones on both ends down to the minor diameter.
    /// <para>Representation support: implicit-Native (<see cref="Sdf.Thread"/>, exact
    /// sign). B-Rep-<b>Native</b> for the unmodified basic profile — zero
    /// <paramref name="clearance"/> and <c>chamferEnds: false</c> — as a boolean-free
    /// helical sweep (<see cref="SolidFactory.MakeThreadedRod"/>: one exact
    /// <see cref="HelicalSurface"/> band per profile facet sharing <see cref="Helix3d"/>
    /// rails, spiral-bounded flat caps; not STEP-exportable). With chamfers or clearance
    /// B-Rep stays Impossible — truthfully reported — because 45° chamfer cones cut the
    /// helical bands (future surface-intersection work) and clearance offsets the
    /// profile as a distance field (reflex corners round into arcs with no exact B-Rep
    /// counterpart); meshes then bridge through Surface Nets — the printing route.
    /// Unchamfered, clearance-free threads mesh via exact B-Rep tessellation.</para>
    /// </summary>
    public static Shape ExternalThread(ThreadSpec spec, double length, double clearance = 0, bool chamferEnds = true)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        ValidateThreadClearance(clearance, spec);
        return new ThreadShape(spec, length, -clearance, chamferEnds ? spec.ThreadDepth : 0);
    }

    /// <summary>Coarse-metric convenience overload:
    /// <c>ExternalThread(8, …)</c> is an M8×1.25 stud (see <see cref="StandardThreads.Metric"/>).</summary>
    public static Shape ExternalThread(double size, double length, double clearance = 0, bool chamferEnds = true) =>
        ExternalThread(StandardThreads.Metric(size), length, clearance, chamferEnds);

    /// <summary>
    /// Cuts internally threaded holes: at each 2D point on <paramref name="plane"/>
    /// (default world XY) a tap-drill pilot (<see cref="ThreadSpec.TapDrillDiameter"/>,
    /// via <see cref="Drill"/>) plus a modeled thread void, both along −normal to
    /// <paramref name="depth"/> below the plane. The internal and external basic
    /// profiles coincide (ISO 68-1), so the void is the external form dilated by
    /// <paramref name="clearance"/> normal to the flanks — the hole <em>grows</em> with
    /// clearance (typical FDM: 0.1–0.25 mm; default 0). The pilot truncates the
    /// internal thread's crests to the tap-drill diameter, as tapping does.
    /// <para>Representation support: implicit-Native (exact SDF subtraction) and
    /// B-Rep-<b>Native</b> at zero <paramref name="clearance"/> — the pilot and thread
    /// are subtracted as ONE combined tool per point (the thread form clipped at the
    /// pilot radius, so no coaxial pilot-bore∩root-band tangency exists) whose helical
    /// bands cross the drilled plane in exact spiral arcs. Nonzero clearance keeps
    /// B-Rep Impossible (the profile offset is a distance field — see
    /// <see cref="ExternalThread(ThreadSpec, double, double, bool)"/>) and meshes then
    /// bridge through Surface Nets. Like <see cref="Drill"/>, a blind depth whose tool
    /// bottom is exactly coplanar with a body face is rejected at B-Rep lowering.</para>
    /// </summary>
    public Shape ThreadedHole(
        ThreadSpec spec, IReadOnlyList<Vector2d> points, double depth,
        SketchPlane? plane = null, double clearance = 0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(depth));
        ValidateThreadClearance(clearance, spec);

        // Overlapping thread voids are well-defined SDF unions, but two fasteners
        // cannot share material — reject like Drill does, against the thread's
        // (clearance-grown) major diameter rather than the pilot's.
        const double tolerance = 1e-9; // same absolute weld-scale guard Drill uses
        double surfaceDiameter = spec.MajorDiameter + 2 * clearance;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                if (points[i].DistanceTo(points[j]) <= surfaceDiameter + tolerance)
                    throw new ArgumentException(
                        $"Threaded holes at {points[i]} and {points[j]} (thread major diameter " +
                        $"{surfaceDiameter:g6} each) overlap or are tangent.", nameof(points));
            }
        }

        var placementPlane = plane ?? SketchPlane.XY;
        var result = Drill(HoleSpec.Simple(spec.TapDrillDiameter), points, depth, placementPlane);

        // The cutting tool is the external thread form, flipped to advance along
        // −normal and raised so it overshoots the drilled surface (mirroring the drill
        // tools' overshoot; SDF subtraction has no coplanarity concerns, but the tool
        // must not stop exactly at the surface). The flip diag(1, −1, −1) is an exact
        // rotation by π about X — Math.Cos(π) style matrices carry ~1e-16 skew.
        double overshoot = 0.05 * Math.Max(depth, spec.MajorDiameter);
        var flipDown = new Matrix4d(
            1, 0, 0, 0,
            0, -1, 0, 0,
            0, 0, -1, overshoot,
            0, 0, 0, 1);
        foreach (var point in points)
        {
            var tool = new ThreadShape(spec, depth + overshoot, +clearance, chamferLength: 0)
                .Transform(flipDown)
                .Transform(placementPlane.ToMatrixAt(point));
            result -= tool;
        }

        // Wrapped in a node (like Drill) so B-Rep classification can report the real
        // blocker instead of attempting the coaxial tool∩pilot-bore boolean, which the
        // splitter cannot handle yet; implicit/mesh lower the expansion unchanged.
        return new ThreadedHoleShape(this, result, spec, points, depth, placementPlane.ToMatrix(), clearance);
    }

    private static void ValidateThreadClearance(double clearance, ThreadSpec spec)
    {
        if (clearance < 0)
            throw new ArgumentOutOfRangeException(nameof(clearance),
                "Thread clearance must be non-negative (it always grows the internal void and shrinks the external thread).");
        if (clearance >= spec.ThreadDepth / 2)
            throw new ArgumentOutOfRangeException(nameof(clearance),
                $"Thread clearance {clearance:g4} would degenerate the {spec.Designation} profile " +
                $"(limit: half the thread depth, {spec.ThreadDepth / 2:g4}).");
    }

    // ---- Rim features (chamfer / fillet) ----

    /// <summary>
    /// 45° chamfer of the outer rims of planar faces selected by <paramref name="faces"/>
    /// (a query over the lowered B-Rep, e.g.
    /// <c>s => s.PlanarFacesWithNormal(Vector3d.UnitZ)</c>). Straight edges miter at
    /// sharp corners; full circular rims get exact cone bands.
    /// </summary>
    public Shape Chamfer(double setback, Func<BrepSolid, IEnumerable<BrepFace>> faces) =>
        Chamfer(setback, setback, faces);

    public Shape Chamfer(double topSetback, double sideSetback, Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        if (topSetback <= 0 || sideSetback <= 0)
            throw new ArgumentOutOfRangeException(nameof(topSetback));
        return new RimShape(this, fillet: false, topSetback, sideSetback, faces);
    }

    /// <summary>
    /// Fillets the outer rims of selected planar faces. Rims may be full circles,
    /// tangent-continuous line+arc chains, or chains with SHARP corners between straight
    /// edges — those miter on the exact bicylinder ellipse (convex and reflex alike). A
    /// sharp corner where an ARC meets another edge is still refused: torus ∩ cylinder is
    /// not a conic, so there is no exact miter to build.
    /// </summary>
    public Shape Fillet(double radius, Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return new RimShape(this, fillet: true, radius, radius, faces);
    }

    /// <summary>
    /// Chamfer by a setback measured IN the selected face and an angle measured FROM it —
    /// the "distance and angle" spelling; 45° equals <see cref="Chamfer(double, Func{BrepSolid, IEnumerable{BrepFace}})"/>.
    /// </summary>
    public Shape ChamferAtAngle(double setback, double angleDegrees, Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        if (setback <= 0)
            throw new ArgumentOutOfRangeException(nameof(setback));
        if (angleDegrees <= 0 || angleDegrees >= 90)
            throw new ArgumentOutOfRangeException(nameof(angleDegrees),
                "The chamfer angle is measured from the chamfered face and must lie strictly between 0° and 90°.");
        return new RimShape(
            this, fillet: false, setback, setback * Math.Tan(angleDegrees * Math.PI / 180), faces);
    }

    /// <summary>
    /// Fillets selected EDGES rather than faces (e.g.
    /// <c>s => s.PlanarFacesWithNormal(Vector3d.UnitZ).SelectMany(f => f.RimEdges())</c>,
    /// or any LINQ over <c>solid.Edges</c> with <c>IsLinear</c>/<c>IsCircular</c>/
    /// <c>ConvexEdges</c>). The selection is resolved to the rim features that reproduce
    /// it — see <see cref="Filleting.RimFacesFor"/> for exactly which sets are exact and
    /// why the rest are refused.
    /// </summary>
    public Shape FilletEdges(double radius, Func<BrepSolid, IEnumerable<BrepEdge>> edges)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return new RimShape(this, fillet: true, radius, radius,
            solid => Filleting.RimFacesFor(solid, edges(solid)));
    }

    /// <summary>Chamfers selected EDGES; see <see cref="FilletEdges"/>.</summary>
    public Shape ChamferEdges(double setback, Func<BrepSolid, IEnumerable<BrepEdge>> edges) =>
        ChamferEdges(setback, setback, edges);

    /// <inheritdoc cref="ChamferEdges(double, Func{BrepSolid, IEnumerable{BrepEdge}})"/>
    public Shape ChamferEdges(
        double topSetback, double sideSetback, Func<BrepSolid, IEnumerable<BrepEdge>> edges)
    {
        if (topSetback <= 0 || sideSetback <= 0)
            throw new ArgumentOutOfRangeException(nameof(topSetback));
        return new RimShape(this, fillet: false, topSetback, sideSetback,
            solid => Filleting.RimFacesFor(solid, edges(solid)));
    }

    // ---- Patterns ----

    /// <summary>This shape unioned with <paramref name="count"/> − 1 copies stepped
    /// along <paramref name="step"/>.</summary>
    public Shape PatternLinear(int count, in Vector3d step)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        var copies = new List<Shape>(count) { this };
        for (int i = 1; i < count; i++)
            copies.Add(Transform(Matrix4d.CreateTranslation(step * i)));
        return UnionTree(copies);
    }

    /// <summary>This shape unioned with copies rotated about an axis
    /// (<paramref name="angleStep"/> defaults to a full turn divided evenly).</summary>
    public Shape PatternCircular(int count, in Vector3d axisOrigin, in Vector3d axisDirection, double? angleStep = null)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        double step = angleStep ?? 2 * Math.PI / count;
        var axis = axisDirection.Normalized();
        var toOrigin = Matrix4d.CreateTranslation(-axisOrigin);
        var back = Matrix4d.CreateTranslation(axisOrigin);
        var copies = new List<Shape>(count) { this };
        for (int i = 1; i < count; i++)
            copies.Add(Transform(back * Matrix4d.CreateFromAxisAngle(axis, step * i) * toOrigin));
        return UnionTree(copies);
    }

    private static Shape UnionTree(List<Shape> shapes)
    {
        // Balanced union tree keeps boolean operand complexity even.
        while (shapes.Count > 1)
        {
            var next = new List<Shape>((shapes.Count + 1) / 2);
            for (int i = 0; i < shapes.Count; i += 2)
                next.Add(i + 1 < shapes.Count ? shapes[i] | shapes[i + 1] : shapes[i]);
            shapes = next;
        }
        return shapes[0];
    }

    // ---- Convex hull ----

    /// <summary>
    /// Convex hull of the operands (OpenSCAD's <c>hull()</c>), computed by quickhull
    /// over the operands' MESH vertices. Honest support story: exact for polyhedral
    /// operands (boxes, polygonal extrusions); curved operands contribute their
    /// tessellated vertices, so the result is the hull of the tessellation (inscribed
    /// in the true hull) — Bridged for every target in <see cref="Explain"/> terms,
    /// and never convertible to B-Rep (no mesh→B-Rep import).
    /// </summary>
    public static Shape Hull(params Shape[] operands)
    {
        if (operands is null || operands.Length == 0)
            throw new ArgumentException("Hull needs at least one operand.", nameof(operands));
        return new HullShape([.. operands]);
    }

    /// <summary>
    /// Isotropic remesh: rebuild this shape's triangulation toward a uniform edge length
    /// (<see cref="Remesher"/>) while keeping the surface it already has. The operation for
    /// display quality and for FEA prep, where a solver wants well-shaped triangles of a
    /// known size rather than whatever the tessellator produced.
    /// </summary>
    /// <param name="targetEdgeLength">The edge length to converge on, in this shape's own units.</param>
    /// <param name="iterations">Remesh passes. More costs time and buys uniformity.</param>
    /// <remarks>
    /// <b>This is a geometry-changing node, and <see cref="Explain"/> says so.</b> Remeshing
    /// is defined on a triangulation, so it is <b>Native for mesh only</b>: to B-Rep it is
    /// Impossible (there is no mesh → B-Rep import, and the result is a tessellation rather
    /// than a surface), and to implicit it is Bridged through a mesh SDF of the remeshed
    /// triangles — which is a different field from the child's own, since it carries the
    /// tessellation's chord error. Reach for it at the end of a model, not in the middle.
    /// <para>
    /// The shape is preserved by projecting every pass back onto the child's mesh as lowered
    /// — the default when <see cref="RemeshOptions.ProjectionTarget"/> is null — because
    /// without a target, smoothing is curvature flow and the model shrinks a little every
    /// pass. So what is preserved is the child's <i>tessellation</i> at the requested quality,
    /// not its exact surface: remesh a curved shape at a coarse quality and the remesh is
    /// faithful to that coarse mesh. Pass an explicit target (EngrCAD.Interop's
    /// <c>SdfProjectionTarget</c> over <c>ToImplicit()</c>) to project onto the exact field
    /// instead.
    /// </para>
    /// <para>
    /// A uniform scale above this node scales the target edge length with it, so the node
    /// means the same thing wherever it sits; a sheared or non-uniformly scaled placement has
    /// no single factor, and the volume-preserving equivalent (the cube root of the
    /// determinant) is used instead.
    /// </para>
    /// </remarks>
    public Shape Remeshed(double targetEdgeLength, int iterations = 10) =>
        Remeshed(new RemeshOptions(targetEdgeLength) { Iterations = iterations });

    /// <summary>
    /// Isotropic remesh with full control over the remesher's behaviour (feature angle,
    /// smoothing rule, scheduling, projection mode and target). See
    /// <see cref="Remeshed(double, int)"/> for the support story and the projection default.
    /// </summary>
    public Shape Remeshed(RemeshOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.TargetEdgeLength > 0))
            throw new ArgumentOutOfRangeException(nameof(options), "TargetEdgeLength must be positive.");
        return new RemeshShape(this, options);
    }

    // ---- Text ----

    /// <summary>
    /// Modeled text (OpenSCAD's <c>text()</c> in 3D): the glyph outlines of
    /// <paramref name="text"/> extruded <paramref name="height"/> along the normal of
    /// <paramref name="plane"/> (default world XY) and unioned. TrueType outlines are
    /// lines and quadratic Béziers, which map onto <see cref="Sketch"/> segments
    /// exactly — so text is as exact as any other sketch feature: exact NURBS profiles
    /// in B-Rep, the exact 2D signed distance in implicit, crisp tessellation in mesh.
    /// Counters (the holes in O, A, 8) come through as holes.
    /// <para><paramref name="size"/> is the <b>em size</b>; for a specified letter
    /// height use <c>font.EmSizeForCapHeight(h)</c>. The text's origin is the
    /// <b>baseline</b> at the start of the first line (see
    /// <see cref="TextOutlines"/> for the full convention), and
    /// <see cref="TextStyle"/> carries spacing, line spacing, alignment and
    /// kerning.</para>
    /// <para><b>Engraving and embossing</b> need no special operation: place the text
    /// on a face with <c>SketchPlane.On(face)</c> (or an explicit
    /// <c>SketchPlane.At(...)</c>) and union it to emboss, or sink the plane by the
    /// depth and subtract a tool that overshoots the face to engrave. Note the honest
    /// boundary documented in the Modeling README: the text itself is exact in every
    /// representation, but <em>booleans between lettering and a body</em> are limited by
    /// the B-Rep boolean engine's handling of sketch-extrusion tools (a limitation with
    /// nothing to do with text). Route those through the implicit lowering —
    /// <c>Shape.From(result.ToImplicit())</c> — where the boolean is exact.</para>
    /// </summary>
    /// <example>
    /// <code>
    /// var font = TrueTypeFont.Load(fontPath);
    /// var plate = Shape.Box(70, 22, 4);                                  // top face at z = 2
    /// var top = SketchPlane.At((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY);
    /// var pocket = SketchPlane.At((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
    /// var style = new TextStyle { Align = TextAlign.Center };
    ///
    /// var badge = Shape.Text("ENGRCAD", font, size: 12, height: 1.2, top, style);
    /// var embossed = plate | badge;
    /// var engraved = plate - Shape.Text("ENGRCAD", font, 12, 1.5, pocket, style);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException">The text draws nothing (empty or all
    /// blanks), or the font has no glyph for one of its characters.</exception>
    public static Shape Text(
        string text, TrueTypeFont font, double size, double height,
        SketchPlane? plane = null, TextStyle? style = null)
    {
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        var sketches = TextOutlines.Sketches(text, font, size, style);
        if (sketches.Count == 0)
            throw new ArgumentException(
                $"\"{text}\" produces no geometry: it is empty or contains only blank glyphs.", nameof(text));

        // Glyphs are disjoint in any sane layout, so this union takes the boolean
        // engine's disjoint fast path (whole-body classification, multi-shell result).
        var glyphs = new List<Shape>(sketches.Count);
        foreach (var sketch in sketches)
            glyphs.Add(Extrude(sketch, height, plane));
        return UnionTree(glyphs);
    }

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

    /// <summary>
    /// Mirror across the plane through <paramref name="point"/> with
    /// <paramref name="normal"/> (OpenSCAD's <c>mirror()</c>). Correct in every
    /// representation: meshes transform positions and reverse winding (staying
    /// outward-oriented), implicit lowering reflects the query point (exact), and
    /// B-Rep support follows the node: box/cylinder/sketch-extrude handle any affine
    /// map, sphere/torus/cone re-place natively under mirrored similarities, while
    /// mirrored revolve/sweep/rim/drill nodes have no B-Rep lowering yet (their mirror
    /// is exact via mesh or SDF — see <see cref="Explain"/>).
    /// </summary>
    public Shape Mirror(in Vector3d point, in Vector3d normal)
    {
        var n = normal.Normalized();
        double t = 2 * point.Dot(n);
        // Householder reflection I − 2nnᵀ, translated so the plane passes through point.
        return Transform(new Matrix4d(
            1 - 2 * n.X * n.X, -2 * n.X * n.Y, -2 * n.X * n.Z, t * n.X,
            -2 * n.Y * n.X, 1 - 2 * n.Y * n.Y, -2 * n.Y * n.Z, t * n.Y,
            -2 * n.Z * n.X, -2 * n.Z * n.Y, 1 - 2 * n.Z * n.Z, t * n.Z,
            0, 0, 0, 1));
    }

    /// <summary>Mirror across the plane through the origin with <paramref name="normal"/>.</summary>
    public Shape Mirror(in Vector3d normal) => Mirror(Vector3d.Zero, normal);

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

    // ---- Planar views ----

    /// <summary>
    /// The cross-section through <paramref name="plane"/> as 2D regions in the plane's own
    /// coordinates — OpenSCAD's <c>projection(cut = true)</c>, and the drawing-view section.
    /// Cavities become holes automatically.
    ///
    /// <para>Exact geometry is used when the shape lowers to B-Rep (fidelity set by
    /// <paramref name="chordTolerance"/> alone, so a bore rim is as smooth as asked for);
    /// otherwise the section is taken from the display mesh at
    /// <paramref name="quality"/>. Move the plane off any flush face or in-plane edge — a
    /// section that runs along a face is an area, not a curve, and is refused.</para>
    /// </summary>
    public IReadOnlyList<Region2d> Section(
        SketchPlane plane,
        double chordTolerance = PlanarSection.DefaultChordTolerance,
        MeshQuality? quality = null) =>
        CanConvertTo(TargetRep.Brep)
            ? PlanarSection.OfSolid(ToBrep(), plane.Frame, chordTolerance)
            : PlanarSection.OfMesh(ToMesh(quality), plane.Frame);

    /// <summary>
    /// The OUTLINE the shape casts along <paramref name="plane"/>'s normal, as 2D regions
    /// in the plane's coordinates — OpenSCAD's <c>projection(cut = false)</c>. A through
    /// hole survives as a hole; a blind pocket or an internal cavity does not (there is
    /// material in front of it).
    ///
    /// <para>Computed from the mesh at <paramref name="quality"/> — a silhouette is the
    /// union of the projected faces, so its fidelity is the mesh's, and a finer mesh costs
    /// more union work. See <see cref="PlanarSection.SilhouetteOfMesh"/> for the cost.</para>
    /// </summary>
    public IReadOnlyList<Region2d> Silhouette(SketchPlane plane, MeshQuality? quality = null) =>
        PlanarSection.SilhouetteOfMesh(ToMesh(quality), plane.Frame);

    private void ThrowIfImpossible(TargetRep target)
    {
        var report = Explain(target);
        if (!report.IsConvertible)
            throw new ShapeConversionException(report);
    }

    internal abstract string Describe();
}
