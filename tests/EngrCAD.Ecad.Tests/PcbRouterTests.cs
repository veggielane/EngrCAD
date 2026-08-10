using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// The stage-5 autorouter. The verification bar (an autorouter that connects while violating
/// clearance is the classic silent failure): every routed net CONNECTS its pins AND passes the exact
/// DRC, or the router REPORTS failure by name — it never ships a clearance-violating trace, and a
/// partial result is still DRC-clean.
/// </summary>
public sealed class PcbRouterTests
{
    // A 0.6 mm single-pad test point and a 0.6 mm obstacle pad — small enough that pads one grid
    // pitch (1 mm) apart clear each other, so the routing fixtures start DRC-clean.
    private static PartDefinition Tp(string name) => new(
        name, "TP", [new Pin("1", PinType.Passive)],
        new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));

    private static PartDefinition Obstacle() => new(
        "OBS", "X", [new Pin("1", PinType.Passive)],
        new Footprint("obs_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));

    // Comfortable rules: the trace (0.4) is wider than the minimum (0.2), pads/tracks a grid pitch
    // apart clear the 0.3 minimum, and the 80° acute limit passes a round-joined trace's 90° bends.
    private static readonly DrcRuleSet Rules = new(
        MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
        MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80);

    private static readonly RouterOptions Options = new()
    {
        GridResolution = 1.0, TraceWidth = 0.4, Clearance = 0.3,
    };

    private static PcbBoard Board(double w, double h) =>
        new([new(0, 0), new(w, 0), new(w, h), new(0, h)], 1.6);   // the default two-layer stackup

    // ---- the verification bar helpers ----------------------------------------

    /// <summary>Asserts a routed layout is DRC-clean (zero violations) against the routing rules —
    /// the never-a-silent-violation guarantee.</summary>
    private static void AssertDrcClean(PcbLayout layout)
    {
        var report = PcbDrc.Check(layout, Rules);
        Assert.True(report.Ok,
            "routed board must be DRC-clean but had: " + string.Join("; ", report.Messages));
    }

    /// <summary>Asserts each named net's pads are joined into ONE connected copper component (the
    /// router connected what it was asked to).</summary>
    private static void AssertConnected(PcbLayout layout, params string[] nets)
    {
        var model = PcbCopperModel.FromLayout(layout);
        foreach (var net in nets)
            Assert.True(PcbConnectivity.For(model, net).IsConnected, $"net '{net}' is not connected");
    }

    // ==== 1) a single 2-pin net ==============================================

    [Fact]
    public void TwoPinNet_ConnectsAndIsDrcClean()
    {
        var sch = new Schematic("t");
        var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
        sch.Connect("N", a.Pin("1"), b.Pin("1"));
        var layout = new PcbLayout(sch, Board(20, 20));
        layout.Place("A", 4, 10); layout.Place("B", 16, 10);

        var r = PcbRouter.Route(layout, Rules, Options);

        Assert.True(r.FullyRouted);
        Assert.Equal(["N"], r.RoutedNets);
        AssertDrcClean(r.Layout);                                   // AND passes DRC
        AssertConnected(r.Layout, "N");                             // every routed net connects
        Assert.Empty(PcbDrc.Check(r.Layout, Rules).Ratsnest);       // ratsnest empty for routed nets
        Assert.True(r.Layout.Check().Ok);                           // the one-declaration identity survives
        Assert.True(r.TracesAdded >= 1 && r.ViasAdded == 0);
    }

    // ==== 2) a few nets on one layer =========================================

    [Fact]
    public void SeveralNets_AllRouteCleanRatsnestEmpty()
    {
        var sch = new Schematic("t");
        for (int i = 0; i < 3; i++)
        {
            var a = sch.Add($"A{i}", Tp($"A{i}")); var b = sch.Add($"B{i}", Tp($"B{i}"));
            sch.Connect($"N{i}", a.Pin("1"), b.Pin("1"));
        }
        var layout = new PcbLayout(sch, Board(20, 20));
        for (int i = 0; i < 3; i++)
        {
            layout.Place($"A{i}", 2, 4 + 6 * i);   // three parallel nets, well apart
            layout.Place($"B{i}", 18, 4 + 6 * i);
        }

        var r = PcbRouter.Route(layout, Rules, Options);

        Assert.True(r.FullyRouted);
        Assert.Equal(["N0", "N1", "N2"], r.RoutedNets.OrderBy(x => x).ToList());
        AssertDrcClean(r.Layout);
        AssertConnected(r.Layout, "N0", "N1", "N2");
        Assert.Empty(PcbDrc.Check(r.Layout, Rules).Ratsnest);
    }

    // ==== 3) a net that MUST use a via to cross another =======================

    [Fact]
    public void CrossingNets_RouterPlacesViaBothCleanConnected()
    {
        var (layout, _) = CrossingBoard();
        var r = PcbRouter.Route(layout, Rules, Options);

        Assert.True(r.FullyRouted);
        Assert.True(r.ViasAdded >= 1, "the crossing net must use a via to change layers");
        AssertDrcClean(r.Layout);                                   // clean, INCLUDING the via rules
        AssertConnected(r.Layout, "X", "Y");                        // both nets joined
        Assert.Empty(PcbDrc.Check(r.Layout, Rules).Ratsnest);
    }

    [Fact]
    public void CrossingNets_WithoutVias_IsUnroutableByName_RestClean()
    {
        var (layout, _) = CrossingBoard();
        // Forbid vias: the vertical net cannot cross the horizontal wall on one layer.
        var r = PcbRouter.Route(layout, Rules, Options with { AllowVias = false });

        Assert.False(r.FullyRouted);
        Assert.Equal(["Y"], r.UnroutedNets);                        // reported by name, not faked
        Assert.Contains("X", r.RoutedNets);
        AssertDrcClean(r.Layout);                                   // the partial board is still clean
        AssertConnected(r.Layout, "X");
    }

    private static (PcbLayout, RouterOptions) CrossingBoard()
    {
        var sch = new Schematic("t");
        var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
        var c = sch.Add("C", Tp("C")); var d = sch.Add("D", Tp("D"));
        sch.Connect("X", a.Pin("1"), b.Pin("1"));   // a wall spanning the board
        sch.Connect("Y", c.Pin("1"), d.Pin("1"));   // must cross the wall
        var layout = new PcbLayout(sch, Board(20, 20));
        layout.Place("A", 1, 10); layout.Place("B", 19, 10);
        layout.Place("C", 10, 1); layout.Place("D", 10, 19);
        return (layout, Options);
    }

    // ==== 4) a congested board that needs RIP-UP to complete =================

    [Fact]
    public void CongestedBoard_RipUpCompletesWhereGreedyFails()
    {
        var layout = RipUpBoard();
        var opt = Options with { AllowVias = false };

        // A pure greedy pass (no rip-up) leaves net B unrouted — but is still clean.
        var greedy = PcbRouter.Route(layout, Rules, opt with { MaxRipUpIterations = 0 });
        Assert.False(greedy.FullyRouted);
        Assert.Equal(["B"], greedy.UnroutedNets);
        AssertDrcClean(greedy.Layout);

        // Rip-up-and-reroute completes it: net B is routed ACROSS net A, A is ripped up and
        // re-routed on its detour, and both are clean.
        var routed = PcbRouter.Route(layout, Rules, opt);
        Assert.True(routed.FullyRouted);
        Assert.True(routed.RipUps >= 1, "the board should require at least one rip-up");
        AssertDrcClean(routed.Layout);
        AssertConnected(routed.Layout, "A", "B");
        Assert.Empty(PcbDrc.Check(routed.Layout, Rules).Ratsnest);
    }

    private static PcbLayout RipUpBoard()
    {
        // Net A spans the board horizontally; net B is a short vertical that crosses it. Greedy
        // routes A straight (blocking B's crossing); B has no clean route, so it rips A up, takes
        // the crossing, and A re-routes around B's short segment.
        var sch = new Schematic("t");
        var a1 = sch.Add("A1", Tp("A1")); var a2 = sch.Add("A2", Tp("A2"));
        var b1 = sch.Add("B1", Tp("B1")); var b2 = sch.Add("B2", Tp("B2"));
        sch.Connect("A", a1.Pin("1"), a2.Pin("1"));
        sch.Connect("B", b1.Pin("1"), b2.Pin("1"));
        var layout = new PcbLayout(sch, Board(12, 12));
        layout.Place("A1", 1, 6); layout.Place("A2", 11, 6);
        layout.Place("B1", 6, 4); layout.Place("B2", 6, 8);
        return layout;
    }

    // ==== 5) a pin boxed in by other-net copper — reported unroutable =========

    [Fact]
    public void BoxedPin_ReportedUnroutable_OtherNetsRoutedAndClean()
    {
        var sch = new Schematic("t");
        var g1 = sch.Add("G1", Tp("G1")); var g2 = sch.Add("G2", Tp("G2"));
        var n1 = sch.Add("N1", Tp("N1")); var n2 = sch.Add("N2", Tp("N2"));
        sch.Connect("GOOD", g1.Pin("1"), g2.Pin("1"));   // routable, well clear of the box
        sch.Connect("BOXED", n1.Pin("1"), n2.Pin("1"));  // one pin walled in
        var obstacles = new List<(double x, double y)>
        {
            (4, 4), (5, 4), (6, 4), (4, 5), (6, 5), (4, 6), (5, 6), (6, 6),
        };
        for (int i = 0; i < obstacles.Count; i++)
            sch.Add($"O{i}", Obstacle());
        var layout = new PcbLayout(sch, Board(16, 16));
        layout.Place("N1", 5, 5); layout.Place("N2", 12, 12);   // N1 sits inside the ring
        layout.Place("G1", 2, 14); layout.Place("G2", 14, 14);  // GOOD runs along the top, clear
        for (int i = 0; i < obstacles.Count; i++)
            layout.Place($"O{i}", obstacles[i].x, obstacles[i].y);
        Assert.True(PcbDrc.Check(layout, Rules).Ok);   // the fixture starts clean

        var opt = Options with { AllowVias = false };
        var r = PcbRouter.Route(layout, Rules, opt);

        Assert.Equal(["BOXED"], r.UnroutedNets);       // named, not faked
        Assert.Equal(["GOOD"], r.RoutedNets);          // the rest of the board still routed
        AssertDrcClean(r.Layout);                      // and the partial board is clean
        AssertConnected(r.Layout, "GOOD");
    }

    // ==== 6) never a silent violation: the partial result is always clean =====

    [Fact]
    public void PartialResult_IsAlwaysDrcClean()
    {
        // A board too tight to fully route (a dense knot of crossing nets on ONE layer): whatever
        // the router manages, it is DRC-clean and the rest is named — never a hidden short.
        var sch = new Schematic("t");
        for (int i = 0; i < 5; i++)
        {
            var a = sch.Add($"H{i}a", Tp($"H{i}a")); var b = sch.Add($"H{i}b", Tp($"H{i}b"));
            sch.Connect($"H{i}", a.Pin("1"), b.Pin("1"));
            var c = sch.Add($"V{i}a", Tp($"V{i}a")); var d = sch.Add($"V{i}b", Tp($"V{i}b"));
            sch.Connect($"V{i}", c.Pin("1"), d.Pin("1"));
        }
        var layout = new PcbLayout(sch, Board(12, 12));
        for (int i = 0; i < 5; i++)
        {
            layout.Place($"H{i}a", 1, 2 + 2 * i); layout.Place($"H{i}b", 11, 2 + 2 * i);   // horizontals
            layout.Place($"V{i}a", 2 + 2 * i, 1); layout.Place($"V{i}b", 2 + 2 * i, 11);   // verticals
        }
        var opt = Options with { AllowVias = false };   // one layer: the grid of crossings cannot all route
        var r = PcbRouter.Route(layout, Rules, opt);

        Assert.False(r.FullyRouted);                    // it genuinely cannot route everything
        AssertDrcClean(r.Layout);                       // yet the partial board is DRC-clean
        // Every routed net is genuinely connected, and every unrouted one is named.
        AssertConnected(r.Layout, [.. r.RoutedNets]);
        Assert.NotEmpty(r.UnroutedNets);
        Assert.Empty(r.RoutedNets.Intersect(r.UnroutedNets));
    }

    // ==== 7) determinism ======================================================

    [Fact]
    public void Deterministic_TwoRunsAreIdentical()
    {
        var layout = RipUpBoard();
        var a = PcbRouter.Route(layout, Rules, Options with { AllowVias = false });
        var b = PcbRouter.Route(layout, Rules, Options with { AllowVias = false });
        Assert.Equal(a.Layout.Save(), b.Layout.Save());   // bit-for-bit identical routed layout
        Assert.Equal(a.RipUps, b.RipUps);
        Assert.Equal(a.RoutedNets, b.RoutedNets);
    }

    // ==== 8) nothing to route → unchanged =====================================

    [Fact]
    public void NothingToRoute_ReturnsUnchangedByteIdentical()
    {
        // Two pads of one net already overlap (same net touching = the intended connection), so the
        // net is connected and there is nothing to route.
        var sch = new Schematic("t");
        var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
        sch.Connect("N", a.Pin("1"), b.Pin("1"));
        var layout = new PcbLayout(sch, Board(20, 20));
        layout.Place("A", 10, 10); layout.Place("B", 10.4, 10);   // pads overlap → connected

        var before = layout.Save();
        var r = PcbRouter.Route(layout, Rules, Options);

        Assert.Empty(r.RoutedNets);
        Assert.Empty(r.UnroutedNets);
        Assert.Equal(0, r.TracesAdded);
        Assert.Equal(before, r.Layout.Save());   // byte-identical to the input
    }

    // ==== 9) routed traces round-trip through persistence =====================

    [Fact]
    public void RoutedLayout_RoundTripsThroughSaveLoad()
    {
        var sch = new Schematic("t");
        var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
        sch.Connect("N", a.Pin("1"), b.Pin("1"));
        var layout = new PcbLayout(sch, Board(20, 20));
        layout.Place("A", 4, 10); layout.Place("B", 16, 10);
        var r = PcbRouter.Route(layout, Rules, Options);
        Assert.NotEmpty(r.Layout.Traces);

        string json = r.Layout.Save();
        var reloaded = PcbLayout.Load(json);
        Assert.Equal(r.Layout.Traces.Count, reloaded.Traces.Count);
        Assert.Equal(json, reloaded.Save());           // save → load → save is a fixed point
        AssertDrcClean(reloaded);                       // and the reloaded board is still clean
        AssertConnected(reloaded, "N");
    }

    // ==== 10) scale invariance (the epsilon-ladder property) ==================

    [Fact]
    public void ScaleInvariant_RoutesTheSameStructureAt1000x()
    {
        PcbLayout Build(double s)
        {
            var sch = new Schematic("t");
            var a = sch.Add("A", ScaledTp("A", s)); var b = sch.Add("B", ScaledTp("B", s));
            sch.Connect("N", a.Pin("1"), b.Pin("1"));
            var layout = new PcbLayout(sch, Board(20 * s, 20 * s));
            layout.Place("A", 4 * s, 10 * s); layout.Place("B", 16 * s, 10 * s);
            return layout;
        }
        var opt = Options;
        var small = PcbRouter.Route(Build(1e-3), Rules.Scaled(1e-3),
            opt with { GridResolution = 1e-3, TraceWidth = 4e-4, Clearance = 3e-4 });
        var big = PcbRouter.Route(Build(1e3), Rules.Scaled(1e3),
            opt with { GridResolution = 1e3, TraceWidth = 4e2, Clearance = 3e2 });

        Assert.True(small.FullyRouted && big.FullyRouted);
        Assert.Equal(small.TracesAdded, big.TracesAdded);
        AssertDrcCleanScaled(small.Layout, Rules.Scaled(1e-3));
        AssertDrcCleanScaled(big.Layout, Rules.Scaled(1e3));
    }

    private static PartDefinition ScaledTp(string name, double s) => new(
        name, "TP", [new Pin("1", PinType.Passive)],
        new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6 * s, 0.6 * s)]));

    private static void AssertDrcCleanScaled(PcbLayout layout, DrcRuleSet rules) =>
        Assert.True(PcbDrc.Check(layout, rules).Ok);
}
