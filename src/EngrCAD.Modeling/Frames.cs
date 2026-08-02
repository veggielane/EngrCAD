using System.Globalization;
using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A structural section for frame members: a designation (what a cut list orders) plus
/// the exact cross-section <see cref="Sketch"/>. Factories cover the common shapes —
/// <see cref="Flat"/> bar, <see cref="Shs"/>/<see cref="Rhs"/> hollow sections,
/// <see cref="EqualAngle"/>, <see cref="Channel"/> and <see cref="RoundTube"/> — and a
/// custom profile is any sketch the caller supplies (wall thickness is exact because a
/// hollow section is simply the outer loop <see cref="Sketch.WithHole"/> the inner).
///
/// <para><b>The member run line passes through the sketch ORIGIN</b>, so where the
/// factory puts the section relative to (0, 0) is part of its contract: the symmetric
/// shapes (flat, SHS/RHS, tube) are centred — the run is the centroid axis — while the
/// angle sits with its heel corner AT the origin (legs along +x and +y, how a fabricator
/// datums an angle) and the channel with the back of its web on x = 0, centred
/// vertically, opening toward +x.</para>
///
/// <para><b>Sharp-corner idealization</b>: the hollow-section and angle factories draw
/// square corners, where rolled and cold-formed stock carries corner radii (an EN 10219
/// SHS rounds at roughly 2t outside). Areas and masses therefore read slightly above the
/// datasheet rows; the profiles are exact for what they draw, and what they draw is the
/// idealization — stated rather than hidden.</para>
/// </summary>
public sealed class FrameProfile
{
    /// <summary>What a cut list calls this section (e.g. "SHS 40x40x3").</summary>
    public string Designation { get; }

    /// <summary>The exact cross-section, in the member's local x/y plane. The member
    /// run line passes through the sketch origin; local +y is the member's "up"
    /// (see <see cref="WeldmentOptions.Up"/>).</summary>
    public Sketch Section { get; }

    /// <summary>Exact section area (outer minus holes), from <see cref="Sketch.Area"/>.</summary>
    public double Area { get; }

    /// <summary>A profile from any closed sketch. The run line passes through the sketch
    /// origin — place the section around (0, 0) accordingly.</summary>
    public FrameProfile(string designation, Sketch section)
    {
        if (string.IsNullOrWhiteSpace(designation))
            throw new ArgumentException("A frame profile needs a designation.", nameof(designation));
        ArgumentNullException.ThrowIfNull(section);
        Designation = designation;
        Section = section;
        Area = section.Area();
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Solid flat bar, centred: <paramref name="width"/> along local x,
    /// <paramref name="thickness"/> along local y (the member's up direction).</summary>
    public static FrameProfile Flat(double width, double thickness)
    {
        if (!(width > 0) || !(thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(width), "Flat bar dimensions must be positive.");
        return new($"FLAT {N(width)}x{N(thickness)}", Sketch.Rectangle(width, thickness));
    }

    /// <summary>Square hollow section, centred, sharp corners (see the class remarks).</summary>
    public static FrameProfile Shs(double size, double wall) => Rhs(size, size, wall, $"SHS {N(size)}x{N(size)}x{N(wall)}");

    /// <summary>Rectangular hollow section, centred: <paramref name="width"/> along local
    /// x, <paramref name="height"/> along local y. Sharp corners (see the class remarks).</summary>
    public static FrameProfile Rhs(double width, double height, double wall) =>
        Rhs(width, height, wall, $"RHS {N(width)}x{N(height)}x{N(wall)}");

    private static FrameProfile Rhs(double width, double height, double wall, string designation)
    {
        if (!(width > 0) || !(height > 0))
            throw new ArgumentOutOfRangeException(nameof(width), "Hollow-section dimensions must be positive.");
        if (!(wall > 0) || wall * 2 >= Math.Min(width, height))
            throw new ArgumentOutOfRangeException(nameof(wall),
                "The wall must be positive and less than half the smaller outside dimension.");
        return new(designation,
            Sketch.Rectangle(width, height).WithHole(Sketch.Rectangle(width - 2 * wall, height - 2 * wall)));
    }

    /// <summary>Equal-leg angle, heel corner at the origin, legs along +x and +y.
    /// Sharp corners — real angles carry root and toe radii (see the class remarks).</summary>
    public static FrameProfile EqualAngle(double leg, double wall)
    {
        if (!(leg > 0))
            throw new ArgumentOutOfRangeException(nameof(leg), "The leg length must be positive.");
        if (!(wall > 0) || wall >= leg)
            throw new ArgumentOutOfRangeException(nameof(wall), "The wall must be positive and less than the leg.");
        return new($"L {N(leg)}x{N(leg)}x{N(wall)}", Sketch.Polygon(
        [
            new(0, 0), new(leg, 0), new(leg, wall), new(wall, wall), new(wall, leg), new(0, leg),
        ]));
    }

    /// <summary>Plain (untapered) channel: back of the web on x = 0, centred vertically,
    /// opening toward +x. <paramref name="width"/> is the flange reach along x,
    /// <paramref name="height"/> the overall depth along y.</summary>
    public static FrameProfile Channel(double width, double height, double wall)
    {
        if (!(width > 0) || !(height > 0))
            throw new ArgumentOutOfRangeException(nameof(width), "Channel dimensions must be positive.");
        if (!(wall > 0) || wall >= width || wall * 2 >= height)
            throw new ArgumentOutOfRangeException(nameof(wall),
                "The wall must be positive, less than the width, and less than half the height.");
        double h = height / 2;
        return new($"C {N(width)}x{N(height)}x{N(wall)}", Sketch.Polygon(
        [
            new(0, -h), new(width, -h), new(width, -h + wall), new(wall, -h + wall),
            new(wall, h - wall), new(width, h - wall), new(width, h), new(0, h),
        ]));
    }

    /// <summary>Circular hollow section (round tube), centred.</summary>
    public static FrameProfile RoundTube(double outerDiameter, double wall)
    {
        if (!(outerDiameter > 0))
            throw new ArgumentOutOfRangeException(nameof(outerDiameter), "The outside diameter must be positive.");
        if (!(wall > 0) || wall * 2 >= outerDiameter)
            throw new ArgumentOutOfRangeException(nameof(wall),
                "The wall must be positive and less than half the outside diameter.");
        return new($"CHS {N(outerDiameter)}x{N(wall)}",
            Sketch.Circle(outerDiameter / 2).WithHole(Sketch.Circle(outerDiameter / 2 - wall)));
    }

    public override string ToString() => Designation;
}

/// <summary>
/// A small catalogue of common metric structural sections, as
/// <see cref="FrameProfile"/> factories. Nominal dimension sets from the EN series
/// (EN 10219 cold-formed hollow sections, EN 10056-1 equal angles, EN 10220 tube).
/// ⚠ Verify against the current datasheet before ordering — and note the sharp-corner
/// idealization <see cref="FrameProfile"/> states (real hollow sections and angles
/// carry corner radii, so catalogue masses per metre sit slightly below these).
/// </summary>
public static class StandardSections
{
    /// <summary>SHS 25×25×2 (EN 10219). ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Shs25x2 => FrameProfile.Shs(25, 2);

    /// <summary>SHS 40×40×3 (EN 10219). ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Shs40x3 => FrameProfile.Shs(40, 3);

    /// <summary>SHS 50×50×3 (EN 10219). ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Shs50x3 => FrameProfile.Shs(50, 3);

    /// <summary>RHS 50×30×3 (EN 10219). ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Rhs50x30x3 => FrameProfile.Rhs(50, 30, 3);

    /// <summary>CHS 26.9×2.6 (EN 10220). ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Chs26x2_6 => FrameProfile.RoundTube(26.9, 2.6);

    /// <summary>CHS 42.4×3.2 (EN 10220). ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Chs42x3_2 => FrameProfile.RoundTube(42.4, 3.2);

    /// <summary>Equal angle 25×25×3 (EN 10056-1). ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Angle25x3 => FrameProfile.EqualAngle(25, 3);

    /// <summary>Equal angle 40×40×4 (EN 10056-1). ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Angle40x4 => FrameProfile.EqualAngle(40, 4);

    /// <summary>Flat bar 50×6. ⚠ Verify against the current datasheet.</summary>
    public static FrameProfile Flat50x6 => FrameProfile.Flat(50, 6);
}

/// <summary>How the members of a two-member joint are trimmed against each other.</summary>
public enum FrameJointStyle
{
    /// <summary>Both members are cut back by the exact bisector plane of their two
    /// axes — the classic mitred corner. Works for any profile whose outline is lines
    /// and circular arcs (a mitred round tube's cut is the exact plane∩cylinder
    /// ellipse).</summary>
    Miter,

    /// <summary>The member of the EARLIER run keeps its natural square end at the joint;
    /// the later run's member is trimmed back by the plane of the through member's wall
    /// facing it. The through member must present a FLAT wall toward the joining member
    /// (a round tube as the through member is the coped-saddle case and is refused —
    /// see <see cref="Cope"/>).</summary>
    Butt,

    /// <summary>Coped (saddle / fishmouth) tube-on-tube joints — REFUSED by name.
    /// The saddle cut is the intersection of two non-coaxial cylinders, a genuinely
    /// transcendental pair the surface-intersection marching tracer is known to
    /// under-seed at structural-section scales (the recorded thread-scale finding:
    /// branches missed outright and traced branches stopping short of their rails), so
    /// the cut would be a sampled polyline whose error is a floor no tessellation
    /// density can lower. Miter tube joints instead, or butt onto a flat-walled
    /// member.</summary>
    Cope,
}

/// <summary>Options for <see cref="Weldment.Build"/>.</summary>
public sealed class WeldmentOptions
{
    /// <summary>How two-member joints are trimmed (default <see cref="FrameJointStyle.Miter"/>).</summary>
    public FrameJointStyle JointStyle { get; init; } = FrameJointStyle.Miter;

    /// <summary>
    /// The roll reference: each member's profile +y axis points along this direction
    /// projected perpendicular to the member axis (default world +Z, so a horizontal
    /// member stands its section upright). <b>The stated fallback rule</b>: a run
    /// parallel to this direction (nothing to project) takes world +Z, then +Y, then +X —
    /// the first that is not parallel to the run — so a vertical member under the default
    /// gets +Y as its profile "up" and its profile x-axis lands on world +X. A member
    /// usually wants a stated up, which is why this is a parameter and not an
    /// arbitrary-perpendicular convention.
    /// </summary>
    public Vector3d Up { get; init; } = Vector3d.UnitZ;

    /// <summary>Material stamped on every member part (drives BOM mass and the default
    /// display colour); null leaves the members unstated.</summary>
    public Material? Material { get; init; }

    /// <summary>The assembly name <see cref="Weldment.ToAssembly"/> uses.</summary>
    public string Name { get; init; } = "frame";
}

/// <summary>
/// One trimmed member of a <see cref="Weldment"/>: the run it came from, its local
/// frame, its exact trimmed solid and the <see cref="Part"/> wrapping it.
/// </summary>
/// <param name="Index">The run's index in the skeleton.</param>
/// <param name="Start">The run's start point (on the member's origin fiber).</param>
/// <param name="End">The run's end point.</param>
/// <param name="Frame">The member's local frame: origin at <paramref name="Start"/>,
/// Z along the run, Y the projected up direction (the profile's local axes).</param>
/// <param name="CutLength">The stock length to cut, in millimetres: the member's exact
/// overall extent along its own axis after trimming (for a mitred end, to the longest
/// point of the miter). Also stamped on <see cref="Part.CutLength"/>, which is what
/// makes a <see cref="Bom"/> of the assembly double as the cut list.</param>
/// <param name="Shape">The member's trimmed solid — an extrusion of the profile minus
/// one planar cutting tool per trimmed end, so it is B-Rep-native and every cut face is
/// an exact plane.</param>
/// <param name="Part">The member as a part, named
/// "<c>{designation} x {cut length}</c>" — identical members share the name, so
/// <see cref="Bom.ByItem"/> rolls them up into one cut-list line.</param>
public sealed record FrameMember(
    int Index, Vector3d Start, Vector3d End, Frame3d Frame,
    double CutLength, Shape Shape, Part Part);

/// <summary>
/// Frames and weldments: straight structural members on a skeleton of runs, trimmed at
/// the joints — the SolidWorks-weldment capability over the machinery that already
/// exists (profiles are <see cref="Sketch"/>es, members are <see cref="Shape.Extrude(Sketch, double, SketchPlane?)"/>,
/// joints are exact plane cuts via the B-Rep boolean, and the cut list is the
/// <see cref="Bom"/> reading <see cref="Part.CutLength"/>).
///
/// <code>
/// var frame = Weldment.Path(StandardSections.Shs40x3,
///     [new(0, 0, 0), new(500, 0, 0), new(500, 0, 300), new(0, 0, 300)],
///     closed: true,
///     new WeldmentOptions { Up = Vector3d.UnitY, Material = Materials.Steel });
/// scene.Add(frame.ToAssembly());
/// var cutList = Bom.For(frame.ToAssembly());
/// </code>
///
/// <para><b>Joints are detected at shared run endpoints</b> (weld tier, 1e-9): exactly
/// two members meeting at a point are trimmed per <see cref="WeldmentOptions.JointStyle"/>;
/// a free end gets the extrusion's own square cap. The miter plane through joint
/// <c>j</c> between members leaving along unit directions <c>a</c> and <c>b</c> has
/// normal <c>a − b</c> — that plane contains both the angle bisector (<c>(a+b)·(a−b) =
/// 0</c>) and the axes' common normal, with no division anywhere, so there is no apex
/// arithmetic to get wrong. Both members' cutting tools are built from the SAME
/// <c>(j, n̂)</c> pair (one exact negation apart), so the two cut faces lie in the
/// bit-identical plane and the closed frame's volume is exactly the sum of its
/// members'.</para>
///
/// <para><b>Every cut is a transversal boolean by construction</b>: a member is extruded
/// OVERLONG past each trimmed joint (by the exact reach of the cut plane across its own
/// section, plus a margin), then a box tool whose base face lies exactly ON the cut
/// plane subtracts the stub — so no boolean ever sees coplanar or tangent input (the
/// <c>Drill</c> overshoot lesson), and the plane∩plane / plane∩cylinder curves are all
/// analytic. The two halves of a joint are separate parts that meet face-to-face in the
/// assembly; no member is ever booleaned against another.</para>
///
/// <para><b>Deciding "one part per member"</b>: each member is its own <see cref="Part"/>
/// (identical members do NOT share a part reference), because "the same member" would be
/// a tolerance judgement over cut planes expressed in local frames that differ by
/// rotation round-off — exactly the kind of near-tie the codebase avoids deciding by
/// ulps. Instead identical members share their NAME (designation + cut length), which is
/// the rollup key <see cref="Bom.ByItem"/> already documents; the reference-identity
/// lines underneath stay honest.</para>
///
/// <para><b>Scope, stated</b>: straight members of one profile per weldment; joints of
/// exactly two members. Multi-member joints, T-joints against a member's side, mixed
/// profiles in one skeleton, corner reliefs and curved members are refused by name or
/// left to future work; coped tube-on-tube saddles are refused with the reason on
/// <see cref="FrameJointStyle.Cope"/>.</para>
/// </summary>
public sealed class Weldment
{
    /// <summary>The section every member carries.</summary>
    public FrameProfile Profile { get; }

    /// <summary>The trimmed members, in run order.</summary>
    public IReadOnlyList<FrameMember> Members { get; }

    private readonly WeldmentOptions _options;

    private Weldment(FrameProfile profile, IReadOnlyList<FrameMember> members, WeldmentOptions options)
    {
        Profile = profile;
        Members = members;
        _options = options;
    }

    /// <summary>Total stock length of the cut list (Σ member cut lengths), millimetres.</summary>
    public double TotalCutLength => Members.Sum(m => m.CutLength);

    /// <summary>
    /// The weldment as a fresh <see cref="Assembly"/> (name from
    /// <see cref="WeldmentOptions.Name"/>): one occurrence per member at identity —
    /// member geometry is built in world coordinates. The same <see cref="Part"/>
    /// references are used every call, so BOMs over different calls agree.
    /// </summary>
    public Assembly ToAssembly()
    {
        var assembly = new Assembly(_options.Name);
        foreach (var member in Members)
            assembly.Add(member.Part);
        return assembly;
    }

    /// <summary>A weldment along a polyline: consecutive points become runs (closed adds
    /// the last-to-first run), so consecutive members share endpoints and get joints.</summary>
    public static Weldment Path(
        FrameProfile profile, IReadOnlyList<Vector3d> points, bool closed = false,
        WeldmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException("A path needs at least two points.", nameof(points));
        var runs = new List<(Vector3d, Vector3d)>();
        for (int i = 0; i + 1 < points.Count; i++)
            runs.Add((points[i], points[i + 1]));
        if (closed)
            runs.Add((points[^1], points[0]));
        return Build(profile, runs, options);
    }

    /// <summary>Builds the weldment: validates the whole skeleton first (every refusal
    /// fires before any geometry is built), then trims each member.</summary>
    public static Weldment Build(
        FrameProfile profile, IReadOnlyList<(Vector3d Start, Vector3d End)> runs,
        WeldmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(runs);
        options ??= new WeldmentOptions();
        if (runs.Count == 0)
            throw new ArgumentException("A weldment needs at least one run.", nameof(runs));
        if (options.JointStyle == FrameJointStyle.Cope)
            throw new NotSupportedException(
                "Coped (saddle) tube joints are not supported: the saddle is the intersection of two "
                + "non-coaxial cylinders, a transcendental pair the surface-intersection marching tracer "
                + "under-seeds at structural-section scales, so the cut would be a sampled polyline whose "
                + "error no tessellation density can lower. Miter the joint, or butt onto a flat-walled member.");

        // ---- validate the skeleton (all-or-nothing, before any geometry) ----
        int n = runs.Count;
        var axes = new Vector3d[n];
        var lengths = new double[n];
        for (int i = 0; i < n; i++)
        {
            var (s, e) = runs[i];
            var axis = e - s;
            double length = axis.Length;
            if (length <= 1e-9) // weld tier: a shorter run has no direction to build on
                throw new ArgumentException($"Run {i} has zero length (start and end coincide at {s}).");
            axes[i] = axis / length;
            lengths[i] = length;
        }

        // Joints: run endpoints grouped at the weld tier. Each entry is (run, end) with
        // end 0 = the run's start point, 1 = its end point.
        var jointOf = new (int Partner, int PartnerEnd)?[n, 2];
        for (int i = 0; i < n; i++)
        {
            for (int ei = 0; ei < 2; ei++)
            {
                var p = ei == 0 ? runs[i].Start : runs[i].End;
                for (int j = i; j < n; j++)
                {
                    for (int ej = 0; ej < 2; ej++)
                    {
                        if (j == i && ej <= ei)
                            continue;
                        var q = ej == 0 ? runs[j].Start : runs[j].End;
                        if ((p - q).Length > 1e-9)
                            continue;
                        if (j == i)
                            throw new ArgumentException($"Run {i}'s two endpoints coincide."); // unreachable past the length guard
                        if (jointOf[i, ei] is not null || jointOf[j, ej] is not null)
                            throw new NotSupportedException(
                                $"Three or more members meet at {p}. Multi-member joints are where corner "
                                + "cases begin (which pair miters, and what closes the third?) and are not "
                                + "supported; join two members per point and trim the rest deliberately.");
                        jointOf[i, ei] = (j, ej);
                        jointOf[j, ej] = (i, ei);
                    }
                }
            }
        }

        // T-joints: an endpoint landing on another run's INTERIOR is a member butting
        // against a side, which v1 does not trim — the untrimmed member would bury
        // itself half a section deep in the other. Refuse by name.
        for (int i = 0; i < n; i++)
        {
            for (int ei = 0; ei < 2; ei++)
            {
                var p = ei == 0 ? runs[i].Start : runs[i].End;
                for (int j = 0; j < n; j++)
                {
                    if (j == i)
                        continue;
                    if ((p - runs[j].Start).Length <= 1e-9 || (p - runs[j].End).Length <= 1e-9)
                        continue; // that is a joint (or a refused multi-joint), handled above
                    if (DistanceToSegment(p, runs[j].Start, runs[j].End) <= 1e-9)
                        throw new NotSupportedException(
                            $"Run {i} ends on the interior of run {j} (a T-joint at {p}). Trimming a member "
                            + "against another member's side is not supported yet; end the run on the through "
                            + "member's wall instead, or lay the skeleton out with two-member corner joints.");
                }
            }
        }

        // ---- per-member trimming ----
        var members = new List<FrameMember>(n);
        var bounds = profile.Section.Bounds;
        double diagonal = bounds.Size.Length;
        for (int i = 0; i < n; i++)
        {
            var (s, e) = runs[i];
            var frame = MemberFrame(s, axes[i], options.Up);
            var startCut = EndCut(profile, runs, axes, jointOf, options, i, end: 0);
            var endCut = EndCut(profile, runs, axes, jointOf, options, i, end: 1);
            members.Add(BuildMember(profile, options, i, s, e, lengths[i], frame, startCut, endCut, diagonal));
        }
        return new Weldment(profile, members, options);
    }

    // ---- geometry ----

    /// <summary>A trimming plane for one member end: the member keeps
    /// <c>KeptNormal·(x − Point) ≥ 0</c>. Null = the extrusion's own square cap.</summary>
    private readonly record struct PlaneCut(Vector3d Point, Vector3d KeptNormal);

    private static Frame3d MemberFrame(in Vector3d origin, in Vector3d axis, in Vector3d up)
    {
        // The stated roll rule (see WeldmentOptions.Up): project the up direction
        // perpendicular to the axis; a run parallel to up falls back to +Z, +Y, +X.
        Span<Vector3d> candidates = [up, Vector3d.UnitZ, Vector3d.UnitY, Vector3d.UnitX];
        foreach (var candidate in candidates)
        {
            var projected = candidate - axis * candidate.Dot(axis);
            if (projected.Length <= 1e-9)
                continue;
            var y = projected.Normalized();
            var x = y.Cross(axis); // right-handed: x × y = axis
            return Frame3d.FromOrthonormal(origin, x, y);
        }
        throw new InvalidOperationException("No roll reference found for the member axis."); // unreachable: the axes are orthogonal
    }

    private static PlaneCut? EndCut(
        FrameProfile profile, IReadOnlyList<(Vector3d Start, Vector3d End)> runs,
        Vector3d[] axes, (int Partner, int PartnerEnd)?[,] jointOf, WeldmentOptions options,
        int run, int end)
    {
        if (jointOf[run, end] is not { } joint)
            return null; // free end: the extrusion's own cap
        // The CANONICAL joint point: both members of one joint read the same stored
        // endpoint (the lower run's), so their cut planes share the origin bit-for-bit
        // even when the user's two coincident endpoints differ in the last ulps.
        var j = run < joint.Partner
            ? (end == 0 ? runs[run].Start : runs[run].End)
            : (joint.PartnerEnd == 0 ? runs[joint.Partner].Start : runs[joint.Partner].End);
        // Unit directions AWAY from the joint, into each member's kept material.
        var a = end == 0 ? axes[run] : -axes[run];
        var b = joint.PartnerEnd == 0 ? axes[joint.Partner] : -axes[joint.Partner];

        if (options.JointStyle == FrameJointStyle.Miter)
        {
            // The bisector plane's normal is a − b: it contains the bisector direction
            // ((a+b)·(a−b) = |a|² − |b|² = 0 for unit vectors) and the axes' common
            // normal, with no division anywhere. n̂·a = (1 − a·b)/|n| > 0, so the kept
            // side is n̂·(x − j) ≥ 0 for THIS member by construction; the partner's cut
            // uses the exactly negated normal, so the two cut planes are bit-identical.
            var normal = a - b;
            if (normal.Length <= 1e-9)
                throw new NotSupportedException(
                    $"Runs {run} and {joint.Partner} leave their shared joint at {j} in the same direction "
                    + "(a zero-angle joint), so no miter plane exists between them.");
            return new PlaneCut(j, normal.Normalized());
        }

        // Butt: the earlier run runs through with its own square end; the later run is
        // trimmed back by the through member's facing wall plane.
        bool through = run < joint.Partner;
        if (through)
            return null;
        // Facing direction: this member's kept direction, projected perpendicular to the
        // through member's axis (b is the through member's kept direction, so its axis
        // line carries ±b).
        var facing = a - b * a.Dot(b);
        if (facing.Length <= 1e-9)
            return null; // collinear splice: the square end IS flush with the through cap
        var w = facing.Normalized();
        // The wall offset: the through member's section extent along w, in ITS frame.
        // The frame's axes depend only on the run's axis direction and Up, so building
        // it from the run start reproduces exactly the frame the through member's own
        // solid is built on.
        var throughFrame = MemberFrame(runs[joint.Partner].Start, axes[joint.Partner], options.Up);
        var wLocal = new Vector2d(w.Dot(throughFrame.X), w.Dot(throughFrame.Y));
        double offset = FlatWallOffset(profile, wLocal, run, joint.Partner);
        return new PlaneCut(j + w * offset, w);
    }

    private static FrameMember BuildMember(
        FrameProfile profile, WeldmentOptions options, int index,
        in Vector3d start, in Vector3d end, double length, in Frame3d frame,
        PlaneCut? startCut, PlaneCut? endCut, double diagonal)
    {
        double margin = 0.25 * diagonal;

        // Axial crossing s(p) of each cut plane per profile fiber p (affine in p), and
        // its extremes over the outer outline — exact for line/arc outlines.
        (double Min, double Max)? startRange = null, endRange = null;
        Vector2d startG = default, endG = default;
        double startC0 = 0, endC0 = 0;
        if (startCut is { } sc)
        {
            (startC0, startG) = CutFunctional(sc, start, frame);
            startRange = OutlineExtremes(profile, startG, startC0);
        }
        if (endCut is { } ec)
        {
            (endC0, endG) = CutFunctional(ec, start, frame);
            endRange = OutlineExtremes(profile, endG, endC0);
        }

        // A member both of whose end cuts cross inside it has fibers of negative length —
        // the solid would be nonsense. min over the outline of (sEnd − sStart) is another
        // affine functional's extreme.
        if (startRange is not null && endRange is not null)
        {
            var difference = OutlineExtremes(profile, endG - startG, endC0 - startC0);
            if (difference.Min <= 0)
                throw new NotSupportedException(
                    $"Member {index}'s two end cuts cross inside it — the member is too short for its "
                    + "joint angles. Lengthen the run or change the joint style.");
        }

        double pre = startRange is { } sr ? Math.Max(0, -sr.Min) + margin : 0;
        double post = endRange is { } er ? Math.Max(0, er.Max - length) + margin : 0;

        var axis = frame.Z;
        var plane = new SketchPlane(Frame3d.FromOrthonormal(start - axis * pre, frame.X, frame.Y));
        var shape = Shape.Extrude(profile.Section, pre + length + post, plane);

        // Subtract a box tool per cut end. The tool's BASE face lies exactly on the cut
        // plane (its sketch plane IS that plane), extruding into the discard side; sized
        // generously so its other faces never touch the member (never coplanar input).
        // Sizing is generous rather than tight — only NON-coincidence matters: the
        // discarded stub reaches at most (pre + length + post) axially and a section
        // diagonal laterally from the cut point, so these bounds clear it with room.
        double lateral = 4 * (pre + length + post + 2 * diagonal);
        if (startCut is { } cutS)
            shape = shape.Subtract(CutTool(cutS, depth: Math.Abs(startRange!.Value.Max) + pre + length + diagonal, lateral));
        if (endCut is { } cutE)
            shape = shape.Subtract(CutTool(cutE, depth: Math.Abs(endRange!.Value.Min) + post + length + diagonal, lateral));

        // The stock length: the member's exact overall axial extent after trimming.
        double sMin = startRange?.Min ?? 0;
        double sMax = endRange?.Max ?? length;
        double cutLength = sMax - sMin;

        string name = $"{profile.Designation} x {cutLength.ToString("0.##", CultureInfo.InvariantCulture)}";
        var part = new Part(name, shape).Of(options.Material);
        part.CutLength = cutLength;
        return new FrameMember(index, start, end, frame, cutLength, shape, part);
    }

    /// <summary>The cut plane's axial crossing s(p) = c0 + g·p for the fiber through
    /// profile point p: x(s) = memberStart + X·p.x + Y·p.y + Z·s.</summary>
    private static (double C0, Vector2d G) CutFunctional(in PlaneCut cut, in Vector3d memberStart, in Frame3d frame)
    {
        double denom = cut.KeptNormal.Dot(frame.Z);
        // |denom| > 0 by construction: n̂·a > 0 at every cut and a = ±Z. (A zero-angle
        // joint, the one configuration that zeroes it, is refused before this runs.)
        return (cut.KeptNormal.Dot(cut.Point - memberStart) / denom,
                new Vector2d(-cut.KeptNormal.Dot(frame.X) / denom, -cut.KeptNormal.Dot(frame.Y) / denom));
    }

    private static Shape CutTool(in PlaneCut cut, double depth, double lateral)
    {
        // Discard side = −KeptNormal. FromNormal(±n̂) negates exactly (normalization
        // commutes with negation bit-for-bit), so the two members of one joint cut on
        // the bit-identical plane.
        var plane = new SketchPlane(Frame3d.FromNormal(cut.Point, -cut.KeptNormal));
        return Shape.Extrude(Sketch.Rectangle(lateral, lateral), depth, plane);
    }

    // ---- exact extremes of an affine functional over the profile outline ----

    /// <summary>Min/max of c0 + g·p over the profile's OUTER outline. Exact per segment
    /// family: line endpoints; arc endpoints plus the interior stationary angles that
    /// fall inside the sweep. Elliptical and Bézier outline segments are refused by name
    /// — their extruded walls' intersection with a cut plane has no analytic curve in
    /// the kernel, so the cut would fall to the marching tracer's sampled polylines.</summary>
    private static (double Min, double Max) OutlineExtremes(FrameProfile profile, in Vector2d g, double c0)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        void Consider(double value)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        foreach (var curve in profile.Section.ToCurves())
        {
            switch (curve)
            {
                case Line2d line:
                    Consider(g.Dot(line.Start));
                    Consider(g.Dot(line.End));
                    break;
                case Arc2d arc:
                {
                    Consider(g.Dot(arc.PointAt(0)));
                    Consider(g.Dot(arc.PointAt(1)));
                    if (g.LengthSquared > 0)
                    {
                        // Stationary where the tangent is perpendicular to g: the radial
                        // is ±g, at θ* = atan2(g.y, g.x) and θ* + π.
                        double angle = Math.Atan2(g.Y, g.X);
                        foreach (double candidate in (Span<double>)[angle, angle + Math.PI])
                        {
                            if (ArcContains(arc, candidate))
                                Consider(g.Dot(arc.Center) + Math.Cos(candidate) * arc.Radius * g.X
                                                          + Math.Sin(candidate) * arc.Radius * g.Y);
                        }
                    }
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"The '{profile.Designation}' profile outline has a {curve.GetType().Name} segment. "
                        + "Joint trimming supports outlines of lines and circular arcs only: an extruded "
                        + "elliptical or Bezier wall meets the cut plane in a curve with no analytic form in "
                        + "the kernel, so the cut would fall to the marching tracer's sampled polylines, whose "
                        + "error is a floor no tessellation density can lower. Use a line/arc profile, or "
                        + "leave this member's ends untrimmed.");
            }
        }
        return (min + c0, max + c0);
    }

    /// <summary>Whether the polar angle lies inside the arc's signed sweep. Angular
    /// slack is absolute (radians are dimensionless — the recorded convention for
    /// angular guards).</summary>
    private static bool ArcContains(Arc2d arc, double angle)
    {
        double sweep = arc.SweepAngle;
        const double tau = 2 * Math.PI;
        if (Math.Abs(sweep) >= tau - 1e-12)
            return true;
        double delta = (angle - arc.StartAngle) % tau;
        if (delta < 0)
            delta += tau;
        if (sweep >= 0)
            return delta <= sweep + 1e-12;
        return delta >= tau + sweep - 1e-12 || delta <= 1e-12;
    }

    /// <summary>The through member's wall offset along <paramref name="wLocal"/> for a
    /// butt joint, refusing when the extreme is not a flat wall: a round (arc) extreme
    /// is the coped-saddle case, a polygon corner is a knife-edge seat.</summary>
    private static double FlatWallOffset(FrameProfile profile, in Vector2d wLocal, int trimmedRun, int throughRun)
    {
        double max = double.NegativeInfinity;
        foreach (var curve in profile.Section.ToCurves())
        {
            switch (curve)
            {
                case Line2d line:
                    max = Math.Max(max, Math.Max(wLocal.Dot(line.Start), wLocal.Dot(line.End)));
                    break;
                case Arc2d arc:
                    max = Math.Max(max, Math.Max(wLocal.Dot(arc.PointAt(0)), wLocal.Dot(arc.PointAt(1))));
                    if (wLocal.LengthSquared > 0)
                    {
                        double angle = Math.Atan2(wLocal.Y, wLocal.X);
                        foreach (double candidate in (Span<double>)[angle, angle + Math.PI])
                        {
                            if (ArcContains(arc, candidate))
                                max = Math.Max(max, wLocal.Dot(arc.Center)
                                    + Math.Cos(candidate) * arc.Radius * wLocal.X
                                    + Math.Sin(candidate) * arc.Radius * wLocal.Y);
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException(
                        $"The '{profile.Designation}' profile outline has a {curve.GetType().Name} segment; "
                        + "butt joints need a line/arc outline (see the miter refusal for why).");
            }
        }

        // Flat iff some straight outline edge attains the extreme at BOTH endpoints
        // (weld tier: profile coordinates are model millimetres).
        bool flat = false;
        bool curved = false;
        foreach (var curve in profile.Section.ToCurves())
        {
            switch (curve)
            {
                case Line2d line when wLocal.Dot(line.Start) >= max - 1e-9 && wLocal.Dot(line.End) >= max - 1e-9:
                    flat = true;
                    break;
                case Arc2d arc when wLocal.LengthSquared > 0:
                {
                    double angle = Math.Atan2(wLocal.Y, wLocal.X);
                    foreach (double candidate in (Span<double>)[angle, angle + Math.PI])
                    {
                        if (ArcContains(arc, candidate)
                            && wLocal.Dot(arc.Center) + Math.Cos(candidate) * arc.Radius * wLocal.X
                                + Math.Sin(candidate) * arc.Radius * wLocal.Y >= max - 1e-9)
                            curved = true;
                    }
                    break;
                }
            }
        }
        if (flat)
            return max;
        if (curved)
            throw new NotSupportedException(
                $"Run {trimmedRun} butts onto run {throughRun}, whose '{profile.Designation}' section presents a "
                + "ROUND wall toward it — that is the coped (saddle) tube joint, which is refused: the saddle "
                + "is a transcendental cylinder-cylinder intersection the marching tracer under-seeds at "
                + "structural-section scales. Miter the joint instead, or butt onto a flat-walled member.");
        throw new NotSupportedException(
            $"Run {trimmedRun} butts onto run {throughRun}, whose '{profile.Designation}' section presents no flat "
            + "wall toward it (the extreme is a corner). Rotate the profile (the Up option), or miter the joint.");
    }

    private static double DistanceToSegment(in Vector3d point, in Vector3d a, in Vector3d b)
    {
        var direction = b - a;
        double lengthSquared = direction.LengthSquared;
        if (lengthSquared <= 0)
            return (point - a).Length;
        double t = Math.Clamp((point - a).Dot(direction) / lengthSquared, 0, 1);
        return (point - (a + direction * t)).Length;
    }
}
