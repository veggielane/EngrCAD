using EngrCAD.Core.Spatial;

namespace EngrCAD.Core.Geometry2;

/// <summary>
/// One proper crossing found by <see cref="Region2dValidation"/>: which segment of which
/// loop crosses which, and roughly where. Loop and segment indices are into the loop list
/// the check was given; <see cref="Point"/> is the segment-intersection point, computed
/// only for the message (the DECISION is exact, the coordinate is not).
/// </summary>
public readonly record struct LoopCrossing(
    int FirstLoop, int FirstSegment, int SecondLoop, int SecondSegment, Vector2d Point)
{
    /// <summary>True when both segments belong to the same loop — a self-intersection.</summary>
    public bool IsSelfIntersection => FirstLoop == SecondLoop;
}

/// <summary>
/// Simplicity validation for closed polygonal loops: does any pair of segments PROPERLY
/// CROSS, either within one loop (a bow-tie or a figure-eight) or between two loops?
///
/// <para><b>Why it exists.</b> Every consumer of <see cref="Region2d"/> — parity
/// containment, the arrangement-based booleans, <c>Region2dOffset</c>,
/// <c>Profile.FromRegion</c> — assumes each loop is a simple closed curve. A
/// self-intersecting outer loop is not a region at all: its "interior" depends on which
/// fill rule you happen to apply, so the area, the containment test and every boolean
/// silently disagree. That silence is the bug this replaces.</para>
///
/// <para><b>What counts.</b> A PROPER crossing only: two segment interiors strictly on
/// opposite sides of each other, decided by <see cref="Predicates2d.Orient2dSign"/> and
/// therefore exact for all finite doubles. Loops that merely TOUCH — at a shared vertex,
/// or along a collinear run — are not flagged, which is the same convention
/// <see cref="Region2d"/> already documented for hole-versus-outer contact. A loop that
/// pinches itself at an exactly shared vertex is therefore accepted; it is degenerate but
/// unambiguous under even–odd parity, and rejecting it would refuse legitimate flattened
/// geometry whose chords happen to meet.</para>
///
/// <para><b>Cost.</b> Candidate pairs come from a <see cref="Bvh"/> over the segment boxes,
/// so a well-behaved sketch costs O(n log n) rather than the O(n²) an all-pairs scan would
/// impose on every region ever built. Below <see cref="BruteForceLimit"/> segments the
/// all-pairs scan is faster than building a tree and is used instead.</para>
/// </summary>
public static class Region2dValidation
{
    /// <summary>Total segment count below which all-pairs testing beats building a BVH.</summary>
    internal const int BruteForceLimit = 24;

    /// <summary>
    /// Finds a proper crossing within <paramref name="loop"/>, if there is one.
    /// </summary>
    public static bool TryFindSelfIntersection(IReadOnlyList<Vector2d> loop, out LoopCrossing crossing)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return TryFindCrossing([loop], out crossing);
    }

    /// <summary>
    /// Finds a proper crossing anywhere among <paramref name="loops"/> — inside one loop or,
    /// when <paramref name="acrossLoops"/> is set, between two — and reports the first one
    /// found. Which one that is depends on the traversal order and is not part of the
    /// contract; the existence answer is.
    ///
    /// <para>Clear <paramref name="acrossLoops"/> when the loops are not yet known to belong
    /// to one region: an unsorted bag (the input to <see cref="Region2d.FromLoops"/>) can
    /// legitimately contain loops that overlap each other, but no loop may ever cross
    /// itself.</para>
    /// </summary>
    public static bool TryFindCrossing(
        IReadOnlyList<IReadOnlyList<Vector2d>> loops, out LoopCrossing crossing, bool acrossLoops = true)
    {
        ArgumentNullException.ThrowIfNull(loops);
        crossing = default;

        // Flatten every loop's segments into one index space so a single sweep covers both
        // the self and the cross-loop cases (they are the same question about the same
        // segment set, and splitting them would double the work AND the code).
        int total = 0;
        foreach (var loop in loops)
            total += loop is null ? 0 : (loop.Count >= 3 ? loop.Count : 0);
        if (total < 4)
            return false; // fewer than two testable pairs

        var loopOf = new int[total];
        var indexIn = new int[total];
        var starts = new Vector2d[total];
        var ends = new Vector2d[total];
        int next = 0;
        for (int l = 0; l < loops.Count; l++)
        {
            var loop = loops[l];
            if (loop is null || loop.Count < 3)
                continue;
            for (int i = 0; i < loop.Count; i++)
            {
                loopOf[next] = l;
                indexIn[next] = i;
                starts[next] = loop[i];
                ends[next] = loop[(i + 1) % loop.Count];
                next++;
            }
        }

        if (total <= BruteForceLimit)
        {
            for (int i = 0; i < total; i++)
            {
                for (int j = i + 1; j < total; j++)
                {
                    if (!acrossLoops && loopOf[i] != loopOf[j])
                        continue;
                    if (TestPair(loops, loopOf, indexIn, starts, ends, i, j, out crossing))
                        return true;
                }
            }
            return false;
        }

        var boxes = new Aabb[total];
        for (int i = 0; i < total; i++)
        {
            boxes[i] = new Aabb(
                new Vector3d(Math.Min(starts[i].X, ends[i].X), Math.Min(starts[i].Y, ends[i].Y), 0),
                new Vector3d(Math.Max(starts[i].X, ends[i].X), Math.Max(starts[i].Y, ends[i].Y), 0));
        }
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
                if (TestPair(loops, loopOf, indexIn, starts, ends, i, j, out crossing))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Throws an <see cref="ArgumentException"/> naming the loops, the segments and the
    /// crossing location when <paramref name="loops"/> are not simple.
    /// <paramref name="describe"/> turns a loop index into the caller's own vocabulary
    /// ("the outer loop", "hole loop 2", "sketch loop 0").
    /// </summary>
    public static void Require(
        IReadOnlyList<IReadOnlyList<Vector2d>> loops, Func<int, string> describe,
        string? parameterName = null, bool acrossLoops = true)
    {
        ArgumentNullException.ThrowIfNull(describe);
        if (!TryFindCrossing(loops, out var crossing, acrossLoops))
            return;
        string message = crossing.IsSelfIntersection
            ? $"{describe(crossing.FirstLoop)} crosses itself: segment {crossing.FirstSegment} crosses "
              + $"segment {crossing.SecondSegment} near ({crossing.Point.X}, {crossing.Point.Y}). "
              + "A region's loops must be simple closed curves — every area, containment and boolean "
              + "answer below this point would depend on an arbitrary fill rule."
            : $"{describe(crossing.FirstLoop)} crosses {describe(crossing.SecondLoop)}: segment "
              + $"{crossing.FirstSegment} crosses segment {crossing.SecondSegment} near "
              + $"({crossing.Point.X}, {crossing.Point.Y}).";
        throw parameterName is null
            ? new ArgumentException(message)
            : new ArgumentException(message, parameterName);
    }

    /// <summary>Proper crossing test for one segment pair; adjacent segments of the same
    /// loop share an endpoint and are skipped (they cannot cross without being degenerate).</summary>
    private static bool TestPair(
        IReadOnlyList<IReadOnlyList<Vector2d>> loops, int[] loopOf, int[] indexIn,
        Vector2d[] starts, Vector2d[] ends, int i, int j, out LoopCrossing crossing)
    {
        crossing = default;
        if (loopOf[i] == loopOf[j])
        {
            int count = loops[loopOf[i]].Count;
            int a = indexIn[i], b = indexIn[j];
            int gap = Math.Abs(a - b);
            if (gap <= 1 || gap == count - 1)
                return false;
        }

        var p = starts[i];
        var q = ends[i];
        var r = starts[j];
        var s = ends[j];
        int o1 = Predicates2d.Orient2dSign(p, q, r);
        int o2 = Predicates2d.Orient2dSign(p, q, s);
        int o3 = Predicates2d.Orient2dSign(r, s, p);
        int o4 = Predicates2d.Orient2dSign(r, s, q);
        if (o1 * o2 >= 0 || o3 * o4 >= 0)
            return false;

        crossing = new LoopCrossing(loopOf[i], indexIn[i], loopOf[j], indexIn[j], IntersectionOf(p, q, r, s));
        return true;
    }

    /// <summary>The crossing point of two segments already KNOWN to cross properly — a
    /// message coordinate only, so the ordinary parametric formula is fine (the denominator
    /// cannot vanish: a zero cross product would have made the orientation signs agree).</summary>
    private static Vector2d IntersectionOf(in Vector2d p, in Vector2d q, in Vector2d r, in Vector2d s)
    {
        var d1 = q - p;
        var d2 = s - r;
        double denominator = d1.Cross(d2);
        if (denominator == 0)
            return p;
        return p + d1 * ((r - p).Cross(d2) / denominator);
    }
}
