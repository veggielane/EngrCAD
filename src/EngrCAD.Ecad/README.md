# EngrCAD.Ecad

Code-defined **schematics** and the **connectivity data model** — the first stage of the
ECAD campaign (schematic → board → placement constraints → copper DRC → routing → MID/LDS
3D routing; see `todo.md`). This project is connectivity only: the graph and its exact
verification. It builds on `EngrCAD.Core` (math) and `EngrCAD.Modeling` (the optional 3D
body hook), and — being kernel-tier — has **no viewer/Avalonia dependency**.

## The one load-bearing rule: the object graph IS the netlist

A netlist is a graph — components, pins, nets — and the failure mode of every ECAD/MCAD
bridge is two models drifting (a net the copper does not connect, a part the schematic does
not place). So there is **one source and everything else derives from it**: the
`Component`s and `Net`s a `Schematic` holds ARE the connectivity. There is no second
editable netlist to keep in step, and a derived view — a `Netlist` index, a future footprint
placement, a 3D body — reads this one graph. A check or a layout that disagrees with the
schematic is then a bug in a derivation, not a difference between two hand-kept files.

## Types

| Type | What it is |
| --- | --- |
| `PinType` | Electrical character of a pin (`Power`/`Input`/`Output`/`Passive`/…) — enough for a floating-net and a no-connect check, not a SPICE model. `Unspecified` is the `0` value, so the default carries no meaning it was not given. |
| `Pin` | One terminal of a part TYPE: a `Number` (its identity within the part), an optional functional `Name`, and a `Type`. A value. |
| `Footprint` / `Pad` / `PadShape` | The 2D pad layout — **data now**, a placeholder the board-layout stage will consume. Nothing here builds board geometry. |
| `PartDefinition` | A reusable part type: name/designation, an ordered `Pin` list, an optional `Footprint`, and an optional 3D `Body` hook (`Func<Shape>`). The definition is the source; a component is meaningless without one. |
| `Component` | A placed instance of a `PartDefinition`: a reference designator (`R1`, `U3`) and an optional value (`"330"`, `"100nF"`). `component.Pin("1")` names a terminal. |
| `PinRef` | A reference to one terminal of one placed component — the thing a `Net` connects. A reference, not a copy. |
| `Net` / `NetKind` | A named connection of terminals. `Signal` (ordinary), `Stub` (a deliberate single-terminal net — a test point), or `NoConnect` (deliberately unconnected — an explicit first-class state, never a null). |
| `Schematic` | The code-first container: fluent `Add` / `Connect` / `Stub` / `NoConnect`, plus `Check`, `ToNetlist`, `Save`/`Load`. |
| `Netlist` | A **derived, read-only** projection (`net → pins`, `pin → net`) — computed fresh each call, so it cannot drift. `ToText()` is the stage-1 textual listing. |
| `SchematicCheckResult` | The DRC of connectivity (see below). |
| `PartLibrary` | Re-attaches 3D bodies by definition name on load (a body is code, so it does not travel in the file). |

## Declaring one

```csharp
var sch = new Schematic("LED indicator");
var r  = sch.Add("R1", resistor, value: "330");
var d  = sch.Add("D1", led);
var bt = sch.Add("BT1", battery);
sch.Connect("VCC",   bt.Pin("+"), r.Pin("1"));
sch.Connect("LED_A", r.Pin("2"),  d.Pin("A"));
sch.Connect("GND",   d.Pin("K"),  bt.Pin("-"));

var report = sch.Check();          // combinatorial, exact
Console.WriteLine(sch.ToNetlist().ToText());
```

`Connect` creates a net or extends the one of that name, so a rail is declared incrementally
(several `Connect("VCC", …)` build one `VCC` net). `Stub(name, pin)` records a deliberate
single-terminal net; `NoConnect(pins…)` marks terminals deliberately unconnected. A pin from
another schematic, a duplicate reference designator, or an unknown pin number is refused **by
name**.

## Verification — combinatorial and exact

`Schematic.Check()` is the DRC of connectivity, and every list NAMES its offenders:

- **The counting identity.** Every terminal of every component is on exactly one net (signal,
  stub, or no-connect). `TotalPins == PinsCoveredOnce` (with no over-assignments) is exposed
  so the identity can be asserted numerically; `UnassignedPins` and `MultiplyAssignedPins`
  name which way it failed (a floating pin, or a short across two nets).
- **No floating net.** A `Signal` net with fewer than two terminals connects to nothing and is
  named; `Stub` and `NoConnect` nets are exempt by their kind, which is what makes the check
  meaningful rather than noisy.
- **No empty net.**

The guards are shown to FIRE: a pin on two nets, a pin on none, a lone signal net and an empty
net each produce a non-`Ok` report naming the offender.

## Persistence — a byte fixed point

`Save`/`Load` use the document model's JSON conventions (`Document`/`SaveParameters`): a JSON
tree with two-space indent, **write-only-when-stated** optional fields, and no informational
field that cannot round-trip. A `PartDefinition` referenced by many components is written
**once and shared by identity** (a definition entry the components reference by id), exactly
as the document model interns parts, so a net referencing a pin is a reference and not a copy.

`save → load → save` is a **byte-identical fixed point**; two loads of one file produce
structurally identical graphs. What does not travel is the `Body` (it is code); supply a
`PartLibrary` on load to re-attach bodies by definition name. A component whose definition the
file does not contain, or a net referencing a component or pin that does not exist, is refused
**by name** at load (the definition is the source — an instance without one is not loadable).

## Not in stage 1 (later campaign stages)

Board geometry, footprint placement, PCB positioning constraints, copper DRC, routing, MID/LDS
3D routing and interchange (IDF/KiCad/STEP) — each a later stage over this one graph. A drawn
schematic **sheet** (symbols and wires to SVG/DXF/PDF via the `DrawingSheet` machinery) is a
VIEW of the graph and a later deliverable; `Netlist.ToText()` is the stage-1 textual view.
