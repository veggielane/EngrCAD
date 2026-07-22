using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class MeshSdfTests
{
    [Fact]
    public void BoxMesh_MatchesAnalyticBoxSdf()
    {
        // A box mesh is geometrically identical to the analytic box SDF, so signed
        // distances must agree everywhere: face, edge, and corner regions, inside and out.
        var meshSdf = new MeshSdf(MeshPrimitives.Box(2, 2, 2));
        var analytic = Sdf.Box(2, 2, 2);

        var rng = new Random(31);
        for (int i = 0; i < 300; i++)
        {
            var p = new Vector3d(
                rng.NextDouble() * 6 - 3,
                rng.NextDouble() * 6 - 3,
                rng.NextDouble() * 6 - 3);
            Assert.Equal(analytic.Evaluate(p), meshSdf.Evaluate(p), 9);
        }
    }

    [Fact]
    public void SphereMesh_ApproximatesAnalyticSphere()
    {
        var meshSdf = new MeshSdf(MeshPrimitives.UvSphere(1.0, segments: 48, rings: 24));
        double chordError = 1.0 - Math.Cos(Math.PI / 24); // max tessellation deviation

        var rng = new Random(37);
        for (int i = 0; i < 200; i++)
        {
            var p = new Vector3d(
                rng.NextDouble() * 4 - 2,
                rng.NextDouble() * 4 - 2,
                rng.NextDouble() * 4 - 2);
            double expected = p.Length - 1.0;
            Assert.True(Math.Abs(meshSdf.Evaluate(p) - expected) < chordError + 1e-9,
                $"at {p}: mesh sdf {meshSdf.Evaluate(p)} vs analytic {expected}");
        }
    }

    [Fact]
    public void SignIsNegativeInsideAndPositiveOutside()
    {
        var sdf = new MeshSdf(MeshPrimitives.Cylinder(1, 2, segments: 32));
        Assert.True(sdf.Evaluate((0, 0, 1)) < 0);       // axis, mid-height
        Assert.True(sdf.Evaluate((0.9, 0, 0.1)) < 0);   // near bottom rim, inside
        Assert.True(sdf.Evaluate((2, 0, 1)) > 0);       // radially outside
        Assert.True(sdf.Evaluate((1.2, 1.2, 2.5)) > 0); // above the top rim, outside
    }

    [Fact]
    public void RoundTrip_MeshToSdfToMesh()
    {
        var original = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);
        var remeshed = SurfaceNets.Polygonize(new MeshSdf(original), resolution: 40);
        remeshed.Validate();

        Assert.True(remeshed.IsClosed);
        Assert.Equal(2, remeshed.EulerCharacteristic);
        Assert.True(Math.Abs(remeshed.Volume() - original.Volume()) / original.Volume() < 0.05,
            $"round-trip volume {remeshed.Volume()} vs original {original.Volume()}");
    }

    [Fact]
    public void ComposesWithAnalyticSdfs()
    {
        // The point of the hybrid kernel: mesh geometry as a first-class implicit node.
        var meshBox = new MeshSdf(MeshPrimitives.Box(1.6, 1.6, 1.6));
        var hybrid = meshBox.SmoothUnion(Sdf.Sphere(0.7).Translate((1.1, 0, 0)), 0.3);

        Assert.True(hybrid.Evaluate((0, 0, 0)) < 0);      // inside the mesh part
        Assert.True(hybrid.Evaluate((1.4, 0, 0)) < 0);    // inside the sphere part
        Assert.True(hybrid.Evaluate((3, 3, 3)) > 0);

        var mesh = SurfaceNets.Polygonize(hybrid, resolution: 48);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.Volume() > 1.6 * 1.6 * 1.6, "blend must add volume beyond the box");
    }

    [Fact]
    public void OpenMesh_IsRejected()
    {
        var open = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)],
            [new[] { 0, 1, 2 }]);
        Assert.Throws<ArgumentException>(() => new MeshSdf(open));
    }
}
