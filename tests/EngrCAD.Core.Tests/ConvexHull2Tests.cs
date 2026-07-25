using Xunit;

namespace EngrCAD.Core.Tests;

public class ConvexHull2Tests
{
    [Fact]
    public void SquareWithInteriorPoints_YieldsTheFourCornersCcw()
    {
        var points = new List<Vector2d>
        {
            (0.5, 0.5), (1, 0), (0.2, 0.8), (1, 1), (0, 0), (0.7, 0.3), (0, 1), (0.5, 1), // (0.5,1) on edge
        };
        var hull = ConvexHull2.Compute(points);

        Assert.Equal(4, hull.Length);
        Assert.Equal(new Vector2d(0, 0), hull[0]); // lexicographic start
        Assert.Equal([(0, 0), (1, 0), (1, 1), (0, 1)], hull.Select(p => ((double, double))(p.X, p.Y)));
    }

    [Fact]
    public void HullIsCcwAndContainsAllPoints()
    {
        var random = new Random(42);
        var points = new List<Vector2d>();
        for (int i = 0; i < 500; i++)
            points.Add((random.NextDouble() * 4 - 2, random.NextDouble() * 2 - 1));

        var hull = ConvexHull2.Compute(points);
        Assert.True(hull.Length >= 3);
        AssertExactlyConvexAndEncloses(hull, points);
    }

    /// <summary>
    /// The hull's contract, checked against exact (BigInteger) arithmetic rather than a
    /// tolerance: every consecutive turn is STRICTLY left (so the polygon is strictly
    /// convex and carries no collinear or duplicated vertices), and every input point is
    /// on or inside every edge's left half-plane. Both properties are decided exactly,
    /// which is only achievable because the hull's turn test is
    /// <see cref="Predicates2d.Orient2dSign"/>; a naive cross product cannot guarantee
    /// them on nearly-collinear input.
    /// </summary>
    private static void AssertExactlyConvexAndEncloses(
        IReadOnlyList<Vector2d> hull, IReadOnlyList<Vector2d> points)
    {
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            var c = hull[(i + 2) % hull.Count];
            Assert.True(ExactReference.Orient2dSign(a, b, c) > 0, $"turn at {i} is not strictly left");
        }
        foreach (var p in points)
        {
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % hull.Count];
                Assert.True(ExactReference.Orient2dSign(a, b, p) >= 0, $"input point {p} lies outside hull edge {i}");
            }
        }
    }

    [Fact]
    public void Degenerate_CoincidentAndCollinear()
    {
        Assert.Throws<ArgumentException>(() => ConvexHull2.Compute(new List<Vector2d>()));

        var single = ConvexHull2.Compute([(1, 2), (1, 2), (1, 2)]);
        Assert.Equal([new Vector2d(1, 2)], single);

        var collinear = ConvexHull2.Compute([(1, 1), (3, 3), (0, 0), (2, 2)]);
        Assert.Equal(2, collinear.Length);
        Assert.Equal(new Vector2d(0, 0), collinear[0]);
        Assert.Equal(new Vector2d(3, 3), collinear[1]);
    }

    [Fact]
    public void Degenerate_LongCollinearRunsAndDuplicates_KeepOnlyTheCorners()
    {
        // A unit square whose edges carry 20 exactly-collinear interior samples each, and
        // whose corners are repeated three times. Collinear runs are the case a naive
        // cross product decides by rounding: samples on an axis-aligned edge give an exact
        // zero, but the diagonal-ish accumulation inside the chain does not have to.
        var points = new List<Vector2d>();
        for (int i = 0; i <= 20; i++)
        {
            double t = i / 20.0;
            points.Add((t, 0));
            points.Add((1, t));
            points.Add((1 - t, 1));
            points.Add((0, 1 - t));
        }
        foreach (var corner in (ReadOnlySpan<Vector2d>)[(0, 0), (1, 0), (1, 1), (0, 1)])
        {
            points.Add(corner);
            points.Add(corner);
            points.Add(corner);
        }

        var hull = ConvexHull2.Compute(points);
        Assert.Equal([(0, 0), (1, 0), (1, 1), (0, 1)], hull.Select(p => ((double, double))(p.X, p.Y)));
        AssertExactlyConvexAndEncloses(hull, points);
    }

    [Fact]
    public void Degenerate_EveryPointDuplicated_MatchesTheDeduplicatedHull()
    {
        var random = new Random(7);
        var unique = new List<Vector2d>();
        for (int i = 0; i < 200; i++)
            unique.Add((random.NextDouble() * 6 - 3, random.NextDouble() * 6 - 3));

        // Duplicates are exactly collinear with every other pair, so the exact turn test
        // must pop them; a hull that keeps one would have a zero-length edge.
        var duplicated = new List<Vector2d>();
        foreach (var p in unique)
        {
            duplicated.Add(p);
            duplicated.Add(p);
            duplicated.Add(p);
        }

        var hull = ConvexHull2.Compute(unique);
        var hullWithDuplicates = ConvexHull2.Compute(duplicated);
        Assert.Equal(hull, hullWithDuplicates);
        AssertExactlyConvexAndEncloses(hullWithDuplicates, duplicated);
    }

    [Fact]
    public void NearCollinear_UlpGridAtKettnerMagnitudes_StaysExactlyConvex()
    {
        // The Kettner et al. robustness input reused as a hull: an ulp-grid around
        // (0.5, 0.5) plus two far points on the line y = x through it. The orientation
        // queries here are exactly the ones where a naive determinant provably disagrees
        // with exact arithmetic (see Predicates2dTests.Orient2d_KettnerGrid_*).
        double ulp = Math.BitIncrement(0.5) - 0.5; // 2^-53
        var points = new List<Vector2d>();
        for (int i = 0; i < 40; i++)
        {
            for (int j = 0; j < 40; j++)
                points.Add((0.5 + i * ulp, 0.5 + j * ulp));
        }
        points.Add((12, 12));
        points.Add((24, 24));

        var hull = ConvexHull2.Compute(points);
        Assert.True(hull.Length >= 3);
        AssertExactlyConvexAndEncloses(hull, points);

        // (12, 12) lies exactly on the segment from (0.5, 0.5) to (24, 24), so a strictly
        // convex hull must drop it.
        Assert.DoesNotContain(new Vector2d(12, 12), hull);
    }

    [Fact]
    public void NearCollinear_MixedMagnitudeSlivers_ExactHullSurvivesWhereNaiveDoesNot()
    {
        // The hostile family for a monotone chain: points scattered along one line but
        // over a wide EXPONENT range, each nudged by a few ulps. Coordinate differences
        // then stop being exact (Sterbenz no longer applies across binades), which is
        // precisely when the raw cross product starts reporting inconsistent turns and the
        // chain pops a vertex that belongs to the hull.
        int naiveFailures = 0;
        for (int seed = 0; seed < 120; seed++)
        {
            var random = new Random(10_000 + seed);
            var points = new List<Vector2d>();
            for (int i = 0; i < 60; i++)
            {
                double x = (random.NextDouble() * 2 - 1) * Math.Pow(2, random.Next(-40, 40));
                double y = x * 0.1;
                int nudges = random.Next(0, 3);
                for (int k = 0; k < nudges; k++)
                    y = random.Next(2) == 0 ? Math.BitIncrement(y) : Math.BitDecrement(y);
                points.Add((x, y));
            }

            // The contract holds on every one of these clouds.
            AssertExactlyConvexAndEncloses(ConvexHull2.Compute(points), points);

            if (!IsExactlyConvex(NaiveMonotoneChain(points)))
                naiveFailures++;
        }

        // ... and the naive turn test genuinely fails on this family, so the switch to the
        // adaptive predicate was a fix, not a cosmetic refactor. (Measured ~7% of clouds;
        // asserting "at least one" keeps the test robust to RNG changes.)
        Assert.True(naiveFailures > 0, "the naive chain never failed; the input family stopped being hostile");
    }

    private static bool IsExactlyConvex(IReadOnlyList<Vector2d> hull)
    {
        for (int i = 0; i < hull.Count; i++)
        {
            if (ExactReference.Orient2dSign(hull[i], hull[(i + 1) % hull.Count], hull[(i + 2) % hull.Count]) <= 0)
                return false;
        }
        return true;
    }

    /// <summary>The pre-<see cref="Predicates2d"/> turn test, kept only to demonstrate that it fails.</summary>
    private static List<Vector2d> NaiveMonotoneChain(IReadOnlyList<Vector2d> points)
    {
        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        var hull = new List<Vector2d>();
        for (int pass = 0; pass < 2; pass++)
        {
            int chainStart = hull.Count;
            for (int s = 0; s < sorted.Count; s++)
            {
                var p = sorted[pass == 0 ? s : sorted.Count - 1 - s];
                while (hull.Count - chainStart >= 2 &&
                       (hull[^1] - hull[^2]).Cross(p - hull[^1]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    [Fact]
    public void Indices_PointBackIntoTheInput()
    {
        var points = new List<Vector2d> { (0, 0), (2, 0), (1, 5), (1, 1) };
        var indices = ConvexHull2.ComputeIndices(points);
        Assert.Equal([0, 1, 2], indices);
    }
}
