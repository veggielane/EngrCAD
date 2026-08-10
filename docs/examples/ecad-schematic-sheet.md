---
title: "Drawing the schematic sheet"
---

A [code-defined schematic](ecad-schematics.md) is a graph — components, pins, nets. The
**drawn sheet** is the human-readable VIEW of that graph: placed symbols, orthogonal wires
between connected pins, junction dots, net labels, reference designators and values, a border
and a title block, written to **SVG / DXF / PDF**. It replaces `Netlist.ToText()` as the way
to look at a schematic.

## The one rule: the sheet is DERIVED, so it cannot disagree with the netlist

A `SchematicDrawing` is a deterministic FUNCTION of the graph and the placement — the same
schematic and the same placement produce byte-identical SVG. It is never a second editable
thing, so it cannot omit a connection the netlist has, nor invent one it does not. That is why
the drawing carries its own `Verify()`: the connectivity is *reconstructed from the drawn
primitives* (the wire segments, the pin anchors, the net labels) and checked against the
graph.

## A small sheet

The LED indicator — a battery, a current-limiting resistor and an LED — with each part's 2D
`Symbol` and a hand-placed position on the sheet:

```csharp svg:ecad-schematic-sheet
// A 2-pin passive part with a box symbol (pin at the top, pin at the bottom).
PartDefinition Passive(string name, string prefix,
    (string N, string Nm, PinType T) top, (string N, string Nm, PinType T) bot)
{
    var symbol = new Symbol(name,
    [
        new SymbolPin(top.N, top.Nm, new Vector2d(0, 5.08), SymbolPinDirection.Down, 2.54, top.T),
        new SymbolPin(bot.N, bot.Nm, new Vector2d(0, -5.08), SymbolPinDirection.Up, 2.54, bot.T),
    ],
    [new SymbolRectangle(new Vector2d(-1.4, -2.8), new Vector2d(1.4, 2.8))]);
    return new PartDefinition(name, prefix,
        [new Pin(top.N, top.Nm, top.T), new Pin(bot.N, bot.Nm, bot.T)], symbol: symbol);
}

var resistor = Passive("R_0805", "R", ("1", "", PinType.Passive), ("2", "", PinType.Passive));
var led      = Passive("LED_3MM", "D", ("A", "anode", PinType.Passive), ("K", "cathode", PinType.Passive));
var battery  = Passive("BATT", "BT", ("+", "V+", PinType.Power), ("-", "V-", PinType.Ground));

// The schematic (the graph).
var sch = new Schematic("LED indicator");
var bt = sch.Add("BT1", battery);
var r  = sch.Add("R1", resistor, value: "330");
var d  = sch.Add("D1", led);
sch.Connect("VCC",   bt.Pin("+"), r.Pin("1"));
sch.Connect("LED_A", r.Pin("2"),  d.Pin("A"));
sch.Connect("GND",   d.Pin("K"),  bt.Pin("-"));

// Place the symbols by hand (v1 does not invent a good layout).
var placement = new SchematicPlacement()
    .Place("BT1", new Vector2d(40, 60))
    .Place("R1",  new Vector2d(95, 78))
    .Place("D1",  new Vector2d(150, 60));

var sheet = new SchematicSheet(sch, placement,
    format: SheetFormat.Custom("A6", 190, 120),
    title: new TitleBlock { Title = "LED indicator", DrawingNumber = "EX-001", Author = "EngrCAD" });

var svg = sheet.Draw().ToSvg();
```

![The drawn LED-indicator schematic sheet](images/ecad-schematic-sheet.svg)

`VCC` and `GND` are power/ground rails, so each is drawn as a **label** at its pins rather than
one long wire (a `GND` rail is not one wire across the sheet). `LED_A` is an ordinary signal
net, drawn as an orthogonal **wire** from R1.2 to D1.A.

## The verification: the drawing joins exactly the pins the netlist connects

`SchematicDrawing.Verify()` asserts the bar in both directions — no connection omitted, none
invented — reading the drawn primitives, not the router's bookkeeping:

```csharp run:ecad-schematic-verify
PartDefinition Passive(string name, string prefix,
    (string N, string Nm, PinType T) top, (string N, string Nm, PinType T) bot)
{
    var symbol = new Symbol(name,
    [
        new SymbolPin(top.N, top.Nm, new Vector2d(0, 5.08), SymbolPinDirection.Down, 2.54, top.T),
        new SymbolPin(bot.N, bot.Nm, new Vector2d(0, -5.08), SymbolPinDirection.Up, 2.54, bot.T),
    ], [new SymbolRectangle(new Vector2d(-1.4, -2.8), new Vector2d(1.4, 2.8))]);
    return new PartDefinition(name, prefix,
        [new Pin(top.N, top.Nm, top.T), new Pin(bot.N, bot.Nm, bot.T)], symbol: symbol);
}
var resistor = Passive("R", "R", ("1", "", PinType.Passive), ("2", "", PinType.Passive));
var led      = Passive("D", "D", ("A", "", PinType.Passive), ("K", "", PinType.Passive));
var battery  = Passive("BT", "BT", ("+", "", PinType.Power), ("-", "", PinType.Ground));

var sch = new Schematic("LED indicator");
var bt = sch.Add("BT1", battery);
var r  = sch.Add("R1", resistor, value: "330");
var d  = sch.Add("D1", led);
sch.Connect("VCC",   bt.Pin("+"), r.Pin("1"));
sch.Connect("LED_A", r.Pin("2"),  d.Pin("A"));
sch.Connect("GND",   d.Pin("K"),  bt.Pin("-"));

var drawing = new SchematicSheet(sch, SchematicPlacement.Grid(sch)).Draw();
var c = drawing.Connectivity;

// Each net's two pins are JOINED — VCC/GND by a shared label, LED_A by a wire.
if (!c.AreJoined(bt.Pin("+"), r.Pin("1"))) throw new Exception("VCC not joined");
if (!c.AreJoined(r.Pin("2"),  d.Pin("A"))) throw new Exception("LED_A not joined");
if (!c.AreJoined(d.Pin("K"),  bt.Pin("-"))) throw new Exception("GND not joined");

var report = drawing.Verify();
if (!report.Ok) throw new Exception(report.ToString());

// The sheet is a deterministic function of the graph.
if (new SchematicSheet(sch, SchematicPlacement.Grid(sch)).Draw().ToSvg() != drawing.ToSvg())
    throw new Exception("the sheet is not deterministic");

// Write all three formats.
drawing.SaveSvg(Path.Combine(Scratch, "led.svg"));
drawing.SaveDxf(Path.Combine(Scratch, "led.dxf"));
drawing.SavePdf(Path.Combine(Scratch, "led.pdf"));
```

## Placement — hand-placed in v1

Positions are given by a `SchematicPlacement`: `Place(refdes, position, quarterTurns, mirror)`
sets a symbol's origin (sheet millimetres), an orthogonal rotation (90° steps — the schematic
convention) and an optional mirror. A quarter turn is an exact sign swap, so a pin's world
anchor coincides with its wire endpoint to the bit.

`SchematicPlacement.Grid(schematic, format)` is a deterministic grid **placeholder**, clearly
labelled as such — enough to see a schematic at all. A real auto-placer that produces a
*good* layout is its own problem and is deliberately not attempted.

## Wires, junctions and labels

- **Wires** are orthogonal (Manhattan): two pins take an L, three or more a horizontal trunk
  at the pins' median height with a vertical stub from each pin. It is a small SCHEMATIC
  router — no layers, no clearance — and it may cross a symbol or another net (a crossing is
  not a connection); an obstacle-avoiding route is a separate problem.
- **Junction dots** mark points where three or more wires meet (a T or a cross). Two nets
  passing over one another mid-segment are NOT a junction — wires join only where a dot says
  so, the schematic convention.
- **Net labels** carry a net drawn as labels rather than wires. A net is labelled when it is a
  power/ground rail — any pin typed `Power` or `Ground`, or a recognised rail name
  (`VCC`, `GND`, `+3V3`, …) — **or** when its pin count passes the fanout threshold (default
  4). Both are configurable on `SchematicSheetOptions`.

## Refused, by name

- A component with **no symbol** cannot be drawn (attach one, e.g. imported from KiCad by
  [`ComponentLibrary`](ecad-library.md)).
- A net that connects a **pin the symbol does not draw** — the symbol and the netlist
  disagreeing about the part's pins (a [`PinIdentity`](ecad-library.md) failure).
- A component the **placement does not cover** (or pass a null placement to grid-place all).

## See also

- [Code-defined schematics](ecad-schematics.md) — the graph this draws.
- [Loading a component](ecad-library.md) — where symbols come from (KiCad `.kicad_sym`).
- [Engineering drawings](drawings.md) — the mechanical-drawing sheet the SVG/DXF/PDF
  writers are shared with.
