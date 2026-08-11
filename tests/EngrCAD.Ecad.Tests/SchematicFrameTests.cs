using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The schematic sheet consumes the SAME shared <see cref="DrawingFrame"/> as the mechanical
/// drawing sheet. These lock the two oracles from the ECAD side: the schematic's furniture is
/// exactly its frame's output (additive extraction), and a mechanical frame and a schematic
/// frame given the same options are byte-identical (one function, so they cannot disagree).
/// </summary>
public class SchematicFrameTests
{
    private static PartDefinition Passive(string name, string prefix) =>
        new(name, prefix, [new Pin("1", "", PinType.Passive), new Pin("2", "", PinType.Passive)],
            symbol: new Symbol(name,
            [
                new SymbolPin("1", "", new Vector2d(0, 5.08), SymbolPinDirection.Down, 2.54, PinType.Passive),
                new SymbolPin("2", "", new Vector2d(0, -5.08), SymbolPinDirection.Up, 2.54, PinType.Passive),
            ], [new SymbolRectangle(new Vector2d(-1, -2.54), new Vector2d(1, 2.54))]));

    private static SchematicSheet Sheet(TitleBlock? title = null, FrameStandards? standards = null)
    {
        var sch = new Schematic("LED indicator");
        var r = sch.Add("R1", Passive("R_0805", "R"), value: "330");
        var d = sch.Add("D1", Passive("LED_3MM", "D"));
        sch.Connect("LED_A", r.Pin("2"), d.Pin("1"));
        var placement = new SchematicPlacement()
            .Place("R1", new Vector2d(50, 60)).Place("D1", new Vector2d(120, 60));
        return new SchematicSheet(sch, placement, format: SheetFormat.Custom("A6", 190, 120),
            title: title, standards: standards);
    }

    /// <summary>The drawn schematic's border and title-block line work is exactly the frame's,
    /// in the frame's order — so the schematic and a mechanical drawing of one project cannot
    /// draw different furniture.</summary>
    [Fact]
    public void SchematicFurnitureIsExactlyTheFrame()
    {
        var title = new TitleBlock
        {
            Title = "LED indicator", DrawingNumber = "EX-001", Author = "EngrCAD",
            Revision = "A", Company = "ACME",
        };
        var sheet = Sheet(title);
        var drawing = sheet.Draw();
        var frame = sheet.Frame().Compute();

        var furnitureLines = drawing.Segments
            .Where(s => s.Layer is SchematicLayers.Border or SchematicLayers.TitleBlock)
            .Select(s => (s.A, s.B, s.Layer)).ToList();
        Assert.Equal(frame.Lines, furnitureLines);

        var furnitureTexts = drawing.Texts.Where(t => t.Layer == SchematicLayers.TitleBlock).ToList();
        Assert.Equal(frame.Texts, furnitureTexts);

        // A schematic block has no scale or projection angle (it is not a scaled projection).
        Assert.DoesNotContain(frame.Texts, t => t.Text.StartsWith("SCALE ") || t.Text.EndsWith("ANGLE"));
        Assert.Contains(frame.Texts, t => t.Text == "LED indicator");
    }

    /// <summary>THE cross-sheet oracle: a mechanical sheet's frame and a schematic sheet's
    /// frame, reconfigured to the SAME options (paper, fields, layout, layers), produce
    /// byte-identical border and title-block geometry — one function, so the two sheets cannot
    /// disagree.</summary>
    [Fact]
    public void MechanicalAndSchematicFramesAgreeUnderTheSameOptions()
    {
        var title = new TitleBlock
        {
            Title = "P", DrawingNumber = "1", Author = "a", Date = "d", Revision = "A", Company = "Co",
        };
        var mechFrame = new DrawingSheet(SheetFormat.A4) { Title = title }.Frame();   // engineering, SheetLayers
        var schFrame = Sheet(title).Frame();                                          // schematic, SchematicLayers

        // By default the two DIFFER — different layout and layers — so the equality below is not
        // vacuous.
        Assert.NotEqual(mechFrame.Compute().Texts, schFrame.Compute().Texts);

        // Reconfigure BOTH to identical frame OPTIONS. One function, so the geometry matches.
        var layout = new SchematicTitleBlock(2.0);
        var a = (mechFrame with
        {
            Format = SheetFormat.A4, Title = title, Layout = layout,
            BorderLayer = "border", TitleBlockLayer = "titleblock",
        }).Compute();
        var b = (schFrame with
        {
            Format = SheetFormat.A4, Title = title, Layout = layout,
            BorderLayer = "border", TitleBlockLayer = "titleblock",
        }).Compute();

        Assert.Equal(a.Lines, b.Lines);
        Assert.Equal(a.Texts, b.Texts);
    }

    /// <summary>The frame's opt-in standards reach the schematic sheet too, and off by default
    /// they change nothing — the schematic's own byte-fixed-point.</summary>
    [Fact]
    public void SchematicStandardsAreOptInAndOffByDefaultChangesNothing()
    {
        var plain = Sheet().Draw().ToSvg();
        var alsoNone = Sheet(standards: FrameStandards.None).Draw().ToSvg();
        Assert.Equal(plain, alsoNone);

        var zoned = Sheet(standards: FrameStandards.Iso5457).Draw();
        // The zone grid added border-layer texts the plain sheet has none of.
        Assert.DoesNotContain(Sheet().Draw().Texts, t => t.Layer == SchematicLayers.Border);
        Assert.Contains(zoned.Texts, t => t.Layer == SchematicLayers.Border);
    }
}
