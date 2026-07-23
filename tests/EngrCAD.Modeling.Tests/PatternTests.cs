using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class PatternTests
{
    [Fact]
    public void LinearPattern_OfBossesOnAPlate_ExactVolume()
    {
        // Three overlapping-with-plate bosses: each union intersects the plate.
        var boss = Shape.Box(0.6, 0.6, 0.8).Translate(-1, 0, 0.5);
        var shape = Shape.Box(4, 2, 1) | boss.PatternLinear(3, (1, 0, 0));
        var solid = shape.ToBrep();
        solid.Validate();
        var mesh = BRepTessellator.Tessellate(solid);
        Assert.True(mesh.IsClosed);
        double exact = 4 * 2 * 1 + 3 * (0.6 * 0.6 * 0.4); // boss halves poke above z = 0.5
        Assert.True(Math.Abs(mesh.Volume() - exact) < 1e-9, $"volume {mesh.Volume()} vs {exact}");
    }

    [Fact]
    public void DisjointPattern_MultiShellSolid()
    {
        var shape = Shape.Box(1, 1, 1).PatternLinear(4, (2, 0, 0));
        var solid = shape.ToBrep();
        solid.Validate();
        Assert.Equal(4, solid.Shells.Count);
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        var mesh = BRepTessellator.Tessellate(solid);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - 4) < 1e-9);
    }

    [Fact]
    public void CircularPattern_AboutZ()
    {
        var shape = Shape.Box(0.5, 0.5, 1).Translate(2, 0, 0).PatternCircular(6, Vector3d.Zero, Vector3d.UnitZ);
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - 6 * 0.25) < 1e-9, $"volume {mesh.Volume()}");
    }

    [Fact]
    public void NestedDifference_SwallowedToolMakesCavity()
    {
        // A tool entirely inside the body: the disjoint fast path must produce a
        // cavity (reversed inner shell), not ignore the tool.
        var shape = Shape.Box(4, 4, 4) - Shape.Box(1, 1, 1);
        var solid = shape.ToBrep();
        solid.Validate();
        Assert.Equal(2, solid.Shells.Count);
        var mesh = BRepTessellator.Tessellate(solid);
        Assert.True(mesh.IsClosed);
        Assert.True(Math.Abs(mesh.Volume() - 63) < 1e-9, $"volume {mesh.Volume()}");
    }
}
