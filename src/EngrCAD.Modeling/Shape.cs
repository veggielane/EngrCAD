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

    /// <summary>
    /// Opt-in adaptive tessellation: when set, B-Rep tessellation (and the feature-edge
    /// overlay) resolve segment counts from the model's own curvature radii through
    /// this criterion instead of the fixed <see cref="SegmentsPerCircle"/>/
    /// <see cref="CurveSamples"/> — see <see cref="TessellationQuality"/>. Null (the
    /// default) keeps the fixed counts exactly.
    /// </summary>
    public TessellationQuality? Tessellation { get; set; }

    /// <summary>
    /// How the implicit lowering's polygonizer places its vertices and how coarse it may
    /// be — sharp-feature placement, its feature angle, and the opt-in adaptive tolerance.
    /// Null (the default) uses <see cref="SurfaceNetsOptions.Default"/>, which has sharp
    /// features ON: a polygonized box is a box.
    /// </summary>
    public SurfaceNetsOptions? SurfaceNets { get; set; }

    /// <summary>The (segmentsPerCircle, curveSamples) a B-Rep tessellation of
    /// <paramref name="solid"/> should use under this quality — the fixed counts, or
    /// the adaptive resolution when <see cref="Tessellation"/> is set.</summary>
    internal (int SegmentsPerCircle, int CurveSamples) ResolveSegments(BrepSolid solid) =>
        Tessellation?.ResolveFor(solid) ?? (SegmentsPerCircle, CurveSamples);

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
    /// Extrudes a sketch with a twist and/or taper — OpenSCAD's
    /// <c>linear_extrude(twist, scale, slices)</c>, uniform-scale form. See
    /// <see cref="Extrude(Sketch, double, double, Vector2d, SketchPlane?, int?)"/>.
    /// </summary>
    public static Shape Extrude(
        Sketch sketch, double height, double twist, double scale = 1,
        SketchPlane? plane = null, int? slices = null) =>
        Extrude(sketch, height, twist, new Vector2d(scale, scale), plane, slices);

    /// <summary>
    /// Extrudes a sketch with a twist and/or per-axis taper — OpenSCAD's
    /// <c>linear_extrude(twist, scale, slices)</c>. The cross-section at height
    /// fraction <c>t</c> is the sketch scaled by <c>lerp(1, scale, t)</c> per axis
    /// about the plane origin, then rotated by <c>twist·t</c> about the plane normal
    /// (radians, counter-clockwise / right-handed — OpenSCAD's <c>twist</c> is the
    /// opposite sign).
    /// <para>Representation support: a pure taper (<paramref name="twist"/> = 0) is
    /// <b>B-Rep-Native</b> — every straight side sweeps an exact plane through the
    /// scaling centre, so the solid is a ruled loft between the base and the scaled
    /// top. A nonzero twist has no analytic side surface in the kernel and is
    /// B-Rep-Impossible; the mesh lowering is a direct section sweep
    /// (<paramref name="slices"/> rings, derived from the twist and the mesh quality
    /// when null), and the implicit lowering wraps that mesh in a mesh SDF.
    /// <c>Explain(target)</c> reports each case.</para>
    /// </summary>
    /// <param name="sketch">The profile; holes are carried through the sweep, and a
    /// pure taper of a holed sketch is B-Rep-Native too (the hole lofts as its own
    /// inner skin about the same scaling centre).</param>
    /// <param name="height">Extrusion height along the plane normal (&gt; 0).</param>
    /// <param name="twist">Total twist over the height, radians.</param>
    /// <param name="scale">Per-axis scale of the top section (components &gt; 0; use
    /// <see cref="Cone"/> or <see cref="Loft(IReadOnlyList{Profile}, LoftStyle)"/> for
    /// apex-degenerate tops).</param>
    /// <param name="plane">Sketch placement (default world XY).</param>
    /// <param name="slices">Section rings for the twisted mesh sweep; null sizes them
    /// from the twist angle and the quality's segments-per-circle.</param>
    public static Shape Extrude(
        Sketch sketch, double height, double twist, Vector2d scale,
        SketchPlane? plane = null, int? slices = null)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(twist))
            throw new ArgumentOutOfRangeException(nameof(twist));
        if (!(scale.X > 0) || !(scale.Y > 0))
            throw new ArgumentOutOfRangeException(nameof(scale),
                "Top-section scale components must be positive; a zero scale degenerates the top to a point (use Cone or a Loft).");
        if (slices is < 1)
            throw new ArgumentOutOfRangeException(nameof(slices));

        // Exact-zero semantic test (not a tolerance): a literal no-op parameterization
        // IS a plain extrusion, and gets the plain node's exactness everywhere.
        if (twist == 0 && scale.X == 1 && scale.Y == 1)
            return Extrude(sketch, height, plane);
        return new TwistExtrudeShape(sketch, plane ?? SketchPlane.XY, height, twist, scale, slices);
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

    // ---- Loft (skin through sections) ----

    /// <summary>
    /// Skins a solid through two or more planar cross-sections — OCCT's
    /// <c>BRepOffsetAPI_ThruSections</c>, via <see cref="SolidFactory.Loft"/>. Sections
    /// correspond by segment index; matching counts loft directly, an INTEGER-ratio
    /// count splits the coarser section's segments into equal-parameter pieces at
    /// lowering (no geometry moves — a square lofting to an octagon splits each side
    /// once), and a non-integer ratio fails at the call; winding
    /// and starting segment are aligned automatically to the least-twist match, and the
    /// first and last sections are capped, so the result is always a closed solid.
    /// <para>Representation support: <b>B-Rep-Native</b> under any similarity, MIRRORED
    /// placements included — the skin interpolates the placed sections exactly, and the
    /// accumulated transform bakes into the section curves. (The chord-length
    /// parameterization and least-twist alignment are METRIC, which is exactly why a
    /// shear is refused and a reflection is not: an isometry preserves every length and
    /// angle those two rules read.) Implicit lowering bridges
    /// through the tessellation (the loft blend is defined on the B-Rep surface, not as
    /// a field), and mesh comes from the exact B-Rep. A sheared placement is
    /// B-Rep-Impossible: the loft's chord-length parameterization and least-twist
    /// alignment are metric, so they do not commute with a shear.</para>
    /// </summary>
    /// <param name="sections">Two or more planar profiles, in loft order.</param>
    /// <param name="style"><see cref="LoftStyle.Smooth"/> (one skin interpolating all
    /// sections) or <see cref="LoftStyle.Ruled"/> (straight strips between consecutive
    /// sections, each junction a real edge).</param>
    public static Shape Loft(IReadOnlyList<Profile> sections, LoftStyle style = LoftStyle.Smooth)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ValidateLoftSections(sections);
        return new LoftShape([.. sections], style);
    }

    /// <summary>
    /// Lofts through sketches, each placed by its own <see cref="SketchPlane"/> — the
    /// sketch-first spelling of <see cref="Loft(IReadOnlyList{Profile}, LoftStyle)"/>,
    /// matching the <see cref="Extrude(Sketch, double, SketchPlane?)"/> vocabulary.
    /// Sketches may carry HOLES: hole j of every section lofts into its own inner skin
    /// (holes correspond by their <c>WithHole</c> declaration order, so every section
    /// must declare the same number), and the caps become faces with hole loops.
    /// </summary>
    public static Shape Loft(
        IReadOnlyList<(Sketch Sketch, SketchPlane Plane)> sections, LoftStyle style = LoftStyle.Smooth)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var profiles = new Profile[sections.Count];
        var holes = new IReadOnlyList<Profile>[sections.Count];
        int holeCount = 0;
        bool anyHoles = false;
        for (int i = 0; i < sections.Count; i++)
        {
            var (sketch, plane) = sections[i];
            ArgumentNullException.ThrowIfNull(sketch, nameof(sections));
            (profiles[i], holes[i]) = PlaceSketchProfiles(sketch, plane);
            if (i == 0)
                holeCount = holes[i].Count;
            else if (holes[i].Count != holeCount)
                throw new ArgumentException(
                    $"Loft section 0 has {holeCount} holes but section {i} has {holes[i].Count}; " +
                    "every section must declare the same holes in the same order (a hole " +
                    "appearing or vanishing mid-loft has no skin to loft).", nameof(sections));
            anyHoles |= holes[i].Count > 0;
        }
        ValidateLoftSections(profiles);
        return new LoftShape(profiles, style, anyHoles ? holes : null);
    }

    /// <summary>
    /// A loft whose sections are <b>generated along a spine</b> by an evolution law —
    /// OCCT's pipe shell with a law: <paramref name="section"/> is carried along
    /// <paramref name="spine"/> in rotation-minimizing frames (the same frames
    /// <see cref="Sweep(Sketch, Curve3d, SketchPlane?)"/> uses), scaled by
    /// <paramref name="scale"/>(s) and rotated in-plane by <paramref name="twist"/>(s)
    /// radians, where s runs 0 → 1 along the spine. The generated sections feed
    /// <see cref="Loft(IReadOnlyList{Profile}, LoftStyle)"/> unchanged, so everything
    /// said there (compatibility, representation support) applies verbatim.
    /// <para>Without laws, prefer <see cref="Sweep(Sketch, Curve3d, SketchPlane?)"/> —
    /// its swept surface is exact along the whole path, where a loft interpolates
    /// <paramref name="sectionCount"/> stations and blends between them. The law is what
    /// this operation exists for. The start frame's x axis is the spine start tangent's
    /// arbitrary perpendicular (the codebase's single such convention); the twist law
    /// rotates from there.</para>
    /// </summary>
    /// <param name="section">The 2D cross-section, in its own sketch coordinates
    /// (origin rides ON the spine).</param>
    /// <param name="spine">The path the sections are stationed along (open curves).</param>
    /// <param name="sectionCount">How many stations to generate (≥ 2). More stations
    /// follow the spine and the law more closely.</param>
    /// <param name="scale">Uniform in-plane scale at s ∈ [0, 1]; null = 1 everywhere.
    /// Must stay positive.</param>
    /// <param name="twist">In-plane rotation in radians at s ∈ [0, 1]; null = none.</param>
    /// <param name="style">Loft style for the generated sections.</param>
    public static Shape LoftAlong(
        Sketch section, Curve3d spine, int sectionCount = 8,
        Func<double, double>? scale = null, Func<double, double>? twist = null,
        LoftStyle style = LoftStyle.Smooth)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(spine);
        if (sectionCount < 2)
            throw new ArgumentOutOfRangeException(nameof(sectionCount), "A loft needs at least 2 sections.");
        if (spine.IsClosed)
            throw new NotSupportedException(
                "LoftAlong needs an open spine: a periodic loft closing back on its first section " +
                "is not supported yet.");

        var (outer, sectionHoles) = section.ToProfiles();
        // The rotation-minimizing frames come from the sweep machinery itself (only the
        // path and the start x seed the frames — the generator argument is irrelevant to
        // them), so a law-free LoftAlong stations its sections on the same frames a
        // sweep would use.
        var startTangent = spine.TangentAt(spine.Domain.Start);
        var swept = new SweptSurface(
            outer.Segments[0], spine, startTangent.ArbitraryPerpendicular(Tolerance.Default));

        var profiles = new Profile[sectionCount];
        IReadOnlyList<Profile>[]? holesPerStation = null;
        for (int k = 0; k < sectionCount; k++)
        {
            double s = (double)k / (sectionCount - 1);
            double factor = scale?.Invoke(s) ?? 1;
            if (!(factor > 0))
                throw new ArgumentOutOfRangeException(nameof(scale),
                    $"The scale law must stay positive; it returned {factor:g4} at s = {s:g4}.");
            var frame = swept.Frame(spine.Domain.ParameterAt(s));
            var placement = frame.ToMatrix();
            if (twist is not null)
                placement *= Matrix4d.CreateRotationZ(twist(s));
            if (factor != 1)
                placement *= Matrix4d.CreateScale(factor);
            profiles[k] = new Profile(
                [.. outer.Segments.Select(c => (Curve3d)new TransformedCurve(c, placement))]);
            if (sectionHoles is { Count: > 0 })
            {
                var stationHoles = new Profile[sectionHoles.Count];
                for (int j = 0; j < sectionHoles.Count; j++)
                    stationHoles[j] = new Profile(
                        [.. sectionHoles[j].Segments.Select(c => (Curve3d)new TransformedCurve(c, placement))]);
                (holesPerStation ??= new IReadOnlyList<Profile>[sectionCount])[k] = stationHoles;
            }
        }
        return new LoftShape(profiles, style, holesPerStation);
    }

    /// <summary>Places a sketch's outer profile AND its holes on a plane.</summary>
    private static (Profile Outer, IReadOnlyList<Profile> Holes) PlaceSketchProfiles(
        Sketch sketch, SketchPlane plane)
    {
        var (outer, holes) = sketch.ToProfiles();
        IReadOnlyList<Profile> placedHoles = holes ?? [];
        var matrix = plane.ToMatrix();
        if (matrix.Equals(Matrix4d.Identity))
            return (outer, placedHoles);
        static Profile Place(Profile profile, Matrix4d matrix) =>
            new([.. profile.Segments.Select(c => (Curve3d)new TransformedCurve(c, matrix))]);
        return (Place(outer, matrix), [.. placedHoles.Select(h => Place(h, matrix))]);
    }

    /// <summary>
    /// The structural checks <see cref="SolidFactory.Loft"/> would make at lowering,
    /// run at construction so a bad section list fails at the call that built it.
    /// </summary>
    private static void ValidateLoftSections(IReadOnlyList<Profile> sections)
    {
        if (sections.Count < 2)
            throw new ArgumentException("A loft needs at least 2 sections.", nameof(sections));
        foreach (var section in sections)
        {
            if (section is null)
                throw new ArgumentException("Loft sections must not be null.", nameof(sections));
        }
        bool singleClosed = sections[0].IsSingleClosedCurve;
        int largest = sections[0].Segments.Count;
        for (int k = 1; k < sections.Count; k++)
        {
            if (sections[k].IsSingleClosedCurve != singleClosed)
                throw new ArgumentException(
                    "Loft sections must be all single closed curves or all segment chains; " +
                    $"section 0 is {(singleClosed ? "a closed curve" : "a chain")} but section {k} is not.",
                    nameof(sections));
            largest = Math.Max(largest, sections[k].Segments.Count);
        }
        for (int k = 0; k < sections.Count; k++)
        {
            // Integer-ratio counts split at lowering (the coarser section's segments
            // become equal-parameter CurveSegment pieces); a non-integer ratio has no
            // canonical correspondence and fails here, at the call that built it.
            if (largest % sections[k].Segments.Count != 0)
                throw new ArgumentException(
                    $"Loft sections have segment counts {sections[k].Segments.Count} (section {k}) " +
                    $"and {largest} whose ratio is not an integer, so there is no canonical " +
                    "correspondence to split into. Rebuild the sections with matching or " +
                    "integer-ratio segment counts (sections match by segment index).", nameof(sections));
        }
    }

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
            this, result, hole, points, depth, placementPlane.ToMatrix(), surfaceDiameter,
            hole.ToolSilhouette(depth));
    }

    /// <summary>Drills one <paramref name="hole"/> at each location of
    /// <paramref name="locations"/> — the <see cref="LocationSet"/> spelling of
    /// <see cref="Drill(HoleSpec, IReadOnlyList{Vector2d}, double, SketchPlane?)"/>.
    /// A hole tool is axisymmetric, so location rotations are ignored.</summary>
    public Shape Drill(HoleSpec hole, LocationSet locations, double depth, SketchPlane? plane = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        return Drill(hole, locations.Points, depth, plane);
    }

    // ---- Extrude/cut UNTIL a face of the body ----

    /// <summary>
    /// Adds a boss: extrudes <paramref name="sketch"/> from <paramref name="plane"/>
    /// (default world XY) along −normal — toward the body, the <see cref="Drill"/>
    /// convention — until it reaches this body, and unions it on. The
    /// build123d/CadQuery <c>extrude(until=NEXT/LAST)</c> convenience.
    ///
    /// <para><b>How the stop is found (and when it refuses).</b> Probe rays from the
    /// profile's interior are cast against this shape's mesh at
    /// <paramref name="quality"/>; the stop must be ONE plane perpendicular to the
    /// extrusion (hit distances clustering within 1e-6 of the body's extent — planar
    /// stop faces tessellate exactly, so this is loose enough for meshing and far
    /// tighter than any genuine curve). Anything else refuses loudly naming the
    /// candidates: a curved or slanted stop face (the hit clusters and their ray
    /// counts), a profile overhanging the body (how many rays missed), tangent grazes
    /// (the ray that saw them). A flat extrusion cannot honestly "conform" to a curved
    /// stop, so it does not guess.</para>
    ///
    /// <para><b>Resolution is eager</b> — the distance is measured at THIS call against
    /// this shape (the <see cref="Bounds"/>/<see cref="Resized"/> policy); wrap the call
    /// in a <see cref="Feature"/> for it to re-measure per regeneration. With
    /// <see cref="Until.Next"/> the boss overshoots INTO the body by half the thinnest
    /// wall (capped at 2%), so the union never sees coplanar faces; with
    /// <see cref="Until.Last"/> the boss ends exactly FLUSH with the far face — if the
    /// body has material beside the boss there, that union is a coplanar boolean, which
    /// the B-Rep lowering may refuse (mesh and implicit handle it).</para>
    /// </summary>
    public Shape ExtrudeUntil(Sketch sketch, SketchPlane? plane, Until until, MeshQuality? quality = null)
    {
        var placement = plane ?? SketchPlane.XY;
        var resolution = UntilResolver.Resolve(this, sketch, placement, until, cut: false, quality);
        return this | ExtrudeBelow(sketch, placement, resolution.Height);
    }

    /// <summary>
    /// Cuts with the profile until a face of the body: <see cref="Until.Next"/> punches
    /// through the FIRST wall and stops in the void behind it (half the gap, capped at
    /// 2% — never coplanar with the wall it exits); <see cref="Until.Last"/> cuts
    /// through everything, overshooting the far face by 2% (the <see cref="Drill"/>
    /// rule). Stop-plane resolution, honesty and eagerness are exactly as on
    /// <see cref="ExtrudeUntil"/>.
    /// </summary>
    public Shape CutUntil(Sketch sketch, SketchPlane? plane, Until until, MeshQuality? quality = null)
    {
        var placement = plane ?? SketchPlane.XY;
        var resolution = UntilResolver.Resolve(this, sketch, placement, until, cut: true, quality);
        // The tool also clears the top (the Drill overshoot rule): with the sketch
        // plane ON a body face, a tool starting exactly at the plane would leave its
        // top face coplanar with the body's.
        var raised = SketchPlane.At(
            placement.Origin + placement.Normal * resolution.TopClearance,
            placement.XAxis, placement.YAxis);
        return this - ExtrudeBelow(sketch, raised, resolution.TopClearance + resolution.Height);
    }

    /// <summary>An extrusion of <paramref name="sketch"/> spanning from
    /// <paramref name="plane"/> to <paramref name="depth"/> below it: the plane is
    /// shifted down and the extrusion runs back up (+normal), so the sketch's own 2D
    /// coordinates are untouched (flipping the plane would mirror them).</summary>
    private static Shape ExtrudeBelow(Sketch sketch, SketchPlane plane, double depth)
    {
        var shifted = SketchPlane.At(
            plane.Origin - plane.Normal * depth, plane.XAxis, plane.YAxis);
        return Extrude(sketch, depth, shifted);
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
    /// cones on both ends down to the minor diameter;
    /// <paramref name="chamferLength"/> overrides that depth (in millimetres of axial —
    /// and, at 45°, radial — cut) when a shallower lead-in is wanted.
    /// <para>Representation support: implicit-Native (<see cref="Sdf.Thread"/>, exact
    /// sign). B-Rep-<b>Native</b> for the unmodified basic profile — zero
    /// <paramref name="clearance"/> — as a boolean-free helical sweep
    /// (<see cref="SolidFactory.MakeThreadedRod"/>: one exact
    /// <see cref="HelicalSurface"/> band per profile facet sharing <see cref="Helix3d"/>
    /// rails, spiral-bounded flat caps; not STEP-exportable), and Native with a
    /// <b>SUB-DEPTH</b> chamfer as well: a coaxial cone cuts each helical band in an
    /// exact conical <see cref="SpiralArc3d"/>, so the chamfer is one ordinary
    /// difference against <see cref="SolidFactory.MakeThreadEndChamferTool"/>.
    /// A chamfer at or past <see cref="ThreadSpec.ThreadDepth"/> — which the
    /// <paramref name="chamferEnds"/> default asks for — puts the cone's base exactly on
    /// the minor diameter and therefore TANGENT to every root band along the end plane,
    /// which is coincident curved-surface boolean input and stays Impossible. Clearance
    /// stays Impossible too (a distance-field offset rounds reflex corners into arcs with
    /// no exact B-Rep counterpart); meshes then bridge through Surface Nets — the
    /// printing route.</para>
    /// <para><paramref name="runoutLength"/> models the incomplete thread a die or a
    /// rolling head leaves where the thread meets its shank: over that axial length at the
    /// <b>z = 0</b> end the crests are truncated by a coaxial cone running from the major
    /// diameter down to the <see cref="ThreadSpec.PitchDiameter"/>, so the thread washes
    /// out instead of ending in a full-form crest. It REPLACES that end's lead-in chamfer
    /// (a stud has a lead-in at its free end and a runout at its shank end, not both at
    /// once) and is <b>Native in both representations</b>: the cone is the general member
    /// of the family a 45° chamfer belongs to, so its cut on every helical band is the
    /// same exact conical <see cref="SpiralArc3d"/>. Ending on the PITCH diameter rather
    /// than the minor is what keeps it exact — a cone reaching the minor diameter is
    /// tangent to every root band along the end plane, which is the coincident
    /// curved-surface input the boolean refuses.</para>
    /// </summary>
    public static Shape ExternalThread(
        ThreadSpec spec, double length, double clearance = 0, bool chamferEnds = true,
        double? chamferLength = null, double runoutLength = 0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (chamferLength is { } explicitChamfer && (!(explicitChamfer >= 0) || explicitChamfer >= spec.MajorDiameter / 2))
            throw new ArgumentOutOfRangeException(nameof(chamferLength),
                "A chamfer must be non-negative and shallower than the major radius.");
        if (!(runoutLength >= 0) || !double.IsFinite(runoutLength))
            throw new ArgumentOutOfRangeException(nameof(runoutLength),
                "A runout length must be non-negative and finite.");
        if (runoutLength >= length)
            throw new ArgumentOutOfRangeException(nameof(runoutLength),
                $"The runout ({runoutLength}) must be shorter than the thread ({length}).");
        ValidateThreadClearance(clearance, spec);
        return new ThreadShape(
            spec, length, -clearance, chamferLength ?? (chamferEnds ? spec.ThreadDepth : 0),
            runoutLength);
    }

    /// <summary>Coarse-metric convenience overload:
    /// <c>ExternalThread(8, …)</c> is an M8×1.25 stud (see <see cref="StandardThreads.Metric"/>).</summary>
    public static Shape ExternalThread(
        double size, double length, double clearance = 0, bool chamferEnds = true,
        double? chamferLength = null, double runoutLength = 0) =>
        ExternalThread(
            StandardThreads.Metric(size), length, clearance, chamferEnds, chamferLength, runoutLength);

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
    /// <c>ConvexEdges</c>). Complete planar face rims resolve to rim surgery; a
    /// selection stopping PART-WAY along a rim resolves to contiguous runs blended with
    /// exact SETBACK terminations at each open end — see
    /// <see cref="Filleting.FilletEdges(BrepSolid, IEnumerable{BrepEdge}, double)"/>.
    /// The whole selection is grouped and validated before any surgery runs.
    /// </summary>
    public Shape FilletEdges(double radius, Func<BrepSolid, IEnumerable<BrepEdge>> edges)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return new RimShape(this, fillet: true, radius, radius,
            solid => Filleting.RimFacesFor(solid, edges(solid)), edgeSelector: edges);
    }

    /// <summary>Chamfers selected EDGES; see <see cref="FilletEdges"/>.</summary>
    public Shape ChamferEdges(double setback, Func<BrepSolid, IEnumerable<BrepEdge>> edges) =>
        ChamferEdges(setback, setback, edges);

    // ---- the same operations, addressed through the typed selection vocabulary ----
    //
    // A design OUTSIDE a feature history had to hand-write a Func over the lowered
    // solid, while a Feature declared a FaceSetRef and got cardinality, a readable
    // Subject in every failure, and a serializable descriptor. That was a difference in
    // spelling, not in kind: these overloads bridge them through AsSelector, so
    //   Shape.Box(...).Fillet(2, FaceSetRef.PlanarWithNormal(Vector3d.UnitZ))
    // is the same call the feature makes. The input name passed down is the parameter's
    // own, so a failed query reads "faces: expected at least one planar face ...".

    /// <inheritdoc cref="Chamfer(double, Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape Chamfer(double setback, FaceSetRef faces) =>
        Chamfer(setback, Selector(faces, nameof(faces)));

    /// <inheritdoc cref="Chamfer(double, double, Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape Chamfer(double topSetback, double sideSetback, FaceSetRef faces) =>
        Chamfer(topSetback, sideSetback, Selector(faces, nameof(faces)));

    /// <inheritdoc cref="Fillet(double, Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape Fillet(double radius, FaceSetRef faces) =>
        Fillet(radius, Selector(faces, nameof(faces)));

    /// <inheritdoc cref="ChamferAtAngle(double, double, Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape ChamferAtAngle(double setback, double angleDegrees, FaceSetRef faces) =>
        ChamferAtAngle(setback, angleDegrees, Selector(faces, nameof(faces)));

    /// <inheritdoc cref="Fillet(Func{Vector3d, double}, Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape Fillet(Func<Vector3d, double> radiusAt, FaceSetRef faces) =>
        Fillet(radiusAt, Selector(faces, nameof(faces)));

    /// <inheritdoc cref="Chamfer(Func{Vector3d, double}, Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape Chamfer(Func<Vector3d, double> setbackAt, FaceSetRef faces) =>
        Chamfer(setbackAt, Selector(faces, nameof(faces)));

    /// <inheritdoc cref="FilletEdges(double, Func{BrepSolid, IEnumerable{BrepEdge}})"/>
    public Shape FilletEdges(double radius, EdgeSetRef edges) =>
        FilletEdges(radius, Selector(edges, nameof(edges)));

    /// <inheritdoc cref="ChamferEdges(double, Func{BrepSolid, IEnumerable{BrepEdge}})"/>
    public Shape ChamferEdges(double setback, EdgeSetRef edges) =>
        ChamferEdges(setback, Selector(edges, nameof(edges)));

    /// <inheritdoc cref="ChamferEdges(double, double, Func{BrepSolid, IEnumerable{BrepEdge}})"/>
    public Shape ChamferEdges(double topSetback, double sideSetback, EdgeSetRef edges) =>
        ChamferEdges(topSetback, sideSetback, Selector(edges, nameof(edges)));

    // Shell deliberately gets NO FaceSetRef overload. Its existing openings parameter is
    // a NULLABLE Func (no openings = a sealed void), so a second reference-typed overload
    // would make the existing, correct call `Shell(t, null)` ambiguous at every site — a
    // source break to save one `.AsSelector(...)`. Write
    // `Shell(t, openings.AsSelector("openings"))` where the typed vocabulary is wanted.

    // ---- direct editing (a body with no construction history) ----

    /// <summary>
    /// Pushes the selected faces along their own outward normals by
    /// <paramref name="distance"/> (positive grows the solid) — <see cref="DirectEdit.OffsetFaces"/>.
    ///
    /// <para>This is the edit an IMPORTED body takes: a solid read from STEP has no parameter
    /// to change, so the only handle on it is its faces. On a shape that DOES have a history,
    /// changing the construction is better than editing its result.</para>
    ///
    /// <para>Where every face adjoining the moved one is parallel to its normal — a box's top
    /// against its four sides — the volume changes by exactly area × distance; where a
    /// neighbour is oblique the boundary slides as well and the change is the frustum
    /// integral.</para>
    ///
    /// <para>Representation support: <b>B-Rep-Native</b> under any similarity, MIRRORED
    /// placements included (an offset by a distance is defined by distance alone, and every
    /// isometry preserves distance); the distance scales with a uniform factor. Implicit
    /// bridges through the tessellation; mesh comes from the exact B-Rep.</para>
    /// </summary>
    public Shape OffsetFaces(double distance, Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        if (!double.IsFinite(distance))
            throw new ArgumentOutOfRangeException(nameof(distance), "The offset distance must be finite.");
        return new DirectEditShape(this, DirectEditKind.Offset, new Vector3d(distance, 0, 0), faces);
    }

    /// <summary>
    /// Translates the selected PLANAR faces by <paramref name="translation"/> —
    /// <see cref="DirectEdit.MoveFaces"/>.
    ///
    /// <para>A plane is invariant under translation within itself, so this IS
    /// <see cref="OffsetFaces"/> by the projected distance <c>v·n̂</c>, per face. Two
    /// consequences follow rather than being arranged: moving a face parallel to itself does
    /// nothing at all, and moving several faces by one vector moves each by its own amount. A
    /// curved face is refused by name at lowering (a translation moves its axis, which no
    /// offset can do).</para>
    ///
    /// <para>Representation support: <b>B-Rep-Native</b> under any similarity, MIRRORED
    /// placements included — the translation takes its LINEAR IMAGE, and because a reflection
    /// preserves dot products the projected distance survives it. Implicit bridges through
    /// the tessellation; mesh comes from the exact B-Rep.</para>
    /// </summary>
    public Shape MoveFaces(in Vector3d translation, Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        return new DirectEditShape(this, DirectEditKind.Move, translation, faces);
    }

    /// <summary>
    /// Removes the selected faces and heals the wound by dropping the boundary they left in
    /// their planar neighbours — <see cref="DirectEdit.DeleteFaces"/>. This is how a boss, a
    /// pad or a pocket comes off an imported body.
    ///
    /// <para>A wound that only PARTLY bounds a neighbouring loop needs those neighbours
    /// EXTENDED until they meet, which is a different operation that can have no answer at
    /// all; it is refused by name at lowering rather than attempted.</para>
    ///
    /// <para>Representation support: <b>B-Rep-Native</b> under any similarity (the operation
    /// is purely topological, so nothing has to commute with it). Implicit bridges through
    /// the tessellation; mesh comes from the exact B-Rep.</para>
    /// </summary>
    public Shape DeleteFaces(Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        return new DirectEditShape(this, DirectEditKind.Delete, Vector3d.Zero, faces);
    }

    /// <inheritdoc cref="OffsetFaces(double, Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape OffsetFaces(double distance, FaceSetRef faces) =>
        OffsetFaces(distance, Selector(faces, nameof(faces)));

    /// <inheritdoc cref="MoveFaces(in Vector3d, Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape MoveFaces(in Vector3d translation, FaceSetRef faces) =>
        MoveFaces(translation, Selector(faces, nameof(faces)));

    /// <inheritdoc cref="DeleteFaces(Func{BrepSolid, IEnumerable{BrepFace}})"/>
    public Shape DeleteFaces(FaceSetRef faces) => DeleteFaces(Selector(faces, nameof(faces)));

    private static Func<BrepSolid, IEnumerable<BrepFace>> Selector(FaceSetRef faces, string name)
    {
        ArgumentNullException.ThrowIfNull(faces);
        return faces.AsSelector(name);
    }

    private static Func<BrepSolid, IEnumerable<BrepEdge>> Selector(EdgeSetRef edges, string name)
    {
        ArgumentNullException.ThrowIfNull(edges);
        return edges.AsSelector(name);
    }

    /// <inheritdoc cref="ChamferEdges(double, Func{BrepSolid, IEnumerable{BrepEdge}})"/>
    public Shape ChamferEdges(
        double topSetback, double sideSetback, Func<BrepSolid, IEnumerable<BrepEdge>> edges)
    {
        if (topSetback <= 0 || sideSetback <= 0)
            throw new ArgumentOutOfRangeException(nameof(topSetback));
        return new RimShape(this, fillet: false, topSetback, sideSetback,
            solid => Filleting.RimFacesFor(solid, edges(solid)), edgeSelector: edges);
    }

    /// <summary>
    /// VARIABLE-setback chamfer: <paramref name="setbackAt"/> is evaluated at each rim
    /// corner of the LOWERED solid (transforms already baked, so the law reads final
    /// coordinates and its result is used verbatim) and interpolates linearly along each
    /// edge. Strips stay exact planes and sharp corners keep exact miters; arc rim edges
    /// need the law constant along the arc, and a full circular rim needs it constant
    /// everywhere — a circle offset by a varying amount is a spiral, which has no exact
    /// B-Rep form. See <see cref="Filleting.ChamferRim(BrepSolid, BrepFace, Func{Vector3d, double})"/>.
    /// </summary>
    public Shape Chamfer(Func<Vector3d, double> setbackAt, Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        ArgumentNullException.ThrowIfNull(setbackAt);
        return new RimShape(this, fillet: false, 0, 0, faces, setbackAt, lawAngleDegrees: null);
    }

    /// <summary>Variable-setback chamfer at a constant angle from the face (the constant
    /// angle is what keeps every strip planar); see <see cref="Chamfer(Func{Vector3d, double}, Func{BrepSolid, IEnumerable{BrepFace}})"/>.</summary>
    public Shape ChamferAtAngle(
        Func<Vector3d, double> setbackAt, double angleDegrees, Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        ArgumentNullException.ThrowIfNull(setbackAt);
        if (angleDegrees <= 0 || angleDegrees >= 90)
            throw new ArgumentOutOfRangeException(nameof(angleDegrees),
                "The chamfer angle is measured from the chamfered face and must lie strictly between 0° and 90°.");
        return new RimShape(this, fillet: false, 0, 0, faces, setbackAt, angleDegrees);
    }

    /// <summary>Variable-setback chamfer of selected EDGES; the selection resolves to
    /// complete rims exactly as <see cref="ChamferEdges(double, Func{BrepSolid, IEnumerable{BrepEdge}})"/>.</summary>
    public Shape ChamferEdges(
        Func<Vector3d, double> setbackAt, Func<BrepSolid, IEnumerable<BrepEdge>> edges)
    {
        ArgumentNullException.ThrowIfNull(setbackAt);
        return new RimShape(this, fillet: false, 0, 0,
            solid => Filleting.RimFacesFor(solid, edges(solid)), setbackAt, lawAngleDegrees: null);
    }

    /// <summary>
    /// VARIABLE-radius fillet: <paramref name="radiusAt"/> is evaluated at each rim corner of
    /// the LOWERED solid (transforms already baked, so the law reads final coordinates and its
    /// result is used verbatim) and interpolates linearly along each edge — the sibling of
    /// <see cref="Chamfer(Func{Vector3d, double}, Func{BrepSolid, IEnumerable{BrepFace}})"/>.
    ///
    /// <para>The band is exact: along a straight run the cross-section at each station is a
    /// true quarter circle of the interpolated radius, and it stays tangent to both
    /// neighbours. What has no exact form is refused by name — a SHARP corner whose two edges
    /// carry different radii (two variable bands are cones that do not circumscribe a common
    /// sphere, so their intersection is a quartic and there is no conic miter to weld them
    /// on), and a varying law along an ARC (a circle offset by a varying amount is a spiral).
    /// A constant law reproduces the plain radius overload exactly, mesh and all.</para>
    /// </summary>
    public Shape Fillet(Func<Vector3d, double> radiusAt, Func<BrepSolid, IEnumerable<BrepFace>> faces)
    {
        ArgumentNullException.ThrowIfNull(radiusAt);
        return new RimShape(this, fillet: true, 0, 0, faces, radiusAt, lawAngleDegrees: null);
    }

    /// <summary>Variable-radius fillet of selected EDGES; the selection resolves to complete
    /// rims exactly as <see cref="FilletEdges(double, Func{BrepSolid, IEnumerable{BrepEdge}})"/>.</summary>
    public Shape FilletEdges(
        Func<Vector3d, double> radiusAt, Func<BrepSolid, IEnumerable<BrepEdge>> edges)
    {
        ArgumentNullException.ThrowIfNull(radiusAt);
        return new RimShape(this, fillet: true, 0, 0,
            solid => Filleting.RimFacesFor(solid, edges(solid)), radiusAt, lawAngleDegrees: null);
    }

    // ---- Draft (mould-release taper) ----

    /// <summary>
    /// Tapers side faces by <paramref name="angleDegrees"/> about the neutral plane
    /// through <paramref name="neutralOrigin"/> perpendicular to
    /// <paramref name="pullDirection"/> (the mould-opening direction) — OCCT's
    /// <c>BRepOffsetAPI_DraftAngle</c>, via <see cref="Draft.Apply"/>. A positive angle
    /// narrows the solid along the pull direction (the classic release taper), a
    /// negative one widens it; geometry ON the neutral plane does not move, which is
    /// what makes it the parting line.
    /// <para><paramref name="faces"/> selects which side faces to taper (a query over
    /// the lowered B-Rep, the same selector story as <see cref="Chamfer(double, Func{BrepSolid, IEnumerable{BrepFace}})"/>);
    /// null drafts every side face. Per-face angles come from CHAINING drafts — the
    /// operation is exact and composable, so
    /// <c>shape.Draft(2, o, pull, left).Draft(5, o, pull, right)</c> is exact too.</para>
    /// <para>Representation support: <b>B-Rep-Native</b> under any similarity, MIRRORED
    /// placements included — a mirrored draft takes the pull direction's LINEAR IMAGE,
    /// un-negated (a pull direction is transported by the map, not conjugated the way a
    /// revolve's axis is), and the angle survives because every isometry preserves
    /// angles (exact plane rotation about each face's neutral line; the solid must
    /// be a planar-faced prism about the pull direction — anything else is refused by
    /// name at lowering). Implicit bridges through the tessellation; mesh comes from the
    /// exact B-Rep.</para>
    /// </summary>
    public Shape Draft(
        double angleDegrees, in Vector3d neutralOrigin, in Vector3d pullDirection,
        Func<BrepSolid, IEnumerable<BrepFace>>? faces = null)
    {
        if (!double.IsFinite(angleDegrees) || Math.Abs(angleDegrees) >= 90)
            throw new ArgumentOutOfRangeException(nameof(angleDegrees),
                "The draft angle must be less than 90 degrees in magnitude.");
        if (!pullDirection.TryNormalize(Tolerance.Default, out _))
            throw new ArgumentException("The pull direction must be non-zero.", nameof(pullDirection));
        return new DraftShape(this, angleDegrees * Math.PI / 180, neutralOrigin, pullDirection, faces);
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

    /// <summary>
    /// This shape stamped once per location of <paramref name="locations"/> and unioned
    /// (balanced tree, like the other patterns) — the general pattern every
    /// <see cref="LocationSet"/> constructor feeds (grids, bolt circles, hex fields,
    /// composed sets). The shape is interpreted as modeled at <paramref name="plane"/>'s
    /// origin (default world XY): each copy is translated to the location's point and
    /// rotated by its angle about the plane normal, in the plane's own coordinates.
    /// <para>For a shape drawn at the plane origin,
    /// <c>shape.Pattern(LocationSet.Polar(n, r))</c> equals
    /// <c>shape.Translate(r, 0, 0).PatternCircular(n, origin, normal)</c> exactly — the
    /// conjugation algebra is the same; the LocationSet spelling just separates WHERE
    /// from WHAT.</para>
    /// </summary>
    public Shape Pattern(LocationSet locations, SketchPlane? plane = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var placement = plane ?? SketchPlane.XY;
        var copies = new List<Shape>(locations.Count);
        foreach (var location in locations)
            copies.Add(Transform(LocationSet.PoseAt(location, placement)));
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

    /// <summary>
    /// Laplacian fairing (implicit smoothing): each pass solves (M + λL)·x′ = M·x over the
    /// child's tessellation, rounding sharp features and reducing noise. <paramref name="timeStep"/>
    /// is <b>dimensionless and scale-free</b> (λ = timeStep · h̄² for the mean edge length),
    /// so the same value fairs the same amount at any model scale; <paramref name="iterations"/>
    /// rebuilds the operator from the current geometry each pass (honest curvature flow, so
    /// k passes of λ are not one pass of k·λ).
    /// <para>
    /// <b>This is a geometry-changing node, and <see cref="Explain"/> says so.</b> Fairing is
    /// defined on a triangulation, so it is <b>Native for mesh only</b>: to B-Rep it is
    /// Impossible (there is no mesh → B-Rep import, and the result is a tessellation rather than
    /// a surface), and to implicit it is Bridged through a mesh SDF of the faired triangles —
    /// a different field from the child's, since it carries the tessellation's chord error.
    /// </para>
    /// <para>
    /// A <b>closed solid has no boundary to pin</b>, so the whole surface fairs: a cube's corners
    /// round, and any curved shape shrinks a little toward its curvature centres every pass. That
    /// is the operation, not a defect — reach for it at the end of a model, and keep the step
    /// small (the default 1 is one visible fairing pass). A uniform scale above this node changes
    /// nothing (the step is dimensionless); the operation commutes with any rigid placement.
    /// </para>
    /// </summary>
    public Shape Smoothed(double timeStep = 1.0, int iterations = 1,
        LaplacianWeighting weighting = LaplacianWeighting.Cotangent) =>
        Smoothed(new LaplacianSmoothOptions
        {
            TimeStep = timeStep,
            Iterations = iterations,
            Weighting = weighting,
        });

    /// <summary>
    /// Laplacian fairing with full control over the smoother's behaviour (step, passes,
    /// weighting). See <see cref="Smoothed(double, int, LaplacianWeighting)"/> for the support
    /// story and the closed-surface fairing behaviour. <see cref="LaplacianSmoothOptions.FixedVertices"/>
    /// is honoured for advanced use, but its indices name the child's <i>lowered</i> mesh, which
    /// a graph author generally has no handle on.
    /// </summary>
    public Shape Smoothed(LaplacianSmoothOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.TimeStep > 0))
            throw new ArgumentOutOfRangeException(nameof(options), "TimeStep must be positive.");
        if (options.Iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Iterations must be at least 1.");
        return new SmoothedShape(this, options);
    }

    /// <summary>
    /// The volume this shape sweeps over a set of sampled rigid poses — the union of
    /// the posed copies, typically the per-frame instance transforms of a
    /// <see cref="MotionStudy"/> (see <see cref="MotionStudy.SweptVolume"/>). Fidelity
    /// is set by how densely the motion was sampled: the swept volume is exactly the
    /// union of the sampled poses, no more.
    /// <para>Support: implicit-<b>Native</b> (the child's field is lowered once and
    /// placed per pose — what the implicit engine is for); mesh Bridged via Surface
    /// Nets over that field; B-Rep <b>Impossible</b> (a motion envelope is not one of
    /// the kernel's surfaces). Every pose must be rigid (or a uniform similarity);
    /// sheared poses are refused at lowering.</para>
    /// </summary>
    public Shape SweptOver(IReadOnlyList<Matrix4d> poses)
    {
        ArgumentNullException.ThrowIfNull(poses);
        if (poses.Count == 0)
            throw new ArgumentException("A swept volume needs at least one pose.", nameof(poses));
        return new MotionSweepShape(this, [.. poses]);
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

    /// <summary>
    /// Modeled text laid along a curve — the ring of lettering round a dial face, a bezel
    /// or a curved nameplate — extruded <paramref name="height"/> along the normal of
    /// <paramref name="plane"/> and unioned. The <paramref name="path"/> is a
    /// <see cref="Curve2d"/> in that plane's own 2D coordinates, exactly like a
    /// <see cref="Sketch"/>; <c>sketch.ToCurves()</c> hands over an existing outline and
    /// <c>Sketch.Circle(r).ToCurves()[0]</c> is the common case.
    /// <para>Every glyph is placed RIGIDLY — rotated to the path's tangent (or left
    /// UPRIGHT, see <paramref name="upright"/>), never bent to its curvature — and only its
    /// control points are mapped, which is exactly the curve because a Bézier is an affine
    /// combination of them. So text on a path is as exact as straight text: Native in all
    /// three representations, nothing sampled. See
    /// <see cref="TextOutlines.SketchesOnPath"/> for the anchoring, arc-length and
    /// alignment conventions.</para>
    /// </summary>
    /// <example>
    /// <code>
    /// var dial = Shape.Cylinder(radius: 30, height: 4);
    /// var top = SketchPlane.At((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY);
    ///
    /// // CLOCKWISE, so the letters stand OUTWARD and read right-way-up from outside the
    /// // dial (a counter-clockwise ring hangs them inward — see TextOutlines.SketchesOnPath).
    /// var ring = new Arc2d(Vector2d.Zero, 24, Math.PI, -2 * Math.PI);
    /// var style = new TextStyle { Align = TextAlign.Center, VerticalAlign = TextVerticalAlign.Bottom };
    ///
    /// var marks = Shape.TextOnPath("ENGRCAD", font, size: 6, height: 0.8, ring, top, style,
    ///                              startOffset: 24 * Math.PI / 2);   // a quarter turn along
    /// var engraved = dial | marks;
    /// </code>
    /// </example>
    /// <param name="upright">Lay every glyph un-rotated (world +X up) rather than tilted to
    /// the path's tangent — the banner/label case; see
    /// <see cref="TextOutlines.SketchesOnPath"/>.</param>
    /// <exception cref="ArgumentException">The text draws nothing, has more than one line,
    /// contains a character the font lacks, or does not fit on the path.</exception>
    public static Shape TextOnPath(
        string text, TrueTypeFont font, double size, double height, Curve2d path,
        SketchPlane? plane = null, TextStyle? style = null, double startOffset = 0, bool upright = false)
    {
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        var sketches = TextOutlines.SketchesOnPath(text, font, size, path, style, startOffset, upright);
        if (sketches.Count == 0)
            throw new ArgumentException(
                $"\"{text}\" produces no geometry: it is empty or contains only blank glyphs.", nameof(text));

        var glyphs = new List<Shape>(sketches.Count);
        foreach (var sketch in sketches)
            glyphs.Add(Extrude(sketch, height, plane));
        return UnionTree(glyphs);
    }

    // ---- Escape hatches: wrap existing engine geometry as leaves ----

    public static Shape From(BrepSolid solid) => new SourceShape(solid);
    public static Shape From(HalfEdgeMesh mesh) => new SourceShape(mesh);
    public static Shape From(Sdf sdf) => new SourceShape(sdf);

    /// <summary>
    /// Imports a mesh file (.stl, .obj, .off, or .wrl) as a mesh-backed shape — the
    /// user-facing wrapper over <see cref="MeshReader.ReadAndRepair"/>: the file is
    /// read (dirty files weld rather than throw), run through the
    /// <see cref="MeshRepair"/> pipeline (crack welding, degenerate/duplicate removal,
    /// consistent outward orientation, T-junction zipping), and wrapped via
    /// <see cref="From(HalfEdgeMesh)"/> so booleans, transforms and the implicit route
    /// all work on it. Use the <c>out</c>-report overload to see what repair did.
    /// </summary>
    /// <exception cref="NotSupportedException">Unrecognized file extension.</exception>
    /// <exception cref="InvalidOperationException">The file's defects need topological
    /// surgery beyond the repair pipeline.</exception>
    public static Shape From(string path) => From(path, out _);

    /// <summary><see cref="From(string)"/> with the repair report, and opt-in hole
    /// filling: <paramref name="fillHolesAndCracks"/> runs the full
    /// <see cref="MeshRepair.AutoRepair(MeshReadResult, MeshRepairOptions?)"/> dispatch
    /// (pair-wise crack welding + hole filling). Off by default — closing holes invents
    /// geometry, which an importer should only do when asked.</summary>
    public static Shape From(
        string path, out MeshRepairReport report, bool fillHolesAndCracks = false,
        MeshRepairOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var (mesh, repairReport) = MeshReader.ReadAndRepair(path, options, fillHolesAndCracks);
        report = repairReport;
        return From(mesh);
    }

    /// <summary>
    /// Heightmap terrain — OpenSCAD's <c>surface()</c>: the grid becomes a closed
    /// solid (top surface, flat base at <paramref name="baseLevel"/>, perimeter
    /// walls), wrapped as a mesh-backed shape via <see cref="From(HalfEdgeMesh)"/>.
    /// Booleans, transforms and the implicit route work as for any mesh source;
    /// B-Rep is Impossible (meshes cannot be imported). Heights can come from
    /// <see cref="Modeling.Heightmap.ReadPng(string)"/> (grayscale PNG, 0..1 — set
    /// <paramref name="heightScale"/> to the terrain's real peak height) or
    /// <see cref="Modeling.Heightmap.ReadDat(string)"/> (OpenSCAD text matrices).
    /// </summary>
    /// <param name="heights"><c>[row, column]</c> heights, columns along +X and rows
    /// along −Y (image order); at least 2×2, all strictly above the base.</param>
    /// <param name="cellSize">Grid spacing in model units.</param>
    /// <param name="heightScale">Multiplier applied to every height (for normalized
    /// PNG data); default 1.</param>
    /// <param name="baseLevel">The z of the flat bottom face (after scaling).</param>
    /// <param name="centered">Center the footprint on the origin (default).</param>
    public static Shape Heightmap(
        double[,] heights, double cellSize = 1, double heightScale = 1,
        double baseLevel = 0, bool centered = true)
    {
        ArgumentNullException.ThrowIfNull(heights);
        if (heightScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(heightScale));
        double[,] scaled = heights;
        if (heightScale != 1)
        {
            scaled = new double[heights.GetLength(0), heights.GetLength(1)];
            for (int r = 0; r < heights.GetLength(0); r++)
                for (int c = 0; c < heights.GetLength(1); c++)
                    scaled[r, c] = heights[r, c] * heightScale;
        }
        return From(Modeling.Heightmap.Mesh(scaled, cellSize, baseLevel, centered));
    }

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

    /// <summary>
    /// Hollow skin of the surface with the given total wall thickness — the SDF onion
    /// <c>|d| − t/2</c>, centred ON the surface (half the wall inside, half outside).
    /// Implicit-Native and B-Rep-Impossible. For the exact B-Rep hollow that keeps the
    /// OUTER surface and thickens inward — with optional openings — use
    /// <see cref="Shell(double, Func{BrepSolid, IEnumerable{BrepFace}}?)"/>; the two are
    /// different geometry, which is why they are different calls rather than one call
    /// with representation-dependent results.
    /// </summary>
    public Shape Shell(double thickness) => new ShellShape(this, thickness);

    /// <summary>
    /// Hollows the solid to walls of <paramref name="thickness"/> — the exact B-Rep
    /// shelling (OCCT's <c>BRepOffsetAPI_MakeThickSolid</c>, via
    /// <see cref="Shelling.Shell"/>). The OUTER surface is kept exactly and the wall
    /// thickens INWARD; faces selected by <paramref name="openings"/> are removed,
    /// opening the cavity through them (the classic tray), and a null selector leaves
    /// the cavity sealed as a second shell.
    /// <para>Unlike <see cref="Shell(double)"/> — the SDF skin <c>|d| − t/2</c>, centred
    /// on the surface — this overload does not grow the part: the outer boundary stays
    /// put. That difference is representation-independent by design: this call is
    /// B-Rep-Native (any similarity, MIRRORED included — an offset is defined by
    /// DISTANCE alone and every isometry preserves it; the child must lower to a
    /// polyhedral solid — planar faces, straight edges, 3-valent corners — anything else
    /// refused by name at lowering) and bridges implicit/mesh through the exact shelled
    /// B-Rep, so every representation shows the SAME walls.</para>
    /// </summary>
    public Shape Shell(double thickness, Func<BrepSolid, IEnumerable<BrepFace>>? openings)
    {
        if (!(thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(thickness), "Wall thickness must be positive.");
        return new BrepShellShape(this, thickness, openings);
    }

    /// <summary>
    /// Rounds EVERY convex edge and corner of the solid with one radius — the exact
    /// morphological opening (K ⊖ B<sub>r</sub>) ⊕ B<sub>r</sub>
    /// (<see cref="Filleting.FilletAllEdges"/>): each face keeps its own plane with a
    /// shrunk boundary, each edge becomes an exact cylindrical band, each corner an
    /// exact spherical patch (a box becomes 26 faces), boolean-free with nothing to
    /// seal.
    /// <para>Representation support: <b>B-Rep-Native</b> under any similarity, MIRRORED
    /// placements included (the radius scales with the part; the opening's structuring
    /// element is a BALL, which every reflection maps to itself). The child must lower to a solid
    /// with planar faces, straight convex edges and 3-valent corners with one incident
    /// face perpendicular to the other two — boxes, convex prisms, sheared boxes;
    /// concave edges and general trihedral corners are refused by name at lowering
    /// (todo.md carries the corner-patch follow-up). Implicit and mesh bridge through
    /// the exact rounded B-Rep. For organic rounding of arbitrary shapes, the implicit
    /// route (<see cref="Offset"/> composed −r then +r) remains available.</para>
    /// </summary>
    public Shape RoundEdges(double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return new RoundEdgesShape(this, radius);
    }

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
    /// Non-uniform scale about the origin (OpenSCAD's <c>scale([x, y, z])</c>).
    /// Support follows the node, exactly as <see cref="Explain"/> reports it: profile
    /// extrusions (box, cylinder, sketch extrude, wedge) bake any affine map into
    /// their construction inputs and stay B-Rep-Native; a sphere/torus/cone under a
    /// non-uniform scale would need an ellipsoid-family surface and is
    /// B-Rep-Impossible; implicit lowerings of a sheared subtree bridge through a
    /// tessellated mesh SDF (a non-uniform scale breaks the distance metric, so there
    /// is no exact field form); meshes transform exactly.
    /// </summary>
    public Shape Scale(double x, double y, double z) => Scale(new Vector3d(x, y, z));

    /// <inheritdoc cref="Scale(double, double, double)"/>
    public Shape Scale(in Vector3d factors)
    {
        if (!(factors.X > 0) || !(factors.Y > 0) || !(factors.Z > 0))
            throw new ArgumentOutOfRangeException(nameof(factors),
                "Scale factors must be positive (use Mirror for reflections).");
        return Transform(Matrix4d.CreateScale(factors));
    }

    /// <summary>
    /// The shape's axis-aligned bounds, measured on its mesh lowering at
    /// <paramref name="quality"/> — the one route every shape has. Tessellations
    /// inscribe curved surfaces, so curved extents read a chord's sagitta small at
    /// coarse quality; exact for polyhedral geometry.
    /// </summary>
    public Aabb Bounds(MeshQuality? quality = null)
    {
        var bounds = Aabb.Empty;
        foreach (var position in ToMesh(quality).ToIndexed().Positions)
            bounds = bounds.Union(position);
        return bounds;
    }

    /// <summary>
    /// Scales the shape (about the origin, per axis) so its bounds measure
    /// <paramref name="newSize"/> — OpenSCAD's <c>resize()</c>. A zero component
    /// keeps that axis unscaled, or, with the matching <paramref name="auto"/> flag,
    /// scales it by the same factor as the first sized axis (so
    /// <c>Resized((50, 0, 0), auto: (false, true, true))</c> is a proportional
    /// resize). The current size is measured per <see cref="Bounds"/> — on the mesh
    /// lowering at <paramref name="quality"/>, eagerly, at this call.
    /// <para>The result is an ordinary scale transform, so representation support is
    /// <see cref="Scale(double, double, double)"/>'s: equal factors keep every node's
    /// support unchanged; unequal factors are B-Rep-Impossible for the curved
    /// primitives (the message names the surface it would need) and bridge the
    /// implicit lowering through a tessellated mesh SDF.</para>
    /// </summary>
    public Shape Resized(in Vector3d newSize, (bool X, bool Y, bool Z) auto, MeshQuality? quality = null)
    {
        if (newSize.X < 0 || newSize.Y < 0 || newSize.Z < 0)
            throw new ArgumentOutOfRangeException(nameof(newSize), "Target sizes must be non-negative.");
        if (newSize.X == 0 && newSize.Y == 0 && newSize.Z == 0)
            throw new ArgumentException("At least one target size must be positive.", nameof(newSize));

        var size = Bounds(quality).Size;
        Span<double> factors = [1, 1, 1];
        Span<double> targets = [newSize.X, newSize.Y, newSize.Z];
        Span<bool> autos = [auto.X, auto.Y, auto.Z];
        Span<double> current = [size.X, size.Y, size.Z];

        double? firstFactor = null;
        for (int axis = 0; axis < 3; axis++)
        {
            if (targets[axis] <= 0)
                continue;
            if (current[axis] <= 0)
                throw new InvalidOperationException(
                    $"The shape has zero extent on axis {(char)('X' + axis)}; it cannot be resized to {targets[axis]:g4} there.");
            factors[axis] = targets[axis] / current[axis];
            firstFactor ??= factors[axis];
        }
        for (int axis = 0; axis < 3; axis++)
        {
            if (targets[axis] <= 0 && autos[axis])
                factors[axis] = firstFactor ?? throw new ArgumentException(
                    "An auto axis needs at least one sized axis to take its factor from.", nameof(auto));
        }
        return Scale(new Vector3d(factors[0], factors[1], factors[2]));
    }

    /// <summary>Resize with one auto flag for every zero-sized axis — OpenSCAD's
    /// <c>resize(newsize, auto)</c>. See
    /// <see cref="Resized(in Vector3d, ValueTuple{bool, bool, bool}, MeshQuality?)"/>.</summary>
    public Shape Resized(in Vector3d newSize, bool auto = false, MeshQuality? quality = null) =>
        Resized(newSize, (auto, auto, auto), quality);

    /// <summary>
    /// Mirror across the plane through <paramref name="point"/> with
    /// <paramref name="normal"/> (OpenSCAD's <c>mirror()</c>). Correct in every
    /// representation: meshes transform positions and reverse winding (staying
    /// outward-oriented), implicit lowering reflects the query point (exact), and
    /// B-Rep support follows the node: box/cylinder/sketch-extrude handle any affine
    /// map; sphere/torus/cone re-place natively under mirrored similarities; revolves
    /// negate the transformed axis (a reflection conjugates the rotation,
    /// F·Rot(d, φ)·F = Rot(−F·d, φ) — the identity that also makes mirrored threads
    /// left-handed); sweeps need no fix at all (rotation-minimizing transport is
    /// intrinsic); and rim features / drills follow, since chamfers, fillets and
    /// revolved tools commute with isometries. Draft, <c>Shell(t, openings)</c>,
    /// <c>RoundEdges</c>, <c>Loft</c> and the pure taper need no identity at all — each
    /// is defined by LENGTHS and ANGLES, which every isometry preserves, so the
    /// operation on the mirrored child IS the mirrored operation (draft carries the pull
    /// direction's linear image; a rounding's structuring element is a ball). The one
    /// refusal left in this family is <c>SheetMetalBody</c>, whose flange tree is
    /// ordered and quoted on named edges and would have to be rebuilt the other way
    /// round rather than re-placed. See <see cref="Explain"/> for the per-node verdicts.
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

    // ---- Provenance (topological naming) ----

    /// <summary>
    /// Names this construction step. The shape is unchanged — <c>Tag</c> is geometrically
    /// transparent in all three representations — but the B-Rep lowering stamps
    /// <paramref name="tag"/> onto every face it produces, and the faces carry it forward,
    /// so <see cref="FaceSetRef.Tagged"/> can select them later by the name the DESIGN
    /// gave them rather than by what they happen to look like.
    ///
    /// <code>
    /// var body = plate | Shape.Cylinder(8, 12).Translate(20, 0, 6).Tag("boss");
    /// var top  = FaceRef.Extreme(FaceSetRef.Tagged("boss"), Vector3d.UnitZ);
    /// </code>
    ///
    /// <para>This is the persistent half of topological naming, and it complements rather
    /// than replaces the semantic <c>BrepQueries</c> selectors: a semantic query says what
    /// a face IS ("the upward planar one"), a tag says where it CAME FROM. Two identical
    /// bosses are indistinguishable to a query and trivially distinguishable by tag.</para>
    ///
    /// <para><b>The guarantee, and exactly where it stops.</b> A tag is inherited wherever
    /// a face is derived from another. That covers everything the boolean pipeline does
    /// (faces it does not touch are passed through by reference, and every fragment its face
    /// splitting produces takes its parent's tags), so tags survive unions, differences,
    /// intersections, <c>Drill</c>, patterns and transforms — and it covers the operations
    /// that REBUILD a solid face by face, each of which hands every new face its positional
    /// parent: <see cref="Draft"/>,
    /// <see cref="Shell(double, System.Collections.Generic.IEnumerable{int}?)"/>,
    /// <see cref="RoundEdges"/>, and rim
    /// <see cref="Fillet(double, Func{BrepSolid, IEnumerable{BrepFace}}?)"/> /
    /// <see cref="Chamfer(double, Func{BrepSolid, IEnumerable{BrepFace}}?)"/> surgery.</para>
    ///
    /// <para>What carries no provenance is what is genuinely NEW rather than derived, and
    /// that is a statement about the geometry rather than a gap: a fillet band, a corner
    /// patch and a partial run's termination face descend from no single face, so they are
    /// left untagged instead of being attributed to one of the two surfaces they join.
    /// Shelling is the case worth knowing about in the other direction — a wall and its
    /// cavity twin BOTH descend from one parent, so a tagged face there answers with two.
    /// A STEP round trip drops provenance entirely; there is no AP214 entity for it.</para>
    ///
    /// <para><b>Two consequences worth internalizing.</b> A tag names a <em>set</em>, never
    /// "the" face: one face can split into several, so <see cref="FaceSetRef.Tagged"/> is
    /// set-valued by construction and <c>FaceRef.One(...)</c> over it is a claim the author
    /// makes deliberately. And the failure is <em>one-sided</em>: a lost tag yields FEWER
    /// faces, never a face from somewhere else — so an over-narrow selection fails its
    /// cardinality contract loudly instead of quietly blending the wrong edge.</para>
    /// </summary>
    /// <param name="tag">A name meaningful to the design. It is stored in the
    /// geometry-reference descriptor grammar (so a tagged selector serializes with the
    /// rest of a feature's parameters), which restricts it to ASCII letters, digits and
    /// underscores — refused by name rather than quietly mangled, since a mangled tag
    /// would resolve to nothing.</param>
    public Shape Tag(string tag) => new TagShape(this, RefSyntax.RequireIdentifier(tag, nameof(tag)));

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
    /// <see cref="Section"/> without the flattening: the cross-section as exact
    /// <see cref="CurvedRegion2d"/>s, so a bore's rim is ONE arc rather than however many
    /// chords a tolerance asked for — which is what a DXF <c>CIRCLE</c> entity, an SVG
    /// <c>A</c> command and <see cref="Sketch.FromCurvedRegion"/> all want.
    ///
    /// <para>Requires a B-Rep lowering (a mesh section has no exact curves to recover), and
    /// what it cannot express exactly it FLATTENS to <paramref name="chordTolerance"/>
    /// rather than refusing: an oblique plane through a cylinder cuts an ellipse, which the
    /// curved 2D tier deliberately does not carry, and a traced intersection is a polyline
    /// to begin with. So a mixed section is honest, and its exact halves stay exact.</para>
    /// </summary>
    /// <exception cref="ShapeConversionException">The shape has no B-Rep form.</exception>
    public IReadOnlyList<CurvedRegion2d> SectionExact(
        SketchPlane plane, double chordTolerance = PlanarSection.DefaultChordTolerance) =>
        PlanarSection.CurvedOfSolid(ToBrep(), plane.Frame, chordTolerance);

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
