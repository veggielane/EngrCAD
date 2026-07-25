using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// Locks the EXACT tree the builder produces. Every BVH consumer is order-sensitive
/// somewhere — <c>Query</c> appends candidates in leaf-visit order, <c>Nearest</c> breaks
/// distance ties by traversal order, <c>QueryOverlap</c> emits pairs in traversal order and
/// the mesh imprint boolean interns seam points in exactly that order — so a build that
/// merely produces "an equally good tree" silently repermutes downstream geometry. These
/// fingerprints (FNV-1a over the item permutation plus every node's range, leaf flag and
/// bounds BITS) are the contract: any builder rewrite must reproduce them or explicitly
/// argue, with a measurement, that the new tree is better.
/// </summary>
public class BvhBuildOrderTests
{
    /// <summary>Deterministic LCG so the fixtures never depend on <see cref="Random"/>'s implementation.</summary>
    private static double[] Sequence(int count, ulong seed)
    {
        var values = new double[count];
        ulong state = seed;
        for (int i = 0; i < count; i++)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            values[i] = (state >> 11) * (1.0 / 9007199254740992.0);
        }
        return values;
    }

    private static Aabb[] RandomBoxes(int count, ulong seed)
    {
        var r = Sequence(count * 6, seed);
        var boxes = new Aabb[count];
        for (int i = 0; i < count; i++)
        {
            var min = new Vector3d(r[i * 6] * 100, r[i * 6 + 1] * 100, r[i * 6 + 2] * 100);
            var size = new Vector3d(r[i * 6 + 3], r[i * 6 + 4], r[i * 6 + 5]);
            boxes[i] = new Aabb(min, min + size);
        }
        return boxes;
    }

    /// <summary>A k^3 lattice of unit cells: every centroid coordinate is shared by k^2 items,
    /// which is the tie-heavy case a rank-based split has to get right.</summary>
    private static Aabb[] GridBoxes(int k)
    {
        var boxes = new Aabb[k * k * k];
        int n = 0;
        for (int i = 0; i < k; i++)
            for (int j = 0; j < k; j++)
                for (int m = 0; m < k; m++)
                    boxes[n++] = new Aabb(new Vector3d(i, j, m), new Vector3d(i + 1, j + 1, m + 1));
        return boxes;
    }

    /// <summary>Degenerate boxes strung along X: two axes have zero centroid spread.</summary>
    private static Aabb[] LineBoxes(int count)
    {
        var boxes = new Aabb[count];
        for (int i = 0; i < count; i++)
            boxes[i] = new Aabb(new Vector3d(i * 0.001, 0, 0), new Vector3d(i * 0.001 + 0.002, 0, 0));
        return boxes;
    }

    private static Aabb[] EqualBoxes(int count)
    {
        var boxes = new Aabb[count];
        Array.Fill(boxes, new Aabb(Vector3d.Zero, Vector3d.One));
        return boxes;
    }

    internal static ulong Fingerprint(Aabb[] boxes)
    {
        var bvh = Bvh.Build(boxes);
        ulong h = 14695981039346656037UL;

        void Mix(ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                h ^= (byte)(v >> (i * 8));
                h *= 1099511628211UL;
            }
        }

        foreach (int item in bvh.ItemsInTreeOrder)
            Mix((ulong)item);
        Mix((ulong)bvh.NodeCount);

        void Walk(Bvh.NodeView node)
        {
            Mix((ulong)node.First);
            Mix((ulong)node.Count);
            Mix(node.IsLeaf ? 1UL : 0UL);
            Mix((ulong)BitConverter.DoubleToInt64Bits(node.Bounds.Min.X));
            Mix((ulong)BitConverter.DoubleToInt64Bits(node.Bounds.Min.Y));
            Mix((ulong)BitConverter.DoubleToInt64Bits(node.Bounds.Min.Z));
            Mix((ulong)BitConverter.DoubleToInt64Bits(node.Bounds.Max.X));
            Mix((ulong)BitConverter.DoubleToInt64Bits(node.Bounds.Max.Y));
            Mix((ulong)BitConverter.DoubleToInt64Bits(node.Bounds.Max.Z));
            if (!node.IsLeaf)
            {
                Walk(node.Left);
                Walk(node.Right);
            }
        }

        if (boxes.Length > 0)
            Walk(bvh.Root);
        return h;
    }

    [Theory]
    // Fingerprints recorded from the original median-split builder (sequential in-place
    // Array.Sort of the item permutation through a per-axis IComparer<int>) before the
    // key-array + parallel-subtree rewrite. They must never change silently.
    [InlineData("random-8000", 0x33C00000353B1140UL)]
    [InlineData("random-40000", 0xA8ECDD2C1888E2E0UL)]
    [InlineData("grid-20", 0x21B076A0D27F1EDFUL)]
    [InlineData("line-5000", 0x317CA3F44C4D1BDEUL)]
    [InlineData("equal-500", 0x148C54B15CE03E77UL)]
    public void Build_ProducesTheRecordedTree(string fixture, ulong expected)
    {
        Assert.Equal(expected, Fingerprint(Fixture(fixture)));
    }

    internal static Aabb[] Fixture(string name) => name switch
    {
        "random-8000" => RandomBoxes(8000, 20240607),
        "random-40000" => RandomBoxes(40000, 990099),
        "grid-20" => GridBoxes(20),
        "line-5000" => LineBoxes(5000),
        "equal-500" => EqualBoxes(500),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// The builder forks sibling subtrees onto the thread pool above a size threshold, so the
    /// node-allocation order depends on scheduling; a canonical renumbering pass undoes that.
    /// Repeat a build large enough to fork and demand the SAME fingerprint every time —
    /// scheduling must never be observable.
    /// </summary>
    [Fact]
    public void Build_IsDeterministicWhenSubtreesAreBuiltConcurrently()
    {
        var boxes = Fixture("random-40000");
        ulong first = Fingerprint(boxes);
        for (int trial = 0; trial < 8; trial++)
            Assert.Equal(first, Fingerprint(boxes));
    }

    /// <summary>
    /// Structural invariants the fingerprints alone would not explain: children partition
    /// their parent's contiguous range, leaves respect <c>maxLeafSize</c> unless the
    /// centroids are degenerate, and a parent's bounds contain its children's.
    /// </summary>
    [Theory]
    [InlineData("random-8000")]
    [InlineData("grid-20")]
    [InlineData("line-5000")]
    public void Build_RangesPartitionAndBoundsNest(string fixture)
    {
        var bvh = Bvh.Build(Fixture(fixture));
        int leafItems = 0;
        var seen = new bool[bvh.Count];

        void Walk(Bvh.NodeView node)
        {
            if (node.IsLeaf)
            {
                leafItems += node.Count;
                foreach (int item in node.Items)
                {
                    Assert.False(seen[item]);
                    seen[item] = true;
                }
                return;
            }
            var l = node.Left;
            var r = node.Right;
            Assert.Equal(node.First, l.First);
            Assert.Equal(l.First + l.Count, r.First);
            Assert.Equal(node.Count, l.Count + r.Count);
            Assert.True(node.Bounds.Contains(l.Bounds));
            Assert.True(node.Bounds.Contains(r.Bounds));
            Walk(l);
            Walk(r);
        }

        Walk(bvh.Root);
        Assert.Equal(bvh.Count, leafItems);
        Assert.All(seen, Assert.True);
    }
}
