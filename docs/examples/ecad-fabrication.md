---
title: "Gerber & Excellon fabrication export"
---

A routed board that cannot go to fab is unfinished. The fabrication export turns the routed
[copper](ecad-drc.md) into the files a board house takes: one **Gerber** (RS-274X) per copper layer,
a board-outline Gerber, and an **Excellon** NC-drill program.

`PcbGerberExport.Write(layout, dir)` writes the whole set to disk (and reports what it wrote);
`PcbGerberExport.Generate(layout)` returns the same as text. Pads become aperture **flashes**, traces
become round-aperture **draws** (the stroke a round aperture sweeps is exactly the copper model's
trace region), via pads become solid disc flashes with the drill cleared to leave the annular ring,
and anything else — a rotated pad, a copper pour — becomes a region **fill** (`G36`/`G37`), exact for
any shape.

## The bar: the twin-decoder round trip

A fab file that is subtly wrong scraps a board, so a structural validator is not enough — **the
geometry must survive the round trip**. Alongside the writer is a matching reader
(`GerberReader.Read`, `ExcellonReader.Read`): the copper written is parsed *back* and the recovered
copper must equal the copper model's on each layer to the region-area grade — by area **and** by an
intersection / symmetric-difference check through the DRC's own `CurvedRegion2dBoolean` — while the
decoded drill hits equal the board's holes exactly.

The imaging order is the faithfulness argument, and it reproduces a UNION exactly: the copper on a
layer is a union of feature regions, so a via drill is a hole in the copper *only where nothing
covers it*. A via under a routing trace, or a via directly under a pad (a via-in-pad), is filled —
so the writer lays all the solid copper down first, then clears exactly the holes of the final union,
and a covered drill stays solid, matching the model set for set.

## A worked example

Route a small crossing board, export it, decode the Gerber back, and prove per layer that the
recovered copper equals the routed copper.

```csharp run:ecad-fabrication
// A single-pad test point (0.6 mm), the routing fixture.
PartDefinition Tp(string name) => new(name, "TP",
    new[] { new Pin("1", PinType.Passive) },
    new Footprint(name + "_fp", new[] { Pad.Smd("1", new Vector2d(0, 0), 0.6, 0.6) }));

var sch = new Schematic("fab-demo");
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
    new RouterOptions { GridResolution = 1.0, TraceWidth = 0.4, Clearance = 0.3 }).Layout;

// Export the fab set (Gerber per copper layer + outline + Excellon), all as text.
var fab = PcbGerberExport.Generate(routed, "fab-demo");
var model = PcbCopperModel.FromLayout(routed);

// The oracle: parse each copper Gerber BACK and compare the recovered copper against the model's,
// by area and by symmetric difference (the DRC's own exact boolean).
foreach (var layer in fab.CopperLayers)
{
    var decoded = GerberReader.Read(layer.Gerber).Copper;
    var modelUnion = CurvedRegion2dBoolean.UnionAll(
        model.Copper.Where(f => f.Layer == layer.Layer).Select(f => f.Region).ToList());

    double modelArea = modelUnion.Sum(r => r.Area);
    double decodedArea = decoded.Sum(r => r.Area);
    double symmetric =
        CurvedRegion2dBoolean.Difference(modelUnion, decoded).Sum(r => r.Area)
        + CurvedRegion2dBoolean.Difference(decoded, modelUnion).Sum(r => r.Area);
    double relative = symmetric / Math.Max(modelArea, 1e-30);

    Console.WriteLine($"{layer.Layer,-7}: copper {decodedArea:F4} mm^2 recovered vs {modelArea:F4} model, "
                      + $"symmetric difference {relative:E1} relative");
    if (relative > 1e-6)
        throw new Exception($"copper on '{layer.Layer}' did not survive the round trip");
}

// The drill hits recover exactly.
var hits = ExcellonReader.Read(fab.Drill);
Console.WriteLine($"drill: {hits.Count} hits recovered of {model.Drills.Count} in the model");
if (hits.Count != model.Drills.Count)
    throw new Exception("the drill program did not round-trip the holes");
```

The recovered copper matches the routed copper to the coordinate quantization (well under the
region-area grade), and the drill hits recover exactly. `PcbGerberExport.Write(routed, dir)` writes
`fab-demo-Top.gbr`, `fab-demo-Bottom.gbr`, `fab-demo-Edge_Cuts.gbr` and `fab-demo.drl` to a directory
and reports the file list.

## Coordinates and scale

The coordinate format (`%FS`) is derived from the board's own coordinate magnitudes, so the
resolution stays a fixed fraction of the model whatever its scale — a metre-scale board and a
millimetre-scale one both round-trip to the file's precision. The Excellon uses metric, decimal-point
coordinates for the same reason.

## v1 scope

An honest v1: RS-274X Gerber (the modern extended standard, not the obsolete RS-274D with external
aperture files), circle / rectangle / obround / regular-polygon apertures, flashes, draws, region
fills and dark/clear polarity, plus a metric Excellon drill program. An unrepresentable copper
boundary — a Bézier edge, which RS-274X region contours cannot carry — is refused **by name** rather
than silently flattened, and the reader (the round-trip oracle, scoped to what the writer emits)
refuses a truncated file, a missing format spec or an aperture macro by name. Not in v1, each filed:
solder-mask, silkscreen and paste/stencil layers (the board carries no mask/silk model yet), Gerber
X2 attributes and the job file, and a Gerber IMPORT of a foreign board (this is export).
