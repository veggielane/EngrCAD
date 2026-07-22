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
}
