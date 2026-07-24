using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class ConvexHullTests
{
    private static readonly Vector3d[] CubeCorners =
    [
        (-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
        (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1),
    ];

    [Fact]
    public void CubeCorners_HullIsTheCube()
    {
        var hull = ConvexHull.Compute(CubeCorners);
        hull.Validate();
        Assert.True(hull.IsClosed);
        Assert.Equal(2, hull.EulerCharacteristic);
        Assert.Equal(8, hull.VertexCount);
        Assert.Equal(12, hull.FaceCount); // consistent triangulation, 2 per cube face
        Assert.Equal(8.0, hull.Volume(), 12);
    }

    [Fact]
    public void InteriorAndCoplanarPoints_AreAbsorbed()
    {
        // Cube corners + centroid + all six face centers + an edge midpoint: only the
        // corners are hull vertices; on-face points must not spawn extra facets.
        var points = new List<Vector3d>(CubeCorners)
        {
            (0, 0, 0),
            (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
            (1, 1, 0),
        };
        var hull = ConvexHull.Compute(points);
        hull.Validate();
        Assert.True(hull.IsClosed);
        Assert.Equal(8, hull.VertexCount);
        Assert.Equal(8.0, hull.Volume(), 12);
    }

    [Fact]
    public void TwoSpheres_HullVolumeBracketsTheCapsule()
    {
        // Hull of two tessellated unit spheres 3 apart: inscribed in the exact capsule,
        // and well above a single sphere.
        var sphere = MeshPrimitives.UvSphere(1, segments: 32, rings: 16);
        var (positions, _) = sphere.ToIndexed();
        var points = new List<Vector3d>(positions);
        foreach (var p in positions)
            points.Add(p + new Vector3d(0, 0, 3));

        var hull = ConvexHull.Compute(points);
        hull.Validate();
        Assert.True(hull.IsClosed);
        Assert.Equal(2, hull.EulerCharacteristic);

        double capsule = 4.0 / 3.0 * Math.PI + Math.PI * 3; // 4/3·πr³ + πr²·d
        Assert.True(hull.Volume() < capsule, $"hull {hull.Volume()} must be inscribed in capsule {capsule}");
        Assert.True(hull.Volume() > 0.95 * capsule, $"hull {hull.Volume()} too far below capsule {capsule}");
    }

    [Fact]
    public void RandomCloud_HullIsConvexAndContainsEveryInputPoint()
    {
        var random = new Random(1234);
        var points = new List<Vector3d>(500);
        for (int i = 0; i < 500; i++)
        {
            points.Add((
                (random.NextDouble() - 0.5) * 4,
                (random.NextDouble() - 0.5) * 3,
                (random.NextDouble() - 0.5) * 5));
        }

        var hull = ConvexHull.Compute(points);
        hull.Validate();
        Assert.True(hull.IsClosed);
        Assert.Equal(2, hull.EulerCharacteristic);
        Assert.True(hull.Volume() > 0);

        // Convexity: every input point lies on or below every face plane (hull vertices
        // are input points, so this also proves all dihedral angles are convex).
        var (positions, faces) = hull.ToIndexed();
        foreach (var face in faces)
        {
            var a = positions[face[0]];
            var normal = (positions[face[1]] - a).Cross(positions[face[2]] - a).Normalized();
            foreach (var p in points)
            {
                Assert.True(normal.Dot(p - a) <= 1e-8,
                    $"point {p} lies {normal.Dot(p - a)} above a hull face");
            }
        }

        // Every hull vertex is one of the inputs.
        foreach (var v in positions)
            Assert.Contains(points, p => p.DistanceSquaredTo(v) < 1e-24);
    }

    [Fact]
    public void DegenerateInputs_ThrowClearly()
    {
        Assert.Throws<ArgumentException>(() => ConvexHull.Compute([(0, 0, 0), (1, 0, 0), (0, 1, 0)]));
        Assert.Throws<ArgumentException>(
            () => ConvexHull.Compute([(0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0)]));
        Assert.Throws<ArgumentException>(
            () => ConvexHull.Compute([(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0)]));
        Assert.Throws<ArgumentException>(
            () => ConvexHull.Compute([(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0), (0.5, 0.5, 0)]));
    }
}
