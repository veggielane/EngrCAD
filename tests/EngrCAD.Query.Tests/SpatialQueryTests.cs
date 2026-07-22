using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Query.Tests;

public class SpatialQueryTests
{
    private sealed record Thing(int Id, Aabb Bounds, string Category);

    private static List<Thing> RandomThings(int count, int seed = 17)
    {
        var rng = new Random(seed);
        var things = new List<Thing>(count);
        for (int i = 0; i < count; i++)
        {
            var min = new Vector3d(rng.NextDouble() * 100, rng.NextDouble() * 100, rng.NextDouble() * 100);
            var size = new Vector3d(rng.NextDouble() * 6, rng.NextDouble() * 6, rng.NextDouble() * 6);
            things.Add(new Thing(i, new Aabb(min, min + size), i % 2 == 0 ? "even" : "odd"));
        }
        return things;
    }

    private static List<int> Ids(IEnumerable<Thing> things) => [.. things.Select(t => t.Id).OrderBy(x => x)];

    [Fact]
    public void Within_MatchesBruteForce_AndUsesIndex()
    {
        var things = RandomThings(400);
        var collection = things.ToSpatialCollection(t => t.Bounds);
        var rng = new Random(99);

        for (int trial = 0; trial < 25; trial++)
        {
            var min = new Vector3d(rng.NextDouble() * 90, rng.NextDouble() * 90, rng.NextDouble() * 90);
            var region = new Aabb(min, min + new Vector3d(15, 15, 15));

            var actual = Ids(collection.AsQueryable().Where(t => t.Bounds.Within(region)));
            var expected = Ids(things.Where(t => t.Bounds.Intersects(region)));

            Assert.Equal(expected, actual);
            Assert.True(collection.LastQueryUsedIndex, "spatial clause should be answered from the BVH");
        }
    }

    [Fact]
    public void CompoundPredicate_SpatialClausePlusResidual()
    {
        var things = RandomThings(400);
        var collection = things.ToSpatialCollection(t => t.Bounds);
        var region = new Aabb((20, 20, 20), (60, 60, 60));

        var actual = Ids(collection.AsQueryable()
            .Where(t => t.Bounds.Within(region) && t.Category == "even" && t.Id > 10));
        var expected = Ids(things
            .Where(t => t.Bounds.Intersects(region) && t.Category == "even" && t.Id > 10));

        Assert.Equal(expected, actual);
        Assert.True(collection.LastQueryUsedIndex);
    }

    [Fact]
    public void WithinDistance_MatchesBruteForce()
    {
        var things = RandomThings(400);
        var collection = things.ToSpatialCollection(t => t.Bounds);
        var point = new Vector3d(50, 50, 50);

        var actual = Ids(collection.AsQueryable().Where(t => t.Bounds.WithinDistance(point, 12)));
        var expected = Ids(things.Where(t => t.Bounds.DistanceTo(point) <= 12));

        Assert.Equal(expected, actual);
        Assert.True(collection.LastQueryUsedIndex);
    }

    [Fact]
    public void HitBy_MatchesBruteForce()
    {
        var things = RandomThings(400);
        var collection = things.ToSpatialCollection(t => t.Bounds);
        var ray = new Ray3d((-10, 45, 55), (1, 0.1, -0.05));

        var actual = Ids(collection.AsQueryable().Where(t => t.Bounds.HitBy(ray)));
        var expected = Ids(things.Where(t => ray.Intersects(t.Bounds)));

        Assert.Equal(expected, actual);
        Assert.True(collection.LastQueryUsedIndex);
    }

    [Fact]
    public void NonSpatialQuery_FallsBackWithoutIndex()
    {
        var things = RandomThings(100);
        var collection = things.ToSpatialCollection(t => t.Bounds);

        var actual = Ids(collection.AsQueryable().Where(t => t.Id < 30));
        Assert.Equal(Ids(things.Where(t => t.Id < 30)), actual);
        Assert.False(collection.LastQueryUsedIndex);
    }

    [Fact]
    public void Composition_OrderBySelectCountFirst()
    {
        var things = RandomThings(400);
        var collection = things.ToSpatialCollection(t => t.Bounds);
        var region = new Aabb((10, 10, 10), (70, 70, 70));
        var query = collection.AsQueryable().Where(t => t.Bounds.Within(region));

        var expected = things.Where(t => t.Bounds.Intersects(region)).ToList();

        Assert.Equal(expected.Count, query.Count());
        Assert.Equal(expected.Count > 0, query.Any());
        Assert.Equal(
            expected.OrderByDescending(t => t.Id).Select(t => t.Id).ToList(),
            query.OrderByDescending(t => t.Id).Select(t => t.Id).ToList());
        Assert.Equal(
            expected.OrderBy(t => t.Id).First().Id,
            query.OrderBy(t => t.Id).First().Id);
    }

    [Fact]
    public void ChainedWheres_InnerSpatialClauseIntercepted()
    {
        var things = RandomThings(400);
        var collection = things.ToSpatialCollection(t => t.Bounds);
        var region = new Aabb((30, 30, 30), (80, 80, 80));

        var actual = Ids(collection.AsQueryable()
            .Where(t => t.Bounds.Within(region))
            .Where(t => t.Category == "odd"));
        var expected = Ids(things.Where(t => t.Bounds.Intersects(region) && t.Category == "odd"));

        Assert.Equal(expected, actual);
        Assert.True(collection.LastQueryUsedIndex);
    }

    [Fact]
    public void PlainEnumeration_ReturnsEverything()
    {
        var things = RandomThings(50);
        var collection = things.ToSpatialCollection(t => t.Bounds);
        Assert.Equal(Ids(things), Ids(collection.AsQueryable()));
    }

    [Fact]
    public void MeshFaces_QueryableByBounds()
    {
        // The LINQ-native vision applied to kernel geometry: index a mesh's faces and
        // find the ones in a region straight from a LINQ query.
        var mesh = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);
        var faces = mesh.Faces.ToSpatialCollection(f => f.Bounds);

        // Region covering the top cap of the sphere.
        var region = new Aabb((-2, -2, 0.9), (2, 2, 2));
        var actual = faces.AsQueryable()
            .Where(f => f.Bounds.Within(region))
            .Select(f => f.Index)
            .OrderBy(i => i)
            .ToList();

        var expected = mesh.Faces
            .Where(f => f.Bounds.Intersects(region))
            .Select(f => f.Index)
            .OrderBy(i => i)
            .ToList();

        Assert.NotEmpty(actual);
        Assert.Equal(expected, actual);
        Assert.True(faces.LastQueryUsedIndex);
    }
}
