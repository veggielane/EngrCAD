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
- [x] **`MeshSdf` winding-mode construction cleanup** ✅ done —
  `MeshWindingNumber.FromTriangulated` (precondition validated by a cheap scan);
  `MeshSdf` passes its already-triangulated mesh through; extraction loops merged.
- [x] **Trimmed-face tessellation** ✅ done — `TrimmedFaceTessellator`: exact-coordinate
  ear clipper (NOT earcut — its collinear filtering drops iso-parameter uv-collinear
  samples, an unzippable crack) + strip-zip/pole-fan bands + surface-exact midpoint
  refinement, routed by a two-sided 3D boundary match with grid fallback. Closed the
  cut-through-hole boolean limitation end-to-end (slot-through-bore → genus 3, exact-ish
  volume). Remaining gaps: band faces with extra hole loops fall back to grid (renders,
  ignores the hole), |winding| > 1 unsupported, no Delaunay quality flips.

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
- [x] **Bug: sphere-through-box boolean misclassification** ✅ fixed — four compounding
  causes: `BrepQueries.Bounds` sampled edges only (a hemisphere's equator hid the dome
  → face pairs prefiltered away; bounds now include surface-domain samples,
  conservative for trimmed fragments), tracer-clipped arcs could never refine against
  boundaries (`TrySphereCarrier` promotes full-turn revolved spheres to exact analytic
  plane∩sphere circles), closed-interior splitting ignored mandatory seam breaks
  (`SplitByInteriorClosedCurve` builds matching arc pairs), and wrapping traced loops
  were area-classified as holes (they are band boundaries — paired bottom-to-top by v).
  Regression-tested in `SphereBooleanTests` (all three ops, six-face pierce +
  single-face protrusion, analytic cap volumes, genus checks incl. difference χ=−8).
- [x] **Hole-config validation** ✅ done — `Shape.Drill` rejects overlapping/tangent
  holes up front (per pair, against the surface-level circle, naming both points), and
  B-Rep lowering of the new `DrillShape` node rejects a tool bottom exactly coplanar
  with a planar body face with actionable guidance. Remaining gap: two *separate*
  `Drill` calls aren't cross-validated; a future optimization can avoid the read-only
  validation lowering (`DrillShape` lowers the body twice on the B-Rep path).
- [ ] **Bug: cross-drill through a bore fails in `BrepBoolean`** (found by
  TessStepFix) — `Difference(drilled box, perpendicular cylinder tool)` fails
  `Validate` ("Edge is used by 1 coedges") inside the boolean, before tessellation:
  the tool-side band wrap-split by NON-PLANAR tracer curves doesn't seal. The
  tessellation side (band-with-holes) is ready; this is the remaining blocker for
  true cross-drilled-bore booleans.
- [ ] **Threads** — real modeled thread geometry, internal and external:
  - **Threaded holes**: `Shape.ThreadedHole(spec, points, depth, plane)` — drill the
    ISO 262 tap-drill pilot (the `StandardHoles.Tapped` table already has them), then
    cut the internal thread. **External threads**: thread a cylindrical boss/stud
    (`Shape.ExternalThread(diameter, pitch, length, ...)`), with proper lead-in/runout
    chamfers at both ends so printed/machined parts start cleanly.
  - **Standard ISO sizes**: a `StandardThreads` catalog like `StandardHoles` — ISO 68-1
    60° profile, ISO 261/262 coarse (and later fine) pitch series, M2–M12+ reusing the
    existing metric table infrastructure.
  - **Printing clearance**: an additional radial gap parameter for 3D printing —
    internal threads grow by the clearance, external shrink (typical FDM ≈ 0.1–0.25 mm)
    so printed pairs actually mate. Default 0 (exact nominal); the parameter documents
    that clearance is applied normal to the thread flanks.
  - Implementation routes (both worth having, rep-appropriate): **implicit** — a helical
    thread SDF (distance to helix ramp ± profile) is cheap, exact-enough, and
    print-oriented (composes with `Sdf.Sampled` for meshing); **B-Rep** — helix path +
    the ISO profile swept via the existing RMF `Sweep` (a helix `Curve3d` is needed;
    watch tessellation density along the turns). Cosmetic-only threads (annotation, no
    geometry) are the cheap CAD-standard fallback and pair with the existing "thread
    cosmetics" note in the OCCT feature-ops item.
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
  route. Also: surface-mesh quality prep (the isotropic-remeshing item above is a
  prerequisite for good tet input) and region/attribute tagging (material per body,
  face groups for boundary conditions — B-Rep face identity → mesh facet tags).
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
  torus bands). Remaining: sharp-corner fillet corners (ball/miter patches — the
  trimmed-band tessellation blocker is now GONE, this is unblocked), arbitrary edge
  sets (not just face rims), variable radius, chamfer angles other than the
  two-setback form
- [ ] Draft angles (`BRepOffsetAPI_DraftAngle`)
- [ ] Offset surfaces / thick solid / shelling (B-Rep shell — we only shell as SDF)
- [ ] Feature operations (`BRepFeat`): pocket, boss, rib, slot as first-class features
  with faces-to-remove semantics — drilled holes ✅ done (`Shape.Drill` +
  `StandardHoles`; future: drill-tip angles, thread cosmetics/annotation, hole tables)
- [ ] Shape healing (`ShapeFix`): fix wires/faces/gaps/small edges — needed the moment
  we import foreign STEP
- [ ] Local operations: split shape by shape, glue faces

**Geometry**
- [x] Conics ✅ complete — circle, ellipse, B-splines/NURBS with rationals, and now
  `Parabola3d` (focal parameterization, closed-form arc length) + `Hyperbola3d`
  (cosh/sinh branch); `Curve3d` exposes virtual exact `DerivativeAt`/`SecondDerivativeAt`.
  Follow-ups: `Parabola3d.ToNurbs()` (trivially exact quadratic Bézier), STEP export
  mapping (PARABOLA/HYPERBOLA/OFFSET_CURVE_3D — sign conventions verified compatible).
- [x] Offset *curves* ✅ (`OffsetCurve3d`, exact O′ = (1 − dκ)C′); offset *surfaces*
  still missing
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
- [x] STEP import ✅ done (`StepReader`: Part 21 parser + AP214 mapping, round-trips
  everything `StepWriter` emits; exact edge-domain reconstruction, revolve angle/trim
  recovery by profile-space bisection; mm assumed, diagnostics for skips). Follow-ups:
  unit scaling; CONICAL/TOROIDAL_SURFACE synthesis as `RevolvedSurface`; shape healing
  for foreign files (separate item above); `StepWriter` should export
  `TransformedCurve(NurbsCurve)` exactly by transforming control points (currently
  sampled to degree-1 polylines — blocks exact round-trip of NURBS-profile extrusions).
  Code-quality flags: `RecoverRevolvedSurface`'s rim-circle rejection floor is an
  absolute 1e-6 — on large geometry an off-axis rim silently leaves the generator
  untrimmed (scale by axial extent or emit a diagnostic on near-miss); bisections run
  a fixed 100 iterations (exact but wasteful, import-time only).
- [x] **Trimmed-face tessellation follow-ups** ✅ done — direct pathological
  ear-clipper tests landed (comb, spiral, hole-near-hole bridging, vertex-on-diagonal,
  multi-level collinear runs; `InternalsVisibleTo` added); `SegmentsTouch` now takes
  the file's jitter-band tolerance; band faces with extra hole loops tessellate
  properly for **two-ring bands** (seam placed in the largest u-gap, unrolled +
  ear-clipped with hole bridging; polyline edges sampled at exact vertex parameters —
  `PolylineCurve3d.VertexParameters`); `Refine` terminates via a monotone-decrease
  enqueue rule with a fail-safe fallback (no partial output). Still open: pole bands
  with holes and |winding| > 1 fall back to grid; refinement quality upgrade
  (Rivara-with-boundary-constraints instead of the monotone rule's worst-sliver
  tradeoff); no Delaunay flips.
- [ ] Data exchange: IGES, glTF, native BREP serialization format — STL export ✅ done
  (`StlWriter`, binary, `--export .stl`)
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
  screenshotting the demo app. Render-quality fixes ✅ done: 24-bit depth (16
  fallback) kills the PolygonOffset silhouette notching; 2× supersample +
  box-downsample gives deterministic anti-aliasing on every backend (MSAA pbuffers
  are unreliable under ANGLE/WARP); frustum near/far now scale from camera + scene
  so the 110-unit distance clamp is gone (large scenes no longer crop). Remaining:
  `Part.DisplayMode` and section planes are ignored offscreen.
- [x] **Extract a shared viewer render-core** ✅ done — `RenderCore.cs`:
  `ViewerShaders` (one shader set; offscreen uses neutral uniforms), `CameraMath`
  (incl. scene-scaled `FrustumPlanes` — the window path dropped its fixed 0.1/200
  planes and 110/120-unit clamps, so large scenes frame and render fully everywhere),
  `RenderGeometry` (grid/axes/upload). Translucent pass is allocation-free per frame;
  display-mode cycler enumerates the enum properly.
- Add a builder for EngrCad.Run and Show, so we can set defaults like render quality, and so it can consume IOptions, ILogger etc
- [ ] **View-type selector** in the viewer (toolbar): **points / mesh (wireframe) /
  shaded / shaded with edges** — a global viewport display mode, the classic CAD
  view-style dropdown. (Distinct from the per-part display modes; the per-part
  setting should override the global one where set.)
- [ ] **SDF isolines on the section plane** — when the section plane cuts through a
  part whose geometry is an `Sdf` (or a `Shape` whose implicit lowering is available),
  overlay iso-distance contour lines of the field on the exposed cut: the d = 0
  contour is the exact surface cross-section, and a family of d = ±k·spacing lines
  visualizes the distance field itself (great for debugging blends/offsets and for
  seeing wall thickness at a glance). Implementation sketch: sample the SDF on a 2D
  grid over the cut plane's visible rect (batch `Evaluate` seam; `Sdf.Sampled` makes
  it cheap), marching-squares the contours, draw via the line program clipped like
  model geometry. Color by sign (inside/outside) or a diverging ramp by distance.

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