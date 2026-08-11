using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Differential-pair analysis and skew matching. A pair is judged by two measured properties —
/// coupling (does the + trace run parallel to the − trace at the target gap?) and skew (do the two
/// halves match in length?) — so the tests build pairs that are good and bad in each and assert the
/// measurement separates them, and that skew tuning actually equalises the lengths DRC-cleanly.
/// </summary>
public sealed class DiffPairTests
{
    private static PartDefinition Tp(string name) => new(
        name, "TP", [new Pin("1", PinType.Passive)],
        new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));

    private static readonly DrcRuleSet Rules = new(
        MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
        MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80);

    private static PcbBoard Board(double w, double h) =>
        new([new(0, 0), new(w, 0), new(w, h), new(0, h)], 1.6);

    // A layout with two nets P and N (pads placed at the trace ends) and the caller's traces added.
    private static PcbLayout PairLayout(
        IReadOnlyList<Vector2d> pPts, IReadOnlyList<Vector2d> nPts, double width = 0.2)
    {
        var sch = new Schematic("dp");
        var p1 = sch.Add("P1", Tp("P1")); var p2 = sch.Add("P2", Tp("P2"));
        var n1 = sch.Add("N1", Tp("N1")); var n2 = sch.Add("N2", Tp("N2"));
        sch.Connect("P", p1.Pin("1"), p2.Pin("1"));
        sch.Connect("N", n1.Pin("1"), n2.Pin("1"));
        var layout = new PcbLayout(sch, Board(28, 28));
        layout.Place("P1", pPts[0].X, pPts[0].Y); layout.Place("P2", pPts[^1].X, pPts[^1].Y);
        layout.Place("N1", nPts[0].X, nPts[0].Y); layout.Place("N2", nPts[^1].X, nPts[^1].Y);
        string layer = layout.Board.Stackup.Coppers[0].Name;
        layout.AddTrace("P", layer, width, pPts);
        layout.AddTrace("N", layer, width, nPts);
        return layout;
    }

    private static Vector2d[] Line(double x0, double x1, double y) => [new(x0, y), new(x1, y)];

    // ==== 1) a parallel pair is well coupled and low skew =====================

    [Fact]
    public void AParallelPairAtTheTargetGap_IsWellCoupledAndLowSkew()
    {
        var layout = PairLayout(Line(4, 20, 10.0), Line(4, 20, 10.3));   // 0.3 mm apart, both 16 mm
        var report = DiffPairs.Check(layout, new DiffPair("P", "N", TargetGapMm: 0.3));

        Assert.True(report.Ok, report.Message);
        Assert.True(report.WithinSkew);
        Assert.True(report.WellCoupled);
        Assert.Equal(0.0, report.Skew, 6);
        Assert.Equal(0.3, report.MedianGapMm, 3);
        Assert.Equal(1.0, report.CoupledFraction, 3);
    }

    // ==== 2) the same pair judged against the WRONG gap is poorly coupled =====

    [Fact]
    public void TheSamePairAtAWrongTargetGap_IsPoorlyCoupled()
    {
        var layout = PairLayout(Line(4, 20, 10.0), Line(4, 20, 10.3));   // really 0.3 apart
        var report = DiffPairs.Check(layout, new DiffPair("P", "N", TargetGapMm: 0.5)); // asked for 0.5

        Assert.False(report.Ok);
        Assert.False(report.WellCoupled);         // 0.3 is not within 0.05 of 0.5
        Assert.True(report.CoupledFraction < 0.1);
        Assert.Equal(0.3, report.MedianGapMm, 3); // the measured gap is still reported honestly
    }

    // ==== 3) a length mismatch is reported as over-skew ======================

    [Fact]
    public void AMismatchedLengthPair_IsOverSkew()
    {
        // P is 16 mm; N is 12 mm — 4 mm of skew, well over a 0.1 mm tolerance.
        var layout = PairLayout(Line(4, 20, 10.0), Line(4, 16, 12.0));
        var report = DiffPairs.Check(layout, new DiffPair("P", "N", TargetGapMm: 2.0, SkewToleranceMm: 0.1));

        Assert.False(report.WithinSkew);
        Assert.Equal(4.0, report.Skew, 3);
        Assert.False(report.Ok);
    }

    // ==== 4) skew matching equalises the two lengths, DRC-clean ===============

    [Fact]
    public void MatchSkew_EqualisesTheTwoLengths_DrcClean()
    {
        // 0.4 mm traces (comfortably over the 0.2 min width so a serpentine's corners don't pinch),
        // 3 mm apart so the tuned excursions have room. P 16 mm, N 12 mm.
        var layout = PairLayout(Line(4, 20, 10.0), Line(4, 16, 13.0), width: 0.4);
        var pair = new DiffPair("P", "N", TargetGapMm: 3.0, SkewToleranceMm: 0.1);

        var (pRes, nRes) = DiffPairs.MatchSkew(layout, pair, Rules);

        Assert.True(pRes.Ok && nRes.Ok, $"P: {pRes.Message} | N: {nRes.Message}");
        // apply both and re-check the skew.
        int pi = layout.Traces.ToList().FindIndex(t => t.Net == "P");
        int ni = layout.Traces.ToList().FindIndex(t => t.Net == "N");
        layout.ReplaceTrace(pi, pRes.Trace);
        layout.ReplaceTrace(ni, nRes.Trace);

        var after = DiffPairs.Check(layout, pair);
        Assert.True(after.WithinSkew, $"skew after tuning = {after.Skew}");
        Assert.True(after.Skew <= 0.1);
        Assert.True(PcbDrc.Check(layout, Rules).Ok, "the tuned pair must stay DRC-clean");
    }

    // ==== 5) an unrouted net is not a checkable pair =========================

    [Fact]
    public void AnUnroutedNet_IsReportedNotCheckableByName()
    {
        // only P is routed; N has no trace.
        var sch = new Schematic("dp");
        var p1 = sch.Add("P1", Tp("P1")); var p2 = sch.Add("P2", Tp("P2"));
        var n1 = sch.Add("N1", Tp("N1")); var n2 = sch.Add("N2", Tp("N2"));
        sch.Connect("P", p1.Pin("1"), p2.Pin("1"));
        sch.Connect("N", n1.Pin("1"), n2.Pin("1"));
        var layout = new PcbLayout(sch, Board(24, 24));
        layout.Place("P1", 4, 10); layout.Place("P2", 20, 10);
        layout.Place("N1", 4, 12); layout.Place("N2", 20, 12);
        string layer = layout.Board.Stackup.Coppers[0].Name;
        layout.AddTrace("P", layer, 0.2, Line(4, 20, 10));

        var report = DiffPairs.Check(layout, new DiffPair("P", "N", 0.3));

        Assert.False(report.Ok);
        Assert.Contains("N", report.Message);
        Assert.Contains("no routed trace", report.Message);
    }

    // ==== 6) checking is deterministic =======================================

    [Fact]
    public void CheckingIsDeterministic()
    {
        var l1 = PairLayout(Line(4, 20, 10.0), Line(4, 20, 10.3));
        var l2 = PairLayout(Line(4, 20, 10.0), Line(4, 20, 10.3));
        var pair = new DiffPair("P", "N", 0.3);
        Assert.Equal(DiffPairs.Check(l1, pair), DiffPairs.Check(l2, pair));
    }

    // ==== 7) the declaration refuses nonsense by name ========================

    [Fact]
    public void TheDeclaration_RefusesNonsenseByName()
    {
        Assert.Throws<ArgumentException>(() => new DiffPair("P", "P", 0.3).Validate());   // same net
        Assert.Throws<ArgumentException>(() => new DiffPair("P", "N", 0).Validate());     // zero gap
        Assert.Throws<ArgumentException>(() => new DiffPair("P", "N", 0.3, SkewToleranceMm: 0).Validate());
    }
}
