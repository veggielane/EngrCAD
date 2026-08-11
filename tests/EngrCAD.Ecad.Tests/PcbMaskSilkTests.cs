using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Solder-mask and silkscreen fabrication layers. The bar is the fab house style — the TWIN-DECODER
/// round trip (the geometry must survive the round trip, not merely a structural validator pass):
/// a mask window equals its pad grown by the expansion EXACTLY and survives the Gerber round trip; a
/// silkscreen refdes / outline round-trips through the Gerber; a silk-over-copper overlap is DETECTED
/// and named; the full fab set (copper + mask + silk + outline + drill) writes and re-reads; and the
/// mask / silk are ADDITIVE — the copper Gerbers are byte-identical whether or not they are present.
/// </summary>
public sealed class PcbMaskSilkTests
{
    // ==== region-equality oracle (area AND symmetric difference) =============

    private static void AssertRegionsEqual(
        IReadOnlyList<CurvedRegion2d> expected, IReadOnlyList<CurvedRegion2d> actual,
        double tolerance, string what)
    {
        double expectedArea = expected.Sum(r => r.Area);
        double actualArea = actual.Sum(r => r.Area);
        double reference = Math.Max(expectedArea, 1e-30);

        Assert.True(Math.Abs(expectedArea - actualArea) <= tolerance * reference,
            $"{what}: area {actualArea:g9} recovered, expected {expectedArea:g9}");

        double symmetric =
            CurvedRegion2dBoolean.Difference(expected, actual).Sum(r => r.Area)
            + CurvedRegion2dBoolean.Difference(actual, expected).Sum(r => r.Area);
        Assert.True(symmetric <= tolerance * reference,
            $"{what}: recovered differs from expected by area {symmetric:g9} ({symmetric / reference:g3} relative)");
    }

    // ==== 1) a mask WINDOW equals its pad grown by the expansion EXACTLY =======

    [Fact]
    public void MaskWindow_OfRoundPad_EqualsPadPlusExpansion_ByClosedForm()
    {
        // A round SMD pad Ø 0.8 → its mask window is a disc of radius (0.4 + expansion), so its area is
        // exactly π(0.4 + e)² — the analytic oracle, independent of the boolean.
        const double d = 0.8, e = 0.06;
        var layout = RoundPadBoard(padDiameter: d);
        var model = PcbCopperModel.FromLayout(layout);
        var mask = PcbMask.For(model, new PcbMaskSettings(Expansion: e));

        var window = Assert.Single(mask.Top.Openings);
        double expected = Math.PI * (d / 2 + e) * (d / 2 + e);
        // The offset of a disc is an exact disc, so the window is the pad grown by the expansion to
        // essentially machine precision (measured ~1e-12 relative), not merely the region grade.
        Assert.True(Math.Abs(window.Region.Area - expected) <= 1e-12 * expected,
            $"window area {window.Region.Area:g17} vs analytic π(0.4+{e})² = {expected:g17}");
        Assert.Equal("T1.1", window.Source);

        // And it IS the pad grown by the expansion (the region grade), not merely equal in area.
        var padRegion = model.Copper.Single(f => f.Layer == "Top").Region;
        var grown = CurvedRegion2dOffset.Offset(padRegion, e, OffsetJoin.Round);
        AssertRegionsEqual(grown, [window.Region], 1e-9, "mask window vs pad+expansion");
    }

    [Fact]
    public void MaskWindow_WithZeroExpansion_IsThePadExactly()
    {
        var layout = RoundPadBoard(padDiameter: 0.9);
        var model = PcbCopperModel.FromLayout(layout);
        var mask = PcbMask.For(model, new PcbMaskSettings(Expansion: 0));

        var window = Assert.Single(mask.Top.Openings);
        var padRegion = model.Copper.Single(f => f.Layer == "Top").Region;
        Assert.Equal(padRegion.Area, window.Region.Area, 12);   // pad exactly
    }

    // ==== 2) the mask Gerber ROUND-TRIPS =====================================

    [Fact]
    public void MaskGerber_RoundTrips_ForARoutedBoard()
    {
        var layout = PcbFixtures.Layout().WithMask(new PcbMaskSettings(Expansion: 0.05));
        var model = PcbCopperModel.FromLayout(layout);
        var mask = PcbMask.For(model, layout.MaskSettings);
        var fab = PcbGerberExport.Generate(layout);

        foreach (var side in new[] { "Top", "Bottom" })
        {
            var decoded = GerberReader.Read(fab.MaskLayers.Single(l => l.Layer == side).Gerber).Copper;
            var modelWindows = mask.LayerFor(side).Openings.Select(o => o.Region).ToList();
            AssertRegionsEqual(modelWindows, decoded, 1e-4, $"mask '{side}'");
        }
    }

    [Fact]
    public void RectPadMaskWindow_IsARoundedRect_AndRoundTripsAsARegionFill()
    {
        // A rectangular pad grown by the expansion is a ROUNDED rectangle (round joins), so it falls to
        // a region fill — exact for the shape, and it round-trips.
        var def = new PartDefinition("REC", "U", [new Pin("1", PinType.Passive)],
            new Footprint("rec_fp", [new Pad("1", new Vector2d(0, 0), 2.0, 1.0, PadShape.Rectangular)]));
        var sch = new Schematic("rect");
        var u = sch.Add("U1", def);
        sch.Stub("TP", u.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)], 1.6));
        layout.Place("U1", 0, 0);

        var fab = PcbGerberExport.Generate(layout);
        string top = fab.MaskLayers.Single(l => l.Layer == "Top").Gerber;
        Assert.Contains("G36*", top);   // a region fill (rounded-rect window)

        var mask = PcbMask.For(PcbCopperModel.FromLayout(layout), layout.MaskSettings);
        var decoded = GerberReader.Read(top).Copper;
        AssertRegionsEqual(mask.Top.Openings.Select(o => o.Region).ToList(), decoded, 1e-4, "rounded-rect window");
    }

    // ==== 3) via tenting policy ===============================================

    [Fact]
    public void ViaPolicy_TentedGivesNoWindow_OpenedGivesOne()
    {
        var layout = StitchViaBoard();
        var model = PcbCopperModel.FromLayout(layout);

        var tented = PcbMask.For(model, new PcbMaskSettings(Vias: ViaMaskPolicy.Tented));
        var opened = PcbMask.For(model, new PcbMaskSettings(Vias: ViaMaskPolicy.Opened));

        // The stitching via is uncovered copper: tented → no window over it; opened → one window per
        // side it touches (Top + Bottom).
        Assert.DoesNotContain(tented.Top.Openings, o => o.Source == "via1");
        Assert.Contains(opened.Top.Openings, o => o.Source == "via1");
        Assert.Contains(opened.Bottom.Openings, o => o.Source == "via1");
    }

    // ==== 4) the silkscreen ROUND-TRIPS ======================================

    [Fact]
    public void Silkscreen_RefdesAndOutline_RoundTripThroughTheGerber()
    {
        var layout = SilkBoard();
        var fab = PcbGerberExport.Generate(layout);
        var silk = PcbSilkscreen.For(layout);

        Assert.Contains(silk.Top.Strokes, s => s.Kind == SilkKind.Reference);   // "T1"
        Assert.Contains(silk.Top.Strokes, s => s.Kind == SilkKind.Courtyard);   // the body outline

        string topGerber = fab.SilkLayers.Single(l => l.Layer == "Top").Gerber;
        Assert.Contains("D01*", topGerber);   // silk is line-work (draws)

        var decoded = GerberReader.Read(topGerber).Copper;
        var modelFootprint = StrokeAll(silk.Top);
        AssertRegionsEqual(modelFootprint, decoded, 1e-3, "silkscreen");
    }

    // ==== 5) silk-over-copper is DETECTED and named ==========================

    [Fact]
    public void SilkOverExposedCopper_IsDetectedAndNamed()
    {
        // A board mark placed ON a pad — silk printed onto a solderable land, a real defect.
        var padCentre = new Vector2d(0, 0);
        var layout = RoundPadBoard(padDiameter: 1.0)
            .WithSilkscreen(new PcbSilkscreenSettings(
                ShowReferences: false, ShowCourtyards: false,
                Marks: [new SilkMark("XX", padCentre, CopperSide.Top)]));
        var model = PcbCopperModel.FromLayout(layout);
        var mask = PcbMask.For(model, layout.MaskSettings);
        var silk = PcbSilkscreen.For(layout, layout.SilkscreenSettings);

        var violations = silk.OverExposedCopper(mask);
        var hit = Assert.Single(violations);
        Assert.Equal("board", hit.SilkSource);
        Assert.Equal(SilkKind.Mark, hit.Kind);
        Assert.Equal("T1.1", hit.OpeningSource);   // named the pad it lands on
        Assert.True(hit.OverlapArea > 0);
    }

    [Fact]
    public void CleanLayout_HasNoSilkOverCopper()
    {
        // The auto silk puts the refdes above the courtyard and the courtyard around the pads, so a
        // normal layout has no silk on any exposed copper.
        var layout = PcbFixtures.Layout();
        var model = PcbCopperModel.FromLayout(layout);
        var mask = PcbMask.For(model, layout.MaskSettings);
        var silk = PcbSilkscreen.For(layout, layout.SilkscreenSettings);

        Assert.Empty(silk.OverExposedCopper(mask));
    }

    // ==== 6) the FULL fab set writes and re-reads =============================

    [Fact]
    public void FullFabSet_WritesEveryLayer_AndEachReReads()
    {
        var layout = PcbFixtures.Layout()
            .WithMask(new PcbMaskSettings(Expansion: 0.05))
            .WithSilkscreen(PcbSilkscreenSettings.Default);
        string dir = Path.Combine(Path.GetTempPath(), "engrcad-fab-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = PcbGerberExport.Write(layout, dir, "blinky");

            Assert.Equal(2, result.CopperLayerCount);
            Assert.Equal(2, result.MaskLayerCount);
            Assert.Equal(2, result.SilkLayerCount);
            Assert.All(result.Files, f => Assert.True(File.Exists(f)));
            Assert.Contains(result.Files, f => f.EndsWith("-Top_Mask.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Bottom_Mask.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Top_Silkscreen.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Edge_Cuts.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith(".drl"));

            // Every written Gerber re-reads (the whole set is a well-formed manufacturable set).
            foreach (var f in result.Files.Where(f => f.EndsWith(".gbr")))
                GerberReader.Read(File.ReadAllText(f));
            ExcellonReader.Read(File.ReadAllText(result.Files.Single(f => f.EndsWith(".drl"))));

            // The written mask file IS the Generate() text (no drift between disk and text).
            var output = PcbGerberExport.Generate(layout, "blinky");
            string topMaskPath = result.Files.Single(f => f.EndsWith("-Top_Mask.gbr"));
            Assert.Equal(output.MaskLayers.Single(l => l.Layer == "Top").Gerber, File.ReadAllText(topMaskPath));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ==== 7) the mask / silk are ADDITIVE — copper is byte-identical ==========

    [Fact]
    public void CopperGerbers_AreByteIdentical_WithAndWithoutMaskSilk()
    {
        var layout = PcbFixtures.Layout();
        var without = PcbGerberExport.Generate(layout, "b");

        layout.WithMask(new PcbMaskSettings(Expansion: 0.08, Vias: ViaMaskPolicy.Opened))
              .WithSilkscreen(new PcbSilkscreenSettings(TextHeight: 1.2, ShowValues: true));
        var with = PcbGerberExport.Generate(layout, "b");

        for (int i = 0; i < without.CopperLayers.Count; i++)
            Assert.Equal(without.CopperLayers[i].Gerber, with.CopperLayers[i].Gerber);
        Assert.Equal(without.OutlineGerber, with.OutlineGerber);
        Assert.Equal(without.Drill, with.Drill);

        // ... while the mask / silk differ (they honoured the settings).
        Assert.NotEqual(
            without.MaskLayers.Single(l => l.Layer == "Top").Gerber,
            with.MaskLayers.Single(l => l.Layer == "Top").Gerber);
    }

    // ==== 8) determinism =====================================================

    [Fact]
    public void MaskAndSilk_AreDeterministic()
    {
        var layout = PcbFixtures.Layout().WithSilkscreen(new PcbSilkscreenSettings(ShowValues: true));
        var a = PcbGerberExport.Generate(layout, "b");
        var b = PcbGerberExport.Generate(layout, "b");
        for (int i = 0; i < a.MaskLayers.Count; i++)
            Assert.Equal(a.MaskLayers[i].Gerber, b.MaskLayers[i].Gerber);
        for (int i = 0; i < a.SilkLayers.Count; i++)
            Assert.Equal(a.SilkLayers[i].Gerber, b.SilkLayers[i].Gerber);
    }

    // ==== 9) an EMPTY side writes valid, empty mask / silk ===================

    [Fact]
    public void EmptyBottomSide_WritesValidEmptyMaskAndSilk()
    {
        // All-SMD, all-top: nothing lands on the bottom copper — its mask and silk are empty but valid.
        var layout = SilkBoard();   // one top SMD test point
        var mask = PcbMask.For(PcbCopperModel.FromLayout(layout));
        var silk = PcbSilkscreen.For(layout);
        Assert.Empty(mask.Bottom.Openings);
        Assert.Empty(silk.Bottom.Strokes);

        var fab = PcbGerberExport.Generate(layout);
        string bottomMask = fab.MaskLayers.Single(l => l.Layer == "Bottom").Gerber;
        string bottomSilk = fab.SilkLayers.Single(l => l.Layer == "Bottom").Gerber;
        foreach (var g in new[] { bottomMask, bottomSilk })
        {
            Assert.Contains("%FSLA", g);
            Assert.Contains("%MOMM*%", g);
            Assert.EndsWith("M02*\n", g);
            Assert.Empty(GerberReader.Read(g).Copper);
        }
    }

    // ==== 10) refusals — the guards shown to FIRE ============================

    [Fact]
    public void Mask_OnNonExistentLayer_IsRefusedByName()
    {
        var mask = PcbMask.For(PcbCopperModel.FromLayout(PcbFixtures.Layout()));
        var ex = Assert.Throws<ArgumentException>(() => mask.LayerFor("In1"));
        Assert.Contains("In1", ex.Message);
        Assert.Contains("outer copper", ex.Message);
    }

    [Fact]
    public void Silk_OnNonExistentLayer_IsRefusedByName()
    {
        var silk = PcbSilkscreen.For(PcbFixtures.Layout());
        var ex = Assert.Throws<ArgumentException>(() => silk.LayerFor("In1"));
        Assert.Contains("In1", ex.Message);
    }

    [Fact]
    public void MaskWindow_OffTheBoard_IsRefusedByName()
    {
        // A component placed far off the board — its mask window has nowhere to be.
        var layout = RoundPadBoard(padDiameter: 0.8);
        layout.Place("OFF", 1000, 1000);   // way outside the ±5 board
        var model = PcbCopperModel.FromLayout(layout);
        var ex = Assert.Throws<ArgumentException>(() => PcbMask.For(model));
        Assert.Contains("off the board", ex.Message);
    }

    [Fact]
    public void NegativeMaskExpansion_IsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PcbMaskSettings(Expansion: -0.1).Validate());
        Assert.Contains("negative", ex.Message);
    }

    // ==== 11) persistence — layout truth, byte fixed point ==================

    [Fact]
    public void MaskAndSilkSettings_RoundTripAsAByteFixedPoint()
    {
        var layout = PcbFixtures.Layout()
            .WithMask(new PcbMaskSettings(Expansion: 0.075, Vias: ViaMaskPolicy.Opened))
            .WithSilkscreen(new PcbSilkscreenSettings(
                TextHeight: 1.3, LineWidth: 0.2, ShowValues: true,
                Marks: [new SilkMark("REV A", new Vector2d(20, 15)),
                        new SilkMark("BOT", new Vector2d(-20, -15), CopperSide.Bottom, 0.8)]));

        string json1 = layout.Save();
        var reloaded = PcbLayout.Load(json1, PcbFixtures.Library());
        string json2 = reloaded.Save();
        Assert.Equal(json1, json2);   // save -> load -> save is byte-identical

        // The settings survived.
        Assert.Equal(0.075, reloaded.MaskSettings!.Expansion);
        Assert.Equal(ViaMaskPolicy.Opened, reloaded.MaskSettings.Vias);
        Assert.Equal(1.3, reloaded.SilkscreenSettings!.TextHeight);
        Assert.True(reloaded.SilkscreenSettings.ShowValues);
        Assert.Equal(2, reloaded.SilkscreenSettings.BoardMarks.Count);
        Assert.Equal("REV A", reloaded.SilkscreenSettings.BoardMarks[0].Text);
        Assert.Equal(CopperSide.Bottom, reloaded.SilkscreenSettings.BoardMarks[1].Side);
    }

    [Fact]
    public void ALayoutWithNoMaskOrSilk_SavesByteIdenticallyToBefore()
    {
        // Setting nothing leaves the file exactly as a pre-fabrication layout's — the write-only-when-
        // stated contract, so no 'mask'/'silk' keys appear.
        var layout = PcbFixtures.Layout();
        string json = layout.Save();
        Assert.DoesNotContain("\"mask\"", json);
        Assert.DoesNotContain("\"silk\"", json);
        Assert.Null(layout.MaskSettings);
        Assert.Null(layout.SilkscreenSettings);
    }

    // ==== fixtures ===========================================================

    private static PcbLayout RoundPadBoard(double padDiameter)
    {
        PartDefinition Tp(string name) => new(name, "TP",
            [new Pin("1", PinType.Passive)],
            new Footprint(name + "_fp",
                [Pad.Smd("1", new Vector2d(0, 0), padDiameter, padDiameter, PadShape.Round)]));
        var sch = new Schematic("round");
        var t = sch.Add("T1", Tp("T1"), "TP");
        var off = sch.Add("OFF", Tp("OFF"), "TP");
        sch.Stub("A", t.Pin("1"));
        sch.Stub("B", off.Pin("1"));
        var layout = new PcbLayout(sch,
            new PcbBoard([new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)], 1.6));
        layout.Place("T1", 0, 0);
        return layout;
    }

    private static PcbLayout SilkBoard()
    {
        PartDefinition Tp(string name) => new(name, "TP",
            [new Pin("1", PinType.Passive)],
            new Footprint(name + "_fp",
                [Pad.Smd("1", new Vector2d(0, 0), 0.8, 0.8, PadShape.Round)]));
        var sch = new Schematic("silk");
        var t = sch.Add("T1", Tp("T1"), "TP");
        sch.Stub("N", t.Pin("1"));
        var layout = new PcbLayout(sch,
            new PcbBoard([new(-6, -6), new(6, -6), new(6, 6), new(-6, 6)], 1.6));
        layout.Place("T1", 0, 0);
        return layout;
    }

    private static PcbLayout StitchViaBoard()
    {
        PartDefinition Tp(string name) => new(name, "TP",
            [new Pin("1", PinType.Passive)],
            new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));
        var sch = new Schematic("stitch");
        var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
        sch.Connect("GND", a.Pin("1"), b.Pin("1"));
        var layout = new PcbLayout(sch,
            new PcbBoard([new(0, 0), new(20, 0), new(20, 20), new(0, 20)], 1.6));
        layout.Place("A", 3, 3); layout.Place("B", 6, 3);
        layout.AddVia("GND", 15, 15, "Top", "Bottom", drill: 0.4, pad: 0.9);
        return layout;
    }

    private static List<CurvedRegion2d> StrokeAll(SilkLayerContent silk)
    {
        var primitives = new List<CurvedRegion2d>();
        foreach (var stroke in silk.Strokes)
        {
            var edges = new List<CurvedEdge2d>();
            for (int i = 1; i < stroke.Points.Count; i++)
                if (stroke.Points[i - 1].DistanceTo(stroke.Points[i]) > CurvedRegion2d.BoundaryTolerance)
                    edges.Add(CurvedEdge2d.Line(stroke.Points[i - 1], stroke.Points[i]));
            if (edges.Count == 0)
                continue;
            primitives.AddRange(
                CurvedRegion2dOffset.Stroke(edges, silk.LineWidth, StrokeCap.Round, OffsetJoin.Round));
        }
        return CurvedRegion2dBoolean.UnionAll(primitives).ToList();
    }
}
