namespace EngrCAD.Core.Geometry2;

/// <summary>How the corners of an offset region are closed — the classic Clipper join
/// styles, and OpenSCAD's <c>offset(r=…)</c> versus <c>offset(delta=…, chamfer=…)</c>.</summary>
public enum OffsetJoin
{
    /// <summary>Arcs of the exact offset circle, flattened to the arc tolerance
    /// (OpenSCAD's <c>offset(r=…)</c>). The only style that is a true Minkowski sum with a
    /// disk, and the only one that survives an exact 180-degree reversal.</summary>
    Round,

    /// <summary>The two offset edges extended to their intersection — a sharp corner that
    /// preserves the original vertex direction (OpenSCAD's <c>offset(delta=…)</c>).
    /// Degenerates to <see cref="Chamfer"/> past the miter limit.</summary>
    Miter,

    /// <summary>A single straight bevel across the corner
    /// (OpenSCAD's <c>offset(delta=…, chamfer=true)</c>).</summary>
    Chamfer,
}

/// <summary>How the ends of a stroked open path are closed — SVG's
/// <c>stroke-linecap</c> vocabulary.</summary>
public enum StrokeCap
{
    /// <summary>The stroke stops flat exactly at the end point.</summary>
    Butt,

    /// <summary>A half-disc of the stroke's own half-width — the true Minkowski end,
    /// and the only cap under which a stroke is exactly the path dilated by a disk.</summary>
    Round,

    /// <summary>A square extension of half the width past the end point.</summary>
    Square,
}

/// <summary>
/// Polygon offsetting (inflate / deflate, Minkowski sum with a disk for
/// <see cref="OffsetJoin.Round"/>) — the geometry behind shells, pockets, clearances and
/// cutter-compensated toolpaths.
///
/// <para><b>How it works — union of primitives, not edge chasing.</b> An outward offset by
/// d is exactly
/// <c>R ∪ (⋃ edge slabs) ∪ (⋃ corner joins)</c>: for a point p outside R but within d of it,
/// the nearest boundary point is either interior to an edge (then p lies in that edge's
/// slab, the rectangle swept by translating the edge d along its outward normal) or is a
/// vertex (then p lies in that vertex's corner primitive). Every primitive is a small convex
/// polygon, and they are combined by <see cref="Region2dBoolean.UnionAll"/>. That is the
/// whole algorithm — and it is why offsetting had to wait for the arrangement-based
/// boolean.</para>
///
/// <para><b>Self-intersection falls out.</b> The naive "move each edge and re-join" scheme
/// produces the notorious inverted loops when an inward offset eats through a thin neck;
/// here there is nothing to invert. The union of overlapping primitives is just their union,
/// so a deflated region may split into several regions or vanish entirely, and the caller
/// gets a list (possibly empty) either way.</para>
///
/// <para><b>Inward offsets are outward offsets of the complement.</b> Erosion is
/// <c>B \ dilate(B \ R, d)</c> for any box B whose boundary is farther than d from R (this
/// implementation uses R's bounds grown by 3d). No second algorithm, and no special cases
/// for necks, islands, or holes turning into merged voids.</para>
///
/// <para><b>Fidelity.</b> Regions are polygonal, so a round join is a polygonal arc whose
/// vertices sit EXACTLY on the true offset circle and whose chords fall inside it: the
/// result is contained in the true offset, short of it by at most the sagitta
/// <paramref name="arcTolerance"/>. That matches <c>Sketch.ToRegions</c>' inscribed
/// flattening, so a circle offset outward by d has the area of an inscribed polygon,
/// slightly under π(r+d)². Straight edges are exact under every join style.</para>
/// </summary>
public static class Region2dOffset
{
    /// <summary>Default arc flattening tolerance: no chord of a round join deviates more
    /// than 1 µm (model units are millimetres by convention) from the true offset circle.
    /// Matches <c>Sketch.DefaultChordTolerance</c> — a default parameter must be a
    /// constant, so the two cannot share one symbol.</summary>
    public const double DefaultArcTolerance = 1e-3;

    /// <summary>Default miter limit: a mitered corner may extend at most twice the offset
    /// distance from the original vertex before it is cut back to a chamfer (Clipper's
    /// default, and the same convention as SVG's <c>stroke-miterlimit</c>).</summary>
    public const double DefaultMiterLimit = 2.0;

    /// <summary>
    /// Offsets one region by <paramref name="delta"/>: positive inflates, negative deflates,
    /// zero returns the region unchanged. The result may be several regions or none.
    /// </summary>
    /// <param name="region">The region to offset.</param>
    /// <param name="delta">Signed offset distance (positive = outward).</param>
    /// <param name="join">Corner style — see <see cref="OffsetJoin"/>.</param>
    /// <param name="miterLimit">For <see cref="OffsetJoin.Miter"/>: the largest ratio of
    /// corner extension to <paramref name="delta"/> before the corner is chamfered instead.</param>
    /// <param name="arcTolerance">For <see cref="OffsetJoin.Round"/>: the largest allowed
    /// sagitta between a chord and the true offset arc.</param>
    public static IReadOnlyList<Region2d> Offset(
        Region2d region, double delta, OffsetJoin join = OffsetJoin.Round,
        double miterLimit = DefaultMiterLimit, double arcTolerance = DefaultArcTolerance)
    {
        ArgumentNullException.ThrowIfNull(region);
        return Offset([region], delta, join, miterLimit, arcTolerance);
    }

    /// <summary>
    /// Offsets a whole region set at once (the members are read as one area, so parts that
    /// grow into each other merge). See <see cref="Offset(Region2d, double, OffsetJoin, double, double)"/>.
    /// </summary>
    public static IReadOnlyList<Region2d> Offset(
        IReadOnlyList<Region2d> regions, double delta, OffsetJoin join = OffsetJoin.Round,
        double miterLimit = DefaultMiterLimit, double arcTolerance = DefaultArcTolerance)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (!(arcTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(arcTolerance), "The arc tolerance must be positive.");
        if (!(miterLimit >= 1))
            throw new ArgumentOutOfRangeException(nameof(miterLimit), "The miter limit must be at least 1.");
        if (!double.IsFinite(delta))
            throw new ArgumentOutOfRangeException(nameof(delta));

        // Deliberate exact-zero test: "no offset requested" is a caller contract, not a
        // geometric comparison — any nonzero delta builds primitives.
        if (delta == 0 || regions.Count == 0)
            return [.. regions];
        return delta > 0
            ? Grow(regions, delta, join, miterLimit, arcTolerance)
            : Shrink(regions, -delta, join, miterLimit, arcTolerance);
    }

    /// <summary>
    /// Outward dilation with a PER-VERTEX distance, interpolated linearly in arc length
    /// along each edge — a draft-like grow whose reach varies around the outline.
    /// <para><b>The slab is the external TANGENT slab, not the trapezoid through the
    /// offset endpoints.</b> The exact swept region of a linearly varying disc along a
    /// segment is bounded by the external tangent line of the two end circles, tilted by
    /// sin φ = (r₁ − r₀)/L off the edge normal; the trapezoid through the two offset
    /// endpoints under-covers near the smaller end by exactly the tangency wedge (the
    /// backlog filed the trapezoid, and the derivation corrected it). Each vertex then
    /// takes a ROUND join of its own radius between the adjacent tangent-foot
    /// directions — round only, because the tangent-slab boundary makes the round join
    /// the one whose primitive is exactly the swept set.</para>
    /// <para>Same union-of-primitives construction as the constant
    /// <see cref="Offset(Region2d, double, OffsetJoin, double, double)"/>; ALL-EQUAL
    /// distances DELEGATE to it outright, so the constant case is bit-identical by
    /// construction rather than by luck. The SIGN carries the direction exactly as the
    /// constant overload's does — all positive dilates, all negative erodes — with
    /// refusals by name for a MIXED law (see the per-loop overload), a zero or
    /// non-finite distance, and an edge whose distance CHANGES BY MORE THAN ITS LENGTH,
    /// where the larger end's disc swallows the whole edge sweep and no tangent exists;
    /// the offset is growing faster than the outline advances, so the caller splits the
    /// edge or eases the step.</para>
    /// <para>A region with HOLES takes the per-loop overload — one distance list per
    /// loop — since one flat list cannot say which vertices belong to which loop.</para>
    /// </summary>
    public static IReadOnlyList<Region2d> Offset(
        Region2d region, IReadOnlyList<double> distances,
        double arcTolerance = DefaultArcTolerance)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(distances);
        if (region.Holes.Count > 0)
            throw new ArgumentException(
                $"This overload takes one distance per OUTLINE vertex and the region carries "
                + $"{region.Holes.Count} hole(s). Use the per-loop overload, which takes one list per "
                + "loop in AllLoops order (outer first): a hole's distances mean exactly what the "
                + "outline's do — how far the material advances into the void — so a positive law "
                + "shrinks a hole and a negative one opens it.", nameof(region));
        if (distances.Count != region.Outer.Count)
            throw new ArgumentException(
                $"{distances.Count} distances for {region.Outer.Count} outline vertices; supply one per vertex.",
                nameof(distances));
        return Offset(region, [distances], arcTolerance);
    }

    /// <summary>
    /// The variable offset of a region WITH HOLES: one distance list per loop, in
    /// <see cref="Region2d.AllLoops"/> order (outer first, then the holes in order).
    ///
    /// <para><b>A distance is how far the material advances into the VOID</b>, on every
    /// loop alike — so one positive law grows the outline outward and shrinks each hole,
    /// and one negative law does the reverse. That uniformity is not a convention chosen
    /// here: the canonical form keeps material on the LEFT of every loop, so "away from
    /// the material" is already one direction per loop and the outer boundary and a hole
    /// need no separate rule.</para>
    ///
    /// <para><b>Erosion needs no frame, and that is the finding rather than the
    /// feature.</b> The constant erosion is the complement trick
    /// <c>B \ dilate(B \ R, d)</c>, which for a variable law appears to need distances
    /// for the FRAME's own boundary — but the frame CANCELS: dilating <c>B \ R</c> gives
    /// <c>(B \ R) ∪ collar ∪ frameCollar</c>, and subtracting that from B leaves
    /// <c>R \ collar</c> exactly, since the frame sits farther from R than any distance
    /// reaches. So the erosion is the region minus the INWARD collar, built from the same
    /// tangent slabs and round joins with the normal flipped — no frame, no frame
    /// distances, and the design question the backlog filed does not arise.</para>
    ///
    /// <para><b>Which corners take a join swaps with the direction, and it is a
    /// derivation.</b> A point just outside a REFLEX corner has no nearest boundary point
    /// at the vertex (the outward normal cone is empty there), which is why the outward
    /// pass fills convex corners only; inside, the same argument runs the other way — a
    /// point just inside a CONVEX corner projects onto one of the two edges, while a
    /// reflex corner's inward cone spans <c>α − 180°</c>. Since
    /// <c>Cross(−a, −b) == Cross(a, b)</c> exactly, negating both normals does NOT flip
    /// which corners the gate admits; the inward pass hands the pair to
    /// <see cref="AddCornerJoin"/> in the REVERSED ORDER, which does.</para>
    /// </summary>
    public static IReadOnlyList<Region2d> Offset(
        Region2d region, IReadOnlyList<IReadOnlyList<double>> distancesPerLoop,
        double arcTolerance = DefaultArcTolerance)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(distancesPerLoop);
        if (!(arcTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(arcTolerance), "The arc tolerance must be positive.");

        var loops = region.AllLoops().ToList();
        if (distancesPerLoop.Count != loops.Count)
            throw new ArgumentException(
                $"{distancesPerLoop.Count} distance lists for {loops.Count} loops (1 outline + "
                + $"{region.Holes.Count} hole(s)); supply one list per loop in AllLoops order.",
                nameof(distancesPerLoop));

        bool? inward = null;
        var magnitudes = new double[loops.Count][];
        for (int l = 0; l < loops.Count; l++)
        {
            var stated = distancesPerLoop[l];
            ArgumentNullException.ThrowIfNull(stated);
            if (stated.Count != loops[l].Count)
            {
                throw new ArgumentException(
                    $"Loop {l} has {loops[l].Count} vertices and {stated.Count} distances; supply one "
                    + "per vertex.", nameof(distancesPerLoop));
            }
            magnitudes[l] = new double[stated.Count];
            for (int i = 0; i < stated.Count; i++)
            {
                double d = stated[i];
                // Exact-zero semantic test: a zero-radius disc sweeps nothing, so "no offset
                // here" is not a direction the law can carry alongside a real one.
                if (!double.IsFinite(d) || d == 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(distancesPerLoop),
                        $"Distance {i} on loop {l} is {d}; every distance must be finite and non-zero.");
                }
                bool here = d < 0;
                if (inward is { } already && already != here)
                {
                    throw new ArgumentException(
                        $"Distance {i} on loop {l} is {d}, which disagrees in SIGN with an earlier one: a "
                        + "variable offset moves the whole boundary one way. A law that grew in places and "
                        + "shrank in others would pass through a zero-radius disc, where the swept set is "
                        + "not defined, so there is no honest set to return — offset the two stretches "
                        + "separately and compose them.", nameof(distancesPerLoop));
                }
                inward = here;
                magnitudes[l][i] = Math.Abs(d);
            }
        }
        if (inward is null)
            return [region];   // no vertices at all: nothing to move

        // All equal is the constant offset, delegated so it is bit-identical by
        // construction (exact comparison: equal INPUTS, not a tolerance).
        double first = magnitudes[0][0];
        bool allEqual = true;
        foreach (var loop in magnitudes)
        {
            foreach (double d in loop)
                allEqual &= d == first;
        }
        if (allEqual)
        {
            return Offset(
                region, inward.Value ? -first : first, OffsetJoin.Round, DefaultMiterLimit, arcTolerance);
        }

        var collar = new List<Region2d>();
        for (int l = 0; l < loops.Count; l++)
        {
            AddVariableLoopPrimitives(
                loops[l], region.IsCounterClockwise, magnitudes[l], inward.Value, arcTolerance, collar);
        }
        if (collar.Count == 0)
            return [region];

        if (!inward.Value)
            return Region2dBoolean.UnionAll([region, .. collar]);
        return Region2dBoolean.Difference([region], Region2dBoolean.UnionAll(collar));
    }

    /// <summary>
    /// Strokes an OPEN polyline into a region of the given <paramref name="width"/> —
    /// a toolpath's swept footprint, a slot from its centre line, an SVG-style stroke.
    /// The same union-of-primitives construction as <see cref="Offset"/>: one
    /// full-width slab per segment, corner joins at every interior vertex (both sides
    /// are offered; the inner side's wedge is already covered by its slabs, so only
    /// the outer gap changes anything — and a 180° reversal legitimately fills both,
    /// which is what puts the round nose on a doubled-back path), and end caps. Since
    /// it is a union, self-crossing paths need no special handling: the overlap is
    /// just covered once, and the result may carry holes (a path that loops encloses
    /// one).
    /// <para>With <see cref="StrokeCap.Round"/> caps and <see cref="OffsetJoin.Round"/>
    /// joins the stroke is exactly the path's Minkowski sum with a disk of radius
    /// width/2, short of it only by the inscribed-arc sagitta.</para>
    /// </summary>
    /// <param name="path">The polyline's points, in order (at least two distinct;
    /// exact consecutive duplicates are dropped). NOT closed implicitly — see
    /// <paramref name="closed"/>.</param>
    /// <param name="width">Full stroke width (&gt; 0).</param>
    /// <param name="cap">End treatment — see <see cref="StrokeCap"/>. Ignored when
    /// <paramref name="closed"/> is set: a circuit has no ends.</param>
    /// <param name="join">Corner style at interior vertices.</param>
    /// <param name="miterLimit">See <see cref="Offset(Region2d, double, OffsetJoin, double, double)"/>.</param>
    /// <param name="arcTolerance">See <see cref="Offset(Region2d, double, OffsetJoin, double, double)"/>.</param>
    /// <param name="closed">
    /// Stroke the path as a CIRCUIT: the last point joins back to the first, that closing
    /// joint gets its corner fill like any other, and no caps are added.
    ///
    /// <para><b>Why this is a flag and not a guess.</b> A list of POINTS cannot express
    /// closure — repeating the first point at the end is the only spelling available, and it
    /// is ambiguous with a path that genuinely returns to where it started and stops there.
    /// So the caller states it. It is the same contract
    /// <c>CurvedRegion2dOffset.Stroke</c> gets for free, where a chain of EDGES makes closure
    /// structural; the difference is MEASURED rather than asserted: a 10×10 square at width 2
    /// with <see cref="OffsetJoin.Miter"/> joins comes back at 79 through the repeated-point
    /// spelling and 80 here, short by exactly the 1×1 outer corner square at the repeated
    /// start point. Under round joins with round caps the two readings agree as SETS (a full
    /// disc at the closing vertex contains the join wedge), which is why the gap went unseen
    /// for so long.</para>
    ///
    /// <para>A trailing point exactly equal to the first is dropped, so both spellings of a
    /// circuit produce the same answer once the flag is set.</para>
    /// </param>
    public static IReadOnlyList<Region2d> Stroke(
        IReadOnlyList<Vector2d> path, double width, StrokeCap cap = StrokeCap.Round,
        OffsetJoin join = OffsetJoin.Round, double miterLimit = DefaultMiterLimit,
        double arcTolerance = DefaultArcTolerance, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!(width > 0) || !double.IsFinite(width))
            throw new ArgumentOutOfRangeException(nameof(width), "The stroke width must be positive.");
        if (!(arcTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(arcTolerance), "The arc tolerance must be positive.");
        if (!(miterLimit >= 1))
            throw new ArgumentOutOfRangeException(nameof(miterLimit), "The miter limit must be at least 1.");

        // Exact duplicate compaction (zero-length segments carry no direction).
        var points = new List<Vector2d>(path.Count);
        foreach (var point in path)
        {
            if (points.Count > 0 && point == points[^1])
                continue;
            points.Add(point);
        }
        // A circuit spelled by repeating the first point carries that repeat as a
        // NON-consecutive duplicate, so the compaction above cannot see it; dropping it here
        // makes both spellings of a circuit produce the same answer.
        if (closed && points.Count > 1 && points[^1] == points[0])
            points.RemoveAt(points.Count - 1);
        if (points.Count < 2)
            throw new ArgumentException("A stroked path needs at least two distinct points.", nameof(path));
        if (closed && points.Count < 3)
            throw new ArgumentException("A stroked circuit needs at least three distinct points.", nameof(path));

        double half = width / 2;
        int n = points.Count;
        int segments = closed ? n : n - 1;
        var directions = new Vector2d[segments];
        for (int i = 0; i < segments; i++)
            directions[i] = (points[(i + 1) % n] - points[i]).Normalized();

        var primitives = new List<Region2d>();

        // Slabs: the full-width rectangle per segment.
        for (int i = 0; i < segments; i++)
        {
            var shift = directions[i].Perpendicular * half;
            var a = points[i];
            var b = points[(i + 1) % n];
            primitives.Add(new Region2d([a + shift, b + shift, b - shift, a - shift]));
        }

        // Interior joins: offer the corner fill on BOTH sides. The turn's outer side
        // has the genuine gap; the inner side's wedge lies inside its own slabs (so
        // the union is unchanged), and an exact reversal fills both half-discs —
        // exactly the round nose a doubled-back path needs.
        // A circuit's closing joint (at points[0], between the last segment and the first) is
        // an ordinary interior corner and gets its fill like any other — which is the whole
        // difference the `closed` flag buys.
        for (int i = closed ? 0 : 1; i < (closed ? n : n - 1); i++)
        {
            var left0 = directions[(i - 1 + segments) % segments].Perpendicular;
            var left1 = directions[i].Perpendicular;
            // Each side is offered in BOTH orders, because `AddCornerJoin`'s gate admits a
            // pair only when its sweep is counter-clockwise — so which ORDER spells a
            // side's sector depends on which way the path turns. Exactly two of these four
            // survive the gate (one per side) at any real corner, and the inner one lies
            // inside its own slabs so the union is unchanged.
            //
            // Offering only (left0, left1) and (-left0, -left1) was wrong, and silently:
            // Cross(-a, -b) == Cross(a, b) EXACTLY, so negating both normals does not flip
            // the turn, and the two calls always agreed. At a left turn both are proper
            // sectors and the result is correct; at a RIGHT turn both are clockwise and
            // both are refused, so every right-hand corner lost its outer fill — a deficit
            // of exactly (clockwise corners) × w²/4 that no left-turning fixture can see.
            AddCornerJoin(points[i], left0, left1, half, join, miterLimit, arcTolerance, primitives);
            AddCornerJoin(points[i], left1, left0, half, join, miterLimit, arcTolerance, primitives);
            AddCornerJoin(points[i], -left0, -left1, half, join, miterLimit, arcTolerance, primitives);
            AddCornerJoin(points[i], -left1, -left0, half, join, miterLimit, arcTolerance, primitives);
        }

        // Caps — a circuit has no ends, so it gets none.
        if (!closed && cap != StrokeCap.Butt)
        {
            AddCap(points[0], -directions[0], half, cap, arcTolerance, primitives);
            AddCap(points[^1], directions[^1], half, cap, arcTolerance, primitives);
        }

        return Region2dBoolean.UnionAll(primitives);
    }

    /// <summary>A cap extending past <paramref name="end"/> in direction
    /// <paramref name="outward"/> (unit): a half-disc (inscribed polygonal arc, its
    /// diameter chord along the stroke's end edge) or a half-width square.</summary>
    private static void AddCap(
        in Vector2d end, in Vector2d outward, double half, StrokeCap cap,
        double arcTolerance, List<Region2d> into)
    {
        var side = outward.Perpendicular * half;
        if (cap == StrokeCap.Square)
        {
            var reach = outward * half;
            into.Add(new Region2d([end + side, end + side + reach, end - side + reach, end - side]));
            return;
        }
        // Round: rotate the side vector by π through the outward direction (the
        // perpendicular rotated +90° is −outward, so sweep from −side to +side).
        int segments = Math.Max(2, ArcSegments(Math.PI, half, arcTolerance));
        var arc = new List<Vector2d>(segments + 1);
        var from = -side;
        for (int k = 0; k <= segments; k++)
        {
            double angle = Math.PI * k / segments;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            arc.Add(end + new Vector2d(from.X * cos - from.Y * sin, from.X * sin + from.Y * cos));
        }
        into.Add(new Region2d(arc));
    }

    // ---- outward: the region plus one primitive per edge and per convex corner ----

    private static IReadOnlyList<Region2d> Grow(
        IReadOnlyList<Region2d> regions, double delta, OffsetJoin join,
        double miterLimit, double arcTolerance)
    {
        var primitives = new List<Region2d>(regions);
        foreach (var region in regions)
        {
            foreach (var loop in region.AllLoops())
                AddLoopPrimitives(loop, region.IsCounterClockwise, delta, join, miterLimit, arcTolerance, primitives);
        }
        return Region2dBoolean.UnionAll(primitives);
    }

    /// <summary>
    /// Erosion as the complement of a dilation: <c>B \ dilate(B \ R, d)</c>. B is R's bounds
    /// grown by 3d, so every point of B's boundary is farther than d from R and the frame
    /// cannot eat into the answer.
    /// </summary>
    private static IReadOnlyList<Region2d> Shrink(
        IReadOnlyList<Region2d> regions, double delta, OffsetJoin join,
        double miterLimit, double arcTolerance)
    {
        var bounds = Aabb.Empty;
        foreach (var region in regions)
            bounds = bounds.Union(region.Bounds);
        double margin = 3 * delta;
        var frame = new Region2d([
            new Vector2d(bounds.Min.X - margin, bounds.Min.Y - margin),
            new Vector2d(bounds.Max.X + margin, bounds.Min.Y - margin),
            new Vector2d(bounds.Max.X + margin, bounds.Max.Y + margin),
            new Vector2d(bounds.Min.X - margin, bounds.Max.Y + margin),
        ]);

        var complement = Region2dBoolean.Difference([frame], regions);
        if (complement.Count == 0)
            return [];       // R covers its own bounds: impossible, but never divide by luck
        var grown = Grow(complement, delta, join, miterLimit, arcTolerance);
        return Region2dBoolean.Difference([frame], grown);
    }

    // ---- the primitives ----

    /// <summary>
    /// One slab per edge and one join per convex corner. Both windings work unchanged: the
    /// canonical form keeps material on the LEFT of every loop (CCW outer, CW holes), and
    /// <see cref="Region2d.Reversed"/> mirrors that, so the outward normal is simply the
    /// side away from the material and a corner needs filling exactly when the loop turns
    /// towards it.
    /// </summary>
    private static void AddLoopPrimitives(
        IReadOnlyList<Vector2d> loop, bool materialOnLeft, double delta, OffsetJoin join,
        double miterLimit, double arcTolerance, List<Region2d> into)
    {
        int count = loop.Count;
        // Unit edge directions, with zero-length edges collapsed away: a repeated point
        // carries no direction, so it can neither raise a slab nor decide a turn.
        var directions = new Vector2d[count];
        var live = new bool[count];
        for (int i = 0; i < count; i++)
        {
            var edge = loop[(i + 1) % count] - loop[i];
            double length = edge.Length;
            if (!(length > 0))
                continue; // exact-zero division guard (scale-free, not a tolerance)
            directions[i] = edge / length;
            live[i] = true;
        }

        for (int i = 0; i < count; i++)
        {
            if (!live[i])
                continue;
            var a = loop[i];
            var b = loop[(i + 1) % count];
            var normal = OutwardNormal(directions[i], materialOnLeft);
            var shift = normal * delta;
            into.Add(new Region2d([a, b, b + shift, a + shift]));
        }

        for (int i = 0; i < count; i++)
        {
            int previous = PreviousLive(live, i);
            if (previous < 0 || !live[i])
                continue;
            AddCornerJoin(
                loop[i],
                OutwardNormal(directions[previous], materialOnLeft),
                OutwardNormal(directions[i], materialOnLeft),
                delta, join, miterLimit, arcTolerance, into);
        }
    }

    /// <summary>The variable twin of <see cref="AddLoopPrimitives"/>: one external
    /// TANGENT slab per edge (see the public method's derivation note) and one round
    /// join per vertex at that vertex's own radius, spanning the adjacent tangent-foot
    /// directions — <see cref="AddCornerJoin"/> already arcs between whatever unit
    /// normals it is handed, which is what makes the reuse exact rather than
    /// approximate.
    /// <para><paramref name="inward"/> builds the same collar on the MATERIAL side, for
    /// the erosion. The tangency derivation is indifferent to which unit normal it is
    /// given — <c>m̂·(L·d̂ + Δr·m̂) = 0</c> holds for any n̂ ⊥ d̂ — so the slab needs only
    /// the flipped normal, while the JOINS must be offered in the reversed order because
    /// negating both normals leaves the cross product exactly unchanged and would fill
    /// the convex corners rather than the reflex ones.</para></summary>
    private static void AddVariableLoopPrimitives(
        IReadOnlyList<Vector2d> loop, bool materialOnLeft, IReadOnlyList<double> distances,
        bool inward, double arcTolerance, List<Region2d> into)
    {
        int count = loop.Count;
        var tangentNormals = new Vector2d[count];
        var live = new bool[count];
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            var edge = loop[next] - loop[i];
            double length = edge.Length;
            if (!(length > 0))
                continue; // exact-zero division guard (scale-free, not a tolerance)
            double ra = distances[i], rb = distances[next];
            if (Math.Abs(rb - ra) >= length)
                throw new ArgumentException(
                    $"The offset changes by |{rb} − {ra}| = {Math.Abs(rb - ra)} over edge {i}, whose length "
                    + $"is only {length}: the larger end's disc swallows the whole edge sweep and no tangent "
                    + "slab exists. Split the edge or ease the step.", nameof(distances));

            var direction = edge / length;
            var normal = OutwardNormal(direction, materialOnLeft);
            if (inward)
                normal = -normal;
            // The external tangent line tilts off the normal by sin φ = (rb − ra)/L,
            // rotating AGAINST the direction of travel toward the smaller end; the foot
            // direction m̂ = n̂·cos φ − d̂·sin φ satisfies the tangency condition
            // m̂·(L·d̂ + (rb − ra)·m̂) = 0 exactly.
            double sin = (rb - ra) / length;
            double cos = Math.Sqrt(1 - sin * sin);
            var foot = normal * cos - direction * sin;
            tangentNormals[i] = foot;
            live[i] = true;
            into.Add(new Region2d([loop[i], loop[next], loop[next] + foot * rb, loop[i] + foot * ra]));
        }

        for (int i = 0; i < count; i++)
        {
            int previous = PreviousLive(live, i);
            if (previous < 0 || !live[i])
                continue;
            // Outward fills the wedge left open where the loop turns AWAY from the material;
            // inward fills the one a REFLEX corner opens on the material side, which is the
            // same gate asked the other way round — hence the swapped pair, not a negation.
            var (from, to) = inward
                ? (tangentNormals[i], tangentNormals[previous])
                : (tangentNormals[previous], tangentNormals[i]);
            AddCornerJoin(
                loop[i], from, to, distances[i],
                OffsetJoin.Round, DefaultMiterLimit, arcTolerance, into);
        }
    }

    private static Vector2d OutwardNormal(in Vector2d direction, bool materialOnLeft) =>
        materialOnLeft ? new Vector2d(direction.Y, -direction.X) : direction.Perpendicular;

    private static int PreviousLive(bool[] live, int index)
    {
        for (int step = 1; step <= live.Length; step++)
        {
            int candidate = (index - step + live.Length) % live.Length;
            if (candidate == index)
                break;
            if (live[candidate])
                return candidate;
        }
        return -1;
    }

    /// <summary>
    /// Fills the wedge left open at a vertex between two offset edges. Nothing is needed
    /// when the loop turns away from the outward side (a reflex corner of the offset — the
    /// two slabs already overlap there); the sweep angle is otherwise
    /// <c>atan2(cross, dot)</c> of the two outward normals, which reaches exactly π at a
    /// 180-degree reversal (a spike).
    /// </summary>
    private static void AddCornerJoin(
        in Vector2d vertex, in Vector2d fromNormal, in Vector2d toNormal, double delta,
        OffsetJoin join, double miterLimit, double arcTolerance, List<Region2d> into)
    {
        double cross = fromNormal.Cross(toNormal);
        double dot = fromNormal.Dot(toNormal);
        // Exact-zero semantic tests: cross == 0 with dot > 0 is a straight-through vertex
        // (no gap at all); cross == 0 with dot < 0 is a spike, which needs a half turn.
        if (cross < 0 || (cross == 0 && dot > 0))
            return;
        double sweep = Math.Atan2(cross, dot);
        if (sweep <= 0)
            sweep = Math.PI;    // exact reversal: atan2(0, -1) already gives π; belt and braces

        var start = vertex + fromNormal * delta;
        var end = vertex + toNormal * delta;

        if (join == OffsetJoin.Miter)
        {
            // Miter point = v + delta · bisector / cos(sweep/2); with unit normals,
            // bisector / cos(sweep/2) = 2(n1 + n2) / |n1 + n2|². The extension ratio is
            // 1 / cos(sweep/2) = 2 / |n1 + n2|.
            //
            // |n1 + n2|² comes from LengthSquared, never from Length squared: at a right
            // angle the sum is (±1, ±1), whose LengthSquared is exactly 2 while
            // Math.Sqrt(2)² is 2.0000000000000004 — enough to move the apex a few ulps off
            // the two offset edge lines, so the collinear T-junctions on either side stop
            // being exactly collinear and a mitered square comes back with 8 corners.
            var sum = fromNormal + toNormal;
            double sumLengthSquared = sum.LengthSquared;
            if (sumLengthSquared > 0 && 2 / Math.Sqrt(sumLengthSquared) <= miterLimit)
            {
                var apex = vertex + sum * (2 * delta / sumLengthSquared);
                AddIfNonDegenerate([vertex, start, apex, end], into);
                return;
            }
            // Past the limit (or an exact reversal, where the miter is at infinity): cut
            // the corner back to a straight bevel, as Clipper does.
            join = OffsetJoin.Chamfer;
        }

        if (join == OffsetJoin.Chamfer)
        {
            AddIfNonDegenerate([vertex, start, end], into);
            return;
        }

        // Round: an inscribed polygonal arc — every vertex exactly on the offset circle.
        int segments = ArcSegments(sweep, delta, arcTolerance);
        var points = new List<Vector2d>(segments + 2) { vertex, start };
        for (int k = 1; k < segments; k++)
        {
            double angle = sweep * k / segments;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            var rotated = new Vector2d(
                fromNormal.X * cos - fromNormal.Y * sin,
                fromNormal.X * sin + fromNormal.Y * cos);
            points.Add(vertex + rotated * delta);
        }
        points.Add(end);
        AddIfNonDegenerate(points, into);
    }

    /// <summary>Segments for an arc of radius <paramref name="radius"/> spanning
    /// <paramref name="sweep"/> whose sagitta stays within <paramref name="tolerance"/>:
    /// a chord of half-angle θ deviates by r(1 − cos θ), so θ ≤ acos(1 − tol/r).</summary>
    private static int ArcSegments(double sweep, double radius, double tolerance)
    {
        double ratio = 1 - tolerance / radius;
        double maxAngle = ratio <= -1 ? Math.PI : 2 * Math.Acos(Math.Max(ratio, -1));
        return Math.Max(1, (int)Math.Ceiling(sweep / maxAngle));
    }

    /// <summary>A corner primitive that encloses no area (a straight-through vertex that
    /// survived rounding, or the collapsed bevel of an exact reversal) contributes nothing
    /// to the union and would be rejected by <see cref="Region2d"/>'s constructor.</summary>
    private static void AddIfNonDegenerate(IReadOnlyList<Vector2d> loop, List<Region2d> into)
    {
        // Exact-zero test on the shoelace: Region2d's own admission rule, not a tolerance.
        if (loop.Count >= 3 && Region2d.SignedArea(loop) != 0)
            into.Add(new Region2d(loop));
    }
}
