using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Shove (push-and-route) insertion. The bar is the router's — a shove never ships a clearance
/// violation: the whole result (the new trace and every shoved blocker) is DRC-clean or the insertion
/// is refused by name. The tests set up a board where a DIRECT insertion is blocked (a detour router
/// would go around), show that shoving the blocker aside makes room and stays clean, and that the
/// refusals hold (a shove that would collide, or a blocker v1 cannot handle, changes nothing).
/// </summary>
public sealed class ShoveRouterTests
{
    private static PartDefinition Tp(string name) => new(
        name, "TP", [new Pin("1", PinType.Passive)],
        new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));

    private static readonly DrcRuleSet Rules = new(
        MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
        MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80);

    private static PcbBoard Board(double w, double h) =>
        new([new(0, 0), new(w, 0), new(w, h), new(0, h)], 1.6);

    private const double W = 0.4;   // trace width

    // The canonical fixture: a blocker OLD straight across the middle, and a NEW net whose pads are
    // clear (up at y=13) but whose direct route runs parallel to OLD, 0.4 mm away — too close.
    private static (PcbLayout Layout, PcbTrace NewTrace, int OldIndex) Fixture()
    {
        var sch = new Schematic("shove");
        var o1 = sch.Add("O1", Tp("O1")); var o2 = sch.Add("O2", Tp("O2"));
        var m1 = sch.Add("M1", Tp("M1")); var m2 = sch.Add("M2", Tp("M2"));
        sch.Connect("OLD", o1.Pin("1"), o2.Pin("1"));
        sch.Connect("NEW", m1.Pin("1"), m2.Pin("1"));
        var layout = new PcbLayout(sch, Board(28, 20));
        layout.Place("O1", 2, 10); layout.Place("O2", 26, 10);   // blocker's pads span the board
        layout.Place("M1", 4, 13); layout.Place("M2", 24, 13);   // the new net's pads, clear of OLD
        string layer = layout.Board.Stackup.Coppers[0].Name;
        layout.AddTrace("OLD", layer, W, [new(2, 10), new(26, 10)]);   // trace index 0

        // the new trace: down from its pad, a long parallel run 0.4 mm above OLD, back up to its pad.
        var newTrace = new PcbTrace("NEW", layer, W,
            [new(4, 13), new(8, 10.4), new(20, 10.4), new(24, 13)]);
        return (layout, newTrace, 0);
    }

    // ==== 1) shove places the direct trace and the board stays clean ==========

    [Fact]
    public void Insert_ShovesTheBlockerAside_AndTheBoardStaysCleanAndConnected()
    {
        var (layout, newTrace, oldIdx) = Fixture();
        var before = layout.Traces[oldIdx];

        var result = ShoveRouter.Insert(layout, newTrace, Rules);

        Assert.Equal(ShoveOutcome.Inserted, result.Outcome);
        Assert.True(result.ShovedTraces.ContainsKey(oldIdx), "the OLD blocker must have been shoved");
        // the blocker's endpoints (its pads) never moved — only its middle jogged.
        var shovedOld = result.ShovedTraces[oldIdx];
        Assert.Equal(before.Points[0], shovedOld.Points[0]);
        Assert.Equal(before.Points[^1], shovedOld.Points[^1]);

        // apply the result and assert the whole board is DRC-clean and both nets connect.
        layout.ReplaceTrace(oldIdx, shovedOld);
        layout.AddTrace(newTrace);
        Assert.True(PcbDrc.Check(layout, Rules).Ok, "the shoved board must be DRC-clean");
        var model = PcbCopperModel.FromLayout(layout);
        Assert.True(PcbConnectivity.For(model, "OLD").IsConnected);
        Assert.True(PcbConnectivity.For(model, "NEW").IsConnected);
    }

    // ==== 2) the mutation: without a shove, the direct trace VIOLATES =========

    [Fact]
    public void WithoutTheShove_TheDirectTraceViolatesTheDrc()
    {
        var (layout, newTrace, _) = Fixture();
        layout.AddTrace(newTrace);   // just drop it in on top of OLD, no shove

        Assert.False(PcbDrc.Check(layout, Rules).Ok,
            "the direct trace runs 0.4 mm from OLD — under the 0.3 clearance plus widths — so it must violate");
    }

    // ==== 3) a trace with nothing in its way needs no shove ==================

    [Fact]
    public void ATraceWithNothingInItsWay_NeedsNoShove()
    {
        var (layout, _, _) = Fixture();
        string layer = layout.Board.Stackup.Coppers[0].Name;
        // a route up at y=16, well clear of OLD at y=10.
        var clear = new PcbTrace("NEW", layer, W, [new(4, 13), new(8, 16), new(20, 16), new(24, 13)]);

        var result = ShoveRouter.Insert(layout, clear, Rules);

        Assert.Equal(ShoveOutcome.NoShoveNeeded, result.Outcome);
        Assert.Empty(result.ShovedTraces);
        Assert.True(result.Ok);
    }

    // ==== 4) a bent blocker is refused by name (v1 shoves a straight one) =====

    [Fact]
    public void ABentBlocker_IsRefusedByName()
    {
        var sch = new Schematic("shove");
        var o1 = sch.Add("O1", Tp("O1")); var o2 = sch.Add("O2", Tp("O2"));
        var m1 = sch.Add("M1", Tp("M1")); var m2 = sch.Add("M2", Tp("M2"));
        sch.Connect("OLD", o1.Pin("1"), o2.Pin("1"));
        sch.Connect("NEW", m1.Pin("1"), m2.Pin("1"));
        var layout = new PcbLayout(sch, Board(28, 20));
        layout.Place("O1", 2, 10); layout.Place("O2", 26, 10);
        layout.Place("M1", 4, 13); layout.Place("M2", 24, 13);
        string layer = layout.Board.Stackup.Coppers[0].Name;
        layout.AddTrace("OLD", layer, W, [new(2, 10), new(14, 10.2), new(26, 10)]);   // a BENT blocker

        var newTrace = new PcbTrace("NEW", layer, W, [new(4, 13), new(8, 10.4), new(20, 10.4), new(24, 13)]);
        var result = ShoveRouter.Insert(layout, newTrace, Rules);

        Assert.Equal(ShoveOutcome.Refused, result.Outcome);
        Assert.Contains("bent", result.Message);
        Assert.Empty(result.ShovedTraces);
    }

    // ==== 5) the no-cascade guard: shoving into a THIRD trace is refused ======

    [Fact]
    public void AShoveThatWouldCollideWithAThirdTrace_IsRefused_NothingChanged()
    {
        var sch = new Schematic("shove");
        var o1 = sch.Add("O1", Tp("O1")); var o2 = sch.Add("O2", Tp("O2"));
        var m1 = sch.Add("M1", Tp("M1")); var m2 = sch.Add("M2", Tp("M2"));
        var x1 = sch.Add("X1", Tp("X1")); var x2 = sch.Add("X2", Tp("X2"));
        sch.Connect("OLD", o1.Pin("1"), o2.Pin("1"));
        sch.Connect("NEW", m1.Pin("1"), m2.Pin("1"));
        sch.Connect("OBS", x1.Pin("1"), x2.Pin("1"));
        var layout = new PcbLayout(sch, Board(28, 20));
        layout.Place("O1", 2, 10); layout.Place("O2", 26, 10);
        layout.Place("M1", 4, 13); layout.Place("M2", 24, 13);
        layout.Place("X1", 2, 4); layout.Place("X2", 26, 4);
        string layer = layout.Board.Stackup.Coppers[0].Name;
        layout.AddTrace("OLD", layer, W, [new(2, 10), new(26, 10)]);
        // OBS dips up to y=9.2 exactly where OLD would be shoved down to (~9.65), so the shove collides.
        layout.AddTrace("OBS", layer, W, [new(2, 4), new(8, 9.2), new(20, 9.2), new(24, 4)]);

        // the base must be clean before we test the shove (OLD/OBS are 0.8 mm apart in the middle).
        Assert.True(PcbDrc.Check(layout, Rules).Ok, "base fixture must start DRC-clean");

        var newTrace = new PcbTrace("NEW", layer, W, [new(4, 13), new(8, 10.4), new(20, 10.4), new(24, 13)]);
        var result = ShoveRouter.Insert(layout, newTrace, Rules);

        Assert.Equal(ShoveOutcome.Refused, result.Outcome);
        Assert.Contains("violation", result.Message);
    }

    // ==== 6) shoving is deterministic ========================================

    [Fact]
    public void ShovingIsDeterministic()
    {
        var (l1, t1, _) = Fixture();
        var (l2, t2, _) = Fixture();
        var r1 = ShoveRouter.Insert(l1, t1, Rules);
        var r2 = ShoveRouter.Insert(l2, t2, Rules);
        Assert.Equal(r1.Outcome, r2.Outcome);
        Assert.Equal(r1.ShovedTraces[0].Points, r2.ShovedTraces[0].Points);
    }
}
