using Xunit;

namespace EngrCAD.Core.Tests;

public class Fitting2dTests
{
    private static Vector2d Rotate(in Vector2d p, double angle) => new(
        p.X * Math.Cos(angle) - p.Y * Math.Sin(angle),
        p.X * Math.Sin(angle) + p.Y * Math.Cos(angle));

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(-1.2)]
    [InlineData(2.9)]
    public void MinAreaBox_RecoversARotatedRectangleExactly(double angle)
    {
        // A 3×1 rectangle outline (corners + edge midpoints + interior), rotated.
        const double w = 3, h = 1;
        var points = new List<Vector2d>();
        foreach (var (x, y) in new (double, double)[]
                 { (0, 0), (w, 0), (w, h), (0, h), (w / 2, 0), (w, h / 2), (w / 2, h), (0, h / 2), (1, 0.5), (2, 0.2) })
            points.Add(Rotate((x, y), angle) + new Vector2d(5, -2));

        var box = Fitting2d.MinAreaBox(points);

        Assert.Equal(w * h, box.Area, 9);
        double maxExtent = Math.Max(box.HalfExtents.X, box.HalfExtents.Y);
        double minExtent = Math.Min(box.HalfExtents.X, box.HalfExtents.Y);
        Assert.Equal(w / 2, maxExtent, 9);
        Assert.Equal(h / 2, minExtent, 9);

        var expectedCenter = Rotate((w / 2, h / 2), angle) + new Vector2d(5, -2);
        Assert.True(box.Center.AreEqual(expectedCenter, Tolerance.Default));
        Assert.All(points, p => Assert.True(box.Contains(p, Tolerance.Default)));
    }

    [Fact]
    public void MinAreaBox_BeatsTheAxisAlignedBoxOnATiltedShape()
    {
        // A thin diagonal strip: the AABB is fat, the oriented box is thin.
        var points = new List<Vector2d>();
        for (int i = 0; i <= 50; i++)
        {
            double t = i / 50.0 * 10;
            points.Add((t, t + 0.1));
            points.Add((t, t - 0.1));
        }
        var box = Fitting2d.MinAreaBox(points);
        double aabbArea = 10.0 * 10.2; // x-range 10, y-range 10.2
        Assert.True(box.Area < aabbArea / 10, $"oriented area {box.Area} should crush AABB {aabbArea}");
        Assert.All(points, p => Assert.True(box.Contains(p, Tolerance.Default)));
    }

    [Fact]
    public void MinAreaBox_DegenerateInputs()
    {
        var point = Fitting2d.MinAreaBox([(2, 3), (2, 3)]);
        Assert.Equal(new Vector2d(2, 3), point.Center);
        Assert.Equal(Vector2d.Zero, point.HalfExtents);

        var segment = Fitting2d.MinAreaBox([(0, 0), (4, 0), (2, 0)]);
        Assert.Equal(0, segment.Area, 12);
        Assert.Equal(2, segment.HalfExtents.X, 12);
        Assert.Equal(new Vector2d(2, 0), segment.Center);
    }

    [Fact]
    public void MinCircle_RecoversAKnownCircle()
    {
        // Points ON a known circle at scattered angles (with the diametral pair present
        // so the minimal circle is that circle).
        var center = new Vector2d(3, -1);
        const double r = 2.5;
        var points = new List<Vector2d>();
        foreach (double angle in new[] { 0.1, 0.1 + Math.PI, 0.9, 2.0, 3.3, 4.6, 5.5 })
            points.Add(center + new Vector2d(r * Math.Cos(angle), r * Math.Sin(angle)));
        points.Add(center); // interior
        points.Add(center + new Vector2d(1, 0.5));

        var circle = Fitting2d.MinCircle(points);
        Assert.True(circle.Center.AreEqual(center, Tolerance.Default), $"center {circle.Center}");
        Assert.Equal(r, circle.Radius, 9);
    }

    [Fact]
    public void MinCircle_EquilateralTriangle_IsTheCircumcircle()
    {
        var points = new List<Vector2d>();
        for (int i = 0; i < 3; i++)
        {
            double angle = i * 2 * Math.PI / 3;
            points.Add((Math.Cos(angle), Math.Sin(angle)));
        }
        var circle = Fitting2d.MinCircle(points);
        Assert.True(circle.Center.AreEqual(Vector2d.Zero, Tolerance.Default));
        Assert.Equal(1.0, circle.Radius, 9);
    }

    [Fact]
    public void MinCircle_TwoPoints_IsTheirDiameter()
    {
        var circle = Fitting2d.MinCircle([(0, 0), (4, 0)]);
        Assert.True(circle.Center.AreEqual((2, 0), Tolerance.Default));
        Assert.Equal(2.0, circle.Radius, 12);
    }

    [Fact]
    public void MinCircle_MatchesBruteForceOnRandomSets()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 30; trial++)
        {
            var points = new List<Vector2d>();
            int n = random.Next(3, 9);
            for (int i = 0; i < n; i++)
                points.Add((random.NextDouble() * 10, random.NextDouble() * 10));

            var circle = Fitting2d.MinCircle(points);

            // Contains everything.
            Assert.All(points, p => Assert.True(circle.Contains(p, Tolerance.Default)));

            // No pair- or triple-supported circle containing all points is smaller.
            double best = BruteForceRadius(points);
            Assert.True(circle.Radius <= best + 1e-9,
                $"welzl {circle.Radius} vs brute force {best}");
        }
    }

    private static double BruteForceRadius(List<Vector2d> points)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                Consider((points[i] + points[j]) * 0.5, points, ref best);
                for (int k = j + 1; k < points.Count; k++)
                {
                    if (TryCircumcenter(points[i], points[j], points[k], out var c))
                        Consider(c, points, ref best);
                }
            }
        }
        return best;

        static void Consider(Vector2d center, List<Vector2d> points, ref double best)
        {
            double r = points.Max(p => p.DistanceTo(center));
            if (r < best)
                best = r;
        }

        static bool TryCircumcenter(Vector2d a, Vector2d b, Vector2d c, out Vector2d center)
        {
            var ab = b - a;
            var ac = c - a;
            double det = 2 * ab.Cross(ac);
            if (Math.Abs(det) < 1e-12)
            {
                center = default;
                return false;
            }
            center = a + new Vector2d(
                (ac.Y * ab.LengthSquared - ab.Y * ac.LengthSquared) / det,
                (ab.X * ac.LengthSquared - ac.X * ab.LengthSquared) / det);
            return true;
        }
    }
}
