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
- [ ] **Plane cut** — `MeshPlaneCut` (slice, keep one side, return boundary loops,
  optional fill). Cheap to build on our splitter machinery; very common CAD op.
- [ ] **Extrude/shell mesh ops** — `MeshExtrudeFaces` (face-set extrude),
  `MeshExtrudeMesh` (offset + stitch = thicken/shell). Complements our SDF
  `Shell`/`Offset` with a direct mesh route.
- [ ] **Winding-number classification** — `FastWindingMath` + `WindingNumber` /
  `FastWindingNumber` on the BVH (Barill/Jacobson multipole). Robust inside/outside for
  *non-watertight* meshes; would harden `MeshSdf`'s sign and `BrepBoolean`
  classification, and enable an alternative boolean style (see below).
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

- [ ] **Skeletal/blend operators** — we have polynomial smooth min; g3 adds
  `ImplicitBlend3d`, `SkeletalBlend3d`, `SkeletalRicciBlend3d` + `FalloffFunctions`
  (reusable falloff kernels) and N-ary operator variants (`ImplicitNaryUnion3d` etc. —
  ours are binary-only; N-ary flattens deep trees).
- [ ] **Sampled-grid implicits** — `DenseGridTrilinearImplicit` (grid → evaluable field
  with trilinear interpolation), `ImplicitFieldSampler3d` (bake any Sdf to a grid),
  `CachingGridImplicit3d` (lazy). Baking expensive ASTs (e.g. `MeshSdf`) to grids is the
  standard acceleration; pairs with the sparse grids below.
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

## Not worth adopting (deliberate)

- g3's mesh structure itself (index+edge-list) — our half-edge with explicit boundary
  half-edges is a deliberate different choice; adopt its *editability mechanisms*, not
  the structure.
- 2D-only NURBS — we already have 3D NURBS curves/surfaces.
- Its subdivision gap — g3 has no Loop/Catmull-Clark; we already have Loop.
