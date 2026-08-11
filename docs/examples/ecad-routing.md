---
title: "Autorouting"
---

Stage 5 of the ECAD campaign is the **autorouter** — the genuinely hard stage. It turns a placed
board's [ratsnest](ecad-pcb.md) into copper: `PcbTrace`s and vias that join each net's pins. A bad
autorouter is worse than hand routing, so the bar is non-negotiable and it is the whole point:

> Every routed net **connects** its pins **AND** passes the exact [DRC](ecad-drc.md) — or the router
> **reports failure by name**. It never ships a clearance-violating trace, and a partial result (one
> it could not fully route) is still DRC-clean.

An autorouter that connects while violating clearance is the classic *silent* failure, so the
router is built so that outcome cannot happen.

## The grid is an accelerator; the exact DRC is the source of truth

`PcbRouter.Route(layout, rules, options)` searches a uniform routing grid `(x, y, layer)` with A*,
changes layers through vias, and decomposes a multi-pin net into 2-pin connections over an MST. The
grid is *only* an accelerator: a candidate route is **committed only after the exact DRC confirms it
adds no violation** — [`PcbDrc.Violates`](ecad-drc.md) for every new copper feature (clearance,
short, copper-to-edge, trace width, acute angle) plus the drill and via rules — so a grid rounding
error can never produce a violating trace. If the exact check disagrees with the grid, the exact
check wins and the candidate is rejected, not shipped.

A `PcbTrace` is a net's routed copper on one layer: a polyline centre-line of a given width, whose
copper region is the polyline's exact **stroke** (its Minkowski sum with a disc of radius
`width/2`, round caps and round joins) — precisely the model the DRC's clearance rule grows against,
so a trace and the rule it is checked with cannot disagree. Round joins mean the copper carries no
sharp corner, so a routed trace passes the acid-trap rule with nothing arranged. Traces feed the
[copper model](ecad-drc.md) and the [connectivity engine](ecad-pcb.md), which reads a trace as a
**connector** (like a via), not a terminal — so a net is *connected* when its component **pads** end
up in one copper component.

## Rip-up and reroute

When a net cannot find a clean route, the router routes it **across** the traces that block it (at a
high cost), rips those traces up, and re-queues them — *negotiated congestion*. Each rip-up is
bounded, so a truly boxed-in net terminates and is reported **unroutable by name** rather than
looping. A net whose pin is walled in by other-net copper comes back named; the rest of the board is
still routed and clean.

## A worked example

A tiny two-net board: `GND` runs straight across the middle, and `SIG` must get from the bottom edge
to the top edge — so it *cannot* stay on one layer. The router changes layers through a via, and both
nets come out connected and DRC-clean, with an empty ratsnest.

```csharp run:ecad-routing
// A single-pad test point (0.6 mm), small enough that pads a grid pitch apart start DRC-clean.
PartDefinition Tp(string name) => new(name, "TP",
    new[] { new Pin("1", PinType.Passive) },
    new Footprint(name + "_fp", new[] { Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6) }));

var sch = new Schematic("router-demo");
var g1 = sch.Add("G1", Tp("G1")); var g2 = sch.Add("G2", Tp("G2"));
var s1 = sch.Add("S1", Tp("S1")); var s2 = sch.Add("S2", Tp("S2"));
sch.Connect("GND", g1.Pin("1"), g2.Pin("1"));   // a wall across the board
sch.Connect("SIG", s1.Pin("1"), s2.Pin("1"));   // must cross the wall — needs a via

var board = new PcbBoard(new[] {
    new Vector2d(0, 0), new Vector2d(20, 0), new Vector2d(20, 20), new Vector2d(0, 20) }, 1.6);
var layout = new PcbLayout(sch, board);
layout.Place("G1", 1, 10); layout.Place("G2", 19, 10);
layout.Place("S1", 10, 1); layout.Place("S2", 10, 19);

var rules = new DrcRuleSet(
    MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
    MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80);
var routed = PcbRouter.Route(layout, rules,
    new RouterOptions { GridResolution = 1.0, TraceWidth = 0.4, Clearance = 0.3 });

// The verification bar, in code: every net routed, connected, and the board DRC-clean.
var report = PcbDrc.Check(routed.Layout, rules);
var model = PcbCopperModel.FromLayout(routed.Layout);
bool connected = PcbConnectivity.For(model, "GND").IsConnected
              && PcbConnectivity.For(model, "SIG").IsConnected;

Console.WriteLine(routed);                                                  // routed 2 nets (…, … vias)
Console.WriteLine($"DRC: {report.Violations.Count} violations; ratsnest = [{string.Join(", ", report.Ratsnest)}]");
Console.WriteLine($"both nets connected: {connected}");

if (!routed.FullyRouted || !report.Ok || report.Ratsnest.Count != 0 || !connected)
    throw new Exception("the router must connect every net AND leave the board DRC-clean");
```

The `RoutedResult` carries the routed `PcbLayout` (traces and vias added — the input is not
mutated), the nets that routed, the nets that did **not** (by name), and the counts. A layout with
nothing to route (every signal net already connected) returns unchanged, so `result.Layout.Save()`
is byte-identical to the input's. Routed traces are **layout truth** and round-trip in the
[layout file](ecad-pcb.md).

## Length matching (serpentine tuning)

High-speed buses want their nets matched in **length** (equal propagation delay). `LengthMatch.Tune`
lengthens a routed trace to a target by inserting a serpentine — a comb of rectangular bumps on the
trace's longest segment — and `MatchGroup` tunes a set to the longest member. The comb is the
**square-wave-free** kind (90° corners, no 180° hairpin), and each candidate is committed only after
the exact DRC certifies it adds no clearance violation: a tuned trace is DRC-clean, or the tuning is
refused by name. The added length is measured off the built geometry, never claimed.

```csharp run:ecad-lengthmatch
PartDefinition Tp(string n) => new(n, "TP", new[] { new Pin("1", PinType.Passive) },
    new Footprint(n + "_fp", new[] { Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6) }));

var sch = new Schematic("t");
var a = sch.Add("A", Tp("A")); var b = sch.Add("B", Tp("B"));
sch.Connect("N", a.Pin("1"), b.Pin("1"));
var board = new PcbBoard(new[] {
    new Vector2d(0, 0), new Vector2d(24, 0), new Vector2d(24, 24), new Vector2d(0, 24) }, 1.6);
var layout = new PcbLayout(sch, board);
layout.Place("A", 4, 12); layout.Place("B", 20, 12);

var rules = new DrcRuleSet(
    MinCopperClearance: 0.3, MinTraceWidth: 0.2, MinAnnularRing: 0.2,
    MinDrillToCopper: 0.3, MinCopperToEdge: 0.3, MinAcuteAngleDegrees: 80);
var routed = PcbRouter.Route(layout, rules,
    new RouterOptions { GridResolution = 1.0, TraceWidth = 0.4, Clearance = 0.3 }).Layout;

int idx = Enumerable.Range(0, routed.Traces.Count).First(i => routed.Traces[i].Net == "N");
double current = LengthMatch.Length(routed.Traces[idx]);
var result = LengthMatch.Tune(routed, idx, current + 8.0, tolerance: 0.05, rules);

routed.ReplaceTrace(idx, result.Trace);                       // apply the tuning
var report = PcbDrc.Check(routed, rules);

Console.WriteLine($"{result.Outcome}: {current:0.000} -> {result.AchievedLength:0.000} mm");
Console.WriteLine($"DRC clean after tuning: {report.Ok}");

if (result.Outcome != LengthTuneOutcome.Reached || !report.Ok
    || System.Math.Abs(result.AchievedLength - (current + 8.0)) > 0.05)
    throw new Exception("the tuner must reach the target AND leave the board DRC-clean");
```

The endpoints and net never move — only the middle path lengthens — so connectivity is unchanged. A
target **shorter** than the current length is `Refused` (a serpentine only adds), a target already at
the length is an `Unchanged` no-op, and a trace boxed in on both sides is reported `Untunable` with
how much it *could* add. Filed follow-ups: spreading the comb over several segments, teeth to only the
open side, ripping up a neighbour to make room, and **differential-pair coupled tuning**.

## Differential pairs

A **differential pair** is two nets routed together for controlled impedance and common-mode
rejection, judged by two measured properties: **coupling** (do the two traces run parallel at a
target gap?) and **skew** (do their two halves match in length?). `DiffPairs.Check` measures both,
and `DiffPairs.MatchSkew` equalises the lengths by reusing the DRC-gated serpentine tuner.

```csharp run:ecad-diffpair
PartDefinition Tp(string n) => new(n, "TP", new[] { new Pin("1", PinType.Passive) },
    new Footprint(n + "_fp", new[] { Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6) }));

var sch = new Schematic("t");
var p1 = sch.Add("P1", Tp("P1")); var p2 = sch.Add("P2", Tp("P2"));
var n1 = sch.Add("N1", Tp("N1")); var n2 = sch.Add("N2", Tp("N2"));
sch.Connect("D_P", p1.Pin("1"), p2.Pin("1"));
sch.Connect("D_N", n1.Pin("1"), n2.Pin("1"));
var board = new PcbBoard(new[] {
    new Vector2d(0, 0), new Vector2d(24, 0), new Vector2d(24, 24), new Vector2d(0, 24) }, 1.6);
var layout = new PcbLayout(sch, board);
layout.Place("P1", 4, 10); layout.Place("P2", 20, 10);
layout.Place("N1", 4, 10.3); layout.Place("N2", 20, 10.3);

// Two parallel traces 0.3 mm apart — the hand-routed pair.
string layer = layout.Board.Stackup.Coppers[0].Name;
layout.AddTrace("D_P", layer, 0.2, new[] { new Vector2d(4, 10.0), new Vector2d(20, 10.0) });
layout.AddTrace("D_N", layer, 0.2, new[] { new Vector2d(4, 10.3), new Vector2d(20, 10.3) });

var report = DiffPairs.Check(layout, new DiffPair("D_P", "D_N", TargetGapMm: 0.3));
Console.WriteLine(report.Message);
Console.WriteLine($"well coupled: {report.WellCoupled}, within skew: {report.WithinSkew}, ok: {report.Ok}");

if (!report.Ok)
    throw new Exception("a parallel, length-matched pair must read well-coupled and within skew");
```

`Check` reports the two lengths, the skew, the median gap, and the coupled fraction (1.0 for a
perfectly parallel pair), each net's trace resolved by name. A pair whose nets are not routed (or
routed to several traces) is reported *not checkable* rather than throwing. `MatchSkew` lengthens the
shorter half to the longer, DRC-clean, and hands back the tuned traces for `ReplaceTrace`. **v1 is
analysis + skew tuning, not coupled routing** — filed: coupled routing (routing the two together
while holding the gap), per-segment skew tuning that preserves coupling, and impedance from the
stackup.

## v1 scope

An honest v1: a grid/maze A* with rip-up-reroute, **through-vias** (spanning all copper layers) for
layer changes, and 2-pin MST decomposition of multi-pin nets. Deterministic — a fixed net order and
grid give bit-identical routes. **[Length matching](#length-matching-serpentine-tuning)** and
**[differential-pair analysis](#differential-pairs)** have landed. Not in v1 (each filed):
topological / shove / push routing, differential-pair coupled routing, teardrops, and cavity walls as
routing obstacles.
**[Copper pours / ground planes with thermal reliefs](ecad-pcb.md)** and **[Gerber / Excellon
fabrication export](ecad-fabrication.md)** of the routed board — the fab output — have landed.
