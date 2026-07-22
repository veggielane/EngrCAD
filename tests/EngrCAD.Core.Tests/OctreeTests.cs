using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using Xunit;

namespace EngrCAD.Core.Tests;

public class OctreeTests
{
    private static readonly Aabb World = new(new Vector3d(0, 0, 0), new Vector3d(100, 100, 100));

    private static Aabb[] RandomBoxes(Random rng, int count)
    {
        var boxes = new Aabb[count];
        for (int i = 0; i < count; i++)
        {
            var min = new Vector3d(
                rng.NextDouble() * 95,
                rng.NextDouble() * 95,
                rng.NextDouble() * 95);
            var size = new Vector3d(
                rng.NextDouble() * 5,
                rng.NextDouble() * 5,
                rng.NextDouble() * 5);
            boxes[i] = new Aabb(min, min + size);
        }
        return boxes;
    }

    [Fact]
    public void Query_MatchesBruteForce()
    {
        var rng = new Random(19);
        var boxes = RandomBoxes(rng, 500);
        var octree = new Octree(World);
        for (int i = 0; i < boxes.Length; i++)
            octree.Insert(i, boxes[i]);
        Assert.Equal(500, octree.Count);

        for (int trial = 0; trial < 100; trial++)
        {
            var queryMin = new Vector3d(
                rng.NextDouble() * 90,
                rng.NextDouble() * 90,
                rng.NextDouble() * 90);
            var query = new Aabb(queryMin, queryMin + new Vector3d(
                rng.NextDouble() * 20,
                rng.NextDouble() * 20,
                rng.NextDouble() * 20));

            var expected = new List<int>();
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i].Intersects(query))
                    expected.Add(i);
            }

            var actual = new List<int>();
            octree.Query(query, actual);
            actual.Sort();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ItemsOutsideRootBounds_AreStillFound()
    {
        var octree = new Octree(World);
        var outside = new Aabb(new Vector3d(150, 150, 150), new Vector3d(160, 160, 160));
        octree.Insert(1, outside);

        var results = new List<int>();
        octree.Query(new Aabb(new Vector3d(140, 140, 140), new Vector3d(200, 200, 200)), results);
        Assert.Equal([1], results);
    }

    [Fact]
    public void Remove_DeletesExactlyThatItem()
    {
        var rng = new Random(23);
        var boxes = RandomBoxes(rng, 100);
        var octree = new Octree(World);
        for (int i = 0; i < boxes.Length; i++)
            octree.Insert(i, boxes[i]);

        Assert.True(octree.Remove(37, boxes[37]));
        Assert.False(octree.Remove(37, boxes[37]));
        Assert.Equal(99, octree.Count);

        var results = new List<int>();
        octree.Query(World, results);
        Assert.Equal(99, results.Count);
        Assert.DoesNotContain(37, results);
    }

    [Fact]
    public void ManyClusteredItems_RespectsMaxDepth()
    {
        // All items in one tiny corner force maximum subdivision; must terminate.
        var octree = new Octree(World, maxDepth: 4, maxItemsPerNode: 2);
        var tiny = new Aabb(new Vector3d(0.1, 0.1, 0.1), new Vector3d(0.2, 0.2, 0.2));
        for (int i = 0; i < 100; i++)
            octree.Insert(i, tiny);

        var results = new List<int>();
        octree.Query(tiny, results);
        Assert.Equal(100, results.Count);
    }
}
