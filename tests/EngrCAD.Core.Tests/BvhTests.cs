using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.TestSupport;
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

    private static Ray3d RandomRayThroughScene(Random rng, double worldSize)
    {
        var origin = new Vector3d(
            rng.NextDouble() * worldSize * 1.4 - worldSize * 0.2,
            rng.NextDouble() * worldSize * 1.4 - worldSize * 0.2,
            rng.NextDouble() * worldSize * 1.4 - worldSize * 0.2);
        // Aim through the populated region so hit lists are non-trivial.
        var target = new Vector3d(
            rng.NextDouble() * worldSize,
            rng.NextDouble() * worldSize,
            rng.NextDouble() * worldSize);
        var direction = target - origin;
        if (direction.LengthSquared < 1e-6)
            direction = Vector3d.UnitX;
        return new Ray3d(origin, direction);
    }

    [Fact]
    public void QueryAll_MatchesBruteForceAndIsOrderedByEntryT()
    {
        var rng = new Random(17);
        var boxes = RandomBoxes(rng, 500, 100, 8);
        var bvh = Bvh.Build(boxes);

        var actual = new List<BvhRayHit>();
        for (int trial = 0; trial < 200; trial++)
        {
            var ray = RandomRayThroughScene(rng, 100);

            var expected = new List<BvhRayHit>();
            for (int i = 0; i < boxes.Length; i++)
            {
                if (ray.Intersects(boxes[i], out double tMin, out _))
                    expected.Add(new BvhRayHit(i, tMin));
            }
            expected.Sort(); // by (TEntry, Item) — QueryAll's documented order

            actual.Clear();
            bvh.QueryAll(ray, actual);

            Assert.Equal(expected.Count, actual.Count);
            for (int k = 0; k < expected.Count; k++)
            {
                Assert.Equal(expected[k].Item, actual[k].Item);
                Assert.Equal(expected[k].TEntry, actual[k].TEntry); // exact: same slab test
                if (k > 0)
                    Assert.True(actual[k - 1].TEntry <= actual[k].TEntry, "results not t-ordered");
            }
        }
    }

    [Fact]
    public void QueryAll_AppendsAndSortsOnlyTheNewRange()
    {
        var boxes = new[]
        {
            new Aabb(new Vector3d(5, -1, -1), new Vector3d(6, 1, 1)),
            new Aabb(new Vector3d(1, -1, -1), new Vector3d(2, 1, 1)),
        };
        var bvh = Bvh.Build(boxes);

        var results = new List<BvhRayHit> { new(99, double.MaxValue) }; // pre-existing entry
        bvh.QueryAll(new Ray3d(Vector3d.Zero, Vector3d.UnitX), results);

        Assert.Equal(3, results.Count);
        Assert.Equal(99, results[0].Item);          // untouched prefix
        Assert.Equal(1, results[1].Item);           // nearer box first
        Assert.Equal(0, results[2].Item);
        Assert.True(results[1].TEntry < results[2].TEntry);
    }

    [Fact]
    public void QueryAll_EmptyTreeReturnsNothing()
    {
        var bvh = Bvh.Build([]);
        var results = new List<BvhRayHit>();
        bvh.QueryAll(new Ray3d(Vector3d.Zero, Vector3d.UnitX), results);
        Assert.Empty(results);
    }

    [Fact]
    public void QueryOverlap_MatchesBruteForce()
    {
        var rng = new Random(23);
        var boxesA = RandomBoxes(rng, 300, 100, 10);
        var boxesB = RandomBoxes(rng, 200, 100, 10);
        var a = Bvh.Build(boxesA);
        var b = Bvh.Build(boxesB);

        var expected = new List<(int, int)>();
        for (int i = 0; i < boxesA.Length; i++)
            for (int j = 0; j < boxesB.Length; j++)
                if (boxesA[i].Intersects(boxesB[j]))
                    expected.Add((i, j));

        var actual = new List<(int Item, int OtherItem)>();
        a.QueryOverlap(b, actual);

        expected.Sort();
        actual.Sort();
        Assert.Equal(expected, actual);
        Assert.NotEmpty(actual); // sanity: dense enough to actually overlap
    }

    [Fact]
    public void QueryOverlap_SelfQueryContainsAllSelfPairs()
    {
        var rng = new Random(41);
        var boxes = RandomBoxes(rng, 100, 60, 5);
        var bvh = Bvh.Build(boxes);

        var pairs = new List<(int Item, int OtherItem)>();
        bvh.QueryOverlap(bvh, pairs);

        for (int i = 0; i < boxes.Length; i++)
            Assert.Contains((i, i), pairs);
        // Symmetric: (i, j) present iff (j, i) present.
        var set = new HashSet<(int, int)>(pairs);
        foreach (var (i, j) in pairs)
            Assert.Contains((j, i), set);
    }

    [Fact]
    public void QueryOverlap_EmptyTreeReturnsNothing()
    {
        var empty = Bvh.Build([]);
        var full = Bvh.Build([new Aabb(Vector3d.Zero, Vector3d.One)]);
        var pairs = new List<(int Item, int OtherItem)>();
        empty.QueryOverlap(full, pairs);
        full.QueryOverlap(empty, pairs);
        Assert.Empty(pairs);
    }

    [Fact]
    public void TreeOrder_NodeRangesAreContiguousAndPartition()
    {
        var rng = new Random(53);
        var boxes = RandomBoxes(rng, 337, 80, 6); // odd count exercises uneven splits
        var bvh = Bvh.Build(boxes);

        // ItemsInTreeOrder is a permutation of [0, Count).
        var seen = new bool[bvh.Count];
        foreach (int item in bvh.ItemsInTreeOrder)
        {
            Assert.False(seen[item], "duplicate item in tree order");
            seen[item] = true;
        }
        Assert.All(seen, Assert.True);

        // Walk the tree: children partition the parent's range, bounds nest, node
        // indices are dense, and every node's items lie inside its bounds.
        var visited = new bool[bvh.NodeCount];
        int leafItemTotal = 0;
        var stack = new Stack<Bvh.NodeView>();
        stack.Push(bvh.Root);
        Assert.Equal(0, bvh.Root.First);
        Assert.Equal(bvh.Count, bvh.Root.Count);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            Assert.InRange(node.Index, 0, bvh.NodeCount - 1);
            Assert.False(visited[node.Index], "node visited twice");
            visited[node.Index] = true;

            foreach (int item in node.Items)
                Assert.True(node.Bounds.Contains(boxes[item]), "item box outside node bounds");

            if (node.IsLeaf)
            {
                Assert.True(node.Count >= 1);
                leafItemTotal += node.Count;
            }
            else
            {
                var left = node.Left;
                var right = node.Right;
                Assert.Equal(node.First, left.First);
                Assert.Equal(left.First + left.Count, right.First);
                Assert.Equal(node.Count, left.Count + right.Count);
                Assert.True(node.Bounds.Contains(left.Bounds));
                Assert.True(node.Bounds.Contains(right.Bounds));
                stack.Push(left);
                stack.Push(right);
            }
        }

        Assert.Equal(bvh.Count, leafItemTotal);
        Assert.All(visited, Assert.True); // node array is dense — no orphans
    }

    [Fact]
    public void Root_ThrowsOnEmptyTree()
    {
        var bvh = Bvh.Build([]);
        Assert.Throws<InvalidOperationException>(() => bvh.Root);
    }

    private readonly struct BoxDistance(Aabb[] boxes, Vector3d point) : IBvhDistance
    {
        public double DistanceTo(int item) => boxes[item].DistanceTo(point);
    }

    [Fact]
    public void Nearest_StructMetric_BitIdenticalToDelegate()
    {
        var rng = new Random(61);
        var boxes = RandomBoxes(rng, 400, 100, 6);
        var bvh = Bvh.Build(boxes);

        for (int trial = 0; trial < 50; trial++)
        {
            var point = new Vector3d(
                rng.NextDouble() * 120 - 10,
                rng.NextDouble() * 120 - 10,
                rng.NextDouble() * 120 - 10);

            Assert.True(bvh.Nearest(point, i => boxes[i].DistanceTo(point), out int itemA, out double distA));
            var metric = new BoxDistance(boxes, point);
            Assert.True(bvh.Nearest(point, ref metric, out int itemB, out double distB));

            Assert.Equal(itemA, itemB);
            Assert.Equal(distA, distB); // exact: identical traversal, identical arithmetic
        }
    }

    [Fact]
    public void Nearest_StructMetric_EmptyReturnsFalse()
    {
        var bvh = Bvh.Build([]);
        var metric = new BoxDistance([], Vector3d.Zero);
        Assert.False(bvh.Nearest(Vector3d.Zero, ref metric, out _, out _));
    }

    [Fact]
    public void Queries_SteadyState_DoNotAllocate()
    {
        var rng = new Random(71);
        var boxes = RandomBoxes(rng, 500, 100, 8);
        var bvh = Bvh.Build(boxes);
        var ray = RandomRayThroughScene(rng, 100);
        var point = new Vector3d(50, 50, 50);
        var metric = new BoxDistance(boxes, point);
        var hits = new List<BvhRayHit>(512);
        var pairs = new List<(int, int)>(1 << 16);

        void RunBatch(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                hits.Clear();
                bvh.QueryAll(ray, hits);
                bvh.Nearest(point, ref metric, out _, out _);
            }
            pairs.Clear();
            bvh.QueryOverlap(bvh, pairs);
        }

        RunBatch(2000); // size the pair list before AllocationProbe's own warm-up batch
        Assert.True(pairs.Capacity >= pairs.Count, "warmup should have sized the pair list");

        // The MINIMUM over several batches, not one measured batch — see AllocationProbe:
        // tiering promotion and a neighbouring test's GC are one-time artifacts that a
        // single window catches at random, and neither scales with the iteration count.
        long delta = AllocationProbe.SteadyStateBytes(() => RunBatch(2000));

        // Steady state must be allocation-free; allow a stray one-time artifact but
        // nothing per-iteration (a single closure would already cost ~88 B × 2000).
        Assert.True(delta < 1024, $"BVH queries allocated {delta} bytes over 2000 iterations");
    }

    [Fact]
    public void InflatedRayQuery_IsConservative_AndZeroIsTheIncumbentQuery()
    {
        // Ten unit boxes along x at y = 0; a ray running along x at y = 1.4 misses every
        // box (they span y in [-0.5, 0.5]) but passes within 0.9 of all of them.
        var boxes = new Aabb[10];
        for (int i = 0; i < boxes.Length; i++)
            boxes[i] = new Aabb((i * 2 - 0.5, -0.5, -0.5), (i * 2 + 0.5, 0.5, 0.5));
        var bvh = Bvh.Build(boxes);
        var ray = new Ray3d((-5, 1.4, 0), (1, 0, 0));

        var thin = new List<int>();
        bvh.Query(ray, thin);
        Assert.Empty(thin);

        // Inflation can only ADD candidates: at 0 the results are the incumbent query's
        // exactly, and past the miss distance every box appears.
        var zero = new List<int>();
        bvh.Query(ray, 0.0, zero);
        Assert.Equal(thin, zero);

        var fat = new List<int>();
        bvh.Query(ray, 1.0, fat);
        Assert.Equal(boxes.Length, fat.Count);

        // A hit ray keeps its results under any inflation (a superset, never a trade).
        var hitRay = new Ray3d((-5, 0, 0), (1, 0, 0));
        var hit = new List<int>();
        bvh.Query(hitRay, hit);
        var hitFat = new List<int>();
        bvh.Query(hitRay, 0.25, hitFat);
        Assert.True(hit.All(hitFat.Contains));
    }
}
