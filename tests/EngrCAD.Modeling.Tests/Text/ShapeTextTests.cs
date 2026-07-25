using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling.Text;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// <c>Shape.Text</c> end to end. The synthetic font's glyphs are straight-sided, so
/// every volume is analytic: 'I' is a 200x700 bar and 'O' a 700x700 square with a
/// 300x300 counter, both in font units on a 1000-unit em — at size 10 that is 14 and
/// 40 square units of section.
/// </summary>
public class ShapeTextTests
{
    private static readonly TrueTypeFont Font = TrueTypeFont.Load(SyntheticFont.Build());
    private const double Size = 10;
    private const double BarArea = 200 * 700 * 1e-4;                  // 'I'  = 14
    private const double RingArea = (700 * 700 - 300 * 300) * 1e-4;   // 'O'  = 40

    [Fact]
    public void Text_ExtrudesAGlyphToTheExactVolume()
    {
        var shape = Shape.Text("I", Font, Size, height: 3);

        shape.ToBrep().Validate();
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(BarArea * 3, mesh.Volume(), 9);
    }

    [Fact]
    public void Text_CounterBecomesAHoleThroughTheExtrusion()
    {
        var shape = Shape.Text("O", Font, Size, height: 3);

        shape.ToBrep().Validate();
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(RingArea * 3, mesh.Volume(), 9);                 // the counter really is void
    }

    [Fact]
    public void Text_UnionsTheGlyphsOfAWord()
    {
        var shape = Shape.Text("IO", Font, Size, height: 3);

        var solid = shape.ToBrep();
        solid.Validate();
        Assert.Equal(2, solid.Shells.Count);                          // disjoint glyphs, one shell each
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal((BarArea + RingArea) * 3, mesh.Volume(), 9);
    }

    [Fact]
    public void Text_IsNativeInEveryRepresentation()
    {
        // The payoff of mapping glyph contours onto sketch segments exactly: nothing
        // bridges through a tessellation, in any target.
        var shape = Shape.Text("IOC", Font, Size, height: 2);

        foreach (var target in new[] { TargetRep.Brep, TargetRep.Implicit, TargetRep.Mesh })
        {
            var report = shape.Explain(target);
            Assert.True(report.IsConvertible, report.ToString());
            Assert.All(report.Entries, entry => Assert.Equal(NodeSupport.Native, entry.Support));
        }
    }

    [Fact]
    public void Text_ImplicitLoweringSignsTheStrokeCounterAndOutside()
    {
        // 'O' at size 10: outer square [0,7]^2, counter [2,5]^2, extruded z in [0,3].
        var field = Shape.Text("O", Font, Size, height: 3).ToImplicit();

        Assert.True(field.Evaluate((1, 3.5, 1.5)) < 0, "inside the ring wall");
        Assert.True(field.Evaluate((3.5, 3.5, 1.5)) > 0, "inside the counter");
        Assert.True(field.Evaluate((-1, 3.5, 1.5)) > 0, "outside the glyph");
        Assert.True(field.Evaluate((1, 3.5, 4)) > 0, "above the extrusion");
    }

    [Fact]
    public void Text_PlacesOnASketchPlane()
    {
        // On the YZ plane the extrusion grows along world +X, and the glyph's own y
        // (0..7) runs up world Z.
        var shape = Shape.Text("I", Font, Size, height: 2, SketchPlane.YZ);
        var mesh = shape.ToMesh();

        Assert.True(mesh.IsClosed);
        Assert.Equal(BarArea * 2, mesh.Volume(), 9);
        var bounds = mesh.ComputeBounds();
        Assert.Equal(0, bounds.Min.X, 9);
        Assert.Equal(2, bounds.Max.X, 9);
        Assert.Equal(7, bounds.Max.Z, 9);
    }

    [Fact]
    public void Text_EmbossesOntoAFaceSelectedWithSketchPlaneOn()
    {
        // The documented embossing pattern: sketch on the face, union. No new operation
        // is needed - SketchPlane.On + the existing boolean do the whole job.
        var plate = Shape.Box(40, 20, 4);                             // top face at z = 2
        var top = plate.ToBrep().PlanarFacesWithNormal(Vector3d.UnitZ).First();

        var embossed = plate | Shape.Text("I", Font, Size, height: 1, SketchPlane.On(top));

        embossed.ToBrep().Validate();
        var mesh = embossed.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 20 * 4 + BarArea * 1, mesh.Volume(), 6);
    }

    [Fact]
    public void Text_EngravesThroughTheImplicitRoute()
    {
        // Engraving subtracts a text tool that overshoots the face (the same rule
        // Shape.Drill follows so booleans never see coplanar faces). The subtraction is
        // exact as a signed distance field; the B-Rep route is limited by the boolean
        // engine's handling of sketch-extrusion tools (see the Modeling README).
        var plate = Shape.Box(40, 20, 4);                             // z in [-2, 2]
        var pocket = SketchPlane.At((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var field = (plate - Shape.Text("I", Font, Size, height: 1.5, pocket)).ToImplicit();

        Assert.True(field.Evaluate((10, 0, 0)) < 0, "solid plate away from the lettering");
        Assert.True(field.Evaluate((2, 3.5, 1.5)) > 0, "inside the engraved pocket");
        Assert.True(field.Evaluate((2, 3.5, 0.5)) < 0, "below the 1 mm pocket floor");
    }

    [Fact]
    public void Text_ValidatesItsArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Text("I", Font, Size, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Text("I", Font, Size, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Shape.Text("I", Font, 0, 1));
        Assert.Throws<ArgumentNullException>(() => Shape.Text("I", null!, Size, 1));

        var blank = Assert.Throws<ArgumentException>(() => Shape.Text("  ", Font, Size, 1));
        Assert.Contains("no geometry", blank.Message);
        Assert.Throws<ArgumentException>(() => Shape.Text("", Font, Size, 1));
    }

    [Fact]
    public void Text_HonoursStyleInThreeDimensions()
    {
        var centered = Shape.Text("II", Font, Size, height: 1, style: new TextStyle { Align = TextAlign.Center });
        var bounds = centered.ToMesh().ComputeBounds();

        Assert.Equal(-3, bounds.Min.X, 6);
        Assert.Equal(3, bounds.Max.X, 6);
    }
}
