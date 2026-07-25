using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Shelled and offset solids through the tessellator. Every face stays planar, so these
/// volumes are exact — a shelled box's wall volume is arithmetic, not an approximation.
/// </summary>
public class ShellingTessellationTests
{
    private static BrepSolid Block() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (20, 30, 10)));

    private static BrepFace TopOf(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(Vector3d.UnitZ).Single();

    [Fact]
    public void Shell_BoxWithTopOpen_HasTheExactTrayVolume()
    {
        var block = Block();
        var tray = Shelling.Shell(block, 2, f => ReferenceEquals(f, TopOf(block)));
        var mesh = BRepTessellator.Tessellate(tray);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        // Outer 20 x 30 x 10 minus a cavity 16 x 26 x 8 that reaches the open top.
        Assert.Equal(20.0 * 30 * 10 - 16.0 * 26 * 8, mesh.Volume(), 9);
    }

    [Fact]
    public void Shell_SealedVoid_HasTheExactWallVolume()
    {
        var mesh = BRepTessellator.Tessellate(Shelling.Shell(Block(), 2));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        // Two closed surfaces: outer boundary plus the void.
        Assert.Equal(4, mesh.EulerCharacteristic);
        Assert.Equal(20.0 * 30 * 10 - 16.0 * 26 * 6, mesh.Volume(), 9);
    }

    [Fact]
    public void Shell_OpenBothEnds_IsAnExactRectangularTube()
    {
        var block = Block();
        var top = TopOf(block);
        var bottom = block.PlanarFacesWithNormal(-Vector3d.UnitZ).Single();
        var tube = Shelling.Shell(block, 2, f => ReferenceEquals(f, top) || ReferenceEquals(f, bottom));
        var mesh = BRepTessellator.Tessellate(tube);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1
        Assert.Equal(20.0 * 30 * 10 - 16.0 * 26 * 10, mesh.Volume(), 9);
    }

    [Fact]
    public void Offset_Box_HasTheExactGrownVolume()
    {
        var mesh = BRepTessellator.Tessellate(
            Shelling.Offset(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))), 0.5));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(3.0 * 4 * 5, mesh.Volume(), 9);
    }

    [Fact]
    public void Offset_PlateWithHole_ShrinksTheOutsideAndGrowsTheHole()
    {
        var plate = Profile.FromPoints([(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0)]);
        var hole = Profile.FromPoints([(8, 8, 0), (12, 8, 0), (12, 12, 0), (8, 12, 0)]);
        var solid = SolidFactory.Extrude(plate, (0, 0, 5), holes: [hole]);
        var mesh = BRepTessellator.Tessellate(Shelling.Offset(solid, -1));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(0, mesh.EulerCharacteristic); // genus 1 survives the offset
        // 18 x 18 outer, 6 x 6 hole, 3 thick.
        Assert.Equal((18.0 * 18 - 6.0 * 6) * 3, mesh.Volume(), 9);
    }
}
