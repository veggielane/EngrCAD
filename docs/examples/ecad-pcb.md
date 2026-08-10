---
title: "The board and its parts"
---

Stage 2 of the ECAD campaign turns a [schematic](ecad-schematics.md) into a **board**: a plate
with a copper stackup, a set of placed components, and the 3D assembly that falls out of them.
The load-bearing rule carries over — **one declaration produces both**. The schematic graph is
the single source; the board copper, the footprint placement and the 3D bodies all *derive*
from it, so a pin and its pad are one identity (pin `1` ↔ pad `1`) and nothing can drift.

## A board is geometry the kernel already builds

A `PcbBoard` is a polygon outline, a thickness, a copper stackup (top and bottom copper by
default, N layers for a multilayer board) and its own holes (mounting holes and vias). The
**plate** is built with the ordinary `Shape` API — the outline extruded, the holes drilled — so
it is an exact B-Rep with a closed-form volume:

> plate volume = outline area × thickness − Σ πr² × thickness

which is the oracle the tests hold it against (the tessellated volume approaches it from below
by each round hole's inscribed-polygon chord deficit).

## Placing components derives copper, drills and bodies

A `PcbLayout` is a schematic, a board, and a list of placements — each a `(x, y, rotation, side)`
pose naming a component the schematic declares. From that one declaration a placement derives:

- its **footprint pads** projected onto the correct copper layer at the placed pose (an SMD pad
  lands on its side's copper; a through-hole pad appears on every layer);
- its **through-hole pads drilled** into the plate (the same hole serves both faces);
- for a component whose definition carries a 3D `Body`, its **body posed** into the assembly.

A bottom-side placement is a genuine reflection: the body hangs below the board and its
through-holes keep the same world `(x, y)`. The reflection lives on the component's part
transform and its square is the identity (`Mirror(Mirror(x)) == x`), so the board's own +Z — world
up — is never touched (the *FlipX-not-FlipZ* doctrine: the reflection is spent on the part, the
pose stays a proper frame).

## The one-declaration identity check

`PcbLayout.Check()` is the geometric lift of the schematic's pin-counting identity: every pin of
every placed component resolves to **exactly one** placed pad at a known copper location. The two
counts (`PlacedPinCount == PlacedPadCount`, every pin covered once) are exposed so the identity can
be asserted numerically, and the lists it splits into — a pad with no pin, a pin with no pad, a
pad off the board — name which way it failed. A net's pins then resolve to specific copper regions
(`PadsOfNet`), which is the seam DRC and routing (later stages) consume.

```csharp run:ecad-pcb-check
// A schematic — the single source (bodies + footprints named once, instanced as components).
var resistor = new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("R0805", new[] {
        Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
        Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4),
    }),
    body: () => Shape.Box(2.0, 1.25, 0.6).Translate(0, 0, 0.3));
var header = new PartDefinition("HDR_1x2", "J",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("HDR254", new[] {
        Pad.ThroughHole("1", new Vector2d(-1.27, 0), pad: 1.6, drill: 0.9),
        Pad.ThroughHole("2", new Vector2d(1.27, 0), pad: 1.6, drill: 0.9),
    }),
    body: () => Shape.Box(5.08, 2.54, 4.0).Translate(0, 0, 2.0));

var sch = new Schematic("blinky");
var r = sch.Add("R1", resistor, "330");
var j = sch.Add("J1", header);
sch.Connect("VCC", j.Pin("1"), r.Pin("1"));
sch.Connect("SIG", r.Pin("2"), j.Pin("2"));

// A board, and the components placed on it.
var board = new PcbBoard(
    new[] {
        new Vector2d(-20, -15), new Vector2d(20, -15),
        new Vector2d(20, 15), new Vector2d(-20, 15),
    },
    thickness: 1.6,
    holes: new[] {
        new BoardHole(new Vector2d(-17, -12), 3.0, BoardHoleKind.Mounting),
        new BoardHole(new Vector2d(17, 12), 3.0, BoardHoleKind.Mounting),
    });

var layout = new PcbLayout(sch, board);
layout.Place("R1", 6, 0, rotationDegrees: 0, side: CopperSide.Top);
layout.Place("J1", -8, 0, rotationDegrees: 90, side: CopperSide.Top);

// The identity check — the geometric lift of the schematic's pin count.
var check = layout.Check();
if (!check.Ok) throw new Exception(check.ToString());
Console.WriteLine($"pins {check.PlacedPinCount} == pads {check.PlacedPadCount}, identity holds: {check.IdentityHolds}");

// VCC's two pins resolve to two copper locations.
var vcc = layout.Schematic.Nets.First(n => n.Name == "VCC");
foreach (var pad in layout.PadsOfNet(vcc))
    Console.WriteLine($"  VCC -> {pad.Name} at ({pad.World.X:g4}, {pad.World.Y:g4})");

// The plate volume matches the closed form (a through-hole header drilled two 0.9 mm holes).
Console.WriteLine($"plate volume (closed form): {layout.ExpectedPlateVolume():g6} mm^3");

// The layout is a byte-identical save -> load -> save fixed point.
var jsonText = layout.Save();
if (PcbLayout.Load(jsonText, new PartLibrary()).Save() != jsonText)
    throw new Exception("the layout is not a persistence fixed point");
```

## The board as an assembly

`ToAssembly()` builds the drilled plate plus one occurrence per placed component whose definition
carries a body — the same `Assembly` / `PartInstance` flattening the viewer, the BOM and every
exporter already consume. The 3D render below is that assembly:

```csharp render:ecad-pcb-board
var resistor = new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("R0805", new[] {
        Pad.Smd("1", new Vector2d(-1.6, 0), 1.2, 1.4),
        Pad.Smd("2", new Vector2d(1.6, 0), 1.2, 1.4),
    }),
    body: () => Shape.Box(3.2, 1.6, 0.6).Translate(0, 0, 0.3));
var led = new PartDefinition("LED_1206", "D",
    new[] { new Pin("A", "anode", PinType.Passive), new Pin("K", "cathode", PinType.Passive) },
    new Footprint("LED1206", new[] {
        Pad.Smd("A", new Vector2d(-1.6, 0), 1.4, 1.6),
        Pad.Smd("K", new Vector2d(1.6, 0), 1.4, 1.6),
    }),
    body: () => Shape.Cylinder(1.4, 1.2).Translate(0, 0, 0.6));
var header = new PartDefinition("HDR_1x3", "J",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive), new Pin("3", PinType.Passive) },
    new Footprint("HDR254", new[] {
        Pad.ThroughHole("1", new Vector2d(-2.54, 0), 1.6, 0.9),
        Pad.ThroughHole("2", new Vector2d(0.0, 0), 1.6, 0.9),
        Pad.ThroughHole("3", new Vector2d(2.54, 0), 1.6, 0.9),
    }),
    body: () => Shape.Box(7.62, 2.54, 6.0).Translate(0, 0, 3.0));

var sch = new Schematic("blinky");
sch.Add("R1", resistor, "330");
sch.Add("D1", led);
sch.Add("J1", header);

var board = new PcbBoard(
    new[] {
        new Vector2d(-22, -16), new Vector2d(22, -16),
        new Vector2d(22, 16), new Vector2d(-22, 16),
    },
    thickness: 1.6,
    holes: new[] {
        new BoardHole(new Vector2d(-18, -12), 3.2, BoardHoleKind.Mounting),
        new BoardHole(new Vector2d(18, -12), 3.2, BoardHoleKind.Mounting),
        new BoardHole(new Vector2d(-18, 12), 3.2, BoardHoleKind.Mounting),
        new BoardHole(new Vector2d(18, 12), 3.2, BoardHoleKind.Mounting),
    });

var layout = new PcbLayout(sch, board);
layout.Place("R1", 2, 6, 0, CopperSide.Top);
layout.Place("D1", 10, 6, 0, CopperSide.Top);
layout.Place("J1", -12, 0, 0, CopperSide.Top);

var scene = new Scene();
scene.AddTab("Board").Add(layout.ToAssembly());
```

![A small two-layer board with an SMD resistor, an LED and a through-hole header placed on it, rendered as a 3D assembly.](images/ecad-pcb-board.png)

## Multilayer stackups and embedded components

A board is more than two copper layers, and a component is not always on a surface. A full
`LayerStackup` is an ordered list of **copper and dielectric layers, each with a thickness**
(copper–dielectric–…–copper — the standard 2 / 4 / 6-layer builds). The board is extruded through
the *whole* build-up, and each copper plane's z is **derived** from it: the top and bottom coppers
sit at the two faces, the inner coppers between.

A component placed with `Embed(reference, layer, x, y)` seats on an **inner copper layer** — inside
the board, in a cavity milled into the plate. Two embedding styles:

- **Enclosed** (the default): a buried, internal cavity. The build-up above and below stays intact,
  so the die has no external access and its body is strictly inside the board volume.
- **Open cavity**: a well milled down from the placement's face, so the component is accessible and
  the well breaks that surface.

The cavity is sized to the component's footprint (and body) plus a stated clearance, and its volume
comes off the plate **exactly** (footprint-plus-clearance area × depth). The one-declaration
identity holds across layers: an embedded component's pads map to their inner layer's copper, the
identity `Check` still passes, and the copper DRC is fully N-layer aware.

```csharp run:ecad-multilayer-facts
// A 4-layer build-up: Top / prepreg / In1 / core / In2 / prepreg / Bottom.
var stackup = LayerStackup.FourLayer(copper: 0.035, prepreg: 0.2, core: 1.13);
Console.WriteLine($"total thickness = {stackup.TotalThickness:g4} mm (== the sum of every layer)");
foreach (var c in stackup.Coppers)
    Console.WriteLine($"  copper '{c.Name}' at z = {c.Z:g5} mm");

var board = new PcbBoard(
    new[] { new Vector2d(-25, -20), new Vector2d(25, -20), new Vector2d(25, 20), new Vector2d(-25, 20) },
    stackup);

// A die (body + footprint) to bury on an inner layer, and a surface resistor.
var die = new PartDefinition("DIE", "U",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("DIE_SMD", new[] {
        Pad.Smd("1", new Vector2d(-1.5, 0), 1.4, 2.0),
        Pad.Smd("2", new Vector2d(1.5, 0), 1.4, 2.0),
    }),
    body: () => Shape.Box(4.0, 2.5, 0.5).Translate(0, 0, 0.25));
var res = new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("R0805", new[] {
        Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
        Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4),
    }),
    body: () => Shape.Box(2.0, 1.25, 0.5).Translate(0, 0, 0.25));

var sch = new Schematic("stack");
var u1 = sch.Add("U1", die);
var r1 = sch.Add("R1", res);
sch.Connect("A", u1.Pin("1"), r1.Pin("1"));
sch.Connect("B", u1.Pin("2"), r1.Pin("2"));

var layout = new PcbLayout(sch, board);
layout.Embed("U1", "In2", 0, 0, cavityClearance: 0.15);   // buried on the inner layer In2
layout.Place("R1", 14, 0, 0, CopperSide.Top);             // on the surface

// The enclosed cavity removes exactly its pocket, and the die is buried inside the board volume.
var cavity = layout.Cavities().Single();
Console.WriteLine($"cavity removes {cavity.RemovedVolume:g4} mm^3, buried z in [{cavity.ZLow:g3}, {cavity.ZHigh:g3}]");
Console.WriteLine($"plate volume = {layout.ExpectedPlateVolume():g6} mm^3");

// The pin-count identity still holds across layers; U1's pads land on the inner copper.
var check = layout.Check();
if (!check.Ok) throw new Exception(check.ToString());
Console.WriteLine($"identity holds across layers: {check.IdentityHolds}");
var in2 = layout.CopperLayers().First(l => l.Name == "In2");
Console.WriteLine($"In2 copper carries: {string.Join(", ", in2.Pads.Select(p => p.Name))}");
```

The section render below cuts the same idea through the middle, revealing the die sitting in its
cavity between the copper layers:

```csharp render:ecad-multilayer section:y,0
// Look edge-on at the y = 0 cut face, so the section reveals the buried die.
var camera = new CameraState(Math.PI / 2 - 0.55, 0.28, 52, (0, 0, 2.7));

// A thick illustrative build-up so the section reads (total ≈ 5.4 mm).
var stackup = LayerStackup.FourLayer(copper: 0.1, prepreg: 0.5, core: 4.0);
var board = new PcbBoard(
    new[] { new Vector2d(-22, -13), new Vector2d(22, -13), new Vector2d(22, 13), new Vector2d(-22, 13) },
    stackup);

// A die whose body is the cavity's whole depth, buried on the inner layer In2.
var die = new PartDefinition("DIE", "U",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("DIE", new[] {
        Pad.Smd("1", new Vector2d(-2.2, 0), 1.6, 2.4),
        Pad.Smd("2", new Vector2d(2.2, 0), 1.6, 2.4),
    }),
    body: () => Shape.Box(6.0, 4.0, 3.0).Translate(0, 0, 1.5));
var res = new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) },
    new Footprint("R0805", new[] {
        Pad.Smd("1", new Vector2d(-1.0, 0), 1.2, 1.4),
        Pad.Smd("2", new Vector2d(1.0, 0), 1.2, 1.4),
    }),
    body: () => Shape.Box(2.0, 1.25, 0.6).Translate(0, 0, 0.3));

var sch = new Schematic("stack");
var u1 = sch.Add("U1", die);
var r1 = sch.Add("R1", res);
sch.Connect("A", u1.Pin("1"), r1.Pin("1"));
sch.Connect("B", u1.Pin("2"), r1.Pin("2"));

var layout = new PcbLayout(sch, board);
layout.Embed("U1", "In2", 0, 0, cavityClearance: 0.2);   // buried, enclosed, in an internal cavity
layout.Place("R1", 16, 0, 0, CopperSide.Top);            // proud on the top surface

var scene = new Scene();
scene.AddTab("Board").Add(layout.ToAssembly());
```

![A 4-layer board sectioned to reveal a die embedded in an internal cavity between the copper layers, with a surface resistor standing proud on top.](images/ecad-multilayer.png)

An embedded component's cavity wall is a milled edge, so other copper on its seat layer must clear
it (`DrcRule.CavityClearance`), and clearance / shorts are checked **per copper layer including the
inner ones**. Cross-layer via / microvia stitching between layers is a later stage — v1's identity
is **per the pad's own layer**, so a net whose pads sit on different layers reads as unrouted (a
ratsnest) until routing connects them.

## Interchange: IDF import

`IdfReader` imports an IDF 3.0/4.0 board (`.emn`) file — board outline, thickness, drilled holes,
component placements and keep-outs — into a `PcbImport`, honouring the header's unit declaration
(MM / THOU, scaled to millimetres, recorded in `Diagnostics`). IDF carries no connectivity, so
`ToLayout()` synthesizes a data-only schematic (one component per placement, named by package) to
hold the placements against — honest: the layout's identity check then reports the components have
no footprints. `IdfWriter` closes the loop, so `read → write → read → write` is a byte-identical
fixed point for the geometry IDF carries. Section structure is validated up front and a malformed
file is refused by name.

## What is next

Positioning constraints, copper DRC (a region-offset clearance query over the placed pads),
autorouting, panel cutouts and MID/LDS 3D routing are later campaign stages over this one graph —
each reads the netlist↔copper identity stage 2 establishes.
