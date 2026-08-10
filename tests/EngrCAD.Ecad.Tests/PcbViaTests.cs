using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Vias, the net-connectivity engine, and the via DRC rules. The connectivity engine is the heart —
/// it CLOSES the multilayer caveat ("a net whose pads sit on different layers reads as an unrouted
/// ratsnest until routing") by making a via a real cross-layer connection: a net with pads on two
/// layers, tied by a via that touches each, is CONNECTED. The bar is higher than usual because ECAD
/// fails plausibly, so every oracle is checked from BOTH sides and against a closed form.
/// </summary>
public class PcbViaTests
{
    // ---- fixtures ------------------------------------------------------------

    // A 4-layer board: copper planes Top / In1 / In2 / Bottom.
    private static PcbBoard FourLayer() =>
        PcbBoard.Rectangle(40, 30, LayerStackup.FourLayer(copper: 0.035, prepreg: 0.2, core: 1.13));

    // A 6-layer board: Top / In1 / In2 / In3 / In4 / Bottom — enough to exercise every via kind.
    private static PcbBoard SixLayer() =>
        PcbBoard.Rectangle(40, 30, LayerStackup.SixLayer(copper: 0.035, prepreg: 0.2, core: 1.0));

    // A part with ONE round SMD pad at its origin (clean disc geometry).
    private static PartDefinition OnePad(string name = "PAD") => new(
        name, "U",
        [new Pin("1", PinType.Passive)],
        new Footprint(name, [Pad.Smd("1", new Vector2d(0, 0), 1.2, 1.2, PadShape.Round)]));

    // ---- via type is DERIVED from the span -----------------------------------

    [Theory]
    // 6-layer: Top(0) In1(1) In2(2) In3(3) In4(4) Bottom(5).
    [InlineData("Top", "Bottom", ViaType.Through)]     // outer to outer — the whole stack
    [InlineData("Top", "In1", ViaType.Microvia)]       // a single dielectric hop from a face
    [InlineData("Top", "In2", ViaType.Blind)]          // outer to inner, two dielectrics
    [InlineData("In1", "In2", ViaType.Microvia)]       // adjacent inner layers
    [InlineData("In2", "In3", ViaType.Microvia)]
    [InlineData("In4", "Bottom", ViaType.Microvia)]    // adjacent, one outer — still a microvia
    [InlineData("In1", "In4", ViaType.Buried)]         // inner to inner, not adjacent
    [InlineData("In2", "In4", ViaType.Buried)]
    public void AViaTypeIsDerivedFromItsSpan(string from, string to, ViaType expected)
    {
        var layout = new PcbLayout(new Schematic("t"), SixLayer());
        var via = new Via("N", 0, 0, from, to, 0.3, 0.6);
        Assert.Equal(expected, layout.ViaTypeOf(via));
        // Order does not matter — the span is the contiguous copper range.
        Assert.Equal(expected, layout.ViaTypeOf(via with { FromLayer = to, ToLayer = from }));
    }

    [Fact]
    public void AThroughViaTouchesEveryCopperLayerAndABuriedViaOnlyItsSpan()
    {
        var layout = new PcbLayout(new Schematic("t"), SixLayer());   // 6 copper layers

        var through = new Via("N", 0, 0, "Top", "Bottom", 0.3, 0.6);
        Assert.Equal(["Top", "In1", "In2", "In3", "In4", "Bottom"], layout.ViaLayers(through));

        var buried = new Via("N", 0, 0, "In2", "In4", 0.3, 0.6);
        Assert.Equal(["In2", "In3", "In4"], layout.ViaLayers(buried));
    }

    // ---- via geometry: the annular pad on each touched layer -----------------

    [Fact]
    public void AViaPlacesAnAnnularPadOfExactAreaOnEveryTouchedLayer()
    {
        var layout = new PcbLayout(new Schematic("t"), FourLayer());
        layout.AddVia("N", 3, 4, "Top", "Bottom", drill: 0.4, pad: 1.0);   // through: all 4 layers

        var model = PcbCopperModel.FromLayout(layout);
        var placed = Assert.Single(model.Vias);
        Assert.Equal(ViaType.Through, placed.Type);
        Assert.Equal(["Top", "In1", "In2", "Bottom"], placed.Layers);

        // One annular pad copper feature per touched layer, tagged with the via's net + source.
        var viaCopper = model.Copper.Where(f => f.Source == placed.Source).ToList();
        Assert.Equal(4, viaCopper.Count);
        Assert.All(viaCopper, f => Assert.Equal("N", f.Net));
        Assert.Equal(["Bottom", "In1", "In2", "Top"], viaCopper.Select(f => f.Layer).OrderBy(s => s));

        // The annular pad area is EXACTLY π(pad² − drill²)/4 (a disc of the pad diameter with the
        // drill removed), on every touched layer.
        double expectedArea = Math.PI * (1.0 * 1.0 - 0.4 * 0.4) / 4;
        Assert.Equal(expectedArea, placed.AnnularPadArea, 12);
        foreach (var f in viaCopper)
            Assert.Equal(expectedArea, f.Region.Area, 9);
    }

    [Fact]
    public void ABuriedViaTouchesOnlyItsInnerSpan()
    {
        var layout = new PcbLayout(new Schematic("t"), SixLayer());
        layout.AddVia("N", 0, 0, "In2", "In4", drill: 0.3, pad: 0.6);   // buried, In2..In4

        var model = PcbCopperModel.FromLayout(layout);
        var placed = Assert.Single(model.Vias);
        Assert.Equal(ViaType.Buried, placed.Type);
        var layers = model.Copper.Where(f => f.Source == placed.Source).Select(f => f.Layer).OrderBy(s => s);
        Assert.Equal(["In2", "In3", "In4"], layers);   // ONLY the inner span — not Top/In1/Bottom
    }

    // ---- via refusals (by name) ----------------------------------------------

    [Fact]
    public void AViaWithNoNetIsRefusedByName()
    {
        var layout = new PcbLayout(new Schematic("t"), FourLayer());
        var ex = Assert.Throws<ArgumentException>(() => layout.AddVia("", 0, 0, "Top", "Bottom", 0.3, 0.6));
        Assert.Contains("no net", ex.Message);
    }

    [Fact]
    public void AViaOffTheBoardOutlineIsRefusedByName()
    {
        var layout = new PcbLayout(new Schematic("t"), FourLayer());   // 40 × 30, centred
        var ex = Assert.Throws<ArgumentException>(() => layout.AddVia("N", 100, 0, "Top", "Bottom", 0.3, 0.6));
        Assert.Contains("off the board outline", ex.Message);
    }

    [Fact]
    public void AViaSpanningANonexistentLayerIsRefusedByName()
    {
        var layout = new PcbLayout(new Schematic("t"), FourLayer());
        var ex = Assert.Throws<ArgumentException>(() => layout.AddVia("N", 0, 0, "Top", "In7", 0.3, 0.6));
        Assert.Contains("In7", ex.Message);
        Assert.Contains("not a copper layer", ex.Message);
    }

    [Fact]
    public void AViaWhoseEndsAreTheSameLayerIsRefusedByName()
    {
        var layout = new PcbLayout(new Schematic("t"), FourLayer());
        var ex = Assert.Throws<ArgumentException>(() => layout.AddVia("N", 0, 0, "Top", "Top", 0.3, 0.6));
        Assert.Contains("at least two copper layers", ex.Message);
    }

    [Fact]
    public void AMicroviaAcrossNonAdjacentLayersIsRefusedByName()
    {
        var layout = new PcbLayout(new Schematic("t"), SixLayer());
        // Top..In2 is a BLIND via (two dielectrics); asking for a microvia there is refused by name.
        var ex = Assert.Throws<ArgumentException>(
            () => layout.AddVia("N", 0, 0, "Top", "In2", 0.3, 0.6, require: ViaType.Microvia));
        Assert.Contains("Microvia", ex.Message);
        Assert.Contains("Blind", ex.Message);
        // The adjacent hop Top..In1 IS a microvia and passes the assertion.
        layout.AddVia("N", 0, 0, "Top", "In1", 0.3, 0.6, require: ViaType.Microvia);
        Assert.Single(layout.Vias);
    }

    [Fact]
    public void AViaBreachingAnEmbeddedCavityIsRefusedByName()
    {
        var die = new PartDefinition("DIE", "U",
            [new Pin("1", PinType.Passive)],
            new Footprint("DIE", [Pad.Smd("1", new Vector2d(0, 0), 1.4, 2.0, PadShape.Round)]),
            body: () => Shape.Box(4.0, 2.5, 0.5).Translate(0, 0, 0.25));
        var sch = new Schematic("t");
        sch.Add("U1", die);
        var layout = new PcbLayout(sch, FourLayer());
        layout.Embed("U1", "In2", 0, 0, cavityClearance: 0.15);   // a milled cavity around (0, 0)

        var ex = Assert.Throws<ArgumentException>(() => layout.AddVia("N", 0, 0, "Top", "Bottom", 0.3, 0.6));
        Assert.Contains("cavity", ex.Message);
        // A via clear of the cavity is fine.
        layout.AddVia("N", 15, 0, "Top", "Bottom", 0.3, 0.6);
        Assert.Single(layout.Vias);
    }

    [Fact]
    public void AViaWhosePadIsNotLargerThanItsDrillIsRefused()
    {
        var layout = new PcbLayout(new Schematic("t"), FourLayer());
        var ex = Assert.Throws<ArgumentException>(() => layout.AddVia("N", 0, 0, "Top", "Bottom", 0.5, 0.5));
        Assert.Contains("annular ring", ex.Message);
    }

    // ---- the connectivity engine — THE HEART ---------------------------------

    // The headline: a net with a pad on one layer and a pad on another, tied by a via that touches
    // each, is CONNECTED and NOT in the ratsnest — the caveat the multilayer stage left open, closed.
    private static PcbLayout TwoPadNet(bool withVia)
    {
        var sch = new Schematic("t");
        var a = sch.Add("A1", OnePad());
        var b = sch.Add("B1", OnePad());
        sch.Connect("N", a.Pin("1"), b.Pin("1"));

        var layout = new PcbLayout(sch, FourLayer());
        layout.Place("A1", 0, 0, side: CopperSide.Top);      // pad on Top at (0, 0)
        layout.Place("B1", 0, 0, side: CopperSide.Bottom);   // pad on Bottom at (0, 0), same (x, y)
        if (withVia)
            layout.AddVia("N", 0, 0, "Top", "Bottom", drill: 0.4, pad: 1.0);   // ties the two layers
        return layout;
    }

    [Fact]
    public void AViaConnectsTwoPadsOnDifferentLayers_TheClosedCaveat()
    {
        var layout = TwoPadNet(withVia: true);

        var net = PcbConnectivity.For(PcbCopperModel.FromLayout(layout), "N");
        Assert.True(net.IsConnected);
        Assert.Equal(1, net.ComponentCount);
        Assert.Equal(2, net.PadCount);
        Assert.True(layout.IsNetConnected("N"));

        // The DRC ratsnest is EMPTY for it — the via routed the cross-layer net.
        Assert.Empty(PcbDrc.Check(layout).Ratsnest);
    }

    [Fact]
    public void WithoutTheViaTheSameNetIsUnconnected()
    {
        var layout = TwoPadNet(withVia: false);

        var net = PcbConnectivity.For(PcbCopperModel.FromLayout(layout), "N");
        Assert.False(net.IsConnected);
        Assert.Equal(2, net.ComponentCount);     // Top pad and Bottom pad are two islands
        Assert.False(layout.IsNetConnected("N"));

        Assert.Equal(["N"], PcbDrc.Check(layout).Ratsnest);
    }

    [Fact]
    public void AViaOnTheWrongNetDoesNotConnectTheNet()
    {
        var sch = new Schematic("t");
        var a = sch.Add("A1", OnePad());
        var b = sch.Add("B1", OnePad());
        sch.Connect("N", a.Pin("1"), b.Pin("1"));

        var layout = new PcbLayout(sch, FourLayer());
        layout.Place("A1", 0, 0, side: CopperSide.Top);
        layout.Place("B1", 0, 0, side: CopperSide.Bottom);
        // A via on a DIFFERENT net (its own net string, not a schematic net), right where a
        // connecting via would be — it does not join net N.
        layout.AddVia("OTHER", 0, 0, "Top", "Bottom", drill: 0.4, pad: 1.0);

        Assert.False(layout.IsNetConnected("N"));                 // still two islands
        Assert.Equal(["N"], PcbConnectivity.Analyze(layout).Unrouted);
    }

    [Fact]
    public void AViaAtAThirdLocationDoesNotMagicallyConnectDistantSameLayerPads()
    {
        // Two SAME-net pads on ONE layer that do not touch stay unconnected — a via elsewhere on the
        // net does not bridge them unless its copper reaches them.
        var sch = new Schematic("t");
        var a = sch.Add("A1", OnePad());
        var b = sch.Add("B1", OnePad());
        sch.Connect("N", a.Pin("1"), b.Pin("1"));

        var layout = new PcbLayout(sch, FourLayer());
        layout.Place("A1", -10, 0, side: CopperSide.Top);
        layout.Place("B1", 10, 0, side: CopperSide.Top);         // far apart, same layer
        layout.AddVia("N", 0, 8, "Top", "Bottom", drill: 0.4, pad: 1.0);   // a third location, touches neither

        Assert.False(layout.IsNetConnected("N"));
        Assert.Equal(["N"], PcbConnectivity.Analyze(layout).Unrouted);
    }

    [Fact]
    public void ConnectivityIsExactRegionTouchWithNoTolerance()
    {
        // Two Ø1 pads of one net on one layer. Overlapping copper joins them; exactly tangent (a
        // point, no region) does not — the exact region-touch the DRC's short test uses.
        var board = PcbBoard.Rectangle(40, 30, 1.6);
        CopperFeature Pad(string src, double x) =>
            new("Top", "N", src, CurvedRegion2d.Disc(new Vector2d(x, 0), 0.5));

        var overlapping = new PcbCopperModel(board, [Pad("P1", 0), Pad("P2", 0.9)]);   // centres 0.9 < 1.0
        Assert.True(PcbConnectivity.For(overlapping, "N").IsConnected);

        var tangent = new PcbCopperModel(board, [Pad("P1", 0), Pad("P2", 1.0)]);       // centres exactly 1.0
        Assert.False(PcbConnectivity.For(tangent, "N").IsConnected);
    }

    [Fact]
    public void ABlindViaConnectsATopPadToAnInnerLayerPad()
    {
        // On a 4-layer board (Top / In1 / In2 / Bottom), a BLIND via ties a Top pad to a pad on the
        // inner layer In2 — a non-through via reaching an inner layer, the multilayer connectivity case.
        var board = FourLayer();
        var via = new Via("N", 0, 0, "Top", "In2", 0.3, 1.0);
        var annulus = ViaGeometry.AnnularPad(new Vector2d(0, 0), 1.0, 0.3);
        var model = new PcbCopperModel(
            board,
            [
                new CopperFeature("Top", "N", "P1", CurvedRegion2d.Disc(new Vector2d(0, 0), 0.6)),
                new CopperFeature("In2", "N", "P2", CurvedRegion2d.Disc(new Vector2d(0, 0), 0.6)),
                new CopperFeature("Top", "N", "via1", annulus),
                new CopperFeature("In1", "N", "via1", annulus),   // the barrel passes through In1
                new CopperFeature("In2", "N", "via1", annulus),
            ],
            vias: [new PlacedVia(via, "via1", ViaType.Blind, ["Top", "In1", "In2"])]);

        var net = PcbConnectivity.For(model, "N");
        Assert.True(net.IsConnected);
        Assert.Equal(1, net.ComponentCount);
        Assert.Equal(ViaType.Blind, new PcbLayout(new Schematic("t"), board).ViaTypeOf(via));
    }

    [Fact]
    public void AFloatingViaNeverMakesAConnectedNetReadUnconnected()
    {
        // A net whose pads are already joined stays connected even with a redundant via that touches
        // nothing — via pads are connectors, not terminals to be reached.
        var board = PcbBoard.Rectangle(40, 30, 1.6);
        var placed = new Via("N", 15, 10, "Top", "Bottom", 0.4, 1.0);
        var model = new PcbCopperModel(
            board,
            [
                new CopperFeature("Top", "N", "P1", CurvedRegion2d.Disc(new Vector2d(0, 0), 0.6)),
                new CopperFeature("Top", "N", "P2", CurvedRegion2d.Disc(new Vector2d(0.9, 0), 0.6)),  // touch P1
                new CopperFeature("Top", "N", "via1", ViaGeometry.AnnularPad(new Vector2d(15, 10), 1.0, 0.4)),
                new CopperFeature("Bottom", "N", "via1", ViaGeometry.AnnularPad(new Vector2d(15, 10), 1.0, 0.4)),
            ],
            vias: [new PlacedVia(placed, "via1", ViaType.Through, ["Top", "Bottom"])]);

        var net = PcbConnectivity.For(model, "N");
        Assert.True(net.IsConnected);          // P1 and P2 touch; the far via is irrelevant
        Assert.Equal(2, net.PadCount);         // only the two component pads count as terminals
    }

    // ---- via DRC rules -------------------------------------------------------

    [Theory]
    [InlineData(1.30, false)]   // ring (1.6−1.30)/2 = 0.15 = min → passes (a via IS a drilled pad)
    [InlineData(1.32, true)]    // ring (1.6−1.32)/2 = 0.14 < 0.15 → violation
    public void ViaAnnularRingIsFoundBelowTheLimitAndPassesAtOrAbove(double drill, bool violation)
    {
        // A via reuses the existing annular-ring rule (a via is a drilled pad), so the boundary is
        // clean the same way — pad fixed at 1.6, the drill varying.
        var rules = DrcRuleSet.Default with { MinAnnularRing = 0.15 };
        var layout = new PcbLayout(new Schematic("t"), FourLayer());
        layout.AddVia("N", 0, 0, "Top", "Bottom", drill: drill, pad: 1.6);

        var report = PcbDrc.Check(layout, rules);
        Assert.Equal(violation, report.Has(DrcRule.AnnularRing));
        if (violation)
        {
            var hit = Assert.Single(report.OfRule(DrcRule.AnnularRing));
            Assert.Contains("via1", hit.Message);
            Assert.Equal((1.6 - drill) / 2, hit.Measured, 12);
        }
    }

    [Theory]
    [InlineData(1.10, true)]    // gap 1.10 − 0.5 − 0.5 = 0.10 < 0.15 → clearance violation
    [InlineData(1.20, false)]   // gap 0.20 ≥ 0.15 → passes
    public void ViaPadToOtherNetCopperClearanceIsFoundFromBothSides(double centerX, bool violation)
    {
        // A via (net V, pad Ø1 → radius 0.5) near a Ø1 pad of a DIFFERENT net on the SAME layer. The
        // via annular pad is ordinary copper, so the general clearance rule reaches it — a via-to-
        // copper clearance rides the copper-clearance rule, since a via pad IS copper. Gap = centre
        // distance − 0.5 (via pad radius) − 0.5 (pad radius).
        var rules = DrcRuleSet.Default with { MinCopperClearance = 0.15 };
        var board = PcbBoard.Rectangle(40, 30, 1.6);
        var via = new Via("V", 0, 0, "Top", "Bottom", 0.3, 1.0);   // pad radius 0.5
        var model = new PcbCopperModel(
            board,
            [
                new CopperFeature("Top", "V", "via1", ViaGeometry.AnnularPad(new Vector2d(0, 0), 1.0, 0.3)),
                new CopperFeature("Top", "N", "P1", CurvedRegion2d.Disc(new Vector2d(centerX, 0), 0.5)),
            ],
            vias: [new PlacedVia(via, "via1", ViaType.Through, ["Top"])]);

        var report = PcbDrc.Check(model, rules);
        bool hasClash = report.Has(DrcRule.Clearance) || report.Has(DrcRule.Short);
        Assert.Equal(violation, hasClash);
    }

    [Theory]
    [InlineData(0.44, true)]    // web 0.44 − 0.3 = 0.14 < 0.20 → violation
    [InlineData(0.50, false)]   // web exactly 0.20 → passes
    [InlineData(0.70, false)]
    public void TwoViasTooCloseAreFound(double centerDistance, bool violation)
    {
        var rules = DrcRuleSet.Default with { MinViaToVia = 0.2 };
        var layout = new PcbLayout(new Schematic("t"), FourLayer());
        // Two through vias of different nets, drills Ø0.3 (radius 0.15). Web = centre − 0.3.
        layout.AddVia("A", 0, 0, "Top", "Bottom", drill: 0.3, pad: 0.6);
        layout.AddVia("B", centerDistance, 0, "Top", "Bottom", drill: 0.3, pad: 0.6);

        var report = PcbDrc.Check(layout, rules);
        Assert.Equal(violation, report.Has(DrcRule.ViaToVia));
        if (violation)
        {
            var hit = Assert.Single(report.OfRule(DrcRule.ViaToVia));
            Assert.Contains("via1", hit.Message);
            Assert.Contains("via2", hit.Message);
            Assert.Equal(centerDistance - 0.3, hit.Measured, 5);   // bisection resolution ~2e-7
        }
    }

    [Fact]
    public void ASameNetViaOnItsOwnCopperIsNotFlagged()
    {
        // A via touching a pad of its OWN net is the INTENDED connection — never a short or clearance
        // violation (the one-declaration identity: same-net copper touching is a join, not a fault).
        var board = PcbBoard.Rectangle(40, 30, 1.6);
        var via = new Via("N", 0, 0, "Top", "Bottom", 0.3, 1.0);
        var model = new PcbCopperModel(
            board,
            [
                new CopperFeature("Top", "N", "via1", ViaGeometry.AnnularPad(new Vector2d(0, 0), 1.0, 0.3)),
                new CopperFeature("Top", "N", "R1.1", CurvedRegion2d.Disc(new Vector2d(0.3, 0), 0.6)),  // overlaps the via pad
            ],
            vias: [new PlacedVia(via, "via1", ViaType.Through, ["Top"])]);

        var report = PcbDrc.Check(model);
        Assert.False(report.Has(DrcRule.Short));
        Assert.False(report.Has(DrcRule.Clearance));
        Assert.True(report.Ok);
    }

    // ---- no-via bit-identity --------------------------------------------------

    [Fact]
    public void ANoViaLayoutHasNoViaCopperAndSavesUnchanged()
    {
        var layout = PcbFixtures.Layout();   // the stage-2 fixture, no vias
        var model = PcbCopperModel.FromLayout(layout);
        Assert.Empty(model.Vias);
        // The saved JSON carries no "vias" key at all (write-only-when-stated).
        Assert.DoesNotContain("\"vias\"", layout.Save());
    }

    [Fact]
    public void AScaledRuleSetScalesTheViaToViaLimit()
    {
        var rules = DrcRuleSet.Default;
        Assert.Equal(rules.MinViaToVia * 1000, rules.Scaled(1000).MinViaToVia, 9);
    }

    // ---- persistence: vias are layout truth ----------------------------------

    [Fact]
    public void AViaLayoutIsAByteIdenticalFixedPoint()
    {
        var layout = TwoPadNet(withVia: true);
        string json = layout.Save();

        var reloaded = PcbLayout.Load(json, new PartLibrary());
        Assert.Equal(json, reloaded.Save());   // save → load → save byte-identical

        // The via values survive verbatim.
        var via = Assert.Single(reloaded.Vias);
        Assert.Equal("N", via.Net);
        Assert.Equal("Top", via.FromLayer);
        Assert.Equal("Bottom", via.ToLayer);
        Assert.Equal(0.4, via.DrillDiameter, 12);
        Assert.Equal(1.0, via.PadDiameter, 12);

        // The reloaded layout is still connected — the derived copper rebuilt from the via.
        Assert.True(reloaded.IsNetConnected("N"));
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void ConnectivityAndTheViaDrcAreScaleInvariant(double s)
    {
        // A two-layer board with a via tying a Top pad and a Bottom pad of one net, at scale `s`.
        // Connectivity is dimensionless (region touch) and the via rules ride the RELATIVE rule set.
        var board = PcbBoard.Rectangle(40 * s, 30 * s, 1.6 * s);
        var via = new Via("N", 0, 0, "Top", "Bottom", 0.4 * s, 1.0 * s);
        var annulus = ViaGeometry.AnnularPad(new Vector2d(0, 0), 1.0 * s, 0.4 * s);
        var model = new PcbCopperModel(
            board,
            [
                new CopperFeature("Top", "N", "P1", CurvedRegion2d.Disc(new Vector2d(0, 0), 0.6 * s)),
                new CopperFeature("Bottom", "N", "P2", CurvedRegion2d.Disc(new Vector2d(0, 0), 0.6 * s)),
                new CopperFeature("Top", "N", "via1", annulus),
                new CopperFeature("Bottom", "N", "via1", annulus),
            ],
            drills: [new DrilledHole(new Vector2d(0, 0), 0.4 * s, 1.0 * s, "N", "via1")],
            vias: [new PlacedVia(via, "via1", ViaType.Through, ["Top", "Bottom"])]);

        Assert.True(PcbConnectivity.For(model, "N").IsConnected);      // connected at every scale
        Assert.True(PcbDrc.Check(model, DrcRuleSet.Default.Scaled(s)).Ok);   // clean under scaled rules
    }

    [Fact]
    public void ConnectivityIsDeterministic()
    {
        var layout = TwoPadNet(withVia: true);
        Assert.Equal(
            PcbConnectivity.Analyze(layout).Unrouted,
            PcbConnectivity.Analyze(layout).Unrouted);
    }
}
