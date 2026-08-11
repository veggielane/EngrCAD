using System.Text;
using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The per-layer fabrication PLOTS — a sheet per copper / solder-mask / silkscreen / solder-paste
/// layer, each on the SHARED engineering frame. Verified the ECAD house way: the plot set is one
/// plot per present layer, each plot's drawn geometry IS its layer's own copper-model / mask / silk /
/// paste regions (not a re-derivation), the frame is byte-identical to a fab drawing's, and a
/// bottom-side layer is mirrored.
/// </summary>
public class PcbLayerPlotTests
{
    // A two-layer board with R1 (SMD, top) and J1 (a through-hole header — copper on BOTH layers),
    // with the mask, silk and paste layers declared so all four kinds plot.
    private static PcbLayout FullLayout() =>
        PcbFixtures.Layout()
            .WithMask(new PcbMaskSettings(Expansion: 0.05))
            .WithSilkscreen(new PcbSilkscreenSettings(ShowValues: true))
            .WithPaste(PcbPasteSettings.Default);

    // ---- 1. one plot per layer, each naming its layer + side -------------------------------

    [Fact]
    public void PlotSet_IsOnePlotPerLayer_EachNamingItsLayerAndSide()
    {
        var layout = FullLayout();
        var plots = PcbFabricationPlots.For(layout);
        var model = PcbCopperModel.FromLayout(layout);
        string top = model.Board.Stackup.Top.Name, bottom = model.Board.Stackup.Bottom.Name;

        // Copper Top/Bottom, then mask Top/Bottom, silk Top/Bottom, paste Top/Bottom — eight plots.
        Assert.Equal(8, plots.Count);

        // Copper plots: one per copper layer, in stackup order.
        var copper = plots.Where(p => p.Kind == PcbPlotLayerKind.Copper).ToList();
        Assert.Equal(model.Layers.Count, copper.Count);
        Assert.Equal(model.Layers, copper.Select(p => p.LayerName));

        // Each declared layer kind has its two outer sides.
        foreach (var kind in new[]
                 { PcbPlotLayerKind.SolderMask, PcbPlotLayerKind.Silkscreen, PcbPlotLayerKind.SolderPaste })
        {
            var sides = plots.Where(p => p.Kind == kind).ToList();
            Assert.Equal(2, sides.Count);
            Assert.Contains(sides, p => p.ViewSide == CopperSide.Top && p.LayerName == top);
            Assert.Contains(sides, p => p.ViewSide == CopperSide.Bottom && p.LayerName == bottom);
        }

        // Every plot names its layer and side, and the bottom-side ones are mirrored.
        foreach (var p in plots)
        {
            Assert.False(string.IsNullOrEmpty(p.LayerName));
            Assert.Equal(p.ViewSide == CopperSide.Bottom, p.Mirrored);
            // The label states the layer + kind; the view note states the mirror convention.
            Assert.Contains(PcbLayerPlot.KindLabel(p.Kind), p.LabelText());
            Assert.Contains(p.LayerName.ToUpperInvariant(), p.LabelText());
            Assert.Contains(p.Mirrored ? "MIRRORED" : "TOP", p.ViewNote());
        }
    }

    [Fact]
    public void CopperOnlyBoard_PlotsOnlyItsCopperLayers()
    {
        // No mask/silk/paste declared → just the copper layers (the write-only-when-stated rule).
        var layout = PcbFixtures.Layout();
        Assert.Null(layout.MaskSettings);
        var plots = PcbFabricationPlots.For(layout);

        Assert.Equal(PcbCopperModel.FromLayout(layout).Layers.Count, plots.Count);
        Assert.All(plots, p => Assert.Equal(PcbPlotLayerKind.Copper, p.Kind));
    }

    // ---- 2. the drawn geometry IS the layer's own geometry, not a re-derivation ------------

    [Fact]
    public void CopperPlot_DrawsExactlyTheCopperModelsRegionsForThatLayer()
    {
        var layout = FullLayout();
        var model = PcbCopperModel.FromLayout(layout);

        foreach (var plot in PcbFabricationPlots.For(layout).Where(p => p.Kind == PcbPlotLayerKind.Copper))
        {
            var drawing = plot.Compute();
            // The copper model's OWN features on this layer, counted independently of the plot.
            var layerFeatures = model.Copper.Where(f => f.Layer == plot.LayerName).ToList();
            Assert.NotEmpty(layerFeatures);   // J1's THT pads put copper on both layers

            // The plot draws exactly those regions — same count, same total area (the region's own
            // value). A plot showing more or fewer regions than its layer carries is the bug.
            Assert.Equal(layerFeatures.Count, drawing.Regions.Count);
            double modelArea = layerFeatures.Sum(f => f.Region.Area);
            Assert.Equal(modelArea, drawing.PlottedArea, 9);

            // And the copper is DRAWN — every copper region contributes line-work on the copper layer.
            Assert.Contains(drawing.Segments, s => s.Layer == FabricationPlotLayers.Copper);
        }
    }

    [Fact]
    public void MaskPlot_DrawsExactlyTheMaskOpenings_PastePlot_ExactlyTheApertures()
    {
        var layout = FullLayout();
        var model = PcbCopperModel.FromLayout(layout);

        // Mask: the plot's regions ARE the mask windows for that side.
        var mask = PcbMask.For(model, layout.MaskSettings!);
        foreach (var plot in PcbFabricationPlots.For(layout).Where(p => p.Kind == PcbPlotLayerKind.SolderMask))
        {
            var drawing = plot.Compute();
            var content = mask.Layers.Single(m => m.Layer == plot.LayerName);
            Assert.Equal(content.Openings.Count, drawing.Regions.Count);
            Assert.Equal(content.Openings.Sum(o => o.Region.Area), drawing.PlottedArea, 9);
        }

        // Paste: the plot's regions ARE the SMD apertures for that side.
        var paste = PcbPaste.For(model, layout.PasteSettings!);
        foreach (var plot in PcbFabricationPlots.For(layout).Where(p => p.Kind == PcbPlotLayerKind.SolderPaste))
        {
            var drawing = plot.Compute();
            var content = paste.Layers.Single(p => p.Layer == plot.LayerName);
            Assert.Equal(content.Apertures.Count, drawing.Regions.Count);
            Assert.Equal(content.Apertures.Sum(a => a.Region.Area), drawing.PlottedArea, 9);
        }
    }

    [Fact]
    public void SilkPlot_DrawsExactlyTheSilkStrokes()
    {
        var layout = FullLayout();
        var silk = PcbSilkscreen.For(layout, layout.SilkscreenSettings!);

        foreach (var plot in PcbFabricationPlots.For(layout).Where(p => p.Kind == PcbPlotLayerKind.Silkscreen))
        {
            var drawing = plot.Compute();
            var content = silk.Layers.Single(s => s.Layer == plot.LayerName);
            // The plot carries the layer's OWN strokes (silk is line-work, so no regions/area).
            Assert.Equal(content.Strokes.Count, drawing.Strokes.Count);
            Assert.Empty(drawing.Regions);
            Assert.Equal(0, drawing.PlottedArea);
            if (content.Strokes.Count > 0)
                Assert.Contains(drawing.Segments, s => s.Layer == FabricationPlotLayers.Silk);
        }
    }

    // ---- 3. THE PAYOFF: the shared frame is a fab drawing's frame ---------------------------

    [Fact]
    public void Frame_IsByteIdenticalToAFabricationDrawingsFrame()
    {
        var title = new TitleBlock
        {
            Title = "Blinky PCB", DrawingNumber = "PCB-001", Author = "EngrCAD",
            Date = "2026", Revision = "A", Company = "ACME", Material = "FR4", Finish = "ENIG",
        };
        var layout = FullLayout();
        var plot = PcbFabricationPlots.For(layout, SheetFormat.A3, title)
            .First(p => p.Kind == PcbPlotLayerKind.Copper);
        var plotFrame = plot.Frame();
        var fab = new PcbFabricationSheet(layout, SheetFormat.A3, title);
        var fabFrame = fab.Frame();

        // By default the two DIFFER — each prints its own fitted scale (the plot fills the whole
        // sheet, the fab drawing uses a map column) — so the equality below is not vacuous.
        Assert.NotEqual(fabFrame.Compute().Texts, plotFrame.Compute().Texts);

        // Reconfigure BOTH to identical frame OPTIONS (one shared layout). ONE function — the shared
        // DrawingFrame — so the furniture matches byte for byte.
        var shared = new EngineeringTitleBlock(DrawingScales.Format(plot.Scale), ProjectionAngle.Third);
        var a = (fabFrame with { Layout = shared }).Compute();
        var b = (plotFrame with { Layout = shared }).Compute();
        Assert.Equal(a.Lines, b.Lines);
        Assert.Equal(a.Texts, b.Texts);

        // And the plot's own furniture IS exactly its frame's, in the frame's order.
        var drawing = plot.Compute();
        var frame = plotFrame.Compute();
        var furniture = drawing.Segments
            .Where(s => s.Layer is SheetLayers.Border or SheetLayers.TitleBlock)
            .Select(s => (s.A, s.B, s.Layer)).ToList();
        Assert.Equal(frame.Lines, furniture);
        var furnitureTexts = drawing.Texts.Where(t => t.Layer == SheetLayers.TitleBlock).ToList();
        Assert.Equal(frame.Texts, furnitureTexts);
    }

    // ---- 4. the bottom-mirror convention ---------------------------------------------------

    [Fact]
    public void BottomSideLayer_IsMirroredVersusTheTop()
    {
        var layout = FullLayout();
        var model = PcbCopperModel.FromLayout(layout);
        var copper = PcbFabricationPlots.For(layout).Where(p => p.Kind == PcbPlotLayerKind.Copper).ToList();

        var top = copper.Single(p => p.LayerName == model.Board.Stackup.Top.Name);
        var bottom = copper.Single(p => p.LayerName == model.Board.Stackup.Bottom.Name);

        // Each plot STATES its side and whether it is mirrored.
        Assert.Equal(CopperSide.Top, top.ViewSide);
        Assert.False(top.Mirrored);
        Assert.Equal(CopperSide.Bottom, bottom.ViewSide);
        Assert.True(bottom.Mirrored);
        Assert.Contains("MIRRORED", bottom.ViewNote());
        Assert.DoesNotContain("MIRRORED", top.ViewNote());

        var td = top.Compute();
        var bd = bottom.Compute();

        // The two share paper, board, scale and placement, so their transforms differ ONLY by the
        // mirror: the same board point maps to the same Y, and its X is reflected about ColumnCenter.
        Assert.Equal(td.Scale, bd.Scale);
        Assert.Equal(td.ColumnCenter, bd.ColumnCenter);
        Assert.Equal(td.BoardCenter, bd.BoardCenter);

        // An off-centre board point (a J1 pad, at ~(-9.27, 4)) — its X must FLIP, not merely stay put.
        foreach (var p in new[] { new Vector2d(10, 3), new Vector2d(-9.27, 4), new Vector2d(20, -12) })
        {
            var tp = td.Project(p);
            var bp = bd.Project(p);
            Assert.Equal(tp.Y, bp.Y, 9);
            // X reflected about the column centre: top.X + bottom.X == 2 * centre.X.
            Assert.Equal(2 * td.ColumnCenter.X, tp.X + bp.X, 9);
            // And the point genuinely moved (the board is off-centre here), so the mirror is visible.
            Assert.NotEqual(tp.X, bp.X, 6);
        }
    }

    // ---- 5. the writers emit and are deterministic ----------------------------------------

    [Fact]
    public void Writers_EmitAndAreDeterministic()
    {
        var plot = PcbFabricationPlots.For(FullLayout())
            .First(p => p.Kind == PcbPlotLayerKind.Copper);
        var drawing = plot.Compute();

        string svg = drawing.ToSvg();
        Assert.Contains("<svg", svg);
        Assert.Contains("path", svg);

        var dxf = drawing.ToDxf();
        Assert.NotEmpty(dxf.Entities);

        byte[] pdf = drawing.ToPdf();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));

        // Two emissions are byte-identical: a plot is a function of the layout and the paper, and the
        // one Compute() feeds all three writers, so they cannot disagree.
        Assert.Equal(svg, plot.Compute().ToSvg());
        Assert.Equal(pdf, plot.Compute().ToPdf());
        Assert.Equal(DxfText(dxf), DxfText(plot.Compute().ToDxf()));
    }

    private static string DxfText(DxfDocument dxf)
    {
        using var writer = new StringWriter();
        dxf.Save(writer);
        return writer.ToString();
    }

    // ---- 6. edge: a bare board still draws its (empty) copper plots ------------------------

    [Fact]
    public void BareBoard_PlotsEmptyCopperLayersThatStillDraw()
    {
        // A board with no placements — the copper layers exist but carry no features.
        var board = new PcbBoard(
            [
                new Vector2d(-15, -10), new Vector2d(15, -10),
                new Vector2d(15, 10), new Vector2d(-15, 10),
            ],
            thickness: 1.6);
        var layout = new PcbLayout(new Schematic("bare"), board);
        var plots = PcbFabricationPlots.For(layout);

        Assert.All(plots, p => Assert.Equal(PcbPlotLayerKind.Copper, p.Kind));
        var drawing = plots[0].Compute();
        Assert.Empty(drawing.Regions);
        // The outline, the frame and the label still draw, and the writers produce a valid document.
        Assert.NotEmpty(drawing.Outline);
        Assert.Contains(drawing.Segments, s => s.Layer == FabricationLayers.Outline);
        Assert.Contains("<svg", drawing.ToSvg());
    }
}
