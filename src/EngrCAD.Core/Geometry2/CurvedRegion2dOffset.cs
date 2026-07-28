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
        return delta > 0
            ? Grow(regions, delta, join, miterLimit)
            : Shrink(regions, -delta, join, miterLimit);
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
