# EngrCAD — TODO / idea backlog

Open work only — completed items are removed as they land (the record lives in git
history and CLAUDE.md's status). Many items come from a survey of **geometry3Sharp**
(`C:\Users\chris\projects\git\geometry3Sharp`, Ryan Schmidt / gradientspace —
triangle-mesh + implicit library; no half-edge, no BSP, no B-Rep, so it complements
rather than duplicates our engines) and name the g3 classes worth studying before
implementing. Ordered roughly by value-for-effort within each section.

## Mesh engine (EngrCAD.Mesh)

- [ ] **Editable mesh (Euler operators)** — our `HalfEdgeMesh` is immutable-after-build;
  g3's `DMesh3_edge_operators` (SplitEdge/CollapseEdge/FlipEdge/PokeTriangle/MergeEdges,
  all with bowtie/non-manifold guards) shows the operator set a mutable engine needs.
  Study `RefCountVector` (sparse index allocator with free list + `CompactInPlace`) and
  `SmallListSet` (pooled adjacency lists, zero per-vertex allocation) for the storage
  model, and `Timestamp/ShapeTimestamp` counters for cache/spatial-tree invalidation.
- [ ] **Isotropic remeshing with constraints** — `Remesher`/`RemesherPro` +
  `MeshConstraints` (fixed edges, no-flip, project-to-target) +
  `SharpEdgeReprojectionRemesh` for feature recovery. Would give us quality control after
  booleans/decimation, and pairs with our `MeshSdf` as the projection target. Also a
  prerequisite for good FEA tet-mesh input (see Simulation).
- [ ] **Hole-filling suite** — we have none. g3 has a whole ladder: `SimpleHoleFiller` →
  `PlanarHoleFiller` (map to 2D, handles nested holes) → `MinimalHoleFill` (sharp-edge
  reconstruction) → `SmoothedHoleFill` (fill+remesh+Laplacian) → `AutoHoleFill`
  (strategy dispatch). Start with planar + simple; the dispatch pattern is worth copying.
- [ ] **Mesh repair pipeline** — `MeshAutoRepair` sequencing (orient → weld → degenerate
  removal → fill → non-manifold cleanup), `MeshRepairOrientation` (consistent winding
  across components), `MergeCoincidentEdges` (crack closing — overlaps our `MeshWelder`
  zip but edge-based). A repair entry point would let us ingest dirty STL files.
- [ ] **Extrude/shell mesh ops** — `MeshExtrudeFaces` (face-set extrude),
  `MeshExtrudeMesh` (offset + stitch = thicken/shell). Complements our SDF
  `Shell`/`Offset` with a direct mesh route.
- [ ] **Boolean alternative: intersection-imprint + winding classification** —
  `MeshMeshCut` + `MeshBoolean` (cut both meshes along exact intersection segments,
  classify by winding number, weld). This is the exact-intersection rewrite our BSP
  booleans' roadmap calls for; g3's is a working reference including its honest
  coplanar-case caveats. `MeshWindingNumber` exists and could also harden
  `BrepBoolean`'s SDF-probe classification.
- [ ] **Selection/region model** — `MeshVertex/Edge/FaceSelection` (grow/contract),
  `MeshConnectedComponents`, `RegionOperator` (extract-modify-reinsert a submesh with
  index maps), `DSubmesh3`. Foundation for local editing and the viewer's selection
  becoming operational (delete/move a face region).
- [ ] **Undo/redo change records** — `DMesh3Changes` (reversible add/remove/modify
  records). The transactional pattern a real editor needs.

## Implicit engine (EngrCAD.Implicit)

- [ ] **Sparse/multiresolution grids** — `DSparseGrid3` (block-hashed), `BiGrid3`
  (two-level), `HBitArray` (hierarchical bit array for sparse iteration). Storage
  substrate for large SDF domains that our dense Surface Nets sampling can't handle;
  `LazyGridSdf`'s 16³-block cache is the natural seam to build on.
- [ ] **Narrow-band mesh SDF** — `MeshSignedDistanceGrid` (exact narrow band + fast
  sweeping outward, sign by ray parity) and `CachingMeshSDF` (lazy per-cell, pairs with
  continuation meshing). Much faster than our per-query BVH `MeshSdf` when many
  evaluations hit the same region.
- [ ] **SIMD batch evaluation** — the roadmap's standing perf rock:
  `System.Runtime.Intrinsics` through the batch `Evaluate(ReadOnlySpan…)` seam
  (primitives and n-ary operators first; the seam was designed for exactly this).

## Interop / meshing (EngrCAD.Interop)

- [ ] **Continuation ("surface-following") meshing** — `MarchingCubesPro` only evaluates
  cells near the surface it discovers, instead of the full grid our Surface Nets
  samples. Big win for high resolutions; adapt the idea to Surface Nets.
- [ ] **Mesh IO: STL + OBJ read** — g3's `STLReader/Writer` (binary+ASCII),
  `OBJReader/Writer`, `StandardMeshReader/Writer` dispatch facade. We write STL/OBJ;
  reading + the repair pipeline = import path for real-world meshes.
- [ ] **Trimmed-face tessellation remaining gaps** — pole bands with holes and
  |winding| > 1 fall back to grid (renders, ignores holes); refinement quality
  upgrade: Rivara-with-boundary-constraints instead of the monotone-decrease rule's
  worst-sliver tradeoff; no Delaunay flips. Also (Frame3d work finding): bores drilled
  into extruded *side* faces miss the inscribed-ngon volume by ~5e-5 — the trimmed
  side-face triangulation differs from a planar cap's (documented in
  `SketchPlaneFrameTests.On_ExtrudedSideFace_DrillsIntoTheSide`).

## Core (EngrCAD.Core)

- [ ] **BVH query surface parity** — `DMeshAABBTree3` serves nearest / ray / all-hits /
  tree-vs-tree intersection (`FindAllIntersections` returns segment soup!) / winding
  number from ONE structure. Ours does box/ray/nearest; add: all-hit rays with
  t-ordering, **tree–tree overlap and intersection-segment queries** (feeds the
  imprint boolean), and winding number.
- [ ] **Non-allocating `Bvh.Nearest`** (perf-mandate, from a code-quality review) —
  `MeshSdf.Evaluate` passes a lambda to `Bvh.Nearest`, heap-allocating a closure per
  distance query in a kernel hot path. Add a struct/interface-delegate `Nearest`
  overload so `MeshSdf` (and other callers) can query allocation-free.
- [ ] **Tolerance-policy audit** — sweep every project for float comparisons that
  bypass the central `Tolerance` API: raw `==`/`!=` on doubles, hardcoded epsilons
  (`1e-9`, `1e-12`, …) that should reference the policy, and ad-hoc `Math.Abs(a - b) <
  eps` patterns. Fix or explicitly justify each (some deliberate ones exist, e.g.
  `NurbsCurve.TangentAt`'s 1e-14 stationary-point fallback and `GridSdf`'s 1e-9
  grid-sizing slack — those get a comment naming why the central policy doesn't apply).
- [ ] **Exact 2D predicates** — `PrimalQuery2d` / `Query2Integer` (adaptive-exact
  orientation & in-circle). Our earcut port and splitter use epsilon predicates; exact
  predicates are the principled fix for arrangement robustness.
- [ ] **2D arrangement** — `Arrangement2d` (segment insertion splitting a `DGraph2`) +
  `GraphCells2d` (extract bounded cells as polygons). A standalone, reusable version of
  what our `FaceSplitter` improvises in parameter space — and the basis of a sketch
  engine (see below).
- [ ] **Utility gems** — `IndexPriorityQueue` (array-backed heap with O(1) id→slot; our
  decimator's lazy PQ would upgrade nicely), `DVector<T>` (chunked growable array),
  `MemoryPool<T>`, `ProgressCancel` (cooperative cancellation threaded through every
  long op — we have nothing; needed before ops run in a real UI), `gParallel` (block
  parallel-for; our Surface Nets/SDF sampling is single-threaded).
- [ ] **Min-bounding fits** — `ContMinBox2` (min-area OBB), `ContMinCircle2`,
  `ContBox3` (PCA OBB), `OrthogonalPlaneFit3` (best-fit plane). Useful for stock
  computation, drawing views, and feature recognition.
- [ ] **Interval/integer types** — `Interval1i`, `Vector2i/3i`, `AxisAlignedBox3i` for
  grid indexing (our Surface Nets does raw int math inline).

## B-Rep / sketching (EngrCAD.BRep)

- [ ] **Threads follow-ups** (core feature landed: `StandardThreads` ISO catalog,
  `Helix3d`, `Sdf.Thread`, `Shape.ExternalThread`/`ThreadedHole` with printing
  clearance — implicit-Native/mesh-Bridged/B-Rep-Impossible) — **true helical B-Rep
  sweep**: the profile must co-rotate with the axial plane (NOT rotation-minimizing
  frames), and the coaxial tangent seam to the core cylinder needs the coplanar/
  tangent boolean case; `Helix3d` is the ready-made rail. Also: fine-pitch series,
  left-hand threads (`Helix3d` already takes negative pitch), thread runout (grooves
  fading via union-cone), cosmetic-thread annotation for drawings.
- [ ] **2D sketch engine** — combine g3-style `Polygon2d`/`GeneralPolygon2d`
  (polygon-with-holes containment), `PlanarComplex` (nested loop hierarchy),
  `Arrangement2d` + `GraphCells2d` (regions from crossing sketch curves), and
  `PolySimplification2`. This is the missing front door: sketch → regions → `Profile`s
  for extrude/revolve/sweep, with automatic hole detection.
- [ ] **2D sketch constraint solver** — sketching landed geometry-only by design; the
  Onshape-style layer on top is constraints (coincident/tangent/parallel/dimensions)
  solved variationally. Also future: elliptical arcs, sketch offset/thicken,
  sketch-on-face (face → SketchPlane query).
- [ ] **Biarc fitting** — `BiArcFit2` (two tangent-continuous arcs through
  point+tangent pairs). Converts our marched intersection polylines into exact-ish
  arc/line B-Rep curves — better STEP output and lighter seam edges.
- [ ] **2D NURBS/Bezier curves for profiles** — `NURBSCurve2`, `BezierCurve2`,
  `BSplineBasis` (we have 3D NURBS; sketching wants 2D + arc-length via
  `ArcLengthParam`).
- [ ] **Drill follow-ups** — cross-validate holes across *separate* `Drill` calls
  (per-call validation landed); avoid `DrillShape`'s read-only validation lowering
  (the body lowers twice on the B-Rep path); drill-tip angles, thread
  cosmetics/annotation, hole tables.
- [ ] **Boolean/splitting edge cases** (currently unreachable or loudly rejected, from
  the cross-drill work) — equal-radius perpendicular cylinders (tangent bicylinder:
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

- [ ] wedge primitive (the OCCT gap; cone ✅ landed — revolved-line side surface +
  `Sdf.Cone` + `MeshPrimitives.Cone` + `Shape.Cone`, Native in all three reps)
- [ ] `text()` — font outlines → `Profile`s (extrudable text). Parse font glyphs
  (TrueType via a .NET lib) → polygon outlines with holes; g3's `PolygonFont2d` shows a
  poor-man's variant
- [ ] `surface()` — heightmap (image/data grid) → mesh terrain
- [ ] 2D booleans — union/difference/intersection of profiles/regions (needed by the
  sketch engine; `Arrangement2d`+`GraphCells2d` is the mechanism)
- [ ] 2D convex hull (`ConvexHull2` in g3; 3D quickhull ✅ landed — `ConvexHull` in
  Mesh + `Shape.Hull`, exact for polyhedral operands)
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
- [ ] `offset(r|delta, chamfer)` (2D) — polygon offsetting with round/miter/chamfer
  corners (classic Clipper-style); feeds shells, pockets, and toolpaths
- [ ] `linear_extrude(twist, scale, slices)` — twisted/tapered extrusion (a
  `SweptSurface` variant with per-v rotation/scale; g3's `GenCylGenerators` is the
  mesh route)
- [ ] `projection(cut=false)` — solid's shadow as a 2D outline (needs 2D booleans)
- [ ] `projection(cut=true)` — planar cross-section as a 2D region (mesh: plane cut
  loops → polygons; B-Rep: `SurfaceIntersection` per face + loop assembly)
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
- [ ] import STL/OBJ/OFF (+ repair pipeline) — work on existing models
- [ ] import/export DXF + SVG (2D profiles in/out; SVG also useful for drawings)

## OpenCASCADE (OCCT) feature parity (open items)

What remains against the reference B-Rep kernel (covered: primitives,
extrude/revolve/sweep, booleans, rim fillets/chamfers, drilled holes, conics + offset
curves, curve interpolation, projection/extrema, surface intersection, STEP
export+import, volume/area, tessellation — see CLAUDE.md):

- [ ] Loft / ThruSections (skin a solid through a list of profiles)
- [ ] Pipe shell with evolution law (scaling/twisting profile along the spine)
- [ ] Boolean extras: *section* (curve-only result), fuzzy tolerance, modification
  history
- [ ] Fillet/chamfer completion — sharp-corner fillet patches (ball/miter; the
  trimmed-band tessellation blocker is gone, this is unblocked), arbitrary edge sets
  (not just face rims), variable radius, chamfer angles beyond the two-setback form
- [ ] Draft angles (`BRepOffsetAPI_DraftAngle`)
- [ ] Offset surfaces / thick solid / shelling (B-Rep shell — we only shell as SDF)
- [ ] Feature operations (`BRepFeat`): pocket, boss, rib, slot as first-class features
  with faces-to-remove semantics
- [ ] Shape healing (`ShapeFix`): fix wires/faces/gaps/small edges — needed the moment
  we import foreign STEP
- [ ] Local operations: split shape by shape, glue faces
- [ ] Surface interpolation + least-squares approximation (`GeomAPI_PointsToBSpline`
  proper; curve interpolation exists)
- [ ] Ray-parity B-Rep point classifier (drop the `MeshSdf` bridge in booleans)
- [ ] Inertia / center-of-mass global properties (volume/area exist)
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

- [ ] **View-type selector** (toolbar): **points / mesh (wireframe) / shaded / shaded
  with edges** — a global viewport display mode, the classic CAD view-style dropdown.
  (Distinct from the per-part display modes; per-part overrides global where set.)
- [ ] **SDF isolines on the section plane** — when the section plane cuts a part whose
  geometry is an `Sdf` (or a `Shape` with implicit lowering), overlay iso-distance
  contours on the cut: d = 0 is the exact surface cross-section; d = ±k·spacing
  visualizes the field (debugging blends/offsets, wall thickness at a glance).
  Sketch: sample the SDF on a 2D grid over the cut plane (batch `Evaluate`;
  `Sdf.Sampled` makes it cheap), marching-squares, draw via the line program clipped
  like model geometry, color by sign or a diverging ramp.
- [ ] **Offscreen parity** — `Part.DisplayMode` and section planes are ignored by
  `OffscreenRenderer`; honor them so headless renders match the window.
- [ ] Builder for `EngrCad.Run`/`Show` — defaults like render quality; consume
  `IOptions`, `ILogger` etc.
- [ ] Ideas: view cube widget, measure tool, ambient occlusion or matcap shading, edge
  silhouettes from B-Rep edges instead of mesh dihedrals (exact circles stay smooth at
  coarse tessellation).

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

## App layer / infrastructure

- [ ] **Parametric features follow-ups** (`FeatureHistory` landed) — persistent
  topological IDs (selectors are the naming story today), property-panel UI editing of
  `[Param]`s, feature list in the viewer model tree, a feature registry for UI
  insertion.
- [ ] **Assemblies follow-ups** (v1 landed: `Assembly`/`Occurrence` DAG with `Frame3d`
  poses, `PartInstance` flattening, viewer hierarchy/visibility/selection, shared-part
  GPU buffers) — **mates/constraints** (solve for the occurrence frames `Flatten`
  composes — the frames are already mutable), exploded views, BOM (count occurrences
  per distinct part — trivial over `Flatten()`), STEP assembly export
  (`NEXT_ASSEMBLY_USAGE_OCCURRENCE` from the same flattening), true GPU instanced
  drawing (matrix buffer, one draw per part), tree expand/collapse, per-instance
  color/display-mode overrides, retro-assign palette colors when parts are added to an
  assembly after `Tab.Add`.
- [ ] **Standard component library ("smart" components)** — a catalog of real
  hardware — screws/bolts (ISO 4762 SHCS, 7380 button, 10642 csk…), nuts, washers,
  thread inserts (Tappex Trisert already has pilot data in `StandardHoles`), dowel
  pins, bearings — where each component is more than geometry: **placing it modifies
  the host model and assembles itself**. A component carries (a) its own body (a
  `Part`/`Shape`, ideally parametric per size), (b) a placement frame (`Frame3d` — a
  point + direction on a face, or `SketchPlane.On(face)`), and (c) a **host
  preparation operation**: the cut features it needs, applied to the target body when
  placed — a thread insert drills its correct pilot bore, an SHCS drills clearance +
  counterbore (`StandardHoles` already knows the dimensions), a dowel reams its hole.
  Placement thus produces both a modified host and an assembly `Occurrence` of the
  component at the frame — the SolidWorks "Smart Fastener" / Onshape derived-feature
  idea, but in plain C#. Design notes: the preparation op is exactly a `Feature`
  (parametric, regenerates, participates in `FeatureHistory` caching + suppression —
  suppressing the insert removes its bore too); component sizes come from
  datasheet-driven tables like `StandardHoles`/`StandardThreads` (flag
  verify-against-datasheet like the Trisert precedent); assemblies (occurrences ✅
  landed) and threads (✅ landed) are the prerequisites, both in place. Stretch: a screw placed
  through two bodies prepares BOTH (clearance in the near body, tapped/insert bore in
  the far one) — the full fastener stack.
- [ ] **Frame3d enabled next steps** — `FeatureContext.TopPlane` could become
  `SketchPlane.On(topFace)` (behavior decision: drill origins would move from world
  (0,0,z) to the face centroid); arbitrary section planes from a frame; `StepWriter`
  emitting AXIS2 placements via `Frame3d`; Part poses as frames (assemblies above).
- [ ] **Parametric model layer / scripting** — fluent C# builder over the retained
  document model; `.csx` scripting via Roslyn (C# *is* our SCAD language); reusable
  parametric components as plain C# methods — document the pattern.
- [ ] `ILogger` throughout.
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
