using EngrCAD.Core.Spatial;

namespace EngrCAD.Core.Geometry2;

/// <summary>
/// One proper crossing found by <see cref="CurvedRegion2dValidation"/>: which edge of which
/// chain crosses which, and roughly where. Loop and edge indices are into the chain list the
/// check was given.
/// </summary>
public readonly record struct CurvedLoopCrossing(
    int FirstLoop, int FirstEdge, int SecondLoop, int SecondEdge, Vector2d Point)
{
    /// <summary>True when both edges belong to the same chain — a self-intersection.</summary>
    public bool IsSelfIntersection => FirstLoop == SecondLoop;
}

/// <summary>
/// Simplicity validation for closed CURVED chains — the twin of
/// <see cref="Region2dValidation"/> for the lines-and-arcs tier, and for the same reason:
/// every consumer of a <see cref="CurvedRegion2d"/> (parity containment, the exact boolean,
/// the exact offset, <c>Profile.FromCurvedRegion</c>) assumes each chain is a simple closed
/// curve, and a chain that crosses itself has no interior until you pick a fill rule.
///
/// <para><b>What counts is a CROSSING, not a contact.</b> Two edges that meet tangentially
/// are accepted deliberately: for lines and arcs a tangency is always a TOUCH — two distinct
/// circles tangent at a point, or a line tangent to a circle, stay locally on one side of
/// each other — so it never separates the boundary into interior and exterior the way a
/// transversal crossing does. The discriminator is the cross product of the two UNIT tangents
/// at the contact, which is a sine and therefore dimensionless: the guard on it is the one
/// deliberate absolute epsilon here, exactly as the epsilon ladder in CLAUDE.md permits for
/// an angular quantity. Contacts at either edge's own endpoints are skipped, since chains
/// legitimately meet at their joints.</para>
///
/// <para><b>Cost.</b> Candidate pairs come from a <see cref="Bvh"/> over each edge's TIGHT
/// box (an arc contributes its cardinal extremes, so the box is the arc's and not its
/// chord's), which is what makes a many-edge profile O(n log n) rather than the O(n²) an
/// all-pairs scan imposes on every curved region ever built. Below
/// <see cref="Region2dValidation.BruteForceLimit"/> edges the all-pairs scan beats building a
/// tree and is used instead — the same threshold, and the same reason, as the polygonal
/// validator.</para>
/// </summary>
public static class CurvedRegion2dValidation
{
    /// <summary>
    /// Dimensionless guard separating a transversal crossing from a tangential touch: the
    /// sine of the angle between the two unit tangents at the contact. Absolute on purpose —
    /// radians carry no model units — and the same value the check has always used.
    /// </summary>
    public const double TangencySine = 1e-9;

    /// <summary>Finds a proper crossing within one closed curved chain, if there is one.</summary>
    public static bool TryFindSelfIntersection(
        IReadOnlyList<CurvedEdge2d> loop, out CurvedLoopCrossing crossing,
        double tolerance = CurvedRegion2d.BoundaryTolerance)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return TryFindCrossing([loop], out crossing, acrossLoops: true, tolerance);
    }

    /// <summary>
    /// Finds a proper crossing anywhere among <paramref name="loops"/> — inside one chain or,
    /// when <paramref name="acrossLoops"/> is set, between two — and reports the first one
    /// found. Which one that is depends on the traversal order and is not part of the
    /// contract; the existence answer is.
    /// </summary>
    public static bool TryFindCrossing(
        IReadOnlyList<IReadOnlyList<CurvedEdge2d>> loops, out CurvedLoopCrossing crossing,
        bool acrossLoops = true, double tolerance = CurvedRegion2d.BoundaryTolerance)
    {
        ArgumentNullException.ThrowIfNull(loops);
        crossing = default;
        if (!(tolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        // One index space over every chain's edges, so a single sweep answers both the self
        // and the cross-chain question (they are the same question about the same edge set).
        int total = 0;
        foreach (var loop in loops)
            total += loop?.Count ?? 0;
        if (total < 2)
            return false;

        var loopOf = new int[total];
        var indexIn = new int[total];
        var edges = new CurvedEdge2d[total];
        int next = 0;
        for (int l = 0; l < loops.Count; l++)
        {
            var loop = loops[l];
            if (loop is null)
                continue;
            for (int i = 0; i < loop.Count; i++)
            {
                loopOf[next] = l;
                indexIn[next] = i;
                edges[next] = loop[i];
                next++;
            }
        }

        var contacts = new List<CurveIntersection2d.Contact>(4);
        if (total <= Region2dValidation.BruteForceLimit)
        {
            for (int i = 0; i < total; i++)
            {
                for (int j = i + 1; j < total; j++)
                {
                    if (!acrossLoops && loopOf[i] != loopOf[j])
                        continue;
                    if (TestPair(loops, loopOf, indexIn, edges, i, j, tolerance, contacts, out crossing))
                        return true;
                }
            }
            return false;
        }

        var boxes = new Aabb[total];
        for (int i = 0; i < total; i++)
            boxes[i] = edges[i].Bounds();
        var bvh = Bvh.Build(boxes);
        var candidates = new List<int>();
        for (int i = 0; i < total; i++)
        {
            candidates.Clear();
            bvh.Query(boxes[i], candidates);
            foreach (int j in candidates)
            {
                // Each unordered pair is tested once (the tree reports both orders).
                if (j <= i || (!acrossLoops && loopOf[i] != loopOf[j]))
                    continue;
                if (TestPair(loops, loopOf, indexIn, edges, i, j, tolerance, contacts, out crossing))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Throws an <see cref="ArgumentException"/> naming the chains, the edges and the
    /// crossing location when <paramref name="loops"/> are not simple.
    /// <paramref name="describe"/> turns a chain index into the caller's own vocabulary
    /// ("the region's outer chain", "hole chain 2").
    /// </summary>
    public static void Require(
        IReadOnlyList<IReadOnlyList<CurvedEdge2d>> loops, Func<int, string> describe,
        string? parameterName = null, bool acrossLoops = true,
        double tolerance = CurvedRegion2d.BoundaryTolerance)
    {
        ArgumentNullException.ThrowIfNull(describe);
        if (!TryFindCrossing(loops, out var crossing, acrossLoops, tolerance))
            return;
        string message = crossing.IsSelfIntersection
            ? $"{describe(crossing.FirstLoop)} crosses itself: edge {crossing.FirstEdge} crosses "
              + $"edge {crossing.SecondEdge} near ({crossing.Point.X}, {crossing.Point.Y}). "
              + "A region's chains must be simple closed curves — every area, containment and "
              + "boolean answer below this point would depend on an arbitrary fill rule."
            : $"{describe(crossing.FirstLoop)} crosses {describe(crossing.SecondLoop)}: edge "
              + $"{crossing.FirstEdge} crosses edge {crossing.SecondEdge} near "
              + $"({crossing.Point.X}, {crossing.Point.Y}).";
        throw parameterName is null
            ? new ArgumentException(message)
            : new ArgumentException(message, parameterName);
    }

    /// <summary>Proper crossing test for one edge pair. Edges adjacent in a chain share a
    /// joint by construction and are skipped; so is any contact AT an endpoint of either
    /// edge, which is a touch rather than a crossing.</summary>
    private static bool TestPair(
        IReadOnlyList<IReadOnlyList<CurvedEdge2d>> loops, int[] loopOf, int[] indexIn,
        CurvedEdge2d[] edges, int i, int j, double tolerance,
        List<CurveIntersection2d.Contact> contacts, out CurvedLoopCrossing crossing)
    {
        crossing = default;
        var a = edges[i];
        var b = edges[j];
        if (loopOf[i] == loopOf[j])
        {
            int count = loops[loopOf[i]].Count;
            int ii = indexIn[i], ij = indexIn[j];
            if ((ii + 1) % count == ij || (ij + 1) % count == ii)
                return false;
        }

        contacts.Clear();
        CurveIntersection2d.Intersect(a, b, tolerance, contacts);
        foreach (var contact in contacts)
        {
            if (IsEndpoint(a, contact.Point, tolerance) || IsEndpoint(b, contact.Point, tolerance))
                continue;
            double turn = a.TangentAt(contact.Ta).Cross(b.TangentAt(contact.Tb));
            if (Math.Abs(turn) <= TangencySine)
                continue;
            crossing = new CurvedLoopCrossing(loopOf[i], indexIn[i], loopOf[j], indexIn[j], contact.Point);
            return true;
        }
        return false;
    }

    private static bool IsEndpoint(in CurvedEdge2d edge, in Vector2d point, double tolerance) =>
        edge.Start.DistanceTo(point) <= tolerance || edge.End.DistanceTo(point) <= tolerance;
}
