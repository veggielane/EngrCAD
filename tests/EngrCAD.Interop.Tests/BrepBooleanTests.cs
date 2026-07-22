using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class BrepBooleanTests
{
    // Inputs are consumed by the boolean (faces split in place), so build fresh ones.
    private static BrepSolid BoxA() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
    private static BrepSolid BoxB() => SolidFactory.MakeBox(new Aabb((1, 1, 1), (3, 3, 3)));

    private static double MeshedVolume(BrepSolid solid, out EngrCAD.Mesh.HalfEdgeMesh mesh, int segments = 32, int genus = 0)
    {
        // Seam sealing makes boolean output a fully valid topological solid.
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus), "boolean result must satisfy Euler–Poincaré");

        mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: segments);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "boolean result must tessellate closed");
        return mesh.Volume();
    }

    [Fact]
    public void Union_OffsetBoxes_ExactVolume()
    {
        var result = BrepBoolean.Union(BoxA(), BoxB());
        double volume = MeshedVolume(result, out var mesh);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(8 + 8 - 1, volume, 9);
        Assert.DoesNotContain(result.Faces, f => f.IsReversed);
    }

    [Fact]
    public void Intersection_OffsetBoxes_ExactUnitCube()
    {
        var result = BrepBoolean.Intersection(BoxA(), BoxB());
        double volume = MeshedVolume(result, out var mesh);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(1.0, volume, 9);
        Assert.Equal(6, result.Faces.Count());
    }

    [Fact]
    public void Difference_OffsetBoxes_ExactVolume()
    {
        var result = BrepBoolean.Difference(BoxA(), BoxB());
        double volume = MeshedVolume(result, out var mesh);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(8 - 1, volume, 9);
        // The bite's walls come from B, reversed.
        Assert.Contains(result.Faces, f => f.IsReversed);
    }

    [Fact]
    public void Difference_DrillThrough_ExactBoreVolumeAndGenus()
    {
        int n = 32;
        double radius = 0.4;
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));
        var tool = SolidFactory.Extrude(
            Profile.Circle((0, 0, -1), Vector3d.UnitX, Vector3d.UnitY, radius),
            (0, 0, 3));

        var result = BrepBoolean.Difference(box, tool);
        double volume = MeshedVolume(result, out var mesh, n, genus: 1);

        Assert.Equal(0, mesh.EulerCharacteristic); // through-hole: genus 1
        double boreArea = 0.5 * n * radius * radius * Math.Sin(2 * Math.PI / n);
        Assert.Equal(4.0 - boreArea, volume, 9);

        // 4 sides + 2 annular caps + reversed bore wall.
        Assert.Equal(7, result.Faces.Count());
        var wall = Assert.Single(result.Faces, f => f.IsReversed);
        Assert.IsType<ExtrudedSurface>(wall.Surface);
    }

    [Fact]
    public void Intersection_BoxWithTallCylinder_IsCylinderSegment()
    {
        int n = 32;
        double radius = 0.4;
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));
        var tool = SolidFactory.Extrude(
            Profile.Circle((0, 0, -1), Vector3d.UnitX, Vector3d.UnitY, radius),
            (0, 0, 3));

        var result = BrepBoolean.Intersection(box, tool);
        double volume = MeshedVolume(result, out var mesh, n);

        Assert.Equal(2, mesh.EulerCharacteristic);
        double boreArea = 0.5 * n * radius * radius * Math.Sin(2 * Math.PI / n);
        Assert.Equal(boreArea * 1.0, volume, 9); // the bore prism clipped to the box height
    }
}
