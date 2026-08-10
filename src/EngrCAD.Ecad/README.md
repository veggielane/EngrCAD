# EngrCAD.Ecad

Code-defined **schematics**, the **connectivity data model**, the **PCB board + placement** that
derives from them, and the **copper DRC** over the result — stages 1–4 of the ECAD campaign
(schematic → board → placement constraints → copper DRC → routing → MID/LDS 3D routing; see
`todo.md`). It builds on
`EngrCAD.Core` (math) and `EngrCAD.Modeling` (the `Shape`/`Assembly`/`Bom` API the board lowers
to), and — being kernel-tier — has **no viewer/Avalonia dependency**.

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

## Stage 2 — the board and its parts

Stage 2 turns a schematic into a **board** (`PcbBoard`, `PcbLayout`, `PcbAssembly` via
`ToAssembly`) — and keeps the one rule: **one declaration produces both**. The schematic graph is
the single source; the board copper, the footprint placement and the 3D bodies all *derive* from
it, so a pin and its pad are one identity (pin `1` ↔ pad `1`).

| Type | What it is |
| --- | --- |
| `Pad` (extended) | Now carries a `PadKind` (`Smd`/`ThroughHole`) and, for a through-hole pad, a `DrillDiameter` (the annular ring derives). Backward-compatible: a stage-1 SMD footprint saves byte-identically. |
| `PcbBoard` / `PcbStackup` / `CopperLayerSpec` | A polygon outline + thickness + copper stackup (two-layer default, N-layer via `Layers`) + its own `BoardHole`s (mounting/via) and `KeepOut`s. `Plate()` builds the exact B-Rep (outline extruded, holes drilled); `ExpectedPlateVolume()` is the closed-form oracle (area × thickness − Σ πr² × thickness). |
| `PcbPlacement` | A component at `(x, y, rotation, side)` on a board face, naming a schematic component. |
| `PcbLayout` | Schematic + board + placements. Derives `PlacedPads`/`CopperLayers` (pin↔pad → copper), `Plate` (board holes + through-hole pads drilled), `ToAssembly` (board + one occurrence per placed body), and `Check` (the identity check). |
| `PlacedPad` / `CopperLayer` | A footprint pad projected onto the board (world position + the `PinRef` it realises); the placed pad regions per copper layer. |
| `PcbImport` / `IdfReader` / `IdfWriter` | IDF 3.0/4.0 board (`.emn`) import (outline, thickness, holes, placements, keep-outs; units honoured) and a canonical writer for the round trip. |

### Placement geometry

A placement's world transform is exactly the assembly's own `PartInstance.World`
(`WorldOf(placement)` is bit-identical to the flattened occurrence). A **bottom-side** placement
is a genuine reflection on the component's part transform (`Mirror(Mirror(x)) == x`), so the body
hangs below the board and its through-holes keep the same world `(x, y)`, while the board's own
+Z — world up — is never touched (the *FlipX-not-FlipZ* doctrine). A through-hole component drills
the plate by exactly its hole cylinders; an SMD component drills nothing (the plate is
bit-identical to the bare board).

### The one-declaration identity check

`PcbLayout.Check()` is the geometric lift of the schematic's pin-counting identity: every pin of
every placed component resolves to **exactly one** placed pad at a known copper location
(`PlacedPinCount == PlacedPadCount`, every pin covered once). It names its offenders — a pad with
no pin, a pin with no pad, a pad off the board outline, a hole in a keep-out, a component with no
footprint — while placing an unknown reference or a component twice is refused by name at `Place`.
`PadsOfNet(net)` resolves a net's pins to their copper regions, the seam later stages consume.

### Persistence

`PcbLayout.Save`/`Load` extends the schematic seam: the schematic travels **embedded** (the
source, not a copy), the board and placements ride alongside, write-only-when-stated throughout,
so `save → load → save` is a **byte-identical fixed point**. Bodies re-attach from a `PartLibrary`.

### IDF interchange

`IdfReader.Read(emn, emp?)` imports an IDF board into a `PcbImport`, honouring the header unit
(MM/THOU → mm, recorded in `Diagnostics`) and refusing a malformed section structure by name.
IDF carries no connectivity, so `ToLayout()` synthesizes a data-only schematic (a component per
placement). `IdfWriter.Write` closes the loop — `read → write → read → write` is a byte fixed
point for the geometry IDF carries.

## Stage 3 — placement constraints

Stage 3 places components by **constraint** rather than by typed coordinates. The variables are
each free placement's rigid 2D pose `(x, y, θ)` on the board; a rough drawn layout is the *seed*,
and `layout.Constrain()` builds a `ConstrainedLayout` whose `Solve()` returns a **new**
`PcbLayout` at the poses that satisfy the relations — the copper, drills, nets and 3D bodies all
derive from the moved placements, so nothing drifts.

**The solver is the MateSolver doctrine, one layer up** (`PcbConstraintSolver` in `EngrCAD.Ecad`).
The Modeling sketch/mate LM engines are internal/private and bound to their own variable models (3D
6-DOF frames, or free 2D points), and a PCB placement is neither — so this is a *focused* 2D solver
that follows the doctrine exactly: an analytic Jacobian; every residual a length (angular residuals
scaled by the board diagonal, the rotation variable divided by it, so one linear tolerance is
meaningful and every column is O(1)); a rank-revealing DOF report from a diagonally pivoted Cholesky
of JᵀJ at the 1e-6 relative floor; the drawn layout as seed **and** branch selector; an
under-constrained layout reported; a contradiction and a stationary start *named*; a failed solve
leaving the source bit-identically unchanged.

| Constraint | What it fixes |
| --- | --- |
| `Lock` / `Fix` | A placement is a datum (its pose is an input). |
| `Group` / `Cluster` | Several placements move as ONE rigid body. |
| `Orient` / `FixRotation` | A placement's rotation (to an angle, or to where it was drawn). |
| `Distance` / `Spacing` | A stated gap between two points (origins, pads). |
| `AlignX` / `AlignY` | Two points share a coordinate — a column or a row. |
| `Parallel` / `Perpendicular` | Two directions (a component axis, a board edge). |
| `PointOnLine` | A point on a line's carrier at a signed offset (the point-on-line-is-distance-at-zero rule). |
| `AlignEdge` | A component side flush (or at a gap) to a board edge or another side. |
| `InsideRegion` / `InsideBoard` | A footprint stays inside a zone (its bounding circle contained). |
| `ClearOf` / `ClearOfRegion` / `ClearOfKeepOut` | A footprint stays a distance clear of another footprint, or of a keep-out. |

Clearance and containment are **one-sided** (active-set) residuals — they push only when violated
rather than a fake equality — over a footprint modelled by the smallest circle about its origin
enclosing its pads (rotation-invariant, conservative). Constraints persist:
`ConstrainedLayout.Save`/`Load` extends the layout format with a `constraints` array,
write-only-when-stated, so a layout with no constraints is byte-identical to a stage-2 file and a
constrained one is a `save → load → save` byte fixed point. Docs: `examples/ecad-constraints.md`.

## Stage 4 — copper DRC

Stage 4 is the **copper design-rule check** — an exact 2D-region query over a board's copper
against a `DrcRuleSet`. `PcbDrc.Check(layout, rules)` returns a `DrcReport` that **names, locates
and measures** every violation (a report that only said "fail" would be useless — the
`PcbLayoutCheck` house style), plus the **ratsnest** (signal nets the copper does not yet connect)
as *information* rather than a fault.

**The load-bearing rule** is that the DRC reads the netlist to decide what SHOULD connect: a
**short** is copper of DIFFERENT nets electrically connected; copper of the SAME net touching is the
intended connection and is never flagged (the one-declaration identity — a pad's net *is* its pin's
net). Two floating (unconnected / no-connect) pads are electrically distinct and must clear each
other.

| Rule | What it checks |
| --- | --- |
| `Clearance` | Copper of different nets closer than the minimum on one layer. **Grow each net's copper by half the clearance; an EMPTY intersection PROVES the clearance** (`CurvedRegion2dOffset` + `CurvedRegion2dBoolean`). |
| `Short` | Copper of different nets actually overlapping (a stronger failure than a near miss). |
| `AnnularRing` | A drilled pad's copper ring `(min pad dimension − drill) / 2` below the minimum. |
| `DrillToCopper` | A hole closer than the minimum to OTHER-net copper — CROSS-LAYER (a drill goes through the stack). |
| `CopperToEdge` | Copper closer than the minimum to the board outline (an exact polygon inward offset). |
| `TraceWidth` | A conductor narrower than the minimum (`Region2dThickness`' opposing-wall measure). |
| `AcuteAngle` | A copper corner sharper than the acid-trap threshold. |

`DrcRuleSet` transcribes nominal IPC-2221-ish defaults (⚠ verify against your fabricator's
datasheet, like `StandardHoles` / `SheetMaterials`); every length is in the model's mm and
`Scaled(factor)` proves the thresholds are relative (a rule set and board that pass still pass after
a uniform scale). Multi-layer: clearance / shorts / width / acute angles are per layer,
drill-to-copper is cross-layer.

**Traces arrive in stage 5.** Trace width and the acid-trap rule genuinely want conductors; the
copper today is pads, so those rules run on whatever copper a layer carries (a deliberately-thin
pad, a sharp corner) and fully engage once routing produces traces — a trace is a stroked
centre-line region through the same `CopperFeature` type, so the DRC needs no change to reach it.
`PcbCopperModel` is the seam between "what is on the board" and "what the rules say":
`PcbCopperModel.FromLayout(layout)` derives it from placed pads, and the incremental
`PcbDrc.Violates(model, candidate, rules)` is what a stage-5 router costs a candidate route with.
Docs: `examples/ecad-drc.md`.

## Not yet (later campaign stages)

Autorouting, panel cutouts, thermal coupling, MID/LDS 3D routing, and the richer interchange
(KiCad `.kicad_pcb`, STEP AP214 board assemblies) — each a later stage over this one graph. A
drawn schematic **sheet** (symbols and wires to SVG/DXF/PDF via the `DrawingSheet` machinery) is a
VIEW of the graph; `Netlist.ToText()` is the stage-1 textual view.
