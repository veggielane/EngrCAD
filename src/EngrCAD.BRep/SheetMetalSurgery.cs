using EngrCAD.Core;

namespace EngrCAD.BRep;

/// <summary>
/// The cross-section of ONE sheet-metal bend, in the plane perpendicular to the bend
/// axis. Everything the folded geometry needs is a closed form of six numbers, and this
/// is the single place they are written down — the surgery below builds its faces from
/// it, and the modelling layer's unfold walker places its flat frames from it, so the
/// folded body and the flat pattern cannot drift apart.
///
/// <para><b>The K-factor is deliberately absent.</b> It locates the neutral axis, which
/// decides the developed LENGTH and nothing whatever about the folded shape. Keeping it
/// out of this struct is what makes the folded-versus-flat volume comparison a real
/// test rather than a tautology (see <c>SheetMetalSpec</c> in EngrCAD.Modeling).</para>
/// </summary>
/// <param name="BendLinePoint">A point on the bend line: it lies on the INSIDE sheet
/// face (the one the flange folds toward) and on the side wall the flange replaces.</param>
/// <param name="Inside">Unit outward normal of the inside sheet face — the direction the
/// flange rotates toward.</param>
/// <param name="Outward">Unit outward normal of the side wall at the bend line,
/// perpendicular to <paramref name="Inside"/>: the direction the flange initially
/// leaves the sheet in.</param>
/// <param name="Thickness">Sheet thickness.</param>
/// <param name="BendRadius">INSIDE bend radius (the concave side, against
/// <paramref name="Inside"/>).</param>
/// <param name="AngleRadians">Bend angle: how far the flange turns from the sheet's own
/// plane. 90 degrees is a square flange; 0 would be no bend at all.</param>
public readonly record struct SheetBendSection(
    Vector3d BendLinePoint,
    Vector3d Inside,
    Vector3d Outward,
    double Thickness,
    double BendRadius,
    double AngleRadians)
{
    /// <summary>The bend axis direction: rotation from <see cref="Outward"/> toward
    /// <see cref="Inside"/> is positive about it.</summary>
    public Vector3d AxisDirection => Outward.Cross(Inside);

    /// <summary>The bend axis passes through here — one inside radius above the bend
    /// line, on the inside face's side.</summary>
    public Vector3d AxisPoint => BendLinePoint + Inside * BendRadius;

    /// <summary>The radial direction (axis to material) where the bend ends.</summary>
    private Vector3d EndRadial =>
        -Inside * Math.Cos(AngleRadians) + Outward * Math.Sin(AngleRadians);

    /// <summary>Where the inside surface leaves the bend and becomes flat again.</summary>
    public Vector3d InsideTangentPoint => AxisPoint + EndRadial * BendRadius;

    /// <summary>Where the outside surface leaves the bend and becomes flat again.</summary>
    public Vector3d OutsideTangentPoint => AxisPoint + EndRadial * (BendRadius + Thickness);

    /// <summary>Unit direction the straight wall runs in after the bend.</summary>
    public Vector3d FlangeDirection =>
        Outward * Math.Cos(AngleRadians) + Inside * Math.Sin(AngleRadians);

    /// <summary>Outward normal of the flange's INSIDE face — the continuation of
    /// <see cref="Inside"/> past the bend. It is exactly minus the radial at the bend's
    /// end, which is why it is written as that rather than transcribed again.</summary>
    public Vector3d InsideNormalAfterBend => -EndRadial;

    /// <summary>
    /// Outside setback (OSSB): the distance from either tangent point to the OUTER
    /// VIRTUAL SHARP — the corner where the two outside planes would meet if the bend
    /// were square. The classical sheet-metal quantity, and the datum this kernel
    /// measures a flange's <c>Length</c> from.
    /// </summary>
    public double OutsideSetback => OutsideSetbackOf(AngleRadians, BendRadius, Thickness);

    /// <summary>
    /// <c>OSSB = (R + T)·tan(θ/2)</c> — <b>the one place this formula is written.</b>
    /// EngrCAD.Modeling's <c>SheetMetalSpec.OutsideSetback</c> delegates here rather than
    /// transcribing it, so the folded body's geometry and the flat pattern's lengths
    /// cannot disagree across the assembly boundary.
    /// </summary>
    public static double OutsideSetbackOf(double angleRadians, double insideRadius, double thickness) =>
        (insideRadius + thickness) * Math.Tan(angleRadians / 2);

    /// <summary>The exact arc for one surface of the bend, from the sheet's own plane
    /// round to the tangent point. <paramref name="radius"/> is measured from the axis,
    /// so <see cref="BendRadius"/> traces the inside surface and
    /// <c>BendRadius + Thickness</c> the outside.</summary>
    public NurbsCurve Arc(double radius) =>
        NurbsCurve.Arc(AxisPoint, -Inside, Outward, radius, 0, AngleRadians);
}

/// <summary>
/// Sheet-metal edge-flange construction as direct topology surgery on a B-Rep solid —
/// the same doctrine as <see cref="Filleting"/>'s rim features, and for the same reason.
///
/// <para><b>Why there is no boolean here.</b> A flange's bend band meets both the parent
/// sheet and the flange wall <em>tangentially</em>: cylinder and plane share a common
/// tangent plane along the whole bend line. Handing that to <c>BrepBoolean</c> would
/// present it with exactly the coincident/tangent input the v1 boolean refuses by name —
/// and there is nothing to compute anyway, because every face of a bend is known in
/// closed form from <see cref="SheetBendSection"/>. So the bend is built and welded
/// straight into the parent's loops, as a rim fillet's band is. If you find yourself
/// reaching for a union here, that is why it is not one.</para>
///
/// <para><b>A flange's two ends are independent.</b> Each end is either FLUSH with the
/// wall's own corner — where the flange's cross-section is spliced into the neighbouring
/// face's loop, which must be planar and square to the bend line — or INSET, where the
/// flange gets a planar cap and the leftover wall a stub. A flange running to one end of
/// a plate takes one of each, which is the ordinary shop case and needs no special path:
/// the rims are split only at the ends that are inset, and each end is closed by whichever
/// of the two rules applies to it.</para>
///
/// <para><b>A CLOSED CORNER is one operation over two flanges</b>, not two that happen to
/// meet — see <see cref="AddCornerFlanges"/>, whose miter is the exact bicylinder ellipse
/// rather than an intersection. Declaring the pair is what lets both walls be located
/// before either is edited; a second flange added afterwards on a neighbouring edge finds
/// a wall that is no longer four-sided, and is refused here naming the corner call.</para>
///
/// <para><b>What is not surgery at all</b>: a bend RELIEF notches the blank the sheet is
/// extruded from, so a relieved flange arrives here as an ordinary flange flush against
/// the notch's own wall (<c>SheetMetalBody.BaseOutline</c>); and a HEM, a JOG and a CURL
/// are two or more ordinary flanges, so they reach this file as the bends they are.</para>
///
/// <para><b>What is still refused by name</b> rather than approximated: bends along
/// non-straight edges (folding along a curve is not an isometry of the sheet — the band's
/// Gaussian curvature stops being zero — so it is forming rather than bending and has no
/// flat blank behind it), a 180-degree fold (a hem is two bends, not one), and any flange
/// whose bend would interact with a feature other than a declared corner.</para>
/// </summary>
public static class SheetMetalSurgery
{
    /// <summary>The epsilon ladder's absolute WELD tier, named rather than re-typed.
    /// Everything measured against it here is either a LENGTH between points that were
    /// constructed exactly (an edge's own endpoint, a bend line derived from the same
    /// frame) or a DIMENSIONLESS quantity (an angle in radians, a unit vector's dot
    /// product), both of which the absolute tier is right for. Note what is deliberately
    /// never compared this way: no area, cross product or other squared quantity, which
    /// would need the scale-free relative tier instead.</summary>
    private static readonly double Weld = Tolerance.Default.Linear;

    /// <summary>
    /// Grows one edge flange, returning a NEW solid. The input's topology is edited in
    /// place and must not be reused afterwards (the <c>BrepBoolean</c> contract).
    /// </summary>
    /// <param name="solid">The sheet body so far.</param>
    /// <param name="section">The bend cross-section — everything about the flange apart
    /// from its extent along the bend line.</param>
    /// <param name="spanStart">One end of the flange on the bend line.</param>
    /// <param name="spanEnd">The other end.</param>
    /// <param name="wallLength">Length of the straight wall past the bend: the flange's
    /// overall length from the outer virtual sharp, minus
    /// <see cref="SheetBendSection.OutsideSetback"/>.</param>
    public static BrepSolid AddEdgeFlange(
        BrepSolid solid, in SheetBendSection section,
        in Vector3d spanStart, in Vector3d spanEnd, double wallLength) =>
        AddEdgeFlange(solid, section, spanStart, spanEnd, wallLength, []);

    /// <inheritdoc cref="AddEdgeFlange(BrepSolid, in SheetBendSection, in Vector3d, in Vector3d, double)"/>
    /// <param name="wallCutouts">Closed profiles lying ON one of the flange wall's two
    /// planar faces, each punched clean through the wall. Which face they lie on is
    /// DERIVED from their own plane rather than declared (see <c>PunchWall</c>), so a
    /// caller cannot state one and supply the other.</param>
    public static BrepSolid AddEdgeFlange(
        BrepSolid solid, in SheetBendSection section,
        in Vector3d spanStart, in Vector3d spanEnd, double wallLength,
        IReadOnlyList<Profile> wallCutouts)
    {
        ArgumentNullException.ThrowIfNull(solid);
        ArgumentNullException.ThrowIfNull(wallCutouts);
        Validate(section, wallLength);

        var n = section.Inside;
        var a = section.AxisDirection;

        // Order the span along +a so every loop below reads the same way round.
        var (q0, q1) = Order(spanStart, spanEnd, a);

        // Everything checkable is checked HERE, before a single coedge moves. Rim surgery
        // rewrites loops in place, so a refusal that fired part-way would leave a
        // half-edited solid — the rule Filleting's edge-set features already follow.
        var site = Locate(solid, q0, q1, section);

        // Rims: the pieces of the two sheet-face edges the bend takes over, plus the
        // vertices at the flange's ends. A rim is split exactly at the ends that are INSET,
        // which is what creates those vertices; a flush end already has the wall's own
        // corner there.
        var rims = ResolveRims(site, n, section.Thickness, q0, q1, a);

        var flange = BuildFlange(
            section with { BendLinePoint = q0 }, q1 - q0, wallLength, rims, wallCutouts, null);

        var faces = solid.Faces.Where(f => !ReferenceEquals(f, site.Wall)).ToList();
        faces.AddRange(flange.Faces);
        CloseEnds(site, flange, rims, section, faces, -1);

        // The wall is consumed by the bend; detaching drops its coedges from the surviving
        // rims' use lists so they read exactly two uses again.
        Detach(site.Wall);

        var result = new BrepSolid([new BrepShell(faces)]);
        result.Validate();
        return result;
    }

    // ------------------------------------------------------------------ closed corners

    /// <summary>
    /// Grows TWO flanges that meet at a shared corner of the sheet, MITRED against each
    /// other so the corner closes with no gap.
    ///
    /// <para><b>The miter is a closed form, not an intersection.</b> Two bends of the same
    /// radius quoted on the same face have axes that meet over the sheet's corner, and two
    /// equal-radius cylinders with intersecting axes meet in two ELLIPSES — the exact fact
    /// <see cref="Filleting"/> already uses for a sharp rim corner. So the corner needs no
    /// surface–surface intersection, no boolean and no new surface type: each band's cut is
    /// an <see cref="Ellipse3d"/> whose centre is the shared axis point, whose one semi-axis
    /// is the inward radial and whose other reaches the point where the two OFFSET bend
    /// lines cross.</para>
    ///
    /// <para><b>And the two flanges' cut chains are the SAME curves</b> — the configuration
    /// is symmetric under reflection in the miter plane, which swaps the two flanges — so
    /// they are built once and used by one face of each. Nothing lies in the miter plane,
    /// which is what "closed" means: the corner is welded, not butted across a gap.</para>
    ///
    /// <para><b>Why one operation rather than two.</b> A full-width flange consumes its
    /// wall and splices its cross-section into the faces at both ends, so a second flange
    /// quoted on a neighbouring edge would find a wall that is no longer four-sided — which
    /// is exactly the refusal this replaces. Locating BOTH before either is built is the
    /// whole fix, and it also lets the sheet's own corner edge (used by the two walls and by
    /// nothing else afterwards) simply fall away.</para>
    /// </summary>
    public static BrepSolid AddCornerFlanges(
        BrepSolid solid,
        in SheetBendSection sectionA, in Vector3d startA, in Vector3d endA, double wallA,
        IReadOnlyList<Profile> cutoutsA,
        in SheetBendSection sectionB, in Vector3d startB, in Vector3d endB, double wallB,
        IReadOnlyList<Profile> cutoutsB)
    {
        ArgumentNullException.ThrowIfNull(solid);
        Validate(sectionA, wallA);
        Validate(sectionB, wallB);
        RequireCompatibleCorner(sectionA, sectionB, wallA, wallB);

        var (q0A, q1A) = Order(startA, endA, sectionA.AxisDirection);
        var (q0B, q1B) = Order(startB, endB, sectionB.AxisDirection);
        var corner = SharedCorner([q0A, q1A], [q0B, q1B]);

        // Both walls are located BEFORE either is edited, which is the whole point: a
        // full-width flange consumes its wall and splices into the faces at both ends, so
        // the second one would otherwise find a wall that is no longer four-sided.
        var siteA = Locate(solid, q0A, q1A, sectionA, corner);
        var siteB = Locate(solid, q0B, q1B, sectionB, corner);

        int cornerA = q0A.DistanceTo(corner) <= Weld ? 0 : 1;
        int cornerB = q0B.DistanceTo(corner) <= Weld ? 0 : 1;
        RequireRunsIntoTheCorner(siteA, cornerA);
        RequireRunsIntoTheCorner(siteB, cornerB);

        var rimsA = ResolveRims(siteA, sectionA.Inside, sectionA.Thickness, q0A, q1A, sectionA.AxisDirection);
        var rimsB = ResolveRims(siteB, sectionB.Inside, sectionB.Thickness, q0B, q1B, sectionB.AxisDirection);

        var miter = BuildMiter(
            sectionA, sectionB, corner, wallA,
            rimsA.InsideVertex[cornerA], rimsA.OutsideVertex[cornerA]);

        var flangeA = BuildFlange(
            sectionA with { BendLinePoint = q0A }, q1A - q0A, wallA, rimsA, cutoutsA,
            new CornerEnd(cornerA, miter));
        var flangeB = BuildFlange(
            sectionB with { BendLinePoint = q0B }, q1B - q0B, wallB, rimsB, cutoutsB,
            new CornerEnd(cornerB, miter));

        var faces = solid.Faces
            .Where(f => !ReferenceEquals(f, siteA.Wall) && !ReferenceEquals(f, siteB.Wall))
            .ToList();
        faces.AddRange(flangeA.Faces);
        faces.AddRange(flangeB.Faces);
        CloseEnds(siteA, flangeA, rimsA, sectionA, faces, cornerA);
        CloseEnds(siteB, flangeB, rimsB, sectionB, faces, cornerB);

        Detach(siteA.Wall);
        Detach(siteB.Wall);

        var result = new BrepSolid([new BrepShell(faces)]);
        result.Validate();
        return result;
    }

    /// <summary>The shared cross-section two mitred flanges weld along: five curves and the
    /// four vertices between them, built ONCE and used by one face of each flange.</summary>
    /// <param name="Bisector">The outer ellipse's own semi-axis toward the corner. Kept
    /// because it BOUNDS the arc's reach past the span, which the arc's two endpoints do
    /// not: past a square bend the ellipse's furthest point is its semi-axis, at t = 90°,
    /// strictly inside the trimmed range.</param>
    private sealed record Miter(
        BrepVertex TangentInside, BrepVertex TangentOutside,
        BrepVertex TipInside, BrepVertex TipOutside,
        BrepEdge InsideArc, BrepEdge OutsideArc,
        BrepEdge InsideWall, BrepEdge OutsideWall, BrepEdge Tip,
        Vector3d Bisector);

    /// <summary>The ONE point both spans reach — the sheet corner the two flanges miter at.
    /// Refused by name when there is not exactly one, since "which corner" would otherwise
    /// be a guess.</summary>
    private static Vector3d SharedCorner(Vector3d[] spanA, Vector3d[] spanB)
    {
        var shared = spanA.Where(a => spanB.Any(b => b.DistanceTo(a) <= Weld)).ToList();
        if (shared.Count != 1)
            throw new NotSupportedException(
                $"The two flanges of a closed corner must meet at exactly ONE shared point of the sheet; this " +
                $"pair shares {shared.Count}. Quote them on edges that share a corner, and let each run the " +
                "whole way into it.");
        return shared[0];
    }

    private static void RequireRunsIntoTheCorner(FlangeSite site, int end)
    {
        if (!site.Flush[end])
            throw new NotSupportedException(
                "A closed corner needs both bends to run the whole way into the corner: this one stops short " +
                "of it, which is the RELIEVED corner (inset the flange and cut a bend relief) rather than the " +
                "closed one.");
    }

    private static void RequireCompatibleCorner(
        in SheetBendSection a, in SheetBendSection b, double wallA, double wallB)
    {
        if (Math.Abs(a.BendRadius - b.BendRadius) > Weld || Math.Abs(a.Thickness - b.Thickness) > Weld)
            throw new NotSupportedException(
                $"A closed corner miters two bends of the SAME inside radius and thickness; these are " +
                $"{a.BendRadius:g6}/{a.Thickness:g6} and {b.BendRadius:g6}/{b.Thickness:g6}. Two different " +
                "radii give two cylinders of different radii, whose intersection is a quartic rather than the " +
                "ellipse the miter is built from.");
        if (Math.Abs(a.AngleRadians - b.AngleRadians) > Weld)
            throw new NotSupportedException(
                $"A closed corner miters two bends through the SAME angle; these turn " +
                $"{a.AngleRadians * 180 / Math.PI:g6} and {b.AngleRadians * 180 / Math.PI:g6} degrees. At " +
                "different angles the two walls end at different heights and their mitred ends do not meet.");
        if (Math.Abs(wallA - wallB) > Weld)
            throw new NotSupportedException(
                $"A closed corner miters two flanges of the same length; their walls measure {wallA:g6} and " +
                $"{wallB:g6}. Above the shorter one the miter would cut a wall with nothing on the other side " +
                "of it.");
        if (a.Inside.Dot(b.Inside) < 1 - Weld)
            throw new NotSupportedException(
                "A closed corner needs both flanges quoted on the SAME face of the sheet and folded the same " +
                "way; these fold toward opposite faces, so their bends never meet.");
    }

    /// <summary>
    /// The mitred cross-section, in closed form.
    ///
    /// <para>Both bends' axes pass through <c>corner + Inside·R</c>, so the two cylinders
    /// share that centre and their intersection is the ellipse
    /// <c>C₀ − Inside·r·cos t + (Q(r) − corner)·sin t</c>, where <c>Q(r)</c> is where the
    /// two bend lines offset OUTWARD by r cross. That offset crossing has a closed form of
    /// its own: <c>Q = corner + r(ŵ_A + ŵ_B)/(1 + ŵ_A·ŵ_B)</c>, which is the bisector, so
    /// nothing is intersected numerically anywhere in here.</para>
    ///
    /// <para>The mitred WALL end is where the two flanges' wall planes and their tip planes
    /// meet — one 3×3 solve, and the reflection that swaps the two flanges fixes the miter
    /// line pointwise, which is why both tips land on the same point at any bend angle.</para>
    /// </summary>
    private static Miter BuildMiter(
        in SheetBendSection a, in SheetBendSection b, in Vector3d corner, double wallLength,
        BrepVertex quotedCorner, BrepVertex farCorner)
    {
        double c = a.Outward.Dot(b.Outward);
        if (c <= -1 + Weld)
            throw new NotSupportedException(
                "The two bend lines of a closed corner point in opposite directions, so their offsets never " +
                "cross and there is no corner to close.");
        var bisector = (a.Outward + b.Outward) / (1 + c);
        var centre = corner + a.Inside * a.BendRadius;
        double angle = a.AngleRadians;

        var insideArc = new Ellipse3d(
            centre, -a.Inside * a.BendRadius, bisector * a.BendRadius);
        var outsideArc = new Ellipse3d(
            centre, -a.Inside * (a.BendRadius + a.Thickness), bisector * (a.BendRadius + a.Thickness));

        var tangentInside = new BrepVertex(insideArc.PointAt(angle));
        var tangentOutside = new BrepVertex(outsideArc.PointAt(angle));
        var tipInside = new BrepVertex(WallCorner(a, b, tangentInside.Position, wallLength));
        var tipOutside = new BrepVertex(WallCorner(a, b, tangentOutside.Position, wallLength));

        return new Miter(
            tangentInside, tangentOutside, tipInside, tipOutside,
            new BrepEdge(insideArc, new Interval(0, angle), quotedCorner, tangentInside),
            new BrepEdge(outsideArc, new Interval(0, angle), farCorner, tangentOutside),
            Segment(tangentInside, tipInside),
            Segment(tangentOutside, tipOutside),
            Segment(tipInside, tipOutside),
            bisector * (a.BendRadius + a.Thickness));
    }

    /// <summary>Where the two mitred walls' tips meet: the point on both wall planes at
    /// wall-length from each flange's own tangent line. Three planes, one Cramer solve —
    /// exact, and the same point from either flange because the miter plane's reflection
    /// fixes it.</summary>
    private static Vector3d WallCorner(
        in SheetBendSection a, in SheetBendSection b, in Vector3d tangent, double wallLength)
    {
        // A wall plane contains the bend axis direction and the flange direction, so its
        // normal is their cross; the tip plane is perpendicular to the flange direction at
        // the stated length.
        var nA = a.AxisDirection.Cross(a.FlangeDirection);
        var nB = b.AxisDirection.Cross(b.FlangeDirection);
        var nT = a.FlangeDirection;
        double dA = nA.Dot(tangent);
        double dB = nB.Dot(tangent);
        double dT = nT.Dot(tangent) + wallLength;

        var cross = nB.Cross(nT);
        double determinant = nA.Dot(cross);
        if (Math.Abs(determinant) <= 1e-12)
            throw new NotSupportedException(
                "The two flanges of a closed corner are parallel, so their walls never meet and there is no " +
                "miter. Quote them on edges that genuinely turn a corner.");
        return (cross * dA + nT.Cross(nA) * dB + nA.Cross(nB) * dT) / determinant;
    }

    private static (Vector3d Q0, Vector3d Q1) Order(
        in Vector3d start, in Vector3d end, in Vector3d axis)
    {
        var (q0, q1) = (end - start).Dot(axis) >= 0 ? (start, end) : (end, start);
        if ((q1 - q0).Dot(axis) <= Weld)
            throw new ArgumentException("The flange's span along the bend line must be positive.");
        return (q0, q1);
    }

    private static void Validate(in SheetBendSection section, double wallLength)
    {
        if (!(section.Thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(section), "Sheet thickness must be positive.");
        if (!(section.BendRadius > 0))
            throw new ArgumentOutOfRangeException(nameof(section),
                "The inside bend radius must be positive: a zero-radius fold is not a manufacturable bend " +
                "and has no cylindrical band to build.");
        if (!(section.AngleRadians > Weld) || section.AngleRadians >= Math.PI - Weld)
            throw new ArgumentOutOfRangeException(nameof(section),
                $"The bend angle is {section.AngleRadians * 180 / Math.PI:g6} degrees; it must lie strictly " +
                "between 0 and 180. A 180-degree fold is a HEM, and a hem is TWO bends rather than one: at " +
                "exactly 180 the outside setback (R + T)*tan(angle/2) diverges, and a return leg lying flat " +
                "against the sheet is coincident boundary. Use SheetMetalBody.WithHem, which states the open " +
                "gap and derives the intermediate leg from it.");
        if (!(wallLength > Weld))
            throw new ArgumentOutOfRangeException(nameof(wallLength),
                $"The straight wall past the bend measures {wallLength:g6}. A flange's length is measured " +
                "from the OUTER VIRTUAL SHARP, so it must exceed the outside setback " +
                $"(R + T)*tan(angle/2) = {section.OutsideSetback:g6}.");
        // Dimensionless: these two directions define the bend's plane, so this is an
        // exactness check on the caller's frame, not a model-unit tolerance.
        if (Math.Abs(section.Inside.Dot(section.Outward)) > Weld
            || Math.Abs(section.Inside.LengthSquared - 1) > Weld
            || Math.Abs(section.Outward.LengthSquared - 1) > Weld)
            throw new ArgumentException(
                "A bend section needs orthonormal Inside and Outward directions.", nameof(section));
    }

    // ------------------------------------------------------------------ the flange site

    /// <summary>Where the flange grows from: the bend edge on the inside sheet face, the
    /// matching edge on the outside face, the side wall between them, and the wall's two
    /// end edges (indexed by which end of the flange they sit at). Everything indexed by
    /// END is a two-element array in Q0-then-Q1 order, so the two ends read the same way
    /// wherever they are handled independently.</summary>
    private sealed record FlangeSite(
        BrepEdge InsideEdge, BrepEdge OutsideEdge, BrepFace Wall,
        BrepEdge[] EndEdge,
        BrepVertex[] WallCornerInside, BrepVertex[] WallCornerOutside,
        Vector3d[] Q, bool[] Flush);

    /// <summary>The rim edges the bend bands weld to, the vertices at the flange's two
    /// ends, and the leftover rim pieces beyond them. A leftover is null exactly where the
    /// flange is FLUSH with the wall's corner — there is no wall left over there.</summary>
    private sealed record Rims(
        BrepEdge Inside, BrepEdge Outside,
        BrepVertex[] InsideVertex, BrepVertex[] OutsideVertex,
        BrepEdge?[] InsideLeftover, BrepEdge?[] OutsideLeftover);

    /// <summary>Is this span end the corner a mitred pair meets at?</summary>
    private static bool AtCorner(Vector3d? corner, in Vector3d point) =>
        corner is { } c && c.DistanceTo(point) <= Weld;

    private static FlangeSite Locate(
        BrepSolid solid, in Vector3d q0, in Vector3d q1, in SheetBendSection section,
        Vector3d? cornerPoint = null)
    {
        var inside = section.Inside;
        var outward = section.Outward;
        var a = section.AxisDirection;
        BrepEdge? bendEdge = null;
        BrepFace? wall = null;
        foreach (var edge in solid.Edges)
        {
            if (!edge.IsLinear(out var start, out var end))
                continue;
            // Scale-free by construction: IsParallelTo compares an ANGLE (atan2 of the
            // cross against the dot), where testing the bare cross product would apply an
            // absolute tolerance to a length and be wrong at both ends of the scale.
            if (!(end - start).IsParallelTo(a, Tolerance.Default))
                continue;
            if (!OnSegment(start, end, q0) || !OnSegment(start, end, q1))
                continue;
            var users = solid.FacesOf(edge);
            if (users.Count != 2)
                continue;
            var side = users.FirstOrDefault(f => HasNormal(f, outward));
            if (side is null || !users.Any(f => HasNormal(f, inside)))
                continue;
            bendEdge = edge;
            wall = side;
            break;
        }
        if (bendEdge is null || wall is null)
        {
            throw new NotSupportedException(
                $"No straight sheet edge runs from {q0} to {q1} between a planar face with outward normal " +
                $"{inside} and a planar side wall with outward normal {outward}. A flange grows from a " +
                "STRAIGHT edge of a planar sheet face: folding along a curve is not an isometry of the sheet " +
                "(the band's Gaussian curvature stops being zero), so it is forming rather than bending and " +
                "has no flat blank behind it.");
        }

        if (wall.Loops.Count != 1 || wall.OuterLoop.Coedges.Count != 4)
            throw new NotSupportedException(
                $"The side wall at this bend line has {wall.Loops.Count} loop(s) and " +
                $"{wall.OuterLoop.Coedges.Count} edge(s), where a flange grows from a plain four-sided wall. " +
                "A wall already reshaped by a neighbouring flange means the two flanges meet at a CORNER, and " +
                "a corner is ONE operation over two flanges rather than two that happen to meet: declare it " +
                $"with {nameof(AddCornerFlanges)} (SheetMetalBody.WithCorner), which locates both before " +
                "either is built. To leave the corner open instead, inset one flange and cut a bend relief.");

        var coedges = wall.OuterLoop.Coedges;
        int index = 0;
        while (!ReferenceEquals(coedges[index].Edge, bendEdge))
            index++;   // the wall was chosen BECAUSE it uses this edge, so this terminates
        var opposite = coedges[(index + 2) % 4].Edge;
        if (!opposite.IsLinear(out _, out _))
            throw new NotSupportedException("The side wall's far edge must be straight.");

        // The wall loop is a cycle, so the bend edge's two neighbours are exactly the
        // wall's ends; +a decides which is the flange's Q0 end.
        var before = coedges[(index + 3) % 4].Edge;
        var after = coedges[(index + 1) % 4].Edge;
        bool forward = (coedges[index].EndVertex.Position - coedges[index].StartVertex.Position).Dot(a) > 0;
        var (wallStart, wallEnd) = forward
            ? (coedges[index].StartVertex, coedges[index].EndVertex)
            : (coedges[index].EndVertex, coedges[index].StartVertex);
        var (endAtQ0, endAtQ1) = forward ? (before, after) : (after, before);

        // Each end is settled on its own: flush with the wall's corner, or inset from it.
        // The v1 rule refused one of each as "a corner in disguise"; it is not one — an
        // inset end's cap and a flush end's splice never touch the same coedge, so a
        // flange running to one end of a plate is just one of each.
        bool[] flush = [wallStart.Position.DistanceTo(q0) <= Weld, wallEnd.Position.DistanceTo(q1) <= Weld];

        // The wall must descend exactly one sheet thickness at BOTH ends — checked on the
        // wall's own corners, which also settles the split points of an inset end, since
        // both rims are straight and this makes them parallel one thickness apart.
        var wallStartOutside = OppositeEnd(endAtQ0, wallStart);
        var wallEndOutside = OppositeEnd(endAtQ1, wallEnd);
        RequireAt(wallStartOutside, wallStart.Position - inside * section.Thickness, section.Thickness);
        RequireAt(wallEndOutside, wallEnd.Position - inside * section.Thickness, section.Thickness);

        // A FLUSH end splices the flange's cross-section into the face beyond the wall, so
        // that face must be planar and square to the bend line. Checked here rather than at
        // the splice: by then the first end has already been rewritten.
        // (A CORNER end splices into nothing — it welds to its partner — so it is exempt;
        // a corner's own preconditions are checked in AddCornerFlanges, still before either
        // wall is touched.)
        if (flush[0] && !AtCorner(cornerPoint, q0))
            RequireSquareNeighbour(endAtQ0, a, wall);
        if (flush[1] && !AtCorner(cornerPoint, q1))
            RequireSquareNeighbour(endAtQ1, a, wall);

        return new FlangeSite(
            bendEdge, opposite, wall, [endAtQ0, endAtQ1],
            [wallStart, wallEnd], [wallStartOutside, wallEndOutside], [q0, q1], flush);
    }

    /// <summary>
    /// The face across an end edge from the wall must be planar and PERPENDICULAR to the
    /// bend line, since the flange's cross-section has to lie IN its plane.
    ///
    /// <para><b>Perpendicularity is the whole condition, and the SIGN of the neighbour's
    /// normal is deliberately not part of it.</b> The sign says which side of the bend line
    /// the material sits on — a CONVEX corner on the sheet's own outline (normal −a at the
    /// Q0 end) or a REFLEX one (normal +a), which is what every corner of a HOLE is, and so
    /// what every louvre's lanced opening is. Both splice into a simple loop: the tab's
    /// cross-section rises out of the neighbour's plane on the far side of the shared end
    /// edge either way. What genuinely differs is which way round that neighbour's loop
    /// walks, and <see cref="Splice"/> READS that off the coedge rather than deriving it
    /// from a convention — so the gate here states the correctness condition instead of a
    /// proxy for it.</para>
    /// </summary>
    private static void RequireSquareNeighbour(BrepEdge endEdge, in Vector3d axis, BrepFace wall)
    {
        var use = endEdge.Uses.FirstOrDefault(c => !ReferenceEquals(c.Loop.Face, wall))
            ?? throw new InvalidOperationException("The wall's end edge has no neighbouring face.");
        // Dimensionless: two unit directions, so the absolute tier is right.
        if (!use.Loop.Face.IsPlanar(out _, out var normal)
            || Math.Abs(Math.Abs(normal.Dot(axis)) - 1) > Weld)
        {
            throw new NotSupportedException(
                "A full-width flange needs the face at each end of its bend line to be planar and " +
                $"perpendicular to it (bend axis {axis}), so the flange's side can lie in that plane. Inset " +
                "the flange from both ends instead, or square up the neighbouring wall.");
        }
    }

    private static bool HasNormal(BrepFace face, in Vector3d wanted) =>
        face.IsPlanar(out _, out var normal) && normal.AreEqual(wanted, Tolerance.Default);

    private static bool OnSegment(in Vector3d start, in Vector3d end, in Vector3d point)
    {
        var direction = end - start;
        double lengthSquared = direction.LengthSquared;
        if (lengthSquared <= 0)
            return false;
        double t = (point - start).Dot(direction) / lengthSquared;
        return t >= -Weld && t <= 1 + Weld && (start + direction * t).DistanceTo(point) <= Weld;
    }

    // ------------------------------------------------------------------------- rims

    private static void RequireAt(BrepVertex vertex, in Vector3d expected, double thickness)
    {
        if (vertex.Position.DistanceTo(expected) > Weld)
            throw new NotSupportedException(
                $"The wall's far rim runs through {vertex.Position} where the sheet's own thickness " +
                $"({thickness:g6}) puts it at {expected}. A flange grows only from a wall of uniform sheet " +
                "thickness square to both faces.");
    }

    /// <summary>Splits both rims at whichever of the flange's ends are INSET.
    /// <c>SplitEdge</c> patches every using loop, so the two sheet faces and the wall all
    /// follow.</summary>
    private static Rims ResolveRims(
        FlangeSite site, in Vector3d inside, double thickness,
        in Vector3d q0, in Vector3d q1, in Vector3d axis)
    {
        var (insideMiddle, insideVertex, insideLeftover) =
            SplitRim(site.InsideEdge, axis, q0, q1, site.Flush, site.WallCornerInside);
        // Both rims are straight and (per Locate's corner check) exactly one thickness
        // apart, so projecting the span onto the outer rim lands where it should.
        var (outsideMiddle, outsideVertex, outsideLeftover) = SplitRim(
            site.OutsideEdge, axis, q0 - inside * thickness, q1 - inside * thickness,
            site.Flush, site.WallCornerOutside);
        return new Rims(
            insideMiddle, outsideMiddle, insideVertex, outsideVertex, insideLeftover, outsideLeftover);
    }

    /// <summary>
    /// Splits one straight rim at whichever of the two span ends is inset, returning the
    /// piece the bend welds to, the vertices at the flange's two ends and the leftover
    /// pieces beyond them — all in Q0-then-Q1 order whichever way the edge itself runs.
    /// <para>Positions are measured along the bend AXIS rather than as edge fractions, so
    /// every guard here is a model-unit LENGTH and speaks the same units as
    /// <see cref="Locate"/>'s flush test; the two cannot disagree about a degenerate
    /// stub.</para>
    /// </summary>
    private static (BrepEdge Middle, BrepVertex[] Vertex, BrepEdge?[] Leftover) SplitRim(
        BrepEdge edge, in Vector3d axis, in Vector3d q0, in Vector3d q1,
        bool[] flush, BrepVertex[] corner)
    {
        if (flush[0] && flush[1])
            return (edge, [corner[0], corner[1]], [null, null]);

        var domain = edge.Domain;
        var start = edge.Curve.PointAt(domain.Start);
        var end = edge.Curve.PointAt(domain.End);
        double axisStart = start.Dot(axis), axisEnd = end.Dot(axis);
        double[] axisSpan = [q0.Dot(axis), q1.Dot(axis)];
        double[] stub =
        [
            axisSpan[0] - Math.Min(axisStart, axisEnd),
            Math.Max(axisStart, axisEnd) - axisSpan[1],
        ];
        // A BACKSTOP rather than a live guard, and it is worth saying which: an end past the
        // weld tier necessarily has a stub, because this stub and Locate's flush test are
        // the same length measured against the same tier. It stays because this method
        // takes its span as arguments rather than deriving it — the span's own positivity
        // is asked of AddEdgeFlange, not restated here.
        for (int k = 0; k < 2; k++)
        {
            if (!flush[k] && stub[k] <= Weld)
                throw new NotSupportedException(
                    $"An inset end of a flange must leave a positive stub of wall beyond it; this one leaves " +
                    $"{stub[k]:g6} of an edge {Math.Abs(axisEnd - axisStart):g6} long. Make the flange flush " +
                    "with that end of its edge, or move it further in.");
        }

        // Cut in the EDGE's own parameter order, so each later parameter still lies inside
        // whatever the previous cut left. Every parameter is expressed on the SAME base
        // curve, which is what makes that true (SplitEdge slices the domain and shares the
        // curve).
        bool ascending = axisEnd > axisStart;
        var cuts = new List<(double Parameter, int End)>(2);
        for (int k = 0; k < 2; k++)
        {
            if (!flush[k])
                cuts.Add((domain.ParameterAt((axisSpan[k] - axisStart) / (axisEnd - axisStart)), k));
        }
        cuts.Sort((a, b) => a.Parameter.CompareTo(b.Parameter));

        var pieces = new List<BrepEdge>(3);
        var vertex = new BrepVertex?[2];
        var current = edge;
        foreach (var (parameter, endIndex) in cuts)
        {
            var (before, after, split) = TopologyEditor.SplitEdge(current, parameter);
            pieces.Add(before);
            vertex[endIndex] = split;
            current = after;
        }
        pieces.Add(current);
        if (!ascending)
            pieces.Reverse();

        // In Q0-to-Q1 order the pieces are: [leftover at Q0], the bend's rim, [leftover at Q1].
        int index = 0;
        var leftover = new BrepEdge?[2];
        if (!flush[0])
            leftover[0] = pieces[index++];
        var middle = pieces[index++];
        if (!flush[1])
            leftover[1] = pieces[index];
        return (middle,
            [flush[0] ? corner[0] : vertex[0]!, flush[1] ? corner[1] : vertex[1]!],
            leftover);
    }

    // -------------------------------------------------------------- the flange geometry

    /// <summary>What one flange contributes: its five faces, and the cross-section chain
    /// at each end as (edge, sense) descriptors — materialized into coedges only where
    /// they are actually used, since constructing a <see cref="BrepCoedge"/> registers it
    /// on its edge. Both chains are already stored in the direction their consumer walks
    /// them, so nothing downstream has to decide which end reverses.</summary>
    private sealed record FlangeBuild(
        IReadOnlyList<BrepFace> Faces, (BrepEdge Edge, bool SameSense)[][] EndChains);

    /// <summary>A straight edge between two vertices — the shape eleven of this file's
    /// edges have, written once so a position array can never be paired with the wrong
    /// vertex array.</summary>
    private static BrepEdge Segment(BrepVertex from, BrepVertex to) =>
        new(new Line3d(from.Position, to.Position), Interval.Unit, from, to);

    /// <summary>The end of a flange that is MITRED into a closed corner: index plus the
    /// shared cross-section it welds to. Everything the flange would have built for itself
    /// at that end is taken from the miter instead, which is what makes the two flanges
    /// share edges rather than meet across a gap.</summary>
    private sealed record CornerEnd(int End, Miter Miter);

    private static FlangeBuild BuildFlange(
        in SheetBendSection s0, in Vector3d span, double wallLength, Rims rims,
        IReadOnlyList<Profile> wallCutouts, CornerEnd? corner)
    {
        // The far end's cross-section is the near one translated along the bend line —
        // one fact, so the caller cannot hand over an inconsistent pair.
        var s1 = s0 with { BendLinePoint = s0.BendLinePoint + span };
        var u = s0.FlangeDirection;
        var v = s0.InsideNormalAfterBend;
        double thickness = s0.Thickness;
        double radius = s0.BendRadius;

        Vector3d[] tangentInside = [s0.InsideTangentPoint, s1.InsideTangentPoint];
        Vector3d[] tangentOutside = [s0.OutsideTangentPoint, s1.OutsideTangentPoint];
        var alongSpan = span.Normalized();

        var tangentInsideVertex = tangentInside.Select(p => new BrepVertex(p)).ToArray();
        var tangentOutsideVertex = tangentOutside.Select(p => new BrepVertex(p)).ToArray();
        var tipInsideVertex = tangentInside.Select(p => new BrepVertex(p + u * wallLength)).ToArray();
        var tipOutsideVertex = tangentOutside.Select(p => new BrepVertex(p + u * wallLength)).ToArray();

        // The bend arcs are the same construction as the band surfaces' generators (used
        // verbatim below), so the boundary samples the tessellator reads off the edges
        // land on the surfaces' own natural grid.
        NurbsCurve[] insideArcCurve = [s0.Arc(radius), s1.Arc(radius)];
        NurbsCurve[] outsideArcCurve = [s0.Arc(radius + thickness), s1.Arc(radius + thickness)];

        var insideArc = new BrepEdge[2];
        var outsideArc = new BrepEdge[2];
        var insideWall = new BrepEdge[2];
        var outsideWall = new BrepEdge[2];
        var tipEnd = new BrepEdge[2];
        for (int k = 0; k < 2; k++)
        {
            insideArc[k] = new BrepEdge(
                insideArcCurve[k], insideArcCurve[k].Domain, rims.InsideVertex[k], tangentInsideVertex[k]);
            outsideArc[k] = new BrepEdge(
                outsideArcCurve[k], outsideArcCurve[k].Domain, rims.OutsideVertex[k], tangentOutsideVertex[k]);
            insideWall[k] = Segment(tangentInsideVertex[k], tipInsideVertex[k]);
            outsideWall[k] = Segment(tangentOutsideVertex[k], tipOutsideVertex[k]);
            tipEnd[k] = Segment(tipInsideVertex[k], tipOutsideVertex[k]);
        }

        // A mitred end takes the SHARED cross-section instead of its own: the same five
        // edges and four vertices the partner flange uses, so the corner welds by index
        // rather than by tolerance and nothing lies in the miter plane.
        if (corner is { } cut)
        {
            int k = cut.End;
            tangentInsideVertex[k] = cut.Miter.TangentInside;
            tangentOutsideVertex[k] = cut.Miter.TangentOutside;
            tipInsideVertex[k] = cut.Miter.TipInside;
            tipOutsideVertex[k] = cut.Miter.TipOutside;
            insideArc[k] = cut.Miter.InsideArc;
            outsideArc[k] = cut.Miter.OutsideArc;
            insideWall[k] = cut.Miter.InsideWall;
            outsideWall[k] = cut.Miter.OutsideWall;
            tipEnd[k] = cut.Miter.Tip;
        }

        var insideTangentEdge = Segment(tangentInsideVertex[0], tangentInsideVertex[1]);
        var outsideTangentEdge = Segment(tangentOutsideVertex[0], tangentOutsideVertex[1]);
        var insideTipEdge = Segment(tipInsideVertex[0], tipInsideVertex[1]);
        var outsideTipEdge = Segment(tipOutsideVertex[0], tipOutsideVertex[1]);

        // Bend bands. ExtrudedSurface's outward normal is generator' x direction; the
        // generator's tangent leaves the sheet along +Outward, and Outward x AxisDirection
        // is -Inside, so the INSIDE band (material below it) extrudes BACK from the Q1 end
        // and the outside band forwards from Q0. Both keep the whole parameter rectangle,
        // so they tessellate on the natural grid.
        //
        // A MITRED end reaches PAST the span, because the corner's material runs to the
        // miter plane rather than stopping at the sheet's corner — so the band's own
        // parameter rectangle has to be lengthened to cover its loop. This is the
        // trim-the-domain-driven-surface rule that already forces a spliced neighbour to be
        // re-surfaced as a plane, running the other way: a plane has no rectangle to
        // escape, an extrusion does. With no corner the two vectors below are the
        // incumbent ones by REFERENCE, so nothing that already built can move.
        var (insideSurface, outsideSurface) = corner is null
            ? (new ExtrudedSurface(insideArcCurve[1], -span),
               new ExtrudedSurface(outsideArcCurve[0], span))
            : ExtendedBands(s0, s1, span, alongSpan, radius, thickness, corner);

        var bendInside = new BrepFace(
            insideSurface,
            [new BrepLoop(
            [
                Use(rims.Inside, rims.InsideVertex[0], rims.InsideVertex[1]),
                new BrepCoedge(insideArc[1], true),
                new BrepCoedge(insideTangentEdge, false),
                new BrepCoedge(insideArc[0], false),
            ])]);
        var bendOutside = new BrepFace(
            outsideSurface,
            [new BrepLoop(
            [
                new BrepCoedge(outsideArc[0], true),
                new BrepCoedge(outsideTangentEdge, true),
                new BrepCoedge(outsideArc[1], false),
                Use(rims.Outside, rims.OutsideVertex[1], rims.OutsideVertex[0]),
            ])]);

        // Wall cutouts: each is punched straight through, so the two planar wall faces gain
        // a hole loop apiece and the hole's own wall arrives as one band face per profile
        // segment. Built BEFORE the two faces because a BrepFace takes its whole loop list
        // at construction.
        var insideHoles = new List<BrepLoop>();
        var outsideHoles = new List<BrepLoop>();
        var bandFaces = new List<BrepFace>();
        var insideNormal = v;                      // = InsideNormalAfterBend
        foreach (var cutout in wallCutouts)
        {
            PunchWall(
                cutout, insideNormal, thickness,
                insideHoles, outsideHoles, bandFaces);
        }

        var flangeInside = new BrepFace(
            new PlaneSurface(tangentInside[0], alongSpan, u),
            [
                new BrepLoop(
                [
                    new BrepCoedge(insideTangentEdge, true),
                    new BrepCoedge(insideWall[1], true),
                    new BrepCoedge(insideTipEdge, false),
                    new BrepCoedge(insideWall[0], false),
                ]),
                .. insideHoles,
            ]);
        var flangeOutside = new BrepFace(
            new PlaneSurface(tangentOutside[0], u, alongSpan),
            [
                new BrepLoop(
                [
                    new BrepCoedge(outsideTangentEdge, false),
                    new BrepCoedge(outsideWall[0], true),
                    new BrepCoedge(outsideTipEdge, true),
                    new BrepCoedge(outsideWall[1], false),
                ]),
                .. outsideHoles,
            ]);
        // Plane ORIGINS come from the section rather than from the (possibly mitred) end
        // vertices: a plane's stored origin is an arbitrary in-plane point, and taking a
        // mitred one would make the tip plane pass exactly through the miter and only
        // nearly through the flange's own far corner.
        var flangeTip = new BrepFace(
            new PlaneSurface(tangentInside[0] + u * wallLength, alongSpan, -v),
            [new BrepLoop(
            [
                new BrepCoedge(insideTipEdge, true),
                new BrepCoedge(tipEnd[1], true),
                new BrepCoedge(outsideTipEdge, false),
                new BrepCoedge(tipEnd[0], false),
            ])]);

        // The flange's cross-section at each end, walked so that its own face's loop comes
        // out counter-clockwise about that face's outward normal: inside-rim first at the
        // Q0 end (normal -axis), outside-rim first at the Q1 end (normal +axis). The two
        // ends are mirror images, so listing them each in their OWN direction here is what
        // lets both consumers just materialize whichever chain they were handed.
        (BrepEdge, bool)[][] chains =
        [
            [
                (insideArc[0], true), (insideWall[0], true), (tipEnd[0], true),
                (outsideWall[0], false), (outsideArc[0], false),
            ],
            [
                (outsideArc[1], true), (outsideWall[1], true), (tipEnd[1], false),
                (insideWall[1], false), (insideArc[1], false),
            ],
        ];

        return new FlangeBuild(
            [bendInside, bendOutside, flangeInside, flangeOutside, flangeTip, .. bandFaces], chains);
    }

    /// <summary>
    /// Punches one closed profile clean through the flange's wall: a hole loop on each of
    /// the two planar wall faces plus one band face per profile segment.
    ///
    /// <para><b>Which face the profile lies on is DERIVED, not declared.</b> A cutout is
    /// quoted on the flange's own top face, which is the INSIDE face for an Up flange and
    /// the OUTSIDE one for a Down flange — so a caller stating it would have one more
    /// convention to get wrong. Instead the profile's own plane normal decides: with
    /// <paramref name="insideNormal"/> the wall's inside-face normal, a profile wound
    /// counter-clockwise about it is on the inside face and one wound the other way is on
    /// the outside, and the punch direction follows. That is the same rule
    /// <c>SolidFactory.Extrude</c> applies to a hole profile, read the other way round.</para>
    ///
    /// <para>The loop structure is <c>BuildSweptSolid</c>'s verbatim, which is the point:
    /// a hole through a flat plate is a swept solid's hole and nothing here is a second
    /// answer to how one is wound. The near face — the one the profile lies on — takes the
    /// REVERSED coedges (it is the sweep's bottom cap, whose normal opposes the sweep) and
    /// the far face the forward ones.</para>
    /// </summary>
    private static void PunchWall(
        Profile cutout, in Vector3d insideNormal, double thickness,
        List<BrepLoop> insideHoles, List<BrepLoop> outsideHoles, List<BrepFace> bandFaces)
    {
        // A hole's winding is opposite the material's, so a profile wound CCW about the
        // inside normal bounds material on the inside face: that is the face it lies on,
        // and the punch runs from it to the other one.
        bool onInside = cutout.Normal.Dot(insideNormal) > 0;
        var near = onInside ? insideHoles : outsideHoles;
        var far = onInside ? outsideHoles : insideHoles;
        var push = onInside ? -insideNormal * thickness : insideNormal * thickness;
        var translation = Matrix4d.CreateTranslation(push);
        var direction = push;

        if (cutout.IsSingleClosedCurve)
        {
            // A circular cutout: one closed generator, so the band is a single face with
            // two ring loops and the seam vertex is shared by both rings' closed edges.
            var generator = cutout.Segments[0];
            var domain = generator.Domain;
            var seamNear = new BrepVertex(generator.PointAt(domain.Start));
            var seamFar = new BrepVertex(translation.TransformPoint(seamNear.Position));
            var nearEdge = new BrepEdge(generator, domain, seamNear, seamNear);
            var farEdge = new BrepEdge(generator.Transformed(translation), domain, seamFar, seamFar);

            bandFaces.Add(new BrepFace(new ExtrudedSurface(generator, direction),
            [
                new BrepLoop([new BrepCoedge(nearEdge, sameSense: true)]),
                new BrepLoop([new BrepCoedge(farEdge, sameSense: false)]),
            ]));
            near.Add(new BrepLoop([new BrepCoedge(nearEdge, sameSense: false)]));
            far.Add(new BrepLoop([new BrepCoedge(farEdge, sameSense: true)]));
            return;
        }

        int n = cutout.Segments.Count;
        var nearVertices = new BrepVertex[n];
        var farVertices = new BrepVertex[n];
        for (int i = 0; i < n; i++)
        {
            var q = cutout.Segments[i].PointAt(cutout.Segments[i].Domain.Start);
            nearVertices[i] = new BrepVertex(q);
            farVertices[i] = new BrepVertex(translation.TransformPoint(q));
        }

        var nearEdges = new BrepEdge[n];
        var farEdges = new BrepEdge[n];
        var railEdges = new BrepEdge[n];
        for (int i = 0; i < n; i++)
        {
            var segment = cutout.Segments[i];
            int next = (i + 1) % n;
            nearEdges[i] = new BrepEdge(segment, segment.Domain, nearVertices[i], nearVertices[next]);
            farEdges[i] = new BrepEdge(
                segment.Transformed(translation), segment.Domain, farVertices[i], farVertices[next]);
            railEdges[i] = new BrepEdge(
                new Line3d(nearVertices[i].Position, farVertices[i].Position),
                Interval.Unit, nearVertices[i], farVertices[i]);
        }

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            bandFaces.Add(new BrepFace(new ExtrudedSurface(cutout.Segments[i], direction),
            [
                new BrepLoop(
                [
                    new BrepCoedge(nearEdges[i], sameSense: true),
                    new BrepCoedge(railEdges[next], sameSense: true),
                    new BrepCoedge(farEdges[i], sameSense: false),
                    new BrepCoedge(railEdges[i], sameSense: false),
                ]),
            ]));
        }

        var nearLoop = new List<BrepCoedge>(n);
        for (int i = n - 1; i >= 0; i--)
            nearLoop.Add(new BrepCoedge(nearEdges[i], sameSense: false));
        near.Add(new BrepLoop(nearLoop));

        var farLoop = new List<BrepCoedge>(n);
        for (int i = 0; i < n; i++)
            farLoop.Add(new BrepCoedge(farEdges[i], sameSense: true));
        far.Add(new BrepLoop(farLoop));
    }

    /// <summary>
    /// The two bend bands of a MITRED flange, whose parameter rectangles are lengthened to
    /// cover the corner's reach past the span.
    /// <para>The reach is MEASURED off the miter's own four points rather than assumed from
    /// the radius: at a square bend it is exactly <c>R + T</c>, but a shallower or steeper
    /// bend leans the wall and its mitred corner reaches further, so a constant would be
    /// right for one angle and wrong for the rest.</para>
    /// </summary>
    private static (ExtrudedSurface Inside, ExtrudedSurface Outside) ExtendedBands(
        in SheetBendSection s0, in SheetBendSection s1, in Vector3d span, in Vector3d alongSpan,
        double radius, double thickness, CornerEnd corner)
    {
        var at = corner.End == 0 ? s0.BendLinePoint : s1.BendLinePoint;
        // The arc's own semi-axis bounds every point of it, which its two ENDPOINTS do not:
        // past a square bend the furthest point is at t = 90°, strictly inside the range.
        double reach = Math.Abs(corner.Miter.Bisector.Dot(alongSpan));
        foreach (var p in (ReadOnlySpan<Vector3d>)
        [
            corner.Miter.TangentInside.Position, corner.Miter.TangentOutside.Position,
            corner.Miter.TipInside.Position, corner.Miter.TipOutside.Position,
        ])
        {
            reach = Math.Max(reach, Math.Abs((p - at).Dot(alongSpan)));
        }

        double low = corner.End == 0 ? reach : 0;
        double high = corner.End == 1 ? reach : 0;
        var stretched = span + alongSpan * (low + high);
        return (
            new ExtrudedSurface((s1 with { BendLinePoint = s1.BendLinePoint + alongSpan * high })
                .Arc(radius), -stretched),
            new ExtrudedSurface((s0 with { BendLinePoint = s0.BendLinePoint - alongSpan * low })
                .Arc(radius + thickness), stretched));
    }

    /// <summary>A coedge on an existing edge, with the sense that walks it
    /// <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static BrepCoedge Use(BrepEdge edge, BrepVertex from, BrepVertex to)
    {
        if (ReferenceEquals(edge.StartVertex, from) && ReferenceEquals(edge.EndVertex, to))
            return new BrepCoedge(edge, true);
        if (ReferenceEquals(edge.StartVertex, to) && ReferenceEquals(edge.EndVertex, from))
            return new BrepCoedge(edge, false);
        throw new InvalidOperationException(
            $"Edge from {edge.StartVertex.Position} to {edge.EndVertex.Position} does not join " +
            $"{from.Position} and {to.Position}.");
    }

    /// <summary>Coedges for a stored chain, in the order it is stored — the reversal that
    /// used to live here is baked into <c>EndChains[1]</c> by the code that knows which end
    /// is which.</summary>
    /// <summary>The same chain walked backwards: order reversed and every sense flipped.</summary>
    private static (BrepEdge Edge, bool SameSense)[] Reversed((BrepEdge Edge, bool SameSense)[] chain)
    {
        var flipped = new (BrepEdge, bool)[chain.Length];
        for (int i = 0; i < chain.Length; i++)
            flipped[i] = (chain[^(i + 1)].Edge, !chain[^(i + 1)].SameSense);
        return flipped;
    }

    private static IReadOnlyList<BrepCoedge> Materialize((BrepEdge Edge, bool SameSense)[] chain)
    {
        var coedges = new List<BrepCoedge>(chain.Length);
        foreach (var (edge, sense) in chain)
            coedges.Add(new BrepCoedge(edge, sense));
        return coedges;
    }

    // ---------------------------------------------------------------------- rewiring

    /// <summary>Closes the flange's two ends, each by the rule its own end asks for: a
    /// FLUSH end splices the cross-section into the neighbouring face's loop, an INSET one
    /// caps the flange and stubs the leftover wall. The two are independent — they share
    /// no coedge — which is what makes "flush at one end, inset at the other" an ordinary
    /// combination rather than a third path.</summary>
    private static void CloseEnds(
        FlangeSite site, FlangeBuild flange, Rims rims, in SheetBendSection section, List<BrepFace> faces,
        int cornerEnd)
    {
        for (int k = 0; k < 2; k++)
        {
            // A mitred end is closed by its PARTNER, not by a cap or a splice: the five
            // shared edges already have their second use from the other flange's faces.
            if (k == cornerEnd)
                continue;
            if (site.Flush[k])
                Splice(site.EndEdge[k], flange.EndChains[k], site, faces);
            else
                faces.AddRange(CapAndStub(site, flange, rims, section, k));
        }
    }

    /// <summary>
    /// Replaces the wall's end edge in the NEIGHBOURING face's loop with the flange's own
    /// cross-section, so the flange's side becomes part of that face.
    ///
    /// <para><b>Which way round the chain goes is MEASURED, not assumed.</b> A loop walks
    /// counter-clockwise about its face's outward normal, and the neighbour's normal is −a
    /// at the Q0 end of a CONVEX corner (the sheet's own outline) and +a at a REFLEX one
    /// (every corner of a hole, so every louvre's opening) — so the coedge being replaced
    /// runs the other way in the two cases. Reading its start vertex settles it with no
    /// convention to get wrong, which is also why
    /// <see cref="RequireSquareNeighbour"/> checks only perpendicularity.</para>
    /// </summary>
    private static void Splice(
        BrepEdge endEdge, (BrepEdge Edge, bool SameSense)[] chain, FlangeSite site, List<BrepFace> faces)
    {
        var use = endEdge.Uses.First(c => !ReferenceEquals(c.Loop.Face, site.Wall));
        var neighbour = use.Loop.Face;
        var first = chain[0];
        var chainStart = first.SameSense ? first.Edge.StartVertex : first.Edge.EndVertex;
        var walked = ReferenceEquals(use.StartVertex, chainStart) ? chain : Reversed(chain);
        use.Loop.ReplaceCoedge(use, [.. Materialize(walked)]);

        if (neighbour.Surface is PlaneSurface)
            return;
        int index = faces.FindIndex(f => ReferenceEquals(f, neighbour));
        if (index >= 0)
            faces[index] = new BrepFace(AsPlane(neighbour), [.. neighbour.Loops]);
    }

    /// <summary>
    /// The plane of a planar face, through <see cref="BrepQueries.Frame(BrepFace)"/> — the
    /// codebase's one answer to "what are this planar face's own directions", which keeps
    /// the surface's X/Y verbatim (so geometry built from the plane stays bit-identical to
    /// geometry built from the original) and orients Z OUTWARD.
    /// <para>Because the frame's Z is already the outward normal, a face rebuilt on it is
    /// never <see cref="BrepFace.IsReversed"/> — the reversal is folded into the frame.</para>
    /// </summary>
    private static PlaneSurface AsPlane(BrepFace face) =>
        new(face.Frame() ?? throw new NotSupportedException(
            "Expected a planar face at the end of the bend line."));

    /// <summary>One INSET end: the leftover wall becomes a stub, and the flange gets a
    /// planar cap closed by a new vertical edge between the two rims' split vertices. The
    /// two ends are mirror images of each other, so each loop is spelt out in its own
    /// traversal rather than derived from the other's — every sense comes from
    /// <see cref="Use"/>, so none of them is a boolean a reader has to check against the
    /// loop direction by hand.</summary>
    private static IReadOnlyList<BrepFace> CapAndStub(
        FlangeSite site, FlangeBuild flange, Rims rims, in SheetBendSection section, int end)
    {
        var n = section.Inside;
        var w = section.Outward;
        var vertical = Segment(rims.InsideVertex[end], rims.OutsideVertex[end]);
        var plane = AsPlane(site.Wall);

        if (end == 0)
        {
            // Stub loop, CCW about the wall's outward normal: up the wall's own end edge,
            // along the inside rim to the flange, down the new vertical, back outside.
            var stub = new BrepFace(plane, [new BrepLoop(
            [
                Use(site.EndEdge[0], site.WallCornerOutside[0], site.WallCornerInside[0]),
                Use(rims.InsideLeftover[0]!, site.WallCornerInside[0], rims.InsideVertex[0]),
                Use(vertical, rims.InsideVertex[0], rims.OutsideVertex[0]),
                Use(rims.OutsideLeftover[0]!, rims.OutsideVertex[0], site.WallCornerOutside[0]),
            ])]);
            var cap = new BrepFace(
                new PlaneSurface(site.Q[0], n, w),
                [new BrepLoop([.. Materialize(flange.EndChains[0]),
                    Use(vertical, rims.OutsideVertex[0], rims.InsideVertex[0])])]);
            return [stub, cap];
        }

        var stubHigh = new BrepFace(plane, [new BrepLoop(
        [
            Use(vertical, rims.OutsideVertex[1], rims.InsideVertex[1]),
            Use(rims.InsideLeftover[1]!, rims.InsideVertex[1], site.WallCornerInside[1]),
            Use(site.EndEdge[1], site.WallCornerInside[1], site.WallCornerOutside[1]),
            Use(rims.OutsideLeftover[1]!, site.WallCornerOutside[1], rims.OutsideVertex[1]),
        ])]);
        var capHigh = new BrepFace(
            new PlaneSurface(site.Q[1], w, n),
            [new BrepLoop([.. Materialize(flange.EndChains[1]),
                Use(vertical, rims.InsideVertex[1], rims.OutsideVertex[1])])]);
        return [stubHigh, capHigh];
    }

    private static BrepVertex OppositeEnd(BrepEdge edge, BrepVertex known) =>
        ReferenceEquals(edge.StartVertex, known) ? edge.EndVertex : edge.StartVertex;

    /// <summary>Drops a discarded face's coedges from the surviving edges' use lists — the
    /// same prune <c>TopologyEditor.SealSeams</c> opens with, narrowed to one face. Only
    /// the prune is wanted here: <c>SealSeams</c> goes on to unify vertices and merge seam
    /// edges at the 1e-7 seam tier, which would move geometry this file constructs
    /// exactly.</summary>
    private static void Detach(BrepFace face)
    {
        foreach (var loop in face.Loops)
        {
            foreach (var coedge in loop.Coedges)
                coedge.Edge.UsesInternal.Remove(coedge);
        }
    }
}
