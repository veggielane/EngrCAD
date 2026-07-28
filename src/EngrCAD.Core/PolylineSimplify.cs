namespace EngrCAD.Core;

/// <summary>
/// Tolerance-driven polyline simplification (Douglas–Peucker; geometry3Sharp's
/// <c>PolySimplification2</c> role) in 2D and 3D — the inexact companion to
/// <c>Region2d.WithoutCollinearVertices</c>, which drops only vertices that provably change
/// nothing. Use this on traced intersection curves, imported profiles and any polyline whose
/// sample density says more about how it was produced than about its shape.
///
/// <para><b>What it guarantees.</b> Every dropped vertex lies within <c>tolerance</c> of the
/// retained chord that replaced it, measured as a true point-to-SEGMENT distance (a chord
/// the polyline doubles back past measures to its end, not to its infinite extension).
/// Endpoints are always kept. The recursion always splits at the FARTHEST vertex, so the
/// result is deterministic — no ordering effects, no RNG, identical on every run. Nothing is
/// moved: the output is a subsequence of the input, so retained points are bit-for-bit the
/// ones that came in and a simplified curve still interpolates them exactly.</para>
///
/// <para><b>What it does not guarantee.</b> Simplification is not topology-preserving: two
/// far-apart stretches of a wiggly loop can be pulled onto each other, so a simplified loop
/// may self-intersect where the input did not. That is why nothing in the kernel simplifies
/// implicitly and why callers feeding the result to <c>Region2d</c> get
/// <c>Region2dValidation</c>'s refusal for free.</para>
///
/// <para><b>The tolerance is absolute and in model units</b>, deliberately: it is a deviation
/// the caller CHOOSES to accept (a chord tolerance, a display resolution, an import cleanup
/// band), not a degeneracy guard — those are relative per the epsilon ladder in CLAUDE.md.
/// The only guard here is the exact-zero test on a chord's squared length, which selects the
/// point-to-point branch rather than accepting anything.</para>
/// </summary>
public static class PolylineSimplify
{
    // ---- 2D ----

    /// <summary>Douglas–Peucker over an OPEN 2D polyline; the first and last points are
    /// always kept. A non-positive tolerance returns the input unchanged.</summary>
    public static IReadOnlyList<Vector2d> Simplify(IReadOnlyList<Vector2d> polyline, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        if (!(tolerance > 0) || polyline.Count <= 2)
            return polyline;
        var metric = new Chords2d(polyline);
        return Open(ref metric, polyline, tolerance);
    }

    /// <summary>
    /// Douglas–Peucker over a CLOSED 2D loop (implicitly closed — the last point joins back
    /// to the first, the convention everywhere in <c>Geometry2</c>). Never returns fewer
    /// than 3 points.
    /// </summary>
    public static IReadOnlyList<Vector2d> SimplifyLoop(IReadOnlyList<Vector2d> loop, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (!(tolerance > 0) || loop.Count <= 3)
            return loop;
        var metric = new Chords2d(loop);
        return Closed(ref metric, loop, tolerance);
    }

    // ---- 3D ----

    /// <summary>Douglas–Peucker over an OPEN 3D polyline — the traced-intersection-curve
    /// case; the first and last points are always kept.</summary>
    public static IReadOnlyList<Vector3d> Simplify(IReadOnlyList<Vector3d> polyline, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        if (!(tolerance > 0) || polyline.Count <= 2)
            return polyline;
        var metric = new Chords3d(polyline);
        return Open(ref metric, polyline, tolerance);
    }

    /// <summary>Douglas–Peucker over a CLOSED 3D loop (implicitly closed). Never returns
    /// fewer than 3 points.</summary>
    public static IReadOnlyList<Vector3d> SimplifyLoop(IReadOnlyList<Vector3d> loop, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (!(tolerance > 0) || loop.Count <= 3)
            return loop;
        var metric = new Chords3d(loop);
        return Closed(ref metric, loop, tolerance);
    }

    // ---- deviation reporting ----

    /// <summary>The largest distance from any point of <paramref name="original"/> to the
    /// polyline through <paramref name="simplified"/> — the honest measure of what a
    /// simplification cost, and never larger than the tolerance that produced it.</summary>
    public static double MaxDeviation(
        IReadOnlyList<Vector2d> original, IReadOnlyList<Vector2d> simplified, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(simplified);
        if (simplified.Count == 0)
            return double.PositiveInfinity;
        int chords = closed ? simplified.Count : simplified.Count - 1;
        double worst = 0;
        foreach (var p in original)
        {
            double nearest = double.PositiveInfinity;
            for (int i = 0; i < chords; i++)
            {
                nearest = Math.Min(nearest, Chords2d.SquaredDistanceToSegment(
                    p, simplified[i], simplified[(i + 1) % simplified.Count]));
            }
            worst = Math.Max(worst, nearest);
        }
        return Math.Sqrt(worst);
    }

    /// <summary>3D <see cref="MaxDeviation(IReadOnlyList{Vector2d}, IReadOnlyList{Vector2d}, bool)"/>.</summary>
    public static double MaxDeviation(
        IReadOnlyList<Vector3d> original, IReadOnlyList<Vector3d> simplified, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(simplified);
        if (simplified.Count == 0)
            return double.PositiveInfinity;
        int chords = closed ? simplified.Count : simplified.Count - 1;
        double worst = 0;
        foreach (var p in original)
        {
            double nearest = double.PositiveInfinity;
            for (int i = 0; i < chords; i++)
            {
                nearest = Math.Min(nearest, Chords3d.SquaredDistanceToSegment(
                    p, simplified[i], simplified[(i + 1) % simplified.Count]));
            }
            worst = Math.Max(worst, nearest);
        }
        return Math.Sqrt(worst);
    }

    // ---- the algorithm, written once ----

    /// <summary>
    /// Point-to-chord distances over an index space that may run past the end of the list
    /// (wrapping), so the open and closed cases share one recursion. A struct constraint,
    /// as with <c>Bvh.Nearest&lt;TMetric&gt;</c>: the 2D and 3D instantiations are separate
    /// generic bodies with no interface dispatch and no boxing.
    /// </summary>
    private interface IChordMetric
    {
        double SquaredDistanceToChord(int point, int a, int b);
        double SquaredSeparation(int a, int b);
    }

    private static IReadOnlyList<TPoint> Open<TMetric, TPoint>(
        ref TMetric metric, IReadOnlyList<TPoint> points, double tolerance)
        where TMetric : struct, IChordMetric
    {
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        Recurse(ref metric, keep, 0, points.Count - 1, tolerance * tolerance);
        return Collect(points, keep);
    }

    /// <remarks>
    /// A loop has no natural endpoints, so it is cut into two open chains before recursing.
    /// The cut is the loop's first vertex and the vertex FARTHEST from it — the standard
    /// choice, and the one that keeps the answer from being decided by a near-degenerate
    /// first chord. If the tolerance would collapse the loop to a segment, the vertex
    /// farthest from the anchor chord is kept as well, so the output is still a loop.
    /// </remarks>
    private static IReadOnlyList<TPoint> Closed<TMetric, TPoint>(
        ref TMetric metric, IReadOnlyList<TPoint> loop, double tolerance)
        where TMetric : struct, IChordMetric
    {
        int n = loop.Count;
        int opposite = 0;
        double farthest = -1;
        for (int i = 1; i < n; i++)
        {
            double d = metric.SquaredSeparation(0, i);
            if (d > farthest)
            {
                farthest = d;
                opposite = i;
            }
        }
        if (opposite == 0)
            return loop; // every point coincides; nothing to simplify

        var keep = new bool[n];
        keep[0] = true;
        keep[opposite] = true;
        double squared = tolerance * tolerance;
        Recurse(ref metric, keep, 0, opposite, squared);
        Recurse(ref metric, keep, opposite, n, squared); // wraps back to index 0

        int kept = 0;
        foreach (bool k in keep)
        {
            if (k)
                kept++;
        }
        if (kept < 3)
        {
            int third = -1;
            double best = -1;
            for (int i = 0; i < n; i++)
            {
                if (keep[i])
                    continue;
                double d = metric.SquaredDistanceToChord(i, 0, opposite);
                if (d > best)
                {
                    best = d;
                    third = i;
                }
            }
            if (third < 0)
                return loop;
            keep[third] = true;
        }
        return Collect(loop, keep);
    }

    /// <summary>Marks the vertices to keep strictly between two already-kept anchors;
    /// <paramref name="last"/> may run past the end of the list, wrapping.</summary>
    private static void Recurse<TMetric>(
        ref TMetric metric, bool[] keep, int first, int last, double squaredTolerance)
        where TMetric : struct, IChordMetric
    {
        if (last <= first + 1)
            return;
        int split = -1;
        double worst = squaredTolerance;
        for (int i = first + 1; i < last; i++)
        {
            double d = metric.SquaredDistanceToChord(i, first, last);
            if (d > worst)
            {
                worst = d;
                split = i;
            }
        }
        if (split < 0)
            return;
        keep[split % keep.Length] = true;
        Recurse(ref metric, keep, first, split, squaredTolerance);
        Recurse(ref metric, keep, split, last, squaredTolerance);
    }

    private static IReadOnlyList<TPoint> Collect<TPoint>(IReadOnlyList<TPoint> points, bool[] keep)
    {
        var result = new List<TPoint>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }
        return result;
    }

    private readonly struct Chords2d(IReadOnlyList<Vector2d> points) : IChordMetric
    {
        private Vector2d At(int index) => points[index % points.Count];

        public double SquaredSeparation(int a, int b) => (At(b) - At(a)).LengthSquared;

        public double SquaredDistanceToChord(int point, int a, int b) =>
            SquaredDistanceToSegment(At(point), At(a), At(b));

        internal static double SquaredDistanceToSegment(in Vector2d p, in Vector2d a, in Vector2d b)
        {
            var ab = b - a;
            double lengthSquared = ab.LengthSquared;
            // Exact-zero guard, not a tolerance: a collapsed chord IS a point.
            if (lengthSquared == 0)
                return (p - a).LengthSquared;
            double t = (p - a).Dot(ab) / lengthSquared;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return (p - (a + ab * t)).LengthSquared;
        }
    }

    private readonly struct Chords3d(IReadOnlyList<Vector3d> points) : IChordMetric
    {
        private Vector3d At(int index) => points[index % points.Count];

        public double SquaredSeparation(int a, int b) => (At(b) - At(a)).LengthSquared;

        public double SquaredDistanceToChord(int point, int a, int b) =>
            SquaredDistanceToSegment(At(point), At(a), At(b));

        internal static double SquaredDistanceToSegment(in Vector3d p, in Vector3d a, in Vector3d b)
        {
            var ab = b - a;
            double lengthSquared = ab.LengthSquared;
            if (lengthSquared == 0)
                return (p - a).LengthSquared;
            double t = (p - a).Dot(ab) / lengthSquared;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return (p - (a + ab * t)).LengthSquared;
        }
    }
}
