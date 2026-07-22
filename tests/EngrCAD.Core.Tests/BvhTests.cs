using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using Xunit;

namespace EngrCAD.Core.Tests;

public class BvhTests
{
    private static Aabb[] RandomBoxes(Random rng, int count, double worldSize, double maxBoxSize)
    {
        var boxes = new Aabb[count];
        for (int i = 0; i < count; i++)
        {
            var min = new Vector3d(
                rng.NextDouble() * worldSize,
                rng.NextDouble() * worldSize,
                rng.NextDouble() * worldSize);
            var size = new Vector3d(
                rng.NextDouble() * maxBoxSize,
                rng.NextDouble() * maxBoxSize,
                rng.NextDouble() * maxBoxSize);
            boxes[i] = new Aabb(min, min + size);
        }
        return boxes;
    }

    [Fact]
    public void Empty_QueriesReturnNothing()
    {
        var bvh = Bvh.Build([]);
        var results = new List<int>();
        bvh.Query(new Aabb(Vector3d.Zero, Vector3d.One), results);
        Assert.Empty(results);
        Assert.True(bvh.Bounds.IsEmpty);
        Assert.Equal(0, bvh.Count);
    }

    [Fact]
    public void BoxQuery_MatchesBruteForce()
    {
        var rng = new Random(7);
        var boxes = RandomBoxes(rng, 500, 100, 8);
        var bvh = Bvh.Build(boxes);

        for (int trial = 0; trial < 100; trial++)
        {
            var query = RandomBoxes(rng, 1, 100, 25)[0];

            var expected = new List<int>();
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i].Intersects(query))
                    expected.Add(i);
            }

            var actual = new List<int>();
            bvh.Query(query, actual);
            actual.Sort();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void RayQuery_MatchesBruteForce()
    {
        var rng = new Random(11);
        var boxes = RandomBoxes(rng, 500, 100, 8);
        var bvh = Bvh.Build(boxes);

        for (int trial = 0; trial < 100; trial++)
        {
            var origin = new Vector3d(
                rng.NextDouble() * 140 - 20,
                rng.NextDouble() * 140 - 20,
                rng.NextDouble() * 140 - 20);
            var direction = new Vector3d(
                rng.NextDouble() * 2 - 1,
                rng.NextDouble() * 2 - 1,
                rng.NextDouble() * 2 - 1);
            if (direction.LengthSquared < 1e-6)
                direction = Vector3d.UnitX;
            var ray = new Ray3d(origin, direction);

            var expected = new List<int>();
            for (int i = 0; i < boxes.Length; i++)
            {
                if (ray.Intersects(boxes[i]))
                    expected.Add(i);
            }

            var actual = new List<int>();
            bvh.Query(ray, actual);
            actual.Sort();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Bounds_CoversAllInputBoxes()
    {
        var rng = new Random(3);
        var boxes = RandomBoxes(rng, 200, 50, 5);
        var bvh = Bvh.Build(boxes);
        foreach (var box in boxes)
            Assert.True(bvh.Bounds.Contains(box));
    }

    [Fact]
    public void IdenticalBoxes_AllReturned()
    {
        // Degenerate centroid spread must still terminate and index correctly.
        var box = new Aabb(new Vector3d(1, 1, 1), new Vector3d(2, 2, 2));
        var boxes = new Aabb[50];
        Array.Fill(boxes, box);
        var bvh = Bvh.Build(boxes);

        var results = new List<int>();
        bvh.Query(box, results);
        Assert.Equal(50, results.Count);
    }

    [Fact]
    public void Nearest_MatchesBruteForce()
    {
        var rng = new Random(29);
        var boxes = RandomBoxes(rng, 400, 100, 6);
        var bvh = Bvh.Build(boxes);

        for (int trial = 0; trial < 50; trial++)
        {
            var point = new Vector3d(
                rng.NextDouble() * 120 - 10,
                rng.NextDouble() * 120 - 10,
                rng.NextDouble() * 120 - 10);

            Assert.True(bvh.Nearest(point, i => boxes[i].DistanceTo(point), out _, out double distance));

            double expected = double.PositiveInfinity;
            foreach (var box in boxes)
                expected = Math.Min(expected, box.DistanceTo(point));
            Assert.Equal(expected, distance, 12);
        }
    }

    [Fact]
    public void Nearest_EmptyReturnsFalse()
    {
        var bvh = Bvh.Build([]);
        Assert.False(bvh.Nearest(Vector3d.Zero, _ => 0, out _, out _));
    }

    [Fact]
    public void SingleItem_Works()
    {
        var box = new Aabb(Vector3d.Zero, Vector3d.One);
        var bvh = Bvh.Build([box]);
        var results = new List<int>();
        bvh.Query(new Aabb(new Vector3d(0.5, 0.5, 0.5), new Vector3d(2, 2, 2)), results);
        Assert.Equal([0], results);
    }
}
