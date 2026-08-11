using EngrCAD.Core;
using EngrCAD.Ecad;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Length matching (serpentine tuning). The verification bar is the router's: a tuner that ships a
/// DRC-violating serpentine, or claims a length it did not actually reach, is the silent failure —
/// so every Reached result is MEASURED against its own geometry and is DRC-clean when applied, and a
/// trace with no room is reported <see cref="LengthTuneOutcome.Untunable"/> rather than fudged.
/// </summary>
public sealed class LengthMatchTests
{
    private static PartDefinition Tp(string name) => new(
        name, "TP", [new Pin("1", PinType.Passive)],
        new Footprint(name + "_fp", [Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6)]));

    private static readonly DrcRuleSet Rules = new(
        MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
        MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80);

    private static readonly RouterOptions Options = new()
    {
        GridResolution = 1.0, TraceWidth = 0.4, Clearance = 0.3,
    };

    private static PcbBoard Board(double w, double h) =>
        new([new(0, 0), new(w, 0), new(w, h), new(0, h)], 1.6);

    // A routed 2-pin net across a board; returns the routed layout and the (single) trace's index.
    private static (PcbLayout Layout, int TraceIndex) RoutedNet(
        double w, double h, Vector2d aPos, Vector2d bPos, string net = "N")
    {
        var sch = new Schematic("t");
        var a = sch.Add("A", Tp("A"));
        var b = sch.Add("B", Tp("B"));
        sch.Connect(net, a.Pin("1"), b.Pin("1"));
        var layout = new PcbLayout(sch, Board(w, h));
        layout.Place("A", aPos.X, aPos.Y);
        layout.Place("B", bPos.X, bPos.Y);
        var r = PcbRouter.Route(layout, Rules, Options);
        Assert.True(r.FullyRouted, "the fixture net must route");
        int idx = OnlyTraceOf(r.Layout, net);
        return (r.Layout, idx);
    }

    private static int OnlyTraceOf(PcbLayout layout, string net)
    {
        var idxs = Enumerable.Range(0, layout.Traces.Count).Where(i => layout.Traces[i].Net == net).ToList();
        Assert.Single(idxs);   // the simple fixtures route each net to exactly one trace
        return idxs[0];
    }

    private static void AssertDrcCleanAndConnected(PcbLayout layout, string net)
    {
        var report = PcbDrc.Check(layout, Rules);
        Assert.True(report.Ok, "tuned board must be DRC-clean but had: " + string.Join("; ", report.Messages));
        Assert.True(PcbConnectivity.For(PcbCopperModel.FromLayout(layout), net).IsConnected,
            $"net '{net}' must stay connected after tuning");
    }

    // ==== 1) reaches the target, DRC-clean, connected, endpoints unmoved =======

    [Fact]
    public void TuningToALongerTarget_ReachesItMeasured_AndStaysCleanAndConnected()
    {
        var (layout, idx) = RoutedNet(24, 24, new(4, 12), new(20, 12));
        var trace = layout.Traces[idx];
        double current = LengthMatch.Length(trace);
        double target = current + 8.0;

        var result = LengthMatch.Tune(layout, idx, target, tolerance: 0.05, Rules);

        Assert.Equal(LengthTuneOutcome.Reached, result.Outcome);
        // MEASURED, not claimed: the returned geometry's own length is within tolerance of the target.
        Assert.Equal(target, LengthMatch.Length(result.Trace), 3);
        Assert.Equal(result.AchievedLength, LengthMatch.Length(result.Trace), 9);
        // endpoints and net are unmoved — only the middle lengthened.
        Assert.Equal(trace.Points[0], result.Trace.Points[0]);
        Assert.Equal(trace.Points[^1], result.Trace.Points[^1]);
        Assert.Equal(trace.Net, result.Trace.Net);
        Assert.Equal(trace.Layer, result.Trace.Layer);

        // Applying it keeps the board DRC-clean and connected (the independent oracle).
        layout.ReplaceTrace(idx, result.Trace);
        AssertDrcCleanAndConnected(layout, "N");
        Assert.Equal(target, LengthMatch.Length(layout.Traces[idx]), 3);
    }

    // ==== 2) a target shorter than the current is refused by name =============

    [Fact]
    public void ATargetShorterThanCurrent_IsRefusedByName_LeavingTheTraceUnchanged()
    {
        var (layout, idx) = RoutedNet(24, 24, new(4, 12), new(20, 12));
        var trace = layout.Traces[idx];
        double current = LengthMatch.Length(trace);

        var result = LengthMatch.Tune(layout, idx, current - 5.0, tolerance: 0.05, Rules);

        Assert.Equal(LengthTuneOutcome.Refused, result.Outcome);
        Assert.False(result.Ok);
        Assert.Contains("shorter", result.Message);
        Assert.Equal(trace.Points, result.Trace.Points);   // unchanged
    }

    // ==== 3) a target equal to the current is an unchanged no-op ==============

    [Fact]
    public void ATargetEqualToCurrent_IsAnUnchangedNoOp()
    {
        var (layout, idx) = RoutedNet(24, 24, new(4, 12), new(20, 12));
        var trace = layout.Traces[idx];
        double current = LengthMatch.Length(trace);

        var result = LengthMatch.Tune(layout, idx, current, tolerance: 0.05, Rules);

        Assert.Equal(LengthTuneOutcome.Unchanged, result.Outcome);
        Assert.True(result.Ok);
        Assert.Equal(trace.Points, result.Trace.Points);
    }

    // ==== 4) no room for the full target: untunable, with how much it could add =

    [Fact]
    public void NoRoomForTheWholeTarget_IsUntunable_ReportingWhatItCouldAdd()
    {
        var (layout, idx) = RoutedNet(24, 24, new(4, 12), new(20, 12));
        var trace = layout.Traces[idx];
        double current = LengthMatch.Length(trace);
        double impossible = current + 1000.0;   // no board can hold a 1 m serpentine here

        var result = LengthMatch.Tune(layout, idx, impossible, tolerance: 0.05, Rules);

        Assert.Equal(LengthTuneOutcome.Untunable, result.Outcome);
        Assert.False(result.Ok);
        Assert.True(result.MaxAddableLength >= 0 && result.MaxAddableLength < 1000.0,
            $"MaxAddableLength should be a real, bounded amount (got {result.MaxAddableLength})");
        Assert.Equal(trace.Points, result.Trace.Points);   // unchanged
        // the (unchanged) trace is of course still DRC-clean — never a violating result.
        layout.ReplaceTrace(idx, result.Trace);
        AssertDrcCleanAndConnected(layout, "N");
    }

    // ==== 5) a group matches to the longest member ===========================

    [Fact]
    public void MatchGroup_TunesEveryMemberToTheLongest_AllCleanAndConnected()
    {
        var sch = new Schematic("t");
        for (int i = 0; i < 3; i++)
        {
            var a = sch.Add($"A{i}", Tp($"A{i}"));
            var b = sch.Add($"B{i}", Tp($"B{i}"));
            sch.Connect($"N{i}", a.Pin("1"), b.Pin("1"));
        }
        var layout = new PcbLayout(sch, Board(28, 28));
        // three nets of DIFFERENT routed lengths (different horizontal spans), well apart vertically.
        layout.Place("A0", 4, 6); layout.Place("B0", 12, 6);    // ~8 mm
        layout.Place("A1", 4, 14); layout.Place("B1", 24, 14);  // ~20 mm (the longest)
        layout.Place("A2", 4, 22); layout.Place("B2", 16, 22);  // ~12 mm
        var routed = PcbRouter.Route(layout, Rules, Options).Layout;

        int[] idx = [OnlyTraceOf(routed, "N0"), OnlyTraceOf(routed, "N1"), OnlyTraceOf(routed, "N2")];
        double target = idx.Max(i => LengthMatch.Length(routed.Traces[i]));

        var results = LengthMatch.MatchGroup(routed, idx, tolerance: 0.05, Rules);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Ok, $"member did not match: {r.Message}"));
        // apply them all, then assert every member is within tolerance of the target and clean.
        for (int k = 0; k < idx.Length; k++)
            routed.ReplaceTrace(idx[k], results[k].Trace);
        for (int k = 0; k < idx.Length; k++)
        {
            double len = LengthMatch.Length(routed.Traces[idx[k]]);
            Assert.True(Math.Abs(len - target) <= 0.05, $"member {k} length {len} not within tol of {target}");
        }
        AssertDrcCleanAndConnected(routed, "N0");
        AssertDrcCleanAndConnected(routed, "N1");
        AssertDrcCleanAndConnected(routed, "N2");
    }

    // ==== 6) DRC is truth near an obstacle — the result is never a violation ===

    [Fact]
    public void NearAnObstacle_TheResultIsAlwaysDrcClean_WhateverTheOutcome()
    {
        // a net routed with an obstacle net running close beside its channel.
        var sch = new Schematic("t");
        var a = sch.Add("A", Tp("A"));
        var b = sch.Add("B", Tp("B"));
        sch.Connect("N", a.Pin("1"), b.Pin("1"));
        var o1 = sch.Add("O1", Tp("O1"));
        var o2 = sch.Add("O2", Tp("O2"));
        sch.Connect("OBS", o1.Pin("1"), o2.Pin("1"));
        var layout = new PcbLayout(sch, Board(24, 24));
        layout.Place("A", 4, 12); layout.Place("B", 20, 12);   // the tuned net, mid-board
        layout.Place("O1", 4, 14); layout.Place("O2", 20, 14); // the obstacle net, 2 mm above it
        var routed = PcbRouter.Route(layout, Rules, Options).Layout;
        int idx = OnlyTraceOf(routed, "N");
        double current = LengthMatch.Length(routed.Traces[idx]);

        var result = LengthMatch.Tune(routed, idx, current + 30.0, tolerance: 0.05, Rules);

        // whether it Reached, was Untunable, or partial, applying its trace never breaks the DRC.
        routed.ReplaceTrace(idx, result.Trace);
        AssertDrcCleanAndConnected(routed, "N");
        AssertDrcCleanAndConnected(routed, "OBS");
    }

    // ==== 7) tuning is deterministic =========================================

    [Fact]
    public void TuningIsDeterministic()
    {
        var (l1, i1) = RoutedNet(24, 24, new(4, 12), new(20, 12));
        var (l2, i2) = RoutedNet(24, 24, new(4, 12), new(20, 12));
        double target = LengthMatch.Length(l1.Traces[i1]) + 7.0;

        var r1 = LengthMatch.Tune(l1, i1, target, 0.05, Rules);
        var r2 = LengthMatch.Tune(l2, i2, target, 0.05, Rules);

        Assert.Equal(r1.Outcome, r2.Outcome);
        Assert.Equal(r1.Trace.Points, r2.Trace.Points);   // vertex-for-vertex identical
    }

    // ==== 8) an out-of-range index is refused =================================

    [Fact]
    public void AnOutOfRangeTraceIndex_Throws()
    {
        var (layout, _) = RoutedNet(24, 24, new(4, 12), new(20, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => LengthMatch.Tune(layout, 99, 100, 0.05, Rules));
    }
}
