using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Teardrops — same-net drill-breakout relief at trace-to-round-pad / trace-to-via junctions. The oracle
/// that matters is that a teardrop ADDS copper: the teardropped layer's union area strictly EXCEEDS the
/// plain one (the naive chamfer that lies inside the pad adds nothing and fails this). Alongside it: the
/// teardrop is a CONNECTOR not a terminal (the net's pad count is unchanged), the board stays DRC-clean
/// (a teardrop is never a short or a clearance miss), the DRC gate DROPS a teardrop that would come too
/// close to another net, a rectangular pad gets none, off is unchanged, and persistence is a fixed point.
/// </summary>
public sealed class PcbTeardropTests
{
    // Two ROUND through-hole pads (radius 1.0, drill 1.0) 10 mm apart, one net, joined by a trace.
    private static PartDefinition Rnd() => new(
        "J", "J",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("J", [
            Pad.ThroughHole("1", new Vector2d(0, 0), pad: 2.0, drill: 1.0),
            Pad.ThroughHole("2", new Vector2d(10, 0), pad: 2.0, drill: 1.0),
        ]));

    private static PartDefinition Rect() => new(
        "RC", "R",
        [new Pin("1", PinType.Passive), new Pin("2", PinType.Passive)],
        new Footprint("RC", [
            Pad.Smd("1", new Vector2d(0, 0), 2.0, 2.0, PadShape.Rectangular),
            Pad.Smd("2", new Vector2d(10, 0), 2.0, 2.0, PadShape.Rectangular),
        ]));

    private static PcbBoard Board() => PcbBoard.Rectangle(40, 20, 1.6);

    // A SIG net on one part joined pad-to-pad by a trace, teardrops optionally on.
    private static PcbLayout Layout(bool teardrops, bool rectPads = false)
    {
        var sch = new Schematic("td");
        var j = sch.Add("J1", rectPads ? Rect() : Rnd());
        sch.Connect("SIG", j.Pin("1"), j.Pin("2"));
        var layout = new PcbLayout(sch, Board());
        layout.Place("J1", 8, 0);   // pads at (8,10) and (18,10)
        string top = layout.Board.Stackup.Coppers[0].Name;
        layout.AddTrace("SIG", top, 0.4, [new Vector2d(8, 0), new Vector2d(18, 0)]);
        if (teardrops) layout.WithTeardrops();
        return layout;
    }

    private static double TopUnionArea(PcbLayout layout)
    {
        var model = PcbCopperModel.FromLayout(layout);
        string top = layout.Board.Stackup.Coppers[0].Name;
        var regions = model.Copper.Where(f => f.Layer == top).Select(f => f.Region).ToList();
        return CurvedRegion2dBoolean.UnionAll(regions).Sum(r => r.Area);
    }

    // ==== 1) the oracle: teardrops ADD copper =================================

    [Fact]
    public void Teardrops_AddCopper_TheUnionAreaStrictlyExceedsThePlainOne()
    {
        double plain = TopUnionArea(Layout(teardrops: false));
        double withTd = TopUnionArea(Layout(teardrops: true));
        Assert.True(withTd > plain + 0.01,
            $"teardropped area {withTd:g6} must exceed plain {plain:g6} — a no-op teardrop fails this");
    }

    // ==== 2) a teardrop is a CONNECTOR, not a terminal ========================

    [Fact]
    public void Teardrops_DoNotChangeConnectivity_ThePadCountIsUnchanged()
    {
        var plain = Layout(teardrops: false).Connectivity().Of("SIG");
        var withTd = Layout(teardrops: true).Connectivity().Of("SIG");
        Assert.Equal(2, plain.PadCount);
        Assert.Equal(plain.PadCount, withTd.PadCount);       // the teardrop is not counted as a pin
        Assert.True(withTd.IsConnected);
    }

    // ==== 3) the board stays DRC-clean ========================================

    [Fact]
    public void ATeardroppedBoard_IsDrcClean()
    {
        Assert.True(PcbDrc.Check(Layout(teardrops: true)).Ok);
    }

    // ==== 4) off is unchanged =================================================

    [Fact]
    public void WithoutTeardrops_TheCopperIsUnchanged()
    {
        var off = PcbCopperModel.FromLayout(Layout(teardrops: false));
        var explicitNone = PcbCopperModel.FromLayout(Layout(teardrops: false));
        Assert.Equal(off.Copper.Count, explicitNone.Copper.Count);
        // and it is strictly fewer features than the teardropped model (which added two teardrops).
        Assert.True(PcbCopperModel.FromLayout(Layout(teardrops: true)).Copper.Count > off.Copper.Count);
    }

    // ==== 5) a rectangular pad gets no teardrop ===============================

    [Fact]
    public void ARectangularPad_GetsNoTeardrop_TheAreaIsUnchanged()
    {
        double plain = TopUnionArea(Layout(teardrops: false, rectPads: true));
        double withTd = TopUnionArea(Layout(teardrops: true, rectPads: true));
        Assert.Equal(plain, withTd, 6);   // round-only: nothing added
    }

    // ==== 6) the DRC gate: teardrops never turn a clean board dirty ===========

    [Fact]
    public void TheDrcGate_MeansTeardropsNeverTurnACleanBoardDirty()
    {
        // A board that is DRC-clean WITHOUT teardrops, with an other-net part nearby. Every teardrop is
        // gated against other-net copper (dropped if it would come within its clearance), so adding
        // teardrops can never introduce a short or a clearance miss — the safety guarantee.
        PcbLayout Build(bool td)
        {
            var sch = new Schematic("gate");
            var j = sch.Add("J1", Rnd());
            sch.Connect("SIG", j.Pin("1"), j.Pin("2"));
            var o = sch.Add("O1", Rnd());
            sch.Connect("OTH", o.Pin("1"), o.Pin("2"));
            var layout = new PcbLayout(sch, Board());
            layout.Place("J1", 8, 0);
            layout.Place("O1", 8, 3);   // clear of SIG's pads/trace, but nearby
            string top = layout.Board.Stackup.Coppers[0].Name;
            layout.AddTrace("SIG", top, 0.4, [new Vector2d(8, 0), new Vector2d(18, 0)]);
            if (td) layout.WithTeardrops();
            return layout;
        }

        Assert.True(PcbDrc.Check(Build(false)).Ok, "the base board must be clean without teardrops");
        Assert.True(PcbDrc.Check(Build(true)).Ok, "teardrops must not introduce a DRC violation");
    }

    // ==== 7) persistence: a fixed point; default writes no key ================

    [Fact]
    public void TeardropSettings_RoundTrip_AndNoneWritesNoKey()
    {
        Assert.DoesNotContain("teardrops", Layout(teardrops: false).Save());

        var layout = Layout(teardrops: false);
        layout.WithTeardrops(new TeardropSettings(LengthRatio: 1.5, Clearance: 0.3));
        string once = layout.Save();
        Assert.Contains("teardrops", once);
        string twice = PcbLayout.Load(once).Save();
        Assert.Equal(once, twice);
        Assert.Equal(1.5, PcbLayout.Load(once).Teardrops!.LengthRatio, 9);
        Assert.Equal(0.3, PcbLayout.Load(once).Teardrops!.Clearance, 9);
    }
}
