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
/// <para><b>What is still refused by name</b> rather than approximated: bends along
/// non-straight edges (a curved bend line sweeps a developable band, not a cylinder),
/// CLOSED CORNERS and miters (two flanges meeting at a corner of the sheet — caught here
/// as a wall that is no longer four-sided), jogs, hems (a fold back through 180 degrees),
/// louvres, and any flange whose bend would interact with another feature. Bend RELIEFS
/// are not surgery at all: a relief notches the blank the sheet is extruded from, so a
/// relieved flange arrives here as an ordinary flange flush against the notch's own wall
/// (see <c>SheetMetalBody.BaseOutline</c> in EngrCAD.Modeling).</para>
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
        in Vector3d spanStart, in Vector3d spanEnd, double wallLength)
    {
        ArgumentNullException.ThrowIfNull(solid);
        Validate(section, wallLength);

        var n = section.Inside;
        var a = section.AxisDirection;

        // Order the span along +a so every loop below reads the same way round.
        var (q0, q1) = (spanEnd - spanStart).Dot(a) >= 0 ? (spanStart, spanEnd) : (spanEnd, spanStart);
        if ((q1 - q0).Dot(a) <= Weld)
            throw new ArgumentException(
                "The flange's span along the bend line must be positive.", nameof(spanEnd));

        // Everything checkable is checked HERE, before a single coedge moves. Rim surgery
        // rewrites loops in place, so a refusal that fired part-way would leave a
        // half-edited solid — the rule Filleting's edge-set features already follow.
        var site = Locate(solid, q0, q1, section);

        // Rims: the pieces of the two sheet-face edges the bend takes over, plus the
        // vertices at the flange's ends. A rim is split exactly at the ends that are INSET,
        // which is what creates those vertices; a flush end already has the wall's own
        // corner there.
        var rims = ResolveRims(site, n, section.Thickness, q0, q1, a);

        var flange = BuildFlange(section with { BendLinePoint = q0 }, q1 - q0, wallLength, rims);

        var faces = solid.Faces.Where(f => !ReferenceEquals(f, site.Wall)).ToList();
        faces.AddRange(flange.Faces);
        CloseEnds(site, flange, rims, section, faces);

        // The wall is consumed by the bend; detaching drops its coedges from the surviving
        // rims' use lists so they read exactly two uses again.
        Detach(site.Wall);

        var result = new BrepSolid([new BrepShell(faces)]);
        result.Validate();
        return result;
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
                "between 0 and 180. A 180-degree fold is a HEM, which v1 does not model (the flange would " +
                "lie back against the sheet, and those two faces would be coincident boolean input).");
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

    private static FlangeSite Locate(
        BrepSolid solid, in Vector3d q0, in Vector3d q1, in SheetBendSection section)
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
                "STRAIGHT edge of a planar sheet face; a curved bend line would sweep a developable band, " +
                "which v1 does not build.");
        }

        if (wall.Loops.Count != 1 || wall.OuterLoop.Coedges.Count != 4)
            throw new NotSupportedException(
                $"The side wall at this bend line has {wall.Loops.Count} loop(s) and " +
                $"{wall.OuterLoop.Coedges.Count} edge(s); v1 grows a flange only from a plain four-sided " +
                "wall. A wall that has already been reshaped by a neighbouring flange means the two flanges " +
                "meet at a CORNER, and closed corners, miters and reliefs are the follow-up to this rung.");

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
        if (flush[0])
            RequireSquareNeighbour(endAtQ0, -a, wall);
        if (flush[1])
            RequireSquareNeighbour(endAtQ1, a, wall);

        return new FlangeSite(
            bendEdge, opposite, wall, [endAtQ0, endAtQ1],
            [wallStart, wallEnd], [wallStartOutside, wallEndOutside], [q0, q1], flush);
    }

    /// <summary>The face across an end edge from the wall must be planar and perpendicular
    /// to the bend line, since the flange's cross-section has to lie IN its plane.</summary>
    private static void RequireSquareNeighbour(BrepEdge endEdge, in Vector3d expectedNormal, BrepFace wall)
    {
        var use = endEdge.Uses.FirstOrDefault(c => !ReferenceEquals(c.Loop.Face, wall))
            ?? throw new InvalidOperationException("The wall's end edge has no neighbouring face.");
        if (!HasNormal(use.Loop.Face, expectedNormal))
            throw new NotSupportedException(
                "A full-width flange needs the face at each end of its bend line to be planar and " +
                $"perpendicular to it (outward normal {expectedNormal}), so the flange's side can lie in that " +
                "plane. Inset the flange from both ends instead, or square up the neighbouring wall.");
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
        for (int k = 0; k < 2; k++)
        {
            if (!flush[k] && stub[k] <= Weld)
                throw new NotSupportedException(
                    $"An inset end of a flange must leave a positive stub of wall beyond it; this one leaves " +
                    $"{stub[k]:g6} of an edge {Math.Abs(axisEnd - axisStart):g6} long. Make the flange flush " +
                    "with that end of its edge, or move it further in.");
        }
        if (axisSpan[1] - axisSpan[0] <= Weld)
            throw new NotSupportedException("The flange's span along the bend line must be positive.");

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

    private static FlangeBuild BuildFlange(
        in SheetBendSection s0, in Vector3d span, double wallLength, Rims rims)
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

        var insideTangentEdge = Segment(tangentInsideVertex[0], tangentInsideVertex[1]);
        var outsideTangentEdge = Segment(tangentOutsideVertex[0], tangentOutsideVertex[1]);
        var insideTipEdge = Segment(tipInsideVertex[0], tipInsideVertex[1]);
        var outsideTipEdge = Segment(tipOutsideVertex[0], tipOutsideVertex[1]);

        // Bend bands. ExtrudedSurface's outward normal is generator' x direction; the
        // generator's tangent leaves the sheet along +Outward, and Outward x AxisDirection
        // is -Inside, so the INSIDE band (material below it) extrudes BACK from the Q1 end
        // and the outside band forwards from Q0. Both keep the whole parameter rectangle,
        // so they tessellate on the natural grid.
        var bendInside = new BrepFace(
            new ExtrudedSurface(insideArcCurve[1], -span),
            [new BrepLoop(
            [
                Use(rims.Inside, rims.InsideVertex[0], rims.InsideVertex[1]),
                new BrepCoedge(insideArc[1], true),
                new BrepCoedge(insideTangentEdge, false),
                new BrepCoedge(insideArc[0], false),
            ])]);
        var bendOutside = new BrepFace(
            new ExtrudedSurface(outsideArcCurve[0], span),
            [new BrepLoop(
            [
                new BrepCoedge(outsideArc[0], true),
                new BrepCoedge(outsideTangentEdge, true),
                new BrepCoedge(outsideArc[1], false),
                Use(rims.Outside, rims.OutsideVertex[1], rims.OutsideVertex[0]),
            ])]);

        var flangeInside = new BrepFace(
            new PlaneSurface(tangentInside[0], alongSpan, u),
            [new BrepLoop(
            [
                new BrepCoedge(insideTangentEdge, true),
                new BrepCoedge(insideWall[1], true),
                new BrepCoedge(insideTipEdge, false),
                new BrepCoedge(insideWall[0], false),
            ])]);
        var flangeOutside = new BrepFace(
            new PlaneSurface(tangentOutside[0], u, alongSpan),
            [new BrepLoop(
            [
                new BrepCoedge(outsideTangentEdge, false),
                new BrepCoedge(outsideWall[0], true),
                new BrepCoedge(outsideTipEdge, true),
                new BrepCoedge(outsideWall[1], false),
            ])]);
        var flangeTip = new BrepFace(
            new PlaneSurface(tipInsideVertex[0].Position, alongSpan, -v),
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
            [bendInside, bendOutside, flangeInside, flangeOutside, flangeTip], chains);
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
        FlangeSite site, FlangeBuild flange, Rims rims, in SheetBendSection section, List<BrepFace> faces)
    {
        for (int k = 0; k < 2; k++)
        {
            if (site.Flush[k])
                Splice(site.EndEdge[k], Materialize(flange.EndChains[k]), site, faces);
            else
                faces.AddRange(CapAndStub(site, flange, rims, section, k));
        }
    }

    private static void Splice(
        BrepEdge endEdge, IReadOnlyList<BrepCoedge> chain, FlangeSite site, List<BrepFace> faces)
    {
        var use = endEdge.Uses.First(c => !ReferenceEquals(c.Loop.Face, site.Wall));
        var neighbour = use.Loop.Face;
        use.Loop.ReplaceCoedge(use, [.. chain]);

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
