using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Solder-paste (stencil) fabrication layer — the last piece of the fab set, so a routed board can be
/// fully manufactured AND reflow-assembled. The bar is the fab house style — the TWIN-DECODER round trip
/// (the geometry must survive the round trip, not merely a structural validator pass): a stencil aperture
/// equals its SMD pad grown by the expansion EXACTLY and survives the Gerber round trip; a through-hole
/// pad gets NO aperture (the SMD-only rule — the classic bug is pasting a THT pad); the full fab set
/// (copper + mask + silk + paste + outline + drill) writes and re-reads; and the paste is ADDITIVE — the
/// copper / mask / silk Gerbers are byte-identical whether or not it is present.
/// </summary>
public sealed class PcbPasteTests
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

    // ==== 1) an aperture equals its SMD pad grown by the expansion EXACTLY =====

    [Fact]
    public void PasteAperture_OfRoundPad_EqualsPadPlusExpansion_ByClosedForm()
    {
        // A round SMD pad Ø 0.8 with the default negative expansion → the aperture is a disc of radius
        // (0.4 + e) with e = -0.05, so its area is exactly π(0.4 − 0.05)² — the analytic oracle, and it
        // is SMALLER than the pad (a stencil aperture pulls in to control paste volume).
        const double d = 0.8, e = -0.05;
        var layout = RoundPadBoard(padDiameter: d);
        var model = PcbCopperModel.FromLayout(layout);
        var paste = PcbPaste.For(model, new PcbPasteSettings(Expansion: e));

        var aperture = Assert.Single(paste.Top.Apertures);
        Assert.Equal("T1.1", aperture.Source);
        double expected = Math.PI * (d / 2 + e) * (d / 2 + e);
        // The offset of a disc is an exact disc, so the aperture is the pad grown by the expansion to
        // essentially machine precision (~1e-12 relative), not merely the region grade.
        Assert.True(Math.Abs(aperture.Region.Area - expected) <= 1e-12 * expected,
            $"aperture area {aperture.Region.Area:g17} vs analytic π(0.4−0.05)² = {expected:g17}");

        // The negative expansion SHRANK it — the aperture is smaller than the pad.
        var padRegion = model.Copper.Single(f => f.Layer == "Top").Region;
        Assert.True(aperture.Region.Area < padRegion.Area,
            $"aperture area {aperture.Region.Area:g9} should be < pad area {padRegion.Area:g9}");

        // And it IS the pad grown by the expansion (the region grade), not merely equal in area.
        var grown = CurvedRegion2dOffset.Offset(padRegion, e, OffsetJoin.Round);
        AssertRegionsEqual(grown, [aperture.Region], 1e-9, "paste aperture vs pad+expansion");
    }

    [Fact]
    public void PasteAperture_WithZeroExpansion_IsThePadExactly()
    {
        var layout = RoundPadBoard(padDiameter: 0.9);
        var model = PcbCopperModel.FromLayout(layout);
        var paste = PcbPaste.For(model, new PcbPasteSettings(Expansion: 0));

        var aperture = Assert.Single(paste.Top.Apertures);
        var padRegion = model.Copper.Single(f => f.Layer == "Top").Region;
        Assert.Equal(padRegion.Area, aperture.Region.Area, 12);   // the pad exactly
    }

    [Fact]
    public void NegativeExpansionShrinks_ZeroIsThePad_PositiveGrows()
    {
        // The whole point of the paste expansion: negative pulls the aperture in (less paste), zero is the
        // pad, positive grows it. A round Ø 1.0 pad, so the pad area is π·0.5² = π/4.
        var layout = RoundPadBoard(padDiameter: 1.0);
        var model = PcbCopperModel.FromLayout(layout);

        double Area(double e) =>
            PcbPaste.For(model, new PcbPasteSettings(Expansion: e)).Top.Apertures.Single().Region.Area;

        double shrunk = Area(-0.1), exact = Area(0), grown = Area(0.1);
        double pad = model.Copper.Single(f => f.Layer == "Top").Region.Area;

        Assert.Equal(pad, exact, 12);                       // zero expansion is the pad
        Assert.True(shrunk < exact, $"−0.1 area {shrunk:g9} should be < pad {exact:g9}");
        Assert.True(grown > exact, $"+0.1 area {grown:g9} should be > pad {exact:g9}");
        // Exact by closed form: π(0.5 ± 0.1)².
        Assert.Equal(Math.PI * 0.4 * 0.4, shrunk, 12);
        Assert.Equal(Math.PI * 0.6 * 0.6, grown, 12);
    }

    // ==== 2) the SMD-ONLY rule — a through-hole pad gets NO paste =============

    [Fact]
    public void ThroughHolePad_GetsNoPasteAperture_SmdPadDoes()
    {
        // The stage-2 fixture: an SMD resistor (R1, two SMD pads) AND a through-hole header (J1, two
        // drilled pads), both on top, plus two mounting holes. Paste covers the SMD pads ONLY — the
        // classic bug is pasting a THT pad (it is wave/hand-soldered).
        var layout = PcbFixtures.Layout();
        var model = PcbCopperModel.FromLayout(layout);
        var paste = PcbPaste.For(model);

        var sources = paste.Top.Apertures.Select(a => a.Source).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("R1.1", sources);
        Assert.Contains("R1.2", sources);          // the SMD pads get paste
        Assert.DoesNotContain("J1.1", sources);
        Assert.DoesNotContain("J1.2", sources);    // the through-hole pads do NOT
        Assert.Equal(2, paste.Top.Apertures.Count);   // exactly the two SMD pads, nothing else

        // The mounting holes are not pads, so nothing there either — and the bottom side has no top-side
        // component pads.
        Assert.Empty(paste.Bottom.Apertures);
    }

    [Fact]
    public void Via_GetsNoPasteAperture_EvenWhenMaskWouldOpenIt()
    {
        // A stitching via is uncovered copper that the MASK can open by policy — but paste never covers a
        // via (paste on a via wicks solder down the barrel), so it is excluded regardless.
        var layout = StitchViaBoard();
        var model = PcbCopperModel.FromLayout(layout);
        var paste = PcbPaste.For(model);   // there is no via policy on paste

        var sources = paste.Top.Apertures.Select(a => a.Source).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("A.1", sources);
        Assert.Contains("B.1", sources);              // the two SMD test points
        Assert.DoesNotContain("via1", sources);       // never the via
    }

    // ==== 3) the paste Gerber ROUND-TRIPS ====================================

    [Fact]
    public void PasteGerber_RoundTrips_ForABoardWithSmdPads()
    {
        var layout = PcbFixtures.Layout().WithPaste(new PcbPasteSettings(Expansion: -0.05));
        var model = PcbCopperModel.FromLayout(layout);
        var paste = PcbPaste.For(model, layout.PasteSettings);
        var fab = PcbGerberExport.Generate(layout);

        foreach (var side in new[] { "Top", "Bottom" })
        {
            var decoded = GerberReader.Read(fab.PasteLayers.Single(l => l.Layer == side).Gerber).Copper;
            var apertures = paste.LayerFor(side).Apertures.Select(a => a.Region).ToList();
            AssertRegionsEqual(apertures, decoded, 1e-4, $"paste '{side}'");
        }
    }

    [Fact]
    public void RectPadPasteAperture_GrownIsARoundedRect_AndRoundTripsAsARegionFill()
    {
        // A rectangular SMD pad grown by a POSITIVE expansion is a ROUNDED rectangle (round joins), so it
        // falls to a region fill — exact for the shape, and it round-trips through the twin decoder.
        var def = new PartDefinition("REC", "U", [new Pin("1", PinType.Passive)],
            new Footprint("rec_fp", [new Pad("1", new Vector2d(0, 0), 2.0, 1.0, PadShape.Rectangular)]));
        var sch = new Schematic("rect");
        var u = sch.Add("U1", def);
        sch.Stub("TP", u.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)], 1.6))
            .WithPaste(new PcbPasteSettings(Expansion: 0.1));
        layout.Place("U1", 0, 0);

        var fab = PcbGerberExport.Generate(layout);
        string top = fab.PasteLayers.Single(l => l.Layer == "Top").Gerber;
        Assert.Contains("G36*", top);   // a region fill (rounded-rect aperture)

        var paste = PcbPaste.For(PcbCopperModel.FromLayout(layout), layout.PasteSettings);
        var decoded = GerberReader.Read(top).Copper;
        AssertRegionsEqual(paste.Top.Apertures.Select(a => a.Region).ToList(), decoded, 1e-4, "rounded-rect aperture");
    }

    // ==== 4) the FULL fab set (now including paste) writes and re-reads =======

    [Fact]
    public void FullFabSet_WritesEveryLayerIncludingPaste_AndEachReReads()
    {
        var layout = PcbFixtures.Layout()
            .WithMask(new PcbMaskSettings(Expansion: 0.05))
            .WithSilkscreen(PcbSilkscreenSettings.Default)
            .WithPaste(new PcbPasteSettings(Expansion: -0.05));
        string dir = Path.Combine(Path.GetTempPath(), "engrcad-paste-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = PcbGerberExport.Write(layout, dir, "blinky");

            Assert.Equal(2, result.CopperLayerCount);
            Assert.Equal(2, result.MaskLayerCount);
            Assert.Equal(2, result.SilkLayerCount);
            Assert.Equal(2, result.PasteLayerCount);
            Assert.All(result.Files, f => Assert.True(File.Exists(f)));
            Assert.Contains(result.Files, f => f.EndsWith("-Top_Paste.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Bottom_Paste.gbr"));
            // The full fab set: copper + mask + silk + paste + outline + drill.
            Assert.Contains(result.Files, f => f.EndsWith("-Top_Mask.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Top_Silkscreen.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Edge_Cuts.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith(".drl"));

            // Every written Gerber re-reads (the whole set is a well-formed manufacturable set).
            foreach (var f in result.Files.Where(f => f.EndsWith(".gbr")))
                GerberReader.Read(File.ReadAllText(f));
            ExcellonReader.Read(File.ReadAllText(result.Files.Single(f => f.EndsWith(".drl"))));

            // The written paste file IS the Generate() text (no drift between disk and text).
            var output = PcbGerberExport.Generate(layout, "blinky");
            string topPastePath = result.Files.Single(f => f.EndsWith("-Top_Paste.gbr"));
            Assert.Equal(output.PasteLayers.Single(l => l.Layer == "Top").Gerber, File.ReadAllText(topPastePath));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ==== 5) paste is ADDITIVE — copper / mask / silk byte-identical ==========

    [Fact]
    public void CopperMaskSilk_AreByteIdentical_WithAndWithoutPaste()
    {
        // Fix the mask and silk on both, and vary ONLY the paste. The copper, outline, drill, mask and
        // silk Gerbers must be byte-identical whether or not the paste settings are stated.
        static PcbLayout Base() => PcbFixtures.Layout()
            .WithMask(new PcbMaskSettings(Expansion: 0.05, Vias: ViaMaskPolicy.Opened))
            .WithSilkscreen(new PcbSilkscreenSettings(TextHeight: 1.2, ShowValues: true));

        var without = PcbGerberExport.Generate(Base(), "b");
        var with = PcbGerberExport.Generate(Base().WithPaste(new PcbPasteSettings(Expansion: -0.08)), "b");

        for (int i = 0; i < without.CopperLayers.Count; i++)
            Assert.Equal(without.CopperLayers[i].Gerber, with.CopperLayers[i].Gerber);
        Assert.Equal(without.OutlineGerber, with.OutlineGerber);
        Assert.Equal(without.Drill, with.Drill);
        for (int i = 0; i < without.MaskLayers.Count; i++)
            Assert.Equal(without.MaskLayers[i].Gerber, with.MaskLayers[i].Gerber);
        for (int i = 0; i < without.SilkLayers.Count; i++)
            Assert.Equal(without.SilkLayers[i].Gerber, with.SilkLayers[i].Gerber);

        // ... while the paste differs (it honoured the −0.08 expansion vs the default −0.05).
        Assert.NotEqual(
            without.PasteLayers.Single(l => l.Layer == "Top").Gerber,
            with.PasteLayers.Single(l => l.Layer == "Top").Gerber);
    }

    // ==== 6) determinism =====================================================

    [Fact]
    public void Paste_IsDeterministic()
    {
        var layout = PcbFixtures.Layout().WithPaste(new PcbPasteSettings(Expansion: -0.06));
        var a = PcbGerberExport.Generate(layout, "b");
        var b = PcbGerberExport.Generate(layout, "b");
        for (int i = 0; i < a.PasteLayers.Count; i++)
            Assert.Equal(a.PasteLayers[i].Gerber, b.PasteLayers[i].Gerber);
    }

    // ==== 7) an all-through-hole board writes a valid, EMPTY paste ============

    [Fact]
    public void AllThroughHoleBoard_WritesValidEmptyPaste()
    {
        // A board with only a through-hole part: no SMD pads, so no paste apertures on either side — but
        // the paste Gerber is still a well-formed RS-274X file and decodes to no copper.
        var def = new PartDefinition("HDR", "J",
            [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
            new Footprint("HDR_fp",
            [
                Pad.ThroughHole("1", new Vector2d(-1.27, 0), pad: 1.6, drill: 0.9),
                Pad.ThroughHole("2", new Vector2d(1.27, 0), pad: 1.6, drill: 0.9),
            ]));
        var sch = new Schematic("tht-only");
        var j = sch.Add("J1", def);
        sch.Stub("A", j.Pin("1"));
        sch.Stub("B", j.Pin("2"));
        var layout = new PcbLayout(sch, new PcbBoard([new(-8, -8), new(8, -8), new(8, 8), new(-8, 8)], 1.6));
        layout.Place("J1", 0, 0);

        var paste = PcbPaste.For(PcbCopperModel.FromLayout(layout));
        Assert.Empty(paste.Top.Apertures);
        Assert.Empty(paste.Bottom.Apertures);

        var fab = PcbGerberExport.Generate(layout);
        foreach (var side in new[] { "Top", "Bottom" })
        {
            string g = fab.PasteLayers.Single(l => l.Layer == side).Gerber;
            Assert.Contains("%FSLA", g);
            Assert.Contains("%MOMM*%", g);
            Assert.EndsWith("M02*\n", g);
            Assert.Empty(GerberReader.Read(g).Copper);
        }
    }

    // ==== 8) refusals — the guards shown to FIRE =============================

    [Fact]
    public void Paste_OnNonExistentLayer_IsRefusedByName()
    {
        var paste = PcbPaste.For(PcbCopperModel.FromLayout(PcbFixtures.Layout()));
        var ex = Assert.Throws<ArgumentException>(() => paste.LayerFor("In1"));
        Assert.Contains("In1", ex.Message);
        Assert.Contains("outer copper", ex.Message);
    }

    [Fact]
    public void PasteAperture_OffTheBoard_IsRefusedByName()
    {
        // An SMD component placed far off the board — its stencil aperture has nowhere to be.
        var layout = RoundPadBoard(padDiameter: 0.8);
        layout.Place("OFF", 1000, 1000);   // way outside the ±5 board
        var model = PcbCopperModel.FromLayout(layout);
        var ex = Assert.Throws<ArgumentException>(() => PcbPaste.For(model));
        Assert.Contains("off the board", ex.Message);
    }

    [Fact]
    public void NonFinitePasteExpansion_IsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new PcbPasteSettings(Expansion: double.NaN).Validate());
        Assert.Contains("finite", ex.Message);
    }

    // ==== 9) persistence — layout truth, byte fixed point ====================

    [Fact]
    public void PasteSettings_RoundTripAsAByteFixedPoint()
    {
        var layout = PcbFixtures.Layout()
            .WithMask(new PcbMaskSettings(Expansion: 0.05))
            .WithSilkscreen(PcbSilkscreenSettings.Default)
            .WithPaste(new PcbPasteSettings(Expansion: -0.075));

        string json1 = layout.Save();
        var reloaded = PcbLayout.Load(json1, PcbFixtures.Library());
        string json2 = reloaded.Save();
        Assert.Equal(json1, json2);   // save -> load -> save is byte-identical

        Assert.Equal(-0.075, reloaded.PasteSettings!.Expansion);
    }

    [Fact]
    public void PasteOnly_RoundTripsAndWritesJustThePasteKey()
    {
        // A layout that states paste and nothing else (no mask/silk) — the write-only-when-stated
        // contract: a 'paste' key, no 'mask'/'silk' keys, and a save -> load -> save fixed point.
        var layout = PcbFixtures.Layout().WithPaste(new PcbPasteSettings(Expansion: -0.04));
        string json1 = layout.Save();
        Assert.Contains("\"paste\"", json1);
        Assert.DoesNotContain("\"mask\"", json1);
        Assert.DoesNotContain("\"silk\"", json1);

        var reloaded = PcbLayout.Load(json1, PcbFixtures.Library());
        Assert.Equal(json1, reloaded.Save());
        Assert.Equal(-0.04, reloaded.PasteSettings!.Expansion);
    }

    [Fact]
    public void ALayoutWithNoPaste_SavesByteIdenticallyToBefore()
    {
        // Setting nothing leaves the file exactly as a pre-paste layout's — no 'paste' key appears.
        var layout = PcbFixtures.Layout();
        string json = layout.Save();
        Assert.DoesNotContain("\"paste\"", json);
        Assert.Null(layout.PasteSettings);
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
}
