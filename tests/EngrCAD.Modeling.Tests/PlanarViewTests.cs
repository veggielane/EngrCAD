using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The 2D views a <see cref="Shape"/> can produce: <see cref="Shape.Section"/>
/// (<c>projection(cut = true)</c>). Curved geometry sections as INSCRIBED polygons, so
/// curved assertions bracket the analytic value from below by the discretization.
/// </summary>
public class PlanarViewTests
{
    private static SketchPlane PlaneAtZ(double z) =>
        new(Frame3d.FromOrthonormal((0, 0, z), Vector3d.UnitX, Vector3d.UnitY));

    private static double TotalArea(IReadOnlyList<Region2d> regions) => regions.Sum(r => r.Area);

    [Fact]
    public void SectionOfABox_IsTheExactRectangle()
    {
        var section = Assert.Single(Shape.Box(10, 6, 4).Section(PlaneAtZ(1)));

        Assert.Equal(60.0, section.Area, 9);
        Assert.Equal(4, section.Outer.Count);
    }

    [Fact]
    public void SectionOfADrilledPlate_HasTheBoreAsAHole()
    {
        const double tolerance = 1e-3;
        const double bore = 3.3;
        var plate = Shape.Box(40, 20, 6)
            .Drill(HoleSpec.Simple(2 * bore), [new Vector2d(-10, 0), new Vector2d(10, 0)], 20,
                new SketchPlane(Frame3d.FromOrthonormal((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY)));

        var section = Assert.Single(plate.Section(PlaneAtZ(0), tolerance));

        Assert.Equal(2, section.Holes.Count);
        double exact = 40 * 20 - 2 * Math.PI * bore * bore;
        Assert.InRange(section.Area, exact, exact + 2 * 2 * Math.PI * bore * tolerance);
    }

    [Fact]
    public void SectionOfANonBRepShape_FallsBackToTheMesh()
    {
        // A smooth blend has no B-Rep form at all, so the section comes from the mesh --
        // still a sensible closed region, just at the mesh's fidelity.
        var blob = Shape.Sphere(5).SmoothUnion(Shape.Sphere(5).Translate((6, 0, 0)), 2);
        Assert.False(blob.CanConvertTo(TargetRep.Brep));

        var section = blob.Section(PlaneAtZ(0));

        Assert.NotEmpty(section);
        Assert.True(TotalArea(section) > Math.PI * 25, "the blend covers more than one sphere's great circle");
    }

    [Fact]
    public void SectionUsesThePlanesOwnAxes()
    {
        // Sectioning a 10x6x4 box on the YZ plane gives the box's 6 x 4 cross-section,
        // measured in that plane's X (world Y) and Y (world Z).
        var plane = new SketchPlane(Frame3d.FromOrthonormal((0.5, 0, 0), Vector3d.UnitY, Vector3d.UnitZ));

        var section = Assert.Single(Shape.Box(10, 6, 4).Section(plane));

        Assert.Equal(24.0, section.Area, 9);
        Assert.Equal(-3.0, section.Bounds.Min.X, 9);
        Assert.Equal(2.0, section.Bounds.Max.Y, 9);
    }
}
