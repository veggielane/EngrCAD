---
title: "Gerber & Excellon fabrication export"
---

A routed board that cannot go to fab is unfinished. The fabrication export turns the routed
[copper](ecad-drc.md) into the files a board house takes: one **Gerber** (RS-274X) per copper layer, a
**solder-mask**, a **silkscreen** and a **solder-paste (stencil)** Gerber per outer side, a board-outline
Gerber, and an **Excellon** NC-drill program — one complete set, so a routed board can be fully
manufactured *and* reflow-assembled.

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
`-Bottom_Mask.gbr`, `-Top_Silkscreen.gbr`, `-Bottom_Silkscreen.gbr`, `-Top_Paste.gbr`, `-Bottom_Paste.gbr`,
`-Edge_Cuts.gbr` and `.drl` — and reports the file list. `silk.OverExposedCopper(mask)` is the
assembly-side check the caller runs, like the DRC: silk printed onto a solderable land is a real defect,
so every overlap is reported by name (the silk element and the pad) rather than thrown.

## Solder paste (the stencil)

The last fabrication layer is what makes the board **reflow-assemblable**: the **solder-paste stencil**
is a thin sheet with an **aperture** cut over each SMD pad, through which solder paste is squeegeed onto
the lands before the parts are placed and reflowed. So the paste layer is the *aperture* layer, and it
covers **SMD pads only** — a through-hole (plated) pad gets no aperture, because it is wave- or
hand-soldered; pasting one would only foul the barrel. This SMD-only rule is the classic bug the layer
must not have, and it is read straight off the copper model: a pad is SMD when it carries **no drill**.

An aperture is the pad grown by a stated **expansion** (`PcbPaste.For`), whose default is slightly
**negative** — the aperture is a hair *smaller* than the pad (`PcbPasteSettings`, default `-0.05 mm`), to
pull the paste volume in and stop bricks bridging or slumping (⚠ verify against your stencil house). So
an aperture is **exact**: a round pad's is a disc of radius `r + expansion` (area `π(r+e)²`), a rectangular
pad's a rounded rectangle. The paste Gerber images those apertures as dark (the same positive-openings
convention the mask uses — the stencil is cut where the Gerber is dark), so it strokes back through the
*same* twin decoder, and it is **additive** — the copper / mask / silk Gerbers are byte-identical whether
or not paste is requested. Paste settings are **layout truth** (`PcbLayout.PasteSettings`,
write-only-when-stated).

```csharp run:ecad-fab-paste
// A board with an SMD resistor (two SMD pads) AND a through-hole header (two drilled pads).
var sch = new Schematic("paste-demo");
var r = sch.Add("R1", new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("R0805", new[] {
        Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
        Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4) })), "330");
var j = sch.Add("J1", new PartDefinition("HDR_1x2", "J",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("HDR254", new[] {
        Pad.ThroughHole("1", new Vector2d(-1.27, 0), pad: 1.6, drill: 0.9),
        Pad.ThroughHole("2", new Vector2d(1.27, 0), pad: 1.6, drill: 0.9) })));
sch.Connect("VCC", j.Pin("1"), r.Pin("1"));
sch.Connect("SIG", r.Pin("2"), j.Pin("2"));

var board = new PcbBoard(new[] {
    new Vector2d(-10, -8), new Vector2d(10, -8), new Vector2d(10, 8), new Vector2d(-10, 8) }, 1.6);
var layout = new PcbLayout(sch, board);
layout.Place("R1", -4, 0);
layout.Place("J1", 4, 0);

// The paste (and mask/silk) are LAYOUT TRUTH (write-only-when-stated).
layout.WithPaste(new PcbPasteSettings(Expansion: -0.05));

// The full fab set now includes paste: copper + solder mask + silkscreen + solder paste + outline + drill.
var fab = PcbGerberExport.Generate(layout, "paste-demo");
Console.WriteLine($"full fab set: {fab.CopperLayers.Count} copper + {fab.MaskLayers.Count} mask + "
                  + $"{fab.SilkLayers.Count} silk + {fab.PasteLayers.Count} paste Gerbers + outline "
                  + $"+ {fab.DrillHitCount} drill hits");

// The SMD-only rule: paste covers the SMD pads (R1.1, R1.2) and NOT the through-hole pads (J1.1, J1.2).
var model = PcbCopperModel.FromLayout(layout);
var paste = PcbPaste.For(model, layout.PasteSettings);
var pasted = paste.Top.Apertures.Select(a => a.Source).OrderBy(s => s).ToList();
Console.WriteLine($"paste apertures on top: {string.Join(", ", pasted)}");
if (pasted.Any(s => s.StartsWith("J1.")))
    throw new Exception("a through-hole pad was pasted (the SMD-only rule was broken)");

// An aperture equals its SMD pad grown by the expansion, EXACTLY (the offset of a shape is that shape,
// so an R1 rectangular pad's 1.2 x 1.4 aperture shrinks to (1.2 - 0.1) x (1.4 - 0.1) of area).
var padArea = model.Copper.First(f => f.Source == "R1.1").Region.Area;
var apArea = paste.Top.Apertures.First(a => a.Source == "R1.1").Region.Area;
Console.WriteLine($"R1.1 pad area {padArea:F4} mm^2, aperture (−0.05 expansion) {apArea:F4} mm^2");
if (apArea >= padArea)
    throw new Exception("a negative paste expansion did not shrink the aperture");

// The paste Gerber round-trips: decode it back and compare the apertures to the model's, by area and by
// symmetric difference (the DRC's own exact boolean).
var decoded = GerberReader.Read(fab.PasteLayers.Single(l => l.Layer == "Top").Gerber).Copper;
var apertures = paste.Top.Apertures.Select(a => a.Region).ToList();
double apAll = apertures.Sum(x => x.Area);
double sym = CurvedRegion2dBoolean.Difference(apertures, decoded).Sum(x => x.Area)
           + CurvedRegion2dBoolean.Difference(decoded, apertures).Sum(x => x.Area);
Console.WriteLine($"paste round-trip: {decoded.Sum(x => x.Area):F4} recovered vs {apAll:F4} model, "
                  + $"symmetric difference {sym / apAll:E1} relative");
if (sym > 1e-4 * apAll)
    throw new Exception("the solder paste did not survive the round trip");
```

## Pick and place (the assembly centroid file)

The copper set (Gerber + Excellon) builds the bare board; the **pick-and-place (centroid) file** is
what a P&P machine reads to *populate* it — one row per placed component: reference designator, X, Y,
rotation, side, and the value / package identifiers a feeder is matched by. `PcbPickAndPlace.Compute`
projects the layout's placements into rows, and one `Compute` feeds **both** writers (the
drawing-sheet rule), so a CSV centroid and a KiCad-style `.pos` cannot disagree about a pose:

- `PcbPickAndPlace.ToCsv(layout)` — the ubiquitous CSV: `Designator,X,Y,Rotation,Side,Value`.
- `PcbPickAndPlace.ToPos(layout)` — a KiCad-style aligned `.pos`: `Ref Val Package PosX PosY Rot Side`.
- `PcbPickAndPlace.Write(layout, dir)` writes both (`<name>-pos.csv`, `<name>.pos`) and reports the paths.

**The pose is the placement, not the 3D body.** A machine places by the footprint origin, which is the
layout's `PcbPlacement` pose — independent of any 3D-model offset — so a row is exactly the placement.
Units are **millimetres** and **degrees (CCW positive)**, and the board-frame X/Y are reported
**verbatim** (no flip — the repo's coordinate honesty). The one real decision is the **bottom-side
rotation**: a bottom part is physically mirrored (the board is flipped about its X axis to populate it),
which **negates** the board-frame angle, so a bottom row's rotation is `(360 − rotation) mod 360` — a
sign swap, never a `cos`, so a quarter turn is exact. A top row carries the placement rotation verbatim.

The rows are in placement (declaration) order, so the output is a deterministic function of the layout
(two emissions are byte-identical). And the CSV survives the **twin-decoder round trip** —
`PcbPickAndPlace.ParseCsv` reads back what `ToCsv` wrote and recovers the designator, X, Y, rotation, side
and value exactly — the repo's rule that a fab file must survive the round trip, not merely a structural
check.

```csharp run:ecad-pickplace
// A resistor (SMD) and an LED (no footprint — its Package falls back to the definition name).
PartDefinition Res(string package) => new("R_" + package, "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint(package, new[] {
        Pad.Smd("1", new Vector2d(-0.5, 0), 0.6, 0.6),
        Pad.Smd("2", new Vector2d(0.5, 0), 0.6, 0.6) }));

var sch = new Schematic("blinky");
sch.Add("R1", Res("0805"), "330");
sch.Add("R2", Res("0603"), "10k, 5%");   // a comma in the value — the CSV quotes it
sch.Add("D1", new PartDefinition("LED", "D",
    new[] { new Pin("A", PinType.Passive), new Pin("K", PinType.Passive) }), "red");

var board = PcbBoard.Rectangle(40, 30, 1.6);
var layout = new PcbLayout(sch, board);
layout.Place("R1", 3.5, -2.25, 90);                       // top
layout.Place("R2", -7.0, 4.0, 90, CopperSide.Bottom);    // bottom → rotation mirrors to 270
layout.Place("D1", 12.25, 6.5, 270);                     // top

var rows = PcbPickAndPlace.Compute(layout);
Console.WriteLine(PcbPickAndPlace.ToCsv(layout));

// The pose is the placement (a top row's rotation is verbatim), and the bottom row is mirrored.
var r1 = rows.Single(r => r.Designator == "R1");
var r2 = rows.Single(r => r.Designator == "R2");
Console.WriteLine($"R1 top rotation {r1.Rotation} (verbatim); R2 bottom rotation {r2.Rotation} (mirror of 90)");
if (r1.Rotation != 90 || r2.Rotation != 270)
    throw new Exception("the top/bottom rotation convention is wrong");

// The twin-decoder round trip: parse the CSV back and recover every pose exactly (the comma survives).
var back = PcbPickAndPlace.ParseCsv(PcbPickAndPlace.ToCsv(layout));
for (int i = 0; i < rows.Count; i++)
    if (back[i].Designator != rows[i].Designator || back[i].X != rows[i].X || back[i].Y != rows[i].Y
        || back[i].Rotation != rows[i].Rotation || back[i].Side != rows[i].Side || back[i].Value != rows[i].Value)
        throw new Exception($"the pick-and-place CSV did not round-trip row {i}");
Console.WriteLine("centroid round trip: every pose recovered exactly");
```

The aligned `.pos` (`PcbPickAndPlace.ToPos(layout)`) carries the same rows with a `Package` column —
each part's footprint name, or (like `D1`) its definition type name when it has no footprint yet.

## Coordinates and scale

The coordinate format (`%FS`) is derived from the board's own coordinate magnitudes, so the
resolution stays a fixed fraction of the model whatever its scale — a metre-scale board and a
millimetre-scale one both round-trip to the file's precision. The Excellon uses metric, decimal-point
coordinates for the same reason.

## v1 scope

An honest v1: RS-274X Gerber (the modern extended standard, not the obsolete RS-274D with external
aperture files), circle / rectangle / obround / regular-polygon apertures, flashes, draws, region
fills and dark/clear polarity, plus a metric Excellon drill program, the solder-mask, silkscreen and
solder-paste layers above, and the board outline. An unrepresentable copper boundary — a Bézier edge,
which RS-274X region contours cannot carry — is refused **by name** rather than silently flattened, and
the reader (the round-trip oracle, scoped to what the writer emits) refuses a truncated file, a missing
format spec or an aperture macro by name; a mask/silk/paste on a non-outer layer, or a pad window /
aperture off the board, are refused by name too. The assembly **pick-and-place (centroid) file** is
its own output, above. Not in v1, each filed: step / multi-level stencils, paste-volume optimisation,
window-paning of large apertures, fine mask tenting control beyond the tented/opened via policy, curved
conformal mask / silk / paste on a MID surface (refused for the distortion reason), a lowercase silk
font (a value's lowercase advances as a blank), Gerber X2 attributes and the job file, an IPC-D-356
netlist test-point export, and a Gerber IMPORT of a foreign board (this is export).
