using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Drafted solids through the tessellator. Every face stays planar, so the mesh volume is
/// the solid's exact volume — these are true analytic ground-truth assertions.
/// </summary>
public class DraftTessellationTests
{
    private const double Ten = Math.PI / 18;

    private static BrepSolid Block() => SolidFactory.MakeBox(new Aabb((-10, -10, 0), (10, 10, 10)));

    private static BrepFace BottomOf(BrepSolid solid) =>
        solid.PlanarFacesWithNormal(-Vector3d.UnitZ).Single();

    [Fact]
    public void Draft_AllSides_HasTheExactFrustumVolume()
    {
        var block = Block();
        var mesh = BRepTessellator.Tessellate(Draft.Apply(block, BottomOf(block), Ten));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        // Square frustum: base 20 x 20, top side 20 - 2 h tan(angle), height 10.
        const double h = 10;
        double topSide = 20 - 2 * h * Math.Tan(Ten);
        double a = 400, b = topSide * topSide;
        Assert.Equal(h / 3 * (a + b + Math.Sqrt(a * b)), mesh.Volume(), 9);
    }

    [Fact]
    public void Draft_OneSide_HasTheExactWedgeVolume()
    {
        var block = Block();
        var mesh = BRepTessellator.Tessellate(Draft.Apply(
            block, BottomOf(block), Ten,
            f => f.IsPlanar(out _, out var n) && n.Dot(Vector3d.UnitX) > 0.99));
        mesh.Validate();
        Assert.True(mesh.IsClosed);

        // x_max(z) = 10 - z tan(angle) over z in [0, 10]; width in y stays 20.
        // V = int 20 (20 - z tan) dz = 4000 - 1000 tan(angle).
        Assert.Equal(4000 - 1000 * Math.Tan(Ten), mesh.Volume(), 9);
    }

    [Fact]
    public void Draft_ZeroAngle_KeepsTheVolume()
    {
        var block = Block();
        var mesh = BRepTessellator.Tessellate(Draft.Apply(block, BottomOf(block), 0));
        mesh.Validate();
        Assert.Equal(4000.0, mesh.Volume(), 9);
    }

    [Fact]
    public void Draft_HexagonalPrism_HasTheExactFrustumVolume()
    {
        var corners = new Vector3d[6];
        for (int i = 0; i < 6; i++)
            corners[i] = (5 * Math.Cos(i * Math.PI / 3), 5 * Math.Sin(i * Math.PI / 3), 0);
        var prism = SolidFactory.Extrude(Profile.FromPoints(corners), (0, 0, 4));
        var mesh = BRepTessellator.Tessellate(Draft.Apply(prism, BottomOf(prism), Ten));
        mesh.Validate();
        Assert.True(mesh.IsClosed);

        // Regular hexagon of circumradius R has area 3 sqrt(3)/2 R^2 and apothem R cos(30).
        // Drafting shrinks the apothem by h tan(angle), so the top circumradius is
        // (apothem - h tan) / cos(30).
        double Area(double circumradius) => 3 * Math.Sqrt(3) / 2 * circumradius * circumradius;
        double apothem = 5 * Math.Cos(Math.PI / 6);
        double top = (apothem - 4 * Math.Tan(Ten)) / Math.Cos(Math.PI / 6);
        double a = Area(5), b = Area(top);
        Assert.Equal(4.0 / 3 * (a + b + Math.Sqrt(a * b)), mesh.Volume(), 9);
    }
}
