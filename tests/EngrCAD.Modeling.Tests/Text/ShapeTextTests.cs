using EngrCAD.BRep;
using EngrCAD.Core;
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
        // is needed - SketchPlane.On + the existing boolean do the whole job. Lettering
        // sitting FLUSH on the face is a coplanar pair, which the v1 boolean does not
        // fuse: it takes the disjoint fast path and the result is the plate and the
        // glyph as two touching shells. Closed, valid and exactly the right volume, but
        // see Text_EmbossesAsOneShellWhenSunkIntoTheFace for a fused union.
        var plate = Shape.Box(40, 20, 4);                             // top face at z = 2
        var top = plate.ToBrep().PlanarFacesWithNormal(Vector3d.UnitZ).First();

        var embossed = plate | Shape.Text("I", Font, Size, height: 1, SketchPlane.On(top));

        var solid = embossed.ToBrep();
        solid.Validate();
        Assert.Equal(2, solid.Shells.Count);                          // touching, not fused
        var mesh = embossed.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 20 * 4 + BarArea * 1, mesh.Volume(), 6);
    }

    [Fact]
    public void Text_EmbossesAsOneShellWhenSunkIntoTheFace()
    {
        // Sink the lettering a fraction into the face and the pair is transversal, so
        // the boolean really fuses: ONE shell, and the raised part is still exactly
        // 1 mm of glyph section above the plate.
        var plate = Shape.Box(40, 20, 4);                             // top face at z = 2
        var sunk = SketchPlane.At((0, 0, 1.9), Vector3d.UnitX, Vector3d.UnitY);

        var embossed = plate | Shape.Text("I", Font, Size, height: 1.1, sunk);

        var solid = embossed.ToBrep();
        solid.Validate();
        Assert.Single(solid.Shells);
        Assert.True(solid.SatisfiesEulerFormula());
        var mesh = embossed.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 20 * 4 + BarArea * 1, mesh.Volume(), 6);
    }

    [Fact]
    public void Text_EngravesNativelyInBRep()
    {
        // Engraving subtracts a text tool that overshoots the face (the same rule
        // Shape.Drill follows so booleans never see coplanar faces). This used to be
        // silently WRONG: with no exact plane∩sketch-extrusion intersection the boolean
        // found no curves at all, took the disjoint fast path, and buried the whole tool
        // as an internal CAVITY — a closed, Validate-clean solid with the wrong volume,
        // which no manifold check can catch. Only the analytic volume pins it.
        var plate = Shape.Box(40, 20, 4);                             // z in [-2, 2]
        var pocket = SketchPlane.At((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var engraved = plate - Shape.Text("I", Font, Size, height: 1.5, pocket);

        var solid = engraved.ToBrep();
        solid.Validate();
        Assert.Single(solid.Shells);                                  // a pocket, not a cavity
        Assert.True(solid.SatisfiesEulerFormula());
        var mesh = engraved.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 20 * 4 - BarArea * 1, mesh.Volume(), 9);    // exactly 1 mm deep
    }

    [Fact]
    public void Text_EngravesAWholeWordWithCounters()
    {
        // Several glyphs in one boolean, one of them with a counter (the island inside
        // 'O' has to survive as standing material). Straight-sided synthetic glyphs, so
        // the volume is exact — no chordal allowance.
        var plate = Shape.Box(40, 20, 4);
        var pocket = SketchPlane.At((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var engraved = plate - Shape.Text("IOI", Font, Size, height: 1.5, pocket);

        var solid = engraved.ToBrep();
        solid.Validate();
        var mesh = engraved.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.Equal(40 * 20 * 4 - (BarArea * 2 + RingArea) * 1, mesh.Volume(), 9);
    }

    [Fact]
    public void Text_EngravesThroughTheImplicitRoute()
    {
        // The B-Rep route is exact (above); the implicit route is the fallback for the
        // configurations it still refuses (coplanar tools, lettering running off an
        // edge) and stays exact as a field.
        var plate = Shape.Box(40, 20, 4);                             // z in [-2, 2]
        var pocket = SketchPlane.At((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var field = (plate - Shape.Text("I", Font, Size, height: 1.5, pocket)).ToImplicit();

        Assert.True(field.Evaluate((10, 0, 0)) < 0, "solid plate away from the lettering");
        Assert.True(field.Evaluate((2, 3.5, 1.5)) > 0, "inside the engraved pocket");
        Assert.True(field.Evaluate((2, 3.5, 0.5)) < 0, "below the 1 mm pocket floor");
    }

    [Fact]
    public void Text_EngravedBodyPolygonizesToAClosedSolid()
    {
        // The implicit fallback, locked down: lower the engraved body to its exact field
        // and polygonize THAT.
        var plate = Shape.Box(40, 20, 4);
        var pocket = SketchPlane.At((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var engraved = plate - Shape.Text("IO", Font, Size, height: 1.5, pocket);

        var mesh = Shape.From(engraved.ToImplicit()).ToMesh(new MeshQuality { SdfResolution = 96 });

        Assert.True(mesh.IsClosed);
        // 1 mm deep over both glyph sections; Surface Nets rounds the corners, so a
        // +/-20 % band on the removed volume is the honest expectation.
        double removed = (BarArea + RingArea) * 1;
        Assert.InRange(mesh.Volume(), 40 * 20 * 4 - removed * 1.2, 40 * 20 * 4 - removed * 0.8);
    }

    [Fact]
    public void Text_LongerWordsStayValidOnTheirOwn()
    {
        // Whole words are fine as standalone geometry: one shell per closed contour.
        var shape = Shape.Text("IOCA IOC", Font, Size, height: 2);

        var solid = shape.ToBrep();
        solid.Validate();
        Assert.Equal(8, solid.Shells.Count);          // 7 letters, and 'A' is two pieces
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.Volume() > 0);
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
