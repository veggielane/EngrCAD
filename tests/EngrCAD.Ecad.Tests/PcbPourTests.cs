using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Copper pours / ground planes. The bar is higher than usual (ECAD fails plausibly): a GND pour makes
/// every GND pin CONNECTED and the GND ratsnest EMPTY (the headline — a ground plane's whole purpose);
/// the poured board passes the DRC with the pour CLEARING every other net (the empty grown-intersection
/// is the proof); a same-net through-hole pad is CONNECTED through its relief spokes AND has an air GAP
/// (a relief that disconnects the pad is the classic bug, a pad that floods is the other); a walled-off
/// island is REMOVED and reported; the pour AREA is a stated function of board/clearance; the pour
/// EXPORTS to Gerber as a region fill and round-trips; persistence is a fixed point; every threshold is
/// RELATIVE (scale-invariant).
/// </summary>
public sealed class PcbPourTests
{
    // ---- fixtures -----------------------------------------------------------

    /// <summary>A 2-pin SMD part (1 × 1 square pads at ±1 mm).</summary>
    private static PartDefinition Smd2() => new(
        "R2", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("R2", [
            Pad.Smd("1", new Vector2d(-1.0, 0), 1.0, 1.0, PadShape.Rectangular),
            Pad.Smd("2", new Vector2d(1.0, 0), 1.0, 1.0, PadShape.Rectangular),
        ]));

    /// <summary>A 2-pin THROUGH-HOLE part (round Ø1.8 pads, Ø1.0 drills, at 0 and +5 mm).</summary>
    private static PartDefinition Tht2() => new(
        "J2", "J",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("J2", [
            Pad.ThroughHole("1", new Vector2d(0, 0), pad: 1.8, drill: 1.0),
            Pad.ThroughHole("2", new Vector2d(5, 0), pad: 1.8, drill: 1.0),
        ]));

    private static PcbBoard Board(double w = 40, double h = 30, double t = 1.6,
        IEnumerable<BoardHole>? holes = null) =>
        new(
            [
                new Vector2d(-w / 2, -h / 2), new Vector2d(w / 2, -h / 2),
                new Vector2d(w / 2, h / 2), new Vector2d(-w / 2, h / 2),
            ],
            t, holes: holes);

    // ---- 1) the headline: a GND pour connects every GND pin, ratsnest empty --

    [Fact]
    public void AGndPourConnectsEveryGndPinAndEmptiesTheRatsnest()
    {
        var sch = new Schematic("plane");
        var r1 = sch.Add("R1", Smd2());
        var r2 = sch.Add("R2", Smd2());
        var r3 = sch.Add("R3", Smd2());
        sch.Connect("GND", r1.Pin("1"), r2.Pin("1"), r3.Pin("1"));
        sch.Connect("VCC", r1.Pin("2"), r2.Pin("2"), r3.Pin("2"));

        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 0, 8);
        layout.Place("R2", 12, -6);
        layout.Place("R3", -12, -6);

        // BEFORE the pour: GND's three pads sit on three components — an unrouted ratsnest.
        Assert.Contains("GND", layout.Connectivity().Unrouted);
        Assert.False(layout.Connectivity().Of("GND").IsConnected);

        // AFTER the pour: the plane joins all three GND pads into ONE component.
        layout.AddPour(new CopperPour("GND", "Top"));

        var gnd = layout.Connectivity().Of("GND");
        Assert.True(gnd.IsConnected);
        Assert.Equal(1, gnd.ComponentCount);
        Assert.Equal(3, gnd.PadCount);                       // three pins, pours are connectors not pins
        Assert.DoesNotContain("GND", layout.Connectivity().Unrouted);
        Assert.DoesNotContain("GND", PcbDrc.Check(layout).Ratsnest);
    }

    // ---- 2) DRC clean: the pour clears every other net (proven) --------------

    [Fact]
    public void APouredBoardIsDrcCleanAndTheGrownIntersectionWithOtherNetsIsEmpty()
    {
        var sch = new Schematic("clean");
        var r1 = sch.Add("R1", Smd2());
        var r2 = sch.Add("R2", Smd2());
        sch.Connect("GND", r1.Pin("1"), r2.Pin("1"));
        sch.Connect("VCC", r1.Pin("2"), r2.Pin("2"));

        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 0, 6);
        layout.Place("R2", 6, -6);
        layout.AddPour(new CopperPour("GND", "Top"));

        var model = PcbCopperModel.FromLayout(layout);

        // No VIOLATIONS at all (the ratsnest for VCC is informational, not a fault).
        var report = PcbDrc.Check(model);
        Assert.True(report.Ok, report.ToString());

        // The tamper-mesh proof, direct: grow the GND pour and every VCC feature by half the DRC
        // clearance; the grown regions are DISJOINT (an empty intersection PROVES the clearance).
        const double c = 0.15;   // the DRC default
        var pour = model.Copper.Where(f => f.Net == "GND" && f.Source.StartsWith("pour")).Select(f => f.Region).ToList();
        var vcc = model.Copper.Where(f => f.Net == "VCC").Select(f => f.Region).ToList();
        Assert.NotEmpty(pour);
        Assert.NotEmpty(vcc);
        Assert.Empty(CurvedRegion2dBoolean.Intersection(
            CurvedRegion2dOffset.Offset(pour, c / 2),
            CurvedRegion2dOffset.Offset(vcc, c / 2)));
    }

    // ---- 3) thermal relief: a THT pad is CONNECTED and has an air GAP --------

    [Fact]
    public void AThroughHolePadIsConnectedThroughItsReliefSpokesAndHasAnAirGap()
    {
        var sch = new Schematic("relief");
        var j = sch.Add("J1", Tht2());
        var r = sch.Add("R1", Smd2());
        sch.Connect("GND", j.Pin("1"), r.Pin("1"));
        sch.Connect("VCC", j.Pin("2"), r.Pin("2"));

        var layout = new PcbLayout(sch, Board());
        layout.Place("J1", 0, 0);                            // J1.1 (GND, THT) at world (0, 0)
        layout.Place("R1", -12, 0);                          // a second GND pad, so GND has two pins
        layout.AddPour(new CopperPour("GND", "Top"));        // default relief: 4 diagonal spokes

        // CONNECTED: the plane joins the THT GND pad to the rest of GND through its spokes.
        var gnd = layout.Connectivity().Of("GND");
        Assert.True(gnd.IsConnected);
        Assert.Equal(1, gnd.ComponentCount);

        // The GAP: a point in the annular relief, BETWEEN two spokes, carries NO copper (pad Ø1.8 → r
        // 0.9, gap 0.4 → the gap spans r ∈ (0.9, 1.3); spokes are on the diagonals, so +X is midway
        // between them). It is neither pad nor pour nor spoke.
        var gapPoint = new Vector2d(1.1, 0);
        var model = PcbCopperModel.FromLayout(layout);
        Assert.DoesNotContain(model.Copper.Where(f => f.Layer == "Top"), f => f.Region.Contains(gapPoint));

        // But a point ON a spoke (a diagonal, mid-gap) IS pour copper — the spoke bridges the gap.
        var spokePoint = new Vector2d(1.1 / Math.Sqrt(2), 1.1 / Math.Sqrt(2));
        Assert.Contains(model.Copper.Where(f => f.Layer == "Top" && f.Source.StartsWith("pour")),
            f => f.Region.Contains(spokePoint));

        // And the whole board is DRC-clean under a realistic acid-trap threshold (thermal-relief spokes
        // meet the plane at ~90° corners, which pass any threshold at or below 90°).
        Assert.True(PcbDrc.Check(model, DrcRuleSet.Default with { MinAcuteAngleDegrees = 45 }).Ok);
    }

    [Fact]
    public void DirectConnectFloodsAThroughHolePadWithNoRelief()
    {
        // ThermalRelief.None floods over the THT pad — the pour covers the pad area, no gap.
        var sch = new Schematic("flood");
        var j = sch.Add("J1", Tht2());
        var r = sch.Add("R1", Smd2());
        sch.Connect("GND", j.Pin("1"), r.Pin("1"));
        sch.Connect("VCC", j.Pin("2"), r.Pin("2"));

        var layout = new PcbLayout(sch, Board());
        layout.Place("J1", 0, 0);
        layout.Place("R1", -12, 0);
        layout.AddPour(new CopperPour("GND", "Top", Relief: ThermalRelief.None));

        var model = PcbCopperModel.FromLayout(layout);
        // The point that was an air gap under relief is now flooded pour copper.
        Assert.Contains(model.Copper.Where(f => f.Layer == "Top" && f.Source.StartsWith("pour")),
            f => f.Region.Contains(new Vector2d(1.1, 0)));
        Assert.True(layout.Connectivity().Of("GND").IsConnected);
    }

    // ---- 4) an isolated island is removed (and reported) --------------------

    [Fact]
    public void AWalledOffIslandIsRemovedAndReported()
    {
        // A VCC bar runs the full width of the board, splitting a GND pour into a top half (with a GND
        // pad) and a bottom half (with none). The bottom half is DEAD copper — removed, reported.
        var sch = new Schematic("island");
        var r = sch.Add("R1", Smd2());
        var v = sch.Add("R2", Smd2());
        sch.Connect("GND", r.Pin("1"), r.Pin("2"));   // both R1 pads on GND (top half)
        sch.Connect("VCC", v.Pin("1"), v.Pin("2"));   // both R2 pads on VCC (the bar's net)

        var board = Board(w: 40, h: 20);
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", 0, 6);                     // GND pads in the TOP half
        layout.Place("R2", 15, 0);                    // VCC pads (the trace's net)
        // A VCC trace spanning the whole board width at y = 0 — grown by clearance it separates the
        // pour into two components.
        layout.AddTrace("VCC", "Top", 0.6,
            [new Vector2d(-21, 0), new Vector2d(21, 0)]);

        var pour = new CopperPour("GND", "Top");
        var baseModel = PcbCopperModel.FromLayout(layout);   // no pour yet

        var removed = CopperPourBuilder.Fill(baseModel, pour);
        Assert.True(removed.DeadCopperRegions >= 1);
        Assert.True(removed.DeadCopperArea > 0);
        // The kept fill does NOT cover the bottom half.
        Assert.DoesNotContain(removed.Regions, region => region.Contains(new Vector2d(0, -6)));
        Assert.Contains(removed.Regions, region => region.Contains(new Vector2d(0, 6)));

        // KEEPING the dead copper is the opt-in: the bottom half is then filled.
        var kept = CopperPourBuilder.Fill(baseModel, pour with { DeadCopper = DeadCopperPolicy.Keep });
        Assert.True(kept.DeadCopperRegions >= 1);            // still reported
        Assert.Contains(kept.Regions, region => region.Contains(new Vector2d(0, -6)));
        Assert.True(kept.Area > removed.Area);               // the bottom half is now included
    }

    // ---- 5) the pour area is a stated function of board / clearance ----------

    [Fact]
    public void ThePourAreaIsBoardInsetLessTheClearedMountingHole()
    {
        // A bare GND pour over a 40 × 30 board with one central Ø3 mounting hole. The only components
        // are GND (both pins GND, so every pad is flooded — same net — and changes no area). The closed
        // form is then exact:  area = (W − 2·edge)(H − 2·edge) − π(holeR + drillClearance)².
        const double w = 40, h = 30, edge = 0.3, drillClear = 0.25, holeR = 1.5;
        var board = Board(w, h, holes: [new BoardHole(new Vector2d(0, 0), 2 * holeR, BoardHoleKind.Mounting)]);

        var sch = new Schematic("area");
        var r1 = sch.Add("R1", Smd2());
        var r2 = sch.Add("R2", Smd2());
        sch.Connect("GND", r1.Pin("1"), r1.Pin("2"), r2.Pin("1"), r2.Pin("2"));

        var layout = new PcbLayout(sch, board);
        layout.Place("R1", -12, 8);
        layout.Place("R2", 12, -8);

        var pour = CopperPourBuilder.Fill(PcbCopperModel.FromLayout(layout),
            new CopperPour("GND", "Top", EdgeClearance: edge, DrillClearance: drillClear));

        double expected = (w - 2 * edge) * (h - 2 * edge) - Math.PI * Math.Pow(holeR + drillClear, 2);
        Assert.Single(pour.Regions);
        Assert.Equal(expected, pour.Area, 6);
    }

    // ---- 6) determinism -----------------------------------------------------

    [Fact]
    public void ThePourIsDeterministic()
    {
        var layout = HeadlineLayout();
        var a = CopperPourBuilder.Fill(PcbCopperModel.FromLayout(layout), new CopperPour("GND", "Top"));
        var b = CopperPourBuilder.Fill(PcbCopperModel.FromLayout(layout), new CopperPour("GND", "Top"));

        Assert.Equal(a.Regions.Count, b.Regions.Count);
        // Bit-identical areas — the pour is a pure function of the board and its declaration.
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Area), BitConverter.DoubleToInt64Bits(b.Area));
    }

    // ---- 7) the pour exports to Gerber and round-trips ----------------------

    [Fact]
    public void APouredBoardExportsToGerberAndTheCopperRoundTrips()
    {
        var layout = HeadlineLayout();
        layout.AddPour(new CopperPour("GND", "Top"));

        var model = PcbCopperModel.FromLayout(layout);
        Assert.Contains(model.Copper, f => f.Source.StartsWith("pour"));   // the pour is real copper

        var output = PcbGerberExport.Generate(layout);

        // The Top layer's copper — pads + the region-filled pour — decodes back to the same copper by
        // AREA (the twin-decoder round-trip oracle; a pour is a G36/G37 region fill).
        var top = output.CopperLayers.Single(l => l.Layer == "Top");
        var decoded = GerberReader.Read(top.Gerber).Copper;
        var modelUnion = CurvedRegion2dBoolean.UnionAll(
            [.. model.Copper.Where(f => f.Layer == "Top").Select(f => f.Region)]);

        double modelArea = modelUnion.Sum(r => r.Area);
        double decodedArea = decoded.Sum(r => r.Area);
        Assert.True(modelArea > 100, "the pour is most of the board's copper");
        Assert.Equal(modelArea, decodedArea, 3);

        // The strong form: the recovered copper IS the model's copper (symmetric difference tiny).
        double symmetric =
            CurvedRegion2dBoolean.Difference(modelUnion, decoded).Sum(r => r.Area)
            + CurvedRegion2dBoolean.Difference(decoded, modelUnion).Sum(r => r.Area);
        Assert.True(symmetric <= 1e-3 * modelArea, $"symmetric difference {symmetric:g6}");
    }

    // ---- 8) persistence: a fixed point, no-pour byte-identical ---------------

    [Fact]
    public void APouredLayoutIsASaveLoadSaveFixedPointAndAPourFreeOneIsUnchanged()
    {
        var layout = HeadlineLayout();

        // Byte-identical before any pour is added (write-only-when-stated).
        string before = layout.Save();
        Assert.DoesNotContain("pours", before);

        // A pour with a stated outline, hatch and custom relief exercises every persisted field.
        layout.AddPour(new CopperPour(
            "GND", "Top",
            Outline: [new Vector2d(-15, -10), new Vector2d(15, -10), new Vector2d(15, 10), new Vector2d(-15, 10)],
            Fill: PourFill.Hatched,
            Clearance: 0.25, DrillClearance: 0.3, EdgeClearance: 0.4,
            Relief: new ThermalRelief(Spokes: 6, SpokeWidth: 0.6, Gap: 0.5, StartAngleDegrees: 30),
            Hatch: new HatchStyle(Spacing: 1.2, LineWidth: 0.35, AngleDegrees: 30, CrossHatch: false),
            DeadCopper: DeadCopperPolicy.Keep));

        string once = layout.Save();
        Assert.Contains("pours", once);
        var reloaded = PcbLayout.Load(once);
        string twice = reloaded.Save();
        Assert.Equal(once, twice);                          // the fixed point

        // The pour survived every field.
        var pour = Assert.Single(reloaded.Pours);
        Assert.Equal(PourFill.Hatched, pour.Fill);
        Assert.Equal(4, pour.Outline!.Count);
        Assert.Equal(6, pour.Relief!.Spokes);
        Assert.Equal(1.2, pour.Hatch!.Spacing);
        Assert.False(pour.Hatch.CrossHatch);
        Assert.Equal(DeadCopperPolicy.Keep, pour.DeadCopper);
    }

    // ---- 9) refusals by name ------------------------------------------------

    [Fact]
    public void ARefusalNamesTheOffendingNetLayerOrOutline()
    {
        var layout = HeadlineLayout();

        var noNet = Assert.Throws<ArgumentException>(() => layout.AddPour(new CopperPour("PLASMA", "Top")));
        Assert.Contains("PLASMA", noNet.Message);

        var noLayer = Assert.Throws<ArgumentException>(() => layout.AddPour(new CopperPour("GND", "In42")));
        Assert.Contains("In42", noLayer.Message);

        var offBoard = Assert.Throws<ArgumentException>(() => layout.AddPour(new CopperPour(
            "GND", "Top",
            Outline: [new Vector2d(500, 500), new Vector2d(600, 500), new Vector2d(600, 600)])));
        Assert.Contains("off the board", offBoard.Message);

        var badClearance = Assert.Throws<ArgumentException>(() =>
            layout.AddPour(new CopperPour("GND", "Top", Clearance: -1)));
        Assert.Contains("clearance", badClearance.Message);
    }

    // ---- 10) hatched fill is the region ∩ a grid ----------------------------

    [Fact]
    public void AHatchedPourIsLighterThanASolidOneOverTheSameRegion()
    {
        var layout = HeadlineLayout();
        var baseModel = PcbCopperModel.FromLayout(layout);

        var solid = CopperPourBuilder.Fill(baseModel, new CopperPour("GND", "Top"));
        var hatched = CopperPourBuilder.Fill(baseModel, new CopperPour("GND", "Top", Fill: PourFill.Hatched));

        // A crosshatch removes copper, so it is strictly lighter than the solid pour over the same
        // region — it is the region intersected with a grid.
        Assert.True(hatched.Area > 0);
        Assert.True(hatched.Area < solid.Area,
            $"hatched {hatched.Area:g6} should be lighter than solid {solid.Area:g6}");
    }

    // ---- 11) scale invariance ----------------------------------------------

    [Fact]
    public void ThePourScalesWithTheBoard()
    {
        // The board, its mounting hole and the pour clearances all scale by s (the footprint pads are
        // GND — flooded — so they change no area); the pour area is then exactly s² times the 1× area,
        // the relative-construction / epsilon-ladder property the whole DRC rests on.
        double AreaAt(double s)
        {
            var sch = new Schematic("scale");
            var r = sch.Add("R1", Smd2());
            sch.Connect("GND", r.Pin("1"), r.Pin("2"));
            var board = Board(40 * s, 30 * s, 1.6 * s,
                holes: [new BoardHole(new Vector2d(0, 0), 3 * s, BoardHoleKind.Mounting)]);
            var layout = new PcbLayout(sch, board);
            layout.Place("R1", -8 * s, 5 * s);
            var pour = CopperPourBuilder.Fill(PcbCopperModel.FromLayout(layout),
                new CopperPour("GND", "Top",
                    Clearance: 0.2 * s, DrillClearance: 0.25 * s, EdgeClearance: 0.3 * s));
            return pour.Area;
        }

        double a1 = AreaAt(1);
        double a1000 = AreaAt(1000);
        Assert.True(a1 > 0 && a1000 > 0);
        Assert.True(Math.Abs(a1000 / a1 - 1e6) < 1e6 * 1e-6, $"ratio {a1000 / a1:g9}, expected 1e6");
    }

    // ---- shared -------------------------------------------------------------

    private static PcbLayout HeadlineLayout()
    {
        var sch = new Schematic("plane");
        var r1 = sch.Add("R1", Smd2());
        var r2 = sch.Add("R2", Smd2());
        var r3 = sch.Add("R3", Smd2());
        sch.Connect("GND", r1.Pin("1"), r2.Pin("1"), r3.Pin("1"));
        sch.Connect("VCC", r1.Pin("2"), r2.Pin("2"), r3.Pin("2"));
        var layout = new PcbLayout(sch, Board());
        layout.Place("R1", 0, 8);
        layout.Place("R2", 12, -6);
        layout.Place("R3", -12, -6);
        return layout;
    }
}
