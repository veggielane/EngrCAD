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

- [ ] **BSP stack-overflows on deep trees** (`CsgNode.Invert` recurses) — it overflows
  outright on a 32k-triangle sphere pair. Now that `BooleanMethod.Exact` is the default
  and faster everywhere measured, decide: make the recursion iterative, or document BSP
  as legacy-only and keep it for the coplanar cases it historically handled.
- [ ] **Region refinement across a seam** — `MeshRegionOperator` deliberately refuses a
  replacement whose seam was re-split (splitting a seam edge leaves the neighbour face
  holding the un-split edge — a T-junction), so `MeshDecimator` round-trips but
  `LoopSubdivision` does not. Refining across a seam means refining the neighbours too:
  a different, larger operation.
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

## Implicit engine (EngrCAD.Implicit)

- [ ] **Sparse/multiresolution grids** — `DSparseGrid3` (block-hashed), `BiGrid3`
  (two-level), `HBitArray` (hierarchical bit array for sparse iteration). Storage
  substrate for large SDF domains that our dense Surface Nets sampling can't handle;
  `LazyGridSdf`'s 16³-block cache is the natural seam to build on.
- [ ] **Mesh-specific narrow band** (the generic `Sdf.NarrowBand` ✅ landed) — triangle
  rasterization into the band plus closest-triangle propagation would beat the generic
  octree culling for meshes specifically. Belongs in Interop next to `MeshSdf`.
- [ ] **Feed Surface Nets deinterleaved coordinates** (Interop) — now that batch
  evaluation is vectorized, sampling is a *minority* of `Polygonize`'s cost (it gained
  only 1.25× where bakes gained 3×), and it still builds a full-size `Vector3d[]` corner
  array that the root immediately transposes back apart.
- [ ] **Vectorize `SketchRegion.SignedDistance`** (Modeling) — now the scalar bottleneck
  under `Sdf.ExtrudedRegion`/`RevolvedRegion`, which the SIMD work left untouched.

## Interop / meshing (EngrCAD.Interop)

- [ ] **Continuation ("surface-following") meshing** — `MarchingCubesPro` only evaluates
  cells near the surface it discovers, instead of the full grid our Surface Nets
  samples. Big win for high resolutions; adapt the idea to Surface Nets.
- [ ] **Trimmed-face tessellation remaining gaps** — pole bands with holes and
  |winding| > 1 fall back to grid (renders, ignores holes); refinement quality
  upgrade: Rivara-with-boundary-constraints instead of the monotone-decrease rule's
  worst-sliver tradeoff; no Delaunay flips. Also (Frame3d work finding): bores drilled
  into extruded *side* faces miss the inscribed-ngon volume by ~5e-5 — the trimmed
  side-face triangulation differs from a planar cap's (documented in
  `SketchPlaneFrameTests.On_ExtrudedSideFace_DrillsIntoTheSide`).

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
- [ ] **Minimum-volume 3D OBB — blocked on a layering decision.** The exact method
  (Freeman–Shapira: the min-volume box has a face flush with a hull face, so project per
  hull face and take `Fitting2d.MinAreaBox`) needs a 3D convex hull, but `ConvexHull`
  lives in EngrCAD.Mesh, which *references* Core — so `Fitting3d` cannot call it, and a
  second quickhull in Core would be worse than today's PCA heuristic. Decide: move
  `ConvexHull` into Core, or add `Fitting3d.MinVolumeBox(hullVertices, hullTriangles)`
  taking a caller-supplied hull. The algorithm is spelled out in `FitBox`'s doc comment.
- [ ] **`Bvh.Build` + `QueryOverlap` is now the largest single line in the exact mesh
  boolean** — ~22 ms of a 58 ms boolean on 32k-triangle operands, after the mesh-side
  costs were fixed. Two builds plus the overlap query; worth attacking in Core (faster
  build, or reusing a hierarchy across a boolean cascade).
- [ ] **Core follow-ups** — thread `ProgressCancel` through `BRepTessellator` and
  `MeshSdf`/winding builds (unstarted); `Region2dBoolean`'s O(E²) interior-sample
  clearance scans still want the same index the arrangement broad phase now has
  (**re-benchmark the arrangement speedup on a quiet machine** — the measured
  candidate-pair reduction is a solid 9.1% of the full scan, but wall-clock numbers
  were taken with several agents building concurrently and disagreed by 3×);
  intersection-segment queries over `Bvh.QueryOverlap` pairs (the triangle–triangle
  layer belongs to EngrCAD.Mesh); routing `FaceSplitter`'s planar tracing through
  `Arrangement2d` (deferred — boolean-critical); optionally migrate
  `MeshWindingNumber` onto `Bvh`'s per-node ranges.

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
- [ ] **`SweptSurface.TryProjectPoint` still pays the generic 2D grid** — the 1D
  reduction that made extrusions and revolves ~15× faster does not apply directly
  (an RMF frame varies along the path), but seeding on the path parameter first would
  still beat a 17×17 scan. Only swept-tube geometry pays this today; `NurbsSurface`
  likewise keeps the base implementation, legitimately.
- [ ] **Ambient occlusion is now the largest single cost of opening a window** (~7–8 s
  of an ~11 s demo launch before lazy tabs; two thread parts alone are 5.7 s and
  already saturate every core, so parallelism has no more to give). The next lever is
  showing the scene flat-lit immediately and streaming occlusion in as bakes finish —
  *not* making the bake less honest.
- [ ] **`Shape.From(brepSolid)` is unsafe as a *boolean operand* when lowered twice** —
  `ShapeCompiler.LowerBrep` hands the raw solid to `BrepBoolean`, which consumes its
  inputs, so this was already a hazard sequentially, not just under the new parallel
  `PreMesh`. Fix by cloning at the `SourceShape` boundary or making `BrepBoolean`
  non-consuming. Not exercised by any test, sample or docs page today.
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

- [ ] wedge primitive (the OCCT gap; cone ✅ landed — revolved-line side surface +
  `Sdf.Cone` + `MeshPrimitives.Cone` + `Shape.Cone`, Native in all three reps)
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
- [ ] **SDF isolines on multi-plane cuts** (found while verifying the quarter cut) —
  with several section planes active the isoline overlay appears to be drawn across
  each plane's full extent rather than only over that plane's actual cut region: on a
  quarter cut, contour fans extend into the removed quadrant and into empty space
  beyond the part. Each plane's contours need clipping by the sibling planes (and by
  the model silhouette) the way the fills already are. Confirm against
  `SectionContourRenderer` before changing anything — the positive-distance family is
  *meant* to extend outside the solid, so the fix is about sibling-plane clipping, not
  about suppressing outside contours.
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
- [ ] Silhouette-adaptive edge sampling — a fixed 96/circle undersamples very large
  rims (the double/triple B-Rep lowering that used to sit here is fixed:
  `Part.TryGetSolid()`).
- [ ] **Construction-tree follow-ups** (tree + per-node preview ✅ landed) — a
  **rollback bar** (drag a marker in the feature list; suppress below it),
  **suppress-from-tree**, and **`[Param]` editing** in the properties panel: all cheap
  now, since the rows already carry the `Feature`, its `Suppressed` flag and
  `ParamInfo`. Also: construction previews don't render in headless `RenderToImage`
  (the same parity gap isolines had), and a preview clears on live reload because node
  references change — it could be restored by path.
- [ ] Move `SectionContours`' per-part implicit lowering onto `Part` alongside
  `TryGetSolid`, so the SDF lowering is cached the same way the B-Rep one now is.
- [ ] **Construction tree in the viewer (Shape graph / features as tree rows)** — today
  the model tree lists parts and assembly occurrences; it should also expand a part
  into **how it was built**: for a `Shape`-backed part the operation graph (each node
  already knows its label via `Shape.Describe()`, the same text `Explain` prints), and
  for a `FeatureHistory`-backed part the ordered feature list with names, suppression
  state, and `[Param]` values. Nested/child rows per operand of booleans, patterns, etc.
  - **Selecting a node previews it in the viewport.** Selecting a **sketch** draws the
    sketch itself — its curves placed on their `SketchPlane` in 3D (arcs/béziers
    flattened for display only), which needs a curve/polyline overlay path; the line
    program plus the `AnnotationLayer`/feature-edge overlays are the precedents for
    drawing non-mesh geometry. Selecting an intermediate operation previews **that
    subtree's** geometry (lower just that sub-shape — cheap near the leaves, and the
    result is cacheable per node), which is effectively a rollback view.
  - Natural follow-ons once the tree exists: a **rollback bar** (show the model as of
    feature N), suppress/unsuppress from the tree (`FeatureHistory` already supports
    suppression), highlight the faces a feature created (needs the topological-naming
    item), and editing `[Param]`s in the properties panel (already an open item under
    parametric-features follow-ups — this is its UI half).
  - Design notes: `Shape` is an immutable graph, so a tree row is just a node reference
    plus a path; per-node preview lowering must stay off the render thread like
    `Scene.PreMesh`, and previews should be cached per node (the B-Rep lowering cache
    item above serves this too). Sketches are pure 2D + a plane, so a display polyline
    is cheap and exact enough at screen resolution.
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
- [ ] **Adopt `ILogger` properly and delete the `IEngrCadLog` shim** — Chris has
  approved taking the dependency, which reverses the earlier deliberate
  no-Microsoft.Extensions decision (the Viewer README documents that rationale and must
  be updated to say why it changed). Take
  **`Microsoft.Extensions.Logging.Abstractions`** — abstractions only, no provider — so
  consumers keep choosing their own sink, and it stays compatible with the
  kernel-projects-carry-no-UI-dependency rule (a logging abstraction is not UI).
  - Replace `IEngrCadLog`/`EngrCadLog.Console`/`EngrCadLog.From(delegates)` with
    `ILogger`/`ILoggerFactory`; `EngrCadOptions.Log` becomes an `ILogger`, the builder
    gains `WithLogger`/`WithLoggerFactory`, and the default becomes
    `NullLogger.Instance`. This is a breaking public-API change — fine at 0.1.0, but
    call it out in the package notes.
  - **Distinguish logging from program output.** `EngrCad.Run`'s `wrote part.step` and
    the live-reload overlay messages are user-facing CLI output, not diagnostics;
    decide deliberately which become `ILogger` Information and which stay direct
    console writes. Note `--mcp` mode needs everything off stdout (stdio carries the
    protocol) — with `ILogger` that becomes a provider/sink choice rather than a
    special case, which is an argument for routing it all through logging.
  - **Structured logging, not interpolated strings**: message templates with named
    placeholders, and `LoggerMessage`/source-generated logging on anything that could
    run hot — the performance mandate (no allocation in hot paths) applies to logging
    calls too, so guard with `IsEnabled` where a message would allocate.
  - Then extend inward: optional `ILogger` on long-running kernel operations alongside
    the existing `ProgressCancel` (booleans, tessellation, `MeshSdf`/winding builds,
    STEP import). Keep diagnostics that are *results* as return values —
    `StepReadResult.Diagnostics`, `MeshRepair`'s reports and `Explain`'s node report are
    data the caller acts on, not log lines; logging complements them rather than
    replacing them.
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
