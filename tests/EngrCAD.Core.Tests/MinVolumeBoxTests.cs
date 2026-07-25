using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// <see cref="Fitting3d.MinVolumeBox"/> — the exact (Freeman–Shapira) oriented box, as
/// opposed to <see cref="Fitting3d.FitBox"/>'s PCA heuristic. Hulls are written out by
/// hand so these tests stay inside EngrCAD.Core: the 3D quickhull lives in EngrCAD.Mesh,
/// which is exactly why the API takes a caller-supplied hull.
/// </summary>
public class MinVolumeBoxTests
{
    /// <summary>The 12 triangles of the unit-corner box [0,1]^3 scaled by <paramref name="size"/>.</summary>
    private static (Vector3d[] Vertices, int[] Triangles) Box(in Vector3d size)
    {
        var vertices = new Vector3d[8];
        for (int i = 0; i < 8; i++)
        {
            vertices[i] = new Vector3d(
                (i & 1) == 0 ? 0 : size.X,
                (i & 2) == 0 ? 0 : size.Y,
                (i & 4) == 0 ? 0 : size.Z);
        }
        int[] triangles =
        [
            0, 2, 1, 1, 2, 3,   // z = 0
            4, 5, 6, 5, 7, 6,   // z = max
            0, 1, 4, 1, 5, 4,   // y = 0
            2, 6, 3, 3, 6, 7,   // y = max
            0, 4, 2, 2, 4, 6,   // x = 0
            1, 3, 5, 3, 7, 5,   // x = max
        ];
        return (vertices, triangles);
    }

    private static Vector3d[] Transform(Vector3d[] points, in Matrix4d m)
    {
        var result = new Vector3d[points.Length];
        for (int i = 0; i < points.Length; i++)
            result[i] = m.TransformPoint(points[i]);
        return result;
    }

    private static void AssertContainsAll(in OrientedBox3d box, Vector3d[] points, double slack)
    {
        var tolerance = new Tolerance(slack, slack);
        foreach (var p in points)
            Assert.True(box.Contains(p, tolerance), $"{p} escaped the box");
    }

    [Fact]
    public void AxisAlignedBox_IsRecoveredExactly()
    {
        var (vertices, triangles) = Box((3, 5, 7));
        var box = Fitting3d.MinVolumeBox(vertices, triangles);

        Assert.Equal(3.0 * 5 * 7, box.Volume, 9);
        AssertContainsAll(box, vertices, 1e-9);
    }

    [Fact]
    public void RotatedBox_IsRecoveredExactly_WherePcaAlsoWould()
    {
        var (vertices, triangles) = Box((3, 5, 7));
        var rotated = Transform(vertices,
            Matrix4d.CreateFromAxisAngle(new Vector3d(1, 2, 3).Normalized(), 0.7) *
            Matrix4d.CreateTranslation((10, -4, 2)));

        var box = Fitting3d.MinVolumeBox(rotated, triangles);

        Assert.Equal(3.0 * 5 * 7, box.Volume, 8);
        AssertContainsAll(box, rotated, 1e-9);
    }

    /// <summary>
    /// THE counterexample to the folklore "the minimum-volume box has a face flush with a
    /// hull face" (the 2D calipers theorem does not lift to 3D). A regular tetrahedron on
    /// alternate corners of the cube [−1, 1]³ is bounded by that cube, volume 8, touching
    /// it only at corners — flush with no face at all. Every face-flush candidate measures
    /// 16: the equilateral section's minimum rectangle a²√3/2 = 6.928 times the height
    /// 4/√3 = 2.309. If this test ever starts reporting 16, someone has "simplified"
    /// <see cref="Fitting3d.MinVolumeBox"/> back to the face-flush search.
    /// </summary>
    [Fact]
    public void RegularTetrahedron_BeatsEveryFaceFlushBox()
    {
        Vector3d[] vertices = [(1, 1, 1), (1, -1, -1), (-1, 1, -1), (-1, -1, 1)];
        int[] triangles = [0, 1, 2, 0, 2, 3, 0, 3, 1, 1, 3, 2];

        var box = Fitting3d.MinVolumeBox(vertices, triangles);

        AssertContainsAll(box, vertices, 1e-9);
        Assert.Equal(8.0, box.Volume, 6);
        double edge = Math.Sqrt(8);
        double faceFlush = edge * edge * Math.Sqrt(3) / 2 * (4 / Math.Sqrt(3));
        Assert.Equal(16.0, faceFlush, 6);
        Assert.True(box.Volume < faceFlush * 0.9,
            $"{box.Volume} is not beating the best face-flush box {faceFlush}");
    }

    /// <summary>
    /// A random tetrahedron IS its own hull, so this sweeps the exact method over arbitrary
    /// shapes: the minimum must never lose to the PCA box or the axis-aligned box, and must
    /// contain every point. The strict-win count keeps the test honest — a bound that is
    /// never actually tighter would prove nothing.
    /// </summary>
    [Fact]
    public void NeverLosesToPcaOrTheAxisAlignedBox()
    {
        var random = new Random(20260725);
        int[] triangles = [0, 1, 2, 0, 1, 3, 0, 2, 3, 1, 2, 3];
        int strictWinsOverPca = 0;
        int trials = 0;

        while (trials < 60)
        {
            var vertices = new Vector3d[4];
            for (int i = 0; i < 4; i++)
            {
                vertices[i] = new Vector3d(
                    random.NextDouble() * 10 - 5,
                    random.NextDouble() * 10 - 5,
                    random.NextDouble() * 10 - 5);
            }
            // Skip near-degenerate tetrahedra: their exact minimum is a sliver where both
            // methods agree at ~0 and the comparison says nothing.
            double signedVolume = (vertices[1] - vertices[0])
                .Cross(vertices[2] - vertices[0]).Dot(vertices[3] - vertices[0]) / 6;
            if (Math.Abs(signedVolume) < 1.0)
                continue;
            trials++;

            var box = Fitting3d.MinVolumeBox(vertices, triangles);
            AssertContainsAll(box, vertices, 1e-9);

            var aabb = Aabb.FromPoints(vertices);
            var pca = Fitting3d.FitBox(vertices);
            Assert.True(box.Volume <= aabb.Volume * (1 + 1e-12) + 1e-9,
                $"exact {box.Volume} lost to the axis-aligned {aabb.Volume}");
            Assert.True(box.Volume <= pca.Volume * (1 + 1e-12) + 1e-9,
                $"exact {box.Volume} lost to PCA {pca.Volume}");
            if (box.Volume < pca.Volume * (1 - 1e-6))
                strictWinsOverPca++;
        }

        Assert.True(strictWinsOverPca > trials / 2,
            $"only {strictWinsOverPca}/{trials} strict wins — the comparison has no teeth");
    }

    /// <summary>
    /// The edge-pair loop runs in parallel. Each index writes its own slot and the
    /// reduction runs in index order, so the answer must be bit-identical every time —
    /// not merely equal to a tolerance.
    /// </summary>
    [Fact]
    public void IsBitIdenticalUnderRepeatedParallelRuns()
    {
        var random = new Random(4242);
        var vertices = new Vector3d[4];
        for (int i = 0; i < 4; i++)
        {
            vertices[i] = new Vector3d(
                random.NextDouble() * 10, random.NextDouble() * 10, random.NextDouble() * 10);
        }
        int[] triangles = [0, 1, 2, 0, 1, 3, 0, 2, 3, 1, 2, 3];

        var first = Fitting3d.MinVolumeBox(vertices, triangles);
        for (int trial = 0; trial < 8; trial++)
        {
            var again = Fitting3d.MinVolumeBox(vertices, triangles);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(first.Volume),
                BitConverter.DoubleToInt64Bits(again.Volume));
            Assert.Equal(first.Frame.X, again.Frame.X);
            Assert.Equal(first.Frame.Origin, again.Frame.Origin);
        }
    }

    [Fact]
    public void FlatCloud_SaysWhatToUseInstead()
    {
        Vector3d[] vertices = [(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0)];
        // A flat "hull": every triangle spans a plane, but there is no volume — this is the
        // case the caller's hull builder would already have refused.
        var ex = Assert.Throws<ArgumentException>(() =>
            Fitting3d.MinVolumeBox(vertices, Array.Empty<int>()));
        Assert.Contains("FitBox", ex.Message);
    }

    [Fact]
    public void EmptyInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => Fitting3d.MinVolumeBox([], Array.Empty<int>()));
        Assert.Throws<ArgumentException>(() =>
            Fitting3d.MinVolumeBox([new Vector3d(0, 0, 0)], new[] { 0, 0 }));
    }
}
