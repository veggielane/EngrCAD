namespace EngrCAD.Core.Geometry2;

/// <summary>
/// A planar polygonal region: one outer boundary loop plus zero or more hole loops
/// (geometry3Sharp's <c>Polygon2d</c> / <c>GeneralPolygon2d</c> in our idioms). Loops are
/// closed implicitly — the last point joins back to the first, never repeat it.
///
/// <para><b>Orientation.</b> The canonical form is a counter-clockwise outer loop with
/// clockwise holes; the constructor re-orients whatever it is given. <see cref="Reversed"/>
/// returns the mirror form (clockwise outer, counter-clockwise holes) for consumers whose
/// winding convention is the opposite; <see cref="IsCounterClockwise"/> says which form a
/// region carries. Orientation never affects <see cref="Area"/>, <see cref="Bounds"/> or
/// <see cref="Contains"/>.</para>
///
/// <para><b>On-boundary convention.</b> Regions are CLOSED sets: a point exactly on any
/// loop — including exactly on a vertex — is <see cref="Contains">contained</see>. Every
/// sidedness decision goes through <see cref="Predicates2d.Orient2dSign"/>, so containment
/// is exact for all finite double inputs (no tolerance, no epsilon band).</para>
///
/// <para><b>Curves.</b> Regions are polygonal by construction; curved sketch input is
/// flattened to a chord tolerance before it reaches this type (see
/// <c>Sketch.ToRegions</c>). The region model is the arrangement-based boolean's currency,
/// not a replacement for exact B-Rep profiles.</para>
/// </summary>
public sealed class Region2d
{
    /// <summary>Outer boundary loop — counter-clockwise unless <see cref="IsCounterClockwise"/> is false.</summary>
    public IReadOnlyList<Vector2d> Outer { get; }

    /// <summary>Hole loops, each strictly inside <see cref="Outer"/> — clockwise unless
    /// <see cref="IsCounterClockwise"/> is false.</summary>
    public IReadOnlyList<IReadOnlyList<Vector2d>> Holes { get; }

    /// <summary>True for the canonical orientation (outer counter-clockwise, holes
    /// clockwise); false for the mirror produced by <see cref="Reversed"/>.</summary>
    public bool IsCounterClockwise { get; }

    /// <summary>Net enclosed area: the outer loop's area minus the holes'. Always positive
    /// and independent of orientation. Exact shoelace arithmetic (anchored at the loop's
    /// first vertex for stability far from the origin).</summary>
    public double Area { get; }

    /// <summary>Bounds of the outer loop, as a degenerate-in-z <see cref="Aabb"/>
    /// (the convention <c>Sketch.Bounds</c> already uses).</summary>
    public Aabb Bounds { get; }

    /// <summary>
    /// Builds a region from an outer loop and optional holes, re-orienting them to the
    /// canonical winding. Validates that every loop encloses area, that each loop is SIMPLE
    /// (no segment of a loop properly crosses another segment of the same loop), that each
    /// hole lies inside the outer loop, and that no two loops properly cross. Touching at a
    /// vertex or along a collinear run is allowed throughout — see
    /// <see cref="Region2dValidation"/> for the exact contract.
    /// </summary>
    public Region2d(IReadOnlyList<Vector2d> outer, IReadOnlyList<IReadOnlyList<Vector2d>>? holes = null)
    {
        var holeLoops = holes ?? [];
        if (outer.Count < 3)
            throw new ArgumentException("A region's outer loop needs at least 3 points and must enclose area.", nameof(outer));
        for (int i = 0; i < holeLoops.Count; i++)
        {
            if (holeLoops[i].Count < 3)
                throw new ArgumentException($"Hole loop {i} needs at least 3 points and must enclose area.", nameof(holes));
        }

        // Simplicity BEFORE the enclosed-area test: a bow-tie with equal lobes has a signed
        // area of exactly zero, so the area test would otherwise refuse it with a message
        // about the wrong defect. One sweep answers self-intersection AND every loop pair at
        // once — the same question about the same segment set, so splitting it would cost
        // twice as much, and treating them as different questions is how the self case came
        // to be missed in the first place.
        Region2dValidation.Require(
            [outer, .. holeLoops],
            index => index == 0 ? "The region's outer loop" : $"Hole loop {index - 1}",
            holeLoops.Count > 0 ? nameof(holes) : nameof(outer));

        double outerArea = SignedArea(outer);
        if (outerArea == 0)
            throw new ArgumentException("A region's outer loop needs at least 3 points and must enclose area.", nameof(outer));

        var holeAreas = new double[holeLoops.Count];
        for (int i = 0; i < holeLoops.Count; i++)
        {
            holeAreas[i] = SignedArea(holeLoops[i]);
            if (holeAreas[i] == 0)
                throw new ArgumentException($"Hole loop {i} needs at least 3 points and must enclose area.", nameof(holes));
            if (!LoopContainsLoop(outer, holeLoops[i]))
                throw new ArgumentException($"Hole loop {i} is not inside the region's outer loop.", nameof(holes));
        }

        Outer = outerArea > 0 ? [.. outer] : Reverse(outer);
        Holes = [.. Enumerable.Range(0, holeLoops.Count)
            .Select(i => holeAreas[i] < 0 ? (IReadOnlyList<Vector2d>)[.. holeLoops[i]] : Reverse(holeLoops[i]))];
        IsCounterClockwise = true;
        Area = Math.Abs(outerArea) - holeAreas.Sum(Math.Abs);
        Bounds = BoundsOf(Outer);
    }

    private Region2d(
        IReadOnlyList<Vector2d> outer, IReadOnlyList<IReadOnlyList<Vector2d>> holes,
        bool counterClockwise, double area, in Aabb bounds)
    {
        Outer = outer;
        Holes = holes;
        IsCounterClockwise = counterClockwise;
        Area = area;
        Bounds = bounds;
    }

    /// <summary>Every loop with its traversal direction flipped (outer clockwise, holes
    /// counter-clockwise, and back again). Geometry, area, bounds and containment are
    /// unchanged — only the winding.</summary>
    public Region2d Reversed() => new(
        Reverse(Outer),
        [.. Holes.Select(Reverse)],
        !IsCounterClockwise, Area, Bounds);

    /// <summary>
    /// Is the point inside the region? The region is a CLOSED set: points exactly on a
    /// loop (edge interior or vertex) are inside. Interior points are decided by even–odd
    /// parity of a +x ray against all loops, with the half-open upward-crossing rule and
    /// exact <see cref="Predicates2d.Orient2dSign"/> sidedness — exact for all finite
    /// doubles.
    /// </summary>
    public bool Contains(in Vector2d point)
    {
        if (OnLoop(Outer, point))
            return true;
        foreach (var hole in Holes)
        {
            if (OnLoop(hole, point))
                return true;
        }
        bool inside = ParityInside(Outer, point);
        foreach (var hole in Holes)
        {
            if (ParityInside(hole, point))
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Sorts an unordered bag of closed loops into regions by CONTAINMENT DEPTH — the
    /// "automatic hole detection" a sketch front door needs (geometry3Sharp's
    /// <c>PlanarComplex</c> nesting): a loop contained in an even number of other loops is
    /// an outer boundary, one contained in an odd number is a hole of its immediate parent
    /// (the deepest loop containing it), and an island inside a hole becomes a region of
    /// its own. Input winding is irrelevant — every loop is re-oriented canonically.
    /// Loops with fewer than 3 points or no enclosed area are ignored.
    ///
    /// <para>Each loop must be SIMPLE, and that is checked BEFORE the enclosed-area filter:
    /// a figure-eight with equal lobes has a signed area of exactly zero, so it would
    /// otherwise be silently dropped rather than refused. Loops may legitimately overlap one
    /// another here — that is what containment sorting is for — so only self-crossings are
    /// rejected at this door; loops that end up in the SAME region are cross-checked by the
    /// constructor.</para>
    /// </summary>
    public static IReadOnlyList<Region2d> FromLoops(IEnumerable<IReadOnlyList<Vector2d>> loops)
    {
        var all = loops as IReadOnlyList<IReadOnlyList<Vector2d>> ?? [.. loops];
        Region2dValidation.Require(all, index => $"Input loop {index}", nameof(loops), acrossLoops: false);
        var kept = all.Where(l => l.Count >= 3 && SignedArea(l) != 0).ToList();
        int n = kept.Count;
        if (n == 0)
            return [];

        // contains[j, i]: loop j contains loop i.
        var contains = new bool[n, n];
        var depth = new int[n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            if (i == j || !LoopContainsLoop(kept[j], kept[i]))
                continue;
            contains[j, i] = true;
            depth[i]++;
        }

        var regions = new List<Region2d>();
        for (int i = 0; i < n; i++)
        {
            if (depth[i] % 2 != 0)
                continue;
            // Holes of loop i: the loops one level deeper whose deepest container is i.
            var holes = new List<IReadOnlyList<Vector2d>>();
            for (int h = 0; h < n; h++)
            {
                if (depth[h] == depth[i] + 1 && contains[i, h])
                    holes.Add(kept[h]);
            }
            regions.Add(new Region2d(kept[i], holes));
        }
        return regions;
    }

    /// <summary>Everything covered by this region or <paramref name="other"/> — see
    /// <see cref="Region2dBoolean"/>.</summary>
    public IReadOnlyList<Region2d> Union(Region2d other) => Region2dBoolean.Union(this, other);

    /// <summary>Everything covered by both this region and <paramref name="other"/> — see
    /// <see cref="Region2dBoolean"/>.</summary>
    public IReadOnlyList<Region2d> Intersect(Region2d other) => Region2dBoolean.Intersection(this, other);

    /// <summary>This region with <paramref name="other"/> cut away — see
    /// <see cref="Region2dBoolean"/>.</summary>
    public IReadOnlyList<Region2d> Subtract(Region2d other) => Region2dBoolean.Difference(this, other);

    /// <summary>Outer loop first, then the holes — the order <c>Region2d.FromLoops</c> round-trips.</summary>
    public IEnumerable<IReadOnlyList<Vector2d>> AllLoops()
    {
        yield return Outer;
        foreach (var hole in Holes)
            yield return hole;
    }

    // ---- loop maths (exact where it decides topology) ----

    /// <summary>Shoelace signed area of a closed loop: positive counter-clockwise.
    /// Anchored at the first vertex so coordinates far from the origin do not lose bits.</summary>
    public static double SignedArea(IReadOnlyList<Vector2d> loop)
    {
        if (loop.Count < 3)
            return 0;
        var p0 = loop[0];
        double sum = 0;
        for (int i = 1; i + 1 < loop.Count; i++)
            sum += (loop[i] - p0).Cross(loop[i + 1] - p0);
        return 0.5 * sum;
    }

    /// <summary>Is the point exactly on the closed loop (edge interior or vertex)? Exact:
    /// collinearity by <see cref="Predicates2d.Orient2dSign"/>, betweenness by coordinate
    /// comparison.</summary>
    public static bool OnLoop(IReadOnlyList<Vector2d> loop, in Vector2d point)
    {
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            if (Predicates2d.Orient2dSign(a, b, point) != 0)
                continue;
            if (Math.Min(a.X, b.X) <= point.X && point.X <= Math.Max(a.X, b.X)
                && Math.Min(a.Y, b.Y) <= point.Y && point.Y <= Math.Max(a.Y, b.Y))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Even–odd parity of a +x ray against one closed loop, ignoring the boundary case.
    /// The half-open rule (a.Y &gt; y) != (b.Y &gt; y) counts each vertex once, and which
    /// side of the edge the point falls on is the exact orientation sign — so the crossing
    /// x is never computed and never rounded.
    /// </summary>
    public static bool ParityInside(IReadOnlyList<Vector2d> loop, in Vector2d point)
    {
        bool inside = false;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            if (a.Y > point.Y == b.Y > point.Y)
                continue;
            // Upward edge: the crossing is right of the point when the point is LEFT of
            // a→b (Orient2d > 0); downward edge: the mirror image.
            int side = Predicates2d.Orient2dSign(a, b, point);
            if (b.Y > a.Y ? side > 0 : side < 0)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Strict containment of one closed loop in another: the first vertex of
    /// <paramref name="inner"/> that does not sit on <paramref name="outer"/> decides
    /// (non-crossing loops cannot disagree). Loops sharing every vertex are not contained.</summary>
    public static bool LoopContainsLoop(IReadOnlyList<Vector2d> outer, IReadOnlyList<Vector2d> inner)
    {
        foreach (var p in inner)
        {
            if (OnLoop(outer, p))
                continue;
            return ParityInside(outer, p);
        }
        return false;
    }

    /// <summary>
    /// Drops vertices that lie EXACTLY on the straight line between their neighbours and
    /// strictly between them — the T-junction vertices an <see cref="Arrangement2d"/> leaves
    /// behind when two coincident edges are merged. Both tests are exact
    /// (<see cref="Predicates2d.Orient2dSign"/> plus a dominant-axis coordinate comparison),
    /// so the polygon's point set is provably unchanged; the betweenness test also means a
    /// 180-degree reversal (a slit) is never removed. Repeats until stable, never below 3
    /// points.
    /// </summary>
    public static IReadOnlyList<Vector2d> WithoutCollinearVertices(IReadOnlyList<Vector2d> loop)
    {
        var points = new List<Vector2d>(loop);
        bool changed = true;
        while (changed && points.Count > 3)
        {
            changed = false;
            for (int i = 0; i < points.Count && points.Count > 3;)
            {
                var previous = points[(i - 1 + points.Count) % points.Count];
                var current = points[i];
                var next = points[(i + 1) % points.Count];
                if (Predicates2d.Orient2dSign(previous, current, next) == 0
                    && StrictlyBetween(previous, next, current))
                {
                    points.RemoveAt(i);
                    changed = true;
                }
                else
                {
                    i++;
                }
            }
        }
        return points;
    }

    /// <summary>For p exactly on the line through a and b: is it strictly between them?
    /// Decided by exact coordinate comparison on the dominant axis (as
    /// <see cref="Arrangement2d"/> does).</summary>
    private static bool StrictlyBetween(in Vector2d a, in Vector2d b, in Vector2d p)
    {
        if (Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y))
            return a.X < b.X ? a.X < p.X && p.X < b.X : b.X < p.X && p.X < a.X;
        return a.Y < b.Y ? a.Y < p.Y && p.Y < b.Y : b.Y < p.Y && p.Y < a.Y;
    }

    private static IReadOnlyList<Vector2d> Reverse(IReadOnlyList<Vector2d> loop)
    {
        var reversed = new Vector2d[loop.Count];
        for (int i = 0; i < loop.Count; i++)
            reversed[i] = loop[loop.Count - 1 - i];
        return reversed;
    }

    private static Aabb BoundsOf(IReadOnlyList<Vector2d> loop)
    {
        var bounds = Aabb.Empty;
        foreach (var p in loop)
            bounds = bounds.Union((p.X, p.Y, 0));
        return bounds;
    }
}
