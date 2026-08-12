using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Coupled routing of a differential pair — the two nets routed TOGETHER as the parallel offsets of a
/// shared centre-line. The verification is that the generated pair is a GOOD pair by the same
/// <see cref="DiffPairs"/> measurement that judges a hand-routed one: well-coupled at the gap, low
/// skew on a straight run, both nets connected, and the whole board DRC-clean — or refused by name.
/// </summary>
public sealed class CoupledRouterTests
{
    private static PartDefinition Tp(string name) => new(
        name, "TP", [new Pin("1", PinType.Passive)],
        new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.4, 0.4)]));

    private static readonly DrcRuleSet Rules = new(
        MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
        MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80);

    private static PcbBoard Board(double w, double h) =>
        new([new(0, 0), new(w, 0), new(w, h), new(0, h)], 1.6);

    private const double W = 0.4;   // trace width
    private const double Gap = 1.0; // centre-to-centre gap (comfortably over clearance + widths)

    // A layout with the pair's four pads placed at the given +/- endpoints.
    private static PcbLayout PairLayout(Vector2d p0, Vector2d p1, Vector2d n0, Vector2d n1)
    {
        var sch = new Schematic("cp");
        var pp0 = sch.Add("PP0", Tp("PP0")); var pp1 = sch.Add("PP1", Tp("PP1"));
        var nn0 = sch.Add("NN0", Tp("NN0")); var nn1 = sch.Add("NN1", Tp("NN1"));
        sch.Connect("D_P", pp0.Pin("1"), pp1.Pin("1"));
        sch.Connect("D_N", nn0.Pin("1"), nn1.Pin("1"));
        var layout = new PcbLayout(sch, Board(28, 28));
        layout.Place("PP0", p0.X, p0.Y); layout.Place("PP1", p1.X, p1.Y);
        layout.Place("NN0", n0.X, n0.Y); layout.Place("NN1", n1.X, n1.Y);
        return layout;
    }

    private static string Layer(PcbLayout l) => l.Board.Stackup.Coppers[0].Name;

    // ==== 1) a straight coupled route is a good pair ==========================

    [Fact]
    public void AStraightCoupledRoute_IsWellCoupledLowSkewCleanAndConnected()
    {
        // centre-line at y=10; the pair straddles it at +/- 0.5 → pads at y=10.5 (P) and y=9.5 (N).
        var layout = PairLayout(new(4, 10.5), new(24, 10.5), new(4, 9.5), new(24, 9.5));
        var pair = new DiffPair("D_P", "D_N", TargetGapMm: Gap);
        var centre = new[] { new Vector2d(4, 10), new Vector2d(24, 10) };

        var result = CoupledRouter.Route(layout, pair, centre, Layer(layout), W, Rules);

        Assert.Equal(CoupledOutcome.Routed, result.Outcome);
        layout.AddTrace(result.Positive);
        layout.AddTrace(result.Negative);

        Assert.True(PcbDrc.Check(layout, Rules).Ok, "the coupled pair must be DRC-clean");
        var model = PcbCopperModel.FromLayout(layout);
        Assert.True(PcbConnectivity.For(model, "D_P").IsConnected);
        Assert.True(PcbConnectivity.For(model, "D_N").IsConnected);

        var check = DiffPairs.Check(layout, pair);
        Assert.True(check.Ok, check.Message);            // well-coupled at the gap, within skew
        Assert.Equal(Gap, check.MedianGapMm, 3);
        Assert.Equal(1.0, check.CoupledFraction, 3);
        Assert.Equal(0.0, check.Skew, 3);
    }

    // ==== 2) a bent centre-line routes cleanly and stays coupled =============

    [Fact]
    public void ABentCoupledRoute_RoutesCleanAndConnected()
    {
        // centre-line (4,10) -> (14,10) -> (14,18); the offsets mitre at the corner.
        // computed pad ends: P (4,10.5)/(13.5,18), N (4,9.5)/(14.5,18).
        var layout = PairLayout(new(4, 10.5), new(13.5, 18), new(4, 9.5), new(14.5, 18));
        var pair = new DiffPair("D_P", "D_N", TargetGapMm: Gap);
        var centre = new[] { new Vector2d(4, 10), new Vector2d(14, 10), new Vector2d(14, 18) };

        var result = CoupledRouter.Route(layout, pair, centre, Layer(layout), W, Rules);

        Assert.Equal(CoupledOutcome.Routed, result.Outcome);
        layout.AddTrace(result.Positive);
        layout.AddTrace(result.Negative);

        Assert.True(PcbDrc.Check(layout, Rules).Ok, "the bent coupled pair must be DRC-clean");
        var model = PcbCopperModel.FromLayout(layout);
        Assert.True(PcbConnectivity.For(model, "D_P").IsConnected);
        Assert.True(PcbConnectivity.For(model, "D_N").IsConnected);
        // the pair is still well-coupled at the gap (parallel offsets hold their distance).
        Assert.True(DiffPairs.Check(layout, pair).WellCoupled);
    }

    // ==== 3) a gap that does not exceed the width is refused ==================

    [Fact]
    public void AGapNotExceedingTheWidth_IsRefusedByName()
    {
        var layout = PairLayout(new(4, 10.2), new(24, 10.2), new(4, 9.8), new(24, 9.8));
        var pair = new DiffPair("D_P", "D_N", TargetGapMm: 0.4);   // gap == width
        var centre = new[] { new Vector2d(4, 10), new Vector2d(24, 10) };

        var result = CoupledRouter.Route(layout, pair, centre, Layer(layout), W, Rules);

        Assert.Equal(CoupledOutcome.Refused, result.Outcome);
        Assert.Contains("merge", result.Message);
    }

    // ==== 4) a centre-line too close to other copper is refused ==============

    [Fact]
    public void ACentreLineTooCloseToOtherCopper_IsRefused()
    {
        var layout = PairLayout(new(4, 10.5), new(24, 10.5), new(4, 9.5), new(24, 9.5));
        string layer = Layer(layout);
        // an obstacle trace whose middle dips to y=11 (its ends stay clear up at y=15), so the coupled
        // + trace at y=10.5 runs 0.5 mm from it in the middle — under the clearance plus widths.
        layout.AddTrace("OBS", layer, W, [new Vector2d(2, 15), new Vector2d(10, 11), new Vector2d(18, 11), new Vector2d(26, 15)]);

        var pair = new DiffPair("D_P", "D_N", TargetGapMm: Gap);
        var centre = new[] { new Vector2d(4, 10), new Vector2d(24, 10) };   // P at y=10.5, 0.5 from OBS at 11

        var result = CoupledRouter.Route(layout, pair, centre, layer, W, Rules);

        Assert.Equal(CoupledOutcome.Refused, result.Outcome);
        Assert.Contains("violates", result.Message);
    }

    // ==== 5) coupled routing is deterministic ================================

    [Fact]
    public void CoupledRoutingIsDeterministic()
    {
        var l1 = PairLayout(new(4, 10.5), new(24, 10.5), new(4, 9.5), new(24, 9.5));
        var l2 = PairLayout(new(4, 10.5), new(24, 10.5), new(4, 9.5), new(24, 9.5));
        var pair = new DiffPair("D_P", "D_N", TargetGapMm: Gap);
        var centre = new[] { new Vector2d(4, 10), new Vector2d(24, 10) };
        var r1 = CoupledRouter.Route(l1, pair, centre, Layer(l1), W, Rules);
        var r2 = CoupledRouter.Route(l2, pair, centre, Layer(l2), W, Rules);
        Assert.Equal(r1.Positive.Points, r2.Positive.Points);
        Assert.Equal(r1.Negative.Points, r2.Negative.Points);
    }
}
