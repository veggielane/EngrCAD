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

- [ ] **The remesher's longest edge converges far more slowly than its distribution.**
  Measured on a Ø20×20 cylinder at a 2 mm target, 94–96% of edges reach the
  [0.66 L, 1.33 L] band within ~14 passes while the maximum sits near **2 L** however many
  passes are spent (4.5 at 10, 4.0 at 14, 4.2 at 20, 3.4 at 40 with a fast-split prepass).
  The mechanism is that a collapse can create a fresh edge of up to twice the target which
  the *next* pass has to find and split. Worth a bounded-maximum mode (re-visit the edges an
  operation created within the same pass, guarded against cascading) if anything ever needs
  a guaranteed maximum rather than a good distribution.
- [ ] **Face-aligned projection accumulates over the whole mesh even under queue
  scheduling** — a vertex's position there is a function of its incident triangles, so a
  partial accumulation would weight it against a subset. Restricting the face loop to the
  faces incident to the active set (and clearing only those accumulators) would make the two
  features compose; today `FaceAligned` costs O(faces) per pass regardless.
- [ ] **`Part`-level display remesh** — `Shape.Remeshed` is a graph node, so a remesh is a
  modelling decision baked into the design. A viewer-only "give this part uniform triangles
  for display/FEA export" switch on `Part` (a post-tessellation pass inside `GetMesh`) is a
  different, smaller thing and is not built; it would need to interact with the mesh cache
  and `MeshQuality` precedence.
- [ ] Mutable in-place variants of fill/extrude once callers want them.

## Implicit engine (EngrCAD.Implicit)

- [ ] **The bézier kernel's Newton stage is fixed at 8 iterations for every lane.** The
  scalar code was too, so this is not a regression — but a lane-wise form makes the waste
  visible: the sticky "active" mask already knows when every lane has stopped moving, and
  the loop only exits early when every lane's derivative has *vanished*, not when every
  lane has *converged*. A convergence exit would change results (the scalar path does the
  full eight), so it needs the golden hashes re-derived deliberately, with a measurement
  showing it is worth the churn — the reject in front already skips most cubics.
- [ ] **The lane-wise arc kernel gives a whole block back to the scalar path when any one
  lane is inside the wedge certainty band.** Per-lane blending would keep the other three
  lanes vectorized. Almost certainly not worth it — the band is measure-zero against a
  sample grid, so the fallback fires only on constructed inputs — but if a consumer ever
  samples *along* a boundary ray (an iso-line trace on a sketch's own sweep boundary, say)
  the whole trace would run scalar.

## Interop / meshing (EngrCAD.Interop)

- [ ] **A grid quad's diagonal is chosen by CORNER ORDER, not geometry.**
  `HalfEdgeMesh.Triangulated` fans from corner 0, so a quad's split is decided by where
  its cycle happens to start. Measured on a left-hand threaded rod, whose band is a
  sheared grid: it tessellates to the *identical* vertex set as the mirror of its
  right-hand twin — 0 of 131 200 vertices differ at 1e-9 — yet carries a systematically
  **3× larger volume deficit** at every density (RH deficit 0.668 vs LH 2.00 at 64
  segments; 0.167 vs 0.501 at 128). Both converge quadratically onto the same analytic
  volume, so this is a discretization *constant*, not a drift, and it is documented at
  the one assertion it forces wider (`ThreadShapeTests`, 2.5% band). A shortest-diagonal
  rule would improve every grid band, not just threads — but it moves rendered geometry,
  so it needs the 54 docs PNGs as its oracle.
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
- [ ] **Surface Nets mesh ASSEMBLY is now the dominant cost, not sampling.** With
  `SurfaceCull` landed, at res 256 a one-sqrt/sample field polygonizes in 132.8 ms vs
  129.3 ms for the real CSG field — evaluation is effectively free and
  `HalfEdgeMesh.Build` alone is 39–48%, the rest per-cell component maps, quad lists and
  the sample window. Further speedup belongs in assembly (e.g. building the half-edge
  structure from the known grid adjacency instead of the generic manifold-validating
  `Build`), not in the grid walk.
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
  - `Box(20,20,20) − Sphere(12)` stays out of the corpus: at each hole rim's u-extreme
    the rim tangent goes vertical, no level path anchors cleanly, and a narrow column a
    few steps tall remains for refinement — worst agreement 0.7024 at 48/24 against the
    0.9239 floor (locked with committed baselines in
    `SpherePiercingEverySide_HasNoFoldsAndABoundedResidual`). A per-column cut at the
    rim's turning vertex is the likely finish.
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
  Also (Frame3d work finding): bores drilled into extruded *side* faces miss the
  inscribed-ngon volume by ~5e-5 — the trimmed side-face triangulation differs from a
  planar cap's (documented in `SketchPlaneFrameTests.On_ExtrudedSideFace_DrillsIntoTheSide`).
- [ ] **A bore drilled into an extruded SIDE face misses the inscribed-ngon volume by
  ~5e-5, and the cause is now known: it is not the triangulation.** The bore's rim on
  that face is a **57-sample `PolylineCurve3d` baked in by the marching tracer** at
  boolean time, because plane-as-a-bounded-extrusion ∩ cylinder is not one of
  `SurfaceIntersection`'s analytic pairs — while the same hole drilled into the top cap,
  where the rim is an exact `Circle3d`, lands within **1e-13**. The fixed 57-gon is a
  floor no sampling density can lower, so the error does not converge and even changes
  sign as the analytic reference's n-gon crosses it: **−7.4e-4 / −5.3e-5 / +4.7e-5 /
  +6.5e-5 at 32/64/128/256** segments (cap: 1.8e-14 / 1.6e-13 / 1.7e-13 / 6.4e-14). The
  fix belongs in `SurfaceIntersection`, which already has the analytic plane∩cylinder
  circle and simply does not recognise the plane when it arrives as an `ExtrudedSurface`
  over a `Line3d` — the same promotion `TryPlanarPatch` does for the boolean's own
  section curves. Documented in
  `SketchPlaneFrameTests.On_ExtrudedSideFace_DrillsIntoTheSide`.
## Core (EngrCAD.Core)

- [ ] **`ShapeCompiler` coplanarity, and a finding under it** — the dot is now named
  (`CoplanarFaceCosine`, 0.081° = acos(1 − 1e-6)) but deliberately not widened: a dot
  of unit vectors is already scale-free, so the quadratic-scale argument does not apply.
  The real issue found while testing it: the companion `CoplanarFaceDistance` check
  measures the axial gap to an **arbitrary point of a tilted face's plane** (whatever
  `IsPlanar` reports as origin), so it is ill-defined precisely in the band a wider
  angle would admit. Needs coplanar-boolean evidence before touching.
- [ ] **`Fitting3d.MinVolumeBox`'s per-family angle is a sweep + golden section, not an
  algebraic root solve** (the OBB itself ✅ landed). O'Rourke derives the critical angle in
  closed form; worth doing if a hull ever shows a minimum hiding in a bracket narrower
  than the 3.75° sweep. The box always contains every input point regardless. Also: a
  convenience overload in EngrCAD.Mesh (`MinVolumeBox(HalfEdgeMesh hull)`) would spare
  callers the `ConvexHull.Compute(...).Triangulated().ToIndexed()` dance.
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
- [ ] **`Region2dBoolean.ContainedIn` is still O(cells × operand vertices)** — the next
  quadratic term after the clearance scan (now BVH-backed: a union of 120 overlapping
  32-gons went 436.2 → 93.6 ms, its classification phase 367.7 → 8.8 ms) and the
  arrangement's insertion, though an order of magnitude smaller in the cases measured.
  A per-operand `Region2d` point-location index would close it. **Still owed**:
  re-benchmark the arrangement broad phase on a quiet machine — the candidate-pair
  reduction is a solid 9.1%, but the wall-clock numbers were taken under load and
  disagreed by 3×.
- [ ] **`Bvh.Build` follow-ups** (the build ✅ landed 4.9× faster and bit-identical) —
  reusing a hierarchy across a boolean cascade is untried, and after the fix the broad
  phase is 10.0 ms of a ~199 ms exact union, so the remaining wins are elsewhere.

## B-Rep / sketching (EngrCAD.BRep)

- [ ] **Threads follow-ups** (B-Rep-native external threads AND threaded holes ✅
  landed — `HelicalSurface`/`SpiralArc3d`/`MakeThreadedRod`, boolean-free lateral
  sweep, clipped-pilot hole tool; **left-hand threads and the ISO 261 fine-pitch
  series** ✅ landed too) — remaining: (a) 45° end-chamfer cones in B-Rep
  (cone∩helical via tracer + trimmed helical tessellation); (b) clearance profiles in
  B-Rep (distance-field offsets round reflex corners — needs arc-generator helical
  bands); (c) helical∩cylinder and helical∩tilted-plane intersections + general
  trimmed helical faces (today only axis-perpendicular plane cuts of threads work,
  others fail loudly); (d) thread runout and cosmetic-thread annotation.
- [ ] **2D sketch engine residue** (the front door ✅ landed — `Region2d`
  polygon-with-holes with automatic nesting detection, `Region2dBoolean` over
  `Arrangement2d`, `Sketch.ToRegions`, `Profile.FromRegion`): **exact curved 2D
  booleans** (arcs and béziers carried through the arrangement as curves instead of
  being flattened at a chord tolerance — today everything built from a region inherits
  that flattening), `PolySimplification2`-style Douglas–Peucker simplification (only
  the exact-collinear pass landed), and `Region2d` self-intersection validation (a
  loop is checked against other loops but not against itself, so a self-intersecting
  outer loop produces garbage silently).
- [ ] **Sketch constraint follow-ups** (the variational solver ✅ landed —
  `Sketch.Constrain()`/`ConstrainedSketch`, full coincident/tangent/parallel/dimension
  vocabulary, analytic-Jacobian LM with rank-revealing DOF reports, drawn config as seed
  AND branch selector, refuse-loudly with named contradictions/stationary points):
  elliptical arcs in sketches; constraint serialization alongside feature history
  (deliberately not v1 — it does not fall out of the `[Param]` descriptor pattern);
  bézier constraints (tangency at bézier endpoints); point-on-arc/curve constraint.
- [ ] **Adopt biarc fits somewhere** (`BiArcFit.TryFitPolyline` ✅ landed and exercised,
  but nothing calls it). Candidates: an opt-in `SurfaceIntersection` post-pass (tracer
  polyline → arc chain when the deviation clears a caller tolerance), `StepWriter`
  emitting fitted arcs instead of degree-1 sampled polylines for
  `TransformedCurve(NurbsCurve)`, and lighter B-Rep seam edges. Each needs a policy
  decision about *who* owns the tolerance.
- [ ] **`ExtrudedSurface`/`RevolvedSurface` inverse evaluation refines from a single best
  seed** — the same defect `SweptSurface` just fixed. A generator whose projection into
  the reduced plane is near-degenerate hides two branches inside one seed interval and 1D
  Newton returns the *mirrored* parameter: on-surface, structurally valid, geometrically
  wrong. `SweptSurface.SolveGeneratorParameter`'s rule (refine from every local minimum
  *and its two neighbours*) ports directly. Deliberately not done with the fix, because
  these two carry the whole boolean regression surface.
- [ ] **`Curve3d.ArcLength`/`ParameterAtLength`** — the 2D family has adaptive-Simpson arc
  length with a bracketed-Newton inverse and a caching `ArcLengthTable2d`; the 3D side
  still has only per-type `Length()` on the conics and the helix.
- [ ] **2D curve ↔ `Sketch` bridge** — `Sketch` builds its own segment types and `Curve2d`
  is a parallel vocabulary. They should meet (a sketch segment exposing a `Curve2d`, or
  `Profile` accepting `Curve2d` chains) before either grows further.
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
  `FaceGeometry.ExactSampleParameters`/`IsPolylineBacked`.) — remaining:
  a cut chain that crosses a face boundary part-way (a pocket or glyph breaking out of
  a side face) throws `Open splitting curves must start and end outside the face`;
  flush/coplanar embossing does not fuse (the union leaves touching shells with the
  right volume — sink the tool a fraction to fuse); equal-radius perpendicular cylinders
  (tangent bicylinder: overlapping v-ranges rejected; the tracer's degenerate output
  there is untested). Also still open: coplanar/tangent boolean cases generally.
- [ ] **Trimmed cylindrical tessellation with WRAPPING loops** — the blocker behind the
  one wrap-split case still refused: a cross-drill piercing a plain `CylinderSurface`
  band makes a wrapping cut whose v varies, and its sub-bands keep the whole surface, so
  they need the trimmed path — which supports cylindrical faces only with *non*-wrapping
  loops. Refused by name today (the message points at the extruded-circle route, which
  does work); letting it through was measurably worse, since the split succeeded and the
  failure resurfaced three stages later as `Directed edge appears twice` from the mesh
  builder.
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

- [ ] **AMD/RCM fill-reducing ordering for `SparseCholesky`** — natural ordering was
  measured sufficient at deformation-ROI scale (≤ ~14k unknowns; see design.md §2), but
  FEA-scale stiffness systems will need it (62.5k unknowns: 1.6 s factor vs 24.5 ms CG).
- [ ] **Shape-level exposure of smoothing/deformation** — the tools are kernel-only
  (`EngrCAD.Mesh`); a `Shape.Smoothed(...)` graph node (mesh-Native, implicit-Bridged
  via `MeshSdf`, B-Rep-Impossible — the `Remeshed` precedent) plus docs-site example
  pages is the user-facing follow-up, owed when it lands per the docs rule.
- [ ] **Decal/engraving pipeline over the exp map** — `MeshLocalParam` gives per-vertex
  (u, v); wrapping a `Sketch`/glyph outline through it onto a curved surface (project
  curves into uv, map back, imprint) is the feature it was built to enable.

## Mechanisms (kinematics)

Mechanisms v1 landed (`Joints.cs`/`Mechanism.cs`/`Couplings.cs`/`HigherPairs.cs`/
`MateSolverRates.cs`/`MotionInterference.cs`; docs `examples/mechanisms.md`): joints as
a vocabulary over mates with DOF asserted against the solver's rank, drivers +
continuation sweeps, named dead centres, analytic velocities/accelerations,
gears/belts/cams, joint limits, interference over the sweep, swept volumes as Shape
nodes, and Grübler/Kutzbach as a cross-check. Remaining follow-ups:

- [ ] **Multiple simultaneous drivers** — `SolveAt` takes one driver; a 2-DOF mechanism
  (a cylindrical joint, a robot with two actuated hinges) wants a set of
  (driver, value) pairs per step. The residual machinery already supports N driver
  rows; the missing part is the API and the sweep over a parameter vector.
- [ ] **Joint/coupling persistence** — `MateSet.SaveMates` covers the mates but a
  reloaded file loses the joint layer (coordinates, limits, couplings, derived
  perpendicular references). Follow the FeatureHistory/mate conventions: a joints
  section referencing joints' ends by the same descriptors mates use.
- [ ] **Rack-and-pinion coupling** — z of one joint against θ of another
  (Δz = r·Δθ): the screw row generalized across joints; ten lines in
  `HigherPairs.cs` once someone needs it.
- [ ] **Cam refinements** — roller-follower radius compensation (offset the law by the
  roller radius along the profile normal), offset followers, and the classic
  dwell-rise-dwell laws (cycloidal, modified trapezoid) as `CamLaw` factories
  (trivial via `FromFunction`; the value is the catalogue, not the math).
- [ ] **B-Rep-exact interference volumes** — `CheckInterference`'s opt-in volumes use
  the exact MESH boolean of the meshes that flagged the clash; for B-Rep-backed parts
  a `BrepBoolean.Intersection` of the posed solids would report the exact volume, at
  the cost of a boolean between arbitrarily-rotated solids per range.
- [ ] **Adaptive swept-volume sampling** — `SweptVolume` unions the sweep's uniform
  frames; sampling by pose DELTA (bounded rotation × extent per step) would bound the
  scallop error instead of inheriting the study's frame count.
- [ ] **Flexible sub-assemblies in mechanisms** — inherited from the mates layer: a
  deep occurrence whose owning sub-assembly is placed more than once is refused (one
  shared frame). A mechanism inside a twice-placed sub-assembly needs per-placement
  frame overlays first.
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
scrubbing, playback, export and docs. What remains:

- [ ] **Web viewport transport** — the whole machine (`Animation`, `AnimationPlayback`)
  is UI-free in `Viewer.Core` precisely so the Blazor viewport can reuse it: a
  play/pause/scrub row driving the same per-instance matrices the desktop sends via
  `SetInstancePoses`. Needs only widgets and a `requestAnimationFrame` clock; no new
  evaluation code.
- [ ] **Pose-track composition** — an `Animation` deliberately takes at most ONE pose
  track (two full-instance-list producers cannot compose; whose matrices win?).
  Composing *relative displacement* tracks (mechanism pose ∘ explode displacement on
  top) is the principled extension — displacements compose where absolute pose lists do
  not.
- [ ] **Explode motion along the explode PATH** — `ExplodeTrack` lerps straight along
  `ExplodeOffset`; assembly instructions sometimes want dogleg paths (out, then over).
  Ties into the explode-path renderer item under Assemblies follow-ups.
- [ ] **Reuse one EGL context across an animation's frames** — `OffscreenRenderer.Render`
  creates and destroys a context per call, so a 36-frame export pays 36 context
  creations plus 36 mesh uploads. A batch render entry holding one context and one set
  of uploaded buffers (poses change per frame, buffers do not — the SetInstancePoses
  insight applied offscreen) should make exports several times faster.
- [ ] **`EngrCad.RenderToImage(scene, animation, t, ...)` sugar + an MCP `screenshot`
  `t` parameter** — a single evaluated frame as a still, so an AI assistant can ask for
  "the mechanism at t = 0.3". Both are thin: evaluate `At(t)`, pass the sample's
  instances/camera to the existing render; the MCP side wants a schema addition and a
  session test.
- [ ] **WebP animation** needs a VP8/VP8L encoder — not something to hand-roll; it
  means taking a dependency (libwebp or a managed port). Worth it only if the payload
  difference matters for the docs site (the committed APNGs are the size pressure to
  watch).

## Simulation

FEA as a first-class citizen of the hybrid kernel: the CAD model (any representation)
feeds the mesher, results feed back into the viewer as fields on the mesh. The mesh
engine's half-edge structure and the implicit engine's SDFs are both real assets here
(SDF-guided sizing fields, inside/outside tests via winding numbers).

- [ ] **Meshing for FEA** — volumetric (tet) meshing from any representation:
  surface mesh → tetrahedra (Delaunay refinement or advancing front; study TetGen/
  NETGEN-class algorithms), with quality controls (aspect-ratio/dihedral bounds,
  sizing fields — an `Sdf` makes a natural sizing/gradation field), boundary-layer
  preservation, and second-order (10-node) tets for accuracy. Hex-dominant or
  voxel/SDF-based meshing (cut cells from `Sdf.Sampled` grids) as an alternative
  route. Also: surface-mesh quality prep (isotropic remeshing above is a
  prerequisite) and region/attribute tagging (material per body, face groups for
  boundary conditions — B-Rep face identity → mesh facet tags).
- [ ] **FEA: structural (linear static)** — small-strain linear elasticity on tet
  meshes: element stiffness (linear + quadratic tets), assembly into sparse symmetric
  systems, boundary conditions from tagged B-Rep faces (fixed supports, loads:
  force/pressure/gravity), solve (start with `EngrCAD.Core.Solvers`' ✅-landed
  `SparseSymmetricCG`/`SparseCholesky` — note the AMD-ordering follow-up above, which
  FEA-scale systems will need), derive stress/strain (von
  Mises), display as color fields + deformed-shape overlay in the viewer. Modal
  analysis as a follow-on (eigen-solver).
- [ ] **FEA: thermal (steady-state + transient)** — heat conduction on the same tet
  meshes: conductivity matrix, boundary conditions (fixed temperature, heat flux,
  convection h·(T−T∞)), steady solve first, transient with implicit time stepping
  after; temperature fields in the viewer. Thermal→structural coupling (thermal
  expansion loads) once both exist.
- [ ] **Results/fields infrastructure** — scalar/vector fields on mesh vertices/cells,
  color-map rendering in the viewer (legend, min/max probes), export (VTK/VTU for
  ParaView interop), and a `Part`-level results attachment so simulation results live
  in the document model alongside geometry.

## OpenSCAD feature parity (open items)

What remains from mapping OpenSCAD's feature set against EngrCAD (the covered ground —
primitives, 3D booleans, transforms, linear/rotate extrude + RMF sweep, STEP/STL/OBJ/PNG
export — is recorded in CLAUDE.md):

- [ ] **Text follow-ups** (`Shape.Text` ✅ landed — dependency-free TrueType reader,
  glyphs → exact sketch segments, containment-based counter detection, layout with
  `kern` kerning; **CFF/OpenType-PostScript outlines ✅ landed** — `CffOutlines`, Type 2
  charstrings → cubic `BezierTo`, CID-keyed via FDArray/FDSelect, every `.otf` opens;
  **GPOS kerning ✅ landed** — `GposKerning`, PairPos 1+2 incl. Extension lookups, with
  the spec's GPOS-over-legacy-`kern` precedence): **text on a curve/path**
  (layout maps the pen position to a frame instead of a straight baseline); **variable
  fonts** (`fvar`/`gvar`, incl. `CFF2` — rejected loudly today); **`seac` accent
  composition** (legacy CFF accents — rejected loudly today, needs charset + standard
  encoding); **vertical alignment** for text blocks (horizontal-only today);
  **`TextFeature`** as a parametric `Feature` (the parameter snapshot must cover the
  font reference).
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
- [ ] mirror B-Rep completion, remaining nodes — revolve/sweep/rim/drill ✅ landed
  (axis negation `F∘R(d,θ)∘F = R(−F·d, θ)` for revolves, intrinsic RMF for sweeps,
  isometry-commuting surgery for rims/drills); still rigid-proper-only:
  `Draft` (pull direction needs the linear image under the reflection),
  `Shell(t, openings)`, `RoundEdges`, `Loft` — all isometry-commuting, each a small
  DecomposeSimilarity change plus tests when wanted
- [ ] **2D offset follow-ups** (`Region2dOffset`/`Sketch.Offset` ✅ landed — round/miter/
  chamfer joins, erosion as complement dilation; **open-path stroking ✅ landed** —
  `Region2dOffset.Stroke(path, width, cap, join)`, butt/round/square caps, both-side
  corner joins so reversals get round noses, closed circuits enclose holes): **exact
  curved offsets** (arcs stay arcs — today everything flattens first, same limitation
  as all region work); **variable offset along the outline** (per-vertex distances —
  trapezoid slabs + interpolated-radius joins on the same union construction; design
  question: how distances interpolate along an edge, linear-in-arclength being the
  obvious rule).
- [ ] **Twist-extrude follow-ups** (`Shape.Extrude(sketch, height, twist, scale, slices)`
  ✅ landed — taper = B-Rep-Native ruled loft, twist = direct mesh section sweep with
  twist-matched profile subdivision + collinear-chord-zip caps, implicit via mesh SDF):
  tapered sketches WITH holes are B-Rep-Impossible until loft sections support holes
  (same gap as the Loft section); an exact twisted B-Rep surface type would make twist
  Native (big kernel feature, low priority).
- [ ] **Planar-view follow-ups** (`PlanarSection.OfMesh`/`OfSolid`/`SilhouetteOfMesh` +
  `Shape.Section`/`Shape.Silhouette` ✅ landed — both OpenSCAD `projection` modes):
  - [ ] **`Region2dBoolean` leaves ~1e-7-area pinholes at near-tangency.** Repro: the
    64-segment torus silhouette viewed side-on. Areas are right to 6 significant figures
    and order-independent after quantization; it is the HOLE COUNT that is unreliable
    there, which is why the test asserts on hole area. A cell-classification fix, not an
    epsilon one.
  - [ ] **B-Rep silhouettes** — true silhouette curves on curved surfaces. Today the
    outline is always mesh-derived, so its fidelity is the mesh's however exact the solid.
  - [ ] **`OfSolid` on a flush plane** — a plane containing a face or an edge throws
    (that section is an area, not a curve). A proper answer needs coplanar-face handling,
    the same gap as coplanar booleans.
- [ ] `roof()` — straight-skeleton roof over a polygon; low priority
- [ ] **Camera-adaptive tessellation on zoom** — `TessellationQuality` ✅ landed (max
  angle + max chord deviation, per-solid resolution driving mesh AND feature edges);
  the follow-on is re-resolving against the on-screen pixel size of a radius when the
  camera zooms, which needs re-tessellation plumbing in the viewer.
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
- [ ] **DXF/SVG follow-ups** (v1 ✅ landed — `DxfDocument` LINE/ARC/CIRCLE/LWPOLYLINE
  with layers both ways, exact bulge arcs; `SvgDrawing` visible/hidden/section line
  classes over Section/Silhouette/Sketch): DXF SPLINE entities (cubic béziers now
  flatten on export), hidden-line classification computed from the model (needs HLR —
  today the caller says which class a curve set is), DXF units header ($INSUNITS).

## OpenCASCADE (OCCT) feature parity (open items)

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
    Remaining: curved-face shelling corners and variable-radius fillet corners still
    need the non-conic surface–surface corner curve.
  - [ ] **Partial-run follow-ups** — SETBACK terminations ✅ landed (`Filleting.
    FilletEdges/ChamferEdges` resolve contiguous partial runs; the termination is the
    planar band cross-section perpendicular to the terminal edge, the industry
    default; `Shape.FilletEdges/ChamferEdges` accept them). Remaining, refused by
    name: cliff and vertex-blend terminations (different surfaces), arc-terminal runs
    (the cylindrical neighbour needs periodic re-trimming), mid-EDGE stops (terminate
    at a parameter, not a vertex — needs an edge split first), and variable-setback
    laws on runs.
  - [ ] **Variable-radius fillets** (variable-SETBACK chamfers ✅ landed — `ChamferRim`/
    `ChamferRimAtAngle`/`ChamferEdges` law overloads + `Shape.Chamfer(setbackAt, faces)`
    + `VariableChamferRimFeature`; strips stay exact planes because a constant top:side
    ratio keeps the four corners coplanar). The radius case is blocked on the *corner*,
    not the band, and needs the same non-conic-corner-curve machinery as curved-face
    shelling.
- [ ] **`StepReader`: trim closed NON-circular generators** — circles ✅ landed (meridian
  arcs trim a closed circular revolve generator; congruent translated end arcs trim a
  closed circular extrusion generator; both closed form, so `FilletAllEdges` output now
  round-trips manifold with zero diagnostics). A closed NURBS generator under a partial
  sweep still keeps the honest non-manifold diagnostic — recovering it needs projection,
  not congruence, and nothing exports one today.
- [ ] **`BrepBoolean` on whole-solid fillets: band-crossing tools** — centre drills
  through the caps ✅ work (locked, Steiner-minus-bore exact). The band case is now
  precisely diagnosed (the old "re-surfaced sub-band loses corner arcs" story was
  wrong): the tool∩band tracer loop closes on the EXTENDED carrier; run pseudo-samples
  now reach the domain edge (adaptive extrapolation ✅ landed — one local gap was
  measured falling short, leaving zero crossing seeds), but `RefineCrossing` demands a
  true 3D intersection to 1e-11 and a chordal tracer polyline is a sagitta (~1e-4) off
  the exact tangency edge mid-chord — skew curves, every seed rejected, band never
  splits, whole band mis-classifies, result cracks along entire tangency edges (refused
  loudly; unpaired edges itemized). Remaining fix: tag tracer curves with their surface
  pair, refine boundary crossings as exact edge∩other-surface root solves, and SNAP the
  polyline segment ends to the exact crossing on BOTH solids (the segment endpoints are
  seam geometry — a 1e-4 chordal end would open a pinhole at every crossing).
- [ ] **Draft follow-ups** (`Draft.Apply` landed with per-face angles in one call, wired
  as `Shape.Draft`): curved faces; caps with holes; a non-planar neutral surface.
- [ ] **Shelling follow-ups** (`Shelling.Offset/Shell` landed with per-face wall
  thickness, wired as `Shape.Shell(t, openings)`): curved faces (a cylinder's or
  revolve's offset surface is analytic — `OffsetCurve3d` gives the generator — but their
  **corners** need surface–surface re-intersection, which is the *same* blocker as
  general trihedral fillet corner patches, so the two should be solved together);
  >3-valent vertices (over-determined corner, same machinery); adjacent openings (their
  shared rim has zero width — the two openings must MERGE into one rim loop, a topology
  pass, not new geometry); global self-intersection detection (deliberately unchecked
  today, as in OCCT and `OffsetCurve3d`). The `Shape` route exposes one thickness;
  per-face thickness and per-face draft angles stay kernel-level escape hatches
  (`Shape.From(...)`) until a selector-to-value vocabulary exists at the Shape level.
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
  face REGIONS — needs the coplanar-overlap machinery the mesh boolean has and the
  B-Rep boolean lacks; not cheap.
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
  1e-7.
- [ ] **`BrepSolid` one-call rigid transform** — `Transformed(Matrix4d)` rebuilding
  topology (the `Clone()` walk) with per-type geometry mapping: plane/cylinder/sphere/
  conic frames rigidly, extrude/revolve generators via `TransformedCurve` + mapped
  axes, NURBS by control points (the affine rule the STEP exporter now uses), swept
  surfaces by transformed path+profile. Assessed (task #11): well-bounded — the per-
  type curve mapping already exists in `StepWriter.Simplify` and the Modeling compiler
  bakes transforms per-type at lowering, so this is consolidation, not new math;
  restrict to rigid (+uniform scale where the type allows) and refuse shear by name.
  Nothing internal needs it today (lowering bakes transforms into construction
  inputs), which is why it stayed behind the STEP/healing items.
- [ ] **Per-part material in the document model** — `Part.MassProperties(density)` takes
  density as an argument because a `Part` has no material. A `Material` (name + density +
  display colour) on `Part` would make `scene.AllInstances.MassProperties()` a one-liner,
  and is the natural seed for the BOM and for Simulation.
- [ ] Topological naming / modification history (which output face came from which
  input face) — the foundation of parametric rebuilds surviving edits
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
- [ ] Data exchange: IGES, glTF, native BREP serialization format. Design assessment
  (task #11, each its own project): **IGES** is a legacy-only format (fixed-column
  Part 21-era encoding, entity soup, no product structure worth the name) whose one
  remaining use is receiving files from old CAM systems — if ever built, import-only,
  reusing the `StepReader` diagnostics conventions; do not write it. **glTF** is the
  opposite: mesh-plus-materials for the web viewer and downstream DCC tools — it
  belongs beside `StlWriter`/`ObjWriter` in the mesh export family (binary `.glb`, one
  buffer, per-part nodes with instance transforms from `PartInstance`, colors from
  `Part.Color`), no B-Rep semantics, and is the natural companion of the WASM viewer.
  **Native BREP serialization** should be the STEP writer's entity model dumped
  without the AP214 ceremony ONLY if a measured need (load time, exactness of swept
  surfaces STEP cannot carry) appears; the honest alternative — version the format
  from day one or don't ship it — is the whole cost.
- [ ] Hidden-line removal (HLR) projections for 2D drawings. Design assessment
  (task #11): the exact-algebra route (OCCT `HLRBRep`: project edges + silhouette
  curves, split at visibility changes, classify against every face) is a large
  project with the same robustness surface as the boolean; the pragmatic first rung
  is MESH-based HLR — project tessellation feature edges (`BrepFeatureEdges` already
  exists), depth-test segments against the triangle BVH (`Part` pick BVHs already
  exist), and emit visible/hidden per segment for the DXF/SVG layer item. That gets
  usable drawings at display fidelity now and leaves exact HLR as the upgrade whose
  seam (a list of classified 2D segments) is already right.
- [ ] OCAF-style document framework: undo/redo, attributes, persistence. Design
  assessment (task #11): do NOT port OCAF's label-tree/attribute model — this
  codebase's document model is `Scene`/`Tab`/`Part` with `FeatureHistory` as the
  parametric core, and its natural persistence is what already half-exists
  (`SaveParameters`/`LoadParameters`, `MatePersistence`): the missing piece is one
  serialized DOCUMENT envelope (scene structure + per-part feature history + mates +
  materials) with a version field. Undo/redo should ride the same seam as hot reload:
  a document snapshot is a value, an edit produces a new one, and the viewer swaps
  scenes — the `MeshChangeSet` journaling lesson (complete state, bit-identical
  restore) applies at document granularity rather than OCAF-style per-attribute
  deltas.

## build123d / CadQuery parity (open items)

Both are **OCCT front ends**, so unlike the OpenSCAD and OCCT sections above this one is
almost entirely about **API design, not kernel capability** — their contribution is how a
model is *expressed*, and the underlying operations are ones we largely have. Read them
for ergonomics, and copy capability rather than syntax: CadQuery's stringly-typed
selectors (`">Z"`, `"|Z and >Y"`) are the part to learn from and *not* imitate, because
`BrepQueries` + LINQ gives the same power type-safely. Landed from this section:
`BrepSelection` (the ordering/grouping layer + GeometryRef spellings), `LocationSet`,
`ExtrudeUntil`/`CutUntil`, `Packing`, and the builder-form prototype whose verdict (an
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
- [ ] **Drafting / dimensions** — build123d's `drafting` module (`Draft`,
  `DimensionLine`, `ExtensionLine`, `TechnicalDrawing`). We have 3D PMI annotations
  landed (`LinearDimension`, `RadialDimension`, `LeaderNote`, `DatumLabel` with
  auto-measuring selectors), so this is mostly a **2D drawing sheet** gap: dimensions
  laid out on a projected view rather than in model space. Pairs with the HLR item in the
  OCCT section — HLR gives the view, drafting gives the annotation on it.
- [ ] **Exporter breadth** — 3MF/AMF/OFF ✅ and DXF/SVG v1 ✅ landed (`ThreeMfWriter`/
  `AmfWriter`/`OffWriter` + `--export`/MCP wiring; `DxfDocument`/`SvgDrawing` with
  build123d's edge-classification line types); remaining: glTF, VTK, VRML.
- [ ] **Deliberately NOT taking**: string selectors (type-unsafe, and LINQ is strictly
  better in C#), Python-style implicit "pending" state carried between builder calls
  (hard to reason about and worse without context managers — confirmed by the builder
  prototype, design.md §6b), and the `Workplane` stack's history/rollback semantics
  (our `FeatureHistory` already covers regeneration properly and with typed
  parameters).

## Viewer

- [ ] Remaining docs-cutaway sweep: other example pages that fake cutaways with
  boolean subtractions (DocsGen `render:` fences now take `section:`/`style:`
  options — convert where the page reads better with a real section).
- [ ] **Section-isoline extraction still runs on the render thread** when the section
  toggle is first enabled (marching squares plus the first `TryGetSdf` lowering). It
  should stream the way ambient occlusion now does —
  `AmbientOcclusion.BakeInBackground` is the precedent.
- [ ] **`Part.ClippedBySection` has no UI** — a per-tree-row toggle beside the
  display-mode cycler; likewise no toolbar affordance for oblique section planes (hosts
  must set `ViewportControl.SectionPlanes` directly), and AO streaming reports only one
  status line rather than per-part progress in the tree.
- [ ] **A construction-preview docs example.** DocsGen snippets can now declare
  `sectionPlanes`/`sectionCombine`/`camera` alongside `scene` (which unblocked
  `section-oblique` and `section-unsectioned-fasteners`), but a construction-tree
  preview still has no headless entry point to render through — previews are built by
  the SceneHost on selection, not by anything `RenderToImage` can drive. Needs a
  `ConstructionPreviewRequest`-shaped seam in the offscreen path first.
- [ ] **3D-annotation (PMI) follow-ups** (v1 ✅ landed: `Annotation`/`LinearDimension`
  (point-to-point + `BetweenFaces` selectors)/`RadialDimension.OnEdge`/`LeaderNote`/
  `DatumLabel` + `HoleCallout`/`ThreadCallout` in Modeling; `StrokeFont` +
  `AnnotationLayer` billboarded rendering with offscreen parity; measure tool) —
  remaining ideas:
  - **Angular dimensions** (two planar faces or three points → arc + degree text)
    and ordinate/chain dimension styles.
  - **Occlusion-aware rendering** (v1 is always-on-top with the depth test off;
    depth-tested with a "hidden = dashed/dimmed" pass is the classic upgrade) and
    **pickable annotations** (select/highlight/edit from the viewport).
  - **Hole-table annotation** from a `Drill` call's point list (one balloon per
    hole, a table note keyed by letter), and cosmetic-thread auto-callouts:
    `Shape.ThreadedHole`/`Drill` could auto-attach `HoleCallout`/`ThreadCallout`
    notes (v1 generates them; attachment is manual).
  - **Multi-line note text** (the stroke-font layout is single-line; callout
    continuation lines currently join with spaces) and tolerance text sugar
    ("±0.1" via `Label` today).
  - Annotation persistence (JSON alongside `FeatureHistory.SaveParameters`) and
    STEP AP242 PMI export (far future).
- [ ] **Construction-tree follow-ups** (tree + per-node preview ✅ landed) — a
  **rollback bar** (drag a marker in the feature list; suppress below it),
  **suppress-from-tree**, and **`[Param]` editing** in the properties panel: all cheap
  now, since the rows already carry the `Feature`, its `Suppressed` flag and
  `ParamInfo`. Also: a preview clears on live reload because node references change —
  it could be restored by path.
- [ ] Idea: matcap shading (ambient occlusion landed).

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
- [ ] **The `ViewerModel` abstraction (Scene→render-instances shared by Avalonia,
  offscreen AND the web client)** — assessed during the step-2 move and deliberately NOT
  forced. What the three front ends genuinely share is already extracted (frame values,
  camera, modes, pick, widget geometry); what remains different is the *lifecycle*:
  the window streams uploads per part through `TabMeshLoader` (two threads), the
  offscreen pass is one-shot and synchronous, and the browser interleaves awaited JS
  uploads on one thread. A shared ViewerModel would have to abstract exactly that
  lifecycle, which is the part that must NOT look the same (the TabMeshLoader lesson).
  The honest next step is smaller: extract the *pure* per-part upload description
  (mesh + feature edges + wire edges + pick BVH, keyed by part reference) that all
  three build today by hand, and leave scheduling to each front end.
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
  which uniforms are which). **Remaining rungs**: construction-tree rows + rollback
  previews (needs `ConstructionPreviewCache`'s background-lowering story rethought for
  one thread), the measure tool (two picks → a transient dimension — `PickResult`
  already carries the world point), exploded views (`Scene.Instances(factor)` is
  front-end-free already), and a multi-plane section UI (the frame already takes
  `SectionPlane[]` + `SectionCombine`; isolines would then want `SectionClip.Siblings`
  per plane).
- [ ] **Docs-site embedding, the general form** — one page embeds the demo today
  (`docs/examples/web.md`). The payoff synergy is DocsGen emitting an interactive WASM
  viewer block *per example* instead of (or alongside) static PNGs — spin-the-model
  documentation, all statically hosted on the existing GitHub Pages deployment. Needs the
  scene-to-frame layer first, plus a way to ship one runtime shared by every embed rather
  than a 1.9 MB payload per page.
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
only via `SaveScreenshot`'s capture-on-next-frame). Remaining:

- [ ] **Untested**: a real third-party MCP client (Claude Desktop/Code) connecting —
  the protocol was driven by hand and via the SDK's own client.
- [ ] **The windowed RPC path needs one manual pass**: transport, vocabulary, and the
  bridge are locked headlessly over real sockets with a stub viewer, but
  `ViewportRemoteViewer` against a real window (UI-thread marshaling under a live
  dispatcher, `SaveScreenshot` actually writing) has no automated test — drive
  `samples/EngrCAD.LiveDemo --rpc` once per release, or build a windowed
  integration test on the `SendInput` harness.
- [ ] **`viewer_screenshot` returns a path, not pixels** — the capture lands on disk
  via the window's next frame. Returning the PNG as an MCP image block needs a
  completion signal from the render pass back to the RPC thread (the status callback
  carries the path today); worth it if assistants use the tool blind.
- [ ] **Option (c) — viewer hosts MCP directly over HTTP+SSE** stays parked unless the
  bridge process proves annoying in practice.
- [ ] **Persisting session edits**: `set_param` edits die with the session by design
  (source is the truth). A `save_parameters` tool writing
  `FeatureHistory.SaveParameters` JSON next to the model would let an assistant hand
  its tuning back to the user as a file.
  (Packaging is settled: `src/EngrCAD.Mcp` is its own package on
  `ModelContextProtocol.Core`, so viewer and kernel consumers inherit nothing.)

## App layer / infrastructure

- [ ] **Parametric features follow-ups** (`FeatureHistory` landed; typed geometry
  inputs landed — `GeometryRefs.cs`: `PlaneRef`/`FaceRef`/`FaceSetRef`/`EdgeSetRef`/
  `AxisRef` with cardinality in the type, descriptor-as-cache-key-as-serialized-form,
  per-`Apply` resolution, and `ValidateInputs` naming the failing property) — persistent
  topological IDs (selectors are the naming story today), property-panel UI editing of
  `[Param]`s, feature list in the viewer model tree, a feature registry for UI
  insertion.
- [ ] **Geometry-reference vocabulary follow-ups** — the named queries cover what the
  standard features need and no more. Wanted next: `PlaneRef.Offset(distance)` and
  `PlaneRef.Rotated` (an offset construction plane is the commonest missing one);
  `FaceSetRef.Largest` / `SmallestArea` (needs a face-area query — `BrepQueries` has
  none, and a curved trimmed face's area is not free); `FaceSetRef.Touching(point)` and
  `.AdjacentTo(faceRef)`; radius/length *ranges* rather than exact values (today
  `Cylindrical(r)` and `Circular(r)` compare at the weld tier, which is right for
  exactly-constructed geometry and useless as a filter); a `VertexRef`. Also: the
  `Shape` API's own selector overloads still take raw `Func`s — `FaceSetRef.AsSelector`
  bridges them, but `Shape.Fillet(radius, FaceSetRef)` overloads would let a design
  outside a feature history use the same vocabulary, and `Draft`/`Shelling`'s per-face
  predicates could take one too.
- [ ] **Assemblies follow-ups** (v2 landed: BOM, exploded views, mates — now ACROSS
  assembly levels with typed `FaceRef`/`AxisRef` references and
  `SaveMates`/`LoadMates` persistence — STEP assembly export + import, tree
  expand/collapse, retro-assigned palette colors) — true GPU instanced drawing (matrix
  buffer, one draw per part), per-instance color/display-mode overrides, an
  **explode-path renderer** (the dashed leader lines drafting standards draw between an
  exploded part and its seat), and **flexible sub-assemblies**: a deep mate target
  inside a multiply-placed sub-assembly is refused today because its internal frame is
  one shared object — per-instance internal DOF (Onshape's "flexible" instances) needs
  instance-specific frame overlays on the flatten seam, a real design task. Mechanisms
  (above) can now assume cross-level mates exist: a linkage whose members are
  sub-assemblies is jointable via occurrence paths.
- [ ] **Standard component library — breadth and fidelity** (v1 landed:
  `HardwareComponent` + `ComponentFeature` + `ComponentAssembly`; ISO 4762 SHCS, Tappex
  Trisert, ISO 2338 dowel; the full two-body fastener stack). Follow-ups: more families
  (ISO 7380 button, ISO 10642 csk, nuts, washers, bearings); higher body fidelity (hex
  socket recesses, a modeled thread on the shank via `Shape.ExternalThread`,
  knurled/flanged inserts, ISO 2338's crowned pin ends); and stacks that anchor into a
  *placed component* — today `PlaceThrough` always cuts the screw's own tapped pilot in
  the far body, so anchoring into an insert means placing the insert separately.
- [ ] **Frame3d enabled next steps** (the `TopPlane` behaviour question is settled: both
  conventions are now expressible — `PlaneRef.TopPlane` keeps world (0,0,z) origins,
  `PlaneRef.OnTopFace` gives the face's own frame — so it is a per-feature choice rather
  than a global decision) — arbitrary section planes from a frame; `StepWriter` emitting
  AXIS2 placements via `Frame3d`; Part poses as frames (assemblies above).
- [ ] **Parametric model layer / scripting** — fluent C# builder over the retained
  document model; `.csx` scripting via Roslyn (C# *is* our SCAD language); reusable
  parametric components as plain C# methods — document the pattern.
- [ ] **Logging follow-ups** (`ILogger` adoption ✅ landed — the `IEngrCadLog` shim is
  gone, `EngrCAD.Viewer`/`EngrCAD.Mcp` take
  `Microsoft.Extensions.Logging.Abstractions`, every message is a source-generated
  `[LoggerMessage]` template with a stable event ID, and levels now distinguish a
  skipped part (Warning) from a failed export (Error)) — remaining: **extend inward**
  with an optional `ILogger` on the long-running kernel operations, alongside the
  existing `ProgressCancel` (booleans, `BRepTessellator`, `MeshSdf`/winding builds,
  STEP import). That means the kernel projects take the abstractions reference too;
  weigh it per project rather than blanket-adding it. Keep diagnostics that are
  *results* as return values — `StepReadResult.Diagnostics`, `MeshRepair`'s reports
  and `Explain`'s node report are data the caller acts on, not log lines; logging
  complements them rather than replacing them.
- [ ] Sheet metal (bend allowances, flanges, unfold) — big, separate domain.
- [ ] nuget.org publish — `Directory.Build.props` URLs are placeholders; a real remote
  exists (github.com/veggielane/EngrCAD). GitHub Pages needs Settings → Pages →
  Source: GitHub Actions enabled once, then a push deploys the docs site.

## Not worth adopting (deliberate)

- g3's mesh structure itself (index+edge-list) — our half-edge with explicit boundary
  half-edges is a deliberate different choice; adopt its *editability mechanisms*, not
  the structure.
- 2D-only NURBS — we already have 3D NURBS curves/surfaces.
- g3's subdivision gap — it has no Loop/Catmull-Clark; we already have Loop.
- Skeletal-*field* convolution blends (`SkeletalBlend3d`/`SkeletalRicciBlend3d`) —
  they operate on 0..1 skeletal fields, not signed distances, and would break the
  implicit engine's sign-exactness contract.
