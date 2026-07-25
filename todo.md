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
`MeshDecimator` on `EditableMesh`, iterative BSP walks. Remaining:

- [ ] **Retire the BSP boolean.** The stack overflow is fixed (every `CsgNode` walk is
  explicit-stack), but fixing it produced the measurement that settles the question: a
  32k+32k sphere union takes **74.9 s** and returns an **open** 347k-face shell, against
  **0.71 s** closed for the exact path. Once coplanar overlaps are classified, delete
  `Csg.cs` and `BooleanMethod` outright rather than maintaining two algorithms.
- [ ] **Region refinement across a seam** — `MeshRegionOperator` deliberately refuses a
  replacement whose seam was re-split (splitting a seam edge leaves the neighbour face
  holding the un-split edge — a T-junction), so `MeshDecimator` round-trips but
  `LoopSubdivision` does not. Refining across a seam means refining the neighbours too:
  a different, larger operation.
- [ ] **SDF projection target for remeshing** — implement `IProjectionTarget` over
  `MeshSdf`/`Sdf` in EngrCAD.Interop (p − d(p)·∇d(p)); the interface lives in
  EngrCAD.Mesh precisely so the mesh kernel needn't depend on the implicit engine. Pairs
  with quality control after Surface Nets output.
- [ ] **`RemesherPro`'s scheduling** — the modified-edge queue and the fast-split
  prepass. The basic pass converges in tens of ms at current sizes, so this is throughput
  for large meshes only; note that queued edge ids are recycled, so every consumer must
  re-validate (the same hazard that put constraints on vertices).
- [ ] **Face-aligned (RZN-flow) sharp-edge reprojection remesh** — g3's
  `RemesherPro.SharpEdgeReprojectionRemesh`: per-triangle rigid repositioning onto an
  ORIENTED projection target with area × (n·n′)³ blending. Needs `IProjectionTarget` to
  grow an oriented overload.
- [ ] **Region-restricted remeshing** — remesh a face selection in place instead of the
  whole mesh (g3's `RegionRemesher`). `FillSmoothed` works around this by remeshing a
  standalone patch and stitching; overlaps with the region-refinement item above.
- [ ] **Move `ClosestPointOnTriangle` into EngrCAD.Core** — Ericson's exact
  Voronoi-region form is private in `MeshProjectionTarget`, and Interop's `MeshSdf`
  almost certainly has a second copy. It belongs beside `Fitting3d`.
- [ ] **Decide `HoleFillOptions.Fallback`'s default** — it ships as `None` to keep
  `FillAll`'s landed "report what you cannot fill well" contract; `Minimal` is arguably
  the better product default for `MeshRepair.AutoRepair`, at the cost of three tests that
  currently pin `Skipped` outcomes.
- [ ] **Expose remeshing through `Shape`/`Part`** (display quality, FEA prep) — and when
  it lands it owes a `docs/examples` page, since today it is kernel API reachable through
  no `Shape` operation.
- [ ] Mutable in-place variants of fill/extrude once callers want them.

## Implicit engine (EngrCAD.Implicit)

- [ ] **Arc distance without `Atan2`** — `SketchRegion`'s lane-wise kernels cover lines
  and full circles; *partial* arcs stay scalar because `ArcSeg.Distance` decides in-sweep
  via `Math.Atan2`, which has no bit-exact vector form. A cross/dot wedge test would
  vectorize, but it changes the boolean at the sweep boundary, so it needs its own
  exactness argument rather than a transcription. Same for béziers, whose control points
  `SketchRegion` cannot reach today (private to `CubicSeg`).

## Interop / meshing (EngrCAD.Interop)

- [ ] **Continuation ("surface-following") Surface Nets** — the slab-streaming sampler
  fixed the *memory* wall (peak is O(n²) now, so resolution 1024 is reachable) but still
  evaluates every grid corner. `MarchingCubesPro`'s idea of only visiting cells near the
  discovered surface is the remaining win, and the slab walk is a natural place to hang it.
- [ ] **Packet nearest-triangle query for `MeshSdf`** — 74–85% of a mesh narrow band's
  wall clock is inside `Bvh.Nearest`. *Seeding* the branch and bound with the previous
  coherent sample was built, verified bit-identical and measured at only 1.12–1.20× (a
  nearest-first search is already its own seed) — see `MeshSdfBatchTests` for the numbers
  and the reverted approach; **don't redo it**. The untried lever is a packet query: one
  traversal per coherent block collecting the candidate triangles for all its points at
  once, then a short per-point scan, which attacks node-test cost rather than the initial
  bound. Needs care over tie-breaking (equidistant triangles must resolve to the same one
  `Bvh.Nearest` picks) and a fallback when the candidate list blows up.
- [ ] **Trimmed-band gaps left by the strip path** (`TrimmedFaceTessellator`). The zip
  handles single-loop bands with single-sample rungs; three cases still ear-clip or
  refuse, each cheap on its own:
  - A **rung sampled at more than two points** (a curved cross edge) is refused rather
    than fanned — fanning collinear rung samples would emit the very zero-area triangles
    the strip path exists to avoid. The fix is to treat a multi-sample rung as a
    degenerate chain end and fan from the opposite chain's first vertex, with the same uv
    positive-area guard.
  - A band whose two chains **meet at a point** (a rung of zero steps) falls back; the
    merge walk needs a shared-apex start case.
  - **Bands with interior hole loops** still ear-clip (`TriangulateBandWithHoles`). The
    slivers there run along hole rims, whose geodesic curvature is generally nonzero, so
    this is not currently visible — but it is the same defect waiting.
- [ ] **Trimmed-face refusals are now loud — find out what they refuse.** Two documented
  gaps used to fall back to the grid silently and now throw: pole-bounded single-chain
  bands with holes, and |winding| > 1 loops. Nothing in the suite or the docs hits them,
  so we do not know whether they are reachable from the `Shape` API at all. Build a repro
  for each (a drilled sphere pole cap; a band cut so its loop wraps twice) and either
  support them or refuse them at construction time in `Shape`.
- [ ] **A per-face triangle-quality assertion for the whole tessellator.** The mitered
  fillet fold was invisible to every existing test — closed, Euler-clean, volume within
  tolerance — because orientation was never checked. `MiteredBandTessellationTests`'
  `FoldReport` helper generalizes to any solid: run it over the whole B-Rep corpus
  (drilled plates, cross-drills, threads, lofts, shells) as one parameterized test.
- [ ] **Refinement quality upgrade** — Rivara-with-boundary-constraints instead of the
  monotone-decrease rule's worst-sliver tradeoff; no Delaunay flips. Lower priority now
  that the base triangulation carries the accuracy rather than the refinement. Also
  (Frame3d work finding): bores drilled into extruded *side* faces miss the inscribed-ngon
  volume by ~5e-5 — the trimmed side-face triangulation differs from a planar cap's
  (documented in `SketchPlaneFrameTests.On_ExtrudedSideFace_DrillsIntoTheSide`).

## Core (EngrCAD.Core)

- [ ] **Remaining tolerance follow-ups** (named seam constants, `ConvexHull2` →
  `Orient2dSign`, `TracerSettings`, and the scale-relative `Sketch` guards ✅ all
  landed): **BSP `Csg.Epsilon` 1e-9 and `MeshWelder`'s 1e-7 absolutes → extent-scaled**
  — deferred while the exact boolean's coplanar handling is in flight, since re-tuning
  BSP's seam epsilons underneath that would confound both; boolean seam re-testing
  required when it happens.
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
- [ ] **Core follow-ups** — thread `ProgressCancel` through the mesh booleans
  (`MeshMeshCut`); `BRepTessellator` ✅ landed, and `MeshSdf`/winding builds were measured
  (21.8 ms / 29.2 ms on 32 040 triangles) and deliberately declined, since viewer
  cancellation is granular to a whole part. Also: `Part.GetMesh` should pass its
  `ProgressCancel` to `BRepTessellator.Tessellate` (one line in
  `EngrCAD.Modeling/Document.cs`, plus relaxing the doc paragraph there — only the
  *lowering* must run to completion, not the tessellation of an already-cached solid);
  intersection-segment queries over `Bvh.QueryOverlap` pairs (the triangle–triangle
  layer belongs to EngrCAD.Mesh); routing `FaceSplitter`'s planar tracing through
  `Arrangement2d` (deferred — boolean-critical); optionally migrate
  `MeshWindingNumber` onto `Bvh`'s per-node ranges.
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
  sweep, clipped-pilot hole tool) — remaining: (a) 45° end-chamfer cones in B-Rep
  (cone∩helical via tracer + trimmed helical tessellation); (b) clearance profiles in
  B-Rep (distance-field offsets round reflex corners — needs arc-generator helical
  bands); (c) helical∩cylinder and helical∩tilted-plane intersections + general
  trimmed helical faces (today only axis-perpendicular plane cuts of threads work,
  others fail loudly); (d) left-hand threads (negative pitch / mirrored lowering);
  (e) fine-pitch series, thread runout, cosmetic-thread annotation.
- [ ] **2D sketch engine residue** (the front door ✅ landed — `Region2d`
  polygon-with-holes with automatic nesting detection, `Region2dBoolean` over
  `Arrangement2d`, `Sketch.ToRegions`, `Profile.FromRegion`): **exact curved 2D
  booleans** (arcs and béziers carried through the arrangement as curves instead of
  being flattened at a chord tolerance — today everything built from a region inherits
  that flattening), `PolySimplification2`-style Douglas–Peucker simplification (only
  the exact-collinear pass landed), and `Region2d` self-intersection validation (a
  loop is checked against other loops but not against itself, so a self-intersecting
  outer loop produces garbage silently).
- [ ] **2D sketch constraint solver** — sketching landed geometry-only by design; the
  Onshape-style layer on top is constraints (coincident/tangent/parallel/dimensions)
  solved variationally. Also future: elliptical arcs, sketch offset/thicken.
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
- [ ] **Drill follow-ups** — drill-tip angles, thread cosmetics/annotation, hole tables.
  Also **cross-PLANE hole validation**: spacing is cross-validated only among drills
  sharing a placement plane, so opposing bores on the two faces of a plate can still
  produce intersecting tools. Needs a tool-vs-tool solid intersection test rather than a
  2D centre-distance one.
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
- [ ] **Boolean/splitting edge cases** (all now LOUD rather than silent — sketch-
  extrusion pockets/slots/engraving are exact as of the bounded-planar-carrier fix) —
  a cut chain that crosses a face boundary part-way (a pocket or glyph breaking out of
  a side face) throws `Open splitting curves must start and end outside the face`;
  flush/coplanar embossing does not fuse (the union leaves touching shells with the
  right volume — sink the tool a fraction to fuse); extruded-line × cylinder/sphere/
  revolved pairs still march, so a **bounded conic-clipping tier** would extend the win
  the planar tier just delivered; equal-radius perpendicular cylinders (tangent bicylinder:
  overlapping v-ranges rejected; the tracer's degenerate output there is untested);
  `CylinderSurface` bands can't wrap-split (tools lower to extruded circles today, but
  a raw `MakeCylinder` cross-drill tool would throw); `CurveSegment`-over-polyline
  edges aren't special-cased in `BRepTessellator.SampleEdge`; `TraceFaces` angle
  probes sample at 2%/98% of edge domains (off-surface for polyline-backed coedges).
  Also still open: coplanar/tangent boolean cases generally.

## Deformation / analysis (new territory, lower priority)

- [ ] **Laplacian smoothing & deformation** — `LaplacianMeshSmoother`,
  `LaplacianMeshDeformer` (handle-based), backed by `SparseSymmetricCG` /
  `CholeskyDecomposition` / `PackedSparseMatrix`. A solvers mini-library would also
  serve future constraint solving in sketches and the FEA items below.
- [ ] **Local parameterization / curves-on-mesh** — `MeshLocalParam` (discrete
  exponential map), `MeshIsoCurves` (iso-contours of a scalar field on a mesh),
  `DijkstraGraphDistance` (approximate geodesics). Enables engraving/wrapping features.
- [ ] **ICP registration** — `MeshICP` for aligning imported scans to models.

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
  force/pressure/gravity), solve (start with the `SparseSymmetricCG`/Cholesky solvers
  from the deformation item — shared solver mini-library), derive stress/strain (von
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
  `kern` kerning): **CFF/OpenType-PostScript outlines** (`CFF ` table, cubic Béziers →
  `BezierTo`) — rejected loudly today, and supporting it opens every `.otf`; **GPOS
  kerning** (modern fonts ship kerning only there); **text on a curve/path** (layout
  maps the pen position to a frame instead of a straight baseline); **variable fonts**
  (`fvar`/`gvar`); **vertical alignment** for text blocks (horizontal-only today);
  **`TextFeature`** as a parametric `Feature` (the parameter snapshot must cover the
  font reference).
- [ ] `surface()` — heightmap (image/data grid) → mesh terrain
- [ ] `minkowski()` — general Minkowski sum is hard; the important special case is
  rounding, which we already have cheaply (SDF `Offset` ≡ sphere-Minkowski, and
  `Filleting`). Document the equivalence; general polyhedron⊕polyhedron is low priority
- [ ] `BrepSolid` one-call transform story (`TransformedCurve` exists; add
  `TransformedSurface` or per-type transforms; `HalfEdgeMesh.Transformed(m)` ✅ landed
  with winding flip)
- [ ] mirror B-Rep completion — mirrored revolve/sweep/rim/drill nodes are Impossible
  in v1 (exact via mesh/SDF); native route: `F∘R(d,θ)∘F = R(−F·d, θ)` axis negation
  for revolves/sweeps (`Shape.Mirror` ✅ landed otherwise: implicit exact via
  improper-similarity decomposition, mesh exact, B-Rep native for
  box/cylinder/extrude/sphere/torus/cone)
- [ ] `resize()` — non-uniform scale to target bounds (mesh: easy; SDF: breaks the
  distance metric — document lower-bound semantics; B-Rep: needs affine surfaces)
- [ ] **2D offset follow-ups** (`Region2dOffset`/`Sketch.Offset` ✅ landed — round/miter/
  chamfer joins, erosion as complement dilation): **exact curved offsets** (arcs stay
  arcs — today everything flattens first, same limitation as all region work); variable
  offset along the outline; open-path offsetting (a stroke, for toolpaths).
- [ ] `linear_extrude(twist, scale, slices)` — twisted/tapered extrusion (a
  `SweptSurface` variant with per-v rotation/scale; g3's `GenCylGenerators` is the
  mesh route)
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
- [ ] **`TessellationQuality` options type** — unify `segmentsPerCircle`/
  `curveSamples`/`resolution` into one type (max angle, max chord deviation, min/max
  segments) with **adaptive** curvature-based sampling ($fn/$fa/$fs, and OCCT's
  deflection-based `BRepMesh` criterion)
- [ ] Debug modifiers (`#`/`%`/`!`/`*`) — per-body display flags (ghost/isolate/hide;
  highlight exists via selection)
- [ ] `$t` animation — time-parameterized models; viewer re-tessellates per frame
- [ ] model-validation report (volumes, bounds, manifoldness per body) in the viewer —
  the `assert/echo` analog
- [ ] export 3MF / AMF (zip+XML; 3MF is the modern printing format), OFF
- [ ] `Shape.From(path)` import sugar — the engine layer ✅ landed (`MeshReader` STL/
  OBJ/OFF + `MeshRepair.Clean` + `ReadAndRepair`); wrap it in Modeling for user-facing
  import, then a docs-site example becomes executable (write-with-StlWriter →
  dirty-in-memory → ReadAndRepair)
- [ ] import/export DXF + SVG (2D profiles in/out; SVG also useful for drawings)

## OpenCASCADE (OCCT) feature parity (open items)

What remains against the reference B-Rep kernel (covered: primitives,
extrude/revolve/sweep, booleans, rim fillets/chamfers, drilled holes, conics + offset
curves, curve interpolation, projection/extrema, surface intersection, STEP
export+import, volume/area, tessellation — see CLAUDE.md):

- [ ] **Loft follow-ups** (`SolidFactory.Loft` landed — cardinal-basis `LoftedSurface`,
  smooth/ruled, exact prismatoid volumes): section compatibility by degree elevation +
  knot merging (mismatched segment counts are rejected today); holes in sections; open
  uncapped skins; periodic lofts closing back on the first section; guide curves/spine.
- [ ] **Pipe shell with evolution law** — a loft whose sections are *generated* (scaled
  or twisted along a spine) rather than given. Now only needs a law evaluator: feed the
  generated `Profile`s to `Loft` and it lands on `LoftedSurface` unchanged.
- [ ] Boolean extras: *section* (curve-only result), fuzzy tolerance, modification
  history
- [ ] **Fillet follow-ups** (sharp-corner miters, edge sets, chamfer angles and
  whole-solid `FilletAllEdges` ✅ landed) — all of these are refused loudly today, so
  they are additions, not bug fixes:
  - [ ] **General trihedral corner patches** — `FilletAllEdges` requires one incident
    face perpendicular to the other two, which is exactly when the spherical triangle
    reduces to a lune closed by an equatorial great circle (an exact surface of
    revolution). The general case needs a trimmed spherical-triangle path. A tetrahedron
    is the smallest failing example.
  - [ ] **Partial edge runs** — a band that stops mid-rim needs a termination surface
    (cliff, setback or vertex blend) and each exact one is a different surface.
  - [ ] **Variable-SETBACK chamfers first, then variable-radius fillets.** The setback
    case is cheap (the corner segment is a boundary ruling of both bilinear strips); the
    radius case is blocked on the *corner*, not the band, and needs the same
    non-conic-corner-curve machinery as curved-face shelling.
  - [ ] **Sharp corners at ARC rim edges** (torus ∩ cylinder is not a conic).
  - [ ] **A `Shape.RoundEdges(radius)` node** wiring `FilletAllEdges` into the graph:
    `ShapeNodes.cs` plus four spots in `ShapeCompiler.cs` (`ClassifyBrep`, the implicit
    `or` list, `LowerBrep`, `LowerImplicit`). Today the Shape-level route is
    `Shape.From(Filleting.FilletAllEdges(shape.ToBrep(), r))`.
- [ ] **`StepReader`: trim a closed generator from meridian boundary arcs** —
  `FilletAllEdges` output EXPORTS correctly (a STEP `SURFACE_OF_REVOLUTION` is unbounded
  by definition and the face boundary trims it), but re-import cannot re-trim a closed
  generator when the swept angle came from rails: the corner patches' meridian boundaries
  are circles *through* the axis, which no rim rule recognizes, so a re-imported rounded
  solid meshes non-manifold. `RecoverRevolvedSurface` says so in a diagnostic rather than
  failing silently. Mitered rim fillets round-trip fine.
- [ ] **`BrepBoolean` on whole-solid fillets** — a fragment's re-surfaced sub-band loses
  the corner arcs from its domain. The solid itself is sound (a locked test checks every
  loop point projects inside its own face's domain), so this is a boolean limitation.
- [ ] **Draft follow-ups** (`Draft.Apply` landed — exact plane rotation about the
  neutral line, composable, planar/extruded faces): curved faces; caps with holes;
  per-face angles in one call; a non-planar neutral surface.
- [ ] **Shelling follow-ups** (`Shelling.Offset/Shell` landed — exact for polyhedra,
  sealed-void and genus-1-tube cases Euler-clean): curved faces (a cylinder's or
  revolve's offset surface is analytic — `OffsetCurve3d` gives the generator — but their
  **corners** need surface–surface re-intersection, which is the *same* blocker as
  general trihedral fillet corner patches, so the two should be solved together);
  >3-valent vertices (over-determined corner, same machinery); adjacent openings; variable per-face
  thickness; global self-intersection detection (deliberately unchecked today, as in
  OCCT and `OffsetCurve3d`).
- [ ] **Wire loft / draft / shelling into the `Shape` API + docs** — they are kernel-only
  today, which by this project's own rule means they are not done. Each needs a
  `ShapeNodes` node, a `ShapeCompiler` arm with honest `Explain` messages
  (`Shape.Loft(sections, style)`, `Shape.Draft(angle, neutral, faces)`, a B-Rep-native
  `Shape.Shell` beside the SDF one), and a `docs/examples` page with a render fence.
- [ ] Feature operations (`BRepFeat`): pocket, boss, rib, slot as first-class features
  with faces-to-remove semantics
- [ ] **Shape-healing follow-ups** (`ShapeHealing.Heal/Analyze` ✅ landed — six passes,
  every repair a return value):
  - [ ] **Geometric gap closing for CURVED edges** (OCCT `ShapeFix_Wire::FixGaps`) —
    re-fit or trim a circle/NURBS through its unified endpoints. `RefitStraightEdges`
    covers lines (a line is determined by its endpoints); curved gaps are counted in
    `Notes` and left, because inventing curve geometry is a modelling operation, not a
    repair. Probably wants per-entity tolerances on `BrepEdge`/`BrepVertex`, which this
    topology does not carry.
  - [ ] **Face-orientation and shell repair** (OCCT `ShapeFix_Shell`) — outward-normal
    voting per connected component and splitting a shell whose faces form several
    components: the B-Rep counterpart of `MeshRepair`'s winding flood.
- [ ] Local operations: split shape by shape, glue faces
- [ ] Surface interpolation + least-squares approximation (`GeomAPI_PointsToBSpline`
  proper; curve interpolation exists)
- [ ] Ray-parity B-Rep point classifier (drop the `MeshSdf` bridge in booleans)
- [ ] **Exact-surface mass-property quadrature** (OCCT `BRepGProp` + `GProp_Domain`) —
  mass properties ✅ landed by tessellate-then-sum with Richardson extrapolation (1.9e-7
  relative on a cylinder at default quality). Exact quadrature is worth doing only
  *after* trimmed parameter-space boundaries become exact, since the domain scan is the
  accuracy limit, not the quadrature. Would make analytic primitives exact rather than
  1e-7.
- [ ] **Move `SymmetricEigen3` from internal to public in EngrCAD.Core** and delete the
  duplicated cyclic-Jacobi solver in `EngrCAD.Mesh/MassProperties.cs`. Core's sorts
  descending, `MassProperties.Principal()` wants ascending — expose both orderings or
  sort at the call site. Also consider moving `SymmetricTensor3` to Core, where a
  symmetric 3×3 type belongs.
- [ ] **Per-part material in the document model** — `Part.MassProperties(density)` takes
  density as an argument because a `Part` has no material. A `Material` (name + density +
  display colour) on `Part` would make `scene.AllInstances.MassProperties()` a one-liner,
  and is the natural seed for the BOM and for Simulation.
- [ ] Topological naming / modification history (which output face came from which
  input face) — the foundation of parametric rebuilds surviving edits
- [ ] STEP follow-ups — unit scaling (mm assumed today); CONICAL/TOROIDAL_SURFACE
  synthesis as `RevolvedSurface`; `StepWriter` exact `TransformedCurve(NurbsCurve)`
  export by transforming control points (currently sampled to degree-1 polylines —
  blocks exact round-trip of NURBS-profile extrusions); export mapping for the new
  conics (PARABOLA/HYPERBOLA/OFFSET_CURVE_3D — sign conventions verified compatible);
  `Parabola3d.ToNurbs()` (trivially exact quadratic Bézier); import bisections run a
  fixed 100 iterations (exact but wasteful, import-time only)
- [ ] Data exchange: IGES, glTF, native BREP serialization format
- [ ] Hidden-line removal (HLR) projections for 2D drawings
- [ ] OCAF-style document framework: undo/redo, attributes, persistence

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
- [ ] **Chord-deviation tessellation for large parts** — investigated, and the obvious
  premise was WRONG: a fixed 96 segments/circle for feature edges is scale-*free*
  (relative sagitta 5.4e-4 at any radius) and a 400 mm flange's rim renders smooth at
  whole-part framing. Zoomed onto a large rim, what actually shows is the **display
  mesh** faceting at `SegmentsPerCircle`, with the exact edge overlay visibly
  *detaching* from the fill it outlines — the edge is the accurate one, and raising its
  count makes the detachment worse. The real fix is the existing **`TessellationQuality`**
  item: one max-chord-deviation criterion driving the display mesh *and*
  `BrepFeatureEdges` so they agree by construction. Camera-adaptive re-extraction on
  zoom is the follow-on.
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

- [ ] **Architecture decision first** — two viable shapes, prototype before committing:
  - **Blazor WebAssembly, kernel in the browser**: the whole kernel (Core/Mesh/
    Implicit/BRep/Interop/Modeling — all UI-free by mandate) compiles to WASM; models
    tessellate client-side; rendering via WebGL2 from .NET (JS interop to a thin
    canvas/WebGL wrapper, or a library like `Blazor.Extensions.Canvas`/three.js
    interop). Zero server; static hosting (could live on the GitHub Pages site).
    Risks to prototype: WASM perf of the kernel's hot paths (no SIMD intrinsics
    guarantees in WASM today — measure booleans/tessellation on a real model), payload
    size, `ArrayPool`/`stackalloc` behavior under WASM.
  - **Blazor Server (or hybrid)**: kernel runs server-side, viewer streams meshes to
    the browser (SignalR); thin WebGL client renders `RenderMesh` buffers. Better for
    heavy models; needs hosting.
- [ ] **Shared render model** — extract the viewer's scene-to-buffers layer so desktop
  and web consume the same thing: `RenderMesh` + part color/transform/display-mode is
  already the seam (`RenderCore.cs` proved the shared-core pattern for shaders/camera;
  a `ViewerModel` abstraction over Scene→render-instances would serve Avalonia, the
  offscreen renderer, AND the web client). GLSL ES shaders port near-verbatim to
  WebGL2 (same ASCII-only rule).
- [ ] **Feature parity ladder** (build in this order): orbit/pan/zoom camera + shaded
  mesh rendering → part colors + feature edges → tab strip + model tree + visibility →
  picking (ray-cast server/client-side against the existing per-part BVH) → display
  modes + section planes (same fragment-discard technique in WebGL) → properties
  panel. Reuse the camera math from `CameraMath` (it's already extracted).
- [ ] **Docs-site embedding** — the payoff synergy: DocsGen examples could emit an
  interactive WASM viewer block per example instead of (or alongside) static PNGs —
  spin-the-model documentation, all statically hosted on the existing GitHub Pages
  deployment.
- [ ] **Out of scope until later**: editing/sketching in the browser, collaboration,
  server-side model storage. This is a *viewer* first.

## MCP server / remote control of the viewer

The **headless server ✅ landed** (`src/EngrCAD.Mcp`: `EngrCadMcp.Run` + `--mcp` over
stdio — list/describe/screenshot/export/reload, PNG returned as an MCP image block,
stdout guarded, geometry evaluated lazily). Remaining:

- [ ] **Write tools** — the v1 boundary is deliberately read-only; the natural next step
  is editing `[Param]` values through `FeatureHistory` and regenerating, so an assistant
  can *drive* a parametric model rather than only inspect it.
- [ ] **`screenshot` takes only one section plane** — the viewer now does up to four with
  quarter/octant combine, so plumb `SectionPlane[]` + `SectionCombine` through. Also: no
  explicit camera (named views only), and `export` to `.png` is hardcoded 1280×800.
- [ ] **Structured content** — results are JSON *text* blocks today; MCP structured
  content (`UseStructuredContent` + output schemas) would let clients consume them
  without parsing.
- [ ] **Delete `src/EngrCAD.Mcp/StandardViews.cs`** — it mirrors `ViewCubeMath.PoseFor`
  and `CameraMath.FrameDistance` because both are `internal`, which is a live parity
  risk (two copies of the pose maths). Make them public, or expose an
  `EngrCad.StandardCamera(instances, view)` helper, and delete the duplicate.
- [ ] **Untested**: a real third-party MCP client (Claude Desktop/Code) connecting — the
  protocol was driven by hand and via the SDK's own client — and the no-GL error path on
  a GPU-less machine.
- [ ] **Live-viewer RPC** (the option (b)/(c) work, still open) — drive a *running*
  window rather than rendering headlessly:
  - **(b) RPC into a *running* viewer** — drive the live window: change the view, toggle
    sections, select parts, grab the framebuffer. Needs a small transport (a **named
    pipe** or a loopback socket carrying JSON-RPC) exposed by `EngrCAD.Viewer` behind an
    opt-in flag (`EngrCadOptions.WithRemoteControl(...)` / `--rpc`), with the MCP server
    as a separate process bridging to it.
  - **(c) Viewer hosts MCP directly** over the HTTP+SSE transport on loopback — removes
    the bridge hop, but puts a web server inside the GUI app; only worth it if (b)'s
    extra process proves annoying. (stdio, the usual MCP transport, does not fit a
    windowed app, which is why (b)/(c) differ from (a).)
  Tools a *live* viewer adds beyond today's read-only set: `set_view`/`fit`,
  `set_section`, `set_display_mode`/`set_view_style`, `select_part`/`get_selection`,
  and `measure`.
- [ ] **Non-negotiable constraints** (the viewer's existing rules, which an RPC layer is
  very good at violating): every mutation must marshal onto the Avalonia UI thread
  (`Dispatcher.UIThread.Post`) — the thread-safe seams are `ViewportControl.SetParts` /
  `SetInstances` and the `Status` callback; **GL only inside the render pass**, so a
  screenshot request must ride the existing `SaveScreenshot` capture-on-next-frame path
  rather than touching GL from the RPC thread; and meshing stays off the UI thread as
  always.
- [ ] **Security**: loopback-only, **off by default**, opt-in flag, and consider a token —
  this endpoint can load models and write files, so it is a local attack surface and
  should never be on implicitly.
  (Packaging is settled: `src/EngrCAD.Mcp` is its own package on
  `ModelContextProtocol.Core`, so viewer and kernel consumers inherit nothing.)

## App layer / infrastructure

- [ ] **Parametric features follow-ups** (`FeatureHistory` landed) — persistent
  topological IDs (selectors are the naming story today), property-panel UI editing of
  `[Param]`s, feature list in the viewer model tree, a feature registry for UI
  insertion.
- [ ] **Assemblies follow-ups** (v2 landed: BOM, exploded views, mates, STEP assembly
  export + import) — true GPU instanced drawing (matrix buffer, one draw per part), tree
  expand/collapse, per-instance color/display-mode overrides, retro-assign palette colors
  when parts are added to an assembly after `Tab.Add`, **mates ACROSS assembly levels**
  (v1 constrains one level; a sub-assembly is one rigid body), mate
  persistence/serialization alongside `SaveParameters`, and an **explode-path renderer**
  (the dashed leader lines drafting standards draw between an exploded part and its seat).
- [ ] **Standard component library — breadth and fidelity** (v1 landed:
  `HardwareComponent` + `ComponentFeature` + `ComponentAssembly`; ISO 4762 SHCS, Tappex
  Trisert, ISO 2338 dowel; the full two-body fastener stack). Follow-ups: more families
  (ISO 7380 button, ISO 10642 csk, nuts, washers, bearings); higher body fidelity (hex
  socket recesses, a modeled thread on the shank via `Shape.ExternalThread`,
  knurled/flanged inserts, ISO 2338's crowned pin ends); and stacks that anchor into a
  *placed component* — today `PlaceThrough` always cuts the screw's own tapped pilot in
  the far body, so anchoring into an insert means placing the insert separately.
- [ ] **Frame3d enabled next steps** — `FeatureContext.TopPlane` could become
  `SketchPlane.On(topFace)` (behavior decision: drill origins would move from world
  (0,0,z) to the face centroid); arbitrary section planes from a frame; `StepWriter`
  emitting AXIS2 placements via `Frame3d`; Part poses as frames (assemblies above).
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
