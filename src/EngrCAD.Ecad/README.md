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
| `PartDefinition` | A reusable part type: name/designation, an ordered `Pin` list, an optional `Footprint`, an optional 2D `Symbol`, and an optional 3D `Body` hook (`Func<Shape>`). The definition is the source; a component is meaningless without one. |
| `Symbol` / `SymbolPin` / `SymbolGraphic` | The 2D SCHEMATIC symbol — graphic primitives (`SymbolPolyline`/`SymbolRectangle`/`SymbolCircle`/`SymbolArc`/`SymbolText`) plus a `SymbolPin` per terminal carrying the pin NUMBER, name, the `Anchor` where a wire lands, a `SymbolPinDirection`, a length and a `PinType`. The representation a drawn schematic **sheet** consumes. |
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
| `CavityClearance` | Copper of another component closer than the copper-to-edge minimum to an embedded component's cavity wall (a milled edge), on the cavity's seat layer. See Stage 4b. |
| `ViaToVia` | Two vias' drilled holes closer than the minimum via-to-via web (a manufacturing spacing between drills, applied to all via pairs regardless of net). See Stage 5 prerequisite. |

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

- **Symbol** (`.kicad_sym`): the `Reference`/`Footprint` properties, nested unit sub-symbols
  recursed for graphics and pins, `rectangle`/`circle`/`arc`/`polyline`/`text` graphics, and
  `pin`s (electrical type → `PinType`, name, number, position, angle → `SymbolPinDirection`,
  length). A `SymbolPin.Anchor` is the connection point where a wire lands, and the direction
  points from there into the body (KiCad's pin angle convention). A bezier graphic, an alternate
  pin function, or an electrical type with no exact `PinType` is ignored **with a diagnostic**.
- **Footprint** (`.kicad_mod`): SMD and plated through-hole pads of the standard shapes
  (`circle`/`rect`/`roundrect`/`oval`) with their `at`/`size`/`drill` — mapped onto the existing
  `Footprint`/`Pad` with **no change to `Pad` or `PadShape`** (the drill a through pad needs was
  already there from stage 2, so the board side that reads footprints is untouched). Pad centres
  and sizes are STATED in the file, so they are carried exactly; a pad rotation, a
  `trapezoid`/`custom` shape or an oval drill is approximated **with a note**.

A loaded `Symbol` is DATA now, so a `PartDefinition` with a symbol round-trips through the
schematic file as a **byte-identical fixed point**; a symbol-less definition serializes exactly
as before (write-only-when-stated). The 3D body stays code, as always; a KiCad `.wrl`/`.step`
model reference is out of scope (its path noted, not loaded).

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
| `SchematicDrawing` | The computed sheet: `Segments`/`Junctions`/`Texts`/`Pins`, the `Connectivity`, `Verify()`, and the writers `ToSvg`/`ToDxf`/`ToPdf` (+ `Save*`). |
| `DrawnConnectivity` / `DrawnConnectivityReport` | The connectivity the drawing EXPRESSES, reconstructed from its primitives (wire segments, pin anchors, net labels) — `AreJoined(a, b)`, `LabelOf(pin)`. `Verify()` asserts the drawn sheet joins exactly the pins the netlist connects, BOTH ways. |

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
Docs: `examples/ecad-schematic-sheet.md`.

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

## Fabrication export — Gerber (RS-274X) + Excellon

The fab output that makes a routed board manufacturable. `PcbGerberExport.Write(layout, dir)` writes
one **Gerber** per copper layer, a board-outline Gerber, and an **Excellon** NC-drill program (and
reports what it wrote); `PcbGerberExport.Generate(layout)` returns the same as text.

| Type | What it is |
| --- | --- |
| `GerberWriter` / `GerberBuilder` | RS-274X (extended Gerber): the format spec, an aperture table (circle/rectangle/obround/regular-polygon `%ADD`s), pads as flashes (`D03`), traces as round-aperture draws (`D01`/`D02`), region fills (`G36`/`G37`) and dark/clear polarity. |
| `GerberReader` / `GerberImage` | The TWIN DECODER — parses exactly what the writer emits and reconstructs the copper as `CurvedRegion2d`s per layer. The round-trip oracle. |
| `ExcellonWriter` / `ExcellonReader` / `DrillHit` | The NC-drill program (a tool per distinct diameter + the hits) and its twin decoder. Metric, decimal coordinates. |
| `PcbGerberExport` / `FabricationOutput` / `GerberExportResult` | Composes the whole fab set for a `PcbLayout` (or a raw `PcbCopperModel` for pours), sharing one coordinate format. |

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
**Not in v1** (each filed): solder-mask / silkscreen / paste layers (no mask/silk model yet), Gerber
X2 attributes and the job file, and a Gerber IMPORT of a foreign board (this is export). Docs:
`examples/ecad-fabrication.md`.

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

**Thermal coupling** — per-component power dissipated into the thermal solver, verified against a
uniformly-dissipating board's analytic temperature rise — is the next stage over this one geometry.

## Not yet (later campaign stages)

Thermal coupling, MID/LDS 3D
routing, and the richer
interchange (KiCad `.kicad_pcb`, STEP AP214 board assemblies) — each a later stage over this one
graph. On the LIBRARY side, **Eagle `.lbr`** (an XML second reader), **IPC-7351 footprint
GENERATION** from a designation (a generator, not a file import), EDIF, and the KiCad 3D model
reference (`.wrl`/`.step`) are filed beside the KiCad symbol/footprint import that just landed. Vias do not yet cut the 3D plate B-Rep (they are modelled in the copper / connectivity / DRC;
drilling the plate is a later refinement). The drawn schematic **sheet** (`SchematicSheet` →
SVG/DXF/PDF) has landed as a VIEW of the graph (see above); what stays open there is a real
**auto-placer** (a good layout, not the grid placeholder) and an **obstacle-avoiding** wire
router (v1 wires may cross symbols), plus hierarchical sheets, buses, off-page connectors and
back-annotation.
