using System.Text;
using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The PCB fabrication drawing — the drill map/table, the layer stackup, the notes, and the
/// SHARED engineering frame. Verified against closed forms and identities, the ECAD house
/// style: the drill table PARTITIONS the board's holes and vias exactly, the outline is the
/// board's own outline scaled, and the frame is byte-identical to a mechanical drawing's frame.
/// </summary>
public class PcbFabricationDrawingTests
{
    // A two-layer board (50 × 40 × 1.6) with two Ø3.2 mounting holes and one Ø0.6 board via,
    // laid out with three placed vias (two Ø0.3, one Ø0.4). Drilled features by size and plating:
    //   (0.3, PTH) × 2, (0.4, PTH) × 1, (0.6, PTH) × 1, (3.2, NPTH) × 2 — six features, four sizes.
    private static PcbLayout DrilledLayout()
    {
        var board = new PcbBoard(
            [
                new Vector2d(-25, -20), new Vector2d(25, -20),
                new Vector2d(25, 20), new Vector2d(-25, 20),
            ],
            thickness: 1.6,
            holes:
            [
                new BoardHole(new Vector2d(-20, -15), 3.2, BoardHoleKind.Mounting),
                new BoardHole(new Vector2d(20, 15), 3.2, BoardHoleKind.Mounting),
                new BoardHole(new Vector2d(0, 12), 0.6, BoardHoleKind.Via),
            ]);
        var layout = new PcbLayout(new Schematic("drill-demo"), board);
        layout.AddVia("GND", 5, 5, "Top", "Bottom", drill: 0.3, pad: 0.6);
        layout.AddVia("GND", -5, 5, "Top", "Bottom", drill: 0.3, pad: 0.6);
        layout.AddVia("SIG", 0, -5, "Top", "Bottom", drill: 0.4, pad: 0.8);
        return layout;
    }

    private static int FeatureCount(PcbLayout layout) =>
        layout.Board.Holes.Count + layout.PlacedVias().Count;

    // ---- 1. the drill table is a closed-form partition of the board's holes + vias --------

    [Fact]
    public void DrillTable_PartitionsTheBoardsHolesAndViasExactly()
    {
        var layout = DrilledLayout();
        var drawing = new PcbFabricationSheet(layout).Compute();

        // Every drilled feature appears in exactly one row: the row counts sum to the total.
        int total = FeatureCount(layout);
        Assert.Equal(total, drawing.DrillTable.Sum(r => r.Count));

        // Each row's count equals the number of features of that diameter and plating — computed
        // here from the board's own data, independently of the drawing.
        var features = new List<(double Diameter, bool Plated)>();
        foreach (var h in layout.Board.Holes)
            features.Add((h.Diameter, h.Kind == BoardHoleKind.Via));
        foreach (var v in layout.PlacedVias())
            features.Add((v.DrillDiameter, true));
        foreach (var row in drawing.DrillTable)
            Assert.Equal(features.Count(f => f == (row.Diameter, row.Plated)), row.Count);

        // The diameters are the board's own values.
        var boardDiameters = features.Select(f => f.Diameter).ToHashSet();
        Assert.All(drawing.DrillTable, r => Assert.Contains(r.Diameter, boardDiameters));

        // The expected rows, in sorted order: (0.3,PTH)×2, (0.4,PTH)×1, (0.6,PTH)×1, (3.2,NPTH)×2.
        Assert.Equal(4, drawing.DrillTable.Count);
        Assert.Equal((0.3, true, 2), (drawing.DrillTable[0].Diameter, drawing.DrillTable[0].Plated, drawing.DrillTable[0].Count));
        Assert.Equal((0.4, true, 1), (drawing.DrillTable[1].Diameter, drawing.DrillTable[1].Plated, drawing.DrillTable[1].Count));
        Assert.Equal((0.6, true, 1), (drawing.DrillTable[2].Diameter, drawing.DrillTable[2].Plated, drawing.DrillTable[2].Count));
        Assert.Equal((3.2, false, 2), (drawing.DrillTable[3].Diameter, drawing.DrillTable[3].Plated, drawing.DrillTable[3].Count));
    }

    [Fact]
    public void DrillTable_AddingAHoleAddsExactlyOneToItsRowsCount()
    {
        var layout = DrilledLayout();
        var before = new PcbFabricationSheet(layout).Compute();
        int beforeCount = before.DrillTable.First(r => r.Diameter == 3.2 && !r.Plated).Count;

        // Add one more Ø3.2 mounting hole (a new board with the same features plus one).
        var b0 = layout.Board;
        var withHole = new PcbBoard(
            b0.OutlinePoints, b0.Thickness,
            holes: [.. b0.Holes, new BoardHole(new Vector2d(0, -15), 3.2, BoardHoleKind.Mounting)]);
        var layout2 = new PcbLayout(new Schematic("drill-demo"), withHole);
        layout2.AddVia("GND", 5, 5, "Top", "Bottom", 0.3, 0.6);
        layout2.AddVia("GND", -5, 5, "Top", "Bottom", 0.3, 0.6);
        layout2.AddVia("SIG", 0, -5, "Top", "Bottom", 0.4, 0.8);

        var after = new PcbFabricationSheet(layout2).Compute();
        int afterCount = after.DrillTable.First(r => r.Diameter == 3.2 && !r.Plated).Count;

        Assert.Equal(beforeCount + 1, afterCount);
        // No other row moved: the total went up by exactly one and the size list is unchanged.
        Assert.Equal(before.DrillTable.Sum(r => r.Count) + 1, after.DrillTable.Sum(r => r.Count));
        Assert.Equal(before.DrillTable.Count, after.DrillTable.Count);
    }

    // ---- 2. the symbol map: one mark per hole, distinct symbols, deterministic ------------

    [Fact]
    public void DrillMap_OneMarkPerHole_DistinctSymbolsSortedByDiameter()
    {
        var layout = DrilledLayout();
        var drawing = new PcbFabricationSheet(layout).Compute();

        // Exactly one mark per drilled feature.
        Assert.Equal(FeatureCount(layout), drawing.DrillMarks.Count);

        // Each mark sits at its hole's own location, mapped by the drawing's own transform.
        var centres = new List<Vector2d>();
        foreach (var h in layout.Board.Holes)
            centres.Add(h.Center);
        foreach (var v in layout.PlacedVias())
            centres.Add(v.Center);
        foreach (var mark in drawing.DrillMarks)
        {
            Assert.Contains(mark.BoardLocation, centres);
            Assert.Equal(drawing.Project(mark.BoardLocation), mark.SheetLocation);
        }

        // Distinct sizes get distinct symbols; indices run 0..N-1 sorted ascending by diameter.
        var rows = drawing.DrillTable;
        Assert.Equal(rows.Count, rows.Select(r => r.Index).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => r.Symbol).Distinct().Count());
        for (int i = 0; i < rows.Count; i++)
            Assert.Equal(i, rows[i].Index);
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i - 1].Diameter <= rows[i].Diameter);

        // Each mark's symbol matches its size's row.
        var byIndex = rows.ToDictionary(r => r.Index);
        foreach (var mark in drawing.DrillMarks)
        {
            Assert.Equal(byIndex[mark.Index].Symbol, mark.Symbol);
            Assert.Equal(byIndex[mark.Index].Glyph, mark.Glyph);
        }

        // Every drill mark draws line work on the drills layer.
        Assert.Contains(drawing.Segments, s => s.Layer == FabricationLayers.Drills);
    }

    [Fact]
    public void SymbolAssignment_IsDeterministic()
    {
        var a = new PcbFabricationSheet(DrilledLayout()).Compute();
        var b = new PcbFabricationSheet(DrilledLayout()).Compute();
        Assert.Equal(a.DrillTable, b.DrillTable);
        Assert.Equal(a.DrillMarks, b.DrillMarks);
    }

    // ---- 3. the outline is the board's own outline, scaled and placed ---------------------

    [Fact]
    public void Outline_IsTheBoardOutlineScaledAndPlaced()
    {
        var layout = DrilledLayout();
        var drawing = new PcbFabricationSheet(layout, SheetFormat.A3).Compute();
        var board = layout.Board;

        // Same vertices, in the same order, mapped by the drawing's own board-to-sheet transform.
        Assert.Equal(board.OutlinePoints.Count, drawing.Outline.Count);
        for (int i = 0; i < board.OutlinePoints.Count; i++)
            Assert.Equal(drawing.Project(board.OutlinePoints[i]), drawing.Outline[i]);

        // The scale is a standard ISO 5455 ratio, and this board does not fit at 1:1 on A3.
        Assert.Contains(drawing.Scale, DrawingScales.Standard);
        Assert.NotEqual(1, drawing.Scale);
    }

    // ---- 4. THE PAYOFF: the shared frame ---------------------------------------------------

    [Fact]
    public void Frame_IsByteIdenticalToAMechanicalDrawingSheetsFrame()
    {
        var title = new TitleBlock
        {
            Title = "Blinky PCB", DrawingNumber = "PCB-001", Author = "EngrCAD",
            Date = "2026", Revision = "A", Company = "ACME", Material = "FR4", Finish = "ENIG",
        };
        var fab = new PcbFabricationSheet(DrilledLayout(), SheetFormat.A3, title);
        var fabFrame = fab.Frame();
        var mechFrame = new DrawingSheet(SheetFormat.A3) { Title = title }.Frame();

        // By default the two DIFFER — the fab prints its fitted map scale, a mechanical sheet with
        // no views prints 1:1 — so the equality below is not vacuous.
        Assert.NotEqual(mechFrame.Compute().Texts, fabFrame.Compute().Texts);

        // Reconfigure BOTH to identical frame OPTIONS (the fab's own scale, third angle). One
        // function — the shared DrawingFrame — so the geometry matches byte for byte.
        var shared = new EngineeringTitleBlock(DrawingScales.Format(fab.Scale), ProjectionAngle.Third);
        var a = (mechFrame with { Layout = shared }).Compute();
        var b = (fabFrame with { Layout = shared }).Compute();
        Assert.Equal(a.Lines, b.Lines);
        Assert.Equal(a.Texts, b.Texts);

        // And the fab drawing's own furniture IS exactly its frame's, in the frame's order.
        var drawing = fab.Compute();
        var frame = fabFrame.Compute();
        var furniture = drawing.Segments
            .Where(s => s.Layer is SheetLayers.Border or SheetLayers.TitleBlock)
            .Select(s => (s.A, s.B, s.Layer)).ToList();
        Assert.Equal(frame.Lines, furniture);
        var furnitureTexts = drawing.Texts.Where(t => t.Layer == SheetLayers.TitleBlock).ToList();
        Assert.Equal(frame.Texts, furnitureTexts);
    }

    // ---- 5. the stackup table -------------------------------------------------------------

    [Fact]
    public void StackupTable_MatchesTheBoardsPhysicalStackup()
    {
        var stackup = LayerStackup.FourLayer(copper: 0.035, prepreg: 0.2, core: 1.13);
        var board = new PcbBoard(
            [
                new Vector2d(-20, -15), new Vector2d(20, -15),
                new Vector2d(20, 15), new Vector2d(-20, 15),
            ],
            stackup);
        var layout = new PcbLayout(new Schematic("stack-demo"), board);
        var drawing = new PcbFabricationSheet(layout).Compute();

        Assert.Equal(stackup.Layers.Count, drawing.StackupTable.Count);
        for (int i = 0; i < stackup.Layers.Count; i++)
        {
            Assert.Equal(stackup.Layers[i].Name, drawing.StackupTable[i].Name);
            Assert.Equal(stackup.Layers[i].Thickness, drawing.StackupTable[i].Thickness);
            Assert.Equal(
                stackup.Layers[i].Kind == StackLayerKind.Copper ? "COPPER" : "DIELECTRIC",
                drawing.StackupTable[i].Type);
        }
        // The copper foil thickness (the closest to a copper weight the board states) is a note.
        Assert.Contains(drawing.Notes, n => n.Contains("COPPER FOIL"));
    }

    [Fact]
    public void CopperOnlyBoard_HasNoStackupTable_AndStatesTheLayerCount()
    {
        var layout = PcbFixtures.Layout();   // a copper-only two-layer board
        Assert.Null(layout.Board.LayerStackup);
        var drawing = new PcbFabricationSheet(layout).Compute();

        Assert.Empty(drawing.StackupTable);
        Assert.Contains(drawing.Notes, n => n.Contains("COPPER") && n.Contains("LAYERS"));
    }

    // ---- 6. the writers are consistent and deterministic ----------------------------------

    [Fact]
    public void Writers_EmitAndAreDeterministic()
    {
        var sheet = new PcbFabricationSheet(DrilledLayout());
        var drawing = sheet.Compute();

        string svg = drawing.ToSvg();
        Assert.Contains("<svg", svg);
        Assert.Contains("path", svg);

        var dxf = drawing.ToDxf();
        Assert.NotEmpty(dxf.Entities);

        byte[] pdf = drawing.ToPdf();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));

        // Two emissions are byte-identical: the drawing is a function of the layout and nothing
        // else (the PDF writer is a byte fixed point by design).
        Assert.Equal(svg, sheet.Compute().ToSvg());
        Assert.Equal(pdf, sheet.Compute().ToPdf());
        Assert.Equal(DxfText(dxf), DxfText(sheet.Compute().ToDxf()));
    }

    private static string DxfText(DxfDocument dxf)
    {
        using var writer = new StringWriter();
        dxf.Save(writer);
        return writer.ToString();
    }

    // ---- 7. empty / edge ------------------------------------------------------------------

    [Fact]
    public void BoardWithNoVias_StillTablesItsBoardHoles()
    {
        var layout = PcbFixtures.Layout();   // two Ø3 mounting holes, no vias
        Assert.Empty(layout.PlacedVias());
        var drawing = new PcbFabricationSheet(layout).Compute();

        Assert.Single(drawing.DrillTable);
        Assert.Equal(2, drawing.DrillTable[0].Count);
        Assert.False(drawing.DrillTable[0].Plated);   // mounting holes are non-plated
        Assert.Equal(3.0, drawing.DrillTable[0].Diameter);
        Assert.Equal(2, drawing.DrillMarks.Count);
    }

    [Fact]
    public void BoardWithNoDrilledFeatures_HasAnEmptyDrillTableAndStillDraws()
    {
        var board = new PcbBoard(
            [
                new Vector2d(-15, -10), new Vector2d(15, -10),
                new Vector2d(15, 10), new Vector2d(-15, 10),
            ],
            thickness: 1.6);
        var layout = new PcbLayout(new Schematic("bare"), board);
        var drawing = new PcbFabricationSheet(layout).Compute();

        Assert.Empty(drawing.DrillTable);
        Assert.Empty(drawing.DrillMarks);
        // The outline and the frame still draw, and the writers still produce a valid document.
        Assert.NotEmpty(drawing.Outline);
        Assert.Contains("<svg", drawing.ToSvg());
    }

    [Fact]
    public void Notes_AreWriteOnlyWhenStated()
    {
        var layout = PcbFixtures.Layout();   // states no material/finish/mask/silk
        var notes = new PcbFabricationSheet(layout).Compute().Notes;

        Assert.Contains(notes, n => n.Contains("MILLIMETRES"));
        Assert.Contains(notes, n => n.Contains("FINISHED BOARD THICKNESS"));
        // The board states no material, surface finish, copper foil or mask, so none is invented.
        Assert.DoesNotContain(notes, n => n.Contains("MATERIAL"));
        Assert.DoesNotContain(notes, n => n.Contains("SURFACE FINISH"));
        Assert.DoesNotContain(notes, n => n.Contains("COPPER FOIL"));
        Assert.DoesNotContain(notes, n => n.Contains("SOLDERMASK"));
    }
}
