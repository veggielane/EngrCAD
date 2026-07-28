using System.Xml.Linq;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="SvgDrawing"/>: line-class presets (visible solid / hidden dashed /
/// section dash-dot) as groups, exact sketch paths (A for arcs, C for cubics), y-flip
/// via one root transform, viewBox sized from content.
/// </summary>
public class SvgDrawingTests
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    [Fact]
    public void LineClasses_BecomeStyledGroups()
    {
        var drawing = new SvgDrawing();
        var section = Shape.Cylinder(5, 10).Section(SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
        var outline = Shape.Box(30, 20, 10).Silhouette(SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
        drawing.Add(outline, SvgLineClass.Visible, layer: "outline");
        drawing.Add(section, SvgLineClass.Section, layer: "cut");

        var svg = XDocument.Parse(drawing.ToSvg());
        var groups = svg.Descendants(Ns + "g").Where(g => g.Attribute("class") is not null).ToList();
        Assert.Equal(2, groups.Count);

        var visible = groups.Single(g => g.Attribute("class")!.Value == "visible");
        Assert.Equal("outline", visible.Attribute("id")!.Value);
        Assert.Null(visible.Attribute("stroke-dasharray"));

        var cut = groups.Single(g => g.Attribute("class")!.Value == "section");
        Assert.NotNull(cut.Attribute("stroke-dasharray"));   // dash-dot
        Assert.NotEmpty(cut.Elements(Ns + "path"));
    }

    [Fact]
    public void HiddenClass_IsDashedAndThin()
    {
        var drawing = new SvgDrawing();
        drawing.Add(Sketch.Rectangle(10, 10), SvgLineClass.Hidden);
        var svg = XDocument.Parse(drawing.ToSvg());
        var group = svg.Descendants(Ns + "g").Single(g => g.Attribute("class")?.Value == "hidden");
        Assert.NotNull(group.Attribute("stroke-dasharray"));
        Assert.True(double.Parse(group.Attribute("stroke-width")!.Value,
            System.Globalization.CultureInfo.InvariantCulture) < 0.5);
    }

    [Fact]
    public void SketchArcs_AreWrittenAsArcCommands()
    {
        var drawing = new SvgDrawing();
        drawing.Add(Sketch.Slot(20, 8));   // two lines + two half-circle arcs
        var svg = XDocument.Parse(drawing.ToSvg());
        string d = svg.Descendants(Ns + "path").Single().Attribute("d")!.Value;
        Assert.Contains("A", d);
        Assert.DoesNotContain("NaN", d);
    }

    [Fact]
    public void FullCircle_SplitsIntoArcsAndCloses()
    {
        var drawing = new SvgDrawing();
        drawing.Add(Sketch.Circle(5));
        string d = XDocument.Parse(drawing.ToSvg()).Descendants(Ns + "path").Single().Attribute("d")!.Value;
        // A full turn cannot be one A command (coincident endpoints): expect several.
        Assert.True(d.Split('A').Length - 1 >= 2, d);
        Assert.Contains("Z", d);
    }

    [Fact]
    public void ViewBox_CoversContentWithMarginAndFlipsY()
    {
        var drawing = new SvgDrawing { Margin = 2 };
        drawing.Add(Sketch.Rectangle(10, 6));   // centered: x,y in [-5,5]x[-3,3]
        var svg = XDocument.Parse(drawing.ToSvg());
        var parts = svg.Root!.Attribute("viewBox")!.Value
            .Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(-7, parts[0], 12);   // minX - margin
        Assert.Equal(-5, parts[1], 12);   // -(maxY + margin)
        Assert.Equal(14, parts[2], 12);
        Assert.Equal(10, parts[3], 12);
        Assert.Contains("scale(1,-1)",
            svg.Root.Element(Ns + "g")!.Attribute("transform")!.Value);
    }

    [Fact]
    public void EmptyDrawing_RefusesToSave()
    {
        Assert.Throws<InvalidOperationException>(() => new SvgDrawing().ToSvg());
    }
}
