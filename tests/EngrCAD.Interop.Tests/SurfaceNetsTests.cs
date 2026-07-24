using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class SurfaceNetsTests
{
    [Fact]
    public void Sphere_ClosedGenusZeroAccurateVolume()
    {
        var mesh = SurfaceNets.Polygonize(Sdf.Sphere(1), resolution: 48);
        mesh.Validate();

        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        double exact = 4.0 / 3.0 * Math.PI;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.02,
            $"volume {mesh.Volume()} should be within 2% of {exact}");
    }

    [Fact]
    public void Polygonize_IsDeterministicAcrossRuns()
    {
        // Sampling runs in parallel; the mesh must still come out bit-for-bit identical.
        var sdf = Sdf.Sphere(1) - Sdf.Box(0.7, 0.7, 3);
        var (p1, f1) = SurfaceNets.Polygonize(sdf, resolution: 64).ToIndexed();
        var (p2, f2) = SurfaceNets.Polygonize(sdf, resolution: 64).ToIndexed();

        Assert.Equal(p1, p2); // Vector3d equality is bitwise
        Assert.Equal(f1.Count, f2.Count);
        for (int i = 0; i < f1.Count; i++)
            Assert.Equal(f1[i], f2[i]);
    }

    [Fact]
    public void Cancellation_ThrowsOperationCanceled()
    {
        var cancel = new ProgressCancel(() => true);
        Assert.Throws<OperationCanceledException>(() =>
            SurfaceNets.Polygonize(Sdf.Sphere(1), resolution: 32, progress: cancel));
    }

    [Fact]
    public void Progress_ReportsUpToCompletion()
    {
        var fractions = new List<double>();
        var mesh = SurfaceNets.Polygonize(Sdf.Sphere(1), resolution: 32,
            progress: new ProgressCancel(f => { lock (fractions) fractions.Add(f); }));

        Assert.True(mesh.IsClosed);
        Assert.NotEmpty(fractions);
        Assert.Equal(1.0, fractions[^1]);
        Assert.All(fractions, f => Assert.InRange(f, 0.0, 1.0));
    }

    [Fact]
    public void Sphere_VerticesLieNearTheSurface()
    {
        var sdf = Sdf.Sphere(1);
        var mesh = SurfaceNets.Polygonize(sdf, resolution: 32);
        double cell = 2.0 / 32 * 1.2; // sampling cell size plus slack

        foreach (var v in mesh.Vertices)
            Assert.True(Math.Abs(sdf.Evaluate(v.Position)) < cell, $"vertex {v.Index} is {sdf.Evaluate(v.Position)} away");
    }

    [Fact]
    public void Box_VolumeMatches()
    {
        var mesh = SurfaceNets.Polygonize(Sdf.Box(2, 1.5, 1), resolution: 48);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        double exact = 2 * 1.5 * 1;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.03);
    }

    [Fact]
    public void Torus_GenusOneTopology()
    {
        var mesh = SurfaceNets.Polygonize(Sdf.Torus(1, 0.35), resolution: 56);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // torus: V − E + F = 0
    }

    [Fact]
    public void SmoothUnion_SingleBlendedComponent()
    {
        var blob = Sdf.Sphere(0.7).Translate((-0.45, 0, 0))
            .SmoothUnion(Sdf.Sphere(0.7).Translate((0.45, 0, 0)), 0.4);
        var mesh = SurfaceNets.Polygonize(blob, resolution: 48);
        mesh.Validate();

        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic); // blended into one genus-0 blob

        double singleSphere = 4.0 / 3.0 * Math.PI * Math.Pow(0.7, 3);
        Assert.True(mesh.Volume() > singleSphere, "blend of two spheres beats one sphere");
    }

    [Fact]
    public void GyroidLattice_ProducesRenderableMesh()
    {
        var lattice = Sdf.Sphere(1) & Sdf.Gyroid(0.7, 0.15);
        var mesh = SurfaceNets.Polygonize(lattice, resolution: 64);

        Assert.True(mesh.FaceCount > 500, "lattice should generate substantial geometry");
        Assert.True(mesh.SignedVolume() > 0, "winding should be outward");
        var render = EngrCAD.Mesh.RenderMesh.CreateFlat(mesh);
        Assert.True(render.TriangleCount > 0);
    }

    [Fact]
    public void AutoBounds_RequiresFiniteField()
    {
        Assert.Throws<ArgumentException>(() => SurfaceNets.Polygonize(Sdf.Gyroid(1, 0.1)));
    }

    [Fact]
    public void ExplicitRegion_SurfaceCrossingBoundaryComesOutOpen()
    {
        // Sample only half of the sphere: the surface exits the region, so the mesh is open.
        var region = new Aabb((-1.5, -1.5, -1.5), (0, 1.5, 1.5));
        var mesh = SurfaceNets.Polygonize(Sdf.Sphere(1), region, resolution: 24);
        Assert.False(mesh.IsClosed);
        Assert.NotEmpty(mesh.BoundaryLoops());
    }
}
