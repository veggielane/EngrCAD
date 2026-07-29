using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// <see cref="MeshFitting"/> — the mesh-side bridge onto <see cref="Fitting3d.MinVolumeBox"/>.
/// Its whole job is to be the flattening every caller was writing by hand, so the tests
/// assert that it agrees EXACTLY with the hand-written dance rather than merely being
/// close, and that hulling a raw cloud is what makes the answer the true minimum.
/// </summary>
public class MeshFittingTests
{
    private static Vector3d[] Cloud(int count, int seed)
    {
        var random = new Random(seed);
        var points = new Vector3d[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = new Vector3d(
                random.NextDouble() * 6 - 3,
                random.NextDouble() * 4 - 2,
                random.NextDouble() * 10 - 5);
        }
        return points;
    }

    [Fact]
    public void MinVolumeBox_MatchesTheHandWrittenFlatteningExactly()
    {
        var points = Cloud(60, 12345);
        var hull = ConvexHull.Compute(points);

        var (positions, faces) = hull.Triangulated().ToIndexed();
        var triangles = faces.SelectMany(f => f).ToArray();
        var reference = Fitting3d.MinVolumeBox(positions, triangles);

        var box = MeshFitting.MinVolumeBox(hull);

        // Same inputs in the same order, so the search is deterministic and this is an
        // identity, not a tolerance.
        Assert.Equal(reference.Volume, box.Volume);
        Assert.Equal(reference.Center, box.Center);
        Assert.Equal(reference.HalfExtents, box.HalfExtents);
    }

    [Fact]
    public void MinVolumeBoxOf_HullsTheCloudFirst_AndContainsEveryPoint()
    {
        var points = Cloud(200, 99);
        var box = MeshFitting.MinVolumeBoxOf(points);

        var tolerance = new Tolerance(1e-9, 1e-9);
        foreach (var p in points)
            Assert.True(box.Contains(p, tolerance), $"{p} escaped the box");

        // It can never lose to the axis-aligned bound the cloud came from.
        var bounds = Aabb.Empty;
        foreach (var p in points)
            bounds = bounds.Union(p);
        Assert.True(box.Volume <= bounds.Size.X * bounds.Size.Y * bounds.Size.Z + 1e-9);
    }

    /// <summary>
    /// The tetrahedron counterexample, reached through the mesh bridge: a regular
    /// tetrahedron on alternate corners of [−1, 1]³ fits its cube at volume 8 while every
    /// face-flush candidate measures 16. Repeated here because the bridge is where a
    /// future "simplification" would most plausibly substitute <c>FitBox</c>.
    /// </summary>
    [Fact]
    public void RegularTetrahedron_StillBeatsEveryFaceFlushBox_ThroughTheBridge()
    {
        Vector3d[] corners = [(1, 1, 1), (1, -1, -1), (-1, 1, -1), (-1, -1, 1)];
        var box = MeshFitting.MinVolumeBoxOf(corners);

        Assert.Equal(8.0, box.Volume, 6);
        Assert.True(box.Volume < 16 * 0.9, $"{box.Volume} is not beating the face-flush 16");
    }

    [Fact]
    public void MinVolumeBoxOf_RefusesACloudTooSmallToHull()
    {
        Assert.Throws<ArgumentException>(() =>
            MeshFitting.MinVolumeBoxOf([(0, 0, 0), (1, 0, 0), (0, 1, 0)]));
    }
}
