namespace EngrCAD.Core.Geometry2;

/// <summary>
/// EXACT offsetting (inflate / deflate, Minkowski sum with a disk for
/// <see cref="OffsetJoin.Round"/>) of a <see cref="CurvedRegion2d"/> — the curved
/// counterpart of <see cref="Region2dOffset"/>, and the same algorithm rather than a second
/// one.
///
/// <para><b>Union of primitives, as before.</b> An outward offset by d is
/// <c>R ∪ (⋃ edge slabs) ∪ (⋃ corner joins)</c>: a point outside R but within d of it has a
/// nearest boundary point either interior to an edge (then it lies in that edge's slab) or
/// at a vertex (then in that vertex's join). What changes is that both families of
/// primitive are now exact:</para>
/// <list type="bullet">
/// <item>a straight edge's slab is still a rectangle;</item>
/// <item>a circular edge's slab is an ANNULAR SECTOR — the edge itself, two radial segments
/// and the offset arc — which is exactly the set of points within d of that arc on its
/// outward side, with no flattening anywhere. When an inward offset reaches or passes the
/// centre the sector degenerates to a pie slice of radius r, which is again exact: every
/// point of that slice is within r ≤ d of the arc;</item>
/// <item>a <see cref="OffsetJoin.Round"/> join is a circular SECTOR, so the round joins that
/// used to be inscribed polygonal fans are now the true offset arcs. The inscribed-arc
/// contract of <see cref="Region2dOffset"/> is therefore not merely honoured here, it is
/// retired: an exactly-offset arc is neither inside nor outside the true offset, it IS the
/// true offset.</item>
/// </list>
///
/// <para><b>Inward offsets are outward offsets of the complement</b> —
/// <c>B \ dilate(B \ R, d)</c> with B the bounds grown by 3d — so self-intersection falls
/// out and a shrunk region may split into several regions or vanish, exactly as in the
/// polygonal path.</para>
///
/// <para><b>Full-turn edges are halved before a slab is raised.</b> The annular sector of a
/// whole circle is an annulus, which is a region with a HOLE rather than a simple loop; two
/// half-arcs give two ordinary sectors whose union is the same set.</para>
/// </summary>
public static class CurvedRegion2dOffset
{
    /// <summary>
    /// Offsets one region by <paramref name="delta"/>: positive inflates, negative deflates,
    /// zero returns the region unchanged. The result may be several regions or none.
    /// </summary>
    /// <param name="region">The region to offset.</param>
    /// <param name="delta">Signed offset distance (positive = outward).</param>
    /// <param name="join">Corner style — see <see cref="OffsetJoin"/>.</param>
    /// <param name="miterLimit">For <see cref="OffsetJoin.Miter"/>: the largest ratio of
    /// corner extension to <paramref name="delta"/> before the corner is chamfered.</param>
    public static IReadOnlyList<CurvedRegion2d> Offset(
        CurvedRegion2d region, double delta, OffsetJoin join = OffsetJoin.Round,
        double miterLimit = Region2dOffset.DefaultMiterLimit)
    {
        ArgumentNullException.ThrowIfNull(region);
        return Offset([region], delta, join, miterLimit);
    }

    /// <summary>
    /// Offsets a whole region set at once (the members are read as one area, so parts that
    /// grow into each other merge).
    /// </summary>
    public static IReadOnlyList<CurvedRegion2d> Offset(
        IReadOnlyList<CurvedRegion2d> regions, double delta, OffsetJoin join = OffsetJoin.Round,
        double miterLimit = Region2dOffset.DefaultMiterLimit)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (!(miterLimit >= 1))
            throw new ArgumentOutOfRangeException(nameof(miterLimit), "The miter limit must be at least 1.");
        if (!double.IsFinite(delta))
            throw new ArgumentOutOfRangeException(nameof(delta));

        // Deliberate exact-zero test: "no offset requested" is a caller contract.
        if (delta == 0 || regions.Count == 0)
            return [.. regions];
        foreach (var region in regions)
        {
            foreach (var loop in region.AllLoops())
            {
                foreach (var edge in loop)
                    RequireOffsettable(edge, nameof(regions));
            }
        }
        return delta > 0
            ? Grow(regions, delta, join, miterLimit)
            : Shrink(regions, -delta, join, miterLimit);
    }

    /// <summary>
    /// Strokes an open chain of lines and arcs into a region of the given
    /// <paramref name="width"/> — a toolpath's swept footprint, a slot from its centre line,
    /// an SVG stroke whose path carries `A` commands. The curved twin of
    /// <see cref="Region2dOffset.Stroke"/>, and the SAME union-of-primitives construction:
    /// one FULL-WIDTH slab per edge, corner joins offered on both sides of every interior
    /// joint, and end caps. Self-crossings need no special handling — the overlap is simply
    /// covered once — and a path that loops encloses a hole.
    ///
    /// <para><b>What is exact here that is not exact there.</b> A straight edge's slab is
    /// still a rectangle; an arc's is the ANNULAR SECTOR between radii r ± width/2 over the
    /// arc's own angular span, which is precisely the set of points whose nearest point on
    /// the path is interior to that arc. Round joins are exact circular sectors and round
    /// caps exact half-discs. So with <see cref="StrokeCap.Round"/> and
    /// <see cref="OffsetJoin.Round"/> the result IS the path's Minkowski sum with a disc of
    /// radius width/2 — not "short of it by the inscribed-arc sagitta", which is the
    /// qualification the polygonal twin has to carry.</para>
    ///
    /// <para><b>The band may swallow the centre.</b> When width/2 ≥ r the inner radius is
    /// non-positive and the slab becomes the pie SECTOR of radius r + width/2, which is again
    /// exact: every point of that sector is at radius ≤ r + width/2 and ≥ 0, so its distance
    /// to the circle is at most max(r, width/2) = width/2.</para>
    ///
    /// <para><b>A chain that returns to its start is stroked as a CIRCUIT</b> — the closing
    /// joint gets its joins and no caps are added. This is where the two twins' contracts
    /// differ, and the reason is the input vocabulary rather than a change of mind: the
    /// polygonal `Stroke` takes POINTS, where closure can only be spelled by repeating the
    /// first one, while a chain of edges makes closure structural (edge n−1's end IS edge 0's
    /// start, at the same weld tier the chain's own continuity is checked at). It matters for
    /// every style except <see cref="OffsetJoin.Round"/> with <see cref="StrokeCap.Round"/>,
    /// where the two agree as sets because a full disc at the closing vertex contains the
    /// join wedge; a butt-capped circuit would otherwise carry a notch, and a mitered one
    /// would lose its last corner.</para>
    /// </summary>
    /// <param name="path">The chain, in order: each edge's end must be the next edge's start
    /// to within <see cref="CurvedRegion2d.BoundaryTolerance"/>. Zero-length edges are
    /// dropped (they carry no direction); a gap is refused by name.</param>
    /// <param name="width">Full stroke width (&gt; 0).</param>
    /// <param name="cap">End treatment — see <see cref="StrokeCap"/>. Ignored on a circuit.</param>
    /// <param name="join">Corner style at interior joints.</param>
    /// <param name="miterLimit">See <see cref="Offset(CurvedRegion2d, double, OffsetJoin, double)"/>.</param>
    public static IReadOnlyList<CurvedRegion2d> Stroke(
        IReadOnlyList<CurvedEdge2d> path, double width, StrokeCap cap = StrokeCap.Round,
        OffsetJoin join = OffsetJoin.Round, double miterLimit = Region2dOffset.DefaultMiterLimit)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!(width > 0) || !double.IsFinite(width))
            throw new ArgumentOutOfRangeException(nameof(width), "The stroke width must be positive.");
        if (!(miterLimit >= 1))
            throw new ArgumentOutOfRangeException(nameof(miterLimit), "The miter limit must be at least 1.");

        // Exact-zero semantic test, as the polygonal twin's duplicate compaction: an edge of
        // no length has no direction, so it can contribute neither a slab nor a joint normal.
        // Dropping one cannot break the chain — its start and end are the same point.
        var edges = new List<CurvedEdge2d>(path.Count);
        foreach (var edge in path)
        {
            RequireOffsettable(edge, nameof(path));
            if (edge.Length != 0)
                edges.Add(edge);
        }
        if (edges.Count == 0)
            throw new ArgumentException("A stroked path needs at least one edge of non-zero length.", nameof(path));
        for (int i = 1; i < edges.Count; i++)
        {
            double gap = edges[i - 1].End.DistanceTo(edges[i].Start);
            if (gap > CurvedRegion2d.BoundaryTolerance)
            {
                throw new ArgumentException(
                    $"The stroked path is not a chain: edge {i - 1} ends at ({edges[i - 1].End.X}, "
                    + $"{edges[i - 1].End.Y}) and edge {i} starts at ({edges[i].Start.X}, "
                    + $"{edges[i].Start.Y}), a gap of {gap}. A stroke is the swept footprint of ONE "
                    + "path; stroke the pieces separately and union the results.", nameof(path));
            }
        }

        // Closure is read off the same weld tier the continuity check just used, so "is this a
        // circuit" and "is this a chain" cannot be answered by two different tolerances.
        bool closed = edges[^1].End.DistanceTo(edges[0].Start) <= CurvedRegion2d.BoundaryTolerance;
        double half = width / 2;
        var primitives = new List<CurvedRegion2d>();

        foreach (var edge in edges)
            AddStrokeSlab(edge, half, primitives);

        // Interior joints: offer the fill on BOTH sides. The turn's outer side has the
        // genuine gap; the inner wedge already lies inside its own slabs, so the union is
        // unchanged there — and an exact reversal fills both, which is the round nose a
        // doubled-back path needs.
        for (int i = closed ? 0 : 1; i < edges.Count; i++)
        {
            var incoming = edges[(i - 1 + edges.Count) % edges.Count];
            var left0 = incoming.TangentAt(1).Perpendicular;
            var left1 = edges[i].TangentAt(0).Perpendicular;
            var vertex = edges[i].Start;
            // Each side is offered in BOTH orders, because AddCornerJoin's gate admits a pair
            // only when its sweep is counter-clockwise — so which ORDER spells a side's
            // sector depends on which way the path turns. Exactly two of these four survive
            // the gate (one per side) at any real joint, and the inner one lies inside its
            // own slabs so the union is unchanged.
            //
            // Offering only (left0, left1) and (-left0, -left1) is WRONG, and silently:
            // Cross(-a, -b) == Cross(a, b) EXACTLY, so negating both normals does not flip
            // the turn and the two calls always agree. At a left turn both are proper sectors
            // and the answer is right; at a RIGHT turn both are clockwise and both are
            // refused, so every right-hand joint loses its outer fill.
            AddCornerJoin(vertex, left0, left1, half, join, miterLimit, primitives);
            AddCornerJoin(vertex, left1, left0, half, join, miterLimit, primitives);
            AddCornerJoin(vertex, -left0, -left1, half, join, miterLimit, primitives);
            AddCornerJoin(vertex, -left1, -left0, half, join, miterLimit, primitives);
        }

        if (!closed && cap != StrokeCap.Butt)
        {
            AddCap(edges[0].Start, -edges[0].TangentAt(0), half, cap, primitives);
            AddCap(edges[^1].End, edges[^1].TangentAt(1), half, cap, primitives);
        }

        return CurvedRegion2dBoolean.UnionAll(primitives);
    }

    /// <summary>
    /// Refuses a cubic BY NAME rather than offsetting it wrongly.
    ///
    /// <para>This is a statement about geometry, not a gap in the implementation: the offset
    /// of a cubic Bézier is not a Bézier of any degree — it is an algebraic curve of degree
    /// 10 — so there is no exact slab to raise and no honest way for this tier to keep its
    /// brand. The boolean and the arrangement carry cubics exactly; the OFFSET is where the
    /// polynomial family stops being closed, and the message says which door to use instead
    /// rather than leaving the caller to discover a wrong answer.</para>
    /// </summary>
    private static void RequireOffsettable(in CurvedEdge2d edge, string parameterName)
    {
        if (!edge.IsBezier)
            return;
        throw new ArgumentException(
            "The exact offset carries lines and circular arcs only, and this region contains a "
            + $"cubic Bézier ({edge}). A cubic's offset is an algebraic curve of degree 10, not a "
            + "Bézier, so there is no exact primitive to raise for it. Flatten first — "
            + "CurvedRegion2d.ToRegion(chordTolerance) then Region2dOffset, or Sketch.Offset — "
            + "which states the chord tolerance it spends.", parameterName);
    }

    private static IReadOnlyList<CurvedRegion2d> Grow(
        IReadOnlyList<CurvedRegion2d> regions, double delta, OffsetJoin join, double miterLimit)
    {
        var primitives = new List<CurvedRegion2d>(regions);
        foreach (var region in regions)
        {
            foreach (var loop in region.AllLoops())
                AddLoopPrimitives(loop, region.IsCounterClockwise, delta, join, miterLimit, primitives);
        }
        return CurvedRegion2dBoolean.UnionAll(primitives);
    }

    private static IReadOnlyList<CurvedRegion2d> Shrink(
        IReadOnlyList<CurvedRegion2d> regions, double delta, OffsetJoin join, double miterLimit)
    {
        var bounds = Aabb.Empty;
        foreach (var region in regions)
            bounds = bounds.Union(region.Bounds);
        double margin = 3 * delta;
        var frame = new CurvedRegion2d([
            CurvedEdge2d.Line((bounds.Min.X - margin, bounds.Min.Y - margin), (bounds.Max.X + margin, bounds.Min.Y - margin)),
            CurvedEdge2d.Line((bounds.Max.X + margin, bounds.Min.Y - margin), (bounds.Max.X + margin, bounds.Max.Y + margin)),
            CurvedEdge2d.Line((bounds.Max.X + margin, bounds.Max.Y + margin), (bounds.Min.X - margin, bounds.Max.Y + margin)),
            CurvedEdge2d.Line((bounds.Min.X - margin, bounds.Max.Y + margin), (bounds.Min.X - margin, bounds.Min.Y - margin)),
        ]);

        var complement = CurvedRegion2dBoolean.Difference([frame], regions);
        if (complement.Count == 0)
            return [];   // R covers its own bounds: impossible, but never divide by luck
        var grown = Grow(complement, delta, join, miterLimit);
        return CurvedRegion2dBoolean.Difference([frame], grown);
    }

    // ---- the primitives ----

    private static void AddLoopPrimitives(
        IReadOnlyList<CurvedEdge2d> loop, bool materialOnLeft, double delta,
        OffsetJoin join, double miterLimit, List<CurvedRegion2d> into)
    {
        foreach (var edge in loop)
            AddSlab(edge, materialOnLeft, delta, into);

        int count = loop.Count;
        for (int i = 0; i < count; i++)
        {
            var previous = loop[(i - 1 + count) % count];
            AddCornerJoin(
                loop[i].Start,
                OutwardNormal(previous.TangentAt(1), materialOnLeft),
                OutwardNormal(loop[i].TangentAt(0), materialOnLeft),
                delta, join, miterLimit, into);
        }
    }

    /// <summary>The band swept by moving one edge <paramref name="delta"/> along its outward
    /// normal: a rectangle for a segment, an annular sector (or a pie slice) for an arc.</summary>
    private static void AddSlab(
        in CurvedEdge2d edge, bool materialOnLeft, double delta, List<CurvedRegion2d> into)
    {
        if (!edge.IsArc)
        {
            var shift = OutwardNormal(edge.TangentAt(0), materialOnLeft) * delta;
            AddIfNonDegenerate(
                [
                    CurvedEdge2d.Line(edge.Start, edge.End),
                    CurvedEdge2d.Line(edge.End, edge.End + shift),
                    CurvedEdge2d.Line(edge.End + shift, edge.Start + shift),
                    CurvedEdge2d.Line(edge.Start + shift, edge.Start),
                ], into);
            return;
        }

        // A full turn's sector would be an annulus (a region with a hole, not a simple loop),
        // so halve until every piece spans at most half a turn.
        if (Math.Abs(edge.SweepAngle) > Math.PI)
        {
            AddSlab(edge.Sub(0, 0.5), materialOnLeft, delta, into);
            AddSlab(edge.Sub(0.5, 1), materialOnLeft, delta, into);
            return;
        }

        // Outward is radially away from the centre exactly when the turn and the material
        // side agree; the offset radius follows.
        int outward = Math.Sign(edge.SweepAngle) * (materialOnLeft ? 1 : -1);
        double offsetRadius = edge.Radius + outward * delta;
        double a0 = edge.StartAngle;
        double a1 = edge.StartAngle + edge.SweepAngle;

        if (offsetRadius <= 0)
        {
            // The offset reaches the centre: every point of the pie slice is within
            // r <= delta of the arc, so the slice IS the swept set.
            AddIfNonDegenerate(
                [
                    edge,
                    CurvedEdge2d.Line(edge.End, edge.Center),
                    CurvedEdge2d.Line(edge.Center, edge.Start),
                ], into);
            return;
        }

        var far1 = OnCircle(edge.Center, offsetRadius, a1);
        var far0 = OnCircle(edge.Center, offsetRadius, a0);
        AddIfNonDegenerate(
            [
                edge,
                CurvedEdge2d.Line(edge.End, far1),
                CurvedEdge2d.Arc(edge.Center, offsetRadius, a1, -edge.SweepAngle).WithEndpoints(far1, far0),
                CurvedEdge2d.Line(far0, edge.Start),
            ], into);
    }

    /// <summary>
    /// The FULL-WIDTH band swept by one edge of a stroked path: the rectangle
    /// <paramref name="half"/> either side of a segment, or the annular sector between radii
    /// r ± <paramref name="half"/> over an arc's own angular span. Unlike
    /// <see cref="AddSlab"/> there is no material side — a stroke has none — so the band is
    /// two-sided and the arc case can reach the centre.
    /// </summary>
    private static void AddStrokeSlab(in CurvedEdge2d edge, double half, List<CurvedRegion2d> into)
    {
        if (!edge.IsArc)
        {
            var side = edge.TangentAt(0).Perpendicular * half;
            AddIfNonDegenerate(
                [
                    CurvedEdge2d.Line(edge.Start + side, edge.End + side),
                    CurvedEdge2d.Line(edge.End + side, edge.End - side),
                    CurvedEdge2d.Line(edge.End - side, edge.Start - side),
                    CurvedEdge2d.Line(edge.Start - side, edge.Start + side),
                ], into);
            return;
        }

        // The same halving rule AddSlab needs, for the same reason: the annular sector of a
        // whole turn is an ANNULUS — a region with a hole rather than a simple loop.
        if (Math.Abs(edge.SweepAngle) > Math.PI)
        {
            AddStrokeSlab(edge.Sub(0, 0.5), half, into);
            AddStrokeSlab(edge.Sub(0.5, 1), half, into);
            return;
        }

        double a0 = edge.StartAngle;
        double a1 = edge.StartAngle + edge.SweepAngle;
        double outerRadius = edge.Radius + half;
        var outerStart = OnCircle(edge.Center, outerRadius, a0);
        var outerEnd = OnCircle(edge.Center, outerRadius, a1);
        var outerArc = CurvedEdge2d.Arc(edge.Center, outerRadius, a0, edge.SweepAngle)
            .WithEndpoints(outerStart, outerEnd);

        double innerRadius = edge.Radius - half;
        if (innerRadius <= 0)
        {
            // The band swallows the centre, so the inner rim is gone and the pie SECTOR is
            // the whole swept set: every point of it sits at radius between 0 and r + half,
            // i.e. within max(r, half) = half of the circle.
            AddIfNonDegenerate(
                [
                    outerArc,
                    CurvedEdge2d.Line(outerEnd, edge.Center),
                    CurvedEdge2d.Line(edge.Center, outerStart),
                ], into);
            return;
        }

        var innerEnd = OnCircle(edge.Center, innerRadius, a1);
        var innerStart = OnCircle(edge.Center, innerRadius, a0);
        AddIfNonDegenerate(
            [
                outerArc,
                CurvedEdge2d.Line(outerEnd, innerEnd),
                CurvedEdge2d.Arc(edge.Center, innerRadius, a1, -edge.SweepAngle).WithEndpoints(innerEnd, innerStart),
                CurvedEdge2d.Line(innerStart, outerStart),
            ], into);
    }

    /// <summary>
    /// A cap reaching past <paramref name="end"/> along the unit <paramref name="outward"/>:
    /// an EXACT half-disc closed by its own diameter, or a half-width square. The polygonal
    /// twin has to inscribe a polygonal fan here; this one does not, which is the whole
    /// reason the round-capped stroke is the Minkowski sum rather than an approximation to it.
    /// </summary>
    private static void AddCap(
        in Vector2d end, in Vector2d outward, double half, StrokeCap cap, List<CurvedRegion2d> into)
    {
        var side = outward.Perpendicular * half;
        if (cap == StrokeCap.Square)
        {
            var reach = outward * half;
            AddIfNonDegenerate(
                [
                    CurvedEdge2d.Line(end + side, end + side + reach),
                    CurvedEdge2d.Line(end + side + reach, end - side + reach),
                    CurvedEdge2d.Line(end - side + reach, end - side),
                    CurvedEdge2d.Line(end - side, end + side),
                ], into);
            return;
        }

        // Round: sweep +pi from -side, which passes through the outward direction at the
        // half-way point (Perpendicular rotates +90 degrees, so side rotated +90 is -outward).
        var from = end - side;
        var to = end + side;
        AddIfNonDegenerate(
            [
                CurvedEdge2d.Arc(end, half, Math.Atan2(-side.Y, -side.X), Math.PI).WithEndpoints(from, to),
                CurvedEdge2d.Line(to, from),
            ], into);
    }

    private static Vector2d OutwardNormal(in Vector2d direction, bool materialOnLeft) =>
        materialOnLeft ? new Vector2d(direction.Y, -direction.X) : direction.Perpendicular;

    /// <summary>
    /// Fills the wedge left open at a vertex between two offset edges. Nothing is needed
    /// when the boundary turns away from the outward side (the two slabs already overlap
    /// there) or runs straight through — including the TANGENT-CONTINUOUS joint of a line
    /// meeting an arc, the commonest joint in a sketch, whose two outward normals are equal
    /// so the exact-zero cross test skips it with no primitive at all.
    /// </summary>
    private static void AddCornerJoin(
        in Vector2d vertex, in Vector2d fromNormal, in Vector2d toNormal, double delta,
        OffsetJoin join, double miterLimit, List<CurvedRegion2d> into)
    {
        double cross = fromNormal.Cross(toNormal);
        double dot = fromNormal.Dot(toNormal);
        // Exact-zero semantic tests, as Region2dOffset: cross == 0 with dot > 0 is a
        // straight-through (or tangent-continuous) joint; cross == 0 with dot < 0 is a spike.
        if (cross < 0 || (cross == 0 && dot > 0))
            return;
        double sweep = Math.Atan2(cross, dot);
        if (sweep <= 0)
            sweep = Math.PI;

        var start = vertex + fromNormal * delta;
        var end = vertex + toNormal * delta;

        if (join == OffsetJoin.Miter)
        {
            // The apex divides by LengthSquared, never by Length squared: at a right angle
            // those are exactly 2 and 2.0000000000000004, and the few ulps are enough to
            // tilt the apex off both offset lines so the collinear joints stop merging.
            var sum = fromNormal + toNormal;
            double sumLengthSquared = sum.LengthSquared;
            if (sumLengthSquared > 0 && 2 / Math.Sqrt(sumLengthSquared) <= miterLimit)
            {
                var apex = vertex + sum * (2 * delta / sumLengthSquared);
                AddIfNonDegenerate(
                    [
                        CurvedEdge2d.Line(vertex, start),
                        CurvedEdge2d.Line(start, apex),
                        CurvedEdge2d.Line(apex, end),
                        CurvedEdge2d.Line(end, vertex),
                    ], into);
                return;
            }
            join = OffsetJoin.Chamfer;
        }

        if (join == OffsetJoin.Chamfer)
        {
            AddIfNonDegenerate(
                [
                    CurvedEdge2d.Line(vertex, start),
                    CurvedEdge2d.Line(start, end),
                    CurvedEdge2d.Line(end, vertex),
                ], into);
            return;
        }

        // Round: an EXACT circular sector. This is the payoff of the curved tier — the
        // polygonal path had to inscribe a polygonal fan here.
        double a0 = Math.Atan2(fromNormal.Y, fromNormal.X);
        AddIfNonDegenerate(
            [
                CurvedEdge2d.Line(vertex, start),
                CurvedEdge2d.Arc(vertex, delta, a0, sweep).WithEndpoints(start, end),
                CurvedEdge2d.Line(end, vertex),
            ], into);
    }

    private static Vector2d OnCircle(in Vector2d center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    /// <summary>A primitive that encloses no area contributes nothing to the union and would
    /// be rejected by <see cref="CurvedRegion2d"/>'s constructor.</summary>
    private static void AddIfNonDegenerate(IReadOnlyList<CurvedEdge2d> loop, List<CurvedRegion2d> into)
    {
        // Exact-zero test on the enclosed area: the region type's own admission rule.
        if (CurvedRegion2d.SignedArea(loop) != 0)
            into.Add(new CurvedRegion2d(loop));
    }
}
