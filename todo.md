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
`MeshExtrude` (faces/thicken), selections + connected components. Remaining:

- [ ] **Phase B: imprint boolean + editor-powered repairs** — the exact-intersection
  boolean rewrite (`MeshMeshCut` + `MeshBoolean`: cut both meshes along exact
  intersection segments via `Bvh.QueryOverlap` candidate pairs + triangle–triangle
  segments, imprint with `EditableMesh.SplitEdge`, classify by `MeshWindingNumber`,
  weld; `MeshChangeSet` gives transactional rollback of failed imprints; g3's honest
  coplanar-case caveats apply). Plus the editor-dependent repairs: `MergeCoincidentEdges`
  (crack closing = `MergeEdges` + spatial-hash search over coincident boundary pairs —
  slots between MeshRepair's weld and orientation passes), `RegionOperator`
  (extract-modify-reinsert as a change-set session; `MeshFaceSelection.ToMesh()` +
  `BoundaryLoops()` are the extraction half), and `MeshRepair` gaining hole-fill
  integration for a full `AutoRepair`.
- [ ] **Isotropic remeshing with constraints** — `Remesher`/`RemesherPro` +
  `MeshConstraints` (fixed edges, no-flip, project-to-target) +
  `SharpEdgeReprojectionRemesh` for feature recovery; now buildable on
  `EditableMesh`'s split/collapse/flip. Quality control after booleans/decimation,
  pairs with `MeshSdf` as projection target, and a prerequisite for good FEA tet
  input (see Simulation).
- [ ] Hole-filling upper tiers — `MinimalHoleFill` (sharp-edge reconstruction) and
  `SmoothedHoleFill` (fill+remesh+Laplacian; needs remeshing above) on top of the
  landed `FillAll` dispatch.
- [ ] Port `MeshDecimator` onto `EditableMesh.CollapseEdge` (measured
  bit-identical-or-better comparison, like the PQ upgrade precedent).
- [ ] `MeshExtrude.Faces` overload taking `MeshFaceSelection`; mutable in-place
  variants of fill/extrude once callers want them.
- [ ] Wave-A review flags (all low) — `CollapseEdge` on a hypothetical isolated edge
  throws instead of returning a result code (unreachable today; add the early guard);
  `StlReader` `MemoryStream.ToArray()` doubles peak memory (use GetBuffer+length);
  `FacePokeInfo` exposes a mutable `int[]` breaking record equality
  (`IReadOnlyList`/`ImmutableArray`); `ObjReader` backslash-continuation is O(n²)
  string concat for pathological files.

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

- [ ] **Tolerance follow-ups from the audit** (audit ✅ complete — ~200 sites reviewed,
  13 call sites routed through the new `FaceGeometry.InverseEvaluationTolerance`
  const, ~60 justified in place, epsilon ladder documented in CLAUDE.md; these
  flagged items were report-only because changing them alters behavior):
  named seam-scale constants (`SealSeams` 1e-7, boundary-match 1e-7, the 1e-8
  curve-parameter dedupe cluster — mechanical bit-identical consts); `ConvexHull2`'s
  raw cross-product turn test → `Predicates2d.Orient2dSign` (with new degenerate
  tests); a `TracerSettings` struct collecting `SurfaceIntersection`'s marching
  constants (1e-10/1e-8/1e-7/1e-14 family — boolean-critical, tune together);
  BSP `Csg.Epsilon` 1e-9 and `MeshWelder` 1e-7 absolutes → extent-scaled (boolean
  seam re-testing required); `Sketch` 1e-12 area/length guards → extent-relative;
  `ShapeCompiler` coplanarity dot 1−1e-6 → explicit angular tolerance.
- [ ] **Core follow-ups from the parity/utils wave** — intersection-segment queries on
  top of `Bvh.QueryOverlap` candidate pairs (the triangle–triangle segment layer
  belongs to EngrCAD.Mesh — part of the imprint-boolean item); arrangement insertion
  acceleration (segment BVH/grid instead of the O(E) scan); consider routing
  `FaceSplitter`'s planar non-periodic tracing through `Arrangement2d` (deferred —
  boolean-critical); minimum-volume 3D OBB (PCA `FitBox` is a heuristic); thread
  `ProgressCancel` through more long ops (mesh booleans, BRepTessellator,
  MeshSdf/winding builds); parallelize more batch kernels via `ParallelFor` (feeds
  the SIMD big rock); optionally migrate `MeshWindingNumber` onto `Bvh`'s now-exposed
  per-node ranges.

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
- [ ] 2D booleans on profiles still need a region model, but the primitives are in:
  `ConvexHull2` ✅ (Core, monotone chain — closes the 2D-hull line; 3D quickhull ✅
  `Shape.Hull`), `Arrangement2d` ✅ + exact predicates ✅ (the mechanism named above)
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

- [ ] Remaining docs-cutaway sweep: other example pages that fake cutaways with
  boolean subtractions (DocsGen `render:` fences now take `section:`/`style:`
  options — convert where the page reads better with a real section).
- [ ] **Multi-section views** — several section planes active at once: two
  perpendicular planes give the classic **quarter cut** (corner cutaway), three give
  an octant view. Shader side: the single `dot(worldPos, uSectionAxis) >
  uSectionOffset` discard becomes a small uniform array of plane equations with a
  combine mode — **intersection of half-spaces** (discard when ALL planes exclude →
  quarter cut, the CAD-standard look) vs union (discard when ANY excludes — today's
  single-plane behavior generalized); cut-material shading and isolines then need
  per-plane treatment (isolines drawn on each active plane's cut, clipped by the
  others). UI: the Section toggle grows to a small panel or repeated axis chips
  (enable/disable per plane, each with its own axis + offset + `[`/`]` focus);
  `RenderToImage`/DocsGen fence options take a list. Offscreen/window parity from
  day one via the shared shaders.
- [ ] Section-plane follow-ups: arbitrary plane orientation from a `Frame3d` (the
  shader already takes a general axis vector + offset; v1 restricts it to X/Y/Z),
  per-part section opt-out, and picking that respects the cut.
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
- [ ] View-cube follow-ups (widget ✅ landed: stroke-font labels, face/edge/corner
  click-to-pose with eased animation, hover highlight, drag-orbits) — rotate-snap
  dragging like commercial cubes; SceneHost toolbar buttons could delegate to
  `ViewCubeMath.PoseFor` for one pose source.
- [ ] B-Rep edge-silhouette follow-ups (`Part.GetFeatureEdges`/`BrepFeatureEdges` ✅
  landed — B-Rep-backed parts overlay their exact edges at display resolution) —
  a Shape-level B-Rep lowering cache: `PreMesh` currently lowers a Shape part's
  B-Rep twice (once inside `ToMesh`, once for the edge overlay) because the mesh
  route does not retain its intermediate solid; a `Part`-cached solid (or Shape
  lowering memoization) would also serve STEP export and annotation resolution
  (which lowers a third time). Also: silhouette-adaptive edge sampling (a fixed
  96/circle undersamples very large rims).
- [ ] Ideas: ambient occlusion or matcap shading.

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
- [ ] `ILogger` throughout — viewer entry points now route through the `IEngrCadLog`
  seam (adaptable to `ILogger`); kernel-side diagnostics/progress logging remain.
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
