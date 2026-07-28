using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class DijkstraGraphDistanceTests
{
    [Fact]
    public void PlaneGrid_AxisAndDiagonalDistancesAreExact()
    {
        const int size = 8;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int corner = LaplacianSmootherTests.Index(size, 0, 0);
        var dijkstra = DijkstraGraphDistance.Compute(grid, corner);

        // Along a grid row the shortest edge path is the straight run of unit edges.
        for (int i = 0; i <= size; i++)
            Assert.Equal(i, dijkstra.Distance(LaplacianSmootherTests.Index(size, i, 0)), 12);

        // The grid's diagonal edges run (i, j) -> (i+1, j+1), so the diagonal is direct.
        for (int k = 0; k <= size; k++)
            Assert.Equal(k * Math.Sqrt(2), dijkstra.Distance(LaplacianSmootherTests.Index(size, k, k)), 12);

        Assert.Equal(0.0, dijkstra.Distance(corner));
        Assert.Equal(corner, dijkstra.NearestSeed(corner));
    }

    [Fact]
    public void Cylinder_VerticalPathIsExactHeight()
    {
        var cylinder = MeshPrimitives.Cylinder(1, 10, 16);
        // Pick a bottom-rim vertex and find the top-rim vertex directly above it.
        var bottom = cylinder.Vertices.First(v => Math.Abs(v.Position.Z) < 1e-12 && Math.Abs(v.Position.Length - 1) < 1e-9);
        var top = cylinder.Vertices.First(v =>
            Math.Abs(v.Position.Z - 10) < 1e-12 &&
            (v.Position - new Vector3d(bottom.Position.X, bottom.Position.Y, 10)).Length < 1e-9);

        var dijkstra = DijkstraGraphDistance.Compute(cylinder, bottom.Index);
        Assert.Equal(10.0, dijkstra.Distance(top.Index), 9); // the vertical side edge chain
    }

    [Fact]
    public void MultipleSeeds_PartitionByNearestSeed()
    {
        const int size = 10;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int left = LaplacianSmootherTests.Index(size, 0, 5);
        int right = LaplacianSmootherTests.Index(size, 10, 5);
        var dijkstra = DijkstraGraphDistance.Compute(grid, [(left, 0.0), (right, 0.0)]);

        Assert.Equal(left, dijkstra.NearestSeed(LaplacianSmootherTests.Index(size, 2, 5)));
        Assert.Equal(right, dijkstra.NearestSeed(LaplacianSmootherTests.Index(size, 8, 5)));
        Assert.Equal(2.0, dijkstra.Distance(LaplacianSmootherTests.Index(size, 2, 5)), 12);
        Assert.Equal(2.0, dijkstra.Distance(LaplacianSmootherTests.Index(size, 8, 5)), 12);
    }

    [Fact]
    public void SeedInitialDistance_OffsetsTheField()
    {
        const int size = 6;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int seed = LaplacianSmootherTests.Index(size, 0, 0);
        var dijkstra = DijkstraGraphDistance.Compute(grid, [(seed, 2.5)]);
        Assert.Equal(2.5, dijkstra.Distance(seed), 12);
        Assert.Equal(3.5, dijkstra.Distance(LaplacianSmootherTests.Index(size, 1, 0)), 12);
    }

    [Fact]
    public void MaxDistance_LeavesFarVerticesUnreached()
    {
        const int size = 10;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int corner = LaplacianSmootherTests.Index(size, 0, 0);
        var dijkstra = DijkstraGraphDistance.Compute(grid, corner, maxDistance: 3.0);

        Assert.True(dijkstra.IsReached(LaplacianSmootherTests.Index(size, 3, 0)));
        Assert.False(dijkstra.IsReached(LaplacianSmootherTests.Index(size, 10, 10)));
        Assert.Equal(double.PositiveInfinity, dijkstra.Distance(LaplacianSmootherTests.Index(size, 10, 10)));
        Assert.Equal(-1, dijkstra.NearestSeed(LaplacianSmootherTests.Index(size, 10, 10)));
        Assert.True(dijkstra.MaxReachedDistance <= 3.0);
    }

    [Fact]
    public void SettledOrder_IsAscendingByDistance()
    {
        var sphere = MeshPrimitives.UvSphere(3, 24, 12);
        var dijkstra = DijkstraGraphDistance.Compute(sphere, 0);
        double previous = 0;
        foreach (int v in dijkstra.SettledOrder)
        {
            Assert.True(dijkstra.Distance(v) >= previous - 1e-12);
            previous = dijkstra.Distance(v);
        }
        Assert.Equal(sphere.VertexCount, dijkstra.SettledOrder.Count);
    }

    [Fact]
    public void PathToSeed_WalksMonotonicallyDown()
    {
        const int size = 8;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int seed = LaplacianSmootherTests.Index(size, 0, 0);
        int far = LaplacianSmootherTests.Index(size, 7, 5);
        var dijkstra = DijkstraGraphDistance.Compute(grid, seed);

        var path = dijkstra.PathToSeed(far);
        Assert.Equal(far, path[0]);
        Assert.Equal(seed, path[^1]);
        for (int i = 1; i < path.Count; i++)
            Assert.True(dijkstra.Distance(path[i]) < dijkstra.Distance(path[i - 1]));
    }

    [Fact]
    public void Sphere_GraphDistanceBoundsTheGeodesic()
    {
        const double radius = 5;
        var sphere = MeshPrimitives.UvSphere(radius, 48, 24);
        // Equator vertex to its antipode: true geodesic is π·r.
        var start = sphere.Vertices.First(v => Math.Abs(v.Position.Z) < 1e-9 && v.Position.X > radius - 1e-9);
        var end = sphere.Vertices.First(v => Math.Abs(v.Position.Z) < 1e-9 && v.Position.X < -radius + 1e-9);
        var dijkstra = DijkstraGraphDistance.Compute(sphere, start.Index);

        double geodesic = Math.PI * radius;
        double graph = dijkstra.Distance(end.Index);
        Assert.True(graph >= geodesic * 0.99, $"Graph distance {graph:F3} cannot beat the geodesic {geodesic:F3}.");
        Assert.True(graph <= geodesic * 1.15, $"Graph distance {graph:F3} should approximate the geodesic {geodesic:F3}.");
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var sphere = MeshPrimitives.UvSphere(2, 16, 8);
        var a = DijkstraGraphDistance.Compute(sphere, 5);
        var b = DijkstraGraphDistance.Compute(sphere, 5);
        Assert.Equal(a.SettledOrder, b.SettledOrder);
        for (int v = 0; v < sphere.VertexCount; v++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Distance(v)), BitConverter.DoubleToInt64Bits(b.Distance(v)));
    }

    [Fact]
    public void Compute_RejectsBadInput()
    {
        var grid = LaplacianSmootherTests.PlaneGrid(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => DijkstraGraphDistance.Compute(grid, 999));
        Assert.Throws<ArgumentException>(() => DijkstraGraphDistance.Compute(grid, Array.Empty<(int, double)>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => DijkstraGraphDistance.Compute(grid, 0, maxDistance: -1));
        var full = DijkstraGraphDistance.Compute(grid, 0, maxDistance: 0.5);
        Assert.Throws<ArgumentException>(() => full.PathToSeed(LaplacianSmootherTests.Index(3, 3, 3)));
    }
}
