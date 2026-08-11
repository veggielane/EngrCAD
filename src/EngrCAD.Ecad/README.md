# EngrCAD.Ecad

Code-defined **schematics**, the **connectivity data model**, the **PCB board + placement** that
derives from them, the **copper DRC** over the result, and the **autorouter** that turns the
ratsnest into DRC-clean copper — stages 1–5 of the ECAD campaign
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
| `PartDefinition` | A reusable part type built from its three views — a 2D `Symbol`, a `Footprint` and a 3D `Model` — plus a name/designation and an ordered `Pin` list (with the legacy `Body` hook, `Func<Shape>`, kept as a code model with the identity placement). Every view is optional; the definition is the source, a component is meaningless without one. |
| `Symbol` / `SymbolPin` / `SymbolGraphic` | The 2D SCHEMATIC symbol — graphic primitives (`SymbolPolyline`/`SymbolRectangle`/`SymbolCircle`/`SymbolArc`/`SymbolText`) plus a `SymbolPin` per terminal carrying the pin NUMBER, name, the `Anchor` where a wire lands, a `SymbolPinDirection`, a length and a `PinType`. The representation a drawn schematic **sheet** consumes. |
| `ComponentModel3D` / `ModelPlacement` | The 3D MODEL — a first-class peer of the symbol and footprint. Unifies a body SOURCE — a FILE reference (`.stl`/`.obj`/`.off`/`.step`, which travels as DATA and loads on demand) or a `Func<Shape>` (code, opaque like the legacy `Body`) — with a `ModelPlacement` (offset/rotate/scale) relative to the footprint origin. A quarter turn is exact (a sign swap, not a `cos`); `.wrl`/`.igs` are recorded but not loaded (refused by name). |
| `PinIdentity` / `PinIdentityReport` | The one-declaration identity check: symbol pin `"1"` == footprint pad `"1"` == netlist pin `"1"`. Names every symbol pin with no pad, pad with no pin, or pin with neither. |
| `Component` | A placed instance of a `PartDefinition`: a reference designator (`R1`, `U3`) and an optional value (`"330"`, `"100nF"`). `component.Pin("1")` names a terminal. |
| `PinRef` | A reference to one terminal of one placed component — the thing a `Net` connects. A reference, not a copy. |
| `Net` / `NetKind` | A named connection of terminals. `Signal` (ordinary), `Stub` (a deliberate single-terminal net — a test point), or `NoConnect` (deliberately unconnected — an explicit first-class state, never a null). |
| `Schematic` | The code-first container: fluent `Add` / `Connect` / `Stub` / `NoConnect`, plus `Check`, `ToNetlist`, `Save`/`Load`. |
| `Netlist` | A **derived, read-only** projection (`net → pins`, `pin → net`) — computed fresh each call, so it cannot drift. `ToText()` is the stage-1 textual listing. |
| `SchematicCheckResult` | The DRC of connectivity (see below). |
| `PartLibrary` | Re-attaches 3D bodies by definition name on load (a body is code, so it does not travel in the file). |
| `ComponentLibrary` / `LoadedPart` | Loads a component from KiCad interchange (`.kicad_sym` + `.kicad_mod`) so it arrives with its symbol, footprint and pins unified by pin number. `LoadedPart` carries the `PartDefinition`, its `PinIdentityReport` and the readers' diagnostics. |
| `KiCadSymbolReader` / `KiCadFootprintReader` | Hand-rolled dependency-free S-expression readers (`SExpr`) for the KiCad symbol and footprint formats — the common subset mapped, the rest refused/ignored **by name** (the `StepReader`/`IgesReader` ethos). |
| `EagleLibraryReader` / `EagleLibrary` | Reads an Eagle `.lbr` (XML) into the SAME `LoadedPart`. `Read` → the library's `Devices`; `Load(deviceName)` → one `PartDefinition`. The deviceset's `<connect gate pin pad>` map unifies symbol pins and package pads by PAD number. Rides the BCL `System.Xml.Linq` (dependency-free, the 3MF/AMF precedent). |

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

### KiCad `.kicad_pcb` whole-board interchange

`KiCadPcbReader.Read(text)` / `ReadFile(path)` imports a whole KiCad board (`.kicad_pcb`) into a
`KiCadPcb` (the reconstructed `PcbLayout` + diagnostics) — the **board twin of the KiCad component
reader**, reusing the same hand-rolled `SExpr` parser and the same covered-subset / refuse-by-name
discipline. It reconstructs the `(general)` thickness, the copper `(layers)` stackup (every `.Cu`
layer, F.Cu first, mapped to a `PcbStackup`), the board outline from the `Edge.Cuts` graphics
(`gr_line` chained, `gr_rect`/`gr_poly`, `gr_arc` flattened), each `(footprint)` as a placed
data-only `PartDefinition` with its pads, the `(net)` table, copper `(segment)`/`(arc)` tracks,
`(via)`s, and copper `(zone)`s as pours.

**Like IDF, a bare board carries no schematic, so the reader synthesizes one from the pads' own net
tags** — a footprint becomes a `PartDefinition`, and each pad's `(net n name)` reconstructs the nets
the design intended. That is what makes "the connectivity matches what KiCad intended" a *checkable*
claim rather than a hope: the pads' net tags ARE the schematic, and `PcbConnectivity` then confirms
the imported copper (tracks, vias, zones) actually joins the pads KiCad tagged. **No additive change
to the board types was needed** — everything builds through the existing public constructors
(`PcbBoard`/`PcbStackup`, `Schematic.Add`/`Connect`/`Stub`, `PcbLayout.Place`/`AddTrace`/`AddVia`/
`AddPour`), because the pads' net assignment already carries the connectivity the board file lacks a
schematic for.

**Coordinate convention.** KiCad stores Y downward; the reader imports coordinates VERBATIM into the
board frame (no Y-flip, noted in `Diagnostics`), which is what "pad centres exact from the file's mm
coordinates" means AND is internally consistent (pads, tracks, vias, zones and the outline share one
frame) — all connectivity, the copper DRC and Gerber export need. A footprint rotation is a CCW
rotation in that frame.

**The fabrication spec is recovered too, best-effort and write-only-when-stated.** A `.kicad_pcb`'s
`(setup (stackup ...))` block carries the fab-package fields the geometry cannot — so the reader
populates a `PcbFabricationSpec` (`layout.Fabrication`) from it: the stackup's TOTAL (sum of every
stated layer thickness) → finished board thickness, the first copper layer's thickness ÷ **0.035 mm**
(1 oz = 35 µm = 0.035 mm, the industry rounding of 34.79 µm and KiCad's own 1 oz thickness — ⚠
verify-against-datasheet) → copper weight in ounces, the first dielectric layer's `(material ...)` →
base material, `(copper_finish ...)` → a named `PcbSurfaceFinish` (an unmapped string → `Other`
carrying the verbatim name, noted), the outer mask/silk layers' `(color ...)` → the mask/silk
colours, and any legacy default net class's `trace_width`/`clearance` → the minimum trace width /
clearance. **Only a field the file actually states is set** — every numeric field gated
finite-and-positive (a garbage value dropped, not crashed on) — so a board with **no stackup imports
byte-identically** (`Fabrication` stays null, the saved layout has no `fabrication` key), and a
stackup board's populated spec **round-trips** through the layout file as a `save → load → save`
fixed point (persistence already carries the spec, so no writer change was needed).

Verified higher than usual (an import that connects the wrong pads is a silent failure): the
**net connectivity matches KiCad's intent** (each multi-pad net connected via its tracks/via/zone,
the GND zone joining every GND pad — and removing the zone leaves GND an unrouted ratsnest, the
mutation that proves the zone connects them); the board is **DRC-clean** with a known-violation
fixture (a copper short between two nets) FOUND; **pad centres are exact** from the file's mm
coordinates (including a 90°-rotated footprint); the imported copper **round-trips to Gerber** and
re-reads (the twin-decoder oracle); determinism; refusals by name; and the **component reader stays
bit-identical** (a new reading path, nothing shared moved). Ignored/refused by name: keepout / rule
areas, teardrops, dimension graphics, 3D-model references, a netless track/via/zone, an arc track
(flattened, noted). Filed: the KiCad `.kicad_sch` schematic and EXPORT of our board to `.kicad_pcb`
(a different, larger job). Docs: `examples/ecad-pcb.md`.

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
| `CavityClearance` | Copper of another component closer than the copper-to-edge minimum to an embedded component's cavity wall (a milled edge), on the cavity's seat layer. See Stage 4b. |
| `ViaToVia` | Two vias' drilled holes closer than the minimum via-to-via web (a manufacturing spacing between drills, applied to all via pairs regardless of net). See Stage 5 prerequisite. |

`DrcRuleSet` transcribes nominal IPC-2221-ish defaults (⚠ verify against your fabricator's
datasheet, like `StandardHoles` / `SheetMaterials`); every length is in the model's mm and
`Scaled(factor)` proves the thresholds are relative (a rule set and board that pass still pass after
a uniform scale). Multi-layer: clearance / shorts / width / acute angles are per layer,
drill-to-copper is cross-layer.

**IPC-6012 class presets + a spec cross-check.** `DrcRuleSet.ForIpcClass(1|2|3)` is the nominal rule
set for an IPC-6012 performance class (Level A/B/C ↔ class 1/2/3). A DRC minimum is a FLOOR the design
must clear, and a stricter class REQUIRES more copper, so **every minimum grows with the class and
class 3 is the strictest** — the DRC direction for a minimum annular ring exactly (Level C leaves the
most copper). Class 2 is field-identical to the Class-2-ish `Default`, so the preset spreads around
it; ⚠ nominal figures, verify-against-datasheet. Being an ordinary `DrcRuleSet`, a preset **drives
`PcbDrc` with no change to the check** (a gap that clears class 2 fails the stricter class 3).
`DrcRuleSet.CheckSpec(spec)` cross-checks a `PcbFabricationSpec`'s own stated minimum trace width /
clearance against the class it *claims* → an `IpcClassCheck`: a spec naming a strict class but stating
a minimum LOOSER (finer) than that class's floor is **`NonConforming`**, each offender named with the
stated value AND the class minimum; a spec whose stated minimums meet its class **`Conforming`**; a
spec with no class, or a class but no minimum to compare, is **`NotCheckable`** with a reason (never
invented into a verdict). Docs: `examples/ecad-drc.md`.

**Traces arrive in stage 5.** Trace width and the acid-trap rule genuinely want conductors; the
copper today is pads, so those rules run on whatever copper a layer carries (a deliberately-thin
pad, a sharp corner) and fully engage once routing produces traces — a trace is a stroked
centre-line region through the same `CopperFeature` type, so the DRC needs no change to reach it.
`PcbCopperModel` is the seam between "what is on the board" and "what the rules say":
`PcbCopperModel.FromLayout(layout)` derives it from placed pads, and the incremental
`PcbDrc.Violates(model, candidate, rules)` is what a stage-5 router costs a candidate route with.
Docs: `examples/ecad-drc.md`.

## Stage 4b — multilayer stackups and embedded components

Stage 2's `PcbStackup` is copper-only (named copper planes at z-heights). Stage 4b adds the full
physical build-up and components inside the board, keeping the copper-only / surface path
**byte-identical**.

| Type | What it is |
| --- | --- |
| `LayerStackup` / `StackLayer` / `StackLayerKind` | The physical build-up: an ordered list of copper AND dielectric layers, each with a thickness, top-most first. `TotalThickness` is the sum; the copper planes' z are **derived** — outer coppers at the two faces (top at `TotalThickness`, bottom at exactly 0), inner coppers at their midplanes. `FourLayer`/`SixLayer`/`TwoLayer` factories; `CopperStackup` is the derived `PcbStackup` every stage-2..4 consumer reads. A board carries a null `LayerStackup` when built the copper-only way. |
| `Embedding` | How a placement sits in z: `Surface` (the default — on an outer copper face, proud), `Enclosed` (buried in an internal cavity, build-up intact above and below), `OpenCavity` (a well milled to the placement's face). |
| `EmbeddedCavity` | The resolved geometry of one embedded component's cavity — the milled pocket, its z-range, the exact removed volume, and the board-local wall the DRC clears copper against. Derived from the placement + footprint + body (the one-declaration rule). |

`PcbBoard` gains a `LayerStackup` constructor (thickness = the stackup total, copper = its derived
planes) and a `LayerStackup` property (null for copper-only boards). `PcbLayout.Embed(reference,
layer, x, y, embedding, cavityClearance, side)` seats a component on an inner copper layer at its z
(`SeatZ`); `Cavities()` resolves the milled pockets, `Plate()` subtracts them (an internal void for
`Enclosed`, a well for `OpenCavity`), and `ExpectedPlateVolume()` is the plate's own closed form less
each cavity's `lateral area × depth`. `EmbeddedBodyBounds(placement)` is the containment oracle — an
enclosed body is strictly inside the outer extruded prism, a surface body is proud.

**The identity holds across layers**: an embedded component's SMD pads land on its inner seat layer
(`CopperLayers`/`PadsOfNet` return them there), `Check()` still passes, and the copper DRC is fully
N-layer aware — clearance and shorts per copper layer including inner ones, plus `CavityClearance`
(other copper clearing an embedded part's milled cavity wall on its seat layer; the part's own pads
are exempt). Persistence writes the full `layerStackup` (or the copper `stackup`) and the placement's
`layer`/`embedding`/`cavityClearance` write-only-when-stated, so a stage-2..4 file is byte-identical
and a multilayer/embedded one is a `save → load → save` fixed point. Refused **by name** at `Embed`:
an unknown layer, a negative clearance, a missing footprint/body, a cavity that would breach a
surface or the outline, or an overlap with another cavity (two cavities on different inner layers
with disjoint z do not overlap — stacked dies). **v1's identity is per the pad's own layer** —
cross-layer via/microvia stitching between board layers is a later stage, so a net whose pads sit on
different layers reads as an unrouted ratsnest until routing. Docs: `examples/ecad-pcb.md`.

### Exploded views — `ToExplodedAssembly()`

A full `LayerStackup` makes the board a sandwich the kernel can take apart. `PcbLayout.ToExplodedAssembly(spacing?, name?)`
slices the plate into **one slab per physical layer** — the outline extruded over that layer's own
z-range (from `LayerStackup.Extents`, the same bottom-up accumulation the copper z's come off, never
recomputed), drilled by every through hole and milled by every embedded cavity that reaches its
z-range — and assembles them with the placed components (surface AND embedded), fanned along the
**stackup normal** (`BoardFrame.Z`). It is the sibling of `ToAssembly()` (the board as one part),
leaves it untouched, and returns an ordinary `Assembly`, so the exploded-view slider,
`ExplodeTrack` and every exporter animate it with **no new code** (the offsets are
`Occurrence.ExplodeOffset`/`ExplodePath`, Modeling-level values, so this needs no viewer dependency).

The explode is decided by the one relationship a board has — its z-stacking:

- **Layers fan up from the BOTTOM layer as the datum** (it stays put — the natural datum, since the
  stackup accumulates from z = 0). A layer's offset is `n · gap · rank` from the bottom, so **stack
  order is explode order** and, because the offset adds to the layer's original contiguous position,
  `gap` is the clean empty gap between consecutive layers whatever their thickness.
- **Surface components** lift off their face — top up clear of the fan, bottom down below the datum.
  Pure Z.
- **Embedded components** come out of their cavity along Z first, then spread aside — an `ExplodePath`
  **dogleg** (leg 1 pure ±n out of the cavity, the final offset carrying the lateral step so the die
  does not tunnel through the layers above it; a diagonal reads as *insert at an angle*).

The slabs are DISJOINT, tile `[0, TotalThickness]` exactly, and their **union is the plate**
(`Σ slab volume == ExpectedPlateVolume()`). At factor 0 the whole assembly is the assembled board —
each component's world transform is bit-identical to `ToAssembly`'s — so an un-exploded flatten is
the board in place. A board built the **copper-only way** (null `LayerStackup`) is refused by name:
there is no modelled dielectric to slice, so use `ToAssembly` for a single-slab board. It is
off-render-thread work (building slabs is geometry); the animation is then matrices only. Docs:
`examples/ecad-pcb.md` (with a committed APNG of a 4-layer board opening).

## Vias and net connectivity — the routing prerequisite

The precursor to autorouting, and what completes the one-declaration identity ACROSS LAYERS.

| Type | What it is |
| --- | --- |
| `Via` / `ViaType` | A net-carrying, plated cross-layer connection at `(x, y)` spanning copper layers `[From, To]`, with a drill and an annular pad diameter. The `ViaType` (`Through` / `Blind` / `Buried` / `Microvia`) is **derived from the span**, never stored twice — through = outer face to outer face, blind = an outer face to an inner, buried = inner to inner, microvia = a single dielectric hop (adjacent copper layers). |
| `PlacedVia` | The resolved copper of a via — centre, net, derived type, the ordered layers it touches, and the annular pad region (exact area `π(pad² − drill²)/4`). Carried by `PcbCopperModel.Vias`. |
| `PcbConnectivity` / `NetConnectivity` / `ConnectivityReport` | The net-connectivity engine — the seam an autorouter reuses. |

`PcbLayout.AddVia(net, x, y, from, to, drill, pad, require?)` / `WithVia(via)` place a via (refused
by name for **no net**, a **non-positive drill**, a **pad not larger than the drill**, an **unknown
layer**, a **zero-span** via, a centre **off the outline**, or a **derived type not matching
`require`** — the way a caller states "this is a microvia" and is refused when the layers are not
adjacent). `ViaTypeOf`/`ViaLayers`/`PlacedVias` are the derived facts; vias round-trip in the layout
file (layout truth, write-only-when-stated — a via-free layout is byte-identical). A via places an
annular pad on **every** copper layer it touches, tagged with its net, plus one drilled hole — fed
into `PcbCopperModel`, so the general clearance / drill-to-copper / annular-ring rules reach a via
as ordinary copper for free. The one genuinely new DRC rule is `ViaToVia` (the drill web).

**`PcbConnectivity`** is the heart. It builds a per-net graph over the net's copper features
(component pads, via pads, and — later — traces): two features join when they **touch on the same
layer** (an exact `CurvedRegion2dBoolean.Intersection`, no tolerance) OR are the two ends of a
**plated barrel** (a via, or a through-hole pad, whose per-layer copies share a source). A net is
**connected** when all its component pads lie in one connected component. This **closes the
multilayer caveat** ("a net whose pads sit on different layers reads as an unrouted ratsnest") — a
via that touches each pad is a real connection, and the DRC's ratsnest now delegates to this engine
(`PcbDrc.Ratsnest`), so `PcbLayout.Connectivity()`/`IsNetConnected(net)` answer it. A via on the
**wrong net** does not connect (only same-net features are nodes); a floating/redundant via never
makes a connected net read unconnected (via pads are connectors, not terminals). Docs:
`examples/ecad-pcb.md`.

## Loading a component — the KiCad interchange

A `PartDefinition` carries three views of one part — its pins, its footprint, and (now) its 2D
schematic `Symbol` — and a real library is *imported* rather than declared by hand.
`ComponentLibrary.Load`/`Read` reads the **KiCad** interchange (`.kicad_sym` + `.kicad_mod`, the
primary open ubiquitous format) so a part arrives with all three at once, and unifies them by
pin NUMBER: **symbol pin `"1"` == footprint pad `"1"` == netlist pin `"1"`**. That identity is
verified by `PinIdentity.Check` (on the returned `LoadedPart`), which names any symbol pin with
no pad, pad with no pin, or pin with neither — the one-declaration rule extended to the drawn
symbol, which is the whole point of loading symbol and footprint together.

```csharp
var part = ComponentLibrary.Load("R_0805.kicad_sym", "R_0805_2012Metric.kicad_mod");
// part.Definition has Pins + Footprint + Symbol; part.Identity.Ok; part.Diagnostics names what
// the readers could not carry. ComponentLibrary.LoadFromPretty resolves the .kicad_mod from a
// .pretty directory by the symbol's referenced footprint name.
```

The readers are hand-rolled and dependency-free (`SExpr` is a small S-expression parser),
validating structure up front and refusing malformed input **by name** (the
`StepReader`/`IgesReader` rule). They cover the **common subset** and NAME the rest:

- **Symbol** (`.kicad_sym`): the `Reference`/`Footprint` properties, the **unit sub-symbols** (each
  `<name>_<unit>_<style>` kept as its own `Symbol` in `PartDefinition.Units`, unit `0` common to
  every unit), `rectangle`/`circle`/`arc`/`polyline`/`text` graphics, and `pin`s (electrical type →
  `PinType`, name, number, position, angle → `SymbolPinDirection`, length). A `SymbolPin.Anchor` is
  the connection point where a wire lands, and the direction points from there into the body (KiCad's
  pin angle convention). A bezier graphic, an alternate pin function, a De Morgan alternate body
  style (`_1_2`), or an electrical type with no exact `PinType` is ignored **with a diagnostic**; two
  units disagreeing about one pin are reported by name (the first is kept).

**Multi-unit symbols.** A dual op-amp is ONE physical package (one footprint, one reference
designator) drawn as SEVERAL schematic symbols — amp A, amp B, a power unit. A `PartDefinition` gains
`Units` (one `Symbol` per unit, each with its own pins at its own anchors) while `Pins` is their
**UNION** — the netlist terminals of the whole package — and the pin NUMBER identity spans the units
(`PinIdentity.Check` takes the union of every unit's pins). A single-unit / symbol-less definition is
**byte-identical** to before: `Symbol` is the sole unit (or null), `Units` derives from it, and the
`units` constructor parameter is OPTIONAL and LAST (passed INSTEAD of `symbol`, never both). The board
side is one component with all pads — units are a schematic-drawing/placement concern. In a
`.kicad_sch` a multi-unit part is placed as several `(symbol …)` instances SHARING one reference
designator, each with a distinct `(unit N)`; `KiCadSchReader` **merges** them into one `Component` and
places only that instance's unit's pins at that instance's location (so a net wired to amp A's output
and one to amp B's input are distinct nets on the same IC). A repeated placement of one unit, or two
different symbols under one reference designator, is reported and skipped. Persistence writes the
per-unit symbols under a `units` key (a single-unit definition keeps the incumbent `symbol` key, so it
saves byte-identically); `save → load → save` is a byte-identical fixed point. Verified higher than
usual (a wrong merge silently mis-wires an IC): a dual op-amp parses to three units with the right
per-unit pins and the union; a sheet placing its three units under "U1" imports as EXACTLY one
component with all eight pins (the mutation against the old two-component behaviour); the two units'
nets are distinct and land on the right pins; a net physically SPANS the two amp units; identity spans
the units; persistence and determinism; the inconsistent-units and De Morgan refusals by name.
Multi-unit schematic DRAWING (placing each unit at its own sheet location) is a filed follow-up.
- **Footprint** (`.kicad_mod`): SMD and plated through-hole pads of the standard shapes
  (`circle`/`rect`/`roundrect`/`oval`) with their `at`/`size`/`drill` — mapped onto the existing
  `Footprint`/`Pad` with **no change to `Pad` or `PadShape`** (the drill a through pad needs was
  already there from stage 2, so the board side that reads footprints is untouched). Pad centres
  and sizes are STATED in the file, so they are carried exactly; a pad rotation, a
  `trapezoid`/`custom` shape or an oval drill is approximated **with a note**.

A loaded `Symbol` is DATA now, so a `PartDefinition` with a symbol round-trips through the
schematic file as a **byte-identical fixed point**; a symbol-less definition serializes exactly
as before (write-only-when-stated). A KiCad footprint's `(model …)` is carried too (see below):
the loaded part arrives with its 3D model REFERENCE.

## The trinity — symbol + footprint + 3D model

A component is now built from its THREE views — a `Symbol` (drawn on the schematic sheet), a
`Footprint` (the copper) and a `ComponentModel3D` (the 3D body) — all sharing one pin-NUMBER
identity. The `Model` is a **first-class peer**, not a bare `Func<Shape>`: it unifies a body
SOURCE with a `ModelPlacement` relative to the footprint origin, in the KiCad `(model …)` shape.

```csharp
// A file-referenced model (travels as data, loaded on demand) with a placement:
var model = ComponentModel3D.FromFile(
    "models/R_0805.step",
    new ModelPlacement(offset: (0, 0, 0.35), rotationDegrees: (0, 0, 90)));
var def = new PartDefinition("R_0805", "R", pins, footprint, symbol: sym, model: model);
// A code model is the other source kind (opaque, like the legacy Body):
var coded = ComponentModel3D.FromShape(() => Shape.Box(2, 1.25, 0.5).Translate(0, 0, 0.25));
```

- **Two source kinds.** A **file** reference (`.stl`/`.obj`/`.off` via `Shape.From`, `.step` via
  `StepReader`) travels through the schematic/board file as DATA and loads on demand
  (`model.TryLoad`/`model.Load`); a **code** model (a `Func<Shape>`) stays OPAQUE and is
  re-attached from a `PartLibrary`, exactly like the legacy `Body`. The legacy `Body` IS the
  spelling of a code model with the identity placement, so a `Body`-only definition seats
  **bit-identically** to before.
- **The placement seats into the pose.** `PcbLayout.ToAssembly` bakes the `ModelPlacement`
  (offset/rotate/scale) into the body BEFORE the side reflection and the placement pose, so it is
  applied in the footprint's own frame and a bottom-side component's model is reflected along with
  its footprint. An IDENTITY placement applies no transform at all (the bit-identity guarantee),
  a quarter turn is EXACT (a sign swap, so a 90° rotate transposes the footprint-plane bounds to
  the last bit), and a scale is exact.
- **Loading is an explicit act, and refusals are by name.** Constructing a model never touches
  the filesystem, so a data-only load that only references a path is honest and complete for
  persistence and connectivity. `.wrl` (VRML — KiCad's default 3D format, so this WILL be hit) has
  no reader, and `.igs`/`.iges` is filed; both are RECORDED but refused by name at load, and a
  missing/unreadable file is a not-loaded reference (a reason), never a data-load crash.
- **Persistence.** A file-referenced model round-trips (`{ path, offset?, rotate?, scale? }`,
  write-only-when-stated) as a **byte-identical fixed point**; a definition with no model — or with
  a code model (opaque) — writes no `model` key, so a pre-model file is byte-identical.
- **KiCad.** A footprint's `(model "path" (offset (xyz …)) (rotate (xyz …)) (scale (xyz …)))`
  becomes the definition's `Model` (a `FromFile` reference; offset in mm, rotate in degrees, scale
  unitless). The file is not force-loaded — an empty library directory is normal — so the
  reference is recorded and loaded on demand.

## Loading a component — the Eagle `.lbr` interchange

`EagleLibraryReader` is the SECOND interchange reader, producing the SAME `LoadedPart`. An Eagle
library is a single XML file, so it rides the BCL's `XDocument` (dependency-free, the
`ThreeMfWriter`/`AmfWriter` precedent for XML) rather than a hand-rolled parser — same
refuse-by-name ethos. `Read(xml)` returns an `EagleLibrary` whose `.Devices` lists every loadable
device by its full name (deviceset + device); `.Load(deviceName)` (or the static
`EagleLibraryReader.Load(xml, name)`) resolves one into a `PartDefinition`.

```csharp
var lib = EagleLibraryReader.ReadFile("passives.lbr");   // lib.Devices : the loadable devices
var part = lib.Load("R-EU_R0805");                        // -> LoadedPart (pins + footprint + symbol)
```

**The `<connect gate pin pad>` map is what unifies the three, and it is the structural difference
from KiCad.** An Eagle symbol's pins are named in the symbol's own vocabulary (`"1"`, `"VCC"`), a
package's pads are numbered, and a **deviceset**'s `<connect>`s bind them — symbol pin `"VCC"` →
pad `"8"`. So the loaded pin's NUMBER is the pad, its NAME is the symbol pin's name, and its symbol
pin, footprint pad and netlist pin all carry the same number, which is exactly what
`PinIdentity.Check` verifies. Eagle stores coordinates in the XML in MILLIMETRES, so pad centres
and pin anchors are carried EXACTLY (a pin's `rot` gives its direction — `R0` points +x into the
body — and its `length` token gives its length). Covered: symbol `wire`/`rectangle`/`circle`/
`polygon`/`text` graphics and `pin`s; package `smd` pads and `pad` plated through-holes of the
standard shapes (round/square/octagon/long). Ignored/refused BY NAME: a package `<hole>`/`<via>`
(not a pad), a graphic kind outside the set, a **multi-gate deviceset** (a gate array, refused at
`Load`), a symbol pin with no `<connect>` (an **unmapped pin**, refused), and whole `.brd`/`.sch`
board/schematic import (refused at the root).

**No additive change to `Symbol`/`Footprint`/`PartDefinition`/`PinIdentity` was needed** — the
Eagle primitives all mapped onto the existing vocabulary, exactly as KiCad's did — so the KiCad
path is BIT-IDENTICAL by construction (nothing shared moved), and an Eagle-loaded part round-trips
through the schematic file as the same byte-identical fixed point.

## Loading a whole schematic — the KiCad `.kicad_sch` interchange

`KiCadSchReader.Read(text)` / `ReadFile(path)` imports a whole KiCad schematic (`.kicad_sch`) into a
`KiCadSchematic` (the reconstructed `Schematic` + diagnostics) — the SCHEMATIC twin of the board
reader (`KiCadPcbReader`), reusing the same `SExpr` parser and the same covered-subset /
refuse-by-name discipline. Symbol parsing is shared with the `.kicad_sym` reader
(`KiCadSymbolReader.ParseSymbolList` over the embedded `lib_symbols`), so it lives in ONE place — a
schematic's `lib_symbols` are the same grammar as a symbol library's symbols, and the `.kicad_sym`
path stays bit-identical (its `Read` now delegates to the shared core).

**The crux is that a schematic never lists its netlist — it DRAWS it.** A board file tags every pad
with its net; a schematic file has no such tag, so the reader RECONSTRUCTS connectivity from the
geometry, the same "two things are one net iff they touch" rule `PcbConnectivity` uses on copper. A
**union-find over the connection POINTS**:

- a WIRE joins its two endpoints;
- a component PIN anchor, a LABEL, a POWER-symbol pin or a JUNCTION lying ON a wire joins that wire
  — so a junction at an X-crossing joins BOTH crossing wires, while a plain crossing with no
  junction stays two nets (the junction dot is the schematic convention);
- two points carrying the same net LABEL are one net (label equivalence);
- a `no_connect` marks an isolated pin as deliberately unconnected.

Points coincide at a weld tolerance (1e-4 mm): KiCad coordinates are exact grid decimals and a
placed pin's anchor is an exact isometry of them (library Y-up flipped to sheet Y-down, plus the
rotation), so points that should coincide differ only by IEEE round-off. Power symbols are net-name
markers (their `Value` is the net name), not components. This is exactly the rule
`SchematicDrawing.Verify` asserts, run the other way round.

**Coverage** of `Read` is a single sheet: embedded `lib_symbols` → `PartDefinition`s (interned per
`lib_id`, so two `Device:R` instances share one definition), placed `(symbol …)` instances (Reference
→ refdes, Value → value), power symbols, `wire`, `junction`, local `label`, `global_label`,
`no_connect`, and **buses** (see below). **Refused BY NAME**: bus GROUP labels (`{…}` named groups /
aliases) and a malformed bus range; and — in the single-sheet `Read` only — hierarchical sheets
(`sheet` subsheets, `hierarchical_label`), so a flat import cannot silently drop a whole subsheet; a
`(sheet_instances …)`, present in every flat sheet, is NOT a subsheet and passes. A netless wire, an
instance referencing an unknown symbol, a dangling pin, or a dangling / non-member bus entry is
REPORTED (a diagnostic), never thrown; a non-`(kicad_sch …)` root or a malformed S-expression is
refused by name.

**Buses are sugar that EXPANDS to member nets.** A bus is a labelled bundle of signals: a bus-VECTOR
label `DATA[m..n]` on a `(bus …)` wire declares the members `DATA`+m..`DATA`+n (so `DATA[0..7]` is
DATA0..DATA7; a reversed `DATA[7..0]` is the same eight), and a `(bus_entry …)` rips a member off the
bus onto a signal wire. **The honest finding is that a ripped tap's net is its OWN local label** — KiCad
requires the ripped wire labelled with a member, and same-named labels are already one net by local-label
equivalence — so on a single flat sheet the bus's CONNECTING role is entirely subsumed by that
equivalence, and the bus model's job reduces to (a) declaring the member NAMESPACE (so a bus-vector
label is not mistaken for a signal net — `DATA[0..7]` is never a net) and (b) VALIDATING each tap
against it (a stray tap, a dangling entry, or a bad range is reported / refused by name). The
connecting role becomes load-bearing only ACROSS sheets (hierarchical bus pins), so buses stay refused
in the hierarchical entry points; a bus GROUP (`{…}`) needs its own member-set resolution and is out of
scope. Verified higher than usual (a mis-expanded bus is a silent failure): the member PARTITION is
reconstructed exactly with `Check()` passing, and the MUTATION that proves it bites is a RELABELLED tap
— moving a tap's label moves its pin to a different member (a positional / membership-blind importer
would pass the partition test and fail this) — plus reversed-range parsing, the plain-net
non-contamination, and the bad-range / non-member / dangling-entry reports.

### Hierarchical / multi-sheet import

`KiCadSchReader.ReadProject(rootPath)` reads a **hierarchical** design — a root `.kicad_sch` plus the
sub-sheet FILES it references through `(sheet … (property "Sheetfile" …))`, resolved relative to the
root's directory, recursively — into ONE flattened `Schematic`; `ReadProjectFrom(rootFile, sheetsByFile)`
is the testable IN-MEMORY twin over a `sheetfile → text` map (the disk entry point is a thin wrapper
over it). The single-sheet `Read`/`ReadFile` are unchanged and still refuse a hierarchical design by
name.

**Cross-sheet net stitching is the whole job, and it is NAME-matching layered on the same geometric
union-find.** Each sheet instance's local connectivity is reconstructed with the flat rule (wires,
on-wire attachments), tagged by INSTANCE so two sheets with a wire at the same `(x, y)` do not join;
then the nets are stitched across sheets:

- a parent **sheet pin** joins the parent net at its position to the sub-sheet's `hierarchical_label`
  of the **same name** (name-matched, scoped to that child instance);
- a **`global_label`** or a **power symbol** joins every sheet that carries that name;
- a **local `label`** stays LOCAL to its sheet — two sheets' "CLK" locals are two nets (the scoping
  crux; a local net's name is qualified by its sheet path so the two stay distinct).

Components get **hierarchical reference designators** — `"PowerSupply/U1"` (the `PartInstance`
occurrence-path convention) — so a sheet placed TWICE gives distinct instances (`"Amp1/U1"`,
`"Amp2/U1"`) with distinct internal nets. **Refused / reported by name**: a **recursive** sheet
reference (a sheet including itself, directly or transitively) is refused by name (a self-including
hierarchy cannot be flattened); a **missing / unreadable** sub-sheet file is reported and its subtree
skipped (never thrown), as is a hierarchical label with no matching parent sheet pin (a dangling
port). Still out of scope across sheets: buses (multi-unit symbols merge in the hierarchical path too,
keyed by the hierarchical refdes). The oracle is the same "reconstructed from geometry + name-matching"
partition asserted exactly, plus the MUTATION that proves the stitch bites — rename the sub-sheet's
hierarchical label and the parent/child net SPLITS.

Verified higher than usual (an importer that mislabels nets is a silent failure): the reconstructed
partition matches the intended one exactly (with `Schematic.Check()` passing); the MUTATION that
proves the reader reads geometry (move a wire endpoint off a pin → the net splits, the pin
reported); the junction rule from both sides (a crossing needs a junction to join); label
equivalence (same label = one net, different = two); no_connect (an isolated marked pin is on no
signal net); the symbol == netlist pin identity (`PinIdentity`); determinism (two reads save
byte-identically); and the refusals. Filed follow-ups: bus GROUPS (`{…}` aliases) and buses across
sheets (hierarchical bus pins). Docs: `examples/ecad-library.md`.

## Drawing the schematic sheet

The human-readable VIEW of a schematic: a drawn SHEET — placed symbols, orthogonal wires,
junction dots, net labels, reference designators + values, a border and a title block —
written to **SVG / DXF / PDF**. It **replaces `Netlist.ToText()`** as the way to look at a
schematic, and it is deliberately a VIEW: a `SchematicDrawing` is a **deterministic function of
the graph and the placement**, derived so it cannot disagree with the netlist (the
one-declaration rule) — the same schematic and placement produce byte-identical SVG.

| Type | What it is |
| --- | --- |
| `SchematicSheet` | A schematic + a `SchematicPlacement` + a paper size + a title block. `Draw()` computes the `SchematicDrawing`. Refuses **by name** at construction: a component with no `Symbol`, a net on a pin the symbol does not draw (a `PinIdentity` mismatch), a component the placement does not cover. |
| `SchematicPlacement` | Where each component's symbol sits (`Place(refdes, position, quarterTurns, mirror)`). Hand-placed in v1; `Grid(schematic, format)` is a deterministic grid PLACEHOLDER (a good auto-layout is a separate problem, not attempted). |
| `SymbolPose` | A symbol's origin, an orthogonal rotation (90° steps — the schematic convention) and an optional mirror. The transform is EXACT (a quarter turn is a sign swap), so a pin's world anchor coincides with its wire endpoint to the bit. |
| `SchematicSheetOptions` | The net-label rule (fanout threshold, power-net names) plus a few sizes. |
| `SchematicDrawing` | The computed sheet: `Segments`/`Junctions`/`Texts`/`Pins`/`Buses`, the `Connectivity`, `Verify()`, and the writers `ToSvg`/`ToDxf`/`ToPdf` (+ `Save*`). |
| `DrawnConnectivity` / `DrawnConnectivityReport` | The connectivity the drawing EXPRESSES, reconstructed from its primitives (wire segments, pin anchors, net labels) — `AreJoined(a, b)`, `LabelOf(pin)`. `Verify()` asserts the drawn sheet joins exactly the pins the netlist connects, BOTH ways. |
| `SchematicBus` / `SchematicBusEntry` / `DrawnBus` | A caller-declared bus (`buses:` on the sheet): a thick bundle wire (`Path`), diagonal entry stubs (`Entries`) and a bus-VECTOR label `NAME[m..n]` (member `NAME`+i). DRAWING SUGAR — the members are the signal wires' own labels, so a bus draws a bundle but connects NOTHING; its line-work is kept off the wire graph, so `Verify()` is unaffected and a bus can never merge two nets. |

**Wires** are orthogonal (Manhattan): two pins take an L, three or more a horizontal trunk at
the pins' median height with a vertical stub from each pin, so interior stubs make **junction
dots** (a T or cross of wires — a crossing is not a connection). It is a small *schematic*
router — no layers, no clearance — and v1 may cross a symbol or another net; an
obstacle-avoiding route is a separate problem. **Net labels** carry a net drawn as labels
rather than wires: a power/ground rail (any pin typed `Power`/`Ground`, or a recognised rail
name) or a net past the fanout threshold (default 4).

The verification is the house style — every net's connected pins are JOINED (by a wire path or
a shared label) and no two pins on different nets are joined — reconstructed from the drawn
primitives so the drawing cannot omit a connection the netlist has nor invent one it does not.

**Buses** are a caller-declared bundle (`SchematicBus`, passed to the sheet with `buses:`): a
THICK bundle wire, diagonal ENTRY stubs (`SchematicBusEntry`) ripping members off it, and a
bus-VECTOR label `NAME[m..n]` (member `NAME`+i — the KiCad notation the bus import reads). They
are **DRAWING SUGAR** on a new `bus` layer: the members are the signal wires' own labels, so a
bus draws a bundle but **connects nothing** — its line-work is deliberately kept OUT of the wire
graph, so a bus wire crossing two member wires cannot merge their nets and `Verify()` is
unaffected. The bus-wire pen is `SchematicSheetOptions.BusWireWidth` (default 0.8 mm, wider than
a wire's 0.5). A sheet declaring **no** bus is byte-identical (buses are opt-in). Caller-declared,
never auto-routed. Filed: bus GROUPS (`{…}` aliases) and buses ACROSS sheets.

**The border and title block come from the shared `DrawingFrame`** (Modeling) — the SAME value
type the mechanical [drawing sheet](../EngrCAD.Modeling/README.md) draws, so a schematic and a
mechanical drawing of one project share one look and cannot DRIFT. The frame is one pure
function of its parameters, and a schematic passes its own two-band `SchematicTitleBlock` and
the ECAD schematic layers; that is the only thing that differs (the mechanical sheet passes the
three-band engineering layout). `SchematicSheet.Frame()` exposes it. The frame's opt-in
`FrameStandards` (the ISO 5457 zone grid and centring marks) reach a schematic via the
`standards:` constructor argument, off by default so an existing schematic sheet is
byte-identical. Docs: `examples/ecad-schematic-sheet.md`.

## Stage 5 — the autorouter

The genuinely hard stage: turn the ratsnest into copper (`PcbTrace`s and vias) that joins every
net's pins. `PcbRouter.Route(layout, rules, options)` → `RoutedResult` is a DRC-aware grid/maze A*
router — a uniform routing grid `(x, y, layer)`, vias to change layers, 2-pin MST decomposition of
multi-pin nets, and rip-up-and-reroute when a net is boxed in by congestion.

**The bar** (an autorouter that connects while violating clearance is the classic silent failure):
every routed net **connects** its pins **AND** passes the exact DRC, or the router **reports failure
by name** — never a silent violation, and a partial result is still DRC-clean.

**The grid is an accelerator; the exact DRC is the source of truth.** A candidate route is committed
only after `PcbDrc.Violates` (plus the drill / via rules) confirms it adds no violation to the board,
so a grid rounding error can never produce a violating trace. If the exact check disagrees with the
grid, the exact check wins and the candidate is rejected.

| Type | What it is |
| --- | --- |
| `PcbTrace` | A net's routed copper on one layer: a polyline centre-line of a given width, whose copper is the polyline's exact **stroke** (round caps/joins — the Minkowski sum with a disc, precisely the DRC's clearance model; round joins carry no acute corner). Layout truth — round-trips in the file. |
| `PcbRouter.Route` | The router. Returns a NEW routed layout (the input is not mutated); a layout with nothing to route returns byte-identical. |
| `RouterOptions` | Grid resolution, trace width, clearance, angles (45°/90°), vias on/off, via sizes, rip-up bound, net order — every field clearance-derived by default. |
| `RoutedResult` | The routed layout, the nets routed / **unrouted by name**, the counts, and the rip-up count. `FullyRouted` when every net routed. |

`PcbConnectivity` reads a trace as a **connector** (like a via), so a net is *connected* when its
component pads end up in one copper component. **Rip-up** routes a blocked net across the traces that
block it, rips those up, and re-queues them (negotiated congestion), bounded so a boxed-in net
terminates and is reported unroutable. Deterministic — a fixed net order and grid give bit-identical
routes. **v1 scope**: through-vias (all copper layers); NOT topological/shove routing, length
matching, differential pairs, copper pours, teardrops, or cavity walls as obstacles. Docs:
`examples/ecad-routing.md`.

## Copper pours — ground / power planes

A `CopperPour` floods a copper layer on one net (a ground plane, a power plane). It is **layout
truth** — declared on the layout (`layout.AddPour(...)`), it round-trips in the file — and it derives
into copper features `PcbCopperModel.FromLayout` adds, so the DRC and the connectivity engine read a
pour exactly like any other copper. A GND pour **joins every GND pad it touches**, so the GND
ratsnest empties.

| Type | What it is |
| --- | --- |
| `CopperPour` | The declaration: net + layer + optional outline, fill (solid / hatched), clearances, thermal-relief and hatch settings, and a dead-copper policy. Refuses a nonexistent net/layer or an off-board outline by name. |
| `CopperPourBuilder.Fill(baseModel, pour)` → `PouredPour` | Fills a pour against the board's base copper (pads/vias/traces) and returns the kept connected region(s) plus diagnostics (dead-copper count/area, relieved pads, spokes). |
| `ThermalRelief` / `HatchStyle` | The relief spoke pattern and the crosshatch pattern; `ThermalRelief.None` floods a through-hole pad. |

**The fill region is exact and the tamper-mesh construction** — the board area (or a stated outline)
inset from the edge, **minus** every other-net copper feature and drill grown by the clearance,
**minus** a thermal-relief annulus around each same-net through-hole pad. Built with
`CurvedRegion2dOffset` / `CurvedRegion2dBoolean`, no tolerance, so the pour clears every other net *by
construction* and a poured board passes `PcbDrc.Check` (the empty grown-intersection is the proof,
the same rule the DRC uses).

**Thermal relief** keeps a same-net through-hole pad solderable: an annular air gap around the pad,
bridged by thin radial spokes (four on the diagonals by default). The pad stays *connected* through
the spokes and *relieved* by the gap — a relief that disconnects the pad and a pad that floods are
the two classic bugs, so a test asserts BOTH. SMD pads and vias are direct-connected (flooded).
Spokes meet the plane at ~90° corners, so a poured board with reliefs wants an acid-trap threshold at
or below 90°.

**Dead copper** — a piece of the pour the net cannot reach (walled off by other-net copper) — is
removed by default and always reported (`PouredPour.DeadCopperArea`); `DeadCopperPolicy.Keep` keeps
it. Each kept connected component becomes a copper feature with its own source, so two disjoint pieces
stay disjoint in connectivity — a pour never force-joins pads its copper does not actually bridge.
`PourFill.Hatched` intersects the fill with a crosshatch grid (region ∩ a line pattern) for a lighter
plane. A pour exports to Gerber as a `G36`/`G37` region fill and round-trips (an other-net pad in a
pour's clearance hole is a copper island the clear pass re-darkens, so it survives the round trip).
**v1 does no inter-pour priority** (each pour is filled against the base copper, not other pours),
custom relief geometry beyond the spoke default is filed, and conformal placement on a curved wall is
not offered. Docs: `examples/ecad-pcb.md` (Copper pours).

## Fabrication export — Gerber (RS-274X) + Excellon

The fab output that makes a routed board manufacturable AND reflow-assemblable.
`PcbGerberExport.Write(layout, dir)` writes the full set — one **Gerber** per copper layer, a
**solder-mask**, a **silkscreen** and a **solder-paste (stencil)** Gerber per outer side, a board-outline
Gerber, and an **Excellon** NC-drill program (and reports what it wrote); `PcbGerberExport.Generate(layout)`
returns the same as text.

| Type | What it is |
| --- | --- |
| `GerberWriter` / `GerberBuilder` | RS-274X (extended Gerber): the format spec, an aperture table (circle/rectangle/obround/regular-polygon `%ADD`s), pads as flashes (`D03`), traces as round-aperture draws (`D01`/`D02`), region fills (`G36`/`G37`) and dark/clear polarity. `MaskLayer` images mask windows dark, `PasteLayer` images stencil apertures dark; `Silkscreen` draws the line-work. |
| `GerberReader` / `GerberImage` | The TWIN DECODER — parses exactly what the writer emits and reconstructs the copper/mask/silk/paste as `CurvedRegion2d`s per layer. The round-trip oracle. |
| `ExcellonWriter` / `ExcellonReader` / `DrillHit` | The NC-drill program (a tool per distinct diameter + the hits) and its twin decoder. Metric, decimal coordinates. |
| `PcbMask` / `PcbMaskSettings` / `MaskOpening` | The solder mask, derived from the copper model: a window (the pad grown by the `Expansion`) over every solderable pad on each outer layer; vias tented or opened. The whole board is mask except these windows. |
| `PcbSilkscreen` / `PcbSilkscreenSettings` / `SilkStroke` / `SilkFont` | The silkscreen, derived from the placements: a reference designator (and optionally value) as stroke-font TEXT and a body/courtyard OUTLINE per surface component, plus board-level marks. `OverExposedCopper` reports silk on a solderable pad by name. |
| `PcbPaste` / `PcbPasteSettings` / `PasteAperture` | The solder-paste (stencil) layer, derived from the copper model: an aperture (the pad grown by the `Expansion`, default slightly negative) over every **SMD** pad on each outer layer — a through-hole pad and a via get none (the SMD-only rule). |
| `PasteStencil` / `PasteStep` / `PasteLevelSelector` | A **step (multi-level) stencil** — a foil milled to different thicknesses in different zones, one paste Gerber per level. Each `PasteStep` has a foil thickness (which names its Gerber file), an aperture expansion, and a selector (a zone, a pad set / `Component`, or the opt-in `FinePitch` heuristic). Every SMD pad is on exactly one level (a partition); a pad no level claims falls to the `Default` level. |
| `PcbGerberExport` / `FabricationOutput` / `GerberExportResult` | Composes the whole fab set for a `PcbLayout` (or a raw `PcbCopperModel` for pours), sharing one coordinate format. |

### Solder mask, silkscreen and solder paste

The mask covers the whole board **except** a window over each solderable pad, each window the pad grown
by a stated **expansion** (`PcbMask.For`) — so a mask window is EXACT (a round pad's is a disc of radius
`r + expansion`, area `π(r+e)²` to ~1e-12; a rectangular pad's a rounded rectangle) and it round-trips
through the same twin decoder the copper does. By the standard positive-openings convention (as
KiCad/Eagle) the mask Gerber images the windows as dark; the fabricator clears mask where the Gerber is
dark. Vias are **tented** (no window) or **opened** by policy. The silkscreen is line-work (a Gerber has
no text primitive) — a refdes/value in a single-stroke vector `SilkFont`, and a courtyard outline, drawn
with a round aperture exactly as a trace draws, so it strokes back through the reader. `PcbSilkscreen.
OverExposedCopper(mask)` is the assembly check: silk on a solderable land is a real defect, reported by
name rather than thrown. The **solder paste** (the stencil) is the reflow-assembly layer: an aperture
over every **SMD** pad — and only SMD pads, because a through-hole pad is wave/hand-soldered and a via
would wick solder down the barrel (the SMD-only rule, the classic bug this layer must not have; a pad is
SMD when it carries no drill) — each aperture the pad grown by a `PcbPasteSettings.Expansion` whose
default is slightly NEGATIVE (the aperture is a hair smaller than the pad, to control the paste volume).
So a round pad's aperture is a disc of radius `r + e` (area `π(r+e)²`, `e` negative → smaller) and it
images the apertures as dark (the mask's positive-openings convention), round-tripping through the same
twin decoder. The mask/silk/paste **settings are layout truth** (`PcbLayout.MaskSettings` /
`SilkscreenSettings` / `PasteSettings`, write-only-when-stated), and the layers are **additive** — the
copper Gerbers, outline and drill are byte-identical whether or not they are requested.

**A step (multi-level) stencil** — a foil milled to different thicknesses in different zones — is a
`PasteStencil`, an ordered list of foil-thickness **levels** (`PasteStep`): a fine-pitch part wants a
thin foil / reduced aperture, a large thermal pad a thick foil / more paste, and each thickness is a
separate milling depth, so the fab consumes **one paste Gerber per level**. Each level has its own foil
thickness (which NAMES its Gerber file, e.g. `_100um`), its own aperture expansion, and a
`PasteLevelSelector` for the pads it covers — a **zone** (`InRectangle` / `InZone`, a pad whose centre lies
in it; ordered, first-match-wins), an explicit **pad set** (`Pads` / `Component` — every pad of a
footprint), or the opt-in **`FinePitch`** heuristic (a pad at or below a *required* size threshold; there
is no silent default — a default there would be a process decision made by a library). A pad no level
claims falls to the **`Default`** level (a step with no selector, which every stencil must declare), so
**every SMD aperture is on exactly one level** — a partition, no pad printed twice or dropped — and a
level's aperture is still the pad grown by *that level's* expansion (the foil thickness only names the
level, it never touches an aperture, so the aperture-equals-pad-plus-expansion oracle is unchanged, and
the SMD-only rule survives on every level). A step stencil is a **fabrication-process** parameter, so —
like a `DrcRuleSet` — it is passed to the export (`PcbGerberExport.Generate(layout, name, stencil)`), not
baked into the layout file, and a layout that declares none saves byte-identically. When no steps are
declared the output is EXACTLY the single stencil (a one-level step at the default expansion is
byte-identical to plain paste, asserted); the per-level file name appends the foil-thickness token
(`-Top_Paste_100um.gbr`), an empty level emits no file, and a non-positive thickness / a missing default /
two levels of one thickness (a file collision) are each refused **by name**.

**The oracle is the twin-decoder round trip** (the repo's rule — the geometry must survive the round
trip, not merely a structural validator pass): the copper written is parsed *back* and the recovered
copper equals the copper model's on each layer to the region-area grade (by area **and** by a
symmetric-difference check through the DRC's own `CurvedRegion2dBoolean`), verified on both a
hand-built and an autorouted board; the decoded drill hits equal the board's holes exactly. **The
imaging order reproduces a UNION exactly**: the copper is a union of feature regions, so a via drill
is a hole only where nothing covers it (a via under a trace, or a via-in-pad, is filled) — the writer
lays all the solid copper down, then clears exactly the holes of the final union. The coordinate
format is derived from the board's own magnitudes, so it is scale-invariant. An unrepresentable
boundary (a Bézier edge in a copper region) is refused **by name**, and the reader refuses a
truncated file / missing format spec / aperture macro by name (the `StepReader`/`IgesReader` ethos).
**Not in v1** (each filed): PERSISTING a step-stencil declaration in the layout file (a step stencil is
passed to the export, not saved — a full serializable grammar for its zones/selectors is a separate job),
a per-fabricator foil-thickness catalogue, paste-volume optimisation, window-paning of
large apertures, fine mask tenting control beyond
the tented/opened via policy, curved conformal mask / silk / paste on a MID surface (refused for the
distortion reason), a lowercase silk font (a value's lowercase advances as a blank), Gerber X2 attributes
and the job file, and a Gerber IMPORT of a foreign board (this is export). Docs:
`examples/ecad-fabrication.md`.

**The assembly pick-and-place (centroid) file** (`PcbPickAndPlace`) is the assembly twin of the copper
Gerber/Excellon set — the file a P&amp;P machine reads to *populate* the board: one row per placed
component (reference designator, X, Y, rotation, side, value/package). `PcbPickAndPlace.Compute` projects
the layout's placements into `PickAndPlaceRow`s, and **one `Compute` feeds both writers** (the
drawing-sheet rule): `ToCsv` (the ubiquitous `Designator,X,Y,Rotation,Side,Value`) and `ToPos` (a
KiCad-style aligned `Ref Val Package PosX PosY Rot Side`) cannot disagree about a pose. **The pose is the
placement, not the 3D body** — a machine places by the footprint origin, so a row is exactly the
`PcbPlacement` pose (independent of any 3D-model offset); board-frame X/Y are reported **verbatim** (the
coordinate-honesty rule) and rotations are degrees, CCW positive. The one real decision is the
**bottom-side rotation**, which is **mirrored** — the board is flipped about its X axis to populate the
bottom, negating the board-frame angle, so a bottom row's rotation is `(360 − rot) mod 360` (a sign swap,
never a `cos`; a quarter turn is exact) while a top row is verbatim. Rows are in placement (declaration)
order, so the output is deterministic (two emissions byte-identical). **The twin-decoder oracle**:
`ParseCsv` reads back what `ToCsv` wrote and recovers the designator, X, Y, rotation, side and value
exactly (RFC-4180 quoting survives a comma or quote in a value), refusing a wrong header / field count /
number / side by name. `Package` is the component's footprint name, or its definition type name when it
carries no footprint. Docs: `examples/ecad-fabrication.md`.

**The IPC-D-356A netlist** (`PcbIpc356`) is the board-house **electrical-test / net-compare** deliverable:
per NET, every conductive **access point** — every component pad and every net-carrying via — with its
net name, refdes + pin, board-frame midpoint (X, Y), layer/access code, drill (for drilled features) and
feature kind. A fab net-compares it against the copper Gerbers; a test house programs a flying-probe /
bed-of-nails tester from it. IPC-D-356**A** is the netname-carrying revision. `PcbIpc356.Write(layout)`
returns the text; `WriteFile` writes `<name>.ipc`. The net of every pad is resolved through
`PcbCopperModel.FromLayout` (the same tagging the DRC/connectivity read — the one-declaration identity).
Conventions, each **stated**: units are metric **micrometres** (`P UNITS CUST 2`, coordinates
`X<sign><µm>`/`Y…`), so the round trip is exact and a wrong scale is a 1000× coordinate-magnitude tell;
coordinates are board-frame **verbatim** (no Y-flip — a bottom access point keeps the same (x, y) as a
top one; which side it is probed from is the ACCESS code); access is `A00` = all layers (a through-hole
pad / through via), else the 1-based number of the top-most reached copper layer (top = 1, bottom = N),
reducing to `top = 1 / bottom = 2 / all = 0` on a 2-layer board; op `327` = SMD pad (no hole), `317` =
drilled (through-hole pad, or a via with a blank component reference). Included: every component pad and
every net-carrying via — an unconnected / no-connect pad is its **own single-point net** (a unique
`N/C-######` name, matching how the copper model treats a null-net feature); board mounting / legacy
holes are excluded (no net). **The bar is the twin-decoder round trip plus a net reconstruction**:
`PcbIpc356.Parse` reads the output back, and the partition of component pads it induces (which pads share
a net) EQUALS the board's own — the copper model's partition — with a dropped or relabelled record making
them differ (the mutation that proves the oracle bites). **Refused by name** (an identity is never
sanitized — it is the reconstruction key): a net over 14 chars / refdes over 6 / pin over 4, whitespace
in an identity, a real net colliding with the `N/C-######` namespace, a drill below the file's 1 µm
resolution; the reader refuses an unknown record / units / a drilled record with no drill / an SMD record
with one by name. **Not in v1** (each filed): wider net-name / refdes fields, per-inner-layer access
encoding for buried vias, and conductor (trace-midpoint, op `378`) records. Docs:
`examples/ecad-fabrication.md`.

## The fabrication drawing — the shared frame's third consumer

The Gerbers and the Excellon program are what a board house *machines* from; the **fabrication
drawing** (`PcbFabricationSheet` → `PcbFabricationDrawing`, `PcbFabricationDrawing.cs`) is what a
board house *reads* beside them — the board OUTLINE at a fitted scale, a **drill map** with a symbol
at every drilled feature, a **drill table** (a keyed LEGEND) grouping the board's holes, vias and
through-hole pad drills by size, a **layer stackup** table, and a **fabrication notes** block. It is
the **third consumer of the shared
`DrawingFrame`** (after the mechanical `DrawingSheet` and the ECAD `SchematicSheet`): a fab drawing
is an engineering drawing, so it uses the same three-band `EngineeringTitleBlock` on the same
`SheetLayers` — which is why `sheet.Frame().Compute()` given the same paper and title-block fields is
**byte-identical** to a mechanical `DrawingSheet`'s frame (asserted, and the whole payoff of one
shared frame). `Compute()` feeds the **SVG / DXF / PDF** writers from one set of primitives (the
drawing-sheet one-`Compute` rule), so the three cannot disagree.

**It reads the board; it never edits it.** Everything derives from the layout's own public read
surface (`board.OutlinePoints`, `board.Holes`, `layout.PlacedVias()`, `board.LayerStackup`), so the
drawing cannot disagree with the board it documents (the one-declaration rule, applied to a drawing).

**The drill table is a closed-form partition, not a picture.** Its rows group the board's holes,
vias AND through-hole COMPONENT PAD drills by an exact `(diameter, plated)` key (a mounting hole is
NPTH; a board via, every placed via and every through-hole pad are PTH; the diameter is the board's
own value carried verbatim, so exact equality IS the right partition). A through-hole pad HAS a drill
and a surface-mount land does NOT — the same SMD-vs-THT distinction the solder-paste layer reads off
the copper model — so `Σ row.Count` equals the count of holes + placed vias + through-hole pads, each
row's count equals the features of that size and plating, and **adding a hole OR a through-hole pad
adds exactly one to its row** (SMD pads add none) — the oracle, with the mutation that proves it.
Sizes sort ascending (then NPTH before PTH), so the symbol assignment is a deterministic function of
the board. **The table is a keyed LEGEND**: each size takes a distinct `Index`/`Symbol` — a LETTER
(`A`, `B`, …), the always-distinct key drawn in the `SYM` column — beside a `DrillGlyph` from the
CANONICAL, ordered `PcbFabricationSheet.DrillGlyphPalette` (the map marker); the glyph cycles the
palette past its length with the letter as the distinguishing suffix, and a board with more distinct
drill sizes than the `A`–`Z` alphabet holds (`MaxLegendSizes` = 26) is refused by name. The **drill
map** places one `DrillMark` per
feature at its own location — `mark.SheetLocation == drawing.Project(mark.BoardLocation)`, the same
board→sheet transform the outline is drawn by, so a test asserts the map cannot omit a hole nor
invent one. The **stackup table** lists the physical `LayerStackup.Layers` (copper + dielectric); a
copper-only board carries no physical stackup, so its table is empty and a note states the copper
count instead. The **notes** are write-only-when-stated — finished thickness, copper-layer count,
copper foil thickness (only when a stackup gives one), the drill summary, and any mask/silk/paste the
layout declares; a value nothing carries is **omitted, not invented**.

**The fab-package fields the geometry cannot carry come from a `PcbFabricationSpec`** — the board's
FABRICATION REQUIREMENTS: base material, finished thickness, copper weight, surface finish
(`PcbSurfaceFinish` + an `Other` name), solder-mask and silkscreen colours, IPC-6012 class, minimum
trace width and clearance, and free-form notes. **Every field is optional** (`null` / an empty notes
list = "not stated"), so `PcbFabricationSpec.Default` is valid and states nothing. It rides in the
layout as **LAYOUT TRUTH** the same way the mask/silk/paste settings do (`layout.WithFabrication(...)`
→ `layout.Fabrication`), so the fabrication drawing reads it **write-only-when-stated** — a stated
field prints its note (e.g. `MATERIAL: FR-4.`, `SURFACE FINISH: ENIG.`, `COPPER WEIGHT: 1 oz (35
um).`, `FABRICATE TO IPC-6012 CLASS 2.`), an unstated one is absent — and it **persists**
write-only-when-stated (a layout that states none saves byte-identically to a pre-spec one, a stated
spec is a `save → load → save` fixed point). A **stated finished thickness OVERRIDES** the modelled
plate thickness in the finished-thickness note (the delivered stackup thickness is what a fabricator
quotes to); with no spec the note is the modelled thickness exactly as before, so a no-spec drawing
is byte-identical. Every stated value is validated at `WithFabrication` and a bad one is **refused by
name** — a non-finite/non-positive thickness / copper weight / minimum trace / minimum clearance, an
IPC class outside {1, 2, 3}, or an `Other` finish with no name. It is also **populated automatically
from a `KiCadPcbReader` import** (the board-setup / stackup carries most of these fields) — see the
whole-board interchange section above.

**v1 scope** (filed): no per-layer copper/mask/silk plots yet (a picture per Gerber, wanting the
copper-model geometry rendered as sheet line work). Docs: `examples/ecad-fab-drawing.md`.

## Enclosure fit — the MCAD/ECAD boundary

Does a placed board fit the box? `Enclosure` is a housing built from the ordinary `Shape` API (a
shelled box with the panel cutouts drilled — no new solid type): a rectangular interior **cavity**,
a **wall**/**floor** thickness, a board **seating height** (the standoffs), a **lid** at a stated
height (the headroom ceiling), named **panel cutouts** and interior **keep-out** volumes.
`EnclosureFit.Check(enclosure, layout)` (or `enclosure.Fit(layout)`) returns a `FitReport` that
**names, locates and measures** every problem.

**It reuses the landed clash machinery** — an instance-bounds broad phase then the transversal
`MeshIntersection.Crosses` narrow phase — so a part resting flush on the lid or seated on its
standoffs is NOT a clash (they touch; they do not interpenetrate). Where the geometry is a plane or
a rectangle the number is closed-form: the board outline against the cavity walls, a part's top
against the lid.

| `FitIssue` | What it catches |
| --- | --- |
| `BoardTooLarge` | The board outline reaches past a side wall — the wall (`+X`, `−Y`, …) and overhang named. |
| `ComponentClashesWall` | A component body interpenetrates a wall/floor. |
| `ComponentClashesLid` | A part reaches past the lid underside — named with its exact clearance deficit. |
| `ConnectorNoCutout` | A component declared panel-mount (`AddPanelConnector`) has no cutout serving it. |
| `ConnectorMisaligned` | A panel connector's cross-section does not fit through / is not centred in its cutout — the offset reported. |
| `ConnectorNotProtruding` | A panel connector's body does not reach the wall its cutout is in — the reach deficit reported. |
| `KeepOutCollision` | A component body intersects an interior keep-out volume (surface crossing OR full containment). |

`Enclosure.SeatFrame()` is where the board mounts — pass it as the layout's `boardFrame` so the
board seats in the cavity and the fit reads one geometry, not two hand-kept poses. A panel connector
is declared with `AddPanelConnector(reference)` and served by a `PanelCutout` whose `For` names it;
panel connectors are excluded from the wall-clash test (passing through a wall is what they are
for). `Enclosure.SmallestFor(layout, clearance, standoff, headroom, wall)` sizes and places the
smallest box the layout fits in place (a starting point, not an enclosure generator). Round panel
cutouts are checked against their bounding box in v1 (exact round-hole corner fit is filed).
`FitReport` is deterministic (same enclosure + board → the same report) and always reports
`Headroom` (positive clears, negative collides). Docs: `examples/ecad-enclosure.md`.

## Thermal coupling (`PcbThermal`)

Where does the heat go? `PcbThermal.Solve(layout, spec)` turns a powered board into a heat-conduction
problem on the **landed [FEA thermal solver](../EngrCAD.Fea/README.md)** — not a lumped estimate —
so the answers are verifiable against closed forms. Each component's dissipation (watts) becomes a
volumetric source over its footprint; the copper spreads it; a held cold edge or a convecting face
carries it away; the result is a temperature field the `FieldDisplay` colour map picks up and a
hot-spot temperature per component.

v1 is the standard **board-level model**: the copper is SMEARED into an effective conductivity over
a homogeneous slab — high in-plane (the copper layers are parallel paths, `k_in = f·k_Cu + (1−f)·k_FR4`),
low through-thickness (they are in series, the harmonic mean) — with `f` the copper volume fraction
(the one honest knob, `PcbThermalSpec.CopperFraction`, or `.FromCoverage(board, coverage)`). A bare
board collapses to the isotropic dielectric conductivity. Power is stated in watts and a film
coefficient in W/(m²·K), converted once to the model unit; boundary conditions (`FixedTemperature`,
`Convection`) name a `BoardSurface` or a raw `Facets` selector.

**Verified against analytic conduction** (an ECAD thermal answer fails plausibly): a uniformly-
dissipating board matches the parabola `T = T0 + (q/2k)(L²−x²)` to 3e-12 relative (quadratic-exact);
a single hot component's far-field matches the series-resistance line `T0 + Q(L−x)/(kA)` to 3.6e-5
with the energy balance exact; real FR4 vs 2.6 % copper drops the peak rise 1129 K → 32.6 K (34.7×)
with the far-field ratio exactly `k_copper/k_bare`; a no-boundary board is refused by name (the
`ThermalSolver` convention); zero power is isothermal; a solve is deterministic to the bit. Filed:
transient warm-up (`SolveTransient` exists), thermal vias as discrete paths, CFD airflow, and
detailed die/package models. Docs: `examples/ecad-thermal.md`.

## MID / LDS — routing on a moulded surface

The flagship: routing conductors and seating components on a **moulded, doubly-curved surface**
(a plastic housing carrying its own circuit on its shaped wall) — the MID / LDS construction. **It
works on ANY surface — a torus, a bumpy blob, a whole closed shell — not one exp-map chart.** A
`MidSurface` wraps an arbitrary triangle mesh and answers the routing's three questions
*intrinsically*: where the nearest surface point is (a pad states its world position and snaps to the
shell), what tangent frame sits there (a component poses on the surface), and what the surface does
*locally* — a small exp-map `LocalExpChart` around a point, the geodesic-distance approximator the DRC
measures a clearance in. **No feature depends on a chart covering the whole part**; every chart is
local and per query, so a closed surface a single global exp map would wrap onto itself is routed with
no chart at all.

| Type | What it is |
| --- | --- |
| `MidSurface` / `SurfacePoint` / `LocalExpChart` | The intrinsic surface model: `Locate(worldPoint)` snaps to the surface, `Frame` gives the tangent frame, `Chart` builds a local exp map (forward `TryProject` and inverse `TryLift`, with the local `ScaleBand`). |
| `MidBoard` | A moulded routing surface. `OnMesh(mesh)` is **INTRINSIC** (no chart, any geometry); `OnSurface(mesh, seed, ref, radius)` is the **global-chart** mode for a developable patch (exact numbers, the bit-for-bit oracle). Holds pads, routed `SurfaceTrace`s, seated components. `MaxDistortion` reports per-region (intrinsic) or per-chart (global); `PlacePad`/`PlacePin` at a world position or a `(u, v)`; `Seat` poses a `HardwareComponent` OR a raw `Shape` body on the surface tangent frame. |
| `MidPad` | A copper land — a surface point, a net, a land width (and a `(u, v)` on a global-chart board). |
| `SurfaceTrace` / `SurfaceRun` | A net's conductor — a centre-line lifted onto the surface. **Reports the distortion it carried** (`MinScale`/`MaxScale`/`Distortion`); `Conductor(thickness)` is a thin conductive `Shape` (a ribbon along the surface) that round-trips through STL/STEP. |
| `MidRouting` | **Places** traces (`Connect`, a **geodesic on the mesh** — `DijkstraGraphDistance` edge path then a straightest-geodesic curve-shortening smoothing) AND **auto-routes** a whole intrinsic board (`Route(MidBoard)`) OR a two-shell stack across shells (`Route(MidStack)`). `Verify` runs the 3D DRC. |
| `SurfaceRouter` / `SurfaceRouteOptions` / `SurfaceRouteResult` | The single-shell surface AUTO-ROUTER — the geodesic analogue of the flat `PcbRouter`: a DRC-aware maze search over the mesh vertex graph (A\*, admissible 3D-straight-line heuristic), MST decomposition of each net from the ratsnest, straightening, and rip-up-and-reroute. Every candidate is committed only after the exact 3D DRC (`Mid3dDrc.RouteCandidateClears`) certifies it clean; a net boxed in is reported UNROUTABLE by name; the partial result is always clean. Runs on an INTRINSIC board (`OnMesh`); a global-chart board is refused with a pointer to `OnMesh`. |
| `CrossShellRouter` / `StackRouteResult` | The CROSS-SHELL auto-router — the surface analogue of the flat router's layer-changing via. Searches the union of both shells' vertex graphs plus VIA EDGES tying corresponding vertices `(k, v) ↔ (k±1, v)` at a via penalty, so one A\* both routes a net across shells and chooses where to change shell; a via edge becomes a placed through-shell `SurfaceVia` and the route splits into per-shell traces. The exact multi-shell DRC certifies every commit (per-shell trace clearance + per-shell via-pad clearance + inter-shell via-to-via web), so a same-shell net gets **no via**, a cross-shell 2-pin **one**, an obstacle hop **two**; rip-up carries over. A one-shell / > 2 shell stack is refused by name. |
| `Mid3dDrc` / `Mid3dDrcReport` / `MidDrcViolation` | The 3D DRC. On an intrinsic board the clearance is a **geodesic surface distance** (a certified 3D-chord broad phase, then a per-pair local chart); on a global-chart board it runs in the one exp map's `(u, v)`. Three-valued — Clear / Violation / **Uncertain** (a conservative refusal where the distortion cannot certify the verdict). |
| `MidStack` / `SurfaceVia` | **MULTI-SHELL** MID — an outer `MidBoard` plus inner shells, each the outer mesh offset inward by a dielectric thickness along its ANGLE-WEIGHTED vertex normal (same topology, so a via ties an outer point to its corresponding inner point). `Shell(k)` / `Outer` / `Inner` are the per-shell boards; `AddVia` places a through-shell `SurfaceVia` (a copper pad per shell + a plated barrel across them); `Connectivity` spans shells (a via ties a net's copper across shells); `Check` runs each shell's same-shell DRC + inter-shell via-to-via spacing. A single-shell stack is a plain `MidBoard`, DRC bit-identical. |

**The certified geodesic DRC**: a 3D chord is never longer than a surface geodesic, so a chord
edge-to-edge distance at or above the clearance *proves* the surface clearance (CLEAR, whatever the
curvature); a closer pair is measured in a local exp-map chart with the distortion folded in. A near
pair is a VIOLATION; a near-limit pair on a high-curvature patch (a small sphere with a clearance a
large fraction of its radius) is **refused** (Uncertain) while the same pair on a plane is certified.
**The decisive precision oracle stays the developable one**: on a cylinder (a single exp map is an
isometry) the 3D DRC verdicts and measured separations equal the **unrolled flat 2D DRC**'s — bit for
bit — and the intrinsic route reaches the same answer to the discretisation grade. A **sphere geodesic**
matches its great-circle closed form `R·θ`; a geodesic trace's endpoints land **exactly** on their
pads; the conductor is a **closed solid**; the check is **deterministic**. The showcase is a moulded
wearable dome (an MCU, two LEDs, a connector, passives seated on the shaped surface, wired by geodesic
conductors the board **auto-routes**) that **self-verifies**.

**The surface auto-router** clears the flat router's bar, lifted onto the surface — the exact 3D DRC
is the source of truth, the vertex graph only accelerates: a 2-pin net on a cylinder and on a sphere
cap routes clean and connected; several nets **route around** each other; a congested board a greedy
pass leaves unrouted is **completed by rip-up** (both clean); a walled-in pin is **unroutable by name**
with the rest routed and clean; a dense knot's **partial result is always DRC-clean** with the failures
named; on a developable cylinder the routed **connectivity matches the unrolled flat board's**; and two
runs are **deterministic** vertex for vertex.

**Multi-shell** (`MidStack`) has landed: an inner moulded shell is the outer mesh offset inward by a
dielectric thickness along its angle-weighted vertex normal (same topology, so a through-shell via ties
an outer point to its corresponding inner point). The DECISIVE oracle is the developable one — a
cylinder's inner shell is a concentric cylinder `r − t` to round-off (rim included) — plus the via
mutation (a via ties a net across shells; remove it and the net splits into a cross-shell ratsnest), the
per-shell + inter-shell DRC (a same-shell clearance found on the inner shell, a via too close to
other-net copper on either shell, a via-to-via web, and a single-shell stack bit-identical to
`Mid3dDrc.Check`), and the self-intersection refusal (a sphere offset past its radius flips its
signed-volume sign).

**Cross-shell auto-routing** (`MidRouting.Route(stack, …)`) has landed — the surface analogue of the flat
router's layer-changing via. One A\* over the union of both shells' vertex graphs plus VIA EDGES tying
corresponding vertices chooses which shell each segment rides and drops a through-shell via at the
transition; the exact multi-shell DRC certifies every commit, so a graph-resolution error can never ship a
clearance-violating trace or via. A cross-shell 2-pin net routes with **one via**, a same-shell net with a
clear path with **none** (the via penalty keeps it on one shell), an **obstacle hop** with **two** — and
the mutation proves it: the same blocked fixture on a single shell is unroutable. A pin boxed in on
**both** shells is unroutable by name; a one-shell / > 2 shell stack is refused by name; the build is
deterministic. **Filed by name**: **topological / shove** routing on the surface (v1 detours but does not
push obstacles), **optimal via minimisation** (v1 uses a fixed via penalty) and **partial-span vias for a
> 2 shell stack** (v1 routes a two-shell stack with full-stack vias), **length matching**, and a conformal
solder mask / pour on the surface (the distortion reason copper pours already refuse curved walls). v1's
single surface has no drills / edges of its own. Docs: `examples/ecad-mid.md`.

## Not yet (later campaign stages)

The richer
interchange grows: **KiCad `.kicad_pcb` whole-board IMPORT has landed** (see Stage 2 above); what
stays filed is EXPORT of our board to `.kicad_pcb` (a different, larger job), the KiCad `.kicad_sch`
schematic, and STEP AP214 board assemblies. On the LIBRARY side, **KiCad `.kicad_sym`/`.kicad_mod`
and Eagle `.lbr` both import**; what stays filed is **IPC-7351 footprint GENERATION** from a
designation (a generator, not a file import), EDIF, whole Eagle `.brd`/`.sch` board/schematic import
(a different, larger job), Eagle 3D package models, the newer Eagle/Fusion XML variants beyond the
classic `.lbr`, and Eagle 3D package models (Eagle's `<packages3d>` reference a model by URN — materially more work than the classic `.lbr` carries). The **KiCad 3D model reference now imports** (the footprint's `(model …)` becomes the definition's `Model`); what stays filed on the model side is IGES (`.igs`/`.iges`) 3D-model loading (a face soup needing `ShapeHealing`) and a VRML (`.wrl`) reader — both refused by name, the reference recorded. Vias do not yet cut the 3D plate B-Rep (they are modelled in the copper / connectivity / DRC;
drilling the plate is a later refinement). The drawn schematic **sheet** (`SchematicSheet` →
SVG/DXF/PDF) has landed as a VIEW of the graph (see above); what stays open there is a real
**auto-placer** (a good layout, not the grid placeholder) and an **obstacle-avoiding** wire
router (v1 wires may cross symbols), plus hierarchical sheets, buses, off-page connectors and
back-annotation.
