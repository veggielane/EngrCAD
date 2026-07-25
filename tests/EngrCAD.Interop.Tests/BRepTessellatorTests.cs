using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class BRepTessellatorTests
{
    [Fact]
    public void Box_TessellatesToExactClosedMesh()
    {
        var solid = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4)));
        var mesh = BRepTessellator.Tessellate(solid);
        mesh.Validate();

        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(24, mesh.Volume(), 9); // planar faces tessellate exactly
        Assert.Equal(2 * (6 + 8 + 12), mesh.SurfaceArea(), 9);
    }

    [Fact]
    public void Cylinder_TessellatesToClosedPrism()
    {
        int n = 48;
        double r = 1.5, h = 4;
        var solid = SolidFactory.MakeCylinder(r, h);
        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: n);
        mesh.Validate();

        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        // The tessellation is exactly an n-gonal prism: caps and band share circle samples.
        double prismVolume = 0.5 * n * r * r * Math.Sin(2 * Math.PI / n) * h;
        Assert.Equal(prismVolume, mesh.Volume(), 9);
    }

    [Fact]
    public void Cylinder_MeshIsRenderable()
    {
        var solid = SolidFactory.MakeCylinder(1, 2);
        var mesh = BRepTessellator.Tessellate(solid);
        var render = EngrCAD.Mesh.RenderMesh.CreateFlat(mesh);
        Assert.True(render.TriangleCount > 0);
    }

    // ---- progress + cooperative cancellation ----

    [Fact]
    public void Progress_RisesMonotonicallyAndFinishesAtOne()
    {
        var solid = SolidFactory.MakeCylinder(1, 2);
        var fractions = new List<double>();
        var progress = new ProgressCancel(fractions.Add);

        var mesh = BRepTessellator.Tessellate(solid, progress: progress);

        Assert.True(mesh.IsClosed);
        Assert.NotEmpty(fractions);
        for (int i = 1; i < fractions.Count; i++)
            Assert.True(fractions[i] >= fractions[i - 1], "progress must never go backwards");
        Assert.Equal(1.0, fractions[^1]);
    }

    /// <summary>
    /// Cancellation is polled at edge and face boundaries and surfaces as an exception —
    /// never as a partial mesh, which the kernel's contract forbids.
    /// </summary>
    [Fact]
    public void Cancellation_ThrowsAndReturnsNoPartialMesh()
    {
        var solid = SolidFactory.MakeCylinder(1, 2);
        int reports = 0;
        var progress = new ProgressCancel(() => reports > 0, _ => reports++);

        Assert.Throws<OperationCanceledException>(
            () => BRepTessellator.Tessellate(solid, progress: progress));
    }

    /// <summary>
    /// A null progress is the default and must stay free: the same solid tessellates to a
    /// bit-identical mesh with and without an observer.
    /// </summary>
    [Fact]
    public void ObservingProgress_DoesNotChangeTheMesh()
    {
        var solid = SolidFactory.MakeCylinder(1, 2);
        var plain = BRepTessellator.Tessellate(solid, segmentsPerCircle: 24);
        var watched = BRepTessellator.Tessellate(
            solid, segmentsPerCircle: 24, progress: new ProgressCancel(_ => { }));

        Assert.Equal(plain.VertexCount, watched.VertexCount);
        Assert.Equal(plain.FaceCount, watched.FaceCount);
        for (int v = 0; v < plain.VertexCount; v++)
            Assert.Equal(plain.GetVertex(v).Position, watched.GetVertex(v).Position);
    }
}
