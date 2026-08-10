---
title: "Copper design-rule check"
---

Stage 4 of the ECAD campaign is the **copper DRC** — the geometric check that turns a board's
copper into a pass/fail report against a rule table. It is a region query the [exact 2D
machinery](dxf-svg.md) answers with **no tolerance**: clearance is *grow each net's copper by half
the clearance and require the grown regions disjoint*, where an **empty intersection PROVES the
clearance** (the same tamper-mesh construction the [anti-drill mesh](tamper-mesh.md) rests on),
measured against a closed form rather than eyeballed.

## The load-bearing rule: the netlist decides what should connect

A **short** is copper of **different nets** electrically connected; copper of the **same net**
touching is the *intended* connection and is never flagged. That is the one-declaration rule doing
real work — the geometry and the netlist have to agree, and a DRC that could not tell an intended
join from a short would be useless. Because a pad's net *is* its pin's net (stage 2's pin↔pad
identity), the DRC reads the schematic to know which is which.

## The rules

`DrcRuleSet` is the standard PCB rule table — minimum copper-to-copper **clearance**, **trace
width**, **annular ring**, **drill-to-copper**, **copper-to-board-edge**, and an **acute-angle /
acid-trap** threshold — all in the model's millimetres. The `Default` values are nominal
IPC-2221-ish figures; ⚠ **verify them against your fabricator's capability sheet**, exactly as
`StandardHoles` and `SheetMaterials` are flagged. Because every threshold is *relative*, a rule
set and a board that pass still pass after a uniform scale of both (`DrcRuleSet.Scaled`).

`PcbDrc.Check(layout, rules)` returns a `DrcReport` that **names, locates and measures** every
violation (a report that only said "fail" would be useless — the `PcbLayoutCheck` house style), and
lists the **ratsnest** — signal nets the copper does not yet connect — as *information*, not a
fault, since a bare-pads board before routing is unrouted, not wrong.

## Multi-layer, and where traces come in

Clearance, shorts, trace width and acute angles are **per layer** (top copper does not clear
against bottom copper in-plane); **drill-to-copper is cross-layer**, since a drill goes through the
whole stack. Trace width and the acid-trap rule genuinely want conductors — the copper today is
**pads**, so those rules run on whatever copper a layer carries (a deliberately-thin pad, a sharp
corner) and fully **engage** once stage-5 routing produces traces, since a trace is a stroked
centre-line region through the same `CopperFeature` type the DRC already reads.

```csharp run:ecad-drc
// --- a placed board (the stage-2 fixture), checked clean with an unrouted ratsnest ---
var resistor = new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("R0805", new[] {
        Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
        Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4),
    }));
var header = new PartDefinition("HDR_1x2", "J",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("HDR254", new[] {
        Pad.ThroughHole("1", new Vector2d(-1.27, 0), pad: 1.6, drill: 0.9),
        Pad.ThroughHole("2", new Vector2d(1.27, 0), pad: 1.6, drill: 0.9),
    }));

var sch = new Schematic("blinky");
var r = sch.Add("R1", resistor, "330");
var j = sch.Add("J1", header);
sch.Connect("VCC", j.Pin("1"), r.Pin("1"));
sch.Connect("SIG", r.Pin("2"), j.Pin("2"));

var board = PcbBoard.Rectangle(50, 40, 1.6);
var layout = new PcbLayout(sch, board);
layout.Place("R1", 6, 0);
layout.Place("J1", -8, 0, rotationDegrees: 90);

var clean = PcbDrc.Check(layout);
Console.WriteLine($"placed board: {clean.Violations.Count} violations, ratsnest = [{string.Join(", ", clean.Ratsnest)}]");
// -> 0 violations; VCC and SIG are unrouted (pads only, no traces) — information, not a fault.

// --- a board with a deliberate clearance violation AND a short ---
// Ø1 pads (radius 0.5). A→B centres 1.10 apart => 0.10 gap < 0.15 clearance (a near miss).
// C and D overlap (centres 0.5 apart) => a short between two different nets.
CopperFeature Pad2(string net, string src, double x, double y) =>
    new("Top", net, src, CurvedRegion2d.Disc(new Vector2d(x, y), 0.5));

var rules = DrcRuleSet.Default;   // 0.15 mm clearance, etc.
var bad = new PcbCopperModel(board, new[] {
    Pad2("VCC", "R1.1",  0, 0), Pad2("GND", "R2.1", 1.10, 0),   // clearance
    Pad2("NET3", "R3.1", 10, 0), Pad2("NET4", "R4.1", 10.5, 0), // short
});

var report = PcbDrc.Check(bad, rules);
foreach (var v in report.Violations)
    Console.WriteLine($"  {v.Rule}: {v.Message}");
// The clearance violation's measured gap matches the closed form (1.10 - 1.0 = 0.10).
var near = report.OfRule(DrcRule.Clearance).First();
Console.WriteLine($"clearance measured {near.Measured:g3} mm against {near.Required:g3} mm required");

// --- move the near miss a hair apart: it passes (the empty-intersection proof) ---
var good = new PcbCopperModel(board, new[] {
    Pad2("VCC", "R1.1", 0, 0), Pad2("GND", "R2.1", 1.16, 0),   // 0.16 gap >= clearance
});
Console.WriteLine($"a hair further apart: {PcbDrc.Check(good, rules).Violations.Count} violations");
```

## The incremental seam for routing

A stage-5 router costs a candidate route with `PcbDrc.Violates(model, candidate, rules)` — the
violations a single new `CopperFeature` would introduce against the model as it stands (clearance
and shorts on its layer, copper-to-edge, width and acute angles), without re-running the whole
board. Same-net copper the candidate joins is never a short, so a router that connects a net's pads
is rewarded, not penalised — the ratsnest shrinks as the shorts stay clear.

## Verification

The bar is higher than usual because ECAD fails plausibly (the tamper-mesh guarantee): a known
clearance violation is **found** and a near miss at clearance + ε **passes**, both measured against
the **closed-form gap**; a short **names both nets** while two same-net overlapping pads are never
flagged; annular ring, copper-to-edge and drill-to-copper are checked from **both sides** of their
limit; and a rule set and board that pass still pass after a **uniform scale** (relative, not
absolute, tolerances). The clearance is proven directly — the grown regions' intersection is
asserted empty on a passing board and non-empty on a failing one.
