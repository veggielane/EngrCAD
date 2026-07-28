using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <c>Shape.RoundEdges(radius)</c> — the Shape-graph wiring of
/// <see cref="Filleting.FilletAllEdges"/> (the exact morphological opening). Kernel
/// behaviour (Steiner volume convergence, refusals) is locked in the BRep tests; here
/// the wiring is pinned: face census, transform baking, honest Explain, mesh route.
/// </summary>
public class RoundEdgesShapeTests
{
    private const double X = 12, Y = 8, H = 6, R = 1.5;

    /// <summary>The rounded box exactly: inner box + 6 slabs + 12 quarter cylinders +
    /// 8 sphere octants (equivalently Steiner's formula for the eroded box).</summary>
    private static double RoundedBoxVolume(double x, double y, double z, double r)
    {
        double ix = x - 2 * r, iy = y - 2 * r, iz = z - 2 * r;
        return ix * iy * iz
            + 2 * r * (ix * iy + iy * iz + ix * iz)
            + Math.PI * r * r * (ix + iy + iz)
            + 4.0 / 3 * Math.PI * r * r * r;
    }

    [Fact]
    public void RoundEdges_Box_Gives26FacesAndTheSteinerVolume()
    {
        var rounded = Shape.Box(X, Y, H).RoundEdges(R);

        var brep = rounded.ToBrep();
        brep.Validate();
        Assert.Equal(26, brep.Faces.Count());

        var mesh = BRepTessellator.Tessellate(brep);
        Assert.True(mesh.IsClosed);
        // Inscribed tessellation converges quadratically from below; at the default 32
        // segments per circle the deficit is well under 1% of the curved terms.
        double exact = RoundedBoxVolume(X, Y, H, R);
        Assert.InRange(mesh.Volume(), exact * 0.99, exact + 1e-9);

        Assert.True(rounded.ToMesh().IsClosed);
    }

    [Fact]
    public void RoundEdges_RadiusScalesWithUniformScale()
    {
        // RoundEdges(R) then Scale(2) must round with an EFFECTIVE radius 2R: compare
        // against Box(2X…).RoundEdges(2R) built directly.
        var scaled = BRepTessellator.Tessellate(Shape.Box(X, Y, H).RoundEdges(R).Scale(2).ToBrep());
        var direct = BRepTessellator.Tessellate(Shape.Box(2 * X, 2 * Y, 2 * H).RoundEdges(2 * R).ToBrep());
        Assert.Equal(direct.Volume(), scaled.Volume(), 6);
    }

    [Fact]
    public void RoundEdges_ExplainIsHonest()
    {
        var rounded = Shape.Box(X, Y, H).RoundEdges(R);

        var brep = rounded.Explain(TargetRep.Brep);
        Assert.True(brep.IsConvertible);
        Assert.Contains(brep.Entries,
            e => e.Node.StartsWith("RoundEdges(", StringComparison.Ordinal) && e.Support == NodeSupport.Native);

        var implicitReport = rounded.Explain(TargetRep.Implicit);
        Assert.True(implicitReport.IsConvertible);
        Assert.Equal(NodeSupport.Bridged, implicitReport.Entries[^1].Support);

        var sheared = rounded.Transform(Matrix4d.CreateScale(new Vector3d(2, 1, 1)));
        Assert.False(sheared.CanConvertTo(TargetRep.Brep));
        Assert.True(sheared.ToMesh().IsClosed);
    }

    [Fact]
    public void RoundEdges_ConcaveEdges_AreRefusedByName()
    {
        // An L-shaped extrusion has a concave edge; the opening cannot round it.
        var ell = Shape.Extrude(
            Sketch.Polygon([new(0, 0), new(8, 0), new(8, 3), new(3, 3), new(3, 8), new(0, 8)]), 4);
        Assert.Throws<NotSupportedException>(() => ell.RoundEdges(1).ToBrep());
    }

    [Fact]
    public void RoundEdges_InvalidRadius_FailsAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(X, Y, H).RoundEdges(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Box(X, Y, H).RoundEdges(-1));
    }

    [Fact]
    public void RoundEdges_AppearsInTheConstructionTreeWithItsChild()
    {
        var tree = ConstructionTree.FromShape(Shape.Box(X, Y, H).RoundEdges(R));
        Assert.StartsWith("RoundEdges(", tree.Label, StringComparison.Ordinal);
        var child = Assert.Single(tree.Children);
        Assert.StartsWith("Box(", child.Label, StringComparison.Ordinal);
    }
}
