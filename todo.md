# EngrCAD — TODO / idea backlog

Ideas harvested from a survey of **geometry3Sharp** (`C:\Users\chris\projects\git\geometry3Sharp`,
Ryan Schmidt / gradientspace — triangle-mesh + implicit library; no half-edge, no BSP, no
B-Rep, so it complements rather than duplicates our engines), merged with EngrCAD's own
known gaps (see [design.md](design.md) §10). Each item names the g3 classes worth
studying before implementing. Ordered roughly by value-for-effort within each section.

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
  booleans/decimation, and pairs with our `MeshSdf` as the projection target.
- [ ] **Hole-filling suite** — we have none. g3 has a whole ladder: `SimpleHoleFiller` →
  `PlanarHoleFiller` (map to 2D, handles nested holes) → `MinimalHoleFill` (sharp-edge
  reconstruction) → `SmoothedHoleFill` (fill+remesh+Laplacian) → `AutoHoleFill`
  (strategy dispatch). Start with planar + simple; the dispatch pattern is worth copying.
- [ ] **Mesh repair pipeline** — `MeshAutoRepair` sequencing (orient → weld → degenerate
  removal → fill → non-manifold cleanup), `MeshRepairOrientation` (consistent winding
  across components), `MergeCoincidentEdges` (crack closing — overlaps our `MeshWelder`
  zip but edge-based). A repair entry point would let us ingest dirty STL files.
- [x] **Plane cut** ✅ done — `MeshPlaneCut.Cut` (slice, keep the side the normal points
  away from, return boundary loops, optional earcut caps with collinear-chord zip).
  Non-convex faces crossing the plane 3+ times are Newell-plane triangulated via
  `PolygonTriangulator` before clipping (the earlier fan-from-vertex-0 gap is fixed and
  covered by a comb-prism regression test).
- [ ] **Extrude/shell mesh ops** — `MeshExtrudeFaces` (face-set extrude),
  `MeshExtrudeMesh` (offset + stitch = thicken/shell). Complements our SDF
  `Shell`/`Offset` with a direct mesh route.
- [x] **Winding-number classification** ✅ done — `MeshWindingNumber` (exact
  Van Oosterom–Strackee solid-angle sum + Barill/Jacobson order-2 multipole
  `FastWindingNumber` over its own contiguous-range hierarchy). Robust inside/outside for
  *non-watertight* meshes; wired as an opt-in `MeshSdf` sign source
  (`MeshSignSource.WindingNumber`, accepts open meshes; default pseudonormal unchanged).
  Still open: using it to harden `BrepBoolean` classification and the imprint boolean below.
- [ ] **Boolean alternative: intersection-imprint + winding classification** —
  `MeshMeshCut` + `MeshBoolean` (cut both meshes along exact intersection segments,
  classify by winding number, weld). This is the exact-intersection rewrite our BSP
  booleans' roadmap calls for; g3's is a working reference including its honest
  coplanar-case caveats.
- [ ] **Selection/region model** — `MeshVertex/Edge/FaceSelection` (grow/contract),
  `MeshConnectedComponents`, `RegionOperator` (extract-modify-reinsert a submesh with
  index maps), `DSubmesh3`. Foundation for local editing and the viewer's selection
  becoming operational (delete/move a face region).
- [ ] **Undo/redo change records** — `DMesh3Changes` (reversible add/remove/modify
  records). The transactional pattern a real editor needs.

## Implicit engine (EngrCAD.Implicit)

- [x] **Skeletal/blend operators** ✅ done — N-ary `Sdf.Union`/`Intersection`/
  `SmoothUnion` (flat single-node loops) + `Sdf.Blend` falloff-kernel blend
  (`Falloff.Wyvill`/`Exponential`), the g3 `ImplicitNaryUnion3d`/`ImplicitBlend3d`/
  `FalloffFunctions` equivalents. Deliberately skipped: skeletal-*field* convolution ops
  (`SkeletalBlend3d`/`SkeletalRicciBlend3d`, `DistanceFieldToSkeletalField`) — they work
  on 0..1 skeletal fields, not signed distances, and would break sign-exactness.
  Negative-blend bounds follow-up ✅ fixed — smooth-op bounds now clamp the expansion
  at 0 (matching how the math degrades to hard min/max for blend ≤ 0), binary and n-ary.
- [x] **Sampled-grid implicits** ✅ done — `Sdf.Sampled(cellSize[, region][, lazy])`:
  bakes any `Sdf` to a dense (or lazy 16³-block) trilinear grid through the batch
  `Evaluate` seam (g3 `DenseGridTrilinearImplicit`/`ImplicitFieldSampler3d`/
  `CachingGridImplicit3d` equivalents). Exact at nodes, O(h²) between; outside the baked
  box = boundary value + Euclidean distance-to-region (correct sign when the solid is
  contained). The standard acceleration for expensive ASTs like `MeshSdf`; pairs with
  the sparse grids below (`LazyGridSdf` is the seam for them and narrow-band SDF).
- [ ] **Sparse/multiresolution grids** — `DSparseGrid3` (block-hashed), `BiGrid3`
  (two-level), `HBitArray` (hierarchical bit array for sparse iteration). Storage
  substrate for large SDF domains that our dense Surface Nets sampling can't handle.
- [ ] **Narrow-band mesh SDF** — `MeshSignedDistanceGrid` (exact narrow band + fast
  sweeping outward, sign by ray parity) and `CachingMeshSDF` (lazy per-cell, pairs with
  continuation meshing). Much faster than our per-query BVH `MeshSdf` when many
  evaluations hit the same region.

## Interop / meshing (EngrCAD.Interop)

- [ ] **Continuation ("surface-following") meshing** — `MarchingCubesPro` only evaluates
  cells near the surface it discovers, instead of the full grid our Surface Nets
  samples. Big win for high resolutions; adapt the idea to Surface Nets.
- [ ] **Mesh IO: STL + OBJ read/write** — g3's `STLReader/Writer` (binary+ASCII),
  `OBJReader/Writer`, `StandardMeshReader/Writer` dispatch facade. We only write OBJ;
  reading STL + repair pipeline = import path for real-world meshes.
- [ ] **Trimmed-face tessellation** (our own roadmap item) — g3's
  `TriangulatedPolygonGenerator` (constrained triangulation by edge insertion into a
  meshed rectangle) is a template for tessellating split generated faces (cut-through
  bores) in parameter space.

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
  `NurbsCurve.TangentAt`'s 1e-14 stationary-point fallback — those get a comment
  naming why the central policy doesn't apply).
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
- [ ] **`Frame3f`-style coordinate frame type** — origin+rotation pose with
  world↔local point/vector/ray transforms. We improvise (origin, x, y) triples in sweep
  frames, cap planes, RMF — a proper `Frame3d` in Core would clean all of it up.
- [ ] **Min-bounding fits** — `ContMinBox2` (min-area OBB), `ContMinCircle2`,
  `ContBox3` (PCA OBB), `OrthogonalPlaneFit3` (best-fit plane). Useful for stock
  computation, drawing views, and feature recognition.
- [ ] **Interval/integer types** — `Interval1i`, `Vector2i/3i`, `AxisAlignedBox3i` for
  grid indexing (our Surface Nets does raw int math inline).

## B-Rep / sketching (EngrCAD.BRep)

- [x] ~~**Bug: drilling into a cylinder fails**~~ ✅ NOT A BUG (verified) — the filed
  repro conflated two *input* degeneracies that fail identically on a box: depth == the
  plate thickness leaves the tool's flat bottom coplanar with the far cap, and Ø10
  counterbores at 10 mm pitch are exactly tangent (non-manifold). With well-posed inputs
  (depth > thickness, recesses clear of each other and the wall) drilling a cylinder
  gives a Validate-clean, correct-genus, exact-volume solid — same as a box. Guarded by
  `HoleTests.CylinderDrilling_SimpleThrough_ExactVolume` /
  `CylinderDrilling_CounterboreMultiHole_ExactVolume`.
- [ ] **Hole-config validation** — degenerate hole inputs (overlapping/tangent recesses,
  tool depth ≤ plate thickness → coplanar far cap) currently surface as a cryptic
  `BRepBoolean` "Directed edge appears twice" from deep in tessellation. `Shape.Drill`
  should validate up front and throw a clear, actionable error (which holes overlap, or
  that the tool doesn't clear the far face) the way the rim features already guard hole
  clearance.
- [ ] **2D sketch engine** — combine g3-style `Polygon2d`/`GeneralPolygon2d`
  (polygon-with-holes containment), `PlanarComplex` (nested loop hierarchy),
  `Arrangement2d` + `GraphCells2d` (regions from crossing sketch curves), and
  `PolySimplification2`. This is the missing front door: sketch → regions → `Profile`s
  for extrude/revolve/sweep, with automatic hole detection.
- [ ] **Biarc fitting** — `BiArcFit2` (two tangent-continuous arcs through
  point+tangent pairs). Converts our marched intersection polylines into exact-ish
  arc/line B-Rep curves — better STEP output and lighter seam edges.
- [ ] **2D NURBS/Bezier curves for profiles** — `NURBSCurve2`, `BezierCurve2`,
  `BSplineBasis` (we have 3D NURBS; sketching wants 2D + arc-length via
  `ArcLengthParam`).

## Deformation / analysis (new territory, lower priority)

- [ ] **Laplacian smoothing & deformation** — `LaplacianMeshSmoother`,
  `LaplacianMeshDeformer` (handle-based), backed by `SparseSymmetricCG` /
  `CholeskyDecomposition` / `PackedSparseMatrix`. A solvers mini-library would also
  serve future constraint solving in sketches.
- [ ] **Local parameterization / curves-on-mesh** — `MeshLocalParam` (discrete
  exponential map), `MeshIsoCurves` (iso-contours of a scalar field on a mesh),
  `DijkstraGraphDistance` (approximate geodesics). Enables engraving/wrapping features.
- [ ] **ICP registration** — `MeshICP` for aligning imported scans to models.

## OpenSCAD feature parity

OpenSCAD's feature set as a checklist of user expectations for a CSG modeler, mapped
against EngrCAD. ✅ = have (sometimes better: exact B-Rep vs OpenSCAD's mesh-only CSG),
🔶 = partial, [ ] = missing.

### Primitives
- ✅ `cube` — `MeshPrimitives.Box` / `SolidFactory.MakeBox` / `Sdf.Box`
- ✅ `sphere` — `MeshPrimitives.UvSphere` / `Sdf.Sphere` (no B-Rep sphere *solid* yet)
- 🔶 `cylinder(r1, r2)` — we have straight cylinders everywhere but **no cone** (r1≠r2):
  add `ConeSurface` + `Sdf.Cone` + `MeshPrimitives.Cone`, tessellator support
- ✅ `polyhedron` — `HalfEdgeMesh.Build(positions, faces)`
- 🔶 2D `square/circle/polygon` — `Profile.FromPoints`/`Profile.Circle` cover modeling
  input; a first-class 2D region type (polygon-with-holes + area/containment) is part of
  the sketch-engine item above
- [ ] `text()` — font outlines → `Profile`s (extrudable text). Parse font glyphs
  (TrueType via a .NET lib) → polygon outlines with holes; g3's `PolygonFont2d` shows a
  poor-man's variant
- [ ] `surface()` — heightmap (image/data grid) → mesh terrain

### Booleans & combinators
- ✅ `union/difference/intersection` (3D) — `MeshBoolean` + `BrepBoolean` + Sdf `|&-`
- [ ] 2D booleans — union/difference/intersection of profiles/regions (needed by the
  sketch engine; `Arrangement2d`+`GraphCells2d` from the g3 list is the mechanism)
- [ ] `hull()` — convex hull, 3D (quickhull over mesh/solid vertices → `HalfEdgeMesh`)
  and 2D (`ConvexHull2` exists in g3). High value for quick bracket/enclosure modeling
- [ ] `minkowski()` — general Minkowski sum is hard; note the important special case is
  rounding, which we already have cheaply: SDF `Offset` (sphere-Minkowski ≡ offset) and
  `Filleting`. Document the equivalence; general polyhedron⊕polyhedron is low priority

### Transformations
- ✅ `translate/rotate/scale/multmatrix` — `Matrix4d`/`Quaterniond` + Sdf transforms;
  **but meshes/solids lack a one-call `Transform(Matrix4d)`** — add
  `HalfEdgeMesh.Transformed(m)` and `BrepSolid` transform (surfaces/curves each need a
  transform story — `TransformedCurve` exists; add `TransformedSurface` or per-type
  transform methods)
- [ ] `mirror()` — reflection transform incl. winding flip (mesh: reverse faces;
  B-Rep: reverse faces/loops — the `ReverseFace` machinery already exists)
- [ ] `resize()` — non-uniform scale to target bounds (mesh: easy; SDF: non-uniform
  scale breaks distance metric — document lower-bound semantics; B-Rep: needs
  affine-transformed surfaces)
- [ ] `offset(r|delta, chamfer)` (2D) — polygon offsetting with round/miter/chamfer
  corners for profiles (classic Clipper-style algorithm); feeds shells, pockets, and
  toolpaths
- ✅ `color()` — viewer-side concern; per-object color exists in the demo scene, but a
  real scene/document model with per-body appearance is TODO (see App layer below)

### Extrusion & projection
- ✅ `linear_extrude(height)` — `SolidFactory.Extrude` (ours adds shear + holes)
- [ ] `linear_extrude(twist, scale, slices)` — twisted/tapered extrusion: profile
  rotates/scales along the path. Fits our generalized-sweep machinery (a `SweptSurface`
  variant with per-v rotation/scale in the frame); g3's `GenCylGenerators` is the mesh
  route
- ✅ `rotate_extrude(angle)` — `SolidFactory.Revolve` (full + partial, holes on partial)
- ✅ beyond OpenSCAD: `Sweep` along arbitrary paths with RMF (OpenSCAD cannot)
- [ ] `projection(cut=false)` — flatten a solid's shadow to a 2D outline (silhouette:
  project triangles, 2D-union them — needs 2D booleans)
- [ ] `projection(cut=true)` — planar cross-section as a 2D region (mesh: plane cut +
  boundary loops → polygons; B-Rep: `SurfaceIntersection` per face + loop assembly)
- [ ] `roof()` (OpenSCAD dev) — straight-skeleton roof over a polygon; low priority

### Quality / tessellation control
- 🔶 `$fn/$fa/$fs` — we expose `segmentsPerCircle`/`curveSamples`/`resolution` per call;
  unify into a `TessellationQuality` options type (max angle, max chord deviation,
  min/max segments) with **adaptive** sampling from curvature rather than fixed counts

### Language / app layer (OpenSCAD's essence)
- [ ] **Parametric model layer** — OpenSCAD is really a *declarative script → CSG tree*
  system. EngrCAD's analog, in order of ambition: (1) a fluent C# builder API over a
  retained **document/scene model** (named bodies, parameters, re-evaluation on change —
  the `TransformSequence` idea from g3); (2) C# scripting via Roslyn scripting API
  (`.csx` models with live re-run — LINQ-native modeling was the founding vision, so C#
  *is* our SCAD language); (3) module/function-style reusable parametric components as
  plain C# methods — document the pattern
- [ ] Debug modifiers (`#` highlight, `%` background/ghost, `!` isolate, `*` disable) —
  per-body display flags in the viewer scene model (highlight ✓ exists via selection;
  add ghost/isolate/hide)
- [ ] `$t` animation — time-parameterized models; viewer re-tessellates per frame
- [ ] `assert/echo` — we have exceptions/tests; a model-validation report (volumes,
  bounds, manifoldness per body) shown in the viewer would serve the same role

### Import / export
- ✅ export STEP (beyond OpenSCAD), OBJ
- [ ] export **STL** (trivial: binary+ASCII writer over `RenderMesh`/`HalfEdgeMesh`) —
  highest-value quick win for 3D printing
- [ ] export 3MF / AMF (zip+XML; 3MF is the modern printing format), OFF
- [ ] import STL/OBJ/OFF (+ repair pipeline from the g3 section) — turns EngrCAD into a
  tool that can work on existing models
- [ ] import/export DXF + SVG (2D profiles in/out; SVG out also useful for drawings)
- [x] export PNG snapshot from the viewer (offscreen render to file) ✅ done — both the
  in-viewer `Capture` button (`ViewportControl.SaveScreenshot`) and headless
  `EngrCad.RenderToImage` / `--render out.png` (`OffscreenRenderer` + EGL pbuffer)

## OpenCASCADE (OCCT) feature parity

The reference open-source B-Rep kernel. Checklist of its capabilities against ours
(✅ = EngrCAD has an equivalent today, at least v1):

**Modeling algorithms**
- [x] Primitives: box, cylinder, sphere, torus ✅ (`SolidFactory`); cone, wedge missing
- [x] Prism (extrude), revolution, pipe (sweep) ✅ (`Extrude`/`Revolve`/`Sweep`)
- [ ] Loft / ThruSections (skin a solid through a list of profiles)
- [ ] Pipe shell with evolution law (scaling/twisting profile along the spine)
- [x] Booleans: fuse/common/cut ✅ (`BrepBoolean`) — OCCT adds *section* (curve-only
  result), fuzzy-tolerance option, and full modification history
- [x] Fillets/chamfers on planar-face rims ✅ done via `Shape.Chamfer/Fillet` with
  LINQ face selectors — chamfer: straight rims (mitered corners) + circular rims
  (cone bands); fillet: tangent-continuous line+arc rims + circular rims (cylinder/
  torus bands). Remaining: sharp-corner fillet corners (ball/miter patches need
  trimmed-band tessellation), arbitrary edge sets (not just face rims), variable
  radius, chamfer angles other than the two-setback form
- [ ] Draft angles (`BRepOffsetAPI_DraftAngle`)
- [ ] Offset surfaces / thick solid / shelling (B-Rep shell — we only shell as SDF)
- [ ] Feature operations (`BRepFeat`): pocket, boss, rib, slot as first-class features
  with faces-to-remove semantics — drilled holes ✅ done (`Shape.Drill` +
  `StandardHoles`; future: drill-tip angles, thread cosmetics/annotation, hole tables)
- [ ] Shape healing (`ShapeFix`): fix wires/faces/gaps/small edges — needed the moment
  we import foreign STEP
- [ ] Local operations: split shape by shape, glue faces

**Geometry**
- [x] Conics (circle, ellipse), B-splines/NURBS with rationals ✅; parabola/hyperbola missing
- [ ] Offset curves and offset surfaces as first-class geometry
- [x] Curve interpolation ✅ (`NurbsCurve.InterpolatePoints`: open natural / closed
  periodic cubic, chord-length parameterization); surface interpolation and
  least-squares *approximation* (`GeomAPI_PointsToBSpline` proper) still missing
- [x] Extrema / point projection ✅ (`TryProjectPoint`, `Bvh.Nearest`)
- [x] Surface–surface intersection ✅ (analytic quadric pairs + marching tracer)
- [x] Point-in-solid classification ✅ (via `MeshSdf` probing — OCCT's `BRepClass3d` is
  purely topological; consider a ray-parity B-Rep classifier to drop the mesh bridge)

**Infrastructure**
- [x] Global properties: volume, area ✅ (mesh-based); inertia/center-of-mass missing
- [x] Deflection-controlled tessellation ✅ (`BRepTessellator` is count-based; OCCT's
  `BRepMesh` is chord-error-based — worth adopting a deflection criterion)
- [ ] Topological naming / modification history (which output face came from which
  input face) — the foundation of parametric rebuilds surviving edits
- [ ] Data exchange: STEP import (we export only), IGES, glTF, native BREP
  serialization format — STL export ✅ done (`StlWriter`, binary, `--export .stl`)
- [ ] Hidden-line removal (HLR) projections for 2D drawings
- [ ] OCAF-style document framework: undo/redo, attributes, persistence

## Not worth adopting (deliberate)

- g3's mesh structure itself (index+edge-list) — our half-edge with explicit boundary
  half-edges is a deliberate different choice; adopt its *editability mechanisms*, not
  the structure.
- 2D-only NURBS — we already have 3D NURBS curves/surfaces.
- Its subdivision gap — g3 has no Loop/Catmull-Clark; we already have Loop.


## Viewer
- [x] add multiple tabs to the scene / viewer ✅ done — `Scene` holds named `Tab`s of
  `Part`s; the viewer shows a tab strip with per-tab cameras (auto-framed on first
  visit, remembered after).
- [x] properties window for model details ✅ done — right panel: kind (Shape/B-Rep/
  mesh/SDF), face count, closed, volume, area, size for the selected part.
- [x] model tree panel ✅ done — left panel lists the current tab's parts with
  visibility checkboxes; tree clicks and viewport picks stay in sync. Becomes the
  assembly hierarchy view once tabs can hold assembly occurrences.
- CAD chrome landed alongside: dark theme, toolbar (Fit + Front/Top/Right/Iso +
  perspective/orthographic toggle), ground grid + RGB world axes, feature-edge overlay
  (`MeshFeatureEdges`, sharp-dihedral + boundary edges), gradient background, status
  bar. Section planes ✅ done (horizontal clip via fragment discard, `gl_FrontFacing`
  cut-material cue, `[`/`]` height keys). Per-part display modes ✅ done
  (`Part.DisplayMode`: Shaded/Wireframe/Translucent — wireframe via `WireframeEdges` over
  the line program, translucent alpha-blended with per-part back-to-front ordering and
  opaque silhouette edges; per-tree-row cycler). Screenshot/export-image button ✅ done
  (`Capture` toolbar button → `ViewportControl.SaveScreenshot` → `glReadPixels` →
  dependency-free `PngWriter`, path reported in the status bar). Ideas for later: view
  cube widget, measure tool, ambient occlusion or matcap shading, edge silhouettes from
  B-Rep edges instead of mesh dihedrals (exact circles stay smooth at coarse tessellation).
- [x] **Headless offscreen rendering** ✅ done — `EngrCad.RenderToImage(scene, path,
  w, h, camera?)` / `CanRenderToImage` and a `--render out.png` switch on `EngrCad.Run`:
  renders a scene to PNG with no window via a direct EGL-pbuffer context over Avalonia's
  ANGLE natives (`OffscreenRenderer` + `EglContext`, `PngWriter`). This is the viewer
  self-verification loop — tests and agents render + inspect pixels instead of
  screenshotting the demo app.
- [ ] **Extract a shared viewer render-core** (from a code-quality review) —
  `OffscreenRenderer` deliberately duplicates ~150 lines from `ViewportControl`
  (shader source strings, `LookAt`/`Perspective`/`WriteColumnMajor`, grid/axes build)
  so the window and headless passes can drift apart silently. Hoist a shared
  `ViewerShaders`/`CameraMath` static so both render identically. (Also minor viewer
  hygiene the review noted: the translucent pass allocates a `List<int>` + sort
  comparer every frame — hoist a reusable buffer.)
- Add a builder for EngrCad.Run and Show, so we can set defaults like render quality, and so it can consume IOptions, ILogger etc
- [ ] **View-type selector** in the viewer (toolbar): **points / mesh (wireframe) /
  shaded / shaded with edges** — a global viewport display mode, the classic CAD
  view-style dropdown. (Distinct from the per-part display modes; the per-part
  setting should override the global one where set.)

## Other ideas
- [x] unify scripting language, the type of modelling is set at the end. ✅ done —
  `EngrCAD.Modeling`'s `Shape` graph: model once, `ToBrep()`/`ToImplicit()`/`ToMesh()`
  at the end, `Explain(target)` reports native/bridged/impossible per node.
- [ ] **2D sketch constraint solver** — sketching (lines/arcs/béziers with a fluent
  builder, exact in all three representations) landed geometry-only by design; the
  Onshape-style layer on top is constraints (coincident/tangent/parallel/dimensions)
  solved variationally. Also future: elliptical arcs, sketch offset/thicken,
  sketch-on-face (face → SketchPlane query).
- [x] **FeatureScript-style modeling in native C#** ✅ done — `Feature` classes with
  `[Param]` properties (ranges/units, reflection metadata), `FeatureContext` (body +
  lowered B-Rep for selector queries + `TopPlane`), `FeatureHistory` (replay with
  prefix caching, validation-first, failure keeps the last good prefix, suppression,
  JSON parameter save/load), standard features, `Feature.FromFunc`. Follow-ups:
  persistent topological IDs (selectors are the naming story today), property-panel
  UI editing of `[Param]`s, feature list in the viewer model tree, a feature registry
  for UI insertion.
- ILogger Throughout
- Sheet Metal
- docfx static site on github pages