using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests.Text;

/// <summary>
/// The outline pipeline against a real installed font (see <see cref="SystemFonts"/>):
/// structural assertions only — every letter encloses material, counters come back as
/// holes rather than as extra outlines, and sizes scale quadratically in area.
/// Skips gracefully when the machine has no TrueType font.
/// </summary>
public class SystemFontTextTests
{
    [SkippableFact]
    public void RealFont_EveryLetterAndDigitEnclosesMaterial()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
        {
            var sketches = TextOutlines.GlyphSketches(font, c, 10);
            Assert.NotEmpty(sketches);
            double area = sketches.Sum(s => s.Area());
            Assert.True(area > 0, $"'{c}' encloses {area:g6}, expected a positive area");
        }
    }

    [SkippableFact]
    public void RealFont_CountersComeBackAsHolesNotExtraOutlines()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        // 'O' and '8' are single outlines with one and two counters: if the counters
        // were classified as outlines we would get 2 and 3 sketches, and the area would
        // exceed the outline's own.
        var o = Assert.Single(TextOutlines.GlyphSketches(font, 'O', 10));
        var eight = Assert.Single(TextOutlines.GlyphSketches(font, '8', 10));
        Assert.True(o.Area() < o.Bounds.Size.X * o.Bounds.Size.Y);
        Assert.True(eight.Area() < eight.Bounds.Size.X * eight.Bounds.Size.Y);

        // A colon really is two disjoint outlines.
        Assert.Equal(2, TextOutlines.GlyphSketches(font, ':', 10).Count);
    }

    [SkippableTheory]
    [InlineData('I', 0)]        // a plain bar
    [InlineData('O', 1)]        // one counter
    [InlineData('8', 2)]        // two counters
    public void RealFont_ExtrudedGlyphIsAClosedSolidOfTheRightGenus(char character, int counters)
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);

        var mesh = Shape.Text(character.ToString(), SystemFonts.Font, size: 10, height: 2).ToMesh();

        Assert.True(mesh.IsClosed, $"the extrusion of '{character}' is not closed");
        Assert.True(mesh.Volume() > 0, $"the extrusion of '{character}' has non-positive volume");

        // A prism over a region with n holes is a genus-n surface, so V - E + F = 2 - 2n.
        // This is the decisive "'O' has exactly one hole" assertion: a counter that had
        // been classified as an outline (or dropped) would change the genus.
        int euler = mesh.VertexCount - mesh.EdgeCount + mesh.FaceCount;
        Assert.Equal(2 - 2 * counters, euler);
    }

    [SkippableFact]
    public void RealFont_WordExtrudesToOneClosedSolidPerGlyph()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);

        var shape = Shape.Text("Hi", SystemFonts.Font, size: 10, height: 2);
        var solid = shape.ToBrep();
        solid.Validate();

        // 'H' is one piece, 'i' is a stem plus a tittle: three disjoint shells.
        Assert.Equal(3, solid.Shells.Count);
        var mesh = shape.ToMesh();
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.Volume() > 0);
    }

    [SkippableFact]
    public void RealFont_EmbossedPlateAddsExactlyTheLetteringVolume()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        var plate = Shape.Box(60, 20, 4);                       // top face at z = 2, volume 4800
        var top = SketchPlane.At((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY);
        var style = new TextStyle { Align = TextAlign.Center };
        const double height = 1;

        var embossed = plate | Shape.Text("EC", font, size: 8, height, top, style);
        var mesh = embossed.ToMesh();
        double lettering = TextOutlines.Sketches("EC", font, 8, style).Sum(s => s.Area());

        Assert.True(mesh.IsClosed);
        // The curved 'C' is tessellated, so the meshed volume sits just under the exact
        // sum; a 0.5 % band is far tighter than any classification mistake.
        Assert.Equal(4800 + lettering * height, mesh.Volume(), 0.005 * lettering * height);
    }

    [SkippableFact]
    public void RealFont_AreaScalesWithTheSquareOfTheSize()
    {
        Skip.If(SystemFonts.SkipReason is not null, SystemFonts.SkipReason);
        var font = SystemFonts.Font;

        double small = TextOutlines.GlyphSketches(font, 'B', 4).Sum(s => s.Area());
        double large = TextOutlines.GlyphSketches(font, 'B', 12).Sum(s => s.Area());

        Assert.Equal(9 * small, large, 9);
    }
}
