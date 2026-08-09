---
title: "Code-defined schematics"
---

An electronic **schematic** in EngrCAD is C#: you declare `Component`s from reusable
`PartDefinition`s and connect their pins into `Net`s, exactly the way `Sketch` declares
curves and `Scene` declares parts. The object graph you build **is the netlist** — there is
no separate capture step and no second file to keep in step.

This is the first stage of the ECAD campaign (schematic → board → placement → copper DRC →
routing). Stage 1 is the connectivity model and its exact verification; a drawn schematic
*sheet* and the board itself come later, and both derive from this one graph.

## The one rule: one declaration, one source of truth

The failure mode of every ECAD/MCAD bridge is two models drifting — a net the copper does not
connect, a part the schematic does not place. So EngrCAD keeps **one source**: the components
and nets a `Schematic` holds ARE the connectivity, and every derived view (a netlist index, a
future footprint placement, a 3D body) reads this one graph. A check or a layout that
disagrees with the schematic is then a bug in a derivation, not a difference between two
hand-kept sources.

## A small schematic

A battery, a current-limiting resistor and an LED:

```csharp run:ecad-schematic
// Part TYPES — declared once, instanced as many components.
var resistor = new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) });
var led = new PartDefinition("LED_3MM", "D",
    new[] { new Pin("A", "anode", PinType.Passive), new Pin("K", "cathode", PinType.Passive) });
var battery = new PartDefinition("BATT_CR2032", "BT",
    new[] { new Pin("+", "V+", PinType.Power), new Pin("-", "V-", PinType.Ground) });

// The schematic: place components, connect their pins into nets.
var sch = new Schematic("LED indicator");
var r  = sch.Add("R1", resistor, value: "330");
var d  = sch.Add("D1", led);
var bt = sch.Add("BT1", battery);

sch.Connect("VCC",   bt.Pin("+"), r.Pin("1"));
sch.Connect("LED_A", r.Pin("2"),  d.Pin("A"));
sch.Connect("GND",   d.Pin("K"),  bt.Pin("-"));

// Verification is combinatorial and exact.
var report = sch.Check();
if (!report.Ok) throw new Exception(report.ToString());

// The counting identity: every terminal is covered exactly once.
if (report.TotalPins != report.PinsCoveredOnce)
    throw new Exception("the counting identity broke");

// The netlist is a DERIVED, read-only view of the graph.
Console.WriteLine(sch.ToNetlist().ToText());

// save -> load -> save is a byte-identical fixed point.
var json = sch.Save();
if (Schematic.Load(json).Save() != json)
    throw new Exception("the schematic is not a persistence fixed point");
```

`Connect(name, …)` creates a net or extends the one of that name, so a rail is declared
incrementally. The derived netlist prints:

```text
Schematic: LED indicator
Components (3):
  R1     R_0805            = 330
  D1     LED_3MM
  BT1    BATT_CR2032
Nets (3):
  VCC         BT1.+, R1.1
  LED_A       R1.2, D1.A
  GND         D1.K, BT1.-
```

## Verification — the DRC of connectivity

`Schematic.Check()` runs three combinatorial checks, each of which NAMES its offenders (a
check that only said "invalid" would be useless):

- **The counting identity.** Every terminal of every component is on exactly one net — a
  signal net, a deliberate stub, or an explicit no-connect. `TotalPins == PinsCoveredOnce`
  (with no over-assignments) is the identity; `UnassignedPins` names a floating pin and
  `MultiplyAssignedPins` names a short across two nets.
- **No floating net.** A `Signal` net with fewer than two terminals connects to nothing.
- **No empty net.**

Both halves fire. A floating pin and a short are each caught by name:

```csharp run:ecad-check
var part = new PartDefinition("R_0805", "R",
    new[] { new Pin("1", PinType.Passive), new Pin("2", PinType.Passive) });

var sch = new Schematic();
var r1 = sch.Add("R1", part);
var r2 = sch.Add("R2", part);

sch.Connect("A", r1.Pin("1"), r2.Pin("1"));
sch.Connect("B", r1.Pin("1"), r2.Pin("2"));   // R1.1 shorted onto a second net
// R1.2 left on no net at all.

var report = sch.Check();
if (report.Ok) throw new Exception("the check should have failed");

// R1.2 is a floating pin, and R1.1 is over-assigned — both named.
if (!report.UnassignedPins.Contains("R1.2"))
    throw new Exception("the floating pin was not named");
if (!report.MultiplyAssignedPins.Any(m => m.Contains("R1.1")))
    throw new Exception("the shorted pin was not named");
```

## No-connect and stub — explicit, first-class

An unconnected pin is a deliberate declaration, never a null. `NoConnect(pins…)` records it,
and `Stub(name, pin)` records a deliberate single-terminal net (a test point) — both exempt
from the floating check, which is what keeps the check meaningful rather than noisy:

```csharp
sch.NoConnect(u.Pin("5"));          // pin 5 is genuinely not connected
sch.Stub("VOUT_TEST", tp.Pin("1")); // a single-terminal net on purpose
```

A no-connect pin that is *also* wired to a signal net is a violation the check names.

## Persistence

`Save`/`Load` use the document model's JSON conventions (`Document`/`SaveParameters`):
write-only-when-stated fields, and no informational field that cannot round-trip. A
`PartDefinition` used by many components is written **once and shared by identity**, so a net
referencing a pin is a reference and not a copy. `save → load → save` is a **byte-identical
fixed point**, and two loads of one file produce structurally identical graphs.

A part's 3D `Body` is code, so it does not travel in the file; pass a `PartLibrary` to
`Schematic.Load` to re-attach bodies by definition name. A component whose definition the file
does not contain, or a net referencing a component or pin that does not exist, is refused **by
name** at load — the definition is the source, so an instance without one is not loadable.
