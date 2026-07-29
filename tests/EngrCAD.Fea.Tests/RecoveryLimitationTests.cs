using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The v1 boundary-recovery limitation, pinned so it is visible and so its eventual fix is
/// visible too.
///
/// <para>Recovery is happy with CAD tessellations — B-Rep output, primitives, Surface Nets
/// fields — where the input triangles are already faces of the Delaunay tetrahedralization
/// (measured: zero recovery rounds on every other fixture in this suite). It is NOT yet happy
/// with an isotropic REMESH, whose vertices sit at near-uniform spacing with no structure:
/// enough of its triangles fail to be Delaunay faces that red subdivision does not clear
/// them, and the budget runs out.</para>
///
/// <para>The point of these tests is that the mesher <b>refuses by name</b> rather than
/// returning a mesh whose boundary is quietly not the input surface. When recovery is
/// strengthened, these tests fail loudly and get rewritten as positive ones — which is the
/// intended outcome, not a regression.</para>
/// </summary>
public class RecoveryLimitationTests
{
    [Theory]
    [InlineData(3.0, 30.0)]
    [InlineData(3.0, 0.0)]
    [InlineData(5.0, 30.0)]
    public void ARemeshedCylinder_IsRefusedByName(double targetEdge, double featureAngle)
    {
        var raw = MeshPrimitives.Cylinder(10, 20, 48);
        var remeshed = Remesher.Remesh(raw, new RemeshOptions(targetEdge)
        {
            Iterations = 12,
            FeatureAngleDegrees = featureAngle,
        }).Mesh;

        Assert.True(remeshed.IsClosed, "the remesher itself must still produce a closed surface");

        var ex = Assert.Throws<TetMeshException>(() =>
            TetMesher.Mesh(remeshed, new TetMeshOptions { MaxSteinerPoints = 20_000 }));

        // Either failure mode is the same limitation, and both name a way forward.
        Assert.True(
            ex.Message.Contains("budget") || ex.Message.Contains("did not converge"),
            $"unexpected refusal: {ex.Message}");
    }

    [Fact]
    public void ARemeshedSphere_IsRefusedByName()
    {
        var remeshed = Remesher.Remesh(
            MeshPrimitives.UvSphere(10, 32, 16),
            new RemeshOptions(3.0) { Iterations = 12, FeatureAngleDegrees = 0 }).Mesh;

        var ex = Assert.Throws<TetMeshException>(() =>
            TetMesher.Mesh(remeshed, new TetMeshOptions { MaxSteinerPoints = 20_000 }));
        Assert.True(
            ex.Message.Contains("budget") || ex.Message.Contains("did not converge"),
            $"unexpected refusal: {ex.Message}");
    }

    [Fact]
    public void TheTessellationsTheyCameFrom_NeedNoRecoveryAtAll()
    {
        // The contrast is the finding: it is the remesh that recovery cannot take, not the
        // shape, and structured input needs ZERO rounds.
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
}
