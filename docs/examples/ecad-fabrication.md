---
title: "Gerber & Excellon fabrication export"
---

A routed board that cannot go to fab is unfinished. The fabrication export turns the routed
[copper](ecad-drc.md) into the files a board house takes: one **Gerber** (RS-274X) per copper layer, a
**solder-mask** and a **silkscreen** Gerber per outer side, a board-outline Gerber, and an **Excellon**
NC-drill program — one complete, manufacturable set.

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

## Solder mask and silkscreen

The two remaining fabrication layers make the board **assemblable**: the **solder mask** covers the
whole board except a window over each solderable pad (so molten solder wets the land and nothing else),
and the **silkscreen** prints the reference designators and component outlines that tell an assembler
what goes where. Both are derived — the mask windows from the copper pads, the silk from the placements
— and both ride the *same* twin-decoder oracle the copper does.

A mask window is the pad grown by a stated **expansion** (a hair of bare laminate the mask pulls back
to, so a small registration error never lets mask creep onto the land), so it is **exact**: a round
pad's window is a disc of radius `r + expansion`, a rectangular pad's a rounded rectangle. By the
standard positive-openings convention (as KiCad / Eagle), the mask Gerber images those windows as dark
— the fabricator clears mask where the Gerber is dark — so a decoded mask Gerber recovers the windows.
Silk text is line-work (a Gerber has no text primitive), drawn with a round aperture exactly as a trace
draws, so it strokes back through the reader too. The expansion and silk settings are **layout truth**
(they ride in the layout file, write-only-when-stated), and the mask/silk are **additive** — the copper
Gerbers are byte-identical whether or not they are present.

```csharp run:ecad-fab-masksilk
// A round test point (Ø 0.9) and an SMD resistor on a small board.
var sch = new Schematic("mask-silk-demo");
var r = sch.Add("R1", new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("R0805", new[] {
        Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
        Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4) })), "330");
var t = sch.Add("T1", new PartDefinition("TP", "TP",
    new[] { new Pin("1", PinType.Passive) },
    new Footprint("TP_fp", new[] { Pad.Smd("1", new Vector2d(0, 0), 0.9, 0.9, PadShape.Round) })));
sch.Connect("SIG", r.Pin("2"), t.Pin("1"));
sch.Stub("VCC", r.Pin("1"));

var board = new PcbBoard(new[] {
    new Vector2d(-10, -8), new Vector2d(10, -8), new Vector2d(10, 8), new Vector2d(-10, 8) }, 1.6);
var layout = new PcbLayout(sch, board);
layout.Place("R1", -4, 0);
layout.Place("T1", 4, 0);

// The mask expansion and silkscreen are LAYOUT TRUTH (write-only-when-stated).
layout.WithMask(new PcbMaskSettings(Expansion: 0.05))
      .WithSilkscreen(new PcbSilkscreenSettings(ShowValues: true));

// The FULL fab set: copper + solder mask + silkscreen + outline + drill, all as text.
var fab = PcbGerberExport.Generate(layout, "mask-silk-demo");
Console.WriteLine($"full fab set: {fab.CopperLayers.Count} copper + {fab.MaskLayers.Count} mask + "
                  + $"{fab.SilkLayers.Count} silk Gerbers + outline + {fab.DrillHitCount} drill hits");

// A mask window equals its pad grown by the expansion, EXACTLY. The round test point's window is a
// disc of radius 0.45 + 0.05, so its area is pi * 0.5^2.
var model = PcbCopperModel.FromLayout(layout);
var mask = PcbMask.For(model, layout.MaskSettings);
var tp = mask.Top.Openings.Single(o => o.Source == "T1.1");
double analytic = Math.PI * 0.5 * 0.5;
Console.WriteLine($"T1.1 window area {tp.Region.Area:F6} mm^2 vs pi*(0.45+0.05)^2 = {analytic:F6}");
if (Math.Abs(tp.Region.Area - analytic) > 1e-6 * analytic)
    throw new Exception("the mask window is not the pad grown by the expansion");

// The mask Gerber round-trips: decode it back and compare the windows to the model's, by area and by
// symmetric difference (the DRC's own exact boolean).
var decoded = GerberReader.Read(fab.MaskLayers.Single(l => l.Layer == "Top").Gerber).Copper;
var windows = mask.Top.Openings.Select(o => o.Region).ToList();
double windowArea = windows.Sum(x => x.Area);
double sym = CurvedRegion2dBoolean.Difference(windows, decoded).Sum(x => x.Area)
           + CurvedRegion2dBoolean.Difference(decoded, windows).Sum(x => x.Area);
Console.WriteLine($"mask round-trip: {decoded.Sum(x => x.Area):F4} recovered vs {windowArea:F4} model, "
                  + $"symmetric difference {sym / windowArea:E1} relative");
if (sym > 1e-4 * windowArea)
    throw new Exception("the solder mask did not survive the round trip");

// The silkscreen is line-work (a refdes, a value and a body outline per part); it round-trips through
// the same twin decoder, and it must not sit on exposed copper — a clean layout reports none.
var silk = PcbSilkscreen.For(layout, layout.SilkscreenSettings);
var overCopper = silk.OverExposedCopper(mask);
Console.WriteLine($"silk: {silk.Top.Strokes.Count} strokes on top; "
                  + $"{overCopper.Count} over exposed copper");
if (overCopper.Count != 0)
    throw new Exception("silk is printed onto a solderable pad");
```

`PcbGerberExport.Write(layout, dir)` writes the whole set — `-Top.gbr`, `-Bottom.gbr`, `-Top_Mask.gbr`,
`-Bottom_Mask.gbr`, `-Top_Silkscreen.gbr`, `-Bottom_Silkscreen.gbr`, `-Edge_Cuts.gbr` and `.drl` — and
reports the file list. `silk.OverExposedCopper(mask)` is the assembly-side check the caller runs, like
the DRC: silk printed onto a solderable land is a real defect, so every overlap is reported by name
(the silk element and the pad) rather than thrown.

## Coordinates and scale

The coordinate format (`%FS`) is derived from the board's own coordinate magnitudes, so the
resolution stays a fixed fraction of the model whatever its scale — a metre-scale board and a
millimetre-scale one both round-trip to the file's precision. The Excellon uses metric, decimal-point
coordinates for the same reason.

## v1 scope

An honest v1: RS-274X Gerber (the modern extended standard, not the obsolete RS-274D with external
aperture files), circle / rectangle / obround / regular-polygon apertures, flashes, draws, region
fills and dark/clear polarity, plus a metric Excellon drill program, the solder-mask and silkscreen
layers above, and the board outline. An unrepresentable copper boundary — a Bézier edge, which RS-274X
region contours cannot carry — is refused **by name** rather than silently flattened, and the reader
(the round-trip oracle, scoped to what the writer emits) refuses a truncated file, a missing format
spec or an aperture macro by name; a mask/silk on a non-outer layer, or a pad window off the board, are
refused by name too. Not in v1, each filed: paste/stencil layers (the SMD pad set), fine mask tenting
control beyond the tented/opened via policy, curved conformal mask/silk on a MID surface (refused for
the distortion reason), a lowercase silk font (a value's lowercase advances as a blank), Gerber X2
attributes and the job file, and a Gerber IMPORT of a foreign board (this is export).
