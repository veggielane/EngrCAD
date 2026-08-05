namespace EngrCAD.Core.Geometry2;

/// <summary>
/// A planar region whose boundary carries CURVES: one outer loop plus zero or more hole
/// loops, each a closed chain of <see cref="CurvedEdge2d"/> (straight segments and circular
/// arcs). The curved sibling of <see cref="Region2d"/>, and the currency of the exact
/// curved 2D boolean and offset.
///
/// <para><b>What "exact" buys.</b> A <see cref="Region2d"/> is polygonal, so every arc that
/// reaches it has already been flattened at a chord tolerance and everything downstream
/// inherits that error — offsets, sketch booleans, sections, silhouettes, and the
/// <c>Profile</c>s an extrusion is built from. Here an arc stays an arc: <see cref="Area"/>
/// is the closed-form value (a disc measures exactly πr², not an inscribed polygon's area),
/// booleans return arcs, and <c>Profile.FromCurvedRegion</c> turns them into exact NURBS
/// arcs, so an extruded sketch boolean becomes an analytic B-Rep rather than a prism of
/// chords.</para>
///
/// <para><b>Loops are closed implicitly by CHAINING</b>, not by repeating a point: edge i's
/// end is edge i+1's start, and the last edge's end returns to the first edge's start. A
/// single full-circle edge is a legal loop.</para>
///
/// <para><b>Orientation.</b> Canonical is a counter-clockwise outer loop with clockwise
/// holes; the constructor re-orients whatever it is given, and <see cref="Reversed"/>
/// produces the mirror form. Orientation never affects <see cref="Area"/>,
/// <see cref="Bounds"/> or <see cref="Contains"/>.</para>
///
/// <para><b>On-boundary convention.</b> Regions are CLOSED sets. Unlike
/// <see cref="Region2d"/>, whose boundary test is exact, a point is on a curved boundary
/// when its distance to an edge is within <see cref="BoundaryTolerance"/> — no point of
/// interest lies exactly on a circle in doubles, so an exact test there would answer "no"
/// for every point a caller means to be on the rim. Interior decisions are still parity
/// decisions, with straight edges routed through <see cref="Predicates2d.Orient2dSign"/>
/// exactly as <see cref="Region2d"/> does.</para>
/// </summary>
public sealed class CurvedRegion2d
{
    /// <summary>Distance within which a point counts as ON the boundary — the 1e-9 absolute
    /// weld tier, the same threshold curved arrangements merge vertices at.</summary>
    public const double BoundaryTolerance = 1e-9;

    /// <summary>Outer boundary chain — counter-clockwise unless <see cref="IsCounterClockwise"/> is false.</summary>
    public IReadOnlyList<CurvedEdge2d> Outer { get; }

    /// <summary>Hole chains, each strictly inside <see cref="Outer"/>.</summary>
    public IReadOnlyList<IReadOnlyList<CurvedEdge2d>> Holes { get; }

    /// <summary>True for the canonical orientation (outer counter-clockwise, holes clockwise).</summary>
    public bool IsCounterClockwise { get; }

    /// <summary>Net enclosed area — the outer loop's minus the holes'. EXACT for lines and
    /// arcs (Green's theorem with the closed-form arc term), always positive, and
    /// independent of orientation.</summary>
    public double Area { get; }

    /// <summary>Tight bounds of the outer loop (arcs contribute their cardinal extremes, not
    /// their chords), as a degenerate-in-z <see cref="Aabb"/>.</summary>
    public Aabb Bounds { get; }

    /// <summary>
    /// Builds a region from an outer chain and optional hole chains, re-orienting them to
    /// the canonical winding. Validates that every chain CLOSES, that it encloses area, that
    /// each hole lies inside the outer loop, and that no two edges cross transversally at
    /// interior points (tangential contact is legal — for lines and arcs a tangency is
    /// always a touch, never a crossing, since two distinct circles tangent at a point stay
    /// on one side of each other there). The crossing check is
    /// <see cref="CurvedRegion2dValidation"/>, whose exact contract — and whose
    /// <c>Bvh</c> broad phase — is documented there.
    /// </summary>
    public CurvedRegion2d(
        IReadOnlyList<CurvedEdge2d> outer,
        IReadOnlyList<IReadOnlyList<CurvedEdge2d>>? holes = null)
    {
        ArgumentNullException.ThrowIfNull(outer);
        var holeLoops = holes ?? [];
        RequireClosed(outer, "The region's outer chain");
        for (int i = 0; i < holeLoops.Count; i++)
            RequireClosed(holeLoops[i], $"Hole chain {i}");

        CurvedRegion2dValidation.Require(
            [outer, .. holeLoops],
            index => index == 0 ? "The region's outer chain" : $"Hole chain {index - 1}",
            holeLoops.Count > 0 ? nameof(holes) : nameof(outer));

        double outerArea = SignedArea(outer);
        if (outerArea == 0)
            throw new ArgumentException("A region's outer chain must enclose area.", nameof(outer));

        var holeAreas = new double[holeLoops.Count];
        for (int i = 0; i < holeLoops.Count; i++)
        {
            holeAreas[i] = SignedArea(holeLoops[i]);
            if (holeAreas[i] == 0)
                throw new ArgumentException($"Hole chain {i} must enclose area.", nameof(holes));
            if (!LoopContainsLoop(outer, holeLoops[i]))
                throw new ArgumentException($"Hole chain {i} is not inside the region's outer chain.", nameof(holes));
        }

        Outer = outerArea > 0 ? [.. outer] : Reverse(outer);
        Holes = [.. Enumerable.Range(0, holeLoops.Count)
            .Select(i => holeAreas[i] < 0 ? (IReadOnlyList<CurvedEdge2d>)[.. holeLoops[i]] : Reverse(holeLoops[i]))];
        IsCounterClockwise = true;
        Area = Math.Abs(outerArea) - holeAreas.Sum(Math.Abs);
        Bounds = BoundsOf(Outer);
    }

    private CurvedRegion2d(
        IReadOnlyList<CurvedEdge2d> outer, IReadOnlyList<IReadOnlyList<CurvedEdge2d>> holes,
        bool counterClockwise, double area, in Aabb bounds)
    {
        Outer = outer;
        Holes = holes;
        IsCounterClockwise = counterClockwise;
        Area = area;
        Bounds = bounds;
    }

    /// <summary>A whole disc as a one-edge region — the smallest region whose exactness is
    /// visible (its <see cref="Area"/> is πr² to the last bit of the multiplication).</summary>
    public static CurvedRegion2d Disc(in Vector2d center, double radius) =>
        new([CurvedEdge2d.Circle(center, radius)]);

    /// <summary>A polygonal <see cref="Region2d"/> read as a curved region — every edge a
    /// straight <see cref="CurvedEdge2d"/>, no geometry changed.</summary>
    public static CurvedRegion2d FromRegion(Region2d region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var result = new CurvedRegion2d(
            LoopOf(region.Outer), [.. region.Holes.Select(LoopOf)],
            region.IsCounterClockwise, region.Area, region.Bounds);
        return result;

        static IReadOnlyList<CurvedEdge2d> LoopOf(IReadOnlyList<Vector2d> loop)
        {
            var edges = new CurvedEdge2d[loop.Count];
            for (int i = 0; i < loop.Count; i++)
                edges[i] = CurvedEdge2d.Line(loop[i], loop[(i + 1) % loop.Count]);
            return edges;
        }
    }

    /// <summary>
    /// This region flattened to a polygonal <see cref="Region2d"/> — the way DOWN to every
    /// consumer that is still polygonal (tessellation, the mesh-derived section paths, SDFs
    /// built from loops). Arcs become inscribed polylines within
    /// <paramref name="chordTolerance"/>, matching <c>Sketch.ToRegions</c>' one-sided
    /// contract so error never accumulates outward.
    /// </summary>
    public Region2d ToRegion(double chordTolerance = 1e-3)
    {
        if (!(chordTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(chordTolerance));
        return new Region2d(
            Flatten(Outer, chordTolerance),
            [.. Holes.Select(h => Flatten(h, chordTolerance))]);
    }

    /// <summary>Every loop with its traversal direction flipped. Geometry, area, bounds and
    /// containment are unchanged — only the winding.</summary>
    public CurvedRegion2d Reversed() => new(
        Reverse(Outer), [.. Holes.Select(Reverse)], !IsCounterClockwise, Area, Bounds);

    /// <summary>Outer chain first, then the holes.</summary>
    public IEnumerable<IReadOnlyList<CurvedEdge2d>> AllLoops()
    {
        yield return Outer;
        foreach (var hole in Holes)
            yield return hole;
    }

    /// <summary>
    /// Is the point inside the region? The region is a CLOSED set: points within
    /// <see cref="BoundaryTolerance"/> of a boundary edge are inside. Interior points are
    /// decided by even–odd parity of a +x ray against all loops.
    /// </summary>
    public bool Contains(in Vector2d point)
    {
        foreach (var loop in AllLoops())
        {
            foreach (var edge in loop)
            {
                if (edge.DistanceTo(point) <= BoundaryTolerance)
                    return true;
            }
        }
        return ParityInside(point);
    }

    /// <summary>Even–odd parity of a +x ray against every loop, ignoring the boundary case.</summary>
    /// <remarks>
    /// The walk is written out rather than routed through <see cref="AllLoops"/> because that
    /// is a <c>yield</c> iterator and this is the innermost loop of every boolean's cell
    /// classification — one allocation per region per cell. Same terms, same order, same
    /// answer bit for bit.
    /// </remarks>
    public bool ParityInside(in Vector2d point)
    {
        int crossings = 0;
        for (int i = 0; i < Outer.Count; i++)
            crossings += Outer[i].RayCrossings(point);
        for (int h = 0; h < Holes.Count; h++)
        {
            var hole = Holes[h];
            for (int i = 0; i < hole.Count; i++)
                crossings += hole[i].RayCrossings(point);
        }
        return (crossings & 1) != 0;
    }

    /// <summary>Everything covered by this region or <paramref name="other"/>.</summary>
    public IReadOnlyList<CurvedRegion2d> Union(CurvedRegion2d other) => CurvedRegion2dBoolean.Union(this, other);

    /// <summary>Everything covered by both this region and <paramref name="other"/>.</summary>
    public IReadOnlyList<CurvedRegion2d> Intersect(CurvedRegion2d other) => CurvedRegion2dBoolean.Intersection(this, other);

    /// <summary>This region with <paramref name="other"/> cut away.</summary>
    public IReadOnlyList<CurvedRegion2d> Subtract(CurvedRegion2d other) => CurvedRegion2dBoolean.Difference(this, other);

    /// <summary>
    /// Sorts an unordered bag of closed curved chains into regions by CONTAINMENT DEPTH,
    /// exactly as <see cref="Region2d.FromLoops"/> does: even depth is an outer boundary,
    /// odd depth is a hole of its immediate parent, and an island inside a hole becomes its
    /// own region. Input winding is irrelevant. Chains enclosing no area are ignored.
    /// </summary>
    public static IReadOnlyList<CurvedRegion2d> FromLoops(IEnumerable<IReadOnlyList<CurvedEdge2d>> loops)
    {
        ArgumentNullException.ThrowIfNull(loops);
        var kept = loops.Where(l => l.Count > 0 && SignedArea(l) != 0).ToList();
        int n = kept.Count;
        if (n == 0)
            return [];

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

        var regions = new List<CurvedRegion2d>();
        for (int i = 0; i < n; i++)
        {
            if (depth[i] % 2 != 0)
                continue;
            var holes = new List<IReadOnlyList<CurvedEdge2d>>();
            for (int h = 0; h < n; h++)
            {
                if (depth[h] == depth[i] + 1 && contains[i, h])
                    holes.Add(kept[h]);
            }
            regions.Add(new CurvedRegion2d(kept[i], holes));
        }
        return regions;
    }

    // ---- loop maths ----

    /// <summary>Signed area of a closed curved chain: positive counter-clockwise. Exact for
    /// lines and arcs, anchored at the chain's first point for stability far from the
    /// origin.</summary>
    public static double SignedArea(IReadOnlyList<CurvedEdge2d> loop)
    {
        if (loop.Count == 0)
            return 0;
        var anchor = loop[0].Start;
        double sum = 0;
        foreach (var edge in loop)
            sum += edge.SignedAreaTerm(anchor);
        return sum;
    }

    /// <summary>Even–odd parity of a +x ray against ONE closed chain.</summary>
    public static bool ParityInside(IReadOnlyList<CurvedEdge2d> loop, in Vector2d point)
    {
        int crossings = 0;
        foreach (var edge in loop)
            crossings += edge.RayCrossings(point);
        return (crossings & 1) != 0;
    }

    /// <summary>Is the point within <see cref="BoundaryTolerance"/> of the chain?</summary>
    public static bool OnLoop(IReadOnlyList<CurvedEdge2d> loop, in Vector2d point)
    {
        foreach (var edge in loop)
        {
            if (edge.DistanceTo(point) <= BoundaryTolerance)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Strict containment of one closed chain in another: the first probe of
    /// <paramref name="inner"/> that does not sit on <paramref name="outer"/> decides
    /// (non-crossing chains cannot disagree). Both endpoints AND midpoints are probed,
    /// because two chains can share every VERTEX and still nest — a chord and its arc do.
    /// </summary>
    public static bool LoopContainsLoop(
        IReadOnlyList<CurvedEdge2d> outer, IReadOnlyList<CurvedEdge2d> inner)
    {
        foreach (var edge in inner)
        {
            if (!OnLoop(outer, edge.Start))
                return ParityInside(outer, edge.Start);
        }
        foreach (var edge in inner)
        {
            var probe = edge.MidPoint;
            if (!OnLoop(outer, probe))
                return ParityInside(outer, probe);
        }
        return false;
    }

    /// <summary>Inscribed polyline of one chain — each edge contributes its start point and
    /// its interior samples, so joints appear exactly once and the loop closes implicitly.</summary>
    public static IReadOnlyList<Vector2d> Flatten(IReadOnlyList<CurvedEdge2d> loop, double chordTolerance)
    {
        var points = new List<Vector2d>();
        foreach (var edge in loop)
        {
            points.Add(edge.Start);
            if (!edge.IsArc)
                continue;
            // A chord of half-angle t deviates by r(1 - cos t), so t <= acos(1 - tol/r).
            double ratio = 1 - chordTolerance / edge.Radius;
            double maxAngle = ratio <= -1 ? Math.PI : 2 * Math.Acos(Math.Max(ratio, -1));
            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(edge.SweepAngle) / maxAngle));
            for (int k = 1; k < segments; k++)
                points.Add(edge.PointAt((double)k / segments));
        }
        return points;
    }

    private static void RequireClosed(IReadOnlyList<CurvedEdge2d> loop, string what)
    {
        if (loop.Count == 0)
            throw new ArgumentException($"{what} is empty.");
        for (int i = 0; i < loop.Count; i++)
        {
            var end = loop[i].End;
            var next = loop[(i + 1) % loop.Count].Start;
            if (end.DistanceTo(next) > BoundaryTolerance)
                throw new ArgumentException($"{what} is not closed: edge {i} ends at {end} but the next starts at {next}.");
        }
    }

    private static IReadOnlyList<CurvedEdge2d> Reverse(IReadOnlyList<CurvedEdge2d> loop)
    {
        var reversed = new CurvedEdge2d[loop.Count];
        for (int i = 0; i < loop.Count; i++)
            reversed[i] = loop[loop.Count - 1 - i].Reversed();
        return reversed;
    }

    private static Aabb BoundsOf(IReadOnlyList<CurvedEdge2d> loop)
    {
        var bounds = Aabb.Empty;
        foreach (var edge in loop)
            bounds = bounds.Union(edge.Bounds());
        return bounds;
    }
}
