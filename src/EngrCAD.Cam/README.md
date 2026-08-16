# EngrCAD.Cam

The CAM layer — manufacturing toolpaths from the same `Shape` graph everything else reads.
Kernel-tier (Core + Modeling; deliberately no viewer dependency). Stage 1 of the CNC/CAM
campaign is landed: **FDM slicing and G-code**. The campaign's later stages — 2.5D CNC milling,
3-axis surfacing, HSM adaptive clearing, toolpath/material-removal animation, non-planar
slicing — are filed in `todo.md` under the CNC/CAM campaign entry.

## FDM slicing (`FdmSlicer`, `PrinterProfile`)

The slicer is deliberately a THIN layer over machinery that already shipped:

- **Layers** are exact sections at each layer's mid-height (the standard slicer convention, so a
  plane never lands flush on the part's own top/bottom face; a flush INTERNAL face is retried
  once at a deterministic +5%-of-a-layer nudge). The shape is lowered ONCE and sectioned N
  times — the `Part.TryGetSolid` lesson — through the same `PlanarSection` routes
  `Shape.Section` takes, so any representation slices (B-Rep exactly, mesh/SDF through the
  display mesh).
- **Walls** are successive inward `Region2dOffset`s: wall *k*'s centreline at `bead·(k + ½)`,
  holes getting their own loops, emitted innermost-first (the outer wall lands on settled
  neighbours — a print-quality ORDER the travel linker is deliberately not allowed to change).
- **Infill** is a rectilinear scan alternating ±45° per layer, spacing `bead / density`,
  clipped to the region inside the innermost wall by an EXACT even-odd crossing count
  (half-open at vertices — the `SheetHatch` rule) and anchored to the GLOBAL grid, so the
  pattern's phase is a function of what was asked, never of where the part sits. Runs are
  linked by the shared deterministic `RunLinker`.
- **`PrinterProfile`** carries the process numbers (nozzle, filament, layer height, walls,
  density, speeds, retraction, temperatures with 0 = "not written") and refuses an unusable
  profile BY NAME — a layer taller than the bead degenerates the stadium cross-section.
- **The print DIRECTION is a parameter, not a re-model**: `Slice(shape, profile,
  printDirection)` rotates the part by the minimal rotation taking the chosen axis to bed +Z
  and slices in bed coordinates (+Z is the byte-identical fast path; antiparallel turns π about
  the one `ArbitraryPerpendicular` convention; zero refuses by name;
  `SlicedPart.PrintDirection` records the choice).
- **The print ANIMATES with no re-meshing**: the viewer's `SectionTracks.Reveal` (the animation
  system's section track — a clip plane is shader state) sweeps a plane up the build direction
  quantized to the slice's own layer count, so the reveal completes whole layers the way a
  printer does. See `docs/examples/cam-slicing.md`'s committed APNG.
- **First-layer adhesion is write-only-when-stated**: `BrimWidth` lays brim rings outward from
  the part's outline (a bore gets interior rings from the outward offset's own hole loops),
  `SkirtLoops`/`SkirtGap` add a priming skirt standing clear — printed skirt-first, brim
  outermost-in so the nozzle finishes at the part; stating neither slices byte-identically.
- **Solid top/bottom shells** (`TopSolidLayers`/`BottomSolidLayers`, 0 = off
  byte-identically): a spot of the infill core is SOLID skin exactly where the neighbouring
  N layers above / M below do not cover it — the exact `Region2dBoolean` intersection of the
  neighbour window's sections, subtracted from the core; skins fill at the bead spacing, a
  window past the stack meets air (so the part's own top/bottom layers are wholly solid with
  no special case), zero sparse density still lays the skins. The step fixture pins the
  solid/sparse split landing exactly at an overhanging wall.
- **Supports follow the same convention** (`SupportOverhangAngle`, 0 = off): overhang facets of
  the ORIENTED part's own mesh — the `Manufacturability` rule, the threshold compared on the
  dot product and never on a derived angle — are projected to the bed and unioned; each layer
  holds columns under whatever overhang is still ABOVE it (a facet partly below the plane
  contributes its clipped upper part, so a slanted overhang's supports track its own height),
  minus the part's section grown by `SupportGap` (a column never fuses to a wall), patterned as
  sparse one-direction breakaway lines at `SupportSpacing`. A facet resting on the bed excludes
  itself with no special case — nothing of it is above any layer, so no layer supports it.

- **Per-feature speeds + the print-time bracket**: each role carries its own optional speed
  (`FirstLayerSpeed` winning on layer 0 whatever the role), resolved by the profile's ONE
  `SpeedFor` rule the writer reads — stating nothing is byte-identical, and stating speeds
  changes ONLY the F words (asserted structurally). `PrintTime.Estimate` reads the DECODED
  program and answers an honest [min, max] bracket: every move at its own feed against the
  closed-form from-rest trapezoid (`d/v + v/a` full-speed, `2·√(d/a)` triangular), the
  infinite-acceleration limit collapsing the bracket; junction-deviation cornering is the
  filed refinement that narrows it without moving its ends.

- **The FDM finish wave** completed the practical slicer feature set: `InfillPattern`
  (rectilinear/grid/triangles/concentric/GYROID — the TPMS level set sectioned at each layer's
  own z — /Hilbert over `SpaceFillingInfill`, every member holding the stated density by
  scaling spacing to direction count); `SpiralVase` (one continuous helical wall, z ramped
  along its own arc length, contradictions refused by name); `SeamPosition` (Rear/Aligned) and
  `ExternalPerimetersFirst`; cooling (`MinLayerTime` slowdown floored at `MinPrintSpeed`,
  `FanSpeed`/`FanOffLayers`), the `MaxVolumetricFlow` hard cap (applied LAST — the melt limit
  outranks every stated speed) and `ZHop`; the support stack completed (`SupportZGap` air under
  the overhang, `SupportInterfaceLayers` densified + perpendicular near the contact,
  `RaftLayers`/`RaftMargin` lifting the part with adhesion moved to the raft, and
  `FdmSupportModifiers` — blocker/enforcer shapes, the code-first paint-on support);
  `DetectBridges` (skin over air filled along its long axis at `BridgeSpeed`),
  `MonotonicSkins`, `IroningFlow` (per-path `Flow`, the identity generalising to
  sum of length x flow); and the dimensional compensations (elephant foot / XY / hole). Filed
  WITH REASONS: Arachne, gap fill, fuzzy skin, lightning infill, tree supports, variable
  layer height, multi-material, and `RetractionExtraRestart` (unmatched extra filament breaks
  the matched retract-pair contract the twin decoder verifies).

- **The integration wave**: custom `StartGcode`/`LayerChangeGcode`/`EndGcode` snippets with
  `{layer}`/`{z}` substitution (the decoder still reads the file, so a smuggled `G91`/`G20`
  refuses by name there); `FuzzySkinThickness` (deterministic hash jitter on the outermost
  wall — the pattern-phase rule applied to noise; layer 0 and inner shells bit-identical);
  `SlicedPart.FilamentByRole` (per-role split summing to `FilamentUsed` exactly, flow
  included); and `FdmPlating.Plate` — multi-part plates over the landed `Packing` machinery,
  one shape the slicer takes whole, disjoint islands getting walls/brims/skins/supports with
  nothing new.

- **The stage-2 completion pack**: `MillDirection` climb/conventional on pockets and
  profiles (DERIVED — material on the left of travel is climb for an M3 right-hand cutter —
  and applied by measured shoelace sign, an island pocket orienting outer and island rings
  oppositely); opt-in canned `G81`/`G83` drilling in `CncGcodeWriter` (Z/R/Q reconstructed
  from the pass's own moves, irregular ladders falling back to expanded emission; the decoder
  expands cycles under Fanuc semantics with modal bare-X/Y re-execution, refusing a missing
  Z/R/Q by name); and `CncToolLibrary.Suggest` over the ⚠ `MillMaterials` chip-load catalogue
  (`rpm = 1000·Vc/(π·D)`, `feed = rpm·flutes·chipload`, the spindle cap preserving the chip
  load rather than the feed).

- **Flat & bull-nose cutters** (`MillCutter` + the internal mesh drop-cutter): raster takes
  a cutter kind; flat/bull ride the tessellation with per-mode contact arithmetic (vertex
  exact, edge a bracketed 1D scan since a torus-line tangency is a quartic, face closed form)
  because the filed SDF route does not survive the disc — certifying a min over it through a
  1-Lipschitz oracle is quadratic in the flatness, and flat is the common case; a ball-nose
  keeps the exact implicit route byte-for-byte. Flat-spot and APT closed-form oracles; the
  ball's mesh-vs-field two-construction cross-check; waterline refuses non-ball by name.

- **Model-fed drilling + raster angle**: `CncDrilling.FromShape/FromPart` derives the drill
  program from the model's own `Drill`/`ThreadedHole` declarations (the `HoleTable` rows
  gained the numeric drilling data) — one op per distinct diameter, a counterbore drilling
  its THROUGH bore, a threaded hole its tap pilot, depth-to-shoulder verbatim, tilted planes
  refused by row letter; and `Raster` takes `rasterAngleDegrees` (grid anchored in the
  rotated frame, quarter turns exact sign swaps — a 90° raster is the transposed grid bit
  for bit).

- **Rest machining** (`CncMill.PocketRest`): the corner residues of the rough opening
  pocketed by a smaller tool over `intersect(grow(residue, 2·r₂), region)` — the 2·r₂ a
  DERIVED sufficiency (any reachable residue point has a legal tool disc whose centre lands
  in the ring ladder's own inset), the tool centre free to stand in cleared space while the
  wall stays inviolate point-by-point; the opening ε-grown before the difference (tangential
  cusp contact is the arrangement's hostile case); residues thinner than a stated minimum
  (default r₂/4) skipped as flattening noise. Oracle: combined rough+rest footprint = the
  finish tool's opening, the (4−π)(R₁²→r₂²) closed-form ladder.

- **Helical ramp entry** (`Pocket(rampAngleDegrees:)`): each level entered on a helix from
  the previous cleared level (radius under the tool radius so no core post, inside the
  MEASURED room, pitch 2π·r·tan(angle), one flat closing turn), the level's rings run as one
  pass linked AT DEPTH where the exact segment-to-boundary distance allows, plunge fallback
  where too tight; plunges end only at level TOPS (asserted through the decoder), ramp 0
  byte-identical. Fixed en route: ring loops now link within each ring level, innermost
  first (the one-global-link order was pen-dependent and measurably started a level at its
  boundary ring).

- **Sequential printing** (`FdmSequential.Plan/Slice/WriteGcode` + `FdmPlating.Arrange`,
  the plate without the union): ascending-height order, pairwise clearance checked
  conservatively (bounds gap under-estimates the true gap — refuses legal, never accepts
  illegal), at most one over-gantry part and it prints last (a second refused naming both);
  the combined program strips middle headers/tails, hops above everything completed and
  moves XY BEFORE descending at each handover, resets E with G92 E0 — the decoder reads the
  whole file and the filament total is the sum of the parts' own.

- **Flat/bull-nose waterline** (silhouette-dilation): the collision region at each tip
  level is the XY silhouette of the part above the tip plane grown by the tool's reach —
  exact vs the mesh for flat, a banded conservative ladder for the bull corner (band k
  clips above z + r·k/K, grows by the band's OUTER reach: over-covers, stock never gouge);
  the 45°-cone oracle brackets the banded 3.661 between the exact 3.414 and the sharp 4.0.

- **No-retract row linking** (`Raster(linkRows: true)`, opt-in, default byte-identical):
  serpentine rows merge into ONE pass, connectors sampled ON the CL surface through the same
  tipAt (the fidelity of a within-row chord — gouge-free by the same construction); one
  plunge replaces one per row, both cutter routes through the one serpentine rule.

- **Laser / drag-knife cutting** (`CncLaser.Cut`/`WriteGcode`, `LaserTool`): one outward
  offset by kerf/2 gives every beam path with the compensation right (outer loops out into
  the waste, hole loops into the holes — the freed part measures the drawn dimensions),
  holes first (the release rule); GRBL M4 dynamic-power G-code with NO Z word anywhere,
  read by the twin decoder (cut length verified through the decoded file at the writer's
  own micron quantization grade).

## G-code (`GcodeWriter`, `GcodeReader`)

The writer is Marlin/RepRap flavour and STATES its modes (G21/G90/M82) because a reader that
cannot see them cannot check them. **The extrusion bookkeeping is an identity, not a
calibration**: every E is cumulative filament with `ΔE = segment length × BeadArea /
FilamentArea` (the stadium bead model, `h·(w − h) + π·h²/4`), and the twin-decoder
`GcodeReader` re-derives BOTH sides from the file alone — deposition length from coordinates,
filament from E deltas — so the tests assert the identity on decoded values, where a structural
validator would prove nothing. Retraction is a stationary negative-E move paired with an equal
unretract (decoder-matchable); temperatures are write-only-when-stated; two writes of one slice
are byte-identical.

The decoder is scoped to what the writer emits, with the traps refused BY NAME rather than
mis-read: `G20` (inches — the unit trap), `G91` (relative coordinates) and `M83` (relative
extrusion) each refuse, because decoding them as their absolute siblings would produce
confidently wrong geometry; `G2`/`G3` arcs DECODE in the I/J centre-offset form (expanded
into 5°-sampled sub-moves so every downstream identity reads the arc as the fine polyline it
machines as), while the ambiguous `R` form, a missing centre and endpoints that disagree
about the radius refuse by name.
Unknown M-codes and comments are dirt — noted, never thrown.

## 2.5D CNC milling (`CncMill`, `MillTool`, `CncGcodeWriter`)

Stage 2 — pocket, profile and drill over the same landed machinery:

- **Pocket** is the inward-offset ring ladder (`Region2dOffset`, rings one `Stepover·D` apart,
  an island's grown boundary ridden like any other loop), executed innermost-first per
  `StepDown` level with the last level clamped to the exact depth. `Stepover ≤ 0.5` provably
  covers the whole reachable area.
- **Profile** is one outline offset by the tool radius (round joins — the path a tool centre
  physically rolls around an outside corner), with holding TABS on the final pass only, evenly
  spaced by arc length, each a vertical rise at the tab's own edge — never a diagonal ramp that
  would leave the closing stretch part-cut.
- **Drill** ships EXPANDED peck moves (plain G0/G1), so the same twin decoder reads a drill
  cycle with nothing new; opt-in canned `G81`/`G83` cycles via
  `CncGcodeWriter.Write(..., cannedDrilling: true)`.
- **`CncGcodeWriter`**: a move's meaning is its SHAPE — an XY move cuts at the feed rate, a
  straight-down move plunges, a straight-up move retracts as a rapid — so one `MillPass`
  vocabulary carries all three operations. The decoder gained a `Rapid` flag (G0 vs G1),
  because feed state persists across both and cannot separate them on its own. Opt-in
  `arcFitting` emits co-circular constant-z runs as one `G2`/`G3` (I/J form) with each
  chord's sagitta capped at the file's own 1e-3 coordinate quantum — the cap, not the
  on-circle test, is what rejects the mirror-symmetry phantom (IEEE negation is exact, so
  four points spanning a straight side can be EXACTLY concyclic on a 675 mm circle whose
  arc would gouge 0.027 mm).
- **The oracle is the morphological OPENING**: a radius-r tool reaches exactly
  `grow_r(shrink_r(region))`, the passes' stroked-and-unioned footprints (the machined-stock
  simulation) must equal it, and a rectangular pocket's unreachable corner residue is CLOSED
  FORM, `(4 − π)·r²`. No-gouge is exact point-by-point. Docs: `docs/examples/cam-milling.md`.

## The machined-stock simulation (`CncStock`)

`CncStock.Simulate(stock, operations, states)` records the stock at N fractions of the total
cut length — each state an ordinary `Shape`. The swept volume of a 2.5D pass is CLOSED FORM (a
constant-z run occupies its stroked footprint from its level up through the stock; a vertical
descent bores an inscribed-32-gon disc, so a drilled state's volume is an EXACT prism), every
pass is entered by a plunge (a single-point drill pass bores its disc through the pass-entry
rule, no special case), and the removal subtracts as z BANDS through the MESH imprint boolean —
one level to the next, so successive levels repeating one footprint never hand the boolean two
coincident side walls. A surfacing pass (XY and Z moving at once) is refused BY NAME — the
3-axis swept volume is not a prism; filed. A state is a still or an export, deliberately not a
live clip (a changing-geometry animation has no matrices-only form — the transient-thermal
precedent); the TOOL animates along its path as an ordinary pose track, `PathTracks.Follow` in
the viewer (arc-length parameterized, matrices only).

## 3-axis surfacing (`CncSurfacing`)

Stage 3 — ball-nose finishing, and the place the implicit engine pays directly: **the
cutter-location surface of a ball-nose tool IS the field's r-offset**, so both strategies read
the shape's own SDF instead of approximating an offset mesh.

- **Raster** (parallel finishing): serpentine grid-anchored rows, each sample's tip height a
  SPHERE TRACE down the vertical ray to the r-isolevel — gouge-free BY CONSTRUCTION, because a
  1-Lipschitz field's `sdf − r` step can never cross the offset surface (a stalled trace stops
  HIGH: stock left, never a gouge).
- **Waterline** (constant-z contouring): the CL contour at a centre plane IS the SDF's
  r-isolevel there — `SdfContours.OnPlane` marching squares chained by exact endpoint
  equality, polished onto the isolevel by an IN-PLANE Newton step (the correction must not
  leave the plane, or the pass stops being a waterline) — exact to round-off on the steep
  walls waterline exists for.
- **Scallop arithmetic is a chord identity**: `h = r − √(r² − (s/2)²)` with
  `StepoverForScallop` its exact inverse; the classic `s²/8r` is its measured small-stepover
  expansion. Passes are in the shape's own coordinates (G-code z = the TIP) and the stage-2
  `CncGcodeWriter` carries them unchanged. Flat/bull-nose cutters, raster angle, no-retract
  row linking and rest machining have since landed. Docs: `docs/examples/cam-surfacing.md`.
- **`AdaptiveRaster`**: the scallop height is the stated number and the row spacing follows
  the surface — each next row placed by bisection on the measured worst 3D CL-point
  distance through the same chord identity, so a 45° slope spaces at exactly cos 45° times
  the flat spacing. Corner radius governs (a flat cutter leaves facets, refused by name);
  rows anchor to the part (a variable spacing has no stated number for the phase rule to
  hold); cliffs floor at 1/32 of the flat spacing — the wall belongs to the flank.
- **`CncHolder.Check`**: holder collision over finished geometry. The holder is a
  conservative flat disc riding `StickoutLength` above the tip, so a pass point collides
  exactly when the flat drop-cutter height at the holder's own radius exceeds
  `cl.z + stickout` — the same `DropProbe` contact arithmetic the flat cutter rides, so the
  two cannot disagree about what a disc touches. The report carries every colliding point
  and `MinimumStickout` (the smallest stickout that clears; at it the setup passes, since
  zero clearance is resting contact). Exact for finishing passes, a lower bound for
  roughing — in-process stock is the caller's margin.

## HSM: trochoidal slotting (`CncHsm`)

Stage 4's first step. The defining invariant is the ENGAGEMENT ANGLE (the arc of tool
circumference in material): `TrochoidalSlot` rides circular loops that advance a small step
per revolution behind an Archimedean spiral-out entry, and **the advance is solved by
bisection against a steady-state model of the measured quantity** — the straight-cut relation
`a = r·(1 − cos φ)` is measurably wrong here (a 60° ask measured 90°, because a trochoid cuts
against the previous loop's CONVEX swept boundary) — with the tests re-measuring the real
path's engagement from the evolving stock independently, a straight slot cut as the ~180°
control that proves the instrument. The spiral entry's honesty is stated: its contact ARC is
wide but shallow (the bounded quantity there is the radial step, the chip load). Swept
footprint = the slot stadium within 2%; no-overcut point-by-point; refusals by name. Filed:
general adaptive (constant-engagement) pocketing, helical z entry, trochoidal linking of
`Region2dThickness` necks, and the trochoid × stock-record composition (scallop cusps are
near-tangent crossings, the imprint boolean's hostile family).

## Verification (the campaign's own bar)

Layer z's as exact arithmetic; wall perimeters against closed forms (an inward offset of a
rectangle is a rectangle, so wall 0's perimeter is exactly `2(a − w) + 2(b − w)`); the wall's
clearance from the section boundary as an exact point-by-point claim (the no-gouge analogue);
infill alternation, perpendicularity and containment; solid-infill coverage as a MEASURED ratio
with its deviations attributed (the stadium bead's corner deficit, the scan's edge margins);
the extrusion identity through the decoder; matched retraction pairs; determinism byte-for-byte;
refusals by name. Docs: `docs/examples/cam-slicing.md`.
