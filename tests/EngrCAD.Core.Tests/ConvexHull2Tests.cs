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

        // Strict convexity, CCW: every consecutive turn is a left turn.
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            var c = hull[(i + 2) % hull.Length];
            Assert.True((b - a).Cross(c - b) > 0, $"turn at {i} is not strictly left");
        }

        // Every input point on or inside every hull edge's left half-plane.
        foreach (var p in points)
        {
            for (int i = 0; i < hull.Length; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % hull.Length];
                Assert.True((b - a).Cross(p - a) >= -1e-12, "input point outside hull");
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
    public void Indices_PointBackIntoTheInput()
    {
        var points = new List<Vector2d> { (0, 0), (2, 0), (1, 5), (1, 1) };
        var indices = ConvexHull2.ComputeIndices(points);
        Assert.Equal([0, 1, 2], indices);
    }
}
