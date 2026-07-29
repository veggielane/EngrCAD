using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// What boundary recovery wants of its input surface — pinned from both sides, because the
/// interesting half is what it ACCEPTS.
///
/// <para><b>The filed limitation was wrong in two directions and these tests record the
/// corrected boundary.</b> It said recovery "is not happy with an isotropic remesh", because
/// near-uniform vertex spacing has no structure. Measured, neither half holds:</para>
///
/// <list type="bullet">
/// <item>A remeshed SPHERE meshes in <b>zero</b> recovery rounds at three target edge lengths
/// once the remesh is Delaunay-clean (<see cref="RemeshOptions.PreventLongEdgeFlips"/>), with
/// one patch per triangle throughout — so "no structure" is not the obstacle and per-triangle
/// patches are not either.</item>
/// <item>A remeshed BOX with a 0.145 degree worst angle and a radius-edge ratio of 198 meshes
/// fine, while a remeshed sphere with a 27.9 degree worst angle and a ratio of 1.07 is
/// refused — so triangle QUALITY is not the criterion either.</item>
/// </list>
///
/// <para>What actually decides it is whether the surface triangulation is already the boundary
/// of the Delaunay tetrahedralization of its own vertices. Where the surface is FLAT a patch
/// absorbs any diagonal, so nothing has to be recovered (a box is 6–7 patches however badly it
/// is triangulated). Where it is CURVED every triangle is its own patch and must appear
/// verbatim, and refinement cannot manufacture that.</para>
/// </summary>
public class RecoveryLimitationTests
{
    /// <summary>
    /// The positive case, and the one that overturns the filed limitation: a remeshed curved
    /// surface is meshed with NO recovery at all when its triangulation is Delaunay-clean.
    /// </summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    public void ADelaunayCleanRemeshedSphere_MeshesWithNoRecoveryAtAll(double targetEdge)
    {
        var remeshed = Remesher.Remesh(
            MeshPrimitives.UvSphere(10, 32, 16),
            new RemeshOptions(targetEdge)
            {
                Iterations = 20,
                FeatureAngleDegrees = 0,
                PreventLongEdgeFlips = true,
            }).Mesh;

        var tets = TetMesher.Mesh(remeshed, null, out var report);

        Assert.Equal(0, report.RecoveryRounds);
        Assert.True(tets.TetCount > 0);
        Assert.True(report.VolumeResidual < 1e-12, $"residual {report.VolumeResidual:E3}");

        // Every triangle is its own patch — the configuration the filed diagnosis blamed.
        // It meshes anyway, which is precisely why the diagnosis was wrong.
        Assert.Equal(remeshed.Triangulated().FaceCount, report.SurfacePatches);
    }

    /// <summary>
    /// Without that flag the same remesh is refused. The flip stage is valence-driven with no
    /// length term, so it can replace a Delaunay diagonal with a longer one — and a surface
    /// triangle that is not locally Delaunay cannot be a face of the tetrahedralization.
    /// </summary>
    [Fact]
    public void TheSameSphereRemeshedWithoutTheFlipGuard_IsRefusedByName()
    {
        var remeshed = Remesher.Remesh(
            MeshPrimitives.UvSphere(10, 32, 16),
            new RemeshOptions(3.0) { Iterations = 12, FeatureAngleDegrees = 0 }).Mesh;

        var ex = Assert.Throws<TetMeshException>(() =>
            TetMesher.Mesh(remeshed, new TetMeshOptions { MaxSteinerPoints = 20_000 }));

        // The refusal must name the mechanism, not merely report a budget.
        Assert.Contains("PreventLongEdgeFlips", ex.Message);
        Assert.Contains("locally Delaunay", ex.Message);
    }

    /// <summary>
    /// A remeshed cylinder is still refused at every setting tried — and the refusal now
    /// reports the input's own measured quality rather than telling the caller to remesh, which
    /// was backwards for exactly this input.
    ///
    /// <para>`MeshPrimitives.Cylinder`'s n-gon caps triangulate as a one-corner fan (worst
    /// angle 3.74 degrees before any remeshing), and remeshing usually makes it far worse:
    /// measured across six settings the worst angle lands between 0.013 and 7.7 degrees with a
    /// radius-edge ratio between 3.7 and 2124.</para>
    /// </summary>
    [Theory]
    [InlineData(3.0, 30.0, false)]
    [InlineData(3.0, 0.0, false)]
    [InlineData(5.0, 30.0, false)]
    [InlineData(3.0, 30.0, true)]
    public void ARemeshedCylinder_IsRefusedWithTheInputsOwnQualityMeasured(
        double targetEdge, double featureAngle, bool preventLongEdgeFlips)
    {
        var raw = MeshPrimitives.Cylinder(10, 20, 48);
        var remeshed = Remesher.Remesh(raw, new RemeshOptions(targetEdge)
        {
            Iterations = 12,
            FeatureAngleDegrees = featureAngle,
            PreventLongEdgeFlips = preventLongEdgeFlips,
        }).Mesh;

        Assert.True(remeshed.IsClosed, "the remesher itself must still produce a closed surface");

        var ex = Assert.Throws<TetMeshException>(() =>
            TetMesher.Mesh(remeshed, new TetMeshOptions { MaxSteinerPoints = 20_000 }));

        // Every refusal states the measured worst triangle, whichever branch it took...
        Assert.Contains("radius-edge ratio", ex.Message);
        // ...and none of them repeats the old backwards advice.
        Assert.DoesNotContain("remesh the surface", ex.Message);
    }

    /// <summary>
    /// Where the remesh genuinely carries slivers the refusal says so and points at the
    /// SURFACE — the correction that matters, since the old message sent the caller back to
    /// the remesher that had just produced them.
    /// </summary>
    [Fact]
    public void ARemeshThatCarriesSlivers_IsBlamedOnTheSurface()
    {
        var remeshed = Remesher.Remesh(
            MeshPrimitives.Cylinder(10, 20, 48),
            new RemeshOptions(3.0) { Iterations = 12, FeatureAngleDegrees = 30 }).Mesh;

        var (worstAngle, worstRadiusEdge) = WorstTriangle(remeshed);
        Assert.True(worstAngle < 5.0 || worstRadiusEdge > 5.0,
            $"expected slivers; worst angle {worstAngle:F3}, worst radius-edge {worstRadiusEdge:F2}");

        var ex = Assert.Throws<TetMeshException>(() =>
            TetMesher.Mesh(remeshed, new TetMeshOptions { MaxSteinerPoints = 20_000 }));

        Assert.Contains("sliver", ex.Message);
        Assert.Contains("Fix the SURFACE", ex.Message);
    }

    /// <summary>
    /// A flat surface is recovered however badly it is triangulated — the other half of the
    /// correction. Patches absorb any diagonal, so a box is a handful of patches whatever the
    /// remesher did to it.
    /// </summary>
    [Fact]
    public void ARemeshedBox_MeshesDespiteAnAwfulTriangulation()
    {
        var remeshed = Remesher.Remesh(
            MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(20, 20, 20))),
            new RemeshOptions(2.0) { Iterations = 20, FeatureAngleDegrees = 30 }).Mesh;

        var (worstAngle, worstRadiusEdge) = WorstTriangle(remeshed);
        Assert.True(worstAngle < 5.0 || worstRadiusEdge > 5.0,
            $"expected a poor triangulation to make the point; worst angle {worstAngle:F3}, " +
            $"worst radius-edge {worstRadiusEdge:F2}");

        var tets = TetMesher.Mesh(remeshed, null, out var report);

        Assert.Equal(0, report.RecoveryRounds);
        Assert.True(tets.TetCount > 0);
        // Six box faces, so a handful of patches however many triangles there are.
        Assert.True(report.SurfacePatches <= 12,
            $"a box should collapse to a handful of patches, got {report.SurfacePatches}");
        Assert.True(report.VolumeResidual < 1e-12, $"residual {report.VolumeResidual:E3}");
    }

    /// <summary>
    /// A non-converging recovery says so instead of spending everything it was allowed.
    ///
    /// <para>The count on a plainly remeshed sphere runs 6, 10, 11, 7, 5 and then sits at 5
    /// forever — 5 at round 12 and still 5 at round 40. It is NOT a stall in the strict sense:
    /// the five faces differ every round, each smaller than the last, until by round 40 their
    /// three vertices agree to 1e-11 on a radius-10 sphere. So an identical-set test does not
    /// fire and a monotone-decrease rule does, which is why that is the rule.</para>
    /// </summary>
    [Fact]
    public void ANonConvergingRecovery_StopsEarlyAndSaysSo()
    {
        var remeshed = Remesher.Remesh(
            MeshPrimitives.UvSphere(10, 32, 16),
            new RemeshOptions(3.0) { Iterations = 12, FeatureAngleDegrees = 0 }).Mesh;

        var ex = Assert.Throws<TetMeshException>(() =>
            TetMesher.Mesh(remeshed, new TetMeshOptions
            {
                MaxSteinerPoints = 200_000,
                MaxRecoveryRounds = 40,
            }));

        Assert.Contains("not converging", ex.Message);
        // It must stop long before the 40 rounds it was allowed, and say a bigger budget is
        // not the answer — the opposite of what the old message implied.
        Assert.DoesNotContain("did not converge after 40", ex.Message);
        Assert.Contains("will not help", ex.Message);
    }

    [Fact]
    public void TheTessellationsTheyCameFrom_NeedNoRecoveryAtAll()
    {
        // Structured CAD tessellations need ZERO rounds — including the cylinder, whose worst
        // triangle (3.74 degrees) is far worse than the remeshed sphere's that gets refused.
        // That contrast is the finding: quality is not the criterion.
        foreach (var surface in new[]
        {
            MeshPrimitives.Cylinder(10, 20, 48),
            MeshPrimitives.UvSphere(10, 32, 16),
            MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(10, 20, 5))),
        })
        {
            var tets = TetMesher.Mesh(surface, null, out var report);
            Assert.Equal(0, report.RecoveryRounds);
            Assert.True(report.VolumeResidual < 1e-12, $"residual {report.VolumeResidual:E3}");
            Assert.True(tets.TetCount > 0);
        }
    }

    /// <summary>Worst (minimum angle in degrees, radius-edge ratio) over a surface's triangles.</summary>
    private static (double MinAngleDegrees, double MaxRadiusEdge) WorstTriangle(HalfEdgeMesh mesh)
    {
        double worstAngle = 180.0, worstRadiusEdge = 0;
        foreach (var face in mesh.Triangulated().Faces)
        {
            var vs = face.Vertices().Select(v => v.Position).ToArray();
            if (vs.Length != 3)
                continue;
            for (int i = 0; i < 3; i++)
            {
                var u = vs[(i + 1) % 3] - vs[i];
                var w = vs[(i + 2) % 3] - vs[i];
                if (u.Length <= 0 || w.Length <= 0)
                    continue;
                worstAngle = Math.Min(worstAngle,
                    Math.Acos(Math.Clamp(u.Dot(w) / (u.Length * w.Length), -1, 1)) * 180.0 / Math.PI);
            }

            double e0 = (vs[1] - vs[0]).Length, e1 = (vs[2] - vs[1]).Length, e2 = (vs[0] - vs[2]).Length;
            double area = (vs[1] - vs[0]).Cross(vs[2] - vs[0]).Length * 0.5;
            double shortest = Math.Min(e0, Math.Min(e1, e2));
            if (area > 0 && shortest > 0)
                worstRadiusEdge = Math.Max(worstRadiusEdge, e0 * e1 * e2 / (4 * area) / shortest);
        }
        return (worstAngle, worstRadiusEdge);
    }
}
