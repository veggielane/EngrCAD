using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Gerber (RS-274X) + Excellon fabrication export. The bar is higher than usual — a fab file that is
/// subtly wrong scraps a board — so the oracle is the TWIN-DECODER ROUND TRIP (the repo's rule: the
/// geometry must survive the round trip, not merely a structural validator pass): the copper written
/// is parsed BACK and the recovered copper equals the copper model's on each layer to the region-area
/// grade (area AND symmetric difference, via the DRC's own `CurvedRegion2dBoolean`), and the decoded
/// drill hits equal the board's holes exactly.
/// </summary>
public sealed class PcbFabricationTests
{
    // A comfortable rule/router setup, matching the routing tests.
    private static readonly DrcRuleSet Rules = new(
        MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
        MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80);

    private static readonly RouterOptions RouterOpt = new()
    {
        GridResolution = 1.0, TraceWidth = 0.4, Clearance = 0.3,
    };

    // ==== the round-trip oracle ==============================================

    /// <summary>Asserts the copper written for a layout round-trips: each copper layer's Gerber decodes
    /// back to copper equal — by AREA and by SYMMETRIC DIFFERENCE — to the copper model's copper on that
    /// layer, and the drill hits recover exactly. This is the whole verification bar.</summary>
    private static void AssertCopperRoundTrips(
        FabricationOutput output, PcbCopperModel model, double tolerance = 1e-4)
    {
        foreach (var layerFile in output.CopperLayers)
        {
            var decoded = GerberReader.Read(layerFile.Gerber).Copper;
            var modelUnion = CurvedRegion2dBoolean.UnionAll(
                [.. model.Copper.Where(f => f.Layer == layerFile.Layer).Select(f => f.Region)]);

            double modelArea = modelUnion.Sum(r => r.Area);
            double decodedArea = decoded.Sum(r => r.Area);
            double reference = Math.Max(modelArea, 1e-30);

            Assert.True(Math.Abs(modelArea - decodedArea) <= tolerance * reference,
                $"layer '{layerFile.Layer}': copper area {decodedArea:g9} recovered, model {modelArea:g9}");

            // Symmetric difference through the DRC's own boolean — the strong form: not merely "the
            // areas match" (a shape could match in area yet differ in shape) but "the recovered copper
            // IS the model's copper", set for set.
            double symmetric =
                CurvedRegion2dBoolean.Difference(modelUnion, decoded).Sum(r => r.Area)
                + CurvedRegion2dBoolean.Difference(decoded, modelUnion).Sum(r => r.Area);
            Assert.True(symmetric <= tolerance * reference,
                $"layer '{layerFile.Layer}': recovered copper differs from the model by area {symmetric:g9} "
                + $"({symmetric / reference:g3} relative)");
        }
    }

    /// <summary>Asserts the Excellon drill hits recover exactly (to the file's precision) — position and
    /// diameter, matched as a multiset (Gerber/Excellon carry no order the model must preserve).</summary>
    private static void AssertDrillsRoundTrip(FabricationOutput output, PcbCopperModel model)
    {
        var decoded = ExcellonReader.Read(output.Drill);
        var modelHits = model.Drills.Select(d => new DrillHit(d.Center, d.Diameter)).ToList();
        Assert.Equal(modelHits.Count, decoded.Count);

        static IEnumerable<DrillHit> Sorted(IEnumerable<DrillHit> hits) =>
            hits.OrderBy(h => h.Diameter).ThenBy(h => h.Center.X).ThenBy(h => h.Center.Y);
        var a = Sorted(modelHits).ToList();
        var b = Sorted(decoded).ToList();

        double tol = 4 * output.Format.Decode(1);   // a few coordinate units — the file's own precision
        for (int i = 0; i < a.Count; i++)
        {
            Assert.True(Math.Abs(a[i].Diameter - b[i].Diameter) <= tol,
                $"drill diameter {b[i].Diameter:g9} recovered, model {a[i].Diameter:g9}");
            Assert.True(a[i].Center.DistanceTo(b[i].Center) <= tol,
                $"drill at {b[i].Center} recovered, model {a[i].Center}");
        }
    }

    private static void AssertFabricationRoundTrips(PcbLayout layout)
    {
        var model = PcbCopperModel.FromLayout(layout);
        var output = PcbGerberExport.Generate(layout);
        AssertCopperRoundTrips(output, model);
        AssertDrillsRoundTrip(output, model);
    }

    // ==== 1) a HAND-BUILT board round-trips ==================================

    [Fact]
    public void HandBuiltBoard_CopperAndDrillsRoundTrip()
    {
        // The stage-2 fixture: an SMD resistor (rectangular pads) top, a through-hole header
        // (round pads, drilled) top, two mounting holes. Rectangles → rect flashes, round pads/holes
        // → circle flashes, drills → Excellon.
        var layout = PcbFixtures.Layout();
        AssertFabricationRoundTrips(layout);

        // At normal scale the recovery is far tighter than the region-area grade — the flashes and
        // draws reproduce the copper to the coordinate quantization (~1e-8 of a millimetre).
        var model = PcbCopperModel.FromLayout(layout);
        AssertCopperRoundTrips(PcbGerberExport.Generate(layout), model, tolerance: 1e-7);

        // The bottom layer carries the through-hole pads (they appear on every copper layer); it is
        // real copper and round-trips too.
        Assert.Contains(model.Copper, f => f.Layer == "Bottom");
    }

    [Fact]
    public void HandBuiltBoard_GerberHasTheExpectedStructure()
    {
        var output = PcbGerberExport.Generate(PcbFixtures.Layout(), "blinky");
        var top = output.CopperLayers.Single(l => l.Layer == "Top").Gerber;

        Assert.StartsWith("%FSLA", top);                  // extended-Gerber format spec (RS-274X)
        Assert.Contains("%MOMM*%", top);                  // millimetre units
        Assert.Contains("%ADD", top);                     // an aperture table
        Assert.Contains("C,", top);                       // a circle aperture (the round header pads)
        Assert.Contains("R,", top);                       // a rectangle aperture (the SMD pads)
        Assert.Contains("D03*", top);                     // pads flash
        Assert.EndsWith("M02*\n", top);                   // and it ends
        Assert.Contains("T1C", output.Drill);             // the drill has a tool table
        Assert.Contains("M30", output.Drill);
    }

    // ==== 2) an AUTOROUTED board round-trips (traces + vias) =================

    [Fact]
    public void AutoroutedBoard_CopperAndDrillsRoundTrip()
    {
        // The routing doc's crossing board: GND straight across, SIG must cross via a via — so the
        // routed copper carries TRACES (draws) on two layers and a VIA (an annular pad + a drill).
        var (layout, _) = CrossingBoard();
        var routed = PcbRouter.Route(layout, Rules, RouterOpt);
        Assert.True(routed.FullyRouted);
        Assert.True(routed.ViasAdded >= 1);
        Assert.NotEmpty(routed.Layout.Traces);

        AssertFabricationRoundTrips(routed.Layout);
    }

    [Fact]
    public void AutoroutedBoard_TracesBecomeDrawsAndViaPadsAreFlashed()
    {
        var (layout, _) = CrossingBoard();
        var routed = PcbRouter.Route(layout, Rules, RouterOpt);
        var output = PcbGerberExport.Generate(routed.Layout);
        string top = output.CopperLayers.Single(l => l.Layer == "Top").Gerber;

        Assert.Contains("D01*", top);        // draws (the trace centre-line)
        Assert.Contains("C,0.9", top);       // the via annular pad (Ø 0.9) as a circle aperture

        // The board's vias are all covered — via1 by the trace that ends on it, via2 by the pad it
        // sits under (a via-in-pad). So the copper model's UNION has NO exposed drill on top, and the
        // Gerber correctly clears NOTHING there (a spurious clear would open a hole the model fills).
        Assert.DoesNotContain("%LPC*%", top);
    }

    [Fact]
    public void StitchingVia_KeepsItsAnnularRing_AndRoundTrips()
    {
        // A stitching via (no trace, no pad over it) IS an annular ring in the copper — a solid disc
        // with the drill cleared. It exercises the clear-polarity path.
        PartDefinition Tp(string name) => new(name, "TP",
            [new Pin("1", PinType.Passive)],
            new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));
        var sch = new Schematic("stitch");
        var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
        sch.Connect("GND", a.Pin("1"), b.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(0, 0), new(20, 0), new(20, 20), new(0, 20)], 1.6));
        layout.Place("A", 3, 3); layout.Place("B", 6, 3);
        layout.AddVia("GND", 15, 15, "Top", "Bottom", drill: 0.4, pad: 0.9);   // isolated, uncovered

        var output = PcbGerberExport.Generate(layout);
        string top = output.CopperLayers.Single(l => l.Layer == "Top").Gerber;
        Assert.Contains("%LPC*%", top);      // the exposed drill is cleared — an annular ring

        AssertFabricationRoundTrips(layout);
    }

    // ==== 3) determinism ======================================================

    [Fact]
    public void Deterministic_OneBoardYieldsByteIdenticalOutput()
    {
        var (layout, _) = CrossingBoard();
        var routed = PcbRouter.Route(layout, Rules, RouterOpt).Layout;

        var a = PcbGerberExport.Generate(routed, "board");
        var b = PcbGerberExport.Generate(routed, "board");
        for (int i = 0; i < a.CopperLayers.Count; i++)
            Assert.Equal(a.CopperLayers[i].Gerber, b.CopperLayers[i].Gerber);
        Assert.Equal(a.OutlineGerber, b.OutlineGerber);
        Assert.Equal(a.Drill, b.Drill);
    }

    // ==== 4) scale invariance (the epsilon-ladder property) ==================

    [Fact]
    public void ScaleInvariant_RoundTripsAt1000xAnd1eMinus3()
    {
        PcbLayout Build(double s)
        {
            PartDefinition Tp(string name) => new(name, "TP",
                [new Pin("1", PinType.Passive)],
                new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6 * s, 0.6 * s)]));

            var sch = new Schematic("scaled");
            var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
            sch.Connect("N", a.Pin("1"), b.Pin("1"));
            var layout = new PcbLayout(sch,
                new PcbBoard([new(0, 0), new(20 * s, 0), new(20 * s, 20 * s), new(0, 20 * s)], 1.6 * s));
            layout.Place("A", 4 * s, 10 * s);
            layout.Place("B", 16 * s, 10 * s);
            return PcbRouter.Route(layout, Rules.Scaled(s),
                RouterOpt with { GridResolution = s, TraceWidth = 0.4 * s, Clearance = 0.3 * s }).Layout;
        }

        AssertFabricationRoundTrips(Build(1e3));
        AssertFabricationRoundTrips(Build(1e-3));
    }

    // ==== 5) an EMPTY layer writes a valid, empty Gerber ======================

    [Fact]
    public void EmptyLayer_WritesAWellFormedEmptyGerber()
    {
        // An all-SMD, all-top board: nothing lands on the bottom copper. Its Gerber is still a
        // well-formed RS-274X file (headers + M02) and decodes to no copper.
        PartDefinition Tp(string name) => new(name, "TP",
            [new Pin("1", PinType.Passive)],
            new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));
        var sch = new Schematic("smd-only");
        var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
        sch.Connect("N", a.Pin("1"), b.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(0, 0), new(20, 0), new(20, 20), new(0, 20)], 1.6));
        layout.Place("A", 4, 10, side: CopperSide.Top);
        layout.Place("B", 16, 10, side: CopperSide.Top);

        var output = PcbGerberExport.Generate(layout);
        var bottom = output.CopperLayers.Single(l => l.Layer == "Bottom");

        Assert.Contains("%FSLA", bottom.Gerber);
        Assert.Contains("%MOMM*%", bottom.Gerber);
        Assert.EndsWith("M02*\n", bottom.Gerber);

        var decoded = GerberReader.Read(bottom.Gerber);
        Assert.Empty(decoded.Copper);                  // a valid, empty layer

        // And the whole board (including the empty bottom) still round-trips.
        AssertFabricationRoundTrips(layout);
    }

    // ==== 6) obround (oval) pads become obround apertures ====================

    [Fact]
    public void OvalPad_BecomesAnObroundAperture_AndRoundTrips()
    {
        var def = new PartDefinition("OVAL", "U", [new Pin("1", PinType.Passive)],
            new Footprint("oval_fp", [new Pad("1", new Vector2d(0, 0), 1.6, 0.8, PadShape.Oval)]));
        var sch = new Schematic("oval");
        sch.Add("U1", def);
        sch.Stub("TP", sch.Find("U1")!.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)], 1.6));
        layout.Place("U1", 0, 0);

        var output = PcbGerberExport.Generate(layout);
        string top = output.CopperLayers.Single(l => l.Layer == "Top").Gerber;
        Assert.Contains("O,", top);   // an obround aperture

        AssertFabricationRoundTrips(layout);
    }

    // ==== 7) a rotated pad falls to a region fill (exact for any shape) ======

    [Fact]
    public void RotatedRectPad_FallsToARegionFill_AndRoundTrips()
    {
        // A rectangular pad placed at 45° is no longer axis-aligned, so it is emitted as a region
        // fill (G36/G37) rather than a rectangle aperture — faithful for ANY orientation.
        var def = new PartDefinition("REC", "U", [new Pin("1", PinType.Passive)],
            new Footprint("rec_fp", [new Pad("1", new Vector2d(0, 0), 2.0, 1.0, PadShape.Rectangular)]));
        var sch = new Schematic("rot");
        sch.Add("U1", def);
        sch.Stub("TP", sch.Find("U1")!.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)], 1.6));
        layout.Place("U1", 0, 0, rotationDegrees: 45);

        var output = PcbGerberExport.Generate(layout);
        string top = output.CopperLayers.Single(l => l.Layer == "Top").Gerber;
        Assert.Contains("G36*", top);   // a region fill

        AssertFabricationRoundTrips(layout);
    }

    // ==== 8) a copper POUR (a hand-built region) round-trips as a region fill =

    [Fact]
    public void CopperPour_RoundTripsAsARegionFill()
    {
        // A hand-built copper model with a rectangular pour with a circular cut-out, and two pads on
        // top of it — the primitive a future pour feature will want, exercised through the model path.
        var board = new PcbBoard([new(-10, -10), new(10, -10), new(10, 10), new(-10, 10)], 1.6);
        var pour = new CurvedRegion2d(
            [
                CurvedEdge2d.Line(new(-8, -8), new(8, -8)), CurvedEdge2d.Line(new(8, -8), new(8, 8)),
                CurvedEdge2d.Line(new(8, 8), new(-8, 8)), CurvedEdge2d.Line(new(-8, 8), new(-8, -8)),
            ],
            [[CurvedEdge2d.Circle(new(0, 0), 2)]]);
        var model = new PcbCopperModel(board,
        [
            new CopperFeature("Top", "GND", "pour", pour),
            new CopperFeature("Top", "SIG", "P1", CurvedRegion2d.Disc(new(5, 5), 1)),
        ]);

        var output = PcbGerberExport.Generate(model);
        Assert.Contains("G36*", output.CopperLayers.Single(l => l.Layer == "Top").Gerber);
        AssertCopperRoundTrips(output, model);
    }

    // ==== 9) Write(layout, dir) writes files and reports ======================

    [Fact]
    public void WriteToDirectory_WritesFilesAndReports()
    {
        var layout = PcbFixtures.Layout();
        string dir = Path.Combine(Path.GetTempPath(), "engrcad-gerber-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = PcbGerberExport.Write(layout, dir, "blinky");

            Assert.Equal(2, result.CopperLayerCount);                  // Top + Bottom
            Assert.Equal(4, result.DrillHitCount);                     // 2 THT drills + 2 mounting holes
            Assert.All(result.Files, f => Assert.True(File.Exists(f)));
            Assert.Contains(result.Files, f => f.EndsWith("-Top.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith("-Edge_Cuts.gbr"));
            Assert.Contains(result.Files, f => f.EndsWith(".drl"));

            // The written files ARE the Generate() output (no drift between disk and text).
            var output = PcbGerberExport.Generate(layout, "blinky");
            string topPath = result.Files.Single(f => f.EndsWith("-Top.gbr"));
            Assert.Equal(output.CopperLayers.Single(l => l.Layer == "Top").Gerber, File.ReadAllText(topPath));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ==== 10) refusals — the guards shown to FIRE =============================

    [Fact]
    public void Reader_RefusesATruncatedGerberByName()
    {
        var output = PcbGerberExport.Generate(PcbFixtures.Layout());
        string top = output.CopperLayers.Single(l => l.Layer == "Top").Gerber;

        // Drop the end-of-file command: the reader must refuse rather than silently accept.
        string truncated = top.Replace("M02*\n", "");
        var ex = Assert.Throws<FormatException>(() => GerberReader.Read(truncated));
        Assert.Contains("M02", ex.Message);
    }

    [Fact]
    public void Reader_RefusesAMissingFormatSpecByName()
    {
        var output = PcbGerberExport.Generate(PcbFixtures.Layout());
        string top = output.CopperLayers.Single(l => l.Layer == "Top").Gerber;
        int fsEnd = top.IndexOf("*%\n", StringComparison.Ordinal) + 3;
        string noFs = top[fsEnd..];   // drop the %FS…% line

        var ex = Assert.Throws<FormatException>(() => GerberReader.Read(noFs));
        Assert.Contains("format", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reader_RefusesAnApertureMacroByName()
    {
        string gerber =
            "%FSLAX46Y46*%\n%MOMM*%\n%AMDONUT*1,1,0.5,0,0*%\nM02*\n";
        var ex = Assert.Throws<FormatException>(() => GerberReader.Read(gerber));
        Assert.Contains("macro", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writer_RefusesABezierCopperBoundaryByName()
    {
        // A copper region whose boundary carries a cubic Bézier cannot be an RS-274X region contour
        // (they hold only lines and circular arcs) — refused by name rather than silently flattened.
        var board = new PcbBoard([new(-5, -5), new(5, -5), new(5, 5), new(-5, 5)], 1.6);
        var curvy = new CurvedRegion2d(
        [
            CurvedEdge2d.Bezier(new(-2, -2), new(-1, 2), new(1, -2), new(2, 2)),
            CurvedEdge2d.Line(new(2, 2), new(2, -2)),
            CurvedEdge2d.Line(new(2, -2), new(-2, -2)),
        ]);
        var model = new PcbCopperModel(board, [new CopperFeature("Top", "N", "curvy", curvy)]);

        var ex = Assert.Throws<NotSupportedException>(() => PcbGerberExport.Generate(model));
        Assert.Contains("Bézier", ex.Message);
    }

    [Fact]
    public void ExcellonReader_RefusesATruncatedProgramByName()
    {
        var output = PcbGerberExport.Generate(PcbFixtures.Layout());
        string truncated = output.Drill.Replace("M30\n", "");
        var ex = Assert.Throws<FormatException>(() => ExcellonReader.Read(truncated));
        Assert.Contains("M30", ex.Message);
    }

    // ==== fixtures ============================================================

    private static (PcbLayout Layout, RouterOptions Options) CrossingBoard()
    {
        PartDefinition Tp(string name) => new(name, "TP",
            [new Pin("1", PinType.Passive)],
            new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));

        var sch = new Schematic("cross");
        var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
        var c = sch.Add("C", Tp("C")); var d = sch.Add("D", Tp("D"));
        sch.Connect("GND", a.Pin("1"), b.Pin("1"));
        sch.Connect("SIG", c.Pin("1"), d.Pin("1"));
        var layout = new PcbLayout(sch, new PcbBoard([new(0, 0), new(20, 0), new(20, 20), new(0, 20)], 1.6));
        layout.Place("A", 1, 10); layout.Place("B", 19, 10);
        layout.Place("C", 10, 1); layout.Place("D", 10, 19);
        return (layout, RouterOpt);
    }
}
