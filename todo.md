# EngrCAD — TODO / idea backlog

Open work only — completed items are removed as they land (the record lives in git
history and CLAUDE.md's status). Many items come from a survey of **geometry3Sharp**
(`C:\Users\chris\projects\git\geometry3Sharp`, Ryan Schmidt / gradientspace —
triangle-mesh + implicit library; no half-edge, no BSP, no B-Rep, so it complements
rather than duplicates our engines) and name the g3 classes worth studying before
implementing. Ordered roughly by value-for-effort within each section.

## Mesh engine (EngrCAD.Mesh)

Wave-A ✅ landed: `EditableMesh` (guarded Euler operators + journaled bit-identical
undo), STL/OBJ/OFF readers + `MeshRepair` v1, `HoleFiller` (simple/planar/FillAll),
`MeshExtrude` (faces/thicken/selections), selections + connected components. Wave-B ✅:
`Remesher` (isotropic, vertex-keyed constraints), `HoleFiller.FillMinimal`/`FillSmoothed`,
`MeshDecimator` on `EditableMesh`, BSP boolean retired (`Csg.cs` and `BooleanMethod`
deleted; the imprint boolean is the only one). Wave-C ✅: `SdfProjectionTarget` (Interop),
seam refinement in `MeshRegionOperator` + `LoopSubdivision(preserveBoundary:)`,
`RemesherPro` scheduling (`RemeshScheduling.Queue`, `FastSplitPasses`), face-aligned
(RZN-flow) reprojection, `RegionRemesher`, `Shape.Remeshed`. Remaining:

- ~~**The remesher's longest edge converges far more slowly than its distribution.**~~
  ✅ **done** — `RemeshOptions.PreventLongEdgeFlips`, and **the filed diagnosis was wrong**.
  It is not "a collapse creates a fresh edge of up to twice the target which the next pass
  has to find and split", and no within-pass re-visit queue was needed (there is no cascade
  to guard against: one split-only round over a converged mesh clears every out-of-band edge
  and a second round finds nothing). The cause is the **flip stage**, established by
  subtraction — switch flips off and nothing else and the same run ends at *exactly* 1.33 L
  with nothing out of band, because the sweep already splits everything too long, while
  switching the smoothing and projection stages off instead leaves the maximum at 2.07 L.
  The flip predicate is pure valence arithmetic that never looks at a length, so on an
  elongated quad it swaps the short diagonal for the long one. Measured (target 2.0, 14
  passes) baseline → guarded: cylinder max 2.01 → 1.46 L / in band 94.6% → 99.6% / 24 → 18 ms;
  box 2.22 → 1.32 L / 95.1% → 99.8% / worst angle 5.57° → 28.93°; sphere 1.83 → 1.31 L /
  96.4% → 99.9°%. Two residuals:
  - [ ] **Should `PreventLongEdgeFlips` be the DEFAULT?** It improves the in-band share, the
    maximum, the shortest edge and the run time together on a cylinder, a box and a UV
    sphere, which is not the "different answer, not the same one faster" that made
    `Scheduling` opt-in. It was left off only so this change moved no committed output
    (0 of 107 docs PNGs). The one genuinely mixed measure is the **cylinder's worst triangle
    angle**, 0.89° → 0.58°, since a refused flip is a valence left irregular — worth
    understanding before flipping the default, because the same measure improves several
    fold on the box and the sphere. Flipping it would move `Shape.Remeshed` output and the
    `remesh-plate` render.
  - [ ] **The remesher has no shape-quality measure of its own.** Everything it reports and
    everything the tests assert is edge LENGTH (the `[0.66 L, 1.33 L]` band), which says
    nothing about slivers: the box fixture above sits at 95.1% in band with a worst triangle
    angle of 5.57°, and the strict form of the flip guard reached 99.7% in band at **0.02°**.
    A minimum-angle or radius-ratio figure on `RemeshResult` would have made that visible
    without a bespoke test helper (`TetQuality` is the precedent, one project over).
- ~~**Face-aligned projection accumulates over the whole mesh even under queue
  scheduling**~~ ✅ **done** — the accumulation now skips every face with no vertex in the
  active set, which is sound because a face contributes only to its own vertices. Measured
  (`UvSphere(1, 48, 32)`, target 0.08), queue scheduling whole-mesh → restricted: 124 → 123 ms
  at 12 passes, 322 → 285 at 40, **623 → 302 at 100** (2.06×, and 3.07× against the plain
  sweep). The shape matters more than the ratio: the whole-mesh figure keeps growing with the
  pass count while the restricted one nearly flattens, so a converged mesh finally costs
  almost nothing per extra pass. Bit-identical, because the walk keeps its **ascending face
  scan** and skips only the projection query — no sort needed, where gathering the incident
  faces into a list would have needed one (built, measured no faster, dropped). Residual:
  - [ ] **Sweep scheduling still walks every face**, deliberately: with every vertex active
    the restriction could only add a membership test per face. If a future caller wants
    face-aligned projection over a large mesh with an explicit small `FixedVertices` set, the
    same skip would apply — but nothing asks for it today.
- [ ] **`Part`-level display remesh** — `Shape.Remeshed` is a graph node, so a remesh is a
  modelling decision baked into the design. A viewer-only "give this part uniform triangles
  for display/FEA export" switch on `Part` (a post-tessellation pass inside `GetMesh`) is a
  different, smaller thing and is not built; it would need to interact with the mesh cache
  and `MeshQuality` precedence.
- [ ] Mutable in-place variants of fill/extrude once callers want them.

## Implicit engine (EngrCAD.Implicit)

- ~~**The bézier kernel's Newton stage is fixed at 8 iterations for every lane.**~~
  ✅/❌ **half landed, half measured and declined** — and the entry's premise was wrong in a
  useful way. It assumed "a convergence exit would change results ... so it needs the golden
  hashes re-derived deliberately". An **exact fixed-point exit does not**: g and g′ are
  functions of `refined` alone, so an iteration reproducing it bit for bit makes every later
  one recompute the same value and take the same branch. Spelled `next == refined` rather
  than as a tolerance (a tolerant stop *would* move results), it is provably identity, so
  the golden churn the item was weighing does not exist. Every golden hash is unchanged, and
  the batch-vs-scalar bit-identity test now independently verifies the argument, the two
  paths running different iteration counts and still agreeing to the bit.
  - **Landed on the scalar path**, where each solve exits as soon as its own parameter stops
    moving: exact counts say **50.0%** of Newton iterations on an all-bézier outline and
    **35.1%** on an engraving-shaped one are redundant. End to end that is only 1.09× and
    1.01–1.03×, because Newton is under half of a kernel that is itself behind the
    bounding-box reject — so the item's closing hint was right about the reject, just not
    about the correctness cost.
  - **Declined on the vector path.** A block exits only when its SLOWEST lane does, and ~30%
    of solves never reach an exact fixed point within the eight steps, so the max over four
    lanes is ~7.5 of 8 — about 6%, bought with three extra vector ops and a branch per
    iteration. Measured **0.99–1.03×**: nothing, one case a slight loss.
  - **The general lesson, which Item "arc certainty band" reached independently**: block
    granularity destroys per-lane savings, so an early exit that pays in scalar code usually
    does not vectorize. Worth reaching for before writing the next masked early-out.
  - Measurement note worth keeping: the first A/B used a MEAN over two passes and the same
    reject-dominated fixture measured **1.59× then 0.77×** on identical code — a 2× swing
    *within one sitting*. A minimum over four passes is the right estimator for a
    deterministic workload that scheduling noise can only slow down, and it collapsed that
    column to a stable 1.01×.
- ~~**The lane-wise arc kernel gives a whole block back to the scalar path when any one
  lane is inside the wedge certainty band.**~~ ❌ **measured and declined** —
  `SketchRegionBenchmark.ArcCertaintyBandCost` holds the measurement so nobody redoes it.
  **The scenario this entry named as the reason to build it is the scenario per-lane
  blending cannot help**: sampling *along* a boundary makes every lane uncertain for the arc
  being traced, so there is nothing left to keep vectorized and blending recovers exactly
  zero. Nor is it a cliff, because the fallback is per SEGMENT — only the traced arc
  degrades while the rest of the sketch vectorizes as usual, measured `batch/scalar`
  2.48× → **1.45×**, not → 1. Blending only pays on a block with SOME uncertain lanes, which
  took deliberate construction to produce (sample stride aligned to the register width,
  four arcs' boundaries visited in rotation: 1.05×) and which a scan line structurally
  cannot generate, its consecutive samples being collinear and so meeting one boundary
  rather than four. Detail worth keeping: the band covers the LINE through the centre, not
  the forward ray (`c₀ = f × o` vanishes both ways), so a horizontal scan line at a rounded
  rectangle's corner-centre height lands in two arcs' bands at once — that is the realistic
  version, it is what the 1.45× row measures, and blending buys nothing in it either.

## Interop / meshing (EngrCAD.Interop)

- ~~**A grid quad's diagonal is chosen by CORNER ORDER, not geometry.**~~ ✅ **done** —
  `PolygonFan` is now the one rule (shorter 3D diagonal for quads, corner-0 fan for
  n-gons) and every consumer goes through it: `Triangulated`, `SignedVolume` via
  `FaceFanStart`, `MeshMassProperties`, `MeshConnectedComponents`, `RenderMesh`, and the
  STL/3MF/AMF writers. The mirrored thread now measures identically to its twin
  (`ThreadShapeTests` tightened from a 2.5% band to 1%, plus an exact 9-digit equality).
  Two findings came out of it: the tie guard has to be RELATIVE, because a UV-sphere
  quad's diagonals are mathematically equal and an exact comparison gave 408 of 960
  splits to round-off; and the win is *consistency* rather than universally less error —
  on a saddle cell the two triangulations bracket the surface with equal magnitude.
  18 of 87 docs PNGs move (SDF/Surface Nets, threads, lofts). Residual:
  - [ ] **The repair/import fans are deliberately untouched** (`MeshRepair`,
    `MeshSoupOps`, `StlReader`): they decompose soup that is not a mesh yet, where the
    fan is a documented fallback for input earcut declined. Worth revisiting only if a
    dirty-import case is ever traced to a fan diagonal.
  - [ ] **A quad is still fanned, not optimally triangulated.** For n > 4 the corner-0 fan
    remains, and on a non-convex n-gon that is simply wrong geometry — nothing in the
    kernel produces one today (planar faces earcut before they reach here), which is why
    it was left alone, but it is where the next defect of this family would live.
- [ ] **`SdfProjectionTarget` stalls on a CSG difference's fictitious faces.** Its
  guarantee is one-sided (a 1-Lipschitz lower bound puts the surface at least |d| away, so
  a step can never cross it) but |d| need not decrease: inside material a subtracted tool
  removed, the field measures the distance to the tool's own surface and the gradient jumps
  at the branch switch, so two branches trade the point back and forth — measured on
  `Box(2,2,2) − Sphere(1.2)`, six steps leave a probe exactly where it started while the
  true distance to the real rim is 0.45 (pinned by a test). Harmless for remeshing, which
  only ever projects near-surface points, but it is why this must not be offered as a
  general closest-point query. A real one would need the field's own structure (a CSG walk
  that knows which branch is a real face there), not more iterations.
  **Scoped** (assessed, not built): the walk is *candidate generation plus a membership
  filter*, and it needs one new virtual rather than a new algorithm. Give `Sdf` a
  `TryClosestPoint(p, out c)` that primitives answer in closed form (sphere, box, cylinder,
  torus, capsule, half-space all have one) and that operators answer by UNIONING their
  children's candidates rather than by combining distances; then keep only the candidates
  that are real points of the composed solid — a candidate `c` is real iff the whole field
  reads `|d(c)| ≈ 0` there, which is exactly what a fictitious face fails (it sits strictly
  inside removed or kept material) — and take the nearest survivor. That is exact for hard
  CSG over closed-form primitives, and `MeshSdf` already has an exact answer of its own
  (BVH nearest triangle). It does NOT cover smooth blends or offsets, whose surface belongs
  to no child, so those keep the iterated gradient step — and the API must then report
  WHICH answer the caller got, since an exact closest point and a converged-ish one are
  different contracts and must not share a return type silently.
- ~~**Surface Nets mesh ASSEMBLY is now the dominant cost, not sampling.**~~ ✅ **done, but
  not the way this entry proposed.** The grid does NOT give twins for free: a dual edge is
  a grid FACE and matching its up-to-four claimants needs a face table the streaming
  window cannot hold. What worked was making the GENERIC builder fast — twin resolution as
  a counting sort over each edge's lower endpoint instead of a `Dictionary<(int,int),int>`,
  plus flat index buffers instead of one `int[4]` per quad — which serves every caller and
  leaves one implementation rather than two and a cross-check. Assembly 3.3–3.7×, whole
  polygonization 1.2–1.5×, allocation at res 256 145 → 103 MB, output bit-identical.
  Residual:
  - [ ] **The grid WALK is now the cost** (~175 ms of a 213 ms res-384 polygonize; assembly
    is 15–18%). The named candidates are the per-cell `int[12]` crossing map (one heap
    allocation per mixed cell — the same defect the quad arrays had), the crossing
    interpolation, and the three quad passes re-reading `values` through `Corner()`.
    Re-measure before choosing: that is what this entry's own history argues for.
- ~~**Surface Nets' manifold contract was false in general.**~~ ✅ **done.**
  `Sphere(16).Lattice(Gyroid(12, 1.2))` at resolution 88 threw `Directed edge 4954 → 4967
  appears twice` from `HalfEdgeMesh.Build`. The gap: an *ambiguous* grid face (inside
  corners exactly a diagonal pair) has all four edges crossing, so it carries two quad
  edges between the same two cells, and when BOTH cells join that pair into one component
  the two quads share a directed edge. Fixed by splitting such a component by the outside
  blob each crossing reaches, tested once per interior face by the cell on its + side; the
  split provably always exists. Only the broken configuration changes, so the golden
  fingerprints and all 97 docs PNGs are untouched. Reproduced on three gyroid variants and,
  tellingly, on a plain `Sphere(10).Shell(0.6)` at resolution 44 — a one-cell-thick wall,
  not a lattice. Residuals:
  - [ ] **A saddle cell whose inside is connected but whose OUTSIDE splits in two has two
    surface components and still gets ONE shared vertex** — the mean of crossings from two
    separate sheets. Manifold, so not this bug, but a geometry-quality question: the general
    rule is one vertex per (inside blob, outside blob) interface, which is exactly the
    surface-component count. Measured cost of adopting it unconditionally: 204 split cells
    in the res-88 gyroid against the 6 that were actually broken, plus 1 cell in the `csg`
    golden and 2 in `torus` — so it moves fingerprints and probably PNGs, and wants its own
    decision with renders looked at, not a silent ride-along on a manifoldness fix.
  - [ ] **Only ONE of the two cells splits** (the + side owns the test), because the sliding
    window cannot promise a cell the slab beyond its + neighbour and the window may be as
    small as two slabs. Manifoldness needs only one side, but the result is side-asymmetric;
    making it symmetric means either a 4-slab window (breaking the "output is independent of
    the window size" invariant, which is locked by test) or carrying a second forward record.
  - [ ] **A non-manifold interior VERTEX is neither checked nor proven absent.** `Build`
    rejects duplicated directed edges and bow-tie *boundary* vertices; an interior vertex
    whose link is two fans would pass. Nothing has produced one, but the contract claim
    should say what it covers.
- [ ] **`MeshSdf` batch queries: two levers measured, both declined — don't redo either.**
  74–85% of a mesh narrow band's wall clock is inside `Bvh.Nearest`, so the headroom is
  real, but *seeding* the branch and bound measured 1.12–1.20× (`MeshSdfBatchTests`) and a
  *packet* query measured 0.30–0.86× on the point layout the batch seam actually delivers
  (`MeshSdfPacketBenchmark`): a packet's shared bound is governed by the group's diameter,
  and every bulk consumer generates points z-fastest, so the groups are collinear rows.
  Pruning on squared distances throughout is 0.94–0.99×. A third attempt needs a lever that
  is neither the initial bound nor the traversal amortization — e.g. giving the batch
  contract a way to say "these points form a compact block", which the 1.45× measured on a
  2³ block would then be reachable through.
- [ ] **Trimmed-band gaps left by the strip path** (`TrimmedFaceTessellator`).
  - ~~A **rung sampled at more than two points** (a curved cross edge)~~ and ~~a band
    whose two chains **meet at a point** (a rung of zero steps)~~ ✅ **both done** — not
    by two special cases but by replacing the rung-counting split with the same monotone
    **stack sweep** the slab path uses, which is correct on any monotone polygon and
    handles a tied run of end vertices and a single apex as its ordinary start/finish
    cases. The tie-breaking is the load-bearing detail: the extremes are the LAST of the
    tied minimum run and the FIRST of the tied maximum run, so a whole tied run lands on
    ONE chain — split across both, the merge interleaves the sides at equal keys and the
    sweep is asked to triangulate collinear points. The old zip stays as a fallback, and
    every band in the docs tessellates bit-identically either way (all 52 rendered PNGs
    unchanged). **Neither shape is reachable from the `Shape` API yet**: the
    constructions that would produce one — `Sphere(10) − Box(20,20,40).Translate((10,10,0))`
    (a spherical band between two meridian cuts) and `Cone(8,0,12) − Box(...)` (a cone
    fragment through the apex) — are refused earlier by the exact B-Rep boolean with
    "produced an unclosed solid", and a sweep of eighteen further candidates (filleted
    rounded rectangles and slots, chamfered arcs, tilted cylinder cuts, drilled cones and
    tori, cut lofts, sweeps and vases) reached neither. Coverage is `TrimmedBandGapTests`,
    on hand-built faces.
  - ~~**Bands with interior hole loops** still ear-clip (`TriangulateBandWithHoles`)~~
    ✅ **done** — and it *was* visible: the cross-drilled bore wall in
    `docs/examples/images/section-oblique.png` rendered as a crumpled fan. This entry
    called it "the same defect waiting", which was right, but the mechanism was worse than
    predicted: the ear clipper is **structurally forced** into a fan there, because both
    ring loops pull back to a bit-identical v so `IsEar` rejects every corner along both
    chains. `ZipSlabs`/`SweepMonotone` now decompose the unrolled band into u-monotone
    slabs; the ear clipper stays as the fallback. Bore wall at 128 spc: 12 164 triangles /
    worst dot 0.0198 → 416 / 0.99981, and the volume converges quadratically instead of
    stalling.
  - **Tier 4 `TriangulateRegion` still ear-clips**, so a non-wrapping region with an
    exactly uv-collinear boundary run would hit the identical forced fan. Nothing in the
    suite or the docs exercises it, so the slab sweep was deliberately not widened to it —
    but the mechanism is now understood, and this is where it would resurface.
- ~~**Trimmed-face refusals are now loud — find out what they refuse.**~~ ✅ **answered**
  — **neither is reachable from the `Shape` API today**, and `TrimmedFaceRefusalTests`
  locks that verdict along with the constructions tried and what stops each one earlier.
  A latitude cut DOES give a sphere a pole-bounded cap with a single winding loop, but
  drilling that cap off-axis does not put a hole in it: the boolean re-splits so the
  bore's rim lands on the two-ring band below, which the slab sweep already handles —
  which is why the pole-bounded-with-holes tier has never been needed. Cutting the cap
  lower, or the same on a cone or a torus, fails earlier in the boolean ("produced an
  unclosed solid"). |winding| > 1 needs a curve wrapping a periodic surface twice, which
  only a helical intersection produces, and those are refused before tessellation
  (`Cylinder − ExternalThread` throws `ShapeConversionException`; a threaded pocket in a
  sphere throws `BrepBooleanException`). Both refusals stay as backstops and are now
  exercised directly on hand-built faces, so the messages cannot rot.
- ~~**A per-face triangle-quality assertion for the whole tessellator.**~~ ✅ **done** —
  `TessellationCorpusQualityTests` + the shared `TessellationQuality` audit, over 21
  constructions at three densities. It caught three real defects on its first run, and
  the two structural ones are fixed (see the Interop README): a reversed face's polygons
  were re-wound with `Reverse()`, which MOVES the downstream fan diagonal from a–c to
  b–d — 5 544 of 30 912 facets of an M8 threaded hole faced inward (worst −0.163) while
  the identical unsubtracted rod was clean; and the two-ring periodic band used a merge
  walk where the monotone stack sweep was needed. Remaining findings are the two items
  below.
- ~~**Refinement quality upgrade — interior rows in the base triangulation.**~~
  ✅ **done, the second candidate fix (rows), and the evidence was right** — the base
  triangulation now carries the surface's curvature itself (`RowedStrip`/
  `RowedPeriodicBand`/`RowedPoleFan` in `TrimmedFaceTessellator`: the natural grid's own
  sample rows threaded between scallops with anchors on existing boundary vertices,
  full-period rows plus a closure duplicate for winding bands, seam chords pre-split
  with bit-identical twins, pole fans kept within ~1.5 steps of the pole; `Refine`'s
  step metric became per-axis max-norm so the grid's own cell diagonal stops counting as
  oversized, and pole-fan edges are refinement-exempt since the pole's u is arbitrary).
  Measured (i9-9900K win-x64): the drilled sphere `Sphere(10) − Cylinder(3, 40)` went
  **43 948 facets / 12 folds / worst −0.2022 → 3 244 / 0 / 0.9994** at 32/24, no longer
  refuses at 128/96, and its volume error falls at ratios 4.35 / 5.08 per doubling — it
  is now the corpus's 22nd member with an analytic (napkin-ring) volume row.
  `Box(20,20,20) − Sphere(12)` went **101 246 / 266 folds / −0.2426 → 4 608 / 0 /
  0.7024** at 48/24 and tessellates at 96/48 where it used to refuse. Refine is
  DEMOTED, not removed: with rows in place it is measured idle on 16 of 19 corpus
  members' trimmed faces and still fixes the residual coarse columns (base 3 folds →
  0 on Box − Sphere). Residuals filed below.
- [ ] **Trimmed-face residuals after the interior-rows upgrade** (all bounded, none a
  fold-or-refusal class):
  - `Box(20,20,20) − Sphere(12)` stays out of the corpus — **but the reason recorded here
    was backwards, and the measurement that settles it also rules out the obvious fix.**
    The entry said a narrow column at each hole rim's u-extreme "remains for refinement",
    i.e. that refinement was covering for a missing row. Measured with
    `AuditTrimmedPath(..., refine: false)`, the BASE triangulation is the BETTER mesh at
    every density from 48/24 up, and it CLEARS the corpus floor at 96/48 where the shipped
    (refined) result does not:

    | density | floor | refined | base only |
    |---|---|---|---|
    | 16/8 | 0.3827 | **0.8832** | 0.8369 |
    | 48/24 | 0.9239 | 0.7024 | **0.9014** |
    | 96/48 | 0.9808 | 0.9240 | **0.9814** |
    | 192/96 | 0.9952 | 0.9727 | **0.9952** |

    So what holds this member below the bar is refinement, not a missing level path — and
    note the 16/8 row, which is why the fix is not simply "refine less": at the COARSEST
    density refinement genuinely helps (0.8369 → 0.8832). Refinement helps where the base
    is coarse and hurts where it is already at grid density.

    **The blunt version was built and measured and is NOT the answer.** Strengthening the
    landed guard from "a split may not invert a facet" to "a split may not make any facet
    agree WORSE than its parent" does exactly what this table asks — refinement goes idle
    on this solid (worst dot equal to the base at every density, triangle counts within
    0.05% of it) and 96/48 clears the floor — and it breaks two other things: the 16/8 row
    above regresses below its own committed floor, and
    `WholeSolidFilletBooleanTests.BandCrossingTool_ConvergesWithTessellationDensity` stalls
    (steps 9.236e-3 then 8.741e-3, where the test requires them to shrink), because there
    refinement is doing real convergence work. A rule that cannot tell those two situations
    apart is not the fix.

    What is left is the original idea stated correctly: a row path that reaches the column
    at the rim's turning vertex, so the BASE is right there and refinement has nothing to
    do — plus, plausibly, a density-aware gate so refinement keeps its coarse-mesh duty and
    drops its fine-mesh interference. Committed baselines live in
    `SpherePiercingEverySide_HasNoFoldsAndABoundedResidual`.
  - **A sub-depth chamfer cone is an extreme-aspect strip, and its facets are coarse
    even though they are now fold-free.** Measured over all 76 scanned depths (M6×1 /
    M8×1.25 / M10×1.5 / M12×1.75, both ends, 64 segments per circle): 0 folds and 0
    slivers everywhere, worst facet-vs-surface agreement **0.513 … 0.979**, well under the
    corpus floor `cos(3·2π/64) = 0.957`. The cause is geometry rather than triangulation —
    at the shallowest step the cone band is 0.034 mm tall around a 25 mm circumference, an
    aspect near 740 — so the sweep cannot improve it and the fix would be density that
    follows the band's own ASPECT rather than the circle count (the same shape as
    `SurfaceIntersection`'s anisotropic second seeding pass, one layer up). Until then a
    chamfered rod is deliberately NOT a corpus member, and
    `SubDepthChamfersCarryNoFoldsAtAnyFraction` bars at 0.4 with the reason stated.
  - The hand-built spherical band with meridian cross edges (`TrimmedBandGapTests`,
    test 1) declines rows in BOTH orientations because its boundary coordinates tie
    bit-exactly in both axes (closed-form azimuth, deterministic profile solves), and
    `SweepMonotone`'s extreme-end tie handling meets exactly-collinear runs; it falls
    back to the rowless path at 2 612 facets / 0.8359 (was 2 784 / 0.1998). Real
    boolean boundaries carry ~1e-9 projection jitter and never tie exactly, which is
    why only the hand-built face shows it.
  - `TriangulateBandWithHoles`/`ZipSlabs` has no interior rows — irrelevant today
    because every reachable band-with-holes lives on a cylinder or extrusion (ruled in
    v, chords exact), but a revolved band with holes would want the same treatment.
  - ~~**A wrapping band whose boundary carries a coarse INTRUDING bump folds, and worse
    with density.**~~ ✅ **the folds are fixed, and the recorded diagnosis was wrong.**
    The entry blamed the periodic-band tier pairing its chains by u and falling to the
    inverting merge walk. Measured on that exact solid: **the merge walk is reached zero
    times at any density**, both chains ARE u-monotone, and interior rows engage normally
    — so the tier named was never involved. Every fold came from **refinement**: driving
    the same faces with `refine: false` leaves the base fold-free at 16/32/48/64/96/128/192
    alike, while refinement inflated the two tube halves ×4.1 at 192 segments and inverted
    53 facets. `Refine` now refuses a split that would turn an agreeing facet into an
    opposing one, and folds are 0 at every one of those densities (was
    2 / 0 / 0 / 1 / 1 / 14 / 53). **The same guard cleared a defect nobody had filed**:
    the drilled sphere, a corpus member audited only to 96/48, carried 127 folds at 192/96
    (worst −0.9367) on its pole-bounded face and now carries none.
  - **Refinement still makes a coarse-rim face WORSE, just no longer inside out.** Beside
    a marching-tracer rim — 15–17 samples baked in at boolean time, whatever the grid
    density — worst facet-vs-surface agreement at 192/96 is ~0.009 refined against ~0.18
    unrefined on `Torus(12,4) − plane − Ø3 bore`, and **0.0079 refined against 0.9144
    unrefined** on the drilled sphere's pole face. That the unrefined base is the better
    mesh is the finding: the fix is a row path that covers the region beside a coarse
    boundary so refinement stays idle there (the interior-rows argument, extended to a
    boundary the rows currently cannot level against), NOT another rule inside `Refine`.
    Recorded by `TrimmedFaceRefusalTests.TorusCutWithABore_...`, which now pins 0 folds at
    48/24, 128/64 and 192/96 and bounds the worst dot as a record rather than a bar.
    A second, independent lever on the same residual is to make the tracer's sample count
    follow the tessellation density instead of its own arc-length step.
  ~~Also (Frame3d work finding): bores drilled into extruded *side* faces miss the
  inscribed-ngon volume by ~5e-5~~ ✅ **fixed and verified** — see below.
- ~~**A bore drilled into an extruded SIDE face misses the inscribed-ngon volume by
  ~5e-5.**~~ ✅ **done** — closed by the bounded conic-clipping tier (`TryPatchQuadric`),
  and re-verified here: `SketchPlaneFrameTests.On_ExtrudedSideFace_DrillsIntoTheSide`
  asserts the volume as an IDENTITY (`< 1e-12` against the inscribed-ngon value at 128
  segments) and records the four densities as **7.1e-14 / 6.8e-14 / 4.3e-14 / −5.3e-14**
  at 32/64/128/256, where the tracer-polyline rim used to give a non-converging,
  sign-flipping −7.4e-4 / −5.3e-5 / +4.7e-5 / +6.5e-5. Two entries said this was still
  open; both were stale.
## Core (EngrCAD.Core)

- [ ] **A PIVOTED real sparse symmetric-indefinite factorization, if a consumer ever needs
  one.** `SparseLdlt` ✅ landed the symmetric-indefinite family (real + complex symmetric
  L·D·Lᵀ over `SparseCholesky`'s shared symbolic pass — see design.md §2(d) for the
  three-way weighing), and its REAL path is deliberately unpivoted: it factors iff every
  leading principal minor is nonsingular, which holds for shifted `K − ω²M` away from a
  measure-zero set of ω and for saddle systems with constraints ordered last, and it
  refuses an exactly-zero pivot loudly — but a NEAR-singular minor still amplifies
  round-off with nothing to repair it (`SmallestPivotMagnitude` is the tell). A real
  Bunch–Kaufman with magnitude-searched 2×2 pivots cannot ride the up-looking machinery:
  a 2×2 pivot merges two columns' patterns, so the precomputed symbolic structure and the
  AMD counts both go stale, and the honest version is a multifrontal/supernodal solver
  with delayed pivots (MA57's shape) — a project of a different order. File a consumer
  first: nothing in the repo today produces a real indefinite system that the unpivoted
  form plus the constraints-last convention cannot factor. (An interim half-step if one
  appears: one round of iterative refinement on the caller's side, which the
  pivot-magnitude report already supports deciding.)
- [x] ~~**`ShapeCompiler` coplanarity, and a finding under it**~~ ✅ **landed** — the
  companion `CoplanarFaceDistance` check now measures a genuine point-to-PLANE distance
  (`ShapeCompiler.BottomLiesInFacePlane`, one shared rule for `Drill` and `ThreadedHole`),
  so it is well defined at any tilt; the angle stays at acos(1 − 1e-6) with a geometric
  reason rather than a deferral. The coplanar-boolean evidence the item was waiting for
  says the guard STAYS: `CoplanarFaces.For` collects only `IsPlanar` faces and a drill
  tool's flat bottom is a `RevolvedSurface` pole cap, so the fusion tier cannot see it.
- [ ] **`Fitting3d.MinVolumeBox`'s per-family angle is a sweep + golden section, not an
  algebraic root solve** (the OBB itself ✅ landed). O'Rourke derives the critical angle in
  closed form; worth doing if a hull ever shows a minimum hiding in a bracket narrower
  than the 3.75° sweep. The box always contains every input point regardless. (~~a
  convenience overload in EngrCAD.Mesh~~ ✅ **landed** as `MeshFitting.MinVolumeBox(hull)`
  / `MinVolumeBoxOf(points)`, asserted bit-identical to the hand-written
  `Compute(...).Triangulated().ToIndexed()` dance.)
  **Correction worth remembering**: this item used to state, and `Fitting3d`'s own doc
  comment used to assert, that the minimum-volume box has a face flush with a hull face
  (Freeman–Shapira). That is FALSE in 3D — the regular tetrahedron on alternate corners
  of [−1,1]³ fits its cube at volume 8 while every face-flush candidate measures 16.
  The shipped implementation follows O'Rourke instead.
- [ ] **Exact tangents for `FaceSplitter`'s arrangement tracing.** `DepartureAngle` and
  `ArrivalAngle` take the chord to a point 2% along the edge, and the tightest-turn
  comparison then needs a `1e-12` angular guard to tolerate the approximation. Every
  analytic curve now overrides `Curve3d.DerivativeAt`, so the chord could become a true
  tangent pulled back through the surface's Jacobian. Needs surface partials at the node and
  a decision about singular Jacobians (poles). This is the change worth making instead of
  routing the tracing through `Arrangement2d` — see the assessment in design.md §5 for why
  that one is a no.
- [ ] **`Region2dBoolean.ContainedIn` is O(cells × operand vertices) — and a
  point-location index was BUILT, MEASURED and DECLINED, so the next attempt has a bar.**
  The filed premise ("a per-operand `Region2d` point-location index would close it") was
  implemented in full — a per-operand y-bucket edge index asking Region2d's own per-edge
  rules (`OnSegment`/`RayCrossesEdge` extracted as shared single-edge predicates), result-
  identical by construction since parity is an order-free count over edges no skipped edge
  can pass, `Region2dGoldenTests` byte-for-byte — and measured NOTHING on the very
  workload this entry cited: the now-committed `Region2dBooleanBenchmark` (120 and 480
  overlapping 32-gons) read 40.1 → 41.8 ms and 135.7 → 137.8 ms (win-x64, i9-9900K,
  minima over interleaved runs of both binaries). The reason is structural: an
  overlap-heavy union's balanced fold keeps the CELL count tiny exactly where the operand
  vertex counts grow (two half-union blobs merge into a handful of cells), so the C·V
  product never gets large and the classification cost is under the ~5% noise floor of
  the whole union. **The term is real and the workload that would feel it is different**:
  an operation whose result KEEPS many cells against many-vertex operands — two
  interleaved combs intersected, a grid of crossing strips — which nothing in the repo
  currently produces at scale. Re-attempt only with such a consumer measured first, and
  hold it to the committed benchmark plus a new fixture that provably carries the C·V
  term. **Still owed**: re-benchmark the arrangement broad phase on a quiet machine — the
  candidate-pair reduction is a solid 9.1%, but the wall-clock numbers were taken under
  load and disagreed by 3×.
- [ ] **`Bvh.Build` follow-ups** (the build ✅ landed 4.9× faster and bit-identical) —
  reusing a hierarchy across a boolean cascade is untried, and after the fix the broad
  phase is 10.0 ms of a ~199 ms exact union, so the remaining wins are elsewhere.

## B-Rep / sketching (EngrCAD.BRep)

- [ ] **`SketchRegion.SignedDistance` returns the wrong SIGN in a ~1e-13 band at a full
  circle's parity seam** (found 2026-08-02 while measuring planetary ring geometry; the
  MAGNITUDE is right and only the parity flips, however far the point is from the
  boundary). Measured on a plain `Sketch.Circle(70.5)`: the point (60, 0) reads
  **−10.5** (inside, correct) and **(60, −1.47e-14) reads +10.5** (outside, wrong);
  (60, −1e-9) is correct again, so the band is narrow and sits at the seam's own
  ordinate. It is reachable from ordinary code without anyone writing a tiny number:
  `sin(2π)` is −2.45e-16, so sampling a full turn INCLUSIVELY (`θ = 2πi/n`, `i` up to
  `n`) lands the last sample exactly in it — which is how it surfaced, as an **odd 121
  boundary transitions** around a 60-tooth ring, combinatorially impossible on a closed
  curve. This is the recorded closed-curve seam family (CLAUDE.md: "a +x parity ray
  whose ordinate falls INSIDE it counts the seam piece's two endpoints on opposite
  sides"), so the fix is likely the same one — the full-turn arc's end point must BE its
  start point exactly, and the first/last y-monotone piece must take its ordinate from
  the STORED endpoints — applied to whichever path `Sketch.Circle` takes into
  `SketchRegion`. Both gear test files currently sample `[0, 2π)` and close the cycle
  explicitly to route around it; **those workarounds should come out when this lands**,
  since they are the evidence it was here. Worth checking whether `Sdf.ExtrudedRegion`
  and `RevolvedRegion` inherit it (a prism's field does not vary along z, so a whole
  scan line at the seam ordinate would be wrong, not one point).

- [ ] **Threads follow-ups** (B-Rep-native external threads AND threaded holes ✅
  landed — `HelicalSurface`/`SpiralArc3d`/`MakeThreadedRod`, boolean-free lateral
  sweep, clipped-pilot hole tool; **left-hand threads and the ISO 261 fine-pitch
  series** ✅ landed too; **general trimmed helical FACES and the coaxial analytic
  intersection family** ✅ landed as well — see below) — remaining:
  - [ ] **(a) 45° end-chamfer cones in B-Rep — a SUB-DEPTH chamfer ✅ landed; a residual
    ~10% of depths still refuses.** `Shape.ExternalThread(..., chamferLength: 0.5)` now
    lowers to a `Validate`-clean, two-manifold solid whose tessellation is closed and whose
    volume converges: one ordinary difference against the new
    `SolidFactory.MakeThreadEndChamferTool`, every pair it makes analytic. Measured
    (win-x64, M8×1.25, 6 mm rod, 32/64/128/256 segments per circle) the plain rod is
    246.5616 / 247.7583 / 248.0578 / 248.1329 and one 0.5 mm chamfer 245.8516 / 246.9694 /
    247.2383 / 247.3058, so the chamfer measures 0.7100 / 0.7889 / 0.8195 / 0.8271 —
    settling on the prototype's ~0.83. Every vertex of the chamfered B-Rep tessellation
    reads |sdf| ≤ 1.14e-15 against `Sdf.Thread`'s own chamfered field, so the two
    representations are the same geometry rather than similar ones (`ChamferedThreadTests`).
    The `chamferEnds: true` DEFAULT — a chamfer of the full thread depth — stays Impossible
    by name: the cone's base lands exactly on the minor diameter and therefore tangent to
    every root band along the end plane.

    **Four defects had to go, and only one was about chamfers** (all four written up in the
    READMEs). The recognizer did NOT decline the cone — it declined the tool's coaxial
    ANNULUS, which `TryCoaxialProfileLine` refuses by design (its b is infinite) and whose
    doc comment said it was "handled by `PlaneHelical` after the caller synthesizes the
    plane", except no caller ever did; the pair fell to the tracer and its polyline ended
    strictly inside the band. A cone's cut on a CONSTANT-radius band is a circle exactly,
    but the general expressions reached it only up to rounding, so `IsPlanar` — an
    exact-zero test — came out true at one end of a rod and false at the other.
    `CurveSegment.PointAt` wrapped a parameter one ULP past an OPEN base's domain end,
    teleporting the last sample to the base's START (0.375 mm off the face, after which the
    band tier stopped recognizing a band and the ear clipper folded 244 of 3562 facets).
    And `BRepTessellator.SampleEdge` read a `CurveSegment`'s [0, 1] domain as RADIANS,
    giving every split spiral edge the same count at any density.

    ~~**What remains**: a sporadic ~10% of chamfer depths still fails, loudly…~~
    ✅ **fixed** — and two things in the old diagnosis were wrong, both worth recording.
    The failures were **not loud**: re-scanning at `88d6e14`, all 76 depths built,
    validated and tessellated without an exception, so nothing failed in the boolean at
    all — what remained was 10 depths (0 / 4 / 3 / 3 per size, not 1 / 3 / 1 / 2) emitting
    SILENT folds, which is a worse failure than a throw and is why the count-based
    assertions never saw it. And the tie-break was **innocent**: `SweepCycle` takes the
    LAST of the tied minimum run and the FIRST of the tied maximum run exactly as
    documented, and on these loops there are no ties at all — measured, `lo` and `hi` are
    each a single vertex. The congruence the entry noticed was real but pointed one level
    down: the two cone faces differ in which chain is DENSE (65 samples against 8), so on
    one of them `lower[0]` lands at the same v as the whole 65-sample upper run, and every
    pop test along that run then compares three points that are collinear BY CONSTRUCTION.
    The defect is `SweepMonotone`'s `TurnsIntoInterior` reading the exact sign of a cross
    product whose true value is zero: pure round-off, so the sweep popped on ~1e-15 of
    jitter and fanned the rim flat into the end plane at facet-vs-surface agreement
    **−0.7071 = −cos 45°** exactly. Fixed by testing the dimensionless SINE of the turn;
    see the Interop README and design.md for why that constant is not tuned and why the
    facet COUNT (not the fold count) is the oracle that proves it exact — exactly the 10
    rows change, the other 66 stay byte-identical, and no changed row gains or loses a
    facet. Pinned by `ChamferedThreadTests.SubDepthChamfersCarryNoFoldsAtAnyFraction`.
  - [ ] **(b) Clearance profiles in B-Rep** (distance-field offsets round reflex corners —
    needs arc-generator helical bands). Unchanged, and note `SurfaceOffset` does NOT help:
    it keeps each carrier in its own family and has no `HelicalSurface` case, and a
    helical band's offset is a helical band on an offset *generator*, which is what the
    arc-generator work has to build.
  - [ ] **(c) NON-coaxial helical intersections** — helical∩cross-hole-cylinder and
    helical∩tilted-plane. These are genuinely transcendental (no v-linear-in-u substitution
    exists), so they belong to the marching tracer. **The SEEDING half is fixed**
    (`SurfaceIntersection`'s anisotropic second pass — see the BRep README): an M8 crest
    flat cross-drilled Ø6 went from **zero** branches to three, and its flank band from six
    to nineteen, with the whole suite bit-identical because the isotropic pass still runs
    first and `March` dedupes later seeds against traced branches. What remains is the
    STEPPING half, and it is a different mechanism: the branches that are now found still
    stop short of the band's rails, because the tracer breaks its step *after* `Correct`
    leaves the domain (the same fact `SnapTracerEnds` exists to paper over on the boolean
    side). Until a traced curve can terminate exactly on a bounded band's rail, a
    cross-drilled thread cannot be split — so this is now a tracer-termination item, not a
    seeding one.
  - [ ] **(d) Thread runout and cosmetic-thread annotation.** The runout half now has its
    geometry: a coaxial cylinder cuts a helical band in one complete iso-v helix, exactly
    (`SurfaceIntersection`'s coaxial case), which is the runout diameter. The annotation
    half is still cheap — `ThreadCallout` exists.
- [ ] **2D sketch engine residue** (the front door ✅ landed — `Region2d`
  polygon-with-holes with automatic nesting detection, `Region2dBoolean` over
  `Arrangement2d`, `Sketch.ToRegions`, `Profile.FromRegion`; **exact curved 2D
  booleans ✅ landed too** — `CurvedEdge2d`/`CurvedRegion2d`/`CurvedArrangement2d`/
  `CurvedRegion2dBoolean`/`CurvedRegion2dOffset` in Core carry lines and arcs
  unflattened, wired up through `Curve2d.TryToCurvedEdge`, `Profile.FromCurvedRegion`
  and `Sketch.ToCurvedRegions`/`FromCurvedRegion`/`UnionExact`/`OffsetExact`):
  `PolySimplification2`-style Douglas–Peucker simplification (only the exact-collinear
  pass landed). ~~`Region2d` self-intersection validation~~ ✅ **done at `798622a`** —
  `Region2dValidation` finds a PROPER crossing within one loop or between two, exactly via
  `Orient2dSign`, over a `Bvh` above 24 segments, and `Region2d`'s constructors refuse
  rather than producing garbage; `CurvedRegion2d` carries the curved twin over its own
  x-sweep broad phase.
- [ ] **Curved-2D-tier follow-ups** (the lines-and-arcs tier ✅ landed and is
  complete in the sense that matters — its tangent+curvature tie-break is decidable
  for exactly those two shapes; see design.md §5):
  - [ ] **Béziers and general NURBS in the curved arrangement.** They are flattened at
    the entry points today, and the refusal is documented rather than hidden. Making
    them exact needs (a) bezier/anything intersection by subdivision or Bézier clipping
    to a stated tolerance, and (b) a REPLACEMENT for the second-order fan tie-break,
    since two Béziers can agree to second order and separate only in the third
    derivative. A jet comparison of bounded order is not sound in general; the honest
    v2 is probably to compare a small parametric offset off the node and refuse when
    even that ties.
  - ~~**`CurvedRegion2dOffset.Stroke`**~~ ✅ **done** — the open-path stroke of a curved
    chain. The filed framing was right that the primitives existed and slightly wrong about
    where the work was: the both-side join bookkeeping ported verbatim from the polygonal
    twin (the existing `AddCornerJoin` took the two outward normals already, and a stroke
    just calls it twice with them negated), and the real content was the arc SLAB — the
    annular sector between r ± w/2, whose area `sweep·r·w` makes every test an equality
    because the squares cancel, plus its `w/2 ≥ r` degeneration to a pie sector. **One
    contract deliberately differs from the polygonal twin**: a chain that returns to its
    start is stroked as a CIRCUIT (closing joint gets its joins, no caps), because a chain of
    EDGES makes closure structural where a list of POINTS can only spell it by repeating the
    first — read at the same weld tier the chain's own continuity check uses. It is invisible
    under round joins + round caps and is what stops a butt-capped circuit carrying a notch:
    measured, a 10×10 square at width 2 with miter joins comes back 79 through the points
    spelling against 80 through the edge one. The oracle worth keeping is not an area formula
    — stroking a simple closed loop by w is the same SET as `Grow(R, w/2) \ Shrink(R, w/2)`,
    which `Stroke` and `Offset` reach by different primitives. Against the polygonal twin on
    a quarter arc (r 8, w 3): flattening to 4/8/16/32 chords approaches the exact answer
    strictly from below and is still 1e-3 short at 32 — a floor, not a tolerance.
    - [ ] Residual, filed rather than done: a `Sketch`-level wrapper (`Sketch.StrokeExact`,
      beside the existing `OffsetExact`) so a designer reaches it without dropping to Core.
      That is Modeling work, not Core.
    - [ ] Residual: the POLYGONAL `Region2dOffset.Stroke` still cannot recognize a circuit
      (its input genuinely cannot express one unambiguously), so a butt-capped closed
      polyline keeps the notch measured above. Left alone deliberately — it is pinned
      bit-for-bit by `Region2dGoldenTests` and the fix belongs with an explicit `closed:`
      flag, not with a first-point-equals-last-point guess.
  - [ ] **Curved `Shape.Section`/`Silhouette`.** A section of a B-Rep could return a
    `CurvedRegion2d` for the analytic pairs (`PlanarSection` already gets exact circles
    and lines from `SurfaceIntersection`) instead of flattening them; the silhouette
    cannot, since it is a union of projected triangles.
  - [ ] **A curved `Region2dValidation`.** `CurvedRegion2d`'s constructor rejects
    transversal self-crossings (tangential contact is legal, and for lines and arcs a
    tangency is always a touch) but its pairwise sweep is O(n²) with only a box reject
    in front of it, where the polygonal validator has a `Bvh` above 24 segments.
  - [ ] **`ContainedIn` is O(cells × operand edges)** here as well — the curved twin of
    the open item below, and that item's measured verdict applies here first: a
    point-location index was built for the polygonal twin and DECLINED at 1.0× on the
    filed workload, so do not build the curved one without a fixture that provably
    carries the cells × edges product.
- [ ] **Sketch constraint follow-ups** (the variational solver ✅ landed —
  `Sketch.Constrain()`/`ConstrainedSketch`, full coincident/tangent/parallel/dimension
  vocabulary, analytic-Jacobian LM with rank-revealing DOF reports, drawn config as seed
  AND branch selector, refuse-loudly with named contradictions/stationary points):
  ~~elliptical arcs in sketches~~ ✅ **landed** (`Ellipse2d` + `EllipseSeg`,
  `Sketch.Ellipse`, `SketchBuilder.EllipticalArcTo`; exact in all three reps, docs
  `sketching.md`) — what remains OF that item is the constraint side: an elliptical arc
  carries no centre/axis variables, so it rides the chord similarity like a bézier and
  tangency to one is not in the vocabulary; constraint serialization alongside feature
  history (deliberately not v1 — it does not fall out of the `[Param]` descriptor
  pattern); bézier constraints (tangency at bézier endpoints). ~~point-on-arc/curve
  constraint~~ ✅ **landed** as `PointOn(point, line)`/`PointOn(point, arc)` — the CARRIER,
  and both reuse an existing residual rather than adding a spelling of one (point-on-line
  IS the point-to-line dimension at zero, legitimate because that residual is signed;
  point-on-arc IS `ArcEndpointConstraint` with an arbitrary point).
  - [ ] **Point-on-BÉZIER and point-on-ELLIPSE** are the two the vocabulary still lacks,
    and they are a different problem from the two that landed: a line's and a circle's
    carrier have a closed-form signed residual (`d̂ × (p − a)`, `|p − c| − r`), where a
    bézier's or an ellipse's nearest-point is itself a solve, so the residual would need
    its own foot parameter as a VARIABLE — which is the standard treatment and is real
    work rather than a reuse. Filed with the bézier tangency it shares a mechanism with.
- ~~**A lane-wise `SketchRegion` kernel for elliptical arcs.**~~ ✅ **done** —
  `EllipseData`/`EllipseRefine`/`EllipseMinimum` in `SketchRegion.cs`. Measured
  **5.4–6.5×** on the batch entry over two elliptical profiles (one-process A/B over the
  new internal `ellipseKernel` seam). The interesting part is the split: the scalar column,
  which contains no SIMD, carries **4.2–5.6×** of it purely from baking the 65 scan points
  and hoisting the Newton step's cosine/sine pair, while SIMD adds only **1.18–1.24×** on
  top — the scan vectorizes, the refinement cannot, and once the scan is baked the
  refinement is most of what is left. It cannot, because `Vector.Cos`/`Vector.Sin` are not
  bit-identical to the scalar ones (measured: 11 858 / 19 172 of 200 000 differ, one ulp).
  - [ ] **The refinement is still scalar per lane, and only a bit-exact vector
    cosine/sine would change that.** Not worth writing one: a correctly-rounded vector
    `sin`/`cos` is a substantial numerics project, and the measurement above says the
    ceiling it would buy is the ~1.2× the vectorized scan already demonstrates, on the one
    segment kind that is rarest in real sketches. Filed so the next reader does not
    re-derive the arithmetic — the barrier is exactness, not effort.
- ~~**Adopt biarc fits somewhere**~~ ✅ **two of the three doors done at `ffbade2`** —
  `SurfaceIntersection.FitAnalytic(curves, tolerance)` (opt-in post-pass: a tracer polyline
  becomes an arc chain only when the measured deviation clears the caller's tolerance, and
  `AnalyticFit` reports `Fitted` + the deviation either way) and `StepWriter`'s
  `options.ArcFitTolerance`. The filed policy question — *who owns the tolerance* — is
  settled the same way at both doors: the CALLER does, nothing fits implicitly, and the cost
  is always a return value.
  - [ ] What remains of the item is the third candidate, **lighter B-Rep seam edges**, which
    is a different question rather than a third application: a seam edge is shared geometry
    that must WELD, so replacing it with a fit moves both adjacent faces' boundaries and the
    tolerance stops being the caller's to choose.
- ~~**`ExtrudedSurface`/`RevolvedSurface` inverse evaluation refines from a single best
  seed**~~ ✅ **done at `8dc573d`** — `SweptSurface.SolveGeneratorParameter`'s rule was
  extracted as `SeedSelection.MarkCandidates` (BRep, internal) and all THREE swept surfaces
  plus the generic 17×17 base grid now refine from every local minimum and its two
  neighbours. The overrides still defer to the base on failure, so "the override is never
  worse than the base" holds by construction.
- ~~**`Curve3d.ArcLength`/`ParameterAtLength`**~~ ✅ **done at `c28ec5f`** — virtual
  `ArcLength(from, to, relativeTolerance)` with exact closed-form overrides on the conics,
  the helix and both wrappers, a bracketed-Newton `ParameterAtLength`, and a caching
  `ArcLengthTable3d` beside `ArcLengthTable2d`.
- ~~**2D curve ↔ `Sketch` bridge**~~ ✅ **done at `ddd9f06`** — `SketchSegment.ToCurve2d` /
  `Sketch.ToCurves` out, `Sketch.FromCurves` back in (refusing what a sketch cannot hold,
  by name, and handing the result to the ordinary constructor so closure/winding/degeneracy
  stay validated in ONE place), and `Curve2d.ToCurve3d` / `Profile.FromCurves` into
  topology. Written up in design.md §5, "Where the 2D curve family meets the sketch and the
  profile".
- [ ] **Drill follow-ups** (drill-tip angles ✅ landed — `HoleSpec.WithTipAngle`, exact
  as an identity, depth measured to the shoulder; **cross-PLANE hole validation** ✅
  landed — bounding-cylinder separation plus a separating axis, since collinear tools
  bored from opposite faces have zero radial axis distance however much web is left) —
  remaining: hole tables, and thread cosmetics/annotation. Not covered by the
  interference test: `ThreadedHole`'s thread void (its tap-drill pilot goes through
  `Drill` and is), and tools from separate `Shape` branches later unioned.
- [ ] **Ambient occlusion is now the largest single cost of opening a window** (~7–8 s
  of an ~11 s demo launch before lazy tabs; two thread parts alone are 5.7 s and
  already saturate every core, so parallelism has no more to give). The next lever is
  showing the scene flat-lit immediately and streaming occlusion in as bakes finish —
  *not* making the bake less honest.
- [ ] **Do NOT "optimize" `BrepQueries.Bounds`** — it is deliberately conservative-over
  for trimmed fragments (the sphere-piercing fix depends on that), and profiling proved
  it is not a bottleneck: on the worst engraving case only 113 of 894 face pairs survive
  it, and all 113 intersections resolve analytically in ~1 ms. Recorded so nobody
  "fixes" it later.
- [ ] **Boolean/splitting edge cases** (all LOUD rather than silent. The **bounded
  conic-clipping tier** ✅ landed — `TryPatchQuadric` gives a bounded planar carrier the
  same exact curves the main switch gives a real `PlaneSurface`, which took a bore in an
  extruded SIDE wall from a non-converging −7.4e-4 / −5.3e-5 / +4.7e-5 / +6.5e-5 at
  32/64/128/256 segments to 7.1e-14 / 6.8e-14 / 4.3e-14 / −5.3e-14, exact as an identity
  like the cap bore. `CylinderSurface` **constant-v** wrap-split ✅ landed, exact at
  1.7e-15 relative. `CurveSegment`-over-polyline in `SampleEdge` and the `TraceFaces`
  2%/98% probes ✅ landed — both now route through
  `FaceGeometry.ExactSampleParameters`/`IsPolylineBacked`. **Coincident PLANAR faces** ✅
  landed — flush embossing, stacked plates, butted blocks and flush pocket floors all
  fuse into one solid (`CoplanarFaces.cs`, normal-agreement classification; design.md
  §5). Cylinder promotion now requires a WHOLE turn ✅, which fixed near-miss pairs at a
  rounded corner. **Cuts that break out through a face boundary part-way** ✅ landed too,
  as a side effect of snapping traced curve ends — a bore swallowing a rounded corner now
  converges quadratically onto its analytic volume; what still refuses is a tool drilled
  ALONG a band's own axis, filed with the other traced-curve residuals below.) — remaining:
  equal-radius perpendicular cylinders (tangent bicylinder: overlapping v-ranges
  rejected; the tracer's degenerate output there is untested); coincident or tangent
  CURVED faces (a shaft in a bore of its own diameter) — refused BY NAME for coaxial
  equal-radius cylinders. The shared blocker this used to name is now half-gone:
  `SurfaceCorner` re-intersects curved carriers exactly wherever an analytic pair exists,
  and curved shelling and draft ride it. What is left here is the OTHER half — clipping
  the two trims against each other on a curved carrier, a 2D arrangement in the shared
  surface's parameter space rather than a corner solve.
- ~~**Trimmed cylindrical tessellation with WRAPPING loops**~~ ✅ **done** — and the
  standing diagnosis was wrong in an instructive way. The refusal blamed a missing
  capability ("the sub-bands need trimmed cylindrical tessellation with wrapping loops");
  the trimmed path had that capability all along and the split was fine. What was broken
  was `BRepTessellator`'s ROUTING: any two-loop closed-edge cylindrical face went to the
  index-pairing RING path, whose correctness condition — the two polylines sample the
  same azimuths in the same order — two independently traced cuts do not meet.
  `IsRingPairedBand` now checks that condition, cross-drills and tilted-plane cuts of a
  plain cylinder work, and the corpus gained `cross-drilled cylinder band`.
  Residual (pre-existing, now measured): a traced rim's sample count comes from the
  tracer's ARC-LENGTH step, so a *small* band gets few samples per turn — a Ø3 drill
  through a Ø10 cylinder reads facet-vs-surface agreement 0.974 / 0.949 / 0.565 at
  32/96/192 (fold-free, volume converging) against 0.858 / 0.9995 / 0.9998 for a Ø10
  drill through a Ø26 one. Same fixed-sampling floor recorded under the whole-solid
  fillet booleans; a density-aware tracer step would close both.
- [ ] **The tracer reports NOTHING for a conic partially crossing a bounded extrusion's
  edge** — a bore whose rim runs off the side wall it pierces. Pre-existing and separate
  from the bounded-patch tier above, which correctly *defers* that case rather than
  fabricating a whole circle the wall does not carry (pinned by
  `BoreCrossingTheWallsEdge_FabricatesNoCircle`). Clipping the conic to the patch would
  produce arcs whose endpoints must weld to the face boundary — that is the real work.

## Deformation / analysis follow-ups

The foundation ✅ landed (`EngrCAD.Core.Solvers`: `PackedSparseMatrix` /
`SparseSymmetricCG` / `SparseCholesky`; mesh engine: `LaplacianMeshSmoother`,
`LaplacianMeshDeformer`, `MeshLocalParam`, `MeshIsoCurves`, `DijkstraGraphDistance`,
`MeshIcp`). Residuals:

- ~~**AMD/RCM fill-reducing ordering for `SparseCholesky`**~~ ✅ **done** — AMD landed as
  `SparseOrdering.Amd` (opt-in; a permutation changes the summation order, so it is not
  bit-identical to the natural path every upstream number was measured on). 4.6–13.4× on
  factor time, 3.5–8.3× on fill, never a loss; table in the Core README. RCM was not
  implemented and should not be: AMD dominates it on every pattern here, and a second
  ordering is a second thing to keep honest. Residuals worth knowing:
  - [ ] **A supernodal/left-looking numeric factorization** is the next lever, not a
    better ordering. AMD takes 3D 40³ (64k unknowns) from 125 s to 26 s, which is a real
    4.8× and still unusable — the fill is 20.6M entries and the up-looking scalar loop
    touches them one at a time. BLAS-3 dense blocks over the supernodes are the standard
    answer and the only thing that closes that gap.
  - [ ] **Nothing consumes `SparseOrdering.Amd` yet.** `LaplacianMeshSmoother`/
    `LaplacianMeshDeformer`/`MeshIcp` still factor natural, deliberately: their committed
    outputs are pinned bit-for-bit and switching would move them. Whoever wires FEA
    assembly should pass `Amd` from the start and pin its own baselines.
- [ ] **Shape-level exposure of smoothing/deformation** — the tools are kernel-only
  (`EngrCAD.Mesh`); a `Shape.Smoothed(...)` graph node (mesh-Native, implicit-Bridged
  via `MeshSdf`, B-Rep-Impossible — the `Remeshed` precedent) plus docs-site example
  pages is the user-facing follow-up, owed when it lands per the docs rule.
- [ ] **Decal/engraving pipeline over the exp map** — `MeshLocalParam` gives per-vertex
  (u, v); wrapping a `Sketch`/glyph outline through it onto a curved surface (project
  curves into uv, map back, imprint) is the feature it was built to enable.

## Mechanisms (kinematics)

- [ ] **Gear follow-ups** (involute spur/helical landed as `Gears.Spur/SpurGear/
  HelicalGear` — the fit tier was adequate: 16 arcs/flank at module·1e-4, so no new 2D
  curve type; conjugate action is measured from CONTACT via the sketch's exact signed
  distance, because `Coupling.Gear` in the mechanism solver ENFORCES the ratio it
  would be asserting. Since then the **rack** (`Gears.Rack`/`RackBar` — the
  straight-line limit, hence exact, hence no fit deviation to report) and the
  **worm and crossed-helical wheel** (`Gears.Worm`/`WormPair`/`WormWheel` — the worm
  is a thread and rides `MakeThreadedRod`) have landed too):
  - **Backlash allowance** — a circumferential thinning parameter on `GearSpec`
    (thin each tooth by j/2), so a real pair at standard centre distance has running
    clearance; today's teeth are the zero-backlash nominal.
  - **Trochoid root for low tooth counts** — `Gears.Spur` refuses below
    z_min = 2(h_a* − x)/sin²α by name; drawing the actual generated trochoid would
    admit z ≥ ~12 if it can be VERIFIED (the conjugate-contact instrument exists and
    measurably sees a 5e-2 flank error as 5.6e-4 rad of transmission wobble).
  - **Internal gears** — invert the material side and the tip/root roles (the RACK
    half of this item landed as `Gears.Rack`/`RackBar`).
  - **Keyway on the bore** (DIN 6885 parallel key seat as a sketch boolean on the
    blank), set screw boss, web/spoke lightening.
  - **Measurement identities** — span measurement over k teeth W = m·cos α·((k−½)π +
    z·inv α) and measurement over pins, as arithmetic on `GearSpec` plus a
    measured-off-the-sketch check (the tooth-thickness bisection pattern in
    `GearTests` generalizes).
  - **Helical pair conjugate test** — the spur pair's contact instrument is 2D; a
    helical pair adds axial overlap, and the transverse-section argument says the 2D
    test at every section is sufficient — worth asserting once on the twisted mesh.
  - **The full gear taxonomy** (requested 2026-08-02), each with its honest scope:
    - **Herringbone / double-helical** — two opposite-hand helicals in one solid; the
      twisted-extrude machinery does each half today, and the work is the mid-plane
      junction (the apex section is the shared spur profile, so the two twists meet in
      a plane of exact mirror symmetry — a weld by construction, verify by the mirror
      identity). Optional apex gap (real hobbed herringbones relieve the middle).
    - **Straight bevel residuals** (`BevelGears.cs` landed: `BevelPair` +
      `Straight`/`StraightGear`, spiral and hypoid refused by name; see the Modeling
      README for the projection measurements). What is left:
      - **`BevelPair.PhaseFor(member)`** — the pair's TOOTH phasing is not solved, so a
        caller placing two members must phase them by hand (the docs example asserts the
        condition its own counts satisfy: contact at the pinion's 90° azimuth needs
        z₁ % 4 == 0 and z₂ % 4 == 2). The `PlanetarySet` phase solve is the pattern —
        same tooth-opposite-space relation, on the shared cone element instead of a
        line of centres — and the contact instrument to verify it with already exists.
      - **Conical end faces** — the loft's sections must be planar, so the ends are
        planes rather than the back and front cones, which makes the heel section
        deeper than the real tooth (×2.4 at δ = 65°) and is what caps the cone angle
        near 68° with the ISO 53 fillet. Trimming with cones needs a loft∩revolve
        boolean whose curves the tracer must find at tooth scale — measure before
        promising, since that is the aspect ratio the thread work found it fails at.
      - **A full-radius root** where two fillets no longer fit the gap, which is what a
        real deep-tooth gear does and would lift the cone-angle cap without changing
        the flank.
    - **Worm and worm wheel** — the worm IS a thread: `MakeThreadedRod`'s helical
      sweep with a trapezoid (ZA) profile is the exact worm body, one axis-touching
      revolve family this kernel already speaks. The WHEEL is the honest problem: a
      true throated wheel is the envelope of the worm's motion (no closed form —
      that is gashing-and-hobbing kinematics), so v1 is a helical gear at the worm's
      lead angle (the crossed-helical approximation, stated, with its point-contact
      caveat named) and the throated envelope is filed as assessed-not-promised.
    - **Cycloidal profiles** — clock/instrument gears and cycloidal-drive discs: the
      epicycloid/hypocycloid are closed-form parametric curves, so they enter exactly
      as the involute did (fit with reported deviation); BS 978-2 clock-gear
      addenda are a transcription with the verify-against-datasheet flag. The
      cycloidal-drive disc (pin-wheel reducer) is the same curve family offset by the
      roller radius — the cam roller-follower machinery already owns that offset.
    - **Planetary residuals** (`PlanetaryGears.cs` landed: `PlanetarySet` with the three
      conditions, the boolean-free internal ring, solved phases verified from contact,
      and Willis emergent over composed `Coupling.Gear`s). What is left:
      - **Internal-mesh interference** — tip, involute and trimming interference are
        genuinely different conditions from an external pair's and none is checked; the
        undercut refusal that fires today is `Gears.Spur`'s applied to the CUTTER, which
        is conservative for a ring and is not the same question.
      - **An internal root fillet** — the ring's tooth roots are sharp (its tips carry
        the cutter's root fillet instead), which is a stress raiser rather than a
        geometric fault.
      - **Unequally spaced planets**, which have their own weaker assembly condition,
        and **profile-shifted sets** (a shifted sun/planet pair changes the operating
        centre distance, so `z_ring = z_sun + 2·z_planet` stops being the coaxiality
        statement and the derived ring count would silently be wrong).
      - **A compound/Ravigneaux vocabulary** — stepped planets and shared carriers are
        more couplings over the same joints, so the mechanism side likely needs nothing
        new; it is the placement and the assembly conditions that generalize.
    - **Crossed helical (screw) gears** — the geometry already exists (two helicals
      at skew shafts); what is missing is only the pairing arithmetic (shaft angle =
      β₁+β₂, matching normal modules) — arithmetic on `GearSpec`, plus the
      point-contact caveat stated.
    - **Non-circular/elliptical gears** — refuse by name for now: conjugacy for a
      stated centre-distance function is an integral condition, a different problem
      from fitting a known curve; file only if a consumer appears.

Mechanisms v1 landed (`Joints.cs`/`Mechanism.cs`/`Couplings.cs`/`HigherPairs.cs`/
`MateSolverRates.cs`/`MotionInterference.cs`; docs `examples/mechanisms.md`): joints as
a vocabulary over mates with DOF asserted against the solver's rank, drivers +
continuation sweeps, named dead centres, analytic velocities/accelerations,
gears/belts/cams, joint limits, interference over the sweep, swept volumes as Shape
nodes, and Grübler/Kutzbach as a cross-check. **Since then**: multiple simultaneous
drivers (`SolveAt`/`Sweep`/`RatesAt` take lists; the multi form IS the implementation
and the single-driver overload is sugar over it; a sweep is a straight line through
driver space so the continuation logic is unchanged; the same coordinate driven twice
is refused by name), `Coupling.RackAndPinion` (a cam pair with a straight law, so it
reads the unwrapped angle and a rack driven through three turns keeps advancing), the
dwell-rise-dwell `CamLaw` catalogue with `Segments`, adaptive `SweptVolume(path,
maxTravel)` (rigidly interpolated placements bounded by exact bounding-box-corner
travel; 97%+ of the analytic disk from a 9-frame full-turn sweep), and **involute gear
geometry** (`Gears.cs`: `GearSpec` + `Gears.Spur/SpurGear/HelicalGear`, biarc-fitted
flanks with the deviation reported, undercut/pointed/fillet refusals by name,
conjugate action verified from contact — see the gear follow-ups item above).
Remaining follow-ups:

- [ ] **B-Rep-exact interference volumes** — `CheckInterference`'s opt-in volumes use
  the exact MESH boolean of the meshes that flagged the clash; for B-Rep-backed parts
  a `BrepBoolean.Intersection` of the posed solids would report the exact volume, at
  the cost of a boolean between arbitrarily-rotated solids per range.
- [ ] **Flexible sub-assemblies in mechanisms** — inherited from the mates layer: a
  deep occurrence whose owning sub-assembly is placed more than once is refused (one
  shared frame). A mechanism inside a twice-placed sub-assembly needs per-placement
  frame overlays first. See the assessment under "Assemblies follow-ups".
- [ ] **Deliberately out of scope**: forces, masses, friction, contact dynamics.
  That is multibody *dynamics* and belongs with Simulation below — mechanisms answer
  "where does it go", not "what does it take". Mass properties already exist
  (`MeshMassProperties`/`BrepMassProperties` return inertia tensors about the centre of
  mass), so dynamics has its inputs waiting whenever it comes.

## Animation and motion export (follow-ups)

The v1 landed (`Animation`/tracks/`AnimationPlayback` in Viewer.Core, APNG + GIF +
frame-sequence export in Viewer, the SceneHost transport, DocsGen `animate:` fences +
`docs/examples/animation.md`) — the load-bearing rule held: an animation moves poses
and the camera only, `Animation.At(t)` is pure, and one evaluation path serves
scrubbing, playback, export and docs. **Batched export + stills landed** since:
`OffscreenRenderer.RenderSequence` holds ONE EGL context, one set of programs and one
set of uploaded buffers for a whole clip (24 frames at 480x360, win-x64: **1069 ms ->
165 ms, 6.5x**, with the batched pixels asserted byte-identical to one `Render` per
frame), and `EngrCad.RenderToImage(scene, animation, t, ...)` + the MCP `screenshot`
`t` parameter both pose through the one `EngrCad.PoseAt` seam. **`DeformationTrack`
landed** too, and the interesting part is that it did not weaken the rule: a deformed
result looked like the exception (new vertex positions per frame) and became a THIRD kind
of answer from `Animation.At` — a scalar — because the displacement now rides as a vertex
attribute and the whole clip is one uniform per frame (design.md §6b). What remains:

- [ ] **Web viewport transport** — the whole machine (`Animation`, `AnimationPlayback`)
  is UI-free in `Viewer.Core` precisely so the Blazor viewport can reuse it: a
  play/pause/scrub row driving the same per-instance matrices the desktop sends via
  `SetInstancePoses`. Needs only widgets and a `requestAnimationFrame` clock; no new
  evaluation code.
- [ ] **Pose-track composition** — an `Animation` deliberately takes at most ONE pose
  track (two full-instance-list producers cannot compose; whose matrices win?).
  Composing *relative displacement* tracks (mechanism pose ∘ explode displacement on
  top) is the principled extension — displacements compose where absolute pose lists do
  not. **Assessed while the explode PATH landed; the shape and the one hard part.** The
  extension is a `DisplacementTrack` returning a per-instance DELTA (matched by
  occurrence path, like everything else here) that `Animation.At` post-multiplies onto
  whatever the pose track produced, with N of them allowed because deltas compose. Two
  of the three current tracks convert cleanly — `ExplodeTrack` already computes a
  displacement per occurrence (`Occurrence.ExplodeDisplacement`) and merely adds it to a
  frame, so it *is* a displacement track wearing an absolute-pose interface. The hard
  one is `MechanismTrack`: its "delta" is only meaningful against the assembled pose it
  was swept from, so composing an explode on top of a running mechanism displaces parts
  along axes the mechanism has already rotated — which is either exactly right (the
  exploded view of a posed mechanism) or exactly wrong (the offsets were designed in the
  assembled configuration), and the answer is a product decision, not a derivation. Do
  not build it until a concrete clip needs it and can settle that question; the honest
  interim is that `ExplodeTrack.Stagger` already sequences within one track, which is
  what most assembly animations actually want.
- [x] **Explode motion along the explode PATH** — `Occurrence.ExplodePath` carries dogleg
  waypoints (out, then over), with the factor mapped to ARC LENGTH so a part crosses the
  corner at constant speed; `ExplodeDisplacement(factor)` is the one rule the flatten
  walk, `ExplodeTrack` and a future explode-path renderer all read. Paths persist in the
  document format and are written only when set, so existing files stay byte-identical.
  The renderer half (the dashed leader lines drafting standards draw between an exploded
  part and its seat) is still open under Assemblies follow-ups — and now has a path to
  draw rather than a straight line to assume.
- [ ] **WebP animation** needs a VP8/VP8L encoder — not something to hand-roll; it
  means taking a dependency (libwebp or a managed port). Worth it only if the payload
  difference matters for the docs site (the committed APNGs are the size pressure to
  watch).

## Simulation

- [ ] **Variable-amplitude SAFETY factor over rainflow damage.** The rainflow path
  (landed: `Rainflow.Count` + `FatigueAnalysis.Evaluate(TransientResults, ...)`)
  publishes damage and life-in-repetitions but deliberately no safety factor: scaling
  the loads scales every counted cycle at once, and under a power-law S-N line the
  damage of a scaled history is not a simple power of the factor once mean corrections
  and the endurance cutoff engage — cycles cross the endurance limit as the factor
  grows, so the factor to a damage target is a bracketed 1-D root find (each probe
  re-scales the counted cycles, no re-solve needed since the stress history is linear
  in the load) against a REQUIRED target life in repetitions. The static pair's
  verify-by-applying oracle carries over: scale the history by the found factor and the
  damage must land ON the target.
- [ ] **Log-scale colour mapping residuals** (the LEGEND half ✅ landed: `FieldLegend`
  reads the `log10(…)` units declaration — `TryLogUnits`/`TickMarks` — and prints
  anti-logged decade ticks, end ticks stating the true range, title in the base units
  tagged LOG SCALE; docs `fields.md`/`fea-fatigue.md`, design.md §6b's field-display
  bullet records why the units string and not a boolean is the opt-in). Remaining:
  **(a)** the first-class `FieldDisplay.LogScale` for a field carrying REAL cycles —
  `FieldRange.Normalize` goes logarithmic for the colour mapping itself, the flag
  round-trips write-only-when-set, and the properties-panel min/max readout follows;
  Modeling-owned (`Results.cs` + `DocumentFile.cs`), deliberately not landed from the
  viewer fence. It composes with the landed half: such a display prints the same decade
  ticks. **(b)** **NaN colours as the map's FIRST stop** — `FieldRange.Normalize(NaN)`
  is NaN and `ColorMaps.Sample`'s `!(t > 0)` catches it — so an infinite-life node
  (NaN = "no value" by the VTU convention) paints as the BOTTOM of the ramp,
  indistinguishable from the shortest finite life: the honest render is a distinct
  neutral (grey), which touches `SourceColors` and possibly the legend (a "no value"
  swatch). The fatigue docs page sidesteps it today by plotting an aluminium life
  field, where every node is finite — that choice is documented on the page.
- [ ] **Marin-style correction for knee-less (aluminium) curves.** `WithEnduranceFactor`
  (landed) refuses a curve with no endurance limit by name, because the classical
  construction anchors on the limit; the honest version for aluminium applies the
  factor at a STATED reference life (5e8 is the rotating-beam convention) and re-fits
  through the same 10³ pivot — one more parameter, but it must be required rather than
  defaulted, since the reference life IS the claim being made.

FEA as a first-class citizen of the hybrid kernel: the CAD model (any representation)
feeds the mesher, results feed back into the viewer as fields on the mesh. The mesh
engine's half-edge structure and the implicit engine's SDFs are both real assets here
(SDF-guided sizing fields, inside/outside tests via winding numbers).

**Tet meshing landed** (`EngrCAD.Fea`: `TetMesher`, `TetMesh`, `TetQuality`,
`QuadraticTetMesh`, on Core's new exact `Predicates3d`) — conforming Delaunay with
verified boundary recovery, radius-edge + sizing-field refinement, region ids from
multi-body input, per-facet source-triangle tags, 10-node elements. Residuals below.

- [ ] **HYPOTHESIS to measure: `MaxElementSize` may not bound element size in the
  presence of a fine curved feature.** Found via `docs/examples/fea-structural.md`'s
  `run:fea-error-estimate` snippet, which sat in `TetMesher.Mesh(part.GetMesh(), new
  TetMeshOptions { RefineQuality = true, MaxElementSize = 4.0 })` on
  `Box(60, 20, 8) − Cylinder(4, 40)` long enough to look like a hang from two
  different processes (a DocsGen run killed at 50+ min, a standalone repro killed at
  23+ min). The snippet's own author then let it COMPLETE: **~40 minutes,
  225 083 elements** — against a nominal count of order 900 for h = 4 over that
  ~9 600 mm³ volume, a **250× overshoot**. The suspect (unmeasured, so a hypothesis
  and not a finding): the bore is Ø8 through an 8 mm plate, and the tessellated bore
  wall's facet size — not `MaxElementSize` — appears to drive the refinement. Two
  things follow whatever the cause: a mesher that answers a request for h = 4 with a
  quarter of a million elements should either SAY so (a report field, the
  `RefinementBlockedByFrozenBoundary` convention) or refuse, and 40 silent minutes
  is indistinguishable from a hang from the outside — a `ProgressCancel` seam
  and/or an element-budget refusal would make the difference observable. **The cheap
  experiment that separates the two candidate causes** (bore-wall facet size driving
  refinement, vs `RefineQuality`'s radius-edge target doing it on its own with the
  bore incidental): hold the bore fixed and sweep `MaxElementSize` — if the element
  count barely moves, the size parameter is not in control and the facet-size framing
  is right; if it scales as h⁻³, the hole is a red herring and the 40 minutes is an
  expensive-but-correct mesh nobody asked for. Two runs, and it decides which duty
  above is the real one. (The docs snippet itself is being re-fixtured by its author
  to a cheap mesh — the example is about the error estimate and never needed an
  expensive one.)
- [ ] **Conforming Delaunay for a CURVED non-Delaunay surface triangulation** (what is left of
  the old "boundary recovery on remeshed surfaces" top gap, whose filed diagnosis was measured
  and found wrong in two directions — see design.md §3b and the Fea README table).
  **What was wrong**: a remeshed surface is not the obstacle (a remeshed sphere meshes in
  **zero** recovery rounds at three target edge lengths once the remesh is Delaunay-clean,
  *with one patch per triangle* — the configuration the old entry blamed), and triangle quality
  is not the criterion either (a remeshed box at a **0.145°** worst angle and radius-edge
  **198** meshes; a remeshed sphere at **27.9°** and **1.07** is refused; the structured
  cylinder that recovers in zero rounds has *worse* triangles than the remeshed sphere that
  does not). **What is actually left**: where a surface is flat a patch absorbs any diagonal and
  nothing has to be recovered, but where it is curved every triangle is its own patch and must
  appear verbatim as a Delaunay face, and refinement cannot manufacture that. The fix is the
  textbook one this v1 deliberately skipped — protecting-ball *segment and subfacet
  encroachment* (Shewchuk's CDT construction, or Murphy–Mount–Gable) — which carries a
  termination proof where the budget carries none. Red subdivision is not a weak version of it;
  it is a different thing that provably cannot reach it, which is why the practical answer
  today is `RemeshOptions.PreventLongEdgeFlips` (measured to turn every refused sphere row into
  a zero-round one) and the refusal now says so.
  Already landed off this item: non-convergence detection (five rounds without improving on the
  best offending count, the trimmed-face monotone-decrease rule) and a refusal that measures
  the input's worst triangle and its curved fraction instead of advising a remesh.
- [ ] **The remesher makes a cylinder primitive worse, and nothing catches it.** Measured
  across six settings, `Remesher.Remesh` of `MeshPrimitives.Cylinder(10, 20, 48)` lands at a
  worst angle between **0.013° and 7.7°** with a radius-edge ratio between **3.7 and 2124** —
  `PreventLongEdgeFlips` included, so it is not the flip stage. The seed is that the primitive's
  n-gon caps triangulate as a **one-corner fan** (worst angle 3.74° before any remeshing) and
  the rim is pinned, so the remesher has little freedom on the cap and degrades it. Two
  separable pieces: the cap fan is a poor triangulation for anything downstream to start from
  (a fan is the cheapest correct answer, not a good one), and the remesher still has no
  shape-quality measure of its own to notice — which is the already-filed
  `RemeshResult` minimum-angle item, here with a fixture that motivates it. Note this is an
  `EngrCAD.Mesh` item, not an Fea one; it surfaced because the tet mesher is the first consumer
  that cannot tolerate it.
- [x] ~~**Sliver removal (the second named gap in tet meshing).**~~ ✅ **done** —
  `TetSmoothing.Smooth` is the optimization-based half (interior-vertex smoothing against a
  worst-incident-dihedral objective, boundary and deliberate anisotropy frozen, exact
  orientation predicate on every candidate). Measured on a 20³ box: **every sliver removed** at
  three sizes (190 → 0, 399 → 0, 1 149 → 0), worst dihedral 0.00° → 10–17°, volume drift
  7.8e-15 … 2.1e-14 — with the residual **coordinate-sensitive** (the same box translated to
  the origin leaves 2 of 190), so the guarantee is determinism rather than sliver-freeness.
  Residuals filed below.
- [ ] **Sliver exudation, and the topological half of `Stellar`.** `TetSmoothing` moves points
  only, which is what lets it keep the boundary, the volume identity, the connectivity and the
  orientation invariant by construction. The stronger techniques change TOPOLOGY — Cheng et
  al.'s weighted-Delaunay perturbation, and Klingner–Shewchuk's edge/face removal and 2-3/3-2
  flips — and would need the boundary contract, the classification and the region ids
  re-established afterwards rather than inherited. Worth it if a fixture ever appears that
  smoothing cannot clear; nothing in the suite currently is (every fixture reaches zero
  slivers), which is the honest reason not to build it yet.
- [ ] **`TetSmoothing` costs ~10x the meshing it follows** — 4.9 s against 504 ms on the
  40 593-element box, 12.9 s at 103 103. The profile is not measured yet, but the shape is
  obvious: 10 search directions × 8 stride halvings × every incident element's six dihedrals,
  per vertex per pass. Cheap levers in likely order: stop a vertex's search as soon as a pass
  finds no improving direction at the FIRST stride (most vertices after pass 1), cache each
  element's dihedrals and invalidate only the incident ones, and hoist the `TargetDihedralDegrees`
  gate to skip whole passes once the mesh is clean. A parallel pass is NOT free here — vertices
  share elements, so a block decomposition would change the visit order and with it the answer,
  and bit-identical output is asserted.
- [ ] **`TetSmoothing` lowers the MEAN minimum dihedral by 2–4° while raising the worst.** The
  objective is the worst incident angle, so lifting it moves a vertex away from what its other
  elements would have preferred (measured 42.4° → 39.5°, 44.4° → 41.2°, 43.6° → 39.9°). That is
  the right trade for conditioning and it is reported rather than buried, but a combined
  objective — maximize the worst, break ties on the mean — would plausibly keep both. Needs a
  measurement before it is worth the extra knob.
- [ ] **Tet meshing performance.** Measured 31k–80k tets/s (win-x64, Release), which is
  usable but well off TetGen. The profile is Delaunay build + per-pass classification;
  the obvious lever is replacing the winding-number classification inside the
  refinement loop with a flood fill over element adjacency once the boundary is known
  (winding numbers only for the initial seed). Also: `SurfacePatches`/`ClaimFaces`
  rebuild per round rather than incrementally, and `BuildEncroachmentIndex` rebuilds a
  BVH per refinement pass.
- [ ] **Boundary-layer residuals** (the layer itself landed — `BoundaryLayerSpec`,
  graded stack marched inward from a `Facets`-selected wall, prisms split by
  Dompierre's index rule, interface read back off the fill; see the Fea README and
  design.md §3b). What it does NOT give:
  - **Uniform thickness only.** No per-facet or per-tag thickness law, and no
    `cos`-correction at a convex corner: the march measures its thickness ALONG its own
    direction, so a box corner's perpendicular stand-off is `cos` of half the corner
    angle (0.577) rather than the requested value. `MinMarchClearance` reports it. A
    per-node correction is the obvious next step and is a local change.
  - **The wall triangulation is not refined for you**, and because the interface is
    frozen this is what sets the in-plane element size — measured, a plain
    two-triangles-per-face box gets no core refinement at all and reports 72 declined
    points. Auto-refining a PLANAR wall patch (and its columns) before marching is safe
    and would remove the sharpest edge of the limitation; a curved wall's midpoint is
    not on the surface, so that half needs the surface, not just the mesh.
  - **A rim may only stop on FLAT faces.** More than two distinct non-wall plane normals
    at a rim vertex is refused by name. Sliding a rim along a CURVED neighbour needs a
    projection onto that surface, which the mesher has no handle on today.
  - **Concave corners stretch rather than fan.** A single marched node at a reentrant
    corner produces a sharp wedge in the offset surface; a peanut (two unioned spheres)
    and a dumbbell prism both fail recovery on the resulting slivers. Multiple normals
    per node (the standard fix) is a real feature, not a tweak.
  - **The self-intersection net is unreachable from real bodies** — see design.md §3b for
    why that is structural rather than lucky. If a shape ever reaches it, that is the
    fixture the backstop has been waiting for.
- [ ] **Tet meshing breadth**: hex-dominant or voxel/SDF-based meshing (cut cells from
  `Sdf.Sampled` grids) as an alternative route; *curved* (iso-parametric) quadratic
  elements, whose
  mid-edge nodes would sit on the true surface rather than at edge midpoints — note
  that this needs a decision about what a shared node means where two boundary patches
  meet at an angle, which is exactly why the current layer is deliberately
  straight-sided; and coincident interfaces between bodies (v1 meshes disjoint bodies
  only, and refuses overlapping ones by name).
- [ ] **Feed the mesher from the model, not just from a mesh.** `TetMesher` takes a
  `HalfEdgeMesh`, so B-Rep face identity reaches it only if the caller threads a
  per-triangle tag array through. `BRepTessellator` knows the provenance; exposing it
  (a per-triangle source-face array beside the mesh) would make
  `TetMeshOptions.FacetTags` populate itself and let boundary conditions be attached
  with the `BrepQueries`/`FaceRef` selector vocabulary instead of by hand.
**Structural (linear static) landed** (`Material`/`Materials`, `AnalysisMesh`,
`StructuralModel`/`Facets`/`Dof`, `StructuralSolver`/`FeaSolveReport`,
`StructuralResults`, `TetElement`/`TetQuadrature`; docs `examples/fea-structural.md`) —
4- and 10-node tets, AMD-ordered sparse Cholesky or CG, facet-selector boundary
conditions, volume-weighted nodal stress, `MeshField` publishing with a display-mesh
sampling step, `.vtu` export. Verified: patch tests exact to 1e-13, manufactured-solution
orders 2.00/1.00 (linear) and 3.03/2.02 (quadratic), cantilever within 0.01% of
Euler-Bernoulli, Kirsch/Howland within 0.44%. Residuals below.

**Modal analysis landed** (`ModalSolver`/`ModalSolveOptions`/`MassLumping`/`ModalSolveReport`,
`ModalResults`/`VibrationMode`/`RigidBodyMode`, `LanczosEigen`, `RigidBodyModes` shared with
the static solver, `TetElement.ConsistentMass` shared with `ThermalElement.Capacity`; docs
`examples/fea-modal.md`) — shift-and-invert Lanczos with deflation, locking and restarts over
ONE factorization; consistent and HRZ mass (row-sum refused by name for 10-node elements);
rigid-body modes separated rather than refused. Verified: axial bar within 0.021% with
convergence orders 2.00/4.12 against theory 2/4, cantilever −0.07% from Euler-Bernoulli with
the shear gap growing by mode, simply-supported within 0.62% of Timoshenko, free-free rigid
modes at 2.4e-12 of the first elastic eigenvalue, orthogonality at 7.1e-15/5.8e-13, effective
mass 61.09% against the classical 0.6132. Residuals below.

**Buckling, stress stiffening, damping and frequency response landed**
(`BucklingSolver`/`BucklingResults`, `TetElement.GeometricStiffness`/`TetQuadrature.ForGeometric`,
`FeaAssembly` shared with the modal solver, `ModalSolveOptions.Prestress`, `RayleighDamping`/
`ModalDamping`, `HarmonicSolver`/`HarmonicResponse`/`HarmonicSweep`; docs
`examples/fea-buckling.md`) — the filed note that the shift logic needed revisiting was right,
and the answer turned out to be that the shift is not the free parameter at all: `A^-1 B` is
self-adjoint in BOTH the A and the B inner product, so the iteration runs in the K inner
product with operator `K^-1(-Kg)`, factorizes K itself, and needs NO shift because the
substitution `theta = 1/lambda` has already made the wanted eigenvalue extreme. `LanczosEigen`
took exactly one generalization for it (the metric became a parameter separate from the
right-hand matrix) and the modal path is unchanged to the bit. Verified: all four Euler end
conditions within 0.05–0.70% of the shear-corrected load, refinement monotone from above,
`omega²(P)/omega²(0) = 1 + P/P_cr` to 7.4e-10, resonant amplification 25.006 against 25.000,
half-power bandwidth within 0.54%, the static correction exact to 1.8e-16. Residuals below.

- [ ] **FEA: non-proportional damping — the quadratic eigenproblem.** A discrete dashpot, two
  materials with different loss factors in one model, viscoelasticity or hysteretic damping all
  leave `phi' C phi` with off-diagonal terms, at which point the damped modes are no longer the
  undamped ones and `(lambda²M + lambda·C + K)phi = 0` needs a `2n` state-space linearization
  in a NON-SYMMETRIC matrix pair — so neither `SparseCholesky` nor `LanczosEigen` applies and
  the modes come out complex. Scoped separately on purpose; `RayleighDamping`'s docs say
  precisely what is and is not covered. Note the steady-state RESPONSE under such damping no
  longer waits on this: `DirectHarmonicSolver` factors the full complex system per frequency
  with the model's own damping assembled — what remains open here is the damped NATURAL MODES.
- [ ] **FEA: transient integration of model-carried damping.** `StructuralModel` now carries a
  damping vocabulary (`SetDamping` per region, `Dashpot`) that only `DirectHarmonicSolver`
  consumes; `TransientSolver` REFUSES a model carrying it rather than silently ignoring it.
  Landing it there is mechanical — the effective stiffness gains `(1+alpha)·a1·C` with C from
  `FeaAssembly.Damping`, and the right-hand-side C·x products become matrix products against
  the assembled C — but it needs its own verification (a dashpot's decay envelope against the
  2-DOF closed form, and the energy-balance identity re-derived with the C term), so it is
  filed rather than bolted on.
- [ ] **FEA: hysteretic (structural) damping for the direct harmonic solve.** A loss factor
  eta enters the steady state as a frequency-INDEPENDENT imaginary stiffness `i·eta·K` (the
  complex modulus), not as `i·omega·C` — at the direct solve's seam that is one more term in
  the imaginary part (`eta_r·K_r` per region beside `omega·C`), and it is the classic direct-
  solve-only damping model. Needs its own 1-DOF closed-form oracle
  (`|u| = f/sqrt((k − omega²m)² + (eta·k)²)`) and a decision about whether `SetDamping` grows
  a loss-factor overload or a separate `SetLossFactor` — the vocabulary should not let one
  region state both without saying what the sum means.
- [ ] **FEA: residual-VECTOR basis augmentation.** `HarmonicSolveOptions.StaticCorrection`
  handles the static part of what truncated modes miss (mode acceleration), which is most of it
  — 3.079% → 1.8e-16 at zero frequency on the cantilever. The remainder wants the static
  response orthogonalised against the kept modes and added to the basis as a pseudo-mode, which
  also improves the response at non-zero frequencies rather than only at DC.
- [ ] **FEA: base-acceleration (support motion) excitation for the harmonic sweep.** The
  central claim was CHECKED and holds: `VibrationMode.ParticipationFactor` is documented and
  computed as `Gamma_d = phi' M iota_d` over the free degrees of freedom, and the modal force
  of a unit base acceleration in RELATIVE coordinates is exactly `-Gamma_d`, so the sweep needs
  a load-vector spelling and no new mathematics. Three things the entry did not say, all of
  which have to be decided rather than discovered:
  - **The answer is RELATIVE displacement, not absolute.** `M u'' + C u' + K u = -M·iota·a_g`
    is written in coordinates measured from the moving support, which is the right quantity for
    STRESS (a rigid ground motion carries none) and the wrong one for a plotted displacement.
    Absolute is relative plus the rigid ground field, so both are available — but a
    `HarmonicResponse` whose displacement silently changed meaning would be the worst outcome,
    so the two must be named apart.
  - **The influence vector is a rigid translation only when every support moves TOGETHER.**
    With supports on different foundations it is the quasi-static response to a unit motion of
    one support group, which is a static solve per group rather than a constant vector. v1
    should take the uniform case and refuse the other by name.
  - **A displacement-stated input scales as omega²**, so the vocabulary has to say whether the
    caller is giving an acceleration, a velocity or a displacement amplitude; naming the method
    after the acceleration and offering the other two as conversions is the honest shape.
- [ ] **FEA: adaptive block shrink on Lanczos QR rank deficiency.** Block Lanczos landed
  (`ModalSolveOptions.BlockSize`/`BucklingSolveOptions.BlockSize`; design.md §3e carries the
  three measured findings) and treats a rank-deficient residual block as a BREAKDOWN — return
  what converged, restart — because restarting is slower and never wrong. The standard
  refinement is to drop the collapsed column and continue with a narrower block, which saves
  the restart's re-convergence; deliberately not built until a fixture wants it, since no
  case in the suite reaches the breakdown path other than by exhausting a small space.
- [ ] **FEA: transient dynamics — several load patterns with independent histories.**
  `TransientSolveOptions.LoadFactor` scales the model's ONE spatial load pattern by one scalar
  law, which covers a step, an impulse, a ramp, a harmonic drive and a measured trace. What it
  cannot express is the archetypal real case of gravity held constant while a shaker runs: that
  needs `f(t) = sum_i g_i(t)·f_i` over a LIST of patterns, and the list has to be proven to
  share one stiffness matrix — which is exactly `StructuralSolver.SolveAll`'s contract, so
  `RequireOneOperator` is the check it would reuse rather than a new one. The shape is a
  `(StructuralModel pattern, Func<double,double> factor)` list with the single-pattern form as
  sugar over it, mirroring `Solve` being `SolveAll([model])[0]`.
- [ ] **FEA: transient dynamics — base excitation (support motion as a history).** A prescribed
  displacement is currently HELD constant for the run and is deliberately not scaled by the load
  factor (a support that has been moved stays moved). A seismic or shaker input needs `u_c(t)`
  with its own `v_c(t)` and `a_c(t)`, which changes the right-hand side from one constant
  correction to a per-step one — `TransientSolver` already forms it as a full-vector product
  against the effective operator for exactly that reason, so it is a change of one line plus the
  vocabulary for stating the history. The alternative formulation (relative coordinates plus a
  `-M·1·a_ground` load) needs no new plumbing at all and is worth measuring against it first.
- [ ] **FEA: transient dynamics — adaptive time stepping.** The step is constant so ONE
  factorization serves the run, which is the whole performance argument. Adaptive stepping
  refactors at every change, so it is only worth it where the response has widely separated
  scales (an impact followed by a ring-down); the honest form is a small set of step sizes with
  a factorization cached per size, not a continuously varying one.
- [ ] **FEA: nonlinear transient (contact, plasticity, large deformation).** Each makes the
  problem a nonlinear solve WRAPPING the linear stepper, with a Newton residual iteration inside
  every step — the stiffness is re-evaluated about the current configuration rather than once
  about the undeformed one. `TransientSolver`'s loop is the inner half of it and its energy
  identity is the natural convergence diagnostic (an energy-balance residual that stops falling
  is a Newton that has stalled). Filed as a different solver, not an option.
- [ ] **FEA: nonlinear buckling (post-buckling and imperfection sensitivity).** The linear
  eigenvalue factor is computed about ONE static state and assumes the prestress scales with
  the load without redistributing. A shell or a thin-walled section can buckle at a fraction of
  that number, and finding out needs an arc-length (Riks) continuation over a geometrically
  nonlinear solve — a different solver, and the honest scope statement is already in
  `BucklingSolver`'s limitations rather than implied away.
- [ ] **FEA: laminate theory and directional failure criteria.** `ElasticLaw` landed
  (orthotropic / transversely isotropic / fully anisotropic, with a material frame; see
  design.md §3h), and it deliberately stops at the constitutive law. Two things a composite
  user reaches for next, neither of which the law can supply. **(a) A LAMINATE is a stack of
  plies at different angles**, and today that is either several mesh regions (which needs a
  ply-thick element through the stack — expensive, and the mesher would have to be told to
  put layers there) or a homogenised law the CALLER computes with classical lamination
  theory. Doing it here means an `A`/`B`/`D` matrix vocabulary and a decision about whether a
  solid element can represent bending-extension coupling at all, which it cannot without
  through-thickness resolution — so the honest first step is a `LaminateStack` that produces
  a homogenised `ElasticLaw` and SAYS what it dropped. **(b) `MaxVonMises` on a composite
  part is a number with no engineering meaning** — a directional material fails by Tsai-Wu,
  Hashin or maximum strain against per-direction allowables, all of which want the stress
  resolved in the MATERIAL frame, which `ElasticLaw` knows and `StructuralResults` does not
  ask it for. That is a post-processing vocabulary (`ElasticLaw.ToMaterialFrame(stress)` plus
  a `FailureCriterion` with its allowables) rather than a solver change, and it is the more
  valuable of the two.
- [ ] **FEA: an anisotropic region's thermal CONDUCTIVITY is still a scalar.** `ElasticLaw`
  carries directional stiffness and directional expansion, but `ThermalElement.Conductivity`
  reads `Material.ThermalConductivity`, so a carbon laminate conducts isotropically in a
  conduction solve while straining orthotropically in a structural one — the two halves of
  one part disagreeing about what it is made of. The fix has the same shape as the elastic
  one and is much smaller (a 3x3 conductivity tensor rotated once at construction, and the
  element integrand becomes `grad N_a · k · grad N_b` instead of `k · grad N_a · grad N_b`),
  but it wants a decision first: a *thermal* law is a different object from an elastic one
  and putting both on `ElasticLaw` would make the name a lie, while a second per-region
  dictionary is a second place a frame can be stated inconsistently. Probably one
  `MaterialLaw` carrying both, with `ElasticLaw` as its elastic half.
- [ ] **FEA: ADAPTIVE refinement, now that there is something to refine against.**
  `StructuralResults.ErrorEstimate` gives a per-element energy-norm error (design.md §3i),
  which is exactly the map an adaptive loop consumes — and the loop itself is the thing
  nobody has built: solve, estimate, choose a target size field from the element errors
  (the standard rule is `h_new = h_old · (target/e_local)^(1/p)` for an equidistributed
  error), re-mesh, repeat until the global figure is under a stated tolerance. Two things
  make it more than a for-loop here. **(a) `TetMesher` re-meshes from the SURFACE**, so
  every pass throws away the volume mesh and its boundary recovery rather than refining in
  place; a `SizingField` from the previous pass's errors is the cheap route and it needs the
  errors mapped from the old elements onto a spatial field (an `Sdf`-shaped
  `Func<Vector3d,double>` over a BVH of the old mesh, which is a small piece of work). **(b)
  The stopping rule wants a decision**: a global 5% target is the textbook one, but a model
  with a genuine singularity — a re-entrant corner, a point load — never reaches it and the
  honest loop caps the passes and REPORTS the figure it stalled at rather than refining
  forever. That is the same shape as the boundary-recovery non-convergence detection.
- [ ] **FEA: superconvergent recovery on tetrahedra does not reach full p+1, and the cap is
  unmeasured.** The quadratic recovered rate settles at **2.76** against a theory of 3
  (linear reaches 2.30 against 2), reported honestly rather than rounded up. Two candidate
  causes and no measurement separating them: the boundary FILL extrapolates a patch
  polynomial to nodes outside its own elements, which is second-order accurate in the
  extrapolation distance and could plausibly cost the difference; and the superconvergence
  theory itself is weaker on simplices than on hexahedra, where SPR was developed — on a
  tetrahedral mesh the Gauss points are not the tensor-product Barlow points and the p+1
  claim is asymptotic at best. Separating them is a measurement, not a redesign: run the
  same study on a mesh whose boundary is far from the region being measured (an interior
  sub-domain norm), and if the rate rises to 3 the fill is the cap.
- [ ] **FEA: recovery-based smoothing for a MATERIAL INTERFACE is wrong and is not refused.**
  A recovered field is smooth by construction, so a patch straddling two regions fits one
  polynomial across a genuine stress discontinuity and reports a value that is wrong on both
  sides. Direct averaging has the same defect and the README says so, but recovery makes it
  worse, and the fix is standard and small: assemble patches PER REGION, so a patch never
  spans a material boundary and an interface node gets one value per region it touches. That
  needs a decision about what a single nodal value then means (`NodalStress` is indexed by
  node, so two values per node has nowhere to go) — most likely per-region nodal fields, the
  same shape as the per-region laws.
- [ ] **FEA: the factorization is the wall, and it is Core's to move.** In Release the
  cost is overwhelmingly `SparseCholesky`: at 46 800 DOF a linear solve measures 323 ms to
  assemble, **79 009 ms to factor**, 249 ms to substitute and 45 ms to recover stress. The
  up-looking algorithm is unblocked (no supernodes, no BLAS-3 inner kernel), which is fine
  for the mesh Laplacians it was written for and is the binding constraint on FEA scale.
  Measured against it with the two interleaved in one sitting, Jacobi-preconditioned CG
  wins at EVERY size with linear elements and past ~15 000 unknowns with quadratic ones —
  2.0x / 3.9x / 15.3x / **48.6x** at 2 160 / 6 552 / 14 688 / 46 800 DOF — the OPPOSITE of
  the Laplacian crossover recorded in CLAUDE.md, as that note itself predicts.
  <br>**ASSESSED, not built — and the assessment says WHICH option, which was not obvious.**
  `SparseCholesky.Analyze` (new, and cheap: 5–520 ms where the factorization is 0.1–134 s)
  reads the three deciding numbers straight off the symbolic pass. On this project's
  cantilever (`FeaBenchmark.WhatWouldMoveTheFactorizationWall`):

  | | free DOF | ordering | factor nnz | updates | longest column | parallel ceiling |
  | --- | ---: | --- | ---: | ---: | ---: | ---: |
  | linear | 14 688 | Natural | 22 701 719 | 1.88e10 | 1 732 | 1.0x |
  | linear | 14 688 | Amd | 8 769 703 | 4.32e9 | 1 702 | 1.6x |
  | linear | 46 800 | Amd | 57 616 104 | 6.42e10 | 3 537 | 1.6x |
  | quadratic | 14 688 | Natural | 96 705 622 | 4.20e11 | 12 956 | 1.0x |
  | quadratic | 14 688 | Amd | 6 498 728 | 2.54e9 | 1 400 | 1.9x |

  - **Tree parallelism is NOT the lever, and the number kills it outright**: the ceiling is
    **1.0x natural and 1.6–1.9x AMD**, with unlimited processors and free synchronisation.
    The reason is structural rather than incidental — in 3D, the top separator's columns are
    a constant fraction of all the work and they form a CHAIN, so a schedule that respects
    the elimination tree has almost nothing to overlap. Do not write a parallel scheduler
    for the up-looking algorithm; it cannot pay.
  - **Blocking IS the lever, and the same table says why**: the longest column is
    1 400–3 748 entries under AMD, so the work that the tree cannot parallelise sits in a
    few nearly-dense columns — which is exactly what a supernodal/multifrontal kernel turns
    into dense BLAS-3, where the parallelism and the vectorisation both live. That is also
    the answer to "why is the tree ceiling so low": the root separator is the work.
  - **A better preconditioner remains unmeasured** and is the other honest option (IC(0),
    or algebraic multigrid, which is what production FEA uses). Note the bar it has to
    clear is not the factorization but CG-with-Jacobi, which already wins by 48.6x at the
    top of the table — the direct path's value there is exactness, not speed.
  - **The by-product is worth more than it looks**: `UpdateCount` predicts factor time at
    **~1.0–1.3 ns per update** across a 15x size range and both element orders (idle
    machine; the constant is machine-specific and spreads on a loaded one). So a caller can
    be told what a factorization will cost BEFORE paying for it, which is the other half of
    what `FeaSolveReport.Advisory` could not do.
  <br>Note for whoever takes this: **benchmark in Release**. The same runs in Debug look
  assembly-dominated (1 822 ms assemble against 657 ms factor on the docs bracket) and
  would send the work into the wrong phase entirely. And **an idle machine**: the same
  Release binary measured the 46 800-DOF factor at 79.0 s idle and 134.4 s with other work
  on the box, so absolute times are only comparable within one sitting.
- [ ] **FEA: should `Direct` still be the default, and what would settle it.** It is the
  default today for EXACTNESS, not speed — the verification claims (patch test to
  round-off, strain at 1e-13, the two orders agreeing on strain energy to twelve digits)
  are not statements one can make about an iterative solve stopped at a relative residual,
  so a CG default would make every headline accuracy claim a statement about an opt-in
  path. That reasoning is written into the enum doc.
  - ✅ **A multi-load-case entry point** — `StructuralSolver.SolveAll` LANDED. The classic
    second argument for a direct solver is now true here rather than filed: N cases pay for
    one factorization and N substitutions, measured **3.5–3.8x for four cases and 6.94x for
    eight** against solving them one at a time, with an extra right-hand side costing
    0.7–27 ms against 34–4 706 ms to factor. It also divides the direct-vs-CG ratio by N,
    since CG reuses nothing, and `FeaSolveReport.Advisory` now says so and falls silent once
    the amortisation has won.
  - [ ] **Evidence that a size-based automatic pick is sound.** Still deliberately not done:
    a crossover measured on one cantilever measures that cantilever's conditioning, and
    baking its threshold into the library default would be exactly the mistake the row above
    documents. Fixtures of genuinely different conditioning (a thin shell-like plate, a
    near-incompressible nu, a graded mesh) would be needed before a threshold means
    anything.
  - [ ] **A reusable factorization as a VALUE**, if a caller ever needs load cases it cannot
    enumerate up front (an optimisation loop, an influence-coefficient sweep, a
    load-stepping scheme). `SolveAll` takes the list eagerly, which is right for the case it
    serves and wrong for a generator; the shape would be a `StructuralOperator` carrying the
    reduced matrix, the DOF map and the factorization, with `Solve(loadCase)` on it. Filed
    rather than built because nothing in the repo generates load cases lazily yet, and an
    object that hands out a live factorization has a lifetime question (it is large, and it
    silently goes stale if the model is edited) that a list does not.
  <br>Mitigated on both sides now: `FeaSolveReport.Advisory` names the slow factorization
  after the fact, and every solve entry point takes a `ProgressCancel` that reaches
  `SparseCholesky.Factorize`'s per-column loop, so the first run can be watched and
  aborted rather than only read about afterwards.
- [ ] **FEA: an optional `ILogger` on the solve — RULED PERMITTED, and the `ProgressCancel`
  prerequisite has now LANDED, so this is a live decision rather than a deferred one.**
  Recorded here so it is not escalated a third time. The rule as it now stands does not say
  "Viewer and Mcp only" — the app-layer work already relaxed it to **"extends inward on a
  weighed per-project basis"**, and `EngrCAD.Interop` and `EngrCAD.BRep` both carry the
  reference (event IDs 80/90). Weighing Fea by that same rule: it is a long-running
  operation with nothing above it to report on its behalf, which is exactly the condition
  the Interop/BRep grants were made for, so it qualifies. Core, Mesh and Implicit stay
  dependency-free: each still has a consumer seam above it, which is the actual test the
  rule applies.
  <br>**What the `ProgressCancel` work leaves for a logger to do, assessed rather than
  assumed.** Cancellation and a live fraction now cover the case that prompted the item (a
  caller who wants to know it has not hung, and to abort). Two genuinely different needs
  survive, and both are narration rather than control: (a) *which phase* a solve is in —
  the fraction says how far the factorization has got but not that assembly finished in
  0.3 s and the factorization is the thing running, which is what a user reads to decide
  whether the mesh is the problem; and (b) an *unattended* run (a CI regression sweep, a
  batch of load cases) where nobody is holding a progress callback and the record has to
  survive the process. Neither is served by a return value, since both are about what
  happened *during* rather than *at the end*. A logger would take event IDs in a new decade
  (100s), Information for phase boundaries with the sizes and the milliseconds, Debug for
  per-element or per-step detail. Still worth pausing on: `FeaSolveReport` already carries
  every number such a log line would print, so the honest scope is timestamps and ordering,
  not new information.
  <br>Original framing, kept because it states the question well:
  The other candidate pre-solve channel, and the precedent is real: Interop and BRep took
  the `Microsoft.Extensions.Logging.Abstractions` reference for exactly this ("the long
  operations accept an optional trailing `ILogger`", event IDs 80/90). What makes it
  Chris's call rather than an implementer's is that CLAUDE.md records the current line as a
  deliberate REVERSAL of the earlier `IEngrCadLog` shim, and records the leaf kernels
  (Core, Mesh, Implicit) as staying dependency-free on the reasoning that everything the
  backlog named was reachable at the Interop/BRep seams. `EngrCAD.Fea` did not exist when
  that was written and has no such seam above it — it is a leaf that is also a long
  operation, which is the case the rule never had to consider.
- [ ] **FEA: assembly parallelisation — MEASURED AND DECLINED, with the numbers, so nobody
  redoes it.** The item read "it is embarrassingly parallel per element with a per-row merge
  at the end", and the loop's shape does look like that. Measuring the phases separately
  (`FeaBenchmark.WhereAssemblyTimeGoes`) says otherwise: **computing the element stiffnesses
  is 6–18% of assembly** and everything else is the scatter into the builder, which is a
  shared write whose ORDER decides the last bits of every summed entry and therefore cannot
  be parallelised without giving up bit-identical output. Assembly is itself ~4% of a
  quadratic direct solve and ~13% of a large iterative one, so a perfect parallelisation of
  the parallelisable half is worth under 2% of a solve.
  <br>The design that WOULD be correct if that share ever grows, recorded so the thinking is
  not lost: per-block `SparseMatrixBuilder`s merged **in block order**, which reproduces the
  serial add sequence exactly (block order = element order, and order is preserved within a
  block), so the packed values stay bit-identical; per-block right-hand-side contributions
  have to be recorded as ordered (index, value) lists and replayed rather than summed into
  dense partial vectors, or the summation order moves. The merge is the cost and it is a
  copy of every entry.
  <br>The item's second half — "the reaction/energy pass recomputes every element stiffness
  a second time" — is true and also not worth fixing: the whole pass measures 5–18 ms
  against hundreds of ms to assemble and seconds to factor, and holding every element
  stiffness would cost 95 MB on the largest linear case to save single-digit milliseconds.
  `FeaSolveReport.ReactionMs` now reports the phase so the decision has a number rather than
  hiding inside "total".
  <br>✅ **What the measurement actually found is fixed**: `SparseMatrixBuilder`'s per-row
  insertion sort was O(k²) at k = 612 raw entries per row, which a 10-node tetrahedral mesh
  reaches routinely and a vertex ring never does. A stable key sort took quadratic packing
  **250 ms → 31 ms** (7.25x on the sort, interleaved against the old code transcribed
  verbatim) and made it linear in the entry count.
  <br>What is left open here is the OTHER half of the scatter: appending ~1.4 M tuples into
  15 000 per-row `List<(int, double)>` measures ~78 ns each, which is allocation churn
  rather than work. A flat append-only `(row, col, value)` array with a stable counting sort
  by row at pack time would remove the per-row lists entirely, is bit-identical by the same
  argument, and is cheaper in memory — but it is a rewrite of the most widely shared class
  in `Core.Solvers` to buy a few percent of a solve, so it wants a consumer that is actually
  assembly-bound first.
- [ ] **FEA: contact, plasticity, large deformation.** Each is a different mathematical
  problem rather than a bigger version of this one (a nonlinear solve wrapping the linear
  one). Modal has landed, so the assembly is now shared by three physics and a fourth
  consumer would be the fifth reason not to fork it.
- [ ] **FEA thermal follow-ups** (v1 ✅ landed — `ThermalModel`/`ThermalSolver`/
  `ThermalResults` + `StructuralModel.ThermalLoad`, docs `examples/fea-thermal.md`):
  - [ ] **Time-varying boundary conditions.** The stepping already carries the previous
    state whole rather than collapsing the prescribed columns (it has to, for the first
    step of a step change), so a per-step prescribed value is one line plus a way to
    express it — a `Func<double, double>` per condition, or a load-step list. Note the
    constant step is what buys the single factorization, so a time-varying *step* is a
    different and much larger change.
  - [ ] **Lumped capacity**, for monotonicity under backward Euler at short steps and for
    a future explicit scheme. Row-sum lumping is unavailable for 10-node elements (it
    gives −V/20 at every corner, a negative capacity), so this means a scaled-diagonal
    (HRZ) scheme — a different approximation with its own error, which is why it is a
    named option rather than a quiet default.
  - [ ] **Temperature-dependent properties and radiation.** Both make conduction
    nonlinear in the unknown, so both are an outer iteration wrapping this solver rather
    than a change to it. Radiation is the more commonly wanted;
    `sigma·epsilon·(T⁴ − Tsurr⁴)` linearised about the current temperature reuses the
    convection matrix's assembly exactly.
  - [ ] **Anisotropic conductivity** — a `k` tensor with a material frame, the thermal
    twin of the orthotropic-elasticity item. `ThermalElement.Conductivity` takes a scalar
    and would need the 3×3 form; everything above it is unchanged.
  - [ ] **Two-way coupling** (deformation feeding back into conduction) is a staggered or
    monolithic solver, not an extension of the one-way path. Filed for completeness; the
    one-way direction covers thermal stress, which is what is usually wanted.
  - [ ] **A transient stores every state's full field**, so a long run at `StoreEvery = 1`
    is O(steps × nodes) doubles. `StoreEvery` is the current answer; a callback per step
    (write to `.vtu` and discard) would let a run of any length stream.
  - [ ] **`Part.Results` has no time axis**, so publishing a transient means choosing one
    state. That is the "time-varying results (a load step / frequency slider driving
    `Part.Results`)" item already filed under results follow-ups; the thermal solver is
    now a second caller wanting it.
- [ ] **Results/fields follow-ups** (v1 ✅ landed — `MeshField`/`FieldRange` +
  `VtuWriter` in EngrCAD.Mesh, `Part.Results`/`FieldDisplay`/`TryResolveFieldDisplay` in
  Modeling, `ColorMaps`/`FieldRendering`/`FieldLegend` in Viewer.Core, drawn in all
  three front ends with `--export .vtu` and `docs/examples/fields.md`):
  - [ ] **Cell-associated fields.** `MeshField` is vertex-only by construction and
    `VtuWriter` writes point data only — a per-ELEMENT result (an element's von Mises,
    a material id) has nowhere to go. Needs an association on the field plus a
    `RenderMesh` face→cell map for display (`SourceVertices`' sibling), and the
    `<CellData>` block, which is ten lines once the association exists.
  - [ ] **A solver's results are on ITS vertex set, not the display mesh.** The seam
    task #27 publishes through requires `field.Count == part.GetMesh().VertexCount`.
    A tet solve's surface vertices need not coincide with the tessellation's, so the
    missing piece is a sampling step (nearest, or barycentric within the surface
    triangle a display vertex lands in) that maps a solution onto the display mesh —
    deliberately NOT guessed at here, since the tet mesher's own vertex conventions
    decide what is cheap.
  - [ ] **Points/wireframe view styles ignore field colour.** The point and line
    programs are flat-colour; a field-coloured part drawn in Points or Wireframe falls
    back to its part colour. The attribute is already uploaded, so this is a per-vertex
    colour varying in those two shaders.
  - [ ] **MCP `export` does not offer `.vtu`.** `EngrCad.Run`'s `--export` does; the
    MCP tool's format switch needs the same case and its description updated.
  - [ ] **One legend per view.** The viewer shows the first visible part's display;
    several parts on genuinely different scales cannot each get a bar. Stacked legends,
    or a scene-level shared range, are the honest options.
  - (A deformed part's missing feature edges, and picking during an animation, moved to
    their own item below now that the deformation rides a uniform.)

- [ ] **Transient thermal playback — DECIDED by measurement, not yet built.** The filed
  question was which of three shapes a colour animation takes, and the entry said decide
  before building; measured (win-x64, Release, best-of-9 after a 1.5 s warm-up budget,
  scalar field on a uv-sphere; "typical" = 12k render verts, "heavy" = 195k; committed
  as `FieldPlaybackBenchmark` in EngrCAD.Viewer.Tests, `ENGRCAD_BENCH`-gated):
  **(a) colours-only rebuild** (`FieldRendering.Colors` for the new step's field, then
  one `glBufferData` of the existing aFieldColor VBO) costs **0.042 / 0.68 ms per
  frame** plus a **140 KB / 2.3 MB** upload; **(c) the existing publish path**
  (`PartUploads.Build All` per step) costs **2.2 / 27.4 ms per frame** — 40–50× more,
  busting a 30 fps budget on the heavy mesh exactly as the entry guessed ("fine for
  scrubbing and not for playback"); **(b) n colour buffers uploaded once** costs
  n × 2.3 MB of GPU memory on the heavy mesh (60 stored steps = 137 MB) plus new
  attribute-selection machinery in three front ends — disqualified as the default by the
  memory alone. **So the design is (a), desktop + offscreen first, and the pieces are
  scoped**: a `FieldSequenceTrack` in Viewer.Core (steps = ordered (field name, real
  seconds) pairs; `t` maps linearly in REAL time with hold-last-step semantics — the
  stored states ARE the answers at their own instants, so holding is honest where
  tweening colours is not), a fourth `Animation` slot whose sample answers a **result
  SELECTION** — which is a real CONTRACT EXTENSION to "matrices, a camera or a scalar"
  and must be stated in design.md §6b's animation section WHEN BUILT, with its cost
  model attached (applying a selection re-uploads one colour buffer; nothing else the
  contract protects moves: instance count/order, meshes, the pick BVH untouched).
  Three design points settled in advance: the **mid-run legend question dissolves under
  the existing one-legend rule** — ONE range for the whole clip (the display's explicit
  `Range`, else the union of the step fields' own ranges), since a legend that rescales
  per frame lies; the **application seam needs no Modeling change** — resolve the
  part's own display once and swap two fields per step
  (`resolved with { Field = stepField, Range = runRange }` over
  `Part.TryResolveFieldDisplay`), with participation = the part carries ALL the track's
  step names (the `PoseByPath` lesson: a track saying nothing about a part leaves it
  alone); and **`OffscreenRenderer.RenderSequence`'s upload-once optimization is
  conditioned on "an animation moves poses"**, which a colour track invalidates — the
  batched exporter must re-upload aFieldColor per frame, the measured cost above, a
  stated price rather than a blocker. Time-scale honesty follows the modal slowdown
  precedent: the legend states the displayed instant (step fields named with their
  times make the existing title do it), and the docs state the slowdown factor.
  Web parity rides `FrameDescription` colour re-upload and should be filed as its own
  rung when the desktop half lands.
  - A **frequency/load-step slider** driving `Part.Results` is the same shape of problem
    and should be scoped with it; result persistence beside
    `FeatureHistory.SaveParameters` is a third neighbour.

- [ ] **A displaced part's feature edges and pick geometry** — the two things a
  `DeformationTrack` deliberately does not move, both filed with their reasons in
  design.md (§6b, "Animating a deformed result").
  - **Feature edges**: a part carrying a displacement draws none at any factor, so a
    deformed plot has no outline. Displacing the exact B-Rep edge samples by the same
    field would restore it — the sampling is the `SourceVertices` question, since an edge
    sample is not a mesh vertex — and the edges could then ride the same attribute path
    (a line program with `aDeformOffset`), which would keep them free during an animation
    rather than merely correct in a still. The **wireframe** view has the same gap and
    predates all of this: `WireframeEdges.Extract` reads the source half-edge mesh, so a
    deformed part in Wireframe has always drawn its undeformed edges while its fills (and
    now its point sprites) move. One line-program attribute closes both.
  - **Picking during an animation**: the pick BVH is built once at the part's own
    `DeformScale`, so a click is exact at factor 1 and off by the difference in
    exaggeration in between. A cheap fix is not obvious — rebuilding a spatial index per
    frame is the cost the design avoids — but a *deformed-ray* trick may exist for small
    displacements, and at minimum the viewer could refuse to hover-highlight while
    playback is running rather than silently answering from stale geometry.

## OpenSCAD feature parity (open items)

What remains from mapping OpenSCAD's feature set against EngrCAD (the covered ground —
primitives, 3D booleans, transforms, linear/rotate extrude + RMF sweep, STEP/STL/OBJ/PNG
export — is recorded in CLAUDE.md):

- [ ] **Text follow-ups** (`Shape.Text` ✅ landed — dependency-free TrueType reader,
  glyphs → exact sketch segments, containment-based counter detection, layout with
  `kern` kerning; **CFF/OpenType-PostScript outlines ✅ landed** — `CffOutlines`, Type 2
  charstrings → cubic `BezierTo`, CID-keyed via FDArray/FDSelect, every `.otf` opens;
  **GPOS kerning ✅ landed** — `GposKerning`, PairPos 1+2 incl. Extension lookups, with
  the spec's GPOS-over-legacy-`kern` precedence; **text on a curve ✅ landed** —
  `Shape.TextOnPath`/`TextOutlines.SketchesOnPath` over a `GlyphPose`, glyphs placed
  rigidly by mapping control points only (exact, because a Bézier is an affine
  combination of them), arc-length spacing, mid-advance anchoring, left-normal "up",
  closed paths wrapping and multi-line refused by name; **vertical alignment ✅ landed** —
  `TextStyle.VerticalAlign`, measured from the font's ascender/descender rather than the
  ink): **variable fonts** (`fvar`/`gvar`, incl. `CFF2` — rejected loudly today);
  **`seac` accent composition** (legacy CFF accents — rejected loudly today, needs
  charset + standard encoding); **`TextFeature`** as a parametric `Feature` (the
  parameter snapshot must cover the font reference).
- [ ] **Text on a path: bent glyphs, and per-glyph rotation control.** Deliberately NOT
  built with the rigid placement (see CLAUDE.md for why bending costs exactness), but two
  real requests remain. (a) An **upright** mode — every glyph translated along the path
  and left un-rotated, the way a row of labels round a bolt circle is usually wanted;
  cheap (it is `GlyphPose.At` at the path point) and the only open question is whether it
  belongs on `TextStyle` or as an argument, since it is a property of the placement rather
  than of the type. (b) **A second line via `Sketch.Offset`** as a convenience overload
  that builds the offset curve itself — currently refused by name so the caller does it,
  which is right while offsetting can self-intersect, but a helper that offsets and
  REPORTS what it got would carry the honest failure.
- [ ] **Heightmap follow-ups** (`surface()` ✅ landed — `Shape.Heightmap` +
  `Heightmap.Mesh/ReadDat/ReadPng`, grayscale-PNG reader dependency-free over BCL
  `ZLibStream`): color-PNG luminance mapping (deliberately not invented silently —
  decide a documented rule first); Adam7 interlaced PNGs; chunk CRC verification
  (currently structural failures only).
- ~~`minkowski()`~~ — resolved as documentation: `docs/examples/implicit.md` maps the
  OpenSCAD recipes onto `Offset` (≡ sphere-Minkowski, exact as a field),
  opening/closing compositions, and the exact B-Rep routes (`RoundEdges`/`Fillet`),
  with convex⊕convex available via `Hull` over translated copies. General
  polyhedron⊕polyhedron is **not planned** (convex decomposition + pairwise sums +
  union — combinatorially explosive, and its engineering uses are better served by
  `Offset` on the exact field).
- [ ] `BrepSolid` one-call transform story (`TransformedCurve` exists; add
  `TransformedSurface` or per-type transforms; `HalfEdgeMesh.Transformed(m)` ✅ landed
  with winding flip)
- [x] ~~mirror B-Rep completion, remaining nodes~~ ✅ **landed in full** — revolve/sweep/
  rim/drill earlier (axis negation `F∘R(d,θ)∘F = R(−F·d, θ)` for revolves, intrinsic RMF
  for sweeps, isometry-commuting surgery for rims/drills), and now `Draft` /
  `Shell(t, openings)` / `RoundEdges` / `Loft` plus the pure taper (which lowers AS a
  two-section loft, so leaving it refused would have been one operation disagreeing with
  itself). Those five needed no identity — each is defined by lengths and angles alone —
  and Draft's pull direction takes its linear image un-negated. Remaining refusal in the
  family, with a real reason: `SheetMetalBody` (an ordered, edge-quoted flange tree would
  need rebuilding the other way round, not re-placing).
- [ ] **2D offset follow-ups** (`Region2dOffset`/`Sketch.Offset` ✅ landed — round/miter/
  chamfer joins, erosion as complement dilation; **open-path stroking ✅ landed** —
  `Region2dOffset.Stroke(path, width, cap, join)`, butt/round/square caps, both-side
  corner joins so reversals get round noses, closed circuits enclose holes; **exact
  curved offsets ✅ landed** — `CurvedRegion2dOffset` keeps arcs as arcs and makes round
  joins true sectors, which retires the inscribed-arc contract rather than honouring it):
  **variable offset along the outline** (per-vertex distances —
  trapezoid slabs + interpolated-radius joins on the same union construction; design
  question: how distances interpolate along an edge, linear-in-arclength being the
  obvious rule). **Scope note:** this is a `EngrCAD.Core.Geometry2` change —
  `Region2dOffset`/`CurvedRegion2dOffset` own the slab-and-join union, and `Sketch.Offset`
  is a thin delegation — so it cannot be built from the Modeling side without a second
  copy of that construction. The curved tier landing has NOT made the item obsolete: it
  retired the inscribed-arc contract (an exactly-offset arc IS the true offset), which is
  about join FIDELITY, while this is about the distance VARYING along the outline.
- [ ] **Twist-extrude follow-ups** (`Shape.Extrude(sketch, height, twist, scale, slices)`
  ✅ landed — taper = B-Rep-Native ruled loft, twist = direct mesh section sweep with
  twist-matched profile subdivision + collinear-chord-zip caps, implicit via mesh SDF):
  tapered sketches WITH holes are B-Rep-Impossible until loft sections support holes
  (same gap as the Loft section); an exact twisted B-Rep surface type would make twist
  Native (big kernel feature, low priority).
- [ ] **Planar-view follow-ups** (`PlanarSection.OfMesh`/`OfSolid`/`SilhouetteOfMesh` +
  `Shape.Section`/`Shape.Silhouette` ✅ landed — both OpenSCAD `projection` modes):
  - [x] ~~**`Region2dBoolean` leaves ~1e-7-area pinholes at near-tangency.**~~ **CLOSED
    with the opposite finding, which is why the note survives the closure.** The
    boolean is correct and the MESH has the hole: 780 of 780 probe points inside the
    64x48 torus's 1.45e-5 side-on hole are covered by ZERO facets, tested with the
    exact `Orient2d` over every triangle. In the band |z| ∈ [r·cos(π/n_minor), r] — the
    minor polygon's scallop, 4.28e-3 deep at n = 48 — the discrete tube only reaches
    that height near its minor-polygon VERTICES, and the major discretization breaks
    that thin band into lenses that need not overlap; the hole measured 1.16e-3 deep, a
    quarter of the scallop. Nor is it systematic: holes at 64x48, none at 32x24, 96x72,
    128x96, 64x96 or 128x48, because whether two lenses overlap is an alignment
    question. The test now asserts the strong form (every hole is uncovered by every
    facet). Residual, if anyone wants the SMOOTH body's silhouette: that is the
    "B-Rep silhouettes" item below, not a boolean fix.
  - [ ] **B-Rep silhouettes** — true silhouette curves on curved surfaces. Today the
    outline is always mesh-derived, so its fidelity is the mesh's however exact the solid.
    Now has a second consumer with a sharper need: `HiddenLineRemoval` draws a smooth
    surface's outline from the display mesh's view-dependent silhouette and labels it
    `EdgeSource.Silhouette` precisely because it is the one part of a drawing that is not
    exact. Exact HLR is blocked on this.
  - [ ] **`OfSolid` on a flush plane** — a plane containing a face or an edge throws
    (that section is an area, not a curve). A proper answer needs coplanar-face handling,
    the same gap as coplanar booleans.
- [ ] `roof()` — straight-skeleton roof over a polygon; low priority
- [ ] **Camera-adaptive tessellation on zoom** — `TessellationQuality` ✅ landed (max
  angle + max chord deviation, per-solid resolution driving mesh AND feature edges);
  the follow-on is re-resolving against the on-screen pixel size of a radius when the
  camera zooms, which needs re-tessellation plumbing in the viewer.
  **Assessed; the criterion is the easy half and the PLUMBING is the item.** Deriving
  the target is one line — a chord deviation of half a device pixel at the current
  camera, i.e. `deviation = 0.5 * worldPerPixel(distance, fov, viewportHeight)` fed to
  the existing `TessellationQuality.MaxChordDeviation` — and `Part.GetMesh(quality)`
  already re-tessellates for a different criterion. What is missing, and what makes this
  a real piece of work rather than a knob:
  (a) **A re-tessellation must not run on the render thread and must not run per frame.**
  It needs `TabMeshLoader`'s generation-token discipline (a zoom that supersedes an
  in-flight re-mesh must not land) plus hysteresis — re-mesh on a factor-of-two change in
  the criterion, not on every wheel notch, or a drag queues dozens of tessellations.
  (b) **Every derived cache keys off the mesh**: feature edges, the pick BVH and the
  ambient-occlusion bake are all per-mesh, so a re-tessellation invalidates three
  expensive things. The AO bake is 12.3 s on the demo scene, which alone rules out
  re-baking per zoom level — the honest v1 keeps the coarse bake and accepts that
  occlusion is one level behind, or caps adaptivity to parts under the AO opt-out
  threshold.
  (c) **`Part.TryGetSolid` is the saving grace**: the B-Rep lowering is cached and
  criterion-independent, so a re-tessellation is the tessellate half only — which is
  what makes this affordable at all (measured elsewhere: lowering dominates a Shape
  part's meshing).
  (d) **The oracle is awkward**: the docs-PNG byte comparison cannot see this (renders
  are one-shot at a fixed camera), so it needs its own test — mesh a large-radius part
  at two camera distances and assert the segment counts differ in the direction the
  criterion predicts, plus that a zoom back out does not *coarsen* below the quality
  floor mid-session (a part that visibly loses detail when you pull back reads as a bug
  even when it is the criterion working).
  Worth about a day and a half. Not blocked on anything; deliberately not started in a
  sweep, because a background re-mesh triggered by camera motion is exactly the kind of
  feature that is fine in every test and janky in the hand.
- [ ] **Debug-modifier follow-ups** (v1 ✅ landed — `Part.Ghost`/`Hidden`/`Isolated`
  + `DebugFilter` shared by window/offscreen/exports/MCP; `#` highlight deliberately
  stays the selection mechanism): web viewport honors Ghost (EffectiveDisplayMode)
  but not yet Hidden/Isolated visibility; tree rows could gray hidden parts.
- [ ] `$t` animation — time-parameterized models; viewer re-tessellates per frame. This
  is the *expensive* cousin of the Animation section above and deliberately separate:
  that one moves poses and the camera only, which is why it can animate with matrices
  alone; this one changes geometry, so every frame pays a full lower + tessellate.
  **Design assessment recorded in design.md** ("$t — assessed and deliberately
  deferred"): shape is `Func<double, Scene>` + offline frame bake, the work is
  prefix/identity caching across frames, and it should be built only when a concrete
  model needs morphing geometry.
- [ ] **DXF/SVG follow-ups** (v1 ✅ landed — `DxfDocument` LINE/ARC/CIRCLE/LWPOLYLINE/TEXT
  with layers and an LTYPE table both ways, exact bulge arcs; `SvgDrawing`
  visible/hidden/section/thin line classes plus sheet-sized output and text over
  Section/Silhouette/Sketch/`DrawingSheet`; hidden-line classification is now COMPUTED
  from the model by `HiddenLineRemoval`; **DXF SPLINE entities ✅ landed** both ways —
  `DxfSpline` + `DxfCurveMode.Spline`, exact cubic round trip, reading narrowed to what
  converts exactly with rational and general B-splines reported by name; **`$INSUNITS`
  ✅ landed** both ways — declared on write, HONOURED (rescaled to mm) on read, `Unitless`
  never scaled). **Two remain, and BOTH turn out to be changes to what the drafting layer
  PRODUCES rather than to how a writer spells it** — which is why neither is the small job
  its one-line description implies:
  - [ ] **MTEXT for multi-line notes.** The filed framing ("a note currently writes one
    TEXT entity per line") is the symptom; the cause is that `SheetAnnotations`'
    `CenteredText` and `SheetNote` **split on `'\n'` inside `Compute()`**, so by the time
    either writer runs there is no multi-line note left — only N single-line `SheetText`s
    at stacked positions. Emitting MTEXT therefore means the content model carrying a note
    as ONE object with its own breaks, after which the SVG writer has to do the stacking
    itself — which breaks the recorded invariant that `ToSvg`/`ToDxf` consume one
    `Compute()` **so the two writers cannot disagree**. Worth doing only alongside a
    decision about where line breaking lives; a `DxfMText` entity read/written in
    isolation is cheap but buys little, since `ToSketches` consumes no text at all.
  - [ ] **SVG hatch as a `<pattern>` fill** rather than one path per hatch line (smaller
    files for a big section). Same shape: `SheetHatch.Fill` returns clipped LINE SEGMENTS,
    and a `<pattern>` needs the cut REGION plus a tile, so the hatch layer's output type
    changes. The anchoring survives either way — `patternUnits="userSpaceOnUse"` is
    origin-anchored exactly as the exact even-odd scan already is — so what is at stake is
    the content model, not the geometry.
- [ ] **DXF SPLINE follow-up: general B-spline decomposition.** Reading converts degree 1
  and cubics ALREADY in Bézier form; a general (uniform, or unevenly-knotted) B-spline is
  reported rather than sampled, which is right but leaves real third-party files on the
  floor. The exact fix is knot insertion to full multiplicity (The NURBS Book A5.6 Bézier
  decomposition) and it belongs in `EngrCAD.BRep` beside `BSplineBasis` — which is already
  public — not in a file reader; the DXF side is then two lines. Rational splines stay
  refused for a different reason: a sketch's `CubicSeg` is polynomial, so exactness would
  need a rational segment type, which is a `Sketch` vocabulary change rather than an
  import one.

## OpenCASCADE (OCCT) feature parity (open items)

- [ ] **Direct editing: offset, move and delete a face on a history-less solid** —
  what makes imported STEP editable. `Shelling.Offset` already offsets EVERY face with
  exact corner re-solves and takes a per-face wall thickness, so offsetting ONE face is
  the same machinery under a selective law; a face MOVE is the offset's rigid cousin;
  delete-face-and-heal is extend-the-neighbours through the same three-plane/Newton
  corner solves. Selection through the existing `FaceSetRef` vocabulary; provenance
  inherits (the six-site rule); refusals BY NAME exactly where the corner machinery
  already refuses (>3-valent vertices, carriers with no same-family offset).
  - Verification: offsetting a box face by d changes the volume by exactly A·d;
    deleting a boss's faces restores the base solid bit-for-bit when the neighbours
    are planar and it does not merely "look removed".
- [ ] **PDF export follow-ups** (the writer landed: `PdfDrawing` +
  `SheetWriter.ToPdf`, byte-fixed-point, twin-decoder-verified — see design.md §6c;
  each item below was declined in v1 with its reason and would be additive):
  - [ ] **Embedded font.** The standard-14 Helvetica over WinAnsi refuses the drafting
    symbols beyond the diameter sign (depth U+21A7, cbore U+2334, csk U+2335) and all
    non-Latin text. The TrueType reader already parses `glyf`; a subset embedder
    (FontFile2 + a CIDFont or a symbolic TrueType with a cmap) is the honest fix and
    removes the ⌀→Ø substitution too. Note the fixed point: an embedded subset must be
    a deterministic function of the used glyph set.
  - [ ] **PDF layers via optional content groups.** SVG and DXF carry the sheet's
    layers; PDF needs /OCProperties + `/OC BDC ... EMC` marked content to give Acrobat
    toggleable layers. Cheap, but every OCG is another object — keep the xref writer's
    object numbering a function of content so the fixed point survives.
  - [ ] **Opt-in Flate compression** for very large sheets (the BCL has `ZLibStream`).
    Declined as default: a sheet's stream is tens of KB and uncompressed ASCII is what
    the docs fence and the committed assertions read directly. If added, note zlib
    output is deterministic for a fixed level/strategy, so the fixed point can hold.
  - [ ] **Loose-profile/sketch PDF export** (`PdfDrawing.Add(Sketch)`): PDF paths are
    lines + cubic Béziers, so circular/elliptical arcs need flattening or the standard
    kappa cubic approximation — either way NOT exact, which is why the overload was
    refused rather than shipped silently lossy. Offer it with a stated tolerance
    parameter, mirroring `DxfCurveMode`'s honesty about what survives.

What remains against the reference B-Rep kernel (covered: primitives,
extrude/revolve/sweep, booleans, rim fillets/chamfers, drilled holes, conics + offset
curves, curve interpolation, projection/extrema, surface intersection, STEP
export+import, volume/area, tessellation — see CLAUDE.md):

- [ ] **Loft follow-ups** (`SolidFactory.Loft` + `Shape.Loft`/`LoftAlong` landed; each
  gap below is rejected by name today — assessed during the Shape wiring, none started):
  - [ ] **Mismatched segment counts** — two exact routes, both compatibility
    *preprocessing* feeding `Loft` unchanged: integer-ratio counts want the coarser
    section's segments split with `CurveSegment` (no geometry moves, correspondence
    stays natural — a square lofting to an octagon splits each side once); single-NURBS
    sections want degree elevation + knot merging (The NURBS Book A5.9; `BSplineBasis`
    is public). Non-integer-ratio chains have no canonical correspondence and should
    stay rejected.
  - [ ] **Holes in sections** — each hole chain lofts as its own inner skin and the caps
    gain hole loops: topology work in `BuildLoftedSolid`, no new surface math.
  - [ ] **Open (uncapped) skins** — structurally blocked: `BrepSolid.Validate` requires
    two-manifold edge use, so this needs a sheet-body concept first, not a loft change.
  - [ ] **Periodic lofts** closing back on the first section — `LoftedSurface` needs a
    periodic knot vector in v plus band topology in BOTH parameters.
  - [ ] **Guide curves / spine** constraining the skin between stations — does NOT fall
    out of the cardinal basis (a guide constrains the blend *between* interpolation
    stations, which the collocation solve never sees); needs a constrained surface fit.
- [ ] Boolean extras: *section* (curve-only result), fuzzy tolerance, modification
  history. Assessed (task #11): **section** is the cheap one — `BrepBoolean` already
  runs per-face-pair `SurfaceIntersection` behind the bounds prefilter, so a
  `Section(a, b)` is that loop with the curves clipped to both faces' trims
  (`FaceGeometry.Contains` at `ExactSampleParameters`) instead of being fed to the
  splitter; the honest hard part is clipping ANALYTIC curves to a trim boundary
  exactly (a tracer polyline clips at vertices, a circle needs its crossing phases
  solved) — without that the section's endpoints are sampling-resolution, which is
  fine for display and wrong for downstream modelling, so the API should say which it
  returns. **Fuzzy tolerance** is NOT a parameter to add but a rewrite of every
  coincidence decision in the splitter (OCCT threads it through BOPAlgo wholesale);
  the existing near-tangency rejections are the honest substitute. **Modification
  history** (which output face came from which input) is cheap to RECORD in
  `BrepBoolean` (fragments know their host face) and belongs with the topological
  naming item below — record at the boolean, resolve at the Shape layer.
- [ ] **Fillet follow-ups** (sharp-corner miters, edge sets, chamfer angles and
  whole-solid `FilletAllEdges` ✅ landed) — all of these are refused loudly today, so
  they are additions, not bug fixes:
  - [ ] **General corner machinery follow-up** — general trihedral corner patches
    ✅ landed (`FilletAllEdges` now rounds tetrahedra and drafted blocks: a trimmed
    spherical-triangle patch whose two pole-tangent arcs are exact meridians, plus a
    dedicated `TriangulatePoleGrid` trimmed tier excluded from midpoint refinement).
    The shared corner machinery ✅ landed too (`SurfaceOffset`/`SurfaceCorner`/
    `ImplicitSurface`/`CarrierBody`), unblocking curved shelling, curved draft and
    variable-radius fillet BANDS. Remaining: the non-conic corner CURVE itself — two
    variable-radius bands meet in a quartic, so a sharp corner under a varying law is
    still refused. `SurfaceCorner.CornerPolicy.AllowTraced` returns one with its
    deviation reported, but nothing in the kernel opts in (design.md §5 records why);
    turning that into a *usable* corner needs the traced curve re-sampled at
    tessellation time against its two exact carriers, which is the same fix the baked
    tracer-polyline residual below wants.
  - [ ] **Partial-run follow-ups** — SETBACK terminations ✅ landed (`Filleting.
    FilletEdges/ChamferEdges` resolve contiguous partial runs; the termination is the
    planar band cross-section perpendicular to the terminal edge, the industry
    default; `Shape.FilletEdges/ChamferEdges` accept them). Remaining, refused by
    name: cliff and vertex-blend terminations (different surfaces), arc-terminal runs
    (the cylindrical neighbour needs periodic re-trimming), mid-EDGE stops (terminate
    at a parameter, not a vertex — needs an edge split first), and variable-setback
    laws on runs.
  - [ ] **Variable-radius fillet follow-ups** — variable-RADIUS fillets ✅ landed
    (`FilletRim`/`FilletEdges` law overloads + `Shape.Fillet(radiusAt, faces)`/
    `FilletEdges(radiusAt, edges)` + `VariableFilletRimFeature`, beside the
    variable-SETBACK chamfers): along a straight run the band is the ruled skin between
    its two end quarter arcs, whose intermediate sections are TRUE circles because equal
    weights make lerping points identical to lerping control points, and its top and
    bottom boundaries are `LoftRailCurve` rails on the band so the grid and the edge
    polylines sample the same points. **Variable laws on partial RUNS ✅ landed**
    (`Filleting.FilletEdges/ChamferEdges` law overloads now resolve runs through
    `RimSurgeon.OpenRun(topLaw, sideRatio)`; the terminations are exact at any law value
    — the band's end cross-section is a planar quarter arc of whatever radius the law
    gives at the stop vertex; kernel corpus member "variable fillet run", analytic
    volume (1 − π/4)·L·(r₀² + r₀r₁ + r₁²)/3 converging at ratio exactly 4.0). Remaining,
    refused by name: a varying radius across a SHARP corner (two variable bands are
    cones that do not circumscribe a common sphere, so they meet in a quartic — a
    constant law across such a corner still works, so the refusal is about the law, and
    on a RUN there is a third way out: stop the run before the corner), and a varying
    law along an ARC or on a full circular rim (a spiral).
  - [ ] **`Shape.FilletEdges(law, edges)`/`ChamferEdges(law, edges)` still resolve
    complete rims only** (Modeling): the scalar edge-set overloads pass `edgeSelector`
    through to `Filleting.FilletEdges(solid, edges, radius)` and so pick up partial
    runs, but the LAW overloads route via `Filleting.RimFacesFor`, which refuses a
    partial selection before the kernel's new law-run path is reached. The fix is the
    scalar overloads' shape: hand the edge selector to
    `Filleting.FilletEdges(solid, edges, law)` (which now resolves rims AND runs) —
    a few lines in `Shape.cs`/`RimShape`, out of the B-Rep agent's fence.
- [ ] **`StepReader`: trim closed NON-circular generators** — circles ✅ landed (meridian
  arcs trim a closed circular revolve generator; congruent translated end arcs trim a
  closed circular extrusion generator; both closed form, so `FilletAllEdges` output now
  round-trips manifold with zero diagnostics). **The recorded residual was wrong in both
  halves, verified by construction**: a partial revolve of a SINGLE closed NURBS profile
  (an elbow with a one-curve tube section) IS exportable — it builds, tessellates and
  writes — and what it hit was not "the honest non-manifold diagnostic" but a SILENT
  full turn: a one-curve profile has no segment junctions, so the sweep traces no
  axis-centered rail arc anywhere, the angle recovery found nothing, and a 1.2 rad elbow
  came back at 2π with zero diagnostics (the tessellator's full-domain gate then refused
  it three stages later). ✅ Landed: `TryAngleFromRotatedCopy` reads the angle in closed
  form as the azimuthal rotation between corresponding samples of the generator and its
  rotated boundary copy (congruence checked in (radius, axial) profile coordinates), and
  the closed-generator diagnostic is exempted for this case since the face genuinely
  covers the whole generator. What REMAINS unreachable is a closed NURBS generator used
  PARTIALLY under a partial sweep with no rims — that would need the projection-style
  trim the old entry described, and nothing exports one (the boolean would have to split
  such a face, and those faces refuse tessellation before any boolean sees them).
- [ ] **Traced-curve residuals after the band-crossing fix** (`SnapTracerEnds` ✅ landed —
  a traced polyline is extended onto the EXACT solution of E(t) = S(u, v) once, on the
  curve object both faces share, and `SplitByCurve`'s interior probe ✅ now takes an exact
  sample instead of a mid-chord midpoint; together they closed the whole-solid-fillet
  band case and, unexpectedly, cuts that break out through a face boundary part-way).
  What is left:
  - [ ] **A tool drilled ALONG a band's own axis** — its intersection with the band runs
    the band's whole LENGTH rather than crossing it. **The recorded symptom is stale**:
    the failure has MOVED from splitting to classification — it no longer throws `Open
    splitting curves must start and end outside the face` but
    `Could not find a probe point on a face fragment` from `BrepBoolean.ProbePoint`,
    which now names the fragment (an `ExtrudedSurface` band fragment, one loop, pulled
    uv u [3.14, 3.92] × v [0.25, 0.75] on the measured case — a lengthwise sliver strip
    whose 12×12 probe grid finds no contained sample). So the splitting half was fixed
    by the SnapTracerEnds era and the remaining defect is a probe/parity one on
    hair-thin lengthwise fragments (pinned by
    `WholeSolidFilletBooleanTests.ToolRunningAlongABandsAxis_StillRefusesLoudly`).
  - [ ] **Tracer polylines now refine at tessellation time — the HOLE-RIM and
    winding-loop half remains.** ✅ Landed: `PolylineCurve3d.Carriers` carries the two
    exact surfaces the tracer marched on (attached in `SurfaceIntersection.March`,
    preserved by `SnapTracerEnds`/`Simplified`/`GeometryTransform`, serialized as two
    optional trailing surface refs in `BrepArchive`), and `BRepTessellator.SampleEdge`
    refines each chord onto the exact intersection (`SurfaceCorner.TrySolvePoint`,
    minimum-norm Newton, weld-tier acceptance, inserts-only so baked vertices pass
    through bit-for-bit) until a chord subtends at most one natural angular step. The
    band-crossing bore went **0.9988/0.9460/0.3229 → 0.9988/0.9999/1.0000** worst
    facet-vs-surface agreement at 32/96/192 — the degradation with density is gone.
    **Scope, measured rather than assumed**: refinement engages only for OPEN traced
    branches whose every use sits in its face's OUTER loop, because the paired
    strip/slab tiers absorb the density while `TriangulateBandWithHoles` measurably
    cannot — refining a plane-cut torus's bore rim (a chain forming a HOLE loop) took
    its band from 0 folds to 3 base folds at 48/24 and an outright refusal at 192/96,
    the recorded narrow-column-at-the-rim residual surfacing as inversions. The open
    item is a row path in the hole/winding tiers that can anchor on a dense rim; when
    it lands, drop the outer-loop clause from `SampleEdge`'s gate and the hole rims
    (torus bore ~0.0198 at 192/96, Ø3-through-Ø10 cylinder band 0.565 at 192) refine
    for free.
- [ ] **Draft follow-ups** (`Draft.Apply` landed with per-face angles in one call, wired
  as `Shape.Draft`; CURVED faces ✅ landed too — a face of revolution about the pull axis
  tapers by rotating its generator in its own half-plane, so a drafted cylinder is
  exactly a cone and a drafted torus band another torus band): curved faces on any OTHER
  axis (their drafted carrier is not a surface of any family this kernel builds); caps
  with holes; a non-planar neutral surface.
- [ ] **Shelling follow-ups** (`Shelling.Offset/Shell` landed with per-face wall
  thickness, wired as `Shape.Shell(t, openings)`; CURVED faces ✅ landed on the shared
  `CarrierBody` rebuild — a cylinder shells to a cup, a cone frustum to a conical cup, a
  sphere offsets to a sphere, a pipe elbow opened at both ends to a genus-1 tube):
  - [ ] **Carriers with no same-family offset** — swept and NURBS surfaces. A sweep's
    parallel surface is not a sweep, so this needs either a fitted offset (with the
    deviation reported, the `AllowTraced` shape of decision) or a new surface type.
  - [ ] **Non-circular curved edges** — the rim rebuild constructs a concentric circle
    and verifies it; a rim that is some other curve falls through to
    `SurfaceCorner.TrySolveCurve`'s exact tier and is refused when that has no analytic
    pair.
  - [ ] **A SEALED shell of a partial revolve** — moving the cap planes cuts the offset
    torus in a quartic rather than a circle, so the concentric hypothesis is genuinely
    false and the refusal is correct. Making it work needs the tier-(c) corner curve.
  - [ ] **Non-concurrent >3-valent vertices** — CONCURRENT ones ✅ landed: four or more
    faces at a vertex is over-determined in general, but a square pyramid's apex has four
    planes that meet in a point by symmetry and offsetting each keeps that true, so the
    case now goes through the least-squares corner solve and is CHECKED rather than
    refused wholesale. What is left is the genuinely non-concurrent corner, where the
    offset opens the vertex into a small FACE — corner-patch construction (the
    `FilletAllEdges` machinery), not a better solve.
  - [ ] **Adjacent openings** — their shared rim has zero width, so the two openings must
    MERGE into one rim loop: a topology pass, not new geometry. Attempted and deliberately
    NOT built during the curved-corner work, because the shape of the fix is not what the
    note above assumed: the two openings lie on different PLANES, so they cannot become one
    face. What is actually needed is for each opening's rim annulus to lose its zero-width
    stretch along the shared edge — the outer edge there IS the inner edge, since neither
    plane moved — which means re-tracing both rim loops rather than merging them. That is a
    loop-surgery pass of the same kind `FaceSplitter` does, and doing it half-way would
    leave a solid that validates and is wrong.
  - [ ] **Global self-intersection detection** — deliberately unchecked, as in OCCT and
    `OffsetCurve3d`.
  The `Shape` route exposes one thickness; per-face thickness and per-face draft angles
  stay kernel-level escape hatches (`Shape.From(...)`) until a selector-to-value
  vocabulary exists at the Shape level.
- [ ] Feature operations (`BRepFeat`): pocket, boss, rib, slot as first-class features
  with faces-to-remove semantics
- [ ] **Shape-healing follow-ups** (curved-edge RE-TRIMMING — FixGaps' parametric mode,
  opt-in `RetrimCurvedEdges` — and `RepairShells` — orientation flood + outward vote +
  shell repartition, on by default — ✅ landed on top of the original six passes):
  - [ ] **Curve re-FITTING for perpendicular gap residuals** — the re-trim removes the
    tangential part of a merge gap; what remains is the vertex's perpendicular distance
    to the curve, reported and never repaired. Closing it means deforming or re-fitting
    the curve (OCCT's geometric FixGaps mode inserts filler segments), a modelling
    operation this healing deliberately refuses; the report IS the per-edge tolerance
    story, chosen over per-entity tolerances every consumer would then have to honour.
  - [ ] **Surface-sampled orientation vote** — the global side of a component is voted
    from the fan volume of its sampled boundary loops, which is exact in sign for
    polyhedra and boolean-style trimmed faces but ~0 for components whose faces are all
    pole-bounded or closed bands (a two-face sphere, a two-band torus): those keep the
    authored side with a note only when the flood already flipped something. A
    surface-domain-sampled flux vote would extend coverage; needs care on trimmed faces
    whose grid covers more than the face.
  - [ ] **Per-face wire winding vs same-sense flag** (OCCT `ShapeFix_Face`) — the flood
    fixes RELATIVE face orientation and the vote the global side, but a single face
    whose wire is wound opposite to its own `IsReversed` flag is internally
    inconsistent in a way only `FaceGeometry.LoopSignedArea` can see.
- [ ] Local operations: split shape by shape, glue faces. Assessed (task #11): *split*
  is the boolean pipeline stopped after face splitting — imprint both solids against
  each other's intersection curves and return all fragments unclassified; the
  machinery exists (`FaceSplitter` + the boolean's per-pair loop), the work is an API
  that returns fragment→host provenance without sealing, which is the modification
  history item again. *Glue* (merge coincident faces of touching solids without a
  boolean) is `ShapeHealing.SewDuplicateEdges` generalized from edges to overlapping
  face REGIONS. The B-Rep boolean now HAS the planar half of that machinery
  (`CoplanarFaces` — same-plane recognition, area-overlap sampling, normal-agreement
  classification), so a planar *glue* is within reach; curved overlaps still are not.
- [ ] Surface interpolation + least-squares approximation (`GeomAPI_PointsToBSpline`
  proper; curve interpolation exists). Assessed (task #11): the interpolation half is
  the tensor-product generalization of `NurbsCurve.InterpolatePoints` — chord-length
  parameters per row/column averaged, then one tridiagonal solve per row and per
  column of the control grid (The NURBS Book A9.1/9.2, global surface interpolation);
  `BSplineBasis` and the curve solver are in place, so this is a well-bounded medium
  item with exact pass-through tests. Least squares wants
  `Core.Solvers.SparseCholesky` over the normal equations — also in place. Nothing
  downstream consumes surface fits yet, which is why it stayed behind the STEP items.
- [ ] Ray-parity B-Rep point classifier (drop the `MeshSdf` bridge in booleans).
  Assessed (task #11): exact ray∩surface exists for planes/quadrics but not for
  trimmed NURBS/swept faces (needs a surface-ray marching with the same rigor as
  `SurfaceIntersection`), and parity through a trimmed face needs the crossing point
  classified against the trim — the pole/parity lessons (`FaceGeometry.Contains`'
  both-directions rule) all apply per ray. The `MeshSdf` probe's known weakness is
  sliver fragments near the surface, which the largest-triangle-centroid rule already
  mitigates; the classifier is worth building only when a boolean failure is traced to
  a probe misclassification the mesh cannot fix, otherwise it is rigor without a
  customer.
- [ ] **Exact-surface mass-property quadrature** (OCCT `BRepGProp` + `GProp_Domain`) —
  mass properties ✅ landed by tessellate-then-sum with Richardson extrapolation (1.9e-7
  relative on a cylinder at default quality). Exact quadrature is worth doing only
  *after* trimmed parameter-space boundaries become exact, since the domain scan is the
  accuracy limit, not the quadrature. Would make analytic primitives exact rather than
  1e-7. **Premise re-verified (OCCT-parity pass): still holds** — trimmed boundaries
  remain chordal AT BOOLEAN TIME (tracer polylines), so an exact domain scan still has
  nothing exact to scan against; what changed is that traced curves now CARRY their
  exact carrier pair (`PolylineCurve3d.Carriers`), so a future exact quadrature could
  refine its own domain boundary against them the way the tessellator now does, rather
  than needing a new mechanism. The tessellate-then-Richardson route also improves for
  free wherever rims refine.
- [x] **`BrepSolid` one-call rigid transform** ✅ landed — `BrepSolid.Transformed(Matrix4d)`
  over the new `GeometryTransform` (per-type surface and curve mapping; the `Clone()` walk
  with geometry moved and provenance inherited). Exact for **proper rigid motions**, which is
  the whole tier: every parameterization here is built from lengths and angles, so an isometry
  lets edge trim domains, seam phases and revolve angles be carried VERBATIM (asserted
  bitwise) instead of re-derived. Curve objects are mapped once and reused so a shared carrier
  stays one object. Verified through the two consumers that can actually see a bad
  re-placement: a posed drilled plate re-tessellates closed at the same discrete volume AND
  survives a second boolean.
  **The filed assessment was wrong in one place and right in another.** It proposed
  "+uniform scale where the type allows" — but the type is not the only party that has to
  allow it: `PolylineCurve3d` is parameterized by cumulative chord length, so scaling its
  points scales its DOMAIN, while a `BrepEdge` stores its trim domain separately and a
  `CurveSegment` stores base parameters in the base's units. Scaling the curve alone
  desynchronizes them silently, and every tracer-produced edge is polyline-backed, so this is
  the common case. Uniform scale is therefore refused for a BOOKKEEPING reason rather than a
  geometric one, and the message says so. It was right that `StepWriter.Simplify` holds
  similar arithmetic — see the residual below.
  Residuals:
  - [ ] **Uniform scale**, per the above. The fix is not in `GeometryTransform` but in
    carrying the factor into every domain that refers to a polyline-backed curve (edge
    domains and `CurveSegment` base parameters). Worth doing when a consumer wants to scale
    an imported body; a design should scale through the `Shape` API, which bakes the factor
    into construction inputs.
  - [ ] **Reflection.** Refused because it is not merely a placement: it reverses orientation,
    so every loop needs re-winding to keep outward normals outward, and the handedness-carrying
    types each need their own rule — `HelicalSurface` (derived: the mirrored band is the same
    surface on frame (L·X, −L·Y, L·Z) with NEGATED pitch and the u parameter negated, so the
    u domain flips, which interacts with `IsFullHelicalBand` and `SampleEdge`'s angular rule)
    and `RevolvedSurface` (the recorded conjugation identity F·Rot(d,φ)·F = Rot(−F·d,φ), i.e.
    the negated transformed axis). `Shape.Mirror` already does all of this correctly one layer
    up by baking the reflection into construction inputs, so this is a convenience rather
    than a capability gap.
  - [ ] **`StepWriter.Simplify` still holds its own wrapper-folding** for Line/Circle/Ellipse/
    NURBS under a `TransformedCurve`. It was deliberately NOT merged into
    `GeometryTransform.Apply`: the two do different jobs (unwrap-and-simplify for export vs
    place), and — the load-bearing part — `Simplify` is handed whatever transform a wrapper
    carries, which may include a uniform scale, where its circle arm keeps `c.Radius` and
    passes scaled axes through. That is only correct for a rigid map, so it is a latent defect
    in `Simplify` rather than a rule to centralize as-is. Merging means first making the shared
    mapper similarity-correct (normalize the axes, scale the radius, branching only when the
    scale differs from 1 so the rigid path stays bit-identical) and then re-checking STEP
    output; worth doing, but as its own change with its own measurement.
- **Per-part material** ✅ landed (`Material`/`Materials`/`ModelUnits`/`PartColor` in
  `EngrCAD.Core`; `Part.Material`/`.Of()`, `Part.MassGrams`/`DisplayMassGrams`,
  `BomLine.Material`/`UnitMassGrams`, `DocumentEdits.SetMaterial`, document persistence,
  properties panel and MCP `describe_part`; docs `examples/materials.md`; design.md §2).
  **The unit is tonne/mm³** — one convention, stated once in `ModelUnits`, with kilograms
  and grams as accessors; the 1000× discrepancy between the FEA catalogue and
  `PartMassProperties`' old kg/mm³ remark is gone. **All four residuals have landed** —
  `HardwareComponent.Material`/`FastenerMaterials` (a BOM of bought-in parts weighs itself;
  the field carries the SUBSTANCE, not the ISO 898-1 property class, and the bearing
  deliberately states nothing because its v1 body omits balls and cage), `AnalysisBody`
  (one list drives `TetMesher.Mesh` and `StructuralModel.For`/`ThermalModel.For`, verified
  on an exactly-analytic series-stiffness bar at nu = 0), the properties-panel material
  dropdown over `ParamEditors.MaterialChoices` on the undo stack, and the fea docs +
  `BrepMassPropertiesTests` unit cross-references. See design.md §3c and §6b. One
  follow-up came out of it:
  - [ ] **Conforming multi-material interfaces in `TetMesher`.** v1 meshes DISJOINT bodies
    and now refuses two that MATE along a face, by name — the natural way to draw a
    bi-material part. Two things stand in the way and both are precisely located.
    (a) Their surfaces share vertices, so the input is not tetrahedralizable without a
    cross-body weld. (b) Welding alone would be *worse than the refusal*:
    `TetMesher.Builder.OffendingFaces` treats every inside-to-inside face as interior, so
    an inter-body face is never recovered onto the input plane and a tetrahedron
    straddling the interface takes ONE region for its whole volume — the material boundary
    becomes a jagged surface of the mesher's choosing rather than the plane that was
    drawn. The fix is to weld across bodies AND make the region-change face a recovery
    target (`label[n] == Inside && region[n] == region[t]` is the interior test, and both
    bodies' interface triangles are already in the same coplanar patch, so `TryOnSurface`
    would succeed). What needs deciding rather than coding: an interface facet is visited
    from BOTH sides, so it would appear twice in the boundary-facet list — a `Facets`
    selector naming it would double-count a pressure, and `TetFacet.SourceTriangle` would
    be ambiguous between two coincident input triangles. Until then, a bonded bi-material
    part is meshed as one surface with one material.
- [ ] **Topological naming residuals** (v1 ✅ landed: `BrepFace.Provenance` +
  `Shape.Tag(name)` + `FaceSetRef.Tagged`/`Within`. Tags survive the whole boolean
  pipeline, `BrepSolid.Clone`, `Drill`, patterns and transforms; the failure is one-sided,
  so a lost tag means fewer faces and never a wrong one — see design.md §6b.
  **The five rebuild sites ✅ landed too**: `Draft` (planar `BuildPrism` + the curved
  carrier path), `CarrierBody.Rebuild`/`Shell`, `Shelling`'s polyhedral `Offset`/`Shell`,
  `Filleting`'s `FilletAllEdges` + rim surgery + `TrimNeighborBand`, and `ShapeHealing`'s
  `WorkFace`. Every one asks the existing `BrepFace.DescendsFrom` rather than restating a
  copy, and `FaceProvenanceTests` measures each by tagging ONE face and asserting where the
  tag landed. **The filed framing had one thing wrong and one thing missing**: the four
  named sites are really SIX derive points, because `Draft` and `Shelling` each have a
  planar path and a curved one and the curved halves share `CarrierBody` — so the shared
  rebuild is where two of the six live and neither `Draft.cs` nor `Shelling.cs` mentions
  provenance at all; and `Shelling`'s `Dictionary<BrepFace,int>` was never needed, since
  every one of these sites already iterates its parent array positionally and the index map
  would have been a second spelling of the loop counter.) What remains:
  - [ ] **EDGE provenance.** Only faces carry tags today. An edge could report the tags of
    its two faces, which is enough for "fillet the edges of the boss" without a new store —
    but the sense in which an edge *belongs* to a step when its two faces disagree wants a
    decision (both? either?) before it is API.
  - [ ] **A tag cannot be attached to an existing `Part`'s geometry after the fact**, only
    written into the graph. A UI that lets a user click a face and name it would need a
    tag-by-selection form, which is a different (and much weaker) guarantee — the tag would
    then be pinned to whatever the query matched at that moment.
- [ ] STEP follow-up residuals (unit scaling, CONICAL/TOROIDAL_SURFACE synthesis, exact
  `TransformedCurve(NurbsCurve)` export, PARABOLA/HYPERBOLA/OFFSET_CURVE_3D mappings and
  `Parabola3d.ToNurbs()` all ✅ landed): import bisections run a fixed 100 iterations
  (exact but wasteful, import-time only); imported PARABOLA/HYPERBOLA consumed OUTSIDE
  an edge (an offset basis, a revolve generator) carry a ±1000 placeholder domain since
  only edge vertices fix the real interval; plane-angle CONVERSION_BASED_UNITs (degree
  files) are not read — sound today because the reader reads no angular quantities, but
  a future entity that does must check; the closed-generator two-rim trim
  disambiguation reads OUR outward-band sense convention, so a foreign face whose wire
  winding contradicts its own same-sense flag could still pick the wrong half (per-face
  winding validation is ShapeFix_Face territory, not started)
- [ ] Data exchange follow-ups (glTF, native BREP and IGES import all ✅ landed; the
  original task-#11 assessment is kept below with each verdict recorded against it).
  **IGES** ✅ **import landed** (`IgesReader` + internal `IgesParser`, docs
  `examples/import.md`), export **filed and refused** — the assessment's "do not write
  it" holds: a writer would be a second, lossier encoding of geometry STEP already
  carries better. Landed entities: 110, 100, 104, 126, 128, 102, 116, 108, 118, 120,
  122, 124, 142, 144. **Residuals, in rough value order**: (a) **186 MSBO + 502 vertex
  list / 504 edge list / 508 loop / 510 face / 514 shell** — the one path that yields a
  SEWN solid rather than a face soup, but it is a second parallel topology encoding
  inside the same format, about as large as the rest of the reader, and it buys
  correctness `ShapeHealing` already supplies; (b) 402 associativity / 308+408
  subfigure instances, i.e. the nearest thing IGES has to assembly structure — worth it
  only if a real file turns up needing it; (c) 106 copious data (the polyline family),
  cheap and common in 2D-ish files; (d) 116-point and loose-curve output is returned but
  nothing consumes it through the `Shape` API yet; (e) the 142 parameter-space curve is
  discarded, since the topology has no pcurve slot — if one is ever added (a real
  change, blast radius across `FaceSplitter`/`BrepArchive`/`StepWriter`/the
  tessellator), IGES is a consumer waiting for it; (f) entity 314 colour and 406
  properties are skipped silently rather than carried onto `Part.Color`.
  **glTF** ✅ **landed** (`GltfWriter` in EngrCAD.Mesh + `GltfScene` in Viewer.Core;
  `.glb`/`.gltf`, `--export` and MCP wiring, docs `examples/exports.md`) — and it went
  further than this assessment expected: glTF has real hierarchy, so it preserves the
  assembly tree with one mesh per distinct `Part` rather than flattening to
  `PartInstance`s. Residual glTF follow-ups: no texture/UV support (nothing produces
  UVs yet); `KHR_materials_*` extensions unused (a metalness/roughness pair per part is
  all `Part` carries); the flat render mesh triples the vertex count against a
  shared-vertex mesh, so a `KHR_draco_mesh_compression` or an indexed-with-smoothing-
  groups path is the size lever if files ever get big; and a deformed `FieldDisplay`
  exports undeformed by design (see the reasoning in `GltfScene`) — a glTF morph target
  is the honest way to carry it, since a target's weight is exactly the exaggeration
  factor the file currently has nowhere to record.
  **Native BREP serialization** ✅ **landed** (`BrepArchive` in EngrCAD.BRep, `.ecb`,
  `--export` wiring, docs `examples/exports.md`, decision recorded in design.md §5). The
  measured need was the second one this assessment named: every modelled thread carries a
  `HelicalSurface`, which STEP has no entity for, so a threaded part had no lossless file
  representation at all. Versioned from day one, unknown versions refused by name. Format
  residuals: no compression (a busy solid is a few hundred KB of text — fine today, and
  the honest lever if it ever is not is gzip around the same bytes rather than a binary
  encoding, since that keeps the diffability the format exists for); no scene/document
  content by design (that is the OCAF envelope item below, which should REFERENCE `.ecb`
  files rather than embed geometry); the reader does not tolerate forward references, so
  a hand-reordered file is refused rather than sorted; and the `Diagnostics` list only
  ever carries unknown-header warnings today — there is no partial-read mode, which is
  the right call for a native format and the wrong one for an import format.
- [ ] **HLR / drawing follow-ups** (v1 ✅ landed — `HiddenLineRemoval` classifying exact
  B-Rep feature edges against the display mesh, `DrawingSheet`/`DrawingView` with
  third/first-angle standard layouts and section views, `SheetAnnotation` dimensions,
  SVG/DXF sheet export; `docs/examples/drawings.md`):
  - [ ] **Exact HLR** (OCCT `HLRBRep`): project edges AND true silhouette curves and
    classify algebraically against every face, instead of ray-casting against the
    display mesh. Blocked on the B-Rep silhouette item in the OpenSCAD section — with
    exact silhouette curves the rest is the same splitting machinery the boolean
    already has. The seam is right today (a list of classified 2D polylines), so this
    is a swap behind `HiddenLineRemoval.Project`, not a rewrite of the sheet layer.
  - [ ] **Auto-dimensioning**: a first pass placing the obvious dimensions (overall
    extents per view, hole diameters and their bolt-circle or grid spacing) from the
    graph's own `DrillShape`/`LocationSet` nodes, the way `HoleTable.For(part)` already
    reads them. Explicit placement stays the contract; this is a starting point, not a
    replacement.
  - [ ] **BOM-linked balloons**: `SheetBalloon` draws a circled string today, and `Bom`
    already numbers distinct parts — connecting them means picking a leader anchor per
    occurrence from the projection (a visible point on that instance's line work) and
    emitting a parts-list table beside the title block.
  - [ ] **More sheet standards**: ISO 5457 border/zone frames with the row/column grid
    and centring marks, the third/first-angle projection SYMBOL as geometry rather than
    the words the title block prints today, an ISO 7200 field layout, and the B-series
    and ANSI A–E paper sizes beside the A series.
  - [ ] **Detail views** (a scaled-up circle of a region) and **broken views** (a long
    part with its middle removed). Both are clipping problems on top of the existing
    view, not new projections.
  - [ ] **Cut-plane indication**: a section view is drawn correctly but nothing marks
    WHERE it was cut on its parent view — the chain-dashed cutting line with its arrows
    and letters. Needs a view-to-view reference, which the sheet model does not have
    yet.
  - [ ] **Corner resolution**: within one bias step of a model vertex the local-surface
    read picks up the far side's faces, so a hidden run stops that far short of its
    corner (measured: three junctions on a box cost 0.037 of an analytic 57.155, i.e.
    one bias each). Absorbed by the minimum-run rule and far below drawing resolution,
    but an exact HLR would not have it at all.
- [ ] **Document framework residuals** (the OCAF assessment's verdict held: do NOT port
  OCAF's label-tree/attribute model; `Scene`/`Tab`/`Part` with `FeatureHistory` as the
  parametric core is the document, and one versioned envelope is the persistence. Landed:
  `Document`/`DocumentFile.cs` — scene structure + per-part feature history + assemblies
  and poses + mates + annotations + results, with snapshots for geometry that has no
  recipe, warnings-not-exceptions on load, and a byte-identical save→load→save fixed
  point; and `DocumentEdits.cs`/`UndoStack.cs` — reversible edits with grouping, the
  serializer as the undo test oracle, and refused edits leaving the document untouched.
  Note the one place the assessment was overruled by building it: undo stores EDITS, not
  document snapshots, because a `Scene` snapshot is not a cheap value — design.md §6b).
  Open:
  - [ ] **`Shape`-graph serialization** would let a code-built `Shape` part save as a
    recipe instead of a snapshot, and would make `BooleanFeature` and `ComponentFeature`
    round-trip through `FeatureHistory` as well. It is the single biggest remaining gap in
    what a document can carry, and it is a real project: the graph has ~40 node types,
    several carrying sketches, fonts, hole specs, catalogue objects and lambdas.
  - [ ] **External geometry references.** Snapshots are embedded on purpose (design.md
    §6b). The case that would justify a `{"kind": "external", "path": ...}` record is a
    scan mesh too large to inline; it needs a resolver hook, path resolution relative to
    the document, and a missing-file policy, so it waits for a real need.
  - [ ] **Selector-backed annotations do not round-trip.** `LinearDimension.BetweenFaces`
    and `RadialDimension.OnEdge` take `Func<BrepSolid, …>` lambdas, so they save as opaque
    markers. The fix is not more serialization machinery but the vocabulary that already
    exists: overloads taking `FaceRef`/`EdgeSetRef`, whose `Descriptor` is the serialized
    form. Small, and it would make dimensions as persistent as features already are.
  - [ ] **Mechanisms in the document envelope.** The mechanism layer itself now
    round-trips (`Mechanism.SaveMechanism`/`LoadMechanism` — joints with saved
    reference directions and unwrap state, couplings by factory args, cam laws per
    `Feature.SaveInputs`, save→load→save a byte fixed point), but a `Document` still
    has no mechanism list to carry one in: a `Mechanism` OWNS its `MateSet` where a
    `Document` owns loose `MateSet`s, so wiring it in means deciding whether a
    document-carried mechanism replaces one of the document's mate sets or sits
    beside them (and what `reload` does to it). The persistence layer is done; the
    open question is the document's ownership model.
  - [ ] **The viewer's undo wiring wants a manual pass.** The stack, the edits and the
    grouping are covered headlessly, and the two edit paths the window offers (the tree's
    suppress toggle, the properties panel's `[Param]` fields) are routed through it — but
    the Ctrl+Z/Ctrl+Y handler and the toolbar buttons themselves are only exercised by
    running the app, since synthetic input does not reach Avalonia's keyboard stack the way
    `SendInput` reaches its pointer stack. Same caveat the RPC window wiring carries.
  - [ ] **The rollback bar is not undoable.** It suppresses a run of features and keeps its
    own per-part bookkeeping of which ones IT suppressed, so folding it into the stack means
    an edit that captures that bookkeeping too — a `CompoundEdit` of `Suppress` edits plus
    the marker state. Worth doing when the bar next gets attention.
  - [ ] **Undo does not reach every mutation yet.** The `DocumentEdits` vocabulary covers
    what a UI performs today; the gaps are deliberate rather than forgotten —
    add/remove a whole `Part` or `Tab` (needs `Tab.Remove`/`Scene.RemoveTab`, and a removed
    part may still be placed by occurrences, so the edit has to decide whether it takes
    them with it), committing a `MateSet.Solve` as one undoable repose of every occurrence
    it moved (mechanically easy — one `CompoundEdit` of `Repose`s — but it wants the solver
    to report which frames it wrote), and `Part.Results`/`FieldDisplay`. None is hard; each
    is a decision about scope rather than about mechanism.

## build123d / CadQuery parity (open items)

- [ ] **Weldment follow-ups** (`Weldment`/`FrameProfile` ✅ landed — skeleton runs,
  exact bisector-plane miters via overlong-extrude + on-plane box tools, butt joints,
  `Part.CutLength` → `BomLine.CutLength` cut lists, prism-cut-identity verification,
  coped saddles refused with the tracer reason; design.md §6b). Open, in rough order
  of value:
  - [ ] **T-joints trimmed to the through member's wall.** v1 refuses an endpoint on
    another run's interior by name; the trim is the SAME plane-cut machinery as the
    butt joint (cut plane = the through member's facing wall), so the work is joint
    DETECTION at a mid-run point plus deciding what the through member does (nothing).
  - [ ] **Mixed profiles per skeleton** (legs SHS, rails angle). The miter still cuts
    both members with one plane; what changes is that the butt wall offset and the
    per-member reach read per-member profiles, and `Weldment.Build` takes a profile
    per run (or a default + overrides).
  - [ ] **Multi-member joints** (three members at a corner): which pair miters and
    what closes the third is a real drafting convention, not a formula — SolidWorks
    asks per-joint trim order. Wants a per-joint override vocabulary, not a guess.
  - [ ] **Coped (saddle) tube joints** stay refused pending the tracer's thread-scale
    seeding limits (the recorded M8 measurement); if `SurfaceIntersection` ever gains
    an analytic or robust cylinder∩cylinder tier at these aspect ratios, the refusal
    on `FrameJointStyle.Cope` names exactly what to relax.
  - [ ] **Corner reliefs / end caps / gussets**, curved members, and profile
    placement offsets (locate the run on a section corner or face rather than the
    factory datum) — all future work, none blocked.

Both are **OCCT front ends**, so unlike the OpenSCAD and OCCT sections above this one is
almost entirely about **API design, not kernel capability** — their contribution is how a
model is *expressed*, and the underlying operations are ones we largely have. Read them
for ergonomics, and copy capability rather than syntax: CadQuery's stringly-typed
selectors (`">Z"`, `"|Z and >Y"`) are the part to learn from and *not* imitate, because
`BrepQueries` + LINQ gives the same power type-safely. Landed from this section:
`BrepSelection` (the ordering/grouping layer + GeometryRef spellings), `LocationSet`,
`ExtrudeUntil`/`CutUntil`, `Packing`, frames & weldments (`Weldment`, docs
`examples/frames.md`), and the builder-form prototype whose verdict (an
honest no) is recorded in design.md §6b with the comparison committed as
`BuilderPrototypeTests`.

- [ ] **Selection-layer follow-ups**: `BrepSelection.Area`'s curved-face quadrature is
  ordering-grade (~1–2%, midpoint grid gated by pulled trim loops) — an exact-surface
  or adaptive tier is possible if a consumer ever needs measurement-grade face areas
  (today that is `BrepMassProperties`' job); `GroupByCoplanar` could gain a
  direction-filtered variant; `SurfaceKind` has no Helical member (helical faces
  report `Swept`).
- [ ] **Location-set follow-ups**: locations are 2D-in-plane by design — a full 3D
  `Frame3d`-list variant (build123d's arbitrary `Locations`) would serve component
  placement on non-parallel faces; `Shape.Pattern` could take a per-location scale law
  the way `LoftAlong` takes laws.
- [ ] **ExtrudeUntil follow-ups**: the resolution is eager (the `Bounds`/`Resized`
  policy) — an `UntilFeature` wrapper would re-measure per regeneration; a conforming
  end face (extrude-until-curved-face, what real CAD does with a trimmed end cap) needs
  kernel work: split the prism's side walls against the target and cap with the
  target's own surface patch.
- [ ] **Packing follow-ups**: rotation (90° first, then free), true-outline nesting
  (the silhouette regions are already computed — only their AABBs are used), and
  multi-plate overflow instead of the loud refusal.
- [ ] **Exporter breadth** — 3MF/AMF/OFF ✅ and DXF/SVG v1 ✅ landed (`ThreeMfWriter`/
  `AmfWriter`/`OffWriter` + `--export`/MCP wiring; `DxfDocument`/`SvgDrawing` with
  build123d's edge-classification line types) and **VTK/VTU** ✅ (`VtuWriter` +
  `--export .vtu`, geometry plus simulation results as point data) and **glTF 2.0** ✅
  (`GltfWriter` + `GltfScene`, `.glb`/`.gltf`, hierarchy-preserving with per-part PBR
  materials and `COLOR_0` result colours); remaining: VRML.
- [ ] **Deliberately NOT taking**: string selectors (type-unsafe, and LINQ is strictly
  better in C#), Python-style implicit "pending" state carried between builder calls
  (hard to reason about and worse without context managers — confirmed by the builder
  prototype, design.md §6b), and the `Workplane` stack's history/rollback semantics
  (our `FeatureHistory` already covers regeneration properly and with typed
  parameters).

## Viewer

- [ ] **3D-annotation (PMI) residuals** (angular dimensions incl. `BetweenFaces`
  included-angle measurement, chain/ordinate styles, multi-line stroke-font layout
  with callout continuation lines, `ToleranceSpec` text sugar, `HoleTable` +
  `HoleAnnotations.AutoAttach`, and pickable annotations ✅ landed):
  - **Occlusion-aware rendering** (v1 is always-on-top with the depth test off;
    depth-tested with a "hidden = dashed/dimmed" pass is the classic upgrade —
    needs a second line batch split by a depth pre-pass or a stippled shader
    variant, so it is real render-path work, not an overlay tweak).
  - **Annotation editing from the viewport** (picking ✅ — selection reports the
    text; dragging a picked dimension's offset would be the next affordance).
  - True leader-less ordinate dimensioning (datum zero point + aligned coordinate
    text per hole, no dimension lines) — `LinearDimension.Ordinate` is the
    baseline/running style.
  - Annotation persistence (JSON alongside `FeatureHistory.SaveParameters`) and
    STEP AP242 PMI export (far future).
- [ ] **Construction-tree residuals** (rollback marker + suppress-from-tree +
  `[Param]` properties-panel editing + preview-restore-by-path + **typed editors**
  ✅ landed — `ParamEditors.KindFor` in Viewer.Core decides checkbox / enum dropdown /
  bounded slider / text from metadata the registry already carried, every editor still
  writing through the one JSON seam, the slider committing on release because each
  application regenerates and is an undo step): the rollback marker is click-to-place
  rather than a literal drag (drag-and-drop in the tree panel would need Avalonia
  pointer capture plumbing for marginal gain). Residual: the browser properties panel is
  read-only, so `ParamEditors` has one consumer — the rule is shared the moment the
  second one wants it.
- [ ] **Ambient-occlusion bake cost — three levers examined, two declined, don't redo
  them.** The bake was 12.3 s on the demo scene and already saturates every core.
  (a) **An any-hit early-out does not exist here**: occlusion accumulates as `1 − t`, so
  it is a NEAREST-hit query and a boolean test is a different renderer (measured 0.055
  darker over the occluded vertices; pinned by
  `Occlusion_AttenuatesWithDISTANCE_NotJustHitOrMiss`). (b) **Nearer-child-first traversal
  landed** — exact, bit-identical, but worth only **1.19× on a gyroid lattice and 1.04×
  (nothing) on a smooth blob**, because an escaping ray never sets the pruning bound below
  1 and most rays escape on ordinary CAD parts. (c) **Fewer rays changes renders**: 16 → 8
  is 1.6–1.9× and moves 59 of the 87 docs PNGs. What is left, in rough order of promise:
  - [ ] **Bake at fewer sample points and interpolate** — the cost is linear in vertex
    GROUPS, and a flat render mesh has one group per position per smoothing group, which
    on a tessellated curve is far more resolution than a half-strength vertex signal
    needs. Merging near-coplanar groups within a distance tier would cut the ray count
    without touching the ray's own cost. Changes output; needs the PNG oracle.
  - [ ] **A cheaper hemisphere for the common case**: a first pass of 4 rays that returns
    exactly 1.0 (fully open) could skip the remaining 12 — but only if "4 rays escaped"
    implies the other 12 do, which it does not, so this needs a conservative bound
    (e.g. an unoccluded cone) rather than a sample count.
  - [ ] The **80k-triangle opt-out** is still a cliff: a part just over it renders flat
    while its neighbour just under it does not. A budget expressed in rays × expected
    per-ray cost would degrade instead of cutting off.
- [ ] **Matcap follow-ups** (the analytic matcap ✅ landed — `ShadingStyle` Lit/Clay/
  Metal behind the `uMatcap` selector in `ViewerShaders.MeshFragment`, driven by the
  toolbar Shading dropdown / `EngrCadOptions.Shading` / `RenderToImage(shading:)` /
  MCP `screenshot`'s `shading` / `EngrCadViewport.Shading`, default byte-identical
  by the docs-PNG oracle): texture-based CUSTOM matcaps stay declined until a color
  image reader lands for other reasons (a texture must reach three front ends);
  more built-in looks (pearl, toon-step) are one lobe set each in the one shader
  file if anyone asks; per-part override stays deliberately not offered (a scene
  lit two ways reads as a rendering bug).

## Blazor web viewer

Reimplement the viewer for the web: a Blazor front end rendering EngrCAD scenes in the
browser. Opens the door to sharing designs by URL, embedding live models in the docs
site, and eventually a hosted modeling experience. The kernel is pure .NET with no
UI dependencies, which makes this unusually feasible.

- [x] **Architecture decided and prototyped: Blazor WebAssembly, kernel in the
  browser.** `src/EngrCAD.Web` (Razor component library) + `samples/EngrCAD.WebDemo`.
  The three risks the decision hung on are now measured, not guessed:
  - **The kernel compiles to WASM unmodified and returns identical geometry** — a flange
    with a 6-hole bolt circle and a filleted rim gave 1 560 triangles, closed, volume
    41 573.0 in headless Edge, matching the desktop run. No WASM-specific code path,
    and no `ArrayPool`/`stackalloc`/`Vector<double>` trouble.
  - **Speed is a constant factor, not a wall**: desktop 88.7 ms total; WASM without AOT
    1 677.3 ms (18.9×); WASM with AOT 385.2 ms (4.3×). All three from clean publishes,
    interleaved into one measurement window.
  - **Payload**: 1.9 MB brotli, or 4.6 MB with AOT. All nine EngrCAD assemblies are
    1.14 MB uncompressed / 0.41 MB brotli, so our own code is about a fifth of the
    download and the runtime is the rest.
- [x] **Docs site hosts the live demo** — `docs/examples/web.md` embeds it in an iframe
  and `.github/workflows/docs.yml` publishes the app into `_site/live/`. The app is
  **path-portable**: `<base href="./" />` plus the already-relative asset references the
  build emits mean no `StaticWebAssetBasePath`, no post-publish rewrite and no repository
  name in the artifact. `?embed` strips the page furniture for the iframe. The page's
  headline geometry (1 560 triangles, closed, 41 573.0 mm³) is pinned by a `run:` snippet
  so the docs build fails if the kernel ever disagrees with what the page claims.
- [ ] **AOT for the docs deployment is deliberately OFF, and worth revisiting** — it is
  4.4× faster for 2.4× the download. Declined for now because AOT compilation adds
  minutes to every docs deploy and the embedded demo rebuilds only on slider release;
  revisit once the WebGL viewer lands and the page becomes something you orbit rather
  than something you rebuild. Still missing a **time-to-first-render** number —
  everything measured so far is compute-only, and the 4.6 MB download is exactly the part
  that number would price.
- [ ] **A published-artifact smoke test would be worth having.** An incremental publish
  can ship a runtime that disagrees with the assemblies: it builds clean, runs ~1.6×
  slow, then aborts with `MONO interpreter: NIY encountered in ...:.cctor ()`. A clean
  publish fixes it and CI is immune (fresh checkout), so the workflow only asserts
  `index.html` exists. A headless-browser check that the published app actually *computes*
  would close the gap — windows runners have Edge — but it can flake, and a flaky check
  blocking docs deploys is its own cost. The verification recipe is in
  `src/EngrCAD.Web/README.md`.
- [x] **Scene-to-frame layer** — `ViewportFrame.Build(...)` is a **pure function** from
  instances + camera to a `FrameDescription`, which is what lets draw order, clear
  colour, furniture ranges and per-instance matrices be asserted as values instead of
  compared by eye (`EngrCAD.Web.Tests`). The window and offscreen passes drifted
  precisely because pixels were the only way to compare them.
- [x] **The orbit-camera component** — `EngrCadViewport.razor`'s pointer/wheel handlers
  call `CameraMath.DragOrbit`/`DragPan`/`DragZoom`/`WheelZoom`, which were moved OUT of
  `ViewportControl` into `EngrCAD.Viewer.Core` (along with `CameraState`,
  `PitchLimit`, `KeyStep`) so both front ends share one implementation; the desktop now
  delegates, every constant preserved verbatim and locked by `OrbitCameraTests`.
  Verified drawing by canvas readback: 33 912 lit pixels, and 111 481 changed after an
  orbit. Headless WebGL2 works under `--disable-gpu`, which had been an open risk.
- [x] **Shared render model, step 1** — the UI-free half of `RenderCore.cs` is now
  `src/EngrCAD.Viewer.Core` (no Avalonia, no Silk.NET; a scratch blazorwasm app builds
  and publishes against it). `ViewStyle`, `SectionPlane`/`SectionAxis`/`SectionCombine`/
  `SectionClip`, `RenderModes`/`EffectiveMode`, `ViewerShaders`, `CameraMath` and
  `RenderGeometry`'s pure half are public there, namespace unchanged. Verified by the
  50 docs PNGs being byte-identical, which is the oracle that actually constrains a
  render refactor.
- [x] **Shared render model, step 2** — `TabMeshLoader` + `MeshFlavor`, `ViewCubeMath`/
  `ViewCubeAnimation` + a new `ViewCubeGeometry` (face table, fill/edge/label builders,
  palette, hover rule), `StrokeFont`, `AnnotationItem`/`AnnotationCamera`/
  `AnnotationGeometry` (+ the overlay colour) and `SectionContours`/
  `SectionContourGeometry` (+ the three isoline family colours) are all public in
  `EngrCAD.Viewer.Core` now, namespace unchanged; the GL halves (`ViewCube`,
  `AnnotationLayer`, `SectionContourRenderer`) stayed behind and consume them.
  (`HoverThrottle` and `WireframeEdges` had already moved with earlier rungs.) Oracle:
  full suite green and all 53 rendered docs PNGs byte-identical. Note `TabMeshLoader`
  is Avalonia-free but **thread-model-bound** — the browser keeps its own
  single-threaded loader by design (EngrCAD.Web README).
- [x] **The per-part upload description** — `PartUpload` + `PartUploads.Build(part,
  request)` + `PartUploadRequest` in `EngrCAD.Viewer.Core`: the render mesh, the
  `FieldRendering.TryBuild` result (and its error), the occlusion array, the feature-edge
  and wireframe segments and the pick BVH, built once for the window, the offscreen pass
  and the browser. The caller states which pieces it wants and where occlusion comes from
  (a delegate — never-bake cache read in the window, bake-inline offscreen, none in the
  browser); the cache and every GL call stay with each front end. The payoff was the
  content rules, above all **"a deformed part gets NO edge overlay at any factor"**, which
  had been written out three times. Oracle: all 108 committed docs PNGs byte-identical.
- [ ] **The full `ViewerModel` abstraction (Scene→render-instances shared by Avalonia,
  offscreen AND the web client)** — assessed twice and still deliberately NOT forced.
  Everything the three front ends genuinely share is now extracted (frame values, camera,
  modes, pick, widget geometry, and — as of the item above — the per-part upload value);
  what remains different is the *lifecycle*: the window streams uploads per part through
  `TabMeshLoader` (two threads), the offscreen pass is one-shot and synchronous, and the
  browser interleaves awaited JS uploads on one thread. A shared ViewerModel would have to
  abstract exactly that lifecycle, which is the part that must NOT look the same (the
  TabMeshLoader lesson). Nothing is blocked on it.
- [ ] **`EngrCAD.Viewer.Core` pulls the whole kernel**, because `RenderModes.Resolve` is
  written against `EngrCAD.Modeling.DisplayMode`. Right for kernel-in-the-browser; if a
  shaders-only consumer ever appears, the fix is a Viewer.Core-local display-mode enum —
  an API change, not a move.
- [ ] **Feature parity ladder** (build in this order): ~~orbit/pan/zoom camera + shaded
  mesh rendering~~ ✅ → ~~feature edges~~ ✅ → ~~display modes + the global view style~~ ✅
  → ~~tab strip + model tree + visibility~~ ✅ → ~~picking~~ ✅ → ~~section planes +
  their SDF isolines~~ ✅ → ~~view cube~~ ✅ → ~~annotations~~ ✅ → ~~properties panel +
  BOM~~ ✅ (the int-uniform prerequisite landed as the `IntUniform`/`Vec4ArrayUniform`
  typed markers in `engrcad-gl.js` — the JS dispatches on marker shape, C# decides
  which uniforms are which) → ~~the measure tool~~ ✅ → ~~exploded views + animation
  playback~~ ✅ (both are `ViewportFrame.PoseByPath`, a pure function matching by
  occurrence path; the transport is `AnimationPlayback` from Viewer.Core with a timer
  and three widgets here) → ~~multi-plane section planes + combine, through to picking~~
  ✅ → ~~debug-modifier parity (`DebugFilter.Shown` in `ResolveInstances`)~~ ✅.
  → ~~multi-plane isolines (`SectionClip.Siblings` per plane)~~ ✅ (per-plane
  contour builds with the plane carried on each `ViewportContours`, sibling-clipped
  under Union exactly as the desktop renderer; single-plane output bit-identical;
  `SetSectionPlanesAsync` is the direct-call path and the `?report` beacon carries
  the quarter-cut relationships `bodySectioned < bodyQuarter < bodyWhole` and
  `0 < goldQuarter < goldContours + goldContoursY`).
  **Remaining rungs**: construction-tree rows + rollback previews (needs
  `ConstructionPreviewCache`'s background-lowering story rethought for one thread);
  a multi-plane section **UI** (the plane list and combine are plumbed end to end now —
  what is missing is a browser toolbar affordance for building the set; the desktop
  toolbar has a plane-count cycler, so this is a real parity gap, not a shared one);
  and the `?report` self-check should grow pose/measure relationships (the pose seam
  is unit tested, but "the canvas changed when the slider moved" is the check this
  front end's culture asks for).
- [ ] **Docs-site embedding, the general form** — one page embeds the demo today
  (`docs/examples/web.md`). The payoff synergy is DocsGen emitting an interactive WASM
  viewer block *per example* instead of (or alongside) static PNGs — spin-the-model
  documentation, all statically hosted on the existing GitHub Pages deployment. Needs the
  scene-to-frame layer first (✅ landed), plus a way to ship one runtime shared by every
  embed rather than a 1.9 MB payload per page.
  **Assessed; the shape, and why it is bigger than it looks.** The runtime sharing is
  the easy half and is already solved by the deployment: `_site/live/` holds ONE published
  app, so every page iframes the same origin and the browser caches the 1.9 MB once —
  what is missing is a way for a page to say WHICH scene that one app should build.
  Three options, in increasing order of what they buy:
  (a) **A snippet id in the query string** (`/live/?example=fillet-corners`), with the
  demo app carrying a switch over the docs' snippet ids. Cheapest, and wrong for the same
  reason a second copy of a shader is: the snippet's source would live in the markdown
  AND in the app, and they would drift.
  (b) **Ship the compiled snippets as a data file.** DocsGen already compiles and runs
  every fence through Roslyn; it could emit the snippet SOURCES into `_site/live/` and the
  app could compile one in the browser — but that means shipping Roslyn to WASM, which is
  several times the payload of the kernel and defeats the shared-runtime argument.
  (c) **Emit each scene as data, not as code.** The document format now exists
  (`Document.Save`), so DocsGen could save each `render:` snippet's scene as a `.json`
  document beside its PNG and the demo app could `Document.Load` one by id. Payload is a
  few KB per example, the app needs no compiler, and the scene is provably the one the
  PNG was rendered from because both come from one evaluation. The cost is that a
  document is geometry-or-history rather than the snippet's own code, so a page's
  interactive block and its code fence are two representations of one model rather than
  one — acceptable, and honest, if the page says so.
  Recommendation: **(c)**, once someone wants it; it is a DocsGen change plus a load-by-id
  route in the demo, not a viewer change. Filed rather than built because it is a docs
  *infrastructure* project with its own deployment questions (cache busting per docs
  build, and what an embed does when a document names geometry this build cannot
  rebuild), and nothing depends on it.
- [ ] **Out of scope until later**: editing/sketching in the browser, collaboration,
  server-side model storage. This is a *viewer* first.

## MCP server / remote control of the viewer

The **headless server ✅ landed** (`src/EngrCAD.Mcp`: `EngrCadMcp.Run` + `--mcp` over
stdio — list/describe/screenshot/export/reload, PNG returned as an MCP image block,
stdout guarded, geometry evaluated lazily), and so have **write tools**
(`set_param`/`suppress_feature`/`unsuppress_feature` over `Part.History` +
`Part.Regenerate`), **screenshot's full option surface** (up to 4 section planes +
combine, explicit camera, export sizes), **structured content** (output schemas +
`structuredContent` on every JSON tool), the **StandardViews deletion** (poses route
through `ViewCubeMath`/`CameraMath`; `NamedViews` is only the name table), the
**forced no-GL error path** (`ENGRCAD_NO_GL`), and the **live-viewer RPC, option (b)**
(`RemoteControl.cs` in EngrCAD.Viewer: loopback-only newline JSON-RPC behind
`WithRemoteControl`/`--rpc`, token optional; `--mcp --viewer <port>` bridges
set_view/fit/set_section/set_display_mode/set_view_style/select_part/get_selection/
measure/viewer_screenshot; every mutation marshals through `Dispatcher.UIThread`, GL
only via `SaveScreenshot`'s capture-on-next-frame), **document persistence**
(`save_document`/`load_document` over the `Document` envelope — a session's edits now
survive it, reopening parametric, with snapshot parts named rather than silently
flattened; a loaded document is an overlay `reload` still discards) and the
**`screenshot` `t` parameter** (posed through the shared `EngrCad.PoseAt`). Remaining:

- [ ] **Untested**: a real third-party MCP client (Claude Desktop/Code) connecting —
  the protocol was driven by hand and via the SDK's own client.
- [x] **The windowed RPC path is covered** — `ViewportRemoteViewerTests` (headless,
  against a REAL `ViewportControl`) plus `WindowedRpcTests` (a live window, opt-in via
  `ENGRCAD_WINDOWED_TESTS=1`). The filed reason for it being untestable was wrong; see
  CLAUDE.md's status paragraph for what a `ViewportControl` does without a window.
- [x] **A client that connects the instant the port is announced sees NO parts** —
  resolved as the filed entry's FIRST shape: `ping` carries **`ready`**
  (`ViewportControl.InstancesDisplayed` — true once the render pass has adopted the
  instance swap and nothing newer is queued), and a part-not-found refusal during the
  gap says "poll ping until ready" instead of reading as "this model has no parts".
  Holding the port announcement until the first frame was rejected for the entry's own
  reason (a window that never renders would never announce), and reporting the pending
  list for its own (it would desynchronize the paths from the indices `select_part`/
  `set_display_mode` address). The windowed test now polls `ready` and then reads the
  list ONCE, with no blind retry; the headless half (a control that never renders IS
  the gap held open) is pinned in `ViewportRemoteViewerTests`. Honest scope: under
  lazy tab meshing the list is a growing prefix, so `ready` means "what is drawn",
  not "the document is fully loaded".
- [x] **`viewer_screenshot` returns pixels** — `ViewportControl.CaptureScreenshotAsync`
  is `SaveScreenshot` with a `TaskCompletionSource`; `ViewportRemoteViewer` awaits it
  OFF the UI thread under a 10 s deadline (blocking the dispatcher is how the frame would
  fail to arrive), the RPC result carries `written: true`, and `ViewerTools.Screenshot`
  reads the file and returns an MCP image block exactly as headless `screenshot` does —
  legitimate because the endpoint is loopback-only.
  **One detail of the filed diagnosis was wrong and is worth keeping.** It said the
  completion must fire "from inside the render pass"; that is too EARLY. `glReadPixels`
  runs there, but the encode and the write are deliberately off-thread, so a task
  completed at that point hands back a path to a file that does not exist yet. It fires
  from the write, immediately after `File.WriteAllBytes`. The entry's *conclusion* — not
  the `Status` callback — was right, but for a stronger reason than the one given: `Status`
  is a BROADCAST carrying prose for successes and failures alike, so a listener cannot
  tell its own capture from the toolbar button's or from a second concurrent request's.
  Synchronising on a string is not synchronisation.
  It also claimed this "cannot be tested by the headless socket harness", which held only
  because the write was tangled with the GL call: splitting `WriteCapture` out (pixels in,
  no context) makes the ORDER assertable — resume on the completion, assert the file is
  already there — and the bridge's image block, the timeout refusal and the
  unreadable-file case are all covered over real sockets with a stub. **Its closing claim
  has since been disproved too** — it named two remaining legs and said the second could
  not be automated because `ViewportRemoteViewer` "takes a concrete `ViewportControl`, so
  there is nothing to substitute". Nothing needs substituting: a `ViewportControl`
  constructs with no Avalonia application, no window and no GL context, and one that will
  never be rendered IS the deadline's fixture. Only the FIRST leg (a real render pass
  reaching the claim, under a live dispatcher) needs a window.
- [ ] **Option (c) — viewer hosts MCP directly over HTTP+SSE** stays parked unless the
  bridge process proves annoying in practice.
- [x] **`viewer_screenshot` reaches an animation's instant** — through the
  `set_animation_time` verb the entry predicted (park the transport, pause, then capture),
  verified against a real window by `WindowedRpcTests`. Rationale in CLAUDE.md and the two
  READMEs.
- [ ] **A narrower `save_parameters` tool** (writing only
  `FeatureHistory.SaveParameters` for one part) is the smaller sibling of the
  `save_document`/`load_document` pair that landed. Worth adding only if a client turns
  up that wants to diff one part's numbers rather than reopen a model; the document pair
  covers the "hand the tuning back" case that motivated it.
  (Packaging is settled: `src/EngrCAD.Mcp` is its own package on
  `ModelContextProtocol.Core`, so viewer and kernel consumers inherit nothing.)

## App layer / infrastructure

- [ ] **Design studies: drive `[Param]` values by an optimizer against a measured
  objective.** Everything below the loop exists and is verified: `FeatureHistory`
  regenerates with prefix caching, `[Param(Min=, Max=)]` already declares the box
  constraints, the FEA suite answers mass/stress/deflection/frequency, and `SolveAll`
  amortises multi-case solves. The feature is the loop — minimize mass subject to a
  stress or deflection limit, report the trajectory and the binding constraint.
  Derivative-free first (regeneration is not differentiable); a failed regeneration
  mid-search is REPORTED and the study continues from the last feasible point, the
  regeneration failure culture applied to a new consumer.
  - Verification: the cantilever gives closed forms — the minimum-mass depth for a
    stated tip-deflection limit is analytic, and the study must land on it to a stated
    tolerance rather than merely improve.
- [ ] **Configurations / design tables: one `FeatureHistory`, N named parameter
  sets.** A configuration is a name plus a `[Param]` value dictionary through the SAME
  JSON seam as `SaveParameters` (one seam, so spellings cannot drift); the document
  carries the set and the active one; the BOM rolls up per configuration. An M4…M12
  family of one bracket is the acceptance case.
  - Verification: save→load→save stays a byte fixed point with configurations present,
    and switching away and back regenerates bit-identical geometry — the cache-key
    property the undo stack already asserts, asked of a new consumer.
- [ ] **Manufacturability checks: draft angles, wall thickness, overhangs.** Three legs
  riding machinery that exists, one entry because the deliverable is one shape — a
  per-part report plus a `FieldDisplay` colouring. Draft: per-face angle against a pull
  direction (planar faces exact via `BrepQueries`, curved sampled and said so). Wall
  thickness: the SDF answers it locally already (the section isolines); a global
  minimum-thickness field wants an honest estimator assessed before a number is
  promised. Overhangs: facet normal against a build direction below a threshold,
  area totalled — pure mesh arithmetic.
  - Verification: closed forms — a drafted block's walls read exactly the drafted
    angle, a shelled box reads its wall thickness exactly, and a 45° cone at a 45°
    threshold reports zero overhang area on either side of the tie.
- [ ] **ISO 286 fits and tolerance stackups along a mate chain.** The fit tables
  (H7/g6 and friends) are a transcription carrying the verify-against-datasheet flag
  (`StandardHoles`' convention); a stackup is a walk along the existing mate graph
  summing dimensions worst-case and RSS. This is where mechanical engineers actually
  lose time — the same argument the ECAD assessment makes for the MCAD boundary.
  - Verification: table rows asserted in the form a human checks (micrometres straight
    from the standard), and a textbook stackup reproduced both worst-case and RSS.

- [ ] **Parametric features follow-ups** (`FeatureHistory` landed; typed geometry
  inputs landed — `GeometryRefs.cs`: `PlaneRef`/`FaceRef`/`FaceSetRef`/`EdgeSetRef`/
  `AxisRef` with cardinality in the type, descriptor-as-cache-key-as-serialized-form,
  per-`Apply` resolution, and `ValidateInputs` naming the failing property; feature
  registry + whole-history JSON landed — `FeatureRegistry` with instance-free
  `[Param]` metadata and honest `CanCreate`/`Reason`, `SaveHistory`/`LoadHistory`
  with exact sketch/hole-spec/component constructor-input serialization via
  `Feature.SaveInputs`, nullable `[Param]` values, and a coverage test enumerating the
  sketch segment types so the writer cannot fall behind the reader again)
  — property-panel UI editing of `[Param]`s driven by the registry's metadata (free-text
  through the JSON seam landed; typed editors are the polish pass), feature list in the
  viewer model tree with registry-backed INSERTION (`DocumentEdits.AddFeature` is the
  undoable half; the catalogue dialog is not built), and **a `Shape`-graph serialization,
  which is what remains of the "code inputs" item** — it would unlock `BooleanFeature`,
  and by extension `ComponentAssembly(name, shape)`, whose base body is a lambda over an
  arbitrary `Shape` and therefore the one opaque record a fastener-bearing host still
  carries. (`ComponentFeature` itself now round-trips, by KIND plus factory arguments —
  the designation was assessed and rejected as the key, being lossy in exactly the fields
  a reload would get wrong.) Persistent topological
  IDs are no longer open in the abstract: `Shape.Tag` + `BrepFace.Provenance` landed, and
  what remains is the per-algorithm inheritance filed under "Topological naming residuals".
- [ ] **An OPTIONAL-numeric parameter editor** (Viewer / Viewer.Core, small). Nullable
  `[Param]` values landed and the rule that follows them is that a parameter whose editor
  cannot express absence keeps a sentinel instead — because `ParamEditors.KindFor` offers a
  slider exactly when the range is finite at both ends, and a slider is a total function
  onto its range. `EdgeFlangeFeature.KFactor` is the case: it stays `double` with 0 meaning
  "inherit" purely because its editor cannot say "unset". A third `ParamEditorKind`
  (a clear/inherit affordance beside the slider — a checkbox, or a "—" button that writes
  `null` through the same JSON seam) would let it become `double?` like its two neighbours
  and would remove the only asymmetry in that feature's API. `ParamEditors.Position`
  already returns 0 for a null value, so the panel does not crash today; it simply reads
  the minimum, which is the misleading part.
- [ ] **Geometry-reference vocabulary follow-ups.** Landed: `PlaneRef.Offset(distance)`
  and `PlaneRef.Rotated(degrees, inPlaneAxis)` (resolve the base, then move — so a
  derived plane re-finds its base per regeneration; axes carried verbatim, rotation axis
  in the base's own coordinates, exact-zero returns the base itself),
  `FaceSetRef.LargestByArea`/`SmallestByArea` over `BrepSelection.Area`,
  `Touching(point)` (carrier projection THEN the face's trim test, so a point over a bore
  matches nothing), `AdjacentTo(set)`, `CylindricalBetween(min, max)`, the edge RANGES
  (`EdgeSetRef.CircularBetween(min, max)` and `LongerThan`/`ShorterThan`/`Between` over
  `BrepQueries.Length`, with an open-ended range taking its own `lengthAtLeast(n)` term
  rather than an infinite bound the shared number lexer cannot read), and the `Shape`
  overloads (`Fillet`/`Chamfer`/`ChamferAtAngle`/`FilletEdges`/`ChamferEdges` in constant
  and variable-law forms take `FaceSetRef`/`EdgeSetRef`). Remaining:
  - **`Shell` cannot take one without a source break** — its `openings` parameter is a
    *nullable* `Func`, so a reference-typed overload makes the existing `Shell(t, null)`
    ambiguous at every call site (seven sites in the repo, four of them writing
    `openings: null`, which a named argument does NOT disambiguate when both overloads
    name the parameter the same). Three routes, none free: rename the reference-typed
    entry (`ShellOpening(...)`); leave callers on `openings.AsSelector("openings")`, which
    is what the doc comment now says; or give `FaceSetRef` an implicit conversion to
    `Func<BrepSolid, IEnumerable<BrepFace>>` — which would need no new overload anywhere,
    would not disturb the existing `FaceSetRef` overloads (an identity conversion still
    wins overload resolution), and costs the input NAME in the failure message, which is
    the whole reason `AsSelector` takes one. Worth a decision the next time someone writes
    the awkward call. `Draft`'s per-face predicate has the same shape.
  - **An exact-LENGTH edge query is deliberately absent** and should stay absent unless the
    measure improves: `BrepQueries.Length` is exact for lines and circular arcs and a
    64-chord polyline otherwise, so a value comparison at the weld tier would be a
    correct-looking question with a wrong answer on any traced or NURBS edge. The range
    filters are honest about being filters; an exact query would not be.
  - **A `VertexRef`** — assessed, and it is not the trivial fifth member it looks like.
    The other four resolve to things the kernel already treats as objects (a face, an
    edge, a frame); a vertex's USES are a *point* (anchor a dimension, seed
    `Touching`, place a pattern) and the natural spellings — "the corner between these
    three faces", "the highest vertex of this face", "the ends of this edge" — are all
    derived rather than stored, so the type's real content is the query set, not the
    resolution. It also needs a cardinality decision the others did not: "the corner
    where these faces meet" is exactly-one while "this face's corners" is a set, so it
    wants BOTH a `VertexRef` and a `VertexSetRef` or an honest reason it does not.
    Worth doing when a consumer exists (a vertex-anchored dimension is the likeliest);
    inventing it before then would fix the query set by guesswork.
- [ ] **Assemblies follow-ups** (v2 landed: BOM, exploded views, mates — now ACROSS
  assembly levels with typed `FaceRef`/`AxisRef` references and
  `SaveMates`/`LoadMates` persistence — STEP assembly export + import, tree
  expand/collapse, retro-assigned palette colors) — true GPU instanced drawing (matrix
  buffer, one draw per part), per-instance color/display-mode overrides, an
  **explode-path renderer** (the dashed leader lines drafting standards draw between an
  exploded part and its seat — and `Occurrence.ExplodePath` now gives it a real path to
  draw rather than a straight line to assume), and **flexible sub-assemblies**: a deep
  mate target inside a multiply-placed sub-assembly is refused today because its
  internal frame is one shared object. Mechanisms (above) can now assume cross-level
  mates exist: a linkage whose members are sub-assemblies is jointable via occurrence
  paths.
  **Flexible sub-assemblies, assessed — this is the big one, and it is a DOCUMENT-MODEL
  change rather than a solver change.** The refusal is honest and structural:
  `Occurrence.SubAssembly` points at a shared `Assembly` object, and that assembly's own
  occurrences carry `Frame3d`s, so two placements of one sub-assembly necessarily agree
  about every internal pose. Onshape's answer is per-instance internal state, and the
  seam it has to attach to here is `Assembly.Flatten` — the ONE walk every consumer sees
  (viewers, exporters, BOM, mates, mechanisms, animation). Three candidate shapes, with
  what each costs:
  (a) **Deep-copy on flexibility** — mark an occurrence flexible and clone its
  sub-assembly. Simplest, and wrong: it breaks part IDENTITY (`Scene.AllParts` dedupes by
  reference, so a cloned subtree would mesh and upload twice) and the BOM would
  double-count.
  (b) **A per-occurrence frame OVERLAY** — `Occurrence.Overrides: Dictionary<string,
  Frame3d>` keyed by the relative occurrence path, consulted by `FlattenInto` as it
  descends. Preserves identity, is additive to the format, and is a few lines in the
  walk. The cost lands on everything that WRITES a frame: the mate solver's variables are
  occurrence frames, so solving inside a flexible instance must write to the overlay
  rather than to the shared occurrence, which means `MateSolver` needs to address "the
  frame of THIS placement of that occurrence" — a path, not an object. That is the real
  work, and it is the same change `MateRef` would need.
  (c) **Instance-level document objects** (a first-class `AssemblyInstance` with its own
  occurrence list) — the most general and the most disruptive; it changes what an
  assembly IS.
  Recommendation: **(b)**, and only when a real model needs it. Roughly two to three days
  with the mate-solver addressing change, and the test that matters is not "it moves" but
  that two placements of one sub-assembly can hold DIFFERENT internal poses while still
  sharing one `Part`, one mesh and one BOM line.
- [ ] **Standard component library — remaining fidelity** (breadth landed: ISO 7380
  button, ISO 10642 csk, ISO 4032 nuts, ISO 7089 washers, 60x deep groove bearings,
  the opt-in exact hex socket on `CapScrew`, and `PlaceThrough(..., anchorInto:)`
  anchoring into a placed insert or nut with engagement/thread/point checks).
  Remaining: a modeled thread on the shank via `Shape.ExternalThread`; knurled/flanged
  inserts; ISO 2338's crowned pin ends; **hex sockets for csk heads** — the head top is
  planar but the primitive rebuild puts cone and shank tangent along a shared rim, so
  it needs either tangent-union support in the exact boolean or planar ⊥-axis caps on
  full-turn revolves (the cap is a `RevolvedSurface` with a pole today, which is also
  why the socketed cap screw is rebuilt from cylinder primitives); washer/nut stack
  seating (a screw seated ON a placed washer rather than on the face); bearing shaft
  seats (`PrepareAnchor` cutting the shaft's seat when bearings join a stack).
- [ ] **Frame3d enabled next steps** (the `TopPlane` behaviour question is settled: both
  conventions are now expressible — `PlaneRef.TopPlane` keeps world (0,0,z) origins,
  `PlaneRef.OnTopFace` gives the face's own frame — so it is a per-feature choice rather
  than a global decision; `StepWriter` AXIS2-via-`Frame3d` landed — a `Placement(Frame3d)`
  overload mirrors `StepReader.Axis2`/`FromZX`, and the matrix path now REFUSES mirrored
  (improper) placements by name, which passed the orthonormality guard and would have
  silently re-posed un-mirrored on read-back) — arbitrary section planes from a frame;
  Part poses as frames (assemblies above).
- [ ] **Parametric model layer follow-ups** (`.csx` scripting landed —
  `tools/EngrCAD.Script` runs a script through `EngrCad.Run` with save-to-reload via
  the new `EngrCad.NotifySourceChanged()`, DocsGen's snippet contract and Roslyn seam,
  docs page + `samples/scripts/bracket.csx`; the reusable-component pattern is
  documented as plain C# methods returning Shape/Part) — remaining: a fluent C#
  builder over the retained document model; `#load` library conventions for shared
  `.csx` component files; a `dotnet tool` packaging of the script runner so
  `engrcad model.csx` works without the repo.
- [ ] **Bounded planar carriers clip; unbounded ones only clip to the query region.**
  Two `PlaneSurface` faces meeting at an angle produce a line spanning the whole
  region, so a boss's wall imprints its footprint edge ACROSS the host's entire top
  face instead of just the shared rim — topologically fine (the splitter keeps only
  the interior stretches and every fragment classifies correctly), but a flush union
  leaves the host's top face in more pieces than it needs. `SolidFactory.MakeBox`
  builds `PlaneSurface` faces; sketch extrusions already get the bounded-parallelogram
  treatment. Options: give `MakeBox` bounded carriers, or pass `Intersect` a per-pair
  region (the two faces' bounds, expanded) instead of the whole-model one — the latter
  touches every pair in the boolean pipeline, so it needs the corpus gate behind it.
- [ ] **Logging follow-ups** (`ILogger` adoption ✅; kernel extension ✅ landed —
  Interop and BRep take the abstractions reference, weighed per project: optional
  trailing `ILogger` on `BrepBoolean` ops (event 80, sub-steps threaded through),
  `BRepTessellator.Tessellate` (81), `MeshSdf` ctors incl. the winding build (82),
  `StepReader.Read/ReadFile` (90); Mesh/Core/Implicit deliberately stay
  dependency-free since everything named is reachable at those seams; results stay
  return values) — remaining: thread a logger from `Shape` lowering
  (`ShapeCompiler`/`Part.TryGetSolid`) down to these seams so a design program's
  logger sees its own booleans; consider `SurfaceNets.Polygonize` and `MeshRepair`
  timing at the same standard; a `--log-kernel` switch on `EngrCad.Run` wiring the
  host console logger into the kernel seams.
- [ ] **Sheet metal v2 — corners, reliefs and the rest of the vocabulary.** v1 landed
  (`Modeling/SheetMetal.cs`, `SheetMetalFeatures.cs`, `BRep/SheetMetalSurgery.cs`, docs
  `examples/sheet-metal.md`): the K-factor bend model, base flange + edge flanges as
  direct topology surgery, the flange tree, `Unfold()` to a `Sketch` with bend lines, DXF
  and SVG out, and the folded-versus-flat volume identity as the oracle. What it refuses
  BY NAME is the backlog, roughly in the order the refusals bite:
  - **Closed corners and miters.** The genuinely fiddly part, and the one that unlocks
    most real parts: two flanges on adjacent edges of one plate. v1 detects it (the
    consumed wall is no longer four-sided) and refuses. The corner needs the two bends'
    bands trimmed against each other and a corner face built — the same
    surface–surface-re-intersection machinery that blocks curved-face shelling and
    non-perpendicular fillet corners, so the three are worth costing together.
  - **A flange flush at ONE end only.** Currently refused as "the corner case in
    disguise", which it is — but the common shop case (a flange running to one end of a
    plate) deserves the small special case: the neighbouring wall's loop is spliced at
    one end and a cap built at the other.
  - **Bend reliefs** (rectangular / obround / tear). Pocket subtractions at known
    coordinates, i.e. the exact sketch-pocket case, so mostly plumbing — but they change
    the FLAT pattern too, which is where the design work is.
  - **Jogs, hems, curls and louvres.** Each is a different forming operation; a hem is
    the interesting one, since folding a flange back against the sheet produces
    coincident faces that v1's tangency argument explicitly refuses.
  - **Bends along non-straight edges** (a developable band rather than a cylinder) —
    needs a new swept surface, not just new bookkeeping.
  - **Holes and cuts ON a flange, carried into the flat pattern.** Today a hole must be
    drilled on the folded solid AFTER the sheet body is built, and the flat pattern does
    not know about it. The unfold walker already has each flange's rigid 2D↔3D frame
    pair, so the mapping exists; what is missing is a place to declare a flange-local
    sketch and the decision about what to do with a hole that crosses a bend.
  - **Multi-body sheets and welded assemblies**; **spring-back compensation** (a press
    property, deliberately out of scope for geometry).
  - Smaller: a `SheetMetalDrawing`-style bend TABLE beside the flat pattern (angle,
    direction, radius, allowance per bend — `FlatBendLine` already carries the data);
    a viewer/`--export` route that writes a part's flat pattern without a script; and
    mirrored placements (v1 is rigid + uniform scale, as loft/draft/shell are).
  - Five cross-cutting cleanups the sheet-metal review surfaced, each outside the
    feature and each with callers beyond it, so all deliberately NOT folded in:
    (a) **`TopologyEditor.Use(edge, from, to)`** — "the coedge sense that walks from→to"
    as an assertion rather than a convention. `SheetMetalSurgery` has it privately;
    `Filleting` hand-computes senses in ~30 places, and two of its recorded lessons
    ("build all new rim edges in the top face's traversal direction", "don't double-flip
    arc spans already measured from traversal-ordered points") are exactly what it would
    have prevented. Best promotion candidate in the area.
    (b) **`TopologyEditor.DetachFace(face)`** — dropping a discarded face's coedges from
    the surviving edges' use lists is now written three times (`SealSeams` step 1,
    `SheetMetalSurgery.Detach`); separating it from `SealSeams`' seam-tier vertex
    unification would also stop new callers reaching for `UsesInternal`.
    (c) **`BrepQueries.FacesOf` scans every face × loop × coedge** where `edge.Uses`
    answers in O(2). `IsConvex` calls it per edge, so `solid.ConvexEdges()` — which is
    what `EdgeSetRef.Convex` resolves to — is O(E·F) today. One line, but it changes the
    RESULT ORDER (solid order → construction order) and `Filleting`/`BrepBoolean` read
    `faces[0]`/`faces[1]`, so it wants its own pass with the ordering checked.
    (d) **`Distance3d.ClosestPointOnSegment`** — the repo now has four private segment
    routines (`Region2dBoolean`, `ThreadSdf`, `ShapeNodes`, `SheetMetalSurgery`), which is
    the exact count that triggered the `ClosestPointOnTriangle` promotion.
    (e) ~~Nullable `[Param]` values~~ — **landed**, and the diagnosis was half right.
    `FeatureHistory.Convert` did throw on `Nullable`1` (swallowed into a warning, so the
    value was silently dropped on load), and four lines in the shared seam fixed it. But
    the conclusion — that the serializer is what forced `0` to mean "inherit" in
    `EdgeFlangeFeature` — is not the whole reason: `ParamEditors.KindFor` offers a SLIDER
    whenever `[Param(Min=, Max=)]` is finite at both ends, and a slider cannot say
    "unset". So `Width` and `BendRadius` became `double?` (unbounded above, hence text
    boxes) while `KFactor` keeps its sentinel, and the residual is filed above as an
    optional-numeric editor.
  - One correction the v1 work made to the original assessment, worth keeping: it
    claimed "folded and unfolded volumes must agree exactly, a strong built-in test
    oracle". They agree exactly only at **K = 0.5** — a constant-thickness bend holds
    `θ·T·(R + T/2)` per unit width while the blank spends `θ·T·(R + K·T)` — so the real
    oracle is the exact DIFFERENCE `Σ width·θ·T²·(0.5 − K)`, which is strictly stronger
    (a blanket agreement test passes a model with K wired to a constant).
- [ ] nuget.org publish — pack VERIFIED solution-wide at 0.1.0 (12 packages, zero
  warnings; every src project has a Description and a packaged README;
  `RepositoryType` added). Remaining, all Chris's to confirm: the placeholder
  `RepositoryUrl`/`PackageProjectUrl` (`example.invalid` — a real remote exists at
  github.com/veggielane/EngrCAD) and the MIT license choice, then the actual push.
  GitHub Pages needs Settings → Pages → Source: GitHub Actions enabled once, then a
  push deploys the docs site.

## Future work (whole domains, not scheduled)

- [ ] **GPU acceleration — for modelling AND simulation, and the contract question comes
  before the kernel question.** The repo's strongest correctness tool is bit-identity
  (batch SDF == scalar to the bit, deterministic assembly order, PNG byte-comparison),
  and a GPU cannot honour it: fused multiply-adds, vendor transcendentals and
  nondeterministic reduction order all move the last bits, and `Vector.Cos` differing
  by 1 ulp was already enough to keep the trig kernels scalar. So every GPU path is
  **opt-in with a stated deviation bound, never a silent default** — the "a silently
  divergent fast path is worse than none" rule is the whole design constraint, and the
  first deliverable is the seam that makes an honest A/B possible (upload boundary =
  the existing SoA batch seam; results compared against the CPU path with the bound
  ASSERTED, not assumed).
  - **Dependency shape**: kernel projects stay free of rendering/GPU references — a new
    leaf (`EngrCAD.Gpu`) implements existing seams (`Sdf` batch evaluation, an
    `IProjectionTarget`, a CG matvec provider) the way the viewer already consumes
    kernels, injected by the caller rather than referenced by Core/Implicit/Fea. The
    compute stack should ride what is already shipped (Silk.NET / ANGLE ES 3.1 compute
    or a thin D3D/Vulkan binding), measured before chosen; consumer GPUs run fp64 at
    1/32–1/64 rate, so fp32-with-refinement vs native fp64 is a MEASUREMENT, not a
    preference.
  - **Modelling candidates, ranked by contract compatibility**: (1) the ambient-
    occlusion bake — embarrassingly parallel hemisphere rays, 12.3 s CPU on the demo
    scene, and its output is *shading*, where a bounded deviation is survivable (though
    the committed PNGs move, so it lands as an opt-in like `Scheduling`); (2) dense/
    NarrowBand `Sampled` grid bakes through the batch seam — but re-measure first:
    SurfaceNets is now ASSEMBLY-bound, not evaluation-bound (the cull lesson: an 8×
    saving in the dominant cost bought 2.5× because the dominant cost stopped being
    dominant); (3) expression-tree→compute-shader SDF compilation, which CLAUDE.md's
    implicit roadmap already names. Mesh booleans and the exact predicates are
    OFF-LIMITS — classification order is load-bearing there.
  - **Simulation candidates**: (1) the CG matvec + Jacobi apply — the memory-bound
    classic, and CG already wins on 3D elasticity at scale, so accelerating it widens
    the win where the direct solver's wall stands; (2) batched element-stiffness
    integration feeding the SAME deterministic scatter (the assembly-parallelisation
    decline showed the scatter is 82–94% of assembly cost, so measure whether the GPU
    half pays at all before building it); (3) the supernodal factorization's BLAS-3
    inner kernel — the analysis already on file says blocking is the lever and the tree
    ceiling is 1.6–1.9×, and a GPU GEMM is that lever's strongest form, but it only
    exists AFTER the supernodal CPU factorization does, so it is sequenced behind it.
  - **Verification bar**: every kernel ships with its CPU twin and an asserted
    deviation bound; FEA results additionally re-verified through the existing
    closed-form suite (the figures that held through the unit consolidation are the
    regression oracle — if a GPU path moves the cantilever or a modal frequency past
    its recorded tolerance, the path is wrong, whatever the speedup); determinism
    stated honestly per path (same-device reproducibility is achievable, cross-device
    is not, and the docs must say which is promised).
- [ ] **2.5D CAM — pocketing, profiling, drilling cycles and a G-code writer.** The
  nearest-term of the domains here, because the hard part shipped without ever being
  called CAM: `Region2dOffset` IS toolpath offsetting, successive inward offsets ARE
  pocket clearing, `Stroke` is documented as toolpath footprints, and
  `Shape.Section`/`Silhouette` produce the 2D input from any solid. What is missing is
  the thin layer on top: pass linking (climb vs conventional ordering), lead-in/out
  arcs, depth stepping, a drilling-cycle vocabulary that reads `HoleTable.For(part)`
  (the holes already know their specs and depths), and a dependency-free G-code
  writer — a text format plainer than four formats already hand-rolled here.
  - **Verification bar, in the house style**: path-length and swept-area identities
    against closed forms the 2D engine already answers; "no gouging" as an EXACT
    claim — every path point at least the tool radius from the region boundary, which
    the exact 2D signed distance can assert point by point; and machined-stock
    simulation by successive 2D boolean subtraction, its residual against the target
    region measured rather than eyeballed.
  - **Honest sequencing**: pocket/profile passes over a `Region2d` → depth stepping
    and linking → G-code out → drilling cycles from the hole table. 3D surfacing is a
    DIFFERENT problem (scallop height over meshes) and should be assessed separately,
    not assumed to follow.

Each of these is its own product-sized campaign rather than a backlog item, and each sits
here because the honest assessment says so — not because nobody got to it. They are kept
in this file, with their reasoning intact, so that a future decision to start one begins
from what was already understood rather than from scratch.

- [ ] **CFD — assess honestly before starting, because it is not "FEA with different
  physics".** Structural and thermal share a shape: symmetric positive-definite operators,
  one unknown field, `SparseCholesky`/CG, and a verification bar of analytic solutions.
  Incompressible flow breaks every one of those, and the backlog should say so before
  anyone budgets it as a third solver.
  - **The matrix is not symmetric.** Advection makes it non-symmetric and, at any
    interesting Reynolds number, non-diagonally-dominant. `SparseSymmetricCG` and
    `SparseCholesky` do not apply; this needs **GMRES or BiCGSTAB with a real
    preconditioner** (ILU at minimum). That is a genuine addition to `Core.Solvers`, and
    it is the first thing to build — it is also independently useful.
  - **It is a saddle-point problem.** Velocity and pressure are coupled and the pressure
    has no equation of its own. Either a segregated scheme (SIMPLE/PISO) or a monolithic
    solve with a block preconditioner; and equal-order velocity/pressure elements are
    **inf-sup unstable** — so either Taylor–Hood (P2 velocity / P1 pressure, which the
    existing 10-node/4-node tet pair gives almost for free) or PSPG stabilization. The
    Taylor–Hood route is the one that reuses what exists.
  - **Advection needs stabilization** (SUPG) once it dominates diffusion, or the solution
    oscillates rather than being merely inaccurate — a failure mode that looks like a bug
    forever.
  - **The mesh requirement is no longer the blocker, and it is worth being precise about
    what that changed.** `BoundaryLayerSpec` now marches a graded anisotropic stack inward
    from a `Facets`-selected wall (measured stretches to 72x, the wall named by the same
    selector a no-slip condition would use, the volume identity and both patch tests
    surviving it). So a CFD solver would no longer be running on isotropic tets. What it
    does NOT give is uniform y+ control on curved walls (no `cos` correction at corners,
    no per-facet thickness law), rims that slide along curved neighbours, or concave
    corners meshed with multiple normals per node — all filed above. Nothing here touches
    the flow physics: **what the mesher gives CFD is the mesh, not the solver**, and the
    staging below is unchanged.
  - **Turbulence is a modelling choice, not an algorithm.** Laminar-only is a defensible
    v1 and covers real engineering (internal flow at low Re, cooling channels); RANS
    (k-ω SST) is a second project with its own wall-function subtleties.
  - **Staging that respects the verification culture**: (1) non-symmetric solvers in Core,
    verified against dense references; (2) **Stokes flow** — linear, no advection, and
    exactly where inf-sup stability is provable and testable; (3) steady Navier–Stokes,
    laminar; (4) transient; (5) turbulence, or not.
  - **Verification bar, non-negotiable and higher than the other solvers'** because CFD
    fails plausibly: Poiseuille flow against the exact parabolic profile and its friction
    factor; **lid-driven cavity against Ghia et al.'s tabulated centreline velocities** at
    Re 100/400/1000; backward-facing step reattachment length against Armaly; and flow past
    a cylinder against the **Schäfer–Turek** benchmark's drag/lift coefficients. Report
    every number in the design record, as the structural and thermal solvers did.
  - **The honest summary**: this is larger than structural and thermal combined, its
    prerequisite (anisotropic meshing) is itself a substantial project, and a
    half-verified CFD solver is worse than none because its output is persuasive. Worth
    doing — but as its own campaign, staged as above, not as a fourth item in a sweep.

- [ ] **ECAD — and the sharp line between the part of it this kernel should touch and the
  part it should not.** "Add ECAD" reads as one thing and is really two, with very
  different verdicts.
  - **What this project should NOT build**: schematic capture, netlist management,
    autorouting, copper DRC, signal integrity, SPICE. Not because they are hard, but
    because they are a *different product* on a different data model — a connectivity
    graph, not a geometry kernel — and every one of them is served by mature free tools.
    Building them badly is worse than not building them, and building them well is a
    second company.
  - **What genuinely fits, and is where mechanical engineers actually lose time**: the
    **MCAD–ECAD boundary**. Does the board fit the enclosure, do the connectors line up
    with their panel cutouts, do the tall parts clear the lid, and where does the heat go?
    Every one of those is a question this kernel is already equipped to answer, and none
    of them needs a netlist.
  - **The reuse story is unusually strong, which is the argument for doing it at all**:
    - A **board is a plate with holes and a thickness** — `Sketch` outline, `Drill` for
      mounting holes and vias, exact in B-Rep today with nothing new.
    - A **component is a `HardwareComponent`**. That abstraction is already "a body + a
      seating convention + a **host preparation**", and a panel-mount connector needing a
      cutout in the enclosure wall is *precisely* that pattern — `ComponentAssembly.Place`
      cutting the host while recording the occurrence is the behaviour, already built and
      tested.
    - **Keep-outs are volumes**, so the implicit engine and the existing boolean both
      apply directly, and a violated keep-out is an ordinary interference query.
    - **Enclosure fit** is `Bvh.QueryOverlap` + `MeshIntersection.Crosses` + the mechanism
      sweep's clash reporting — landed, and it already knows that a *seated* part is not a
      clash, which is the exact subtlety a board sitting on standoffs would otherwise trip.
    - **Thermal coupling is the one that would be genuinely novel**: per-component power
      dissipation as a volumetric `Generation` load into the landed thermal solver,
      conducting through board and standoffs into an enclosure with convective outer
      faces. That is a real engineering question, it is verifiable, and almost nothing in
      the hobby/prosumer tool space answers it.
    - **Drawings and BOM already exist** — an assembly drawing with the board in place, and
      a parts list that distinguishes bought-in from manufactured, come for free.
  - **Interchange, in value order** (this is the actual first deliverable, since without
    import there is nothing to fit):
    - **IDF 4.0** first — board outline, component placements, keep-outs; plain text, still
      spoken by nearly every ECAD tool, and it carries *exactly* the subset above and
      nothing this kernel would have to discard. The classic MCAD/ECAD exchange for the
      classic MCAD/ECAD question.
    - **KiCad `.kicad_pcb`** as the pragmatic modern target: open, documented, S-expression,
      and its component 3D models are already STEP — which this kernel reads.
    - **STEP AP214 board assemblies** — already have the writer, the reader and assemblies,
      so this is mostly a mapping decision.
    - **IPC-2581** (the modern XML successor) and **ODB++** are richer and heavier; file
      them behind the first two.
    - **Gerber/Excellon are FABRICATION formats** and are the wrong layer for this entirely
      — they describe copper artwork for a photoplotter, not a solid model. Named here so
      nobody reaches for them thinking "PCB format".
  - **Verification bar**, in the house style: an IDF round trip that is a fixed point;
    a board-in-enclosure case with a *known* clash that must be found and a near-miss that
    must not be; and a thermal case with an analytic answer (a uniformly dissipating board
    conducting to a fixed-temperature edge) before any pretty picture of a real design.
  - **Honest sequencing**: IDF import → board + components as an `Assembly` → fit and
    keep-out checking → panel cutouts via the component-preparation machinery → thermal
    coupling. Each step is independently useful, which is the test of whether a domain
    belongs here at all. Stop at any point and what exists still earns its keep.

## Not worth adopting (deliberate)

- Raytraced/PBR rendering, a GUI sketcher, and freeform surface-modeling studios —
  each is a different product on the same data model (the "second company" argument
  the ECAD assessment makes), and each is served by mature tools a mesh export away.
  The viewer stays an engineering viewport; sketches stay code; surfacing waits for a
  need the `Shape` vocabulary cannot meet.
- g3's mesh structure itself (index+edge-list) — our half-edge with explicit boundary
  half-edges is a deliberate different choice; adopt its *editability mechanisms*, not
  the structure.
- 2D-only NURBS — we already have 3D NURBS curves/surfaces.
- g3's subdivision gap — it has no Loop/Catmull-Clark; we already have Loop.
- Skeletal-*field* convolution blends (`SkeletalBlend3d`/`SkeletalRicciBlend3d`) —
  they operate on 0..1 skeletal fields, not signed distances, and would break the
  implicit engine's sign-exactness contract.
