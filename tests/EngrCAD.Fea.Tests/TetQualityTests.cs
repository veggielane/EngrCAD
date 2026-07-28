using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Fea.Tests;

public class TetQualityTests
{
    [Fact]
    public void RegularTetrahedron_ScoresPerfectlyOnEveryMeasure()
    {
        // The regular tetrahedron on alternate cube corners: every dihedral is
        // arccos(1/3) = 70.53 degrees and the aspect measure is exactly 1.
        var a = new Vector3d(1, 1, 1);
        var b = new Vector3d(1, -1, -1);
        var c = new Vector3d(-1, 1, -1);
        var d = new Vector3d(-1, -1, 1);

        Span<double> angles = stackalloc double[6];
        TetGeometry.DihedralAngles(a, b, c, d, angles);
        double expected = Math.Acos(1.0 / 3.0) * 180.0 / Math.PI;
        foreach (double angle in angles)
            Assert.Equal(expected, angle * 180.0 / Math.PI, 9);

        Assert.Equal(1.0, TetGeometry.AspectRatio(a, b, c, d), 9);
        // Circumradius sqrt(3); edge 2*sqrt(2). Radius-edge = sqrt(3)/(2 sqrt 2) = 0.6124.
        Assert.Equal(Math.Sqrt(3) / (2 * Math.Sqrt(2)), TetGeometry.RadiusEdgeRatio(a, b, c, d), 9);
    }

    [Fact]
    public void Circumcentre_IsEquidistantFromAllFourVertices()
    {
        var random = new Random(4711);
        for (int i = 0; i < 500; i++)
        {
            var a = RandomPoint(random);
            var b = RandomPoint(random);
            var c = RandomPoint(random);
            var d = RandomPoint(random);
            if (Math.Abs(TetMesh.SignedVolume(a, b, c, d)) < 1e-3)
                continue;

            Assert.True(TetGeometry.TryCircumcentre(a, b, c, d, out var centre, out double radius));
            Assert.Equal(radius, (a - centre).Length, radius * 1e-9);
            Assert.Equal(radius, (b - centre).Length, radius * 1e-9);
            Assert.Equal(radius, (c - centre).Length, radius * 1e-9);
            Assert.Equal(radius, (d - centre).Length, radius * 1e-9);
        }
    }

    [Fact]
    public void DegenerateTetrahedron_IsRefusedRelativeToItsOwnScale()
    {
        // The scale-free guard is the whole point: an absolute epsilon on a determinant is a
        // VOLUME threshold and fails cubically with model scale.
        foreach (double scale in new[] { 1e-4, 1.0, 1e4 })
        {
            var a = new Vector3d(0, 0, 0) * scale;
            var b = new Vector3d(1, 0, 0) * scale;
            var c = new Vector3d(0, 1, 0) * scale;
            var flat = new Vector3d(1, 1, 0) * scale;
            Assert.False(TetGeometry.TryCircumcentre(a, b, c, flat, out _, out _));

            // ... and a legitimately small-but-proper tetrahedron at the same scale is kept.
            var proper = new Vector3d(0.3, 0.3, 0.5) * scale;
            Assert.True(TetGeometry.TryCircumcentre(a, b, c, proper, out _, out double radius));
            Assert.True(radius > 0);
        }
    }

    [Fact]
    public void BoxMesh_ReportsUsableQualityAndAConsistentTotalVolume()
    {
        var box = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(4, 3, 2)));
        var tets = TetMesher.Mesh(box);
        var report = TetQuality.Analyze(tets);

        Assert.Equal(tets.TetCount, report.TetCount);
        Assert.Equal(24.0, report.TotalVolume, 10);
        Assert.Equal(24.0, report.TotalVolume, tets.Volume * 1e-12);
        Assert.True(report.MinDihedralDegrees > 0);
        Assert.True(report.MaxDihedralDegrees < 180);
        Assert.InRange(report.MinAspectRatio, 0, 1.0000001);
        Assert.Equal(tets.TetCount, report.DihedralHistogram.Sum());
        Assert.Equal(tets.TetCount, report.RadiusEdgeHistogram.Sum());

        Assert.Contains("Tetrahedra", report.ToText());
        Assert.Contains("Radius-edge", report.ToText());
    }

    [Fact]
    public void SliverCount_RisesWithTheThreshold_SoTheNumberMeansWhatItSays()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 16, 8);
        var tets = TetMesher.Mesh(sphere);

        int atFive = TetQuality.Analyze(tets, 5).SliverCount;
        int atFifteen = TetQuality.Analyze(tets, 15).SliverCount;
        int atThirty = TetQuality.Analyze(tets, 30).SliverCount;

        Assert.True(atFive <= atFifteen);
        Assert.True(atFifteen <= atThirty);
        Assert.True(atThirty <= tets.TetCount);
    }

    [Fact]
    public void QualityRefinement_ImprovesTheRadiusEdgeDistributionAndKeepsTheVolume()
    {
        var box = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(4, 4, 4)));

        var coarse = TetMesher.Mesh(box);
        var refined = TetMesher.Mesh(box, new TetMeshOptions
        {
            RefineQuality = true,
            RadiusEdgeRatio = 1.6,
            MaxElementSize = 1.5,
        }, out var report);

        Assert.Equal(64.0, refined.Volume, 1e-9);
        Assert.True(report.VolumeResidual < 1e-12, $"residual {report.VolumeResidual:E3}");
        Assert.True(refined.TetCount > coarse.TetCount * 4,
            $"refinement produced {refined.TetCount} tets against {coarse.TetCount}");

        var before = TetQuality.Analyze(coarse);
        var after = TetQuality.Analyze(refined);
        Assert.True(after.MaxEdgeLength < before.MaxEdgeLength,
            $"longest edge {after.MaxEdgeLength:F3} did not improve on {before.MaxEdgeLength:F3}");
    }

    [Fact]
    public void SizingField_GradesTheMesh_SmallElementsWhereTheFieldIsSmall()
    {
        // A field that asks for fine elements near x = 0 and coarse ones near x = 8.
        var box = MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(8, 2, 2)));
        var tets = TetMesher.Mesh(box, new TetMeshOptions
        {
            RefineQuality = true,
            RadiusEdgeRatio = 2.0,
            SizingField = p => 0.4 + 0.35 * p.X,
        }, out var report);

        Assert.Equal(32.0, tets.Volume, 1e-8);
        Assert.True(report.VolumeResidual < 1e-11);

        // Mean element size in the near half must be clearly smaller than in the far half.
        double nearSum = 0, farSum = 0;
        int nearCount = 0, farCount = 0;
        for (int t = 0; t < tets.TetCount; t++)
        {
            var e = tets.GetTet(t);
            var centroid = (tets.Position(e.A) + tets.Position(e.B)
                          + tets.Position(e.C) + tets.Position(e.D)) * 0.25;
            double size = Math.Cbrt(tets.TetVolume(t));
            if (centroid.X < 2.0) { nearSum += size; nearCount++; }
            else if (centroid.X > 6.0) { farSum += size; farCount++; }
        }

        Assert.True(nearCount > 10 && farCount > 10, $"near {nearCount}, far {farCount}");
        double near = nearSum / nearCount, far = farSum / farCount;
        Assert.True(near < far * 0.85,
            $"sizing field did not grade the mesh: mean size {near:F4} near vs {far:F4} far");
    }

    private static Vector3d RandomPoint(Random random) => new(
        random.NextDouble() * 10 - 5, random.NextDouble() * 10 - 5, random.NextDouble() * 10 - 5);
}
