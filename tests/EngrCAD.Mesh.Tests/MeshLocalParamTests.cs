using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshLocalParamTests
{
    /// <summary>Open tube (no caps): a developable rolled grid, rows in z, columns around the azimuth.</summary>
    private static HalfEdgeMesh Tube(double radius, double height, int segments, int rows)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= rows; j++)
        {
            double z = height * j / rows;
            for (int i = 0; i < segments; i++)
            {
                double angle = 2 * Math.PI * i / segments;
                positions.Add(new Vector3d(radius * Math.Cos(angle), radius * Math.Sin(angle), z));
            }
        }
        var faces = new List<int[]>();
        int Id(int i, int j) => j * segments + ((i % segments) + segments) % segments;
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < segments; i++)
            {
                faces.Add([Id(i, j), Id(i + 1, j), Id(i + 1, j + 1)]);
                faces.Add([Id(i, j), Id(i + 1, j + 1), Id(i, j + 1)]);
            }
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    [Fact]
    public void PlaneGrid_UvIsThePlanarOffsetExactly()
    {
        const int size = 10;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int seed = LaplacianSmootherTests.Index(size, 5, 5);
        var map = MeshLocalParam.Compute(grid, seed, maxRadius: 4.0, referenceDirection: Vector3d.UnitX);

        Assert.Equal(Vector2d.Zero, map.Uv(seed));
        foreach (int v in map.MappedVertices)
        {
            var p = grid.GetPosition(v);
            var uv = map.Uv(v);
            // Transport is the identity on a plane and the predictions telescope, so
            // the map reproduces the planar offsets to accumulated round-off.
            Assert.Equal(p.X - 5, uv.X, 9);
            Assert.Equal(p.Y - 5, uv.Y, 9);
        }
        Assert.True(map.MappedVertices.Count > 20);
    }

    [Fact]
    public void Tube_DevelopableSurface_UnrollsWithSmallDistortion()
    {
        const double radius = 2, height = 4;
        const int segments = 48, rows = 20;
        var tube = Tube(radius, height, segments, rows);

        // Seed mid-height at angle 0; +u along the azimuth tangent (+y there), +v up.
        int seed = (rows / 2) * segments; // i = 0, j = rows/2
        var seedPosition = tube.GetPosition(seed);
        Assert.Equal(radius, seedPosition.X, 12);
        var map = MeshLocalParam.Compute(tube, seed, maxRadius: 1.6, referenceDirection: Vector3d.UnitY);

        int checkedCount = 0;
        foreach (int v in map.MappedVertices)
        {
            var p = tube.GetPosition(v);
            double angle = Math.Atan2(p.Y, p.X); // seed sits at angle 0
            var exact = new Vector2d(radius * angle, p.Z - seedPosition.Z); // the unrolled cylinder
            var uv = map.Uv(v);
            double error = (uv - exact).Length;
            double distance = Math.Max(exact.Length, 0.1);
            Assert.True(error <= 0.02 * distance + 1e-9,
                $"v{v}: uv ({uv.X:F4}, {uv.Y:F4}) vs developed ({exact.X:F4}, {exact.Y:F4}), error {error:E2}");
            checkedCount++;
        }
        Assert.True(checkedCount > 100, $"Only {checkedCount} vertices mapped.");
    }

    [Fact]
    public void SphereCap_RadialCoordinateTracksTheGeodesic()
    {
        const double radius = 5;
        var sphere = MeshPrimitives.UvSphere(radius, 48, 24);
        int pole = sphere.Vertices.OrderByDescending(v => v.Position.Z).First().Index;
        double capRadius = radius * (35 * Math.PI / 180); // 35 degrees of arc
        var map = MeshLocalParam.Compute(sphere, pole, capRadius);

        int checkedCount = 0;
        foreach (int v in map.MappedVertices)
        {
            if (v == pole)
                continue;
            var p = sphere.GetPosition(v);
            double geodesic = radius * Math.Acos(Math.Clamp(p.Z / radius, -1, 1));
            if (geodesic < radius * 0.05)
                continue; // skip the immediate pole ring, where relative error is ill-posed
            double r = map.Uv(v).Length;
            Assert.True(Math.Abs(r - geodesic) <= 0.05 * geodesic,
                $"v{v}: |uv| {r:F4} vs geodesic {geodesic:F4}");
            checkedCount++;
        }
        Assert.True(checkedCount > 50, $"Only {checkedCount} vertices checked.");
    }

    [Fact]
    public void SphereCap_AngularCoordinateFollowsTheAzimuth()
    {
        const double radius = 5;
        var sphere = MeshPrimitives.UvSphere(radius, 48, 24);
        int pole = sphere.Vertices.OrderByDescending(v => v.Position.Z).First().Index;
        var map = MeshLocalParam.Compute(sphere, pole, radius * 0.5, referenceDirection: Vector3d.UnitX);

        foreach (int v in map.MappedVertices)
        {
            var p = sphere.GetPosition(v);
            if (v == pole || new Vector2d(p.X, p.Y).Length < 0.5)
                continue;
            double azimuth = Math.Atan2(p.Y, p.X);
            var uv = map.Uv(v);
            double uvAngle = Math.Atan2(uv.Y, uv.X);
            double difference = Math.Abs(Math.IEEERemainder(uvAngle - azimuth, 2 * Math.PI));
            Assert.True(difference < 0.1, $"v{v}: uv angle {uvAngle:F3} vs azimuth {azimuth:F3}");
        }
    }

    [Fact]
    public void MaxRadius_LimitsTheMap()
    {
        const int size = 10;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        int seed = LaplacianSmootherTests.Index(size, 5, 5);
        var map = MeshLocalParam.Compute(grid, seed, maxRadius: 2.0);

        int corner = LaplacianSmootherTests.Index(size, 0, 0);
        Assert.False(map.HasUv(corner));
        Assert.Throws<ArgumentException>(() => map.Uv(corner));
        Assert.True(map.HasUv(LaplacianSmootherTests.Index(size, 6, 5)));
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var sphere = MeshPrimitives.UvSphere(3, 32, 16);
        var a = MeshLocalParam.Compute(sphere, 17, 2.0, Vector3d.UnitX);
        var b = MeshLocalParam.Compute(sphere, 17, 2.0, Vector3d.UnitX);
        Assert.Equal(a.MappedVertices, b.MappedVertices);
        foreach (int v in a.MappedVertices)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Uv(v).X), BitConverter.DoubleToInt64Bits(b.Uv(v).X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Uv(v).Y), BitConverter.DoubleToInt64Bits(b.Uv(v).Y));
        }
    }

    [Fact]
    public void Compute_RejectsBadInput()
    {
        var grid = LaplacianSmootherTests.PlaneGrid(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshLocalParam.Compute(grid, 999, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshLocalParam.Compute(grid, 0, 0));
        // Reference parallel to the seed normal (+z on a flat grid) projects to nothing.
        Assert.Throws<ArgumentException>(() => MeshLocalParam.Compute(grid, 12, 2, Vector3d.UnitZ));
    }
}
