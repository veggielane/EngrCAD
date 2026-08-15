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
confidently wrong geometry; `G2`/`G3` arcs refuse by name (they join with the CNC stages).
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
  cycle with nothing new; canned G81/G83 cycles are filed.
- **`CncGcodeWriter`**: a move's meaning is its SHAPE — an XY move cuts at the feed rate, a
  straight-down move plunges, a straight-up move retracts as a rapid — so one `MillPass`
  vocabulary carries all three operations. The decoder gained a `Rapid` flag (G0 vs G1),
  because feed state persists across both and cannot separate them on its own.
- **The oracle is the morphological OPENING**: a radius-r tool reaches exactly
  `grow_r(shrink_r(region))`, the passes' stroked-and-unioned footprints (the machined-stock
  simulation) must equal it, and a rectangular pocket's unreachable corner residue is CLOSED
  FORM, `(4 − π)·r²`. No-gouge is exact point-by-point. Docs: `docs/examples/cam-milling.md`.

## Verification (the campaign's own bar)

Layer z's as exact arithmetic; wall perimeters against closed forms (an inward offset of a
rectangle is a rectangle, so wall 0's perimeter is exactly `2(a − w) + 2(b − w)`); the wall's
clearance from the section boundary as an exact point-by-point claim (the no-gouge analogue);
infill alternation, perpendicularity and containment; solid-infill coverage as a MEASURED ratio
with its deviations attributed (the stadium bead's corner deficit, the scan's edge margins);
the extrusion identity through the decoder; matched retraction pairs; determinism byte-for-byte;
refusals by name. Docs: `docs/examples/cam-slicing.md`.
