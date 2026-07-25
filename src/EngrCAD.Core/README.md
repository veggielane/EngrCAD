# EngrCAD.Core

Foundation library shared by every engine: double-precision math types and spatial
acceleration structures. Has no dependencies and must stay free of geometry-engine and UI
concerns.

## Contents

- **Math types** (`readonly struct`, zero-allocation): `Vector2d`, `Vector3d` (implicitly
  convertible from tuples: `(1, 2, 3)`), `Matrix4d` (row-major storage, column-vector
  convention: `p' = M·p`, so `A*B` applies `B` first), `Quaterniond` (Hamilton product
  matching matrix composition order), `Aabb`, `Ray3d`, `Interval`.
- **`Frame3d`** — right-handed rigid coordinate frame (origin + orthonormal X/Y/Z,
  Z = X × Y). World↔local maps for points, vectors, and rays; `Then` composition and
  exact transpose `Inverse`; `ToMatrix()` (column-vector convention); `Renormalized()`
  for iterated-frame drift. Factories: `FromXY` (Gram–Schmidt), `FromZX` (the STEP
  AXIS2_PLACEMENT_3D convention), and `FromNormal`, whose deterministic X is
  **exactly `Vector3d.ArbitraryPerpendicular`** — the codebase-wide perpendicular
  convention; sites deriving frames for the same axis must agree bit-for-bit or welded
  tessellation cracks (locked by tests).
- **`Tolerance`** — the central floating-point comparison policy. Kernel code never
  compares doubles with `==`; geometric predicates take a `Tolerance` (linear + angular).
- **`Predicates2d`** — adaptive-exact 2D predicates, a faithful port of Shewchuk's
  public-domain `predicates.c`: `Orient2d(a, b, c)` and `InCircle(a, b, c, d)` (plus
  `*Sign` variants) return values whose SIGN is exactly correct for all finite double
  inputs — exactly-collinear triples and exactly-cocircular quadruples yield exactly
  `0.0`. A cheap floating-point filter with a forward error bound handles the common
  case; near degeneracy the determinant escalates through Shewchuk's adaptive stages to
  an exact multi-term floating-point expansion (all temporaries `stackalloc`, zero heap
  allocation). Relies on .NET's per-operation IEEE-754 rounding with no automatic FMA
  contraction; inputs beyond ~1e150 or with subnormal differences are outside the
  analysis, as in the original. These predicates are the robustness foundation for 2D
  arrangement/sketch work (locked by tests against `BigInteger` exact arithmetic,
  including the classic Kettner ulp-grid where the naive determinant fails).
- **`Geometry2.Arrangement2d`** — planar arrangement of 2D segments: `Insert` splits
  existing edges (and the inserted segment) at proper crossings, T-junctions, and
  collinear overlaps, with all incidence DECISIONS made by the exact predicates and all
  intersection POINTS rounded to doubles under a `VertexSnapTolerance` merge (the
  standard snap-tolerance model, as geometry3Sharp's `Arrangement2d`: decisions exact,
  coordinates rounded). `ExtractCells()` walks every directed edge with the
  tightest-turn (clockwise-next-from-reverse) rule — exact angular comparisons — and
  returns `ArrangementCell2d`s: CCW outer loop, CW hole loops (disconnected island
  components, assigned to the smallest strictly-larger containing cell), net `Area`;
  the unbounded face and zero-area spur loops are dropped, dangling edges appear as
  doubled-back slits. This is the same loop-tracing dance `FaceSplitter.SplitByCurve`
  performs in UV parameter space (which additionally handles periodic wrap and stays
  independent for boolean-safety); the sketch engine's region booleans build on this class.
  `BuildCcwEdgeFans()` exposes the combinatorial embedding (per-vertex incident edge ids in
  exact CCW order) so cell tracing and boundary tracing walk ONE order, plus
  `OtherVertex`/`FindEdge` for graph navigation.
- **`Geometry2.Region2d`** — polygon-with-holes region (g3: `Polygon2d`/`GeneralPolygon2d`/
  `PlanarComplex`): one outer loop + N hole loops, loops closed implicitly (never repeat the
  first point). The canonical winding is CCW outer / CW holes — the constructor re-orients
  whatever it is given and validates that every loop encloses area, that holes lie inside the
  outer loop, and that no two loops properly cross; `Reversed()` returns the mirror winding
  for consumers with the opposite convention (`IsCounterClockwise` reports which form a
  region carries). `Area` is exact shoelace (holes subtracted, anchored at the first vertex).
  **`Contains` treats regions as CLOSED sets** — a point exactly on a loop, including exactly
  on a vertex, is inside — and is EXACT for all finite doubles: the half-open upward-crossing
  parity rule with every sidedness decision made by `Predicates2d.Orient2dSign`, so no
  crossing x is ever computed or rounded. **`Region2d.FromLoops(loops)` is the automatic
  hole detector**: it sorts an unordered bag of closed loops into regions by containment
  DEPTH — even depth = an outer boundary, odd depth = a hole of its deepest container, so an
  island inside a hole becomes a region of its own — the "nested loop hierarchy" a sketch
  front door needs. Regions are polygonal by construction; curved input is flattened to a
  chord tolerance before it reaches this type. `WithoutCollinearVertices(loop)` drops
  vertices lying EXACTLY between their neighbours (exact orientation + dominant-axis
  betweenness, so a 180-degree slit reversal is never eaten).
- **`Geometry2.Region2dBoolean`** — `Union` / `Intersection` / `Difference` of regions (or
  region *sets*, each read as the union of its members), returning a list of canonical
  regions; empty result = empty list. Both operands' loops go into one `Arrangement2d`
  (which splits crossings and dedupes coincident edges), `ExtractCells` carves the plane
  into interior-disjoint cells, each cell is classified once by membership in A and B, kept
  cells' directed loop edges are collected, and **an edge with kept cells on BOTH sides is
  interior and disappears** — what remains is the result boundary, re-traced with the
  arrangement's own CCW fan order so kept material always stays on the left (outer loops CCW,
  hole loops CW). `Region2d.FromLoops` then re-derives the nesting, so a hole CREATED by the
  operation (square minus a centred square) is detected exactly like any other, and a
  difference that splits one region in two just yields two loops. Interior sample points are
  **clearance-based and scale-free**: for each outer-loop edge take the midpoint m and the
  distance d to the nearest OTHER arrangement edge — the open disk of radius d around m meets
  no other edge, so `m + d/2` along the inward (left) normal is strictly interior — using the
  edge with the largest clearance. No triangulation, no shrink factor, no epsilon; and since
  the cell boundary contains every operand edge, the sample can never sit on A's or B's
  boundary, so `Contains`'s closed-set convention never has to decide a tie.
- **`Spatial.Bvh`** — static bounding volume hierarchy (median split on the longest
  centroid axis, flat node array, allocation-free stack traversal). The build sorts the
  item permutation per node through a **contiguous key array** (`Array.Sort(double[],
  int[])`) rather than an `IComparer<int>` over scattered centroids, and **forks sibling
  subtrees onto the thread pool** above 4096 items (capped at ~2^(log2 cores + 1) tasks so
  a nested caller cannot flood the pool); a canonical renumbering pass replays the
  sequential node numbering afterwards, so **the tree is bit-identical to the original
  builder's** — item permutation, node ranges and bounds — and independent of scheduling
  (locked by fingerprint tests in `BvhBuildOrderTests`, which every future builder rewrite
  must reproduce). Measured on 8 cores: 32 400 triangle boxes 22.6 ms → 4.6 ms (4.9×),
  130 000 random boxes 142.5 ms → 33.6 ms. Queries (all zero-allocation per query, results
  appended to caller-provided lists):
  - box overlap and ray candidate queries (`Query`);
  - `QueryAll(ray, List<BvhRayHit>)` — every item whose box the ray passes through,
    ordered by ascending box entry t (ties by item index). Collect-then-range-sort:
    measured ~1.3× faster than a strict best-first PriorityQueue traversal even with a
    reused heap (which a thread-safe API could not cache), and candidate lists are small;
  - `QueryOverlap(other, List<(int, int)>)` — tree-vs-tree broad phase: all item index
    pairs whose boxes intersect (same coordinate space; self-query yields self-pairs and
    both orderings). Candidate pairs only — exact primitive–primitive intersection (e.g.
    triangle–triangle segments for imprint booleans) belongs to the layer owning the items;
  - `Nearest` — branch-and-bound with a caller-supplied exact distance. Two forms: a
    `Func<int, double>` convenience (allocates the caller's closure) and the generic
    `Nearest<TMetric>(in point, ref metric, …) where TMetric : struct, IBvhDistance`
    (constrained call — no boxing, no closure; bit-identical traversal). Hot paths
    (`MeshSdf.Evaluate`) use the struct form.
  - **Tree-order exposure**: the median-split build sorts the item permutation in place,
    so every node owns a contiguous range of `ItemsInTreeOrder`; `Root`/`NodeView`
    (bounds, `IsLeaf`, `First`/`Count`/`Items`, `Left`/`Right`, dense `Index` for
    parallel per-node side data, `NodeCount`) let consumers permute bulk SoA data into
    tree order once and run range scans per node instead of forking their own hierarchy
    (what `MeshWindingNumber` had to do before this existed).
- **`Spatial.Octree`** — dynamic octree for incrementally changing content
  (insert/remove/query); prefer the BVH for static geometry.
- **`IndexPriorityQueue`** — array-backed binary min-heap keyed by non-negative integer
  ids with an O(1) id→slot index, so `Update` (decrease/increase-key), `Remove`, and
  `Contains` work directly — no lazy stale-duplicate entries. Grows on demand; SoA
  storage; modeled on geometry3Sharp's `IndexPriorityQueue`. Used by `MeshDecimator`.
- **`ProgressCancel`** — cooperative cancellation (from a `CancellationToken` or any
  `Func<bool>`, sticky once observed) + coarse progress reporting for long operations,
  taken as an optional trailing `ProgressCancel? progress = null` parameter (zero
  overhead when absent). Cancellation surfaces as `OperationCanceledException`; kernel
  algorithms never return partial geometry. Wired into `SurfaceNets.Polygonize` and
  `MeshDecimator.Decimate`.
- **Integer grid types** (g3: Vector2i/Vector3i/Interval1i/AxisAlignedBox3i):
  `Vector2i`/`Vector3i` (tuple conversion, operators, `ComponentProduct` as a `long`
  for overflow-safe grid sample counts, `ToVector2d/3d`), `Interval1i` (**inclusive**
  [Start, End] index interval, allocation-free `foreach`), and `AxisAlignedBox3i`
  (**inclusive** Min/Max index box: `Counts`, `Count`, per-axis `Interval1i` ranges,
  contains/overlap/intersect/expand). Adopted where they name a concept — grid
  dimension bookkeeping in `SurfaceNets` and `GridSdf`'s `GridFrame.Samples` — while
  hot flat-index arithmetic deliberately stays scalar (bit-for-bit outputs locked by
  the polygonizer determinism test).
- **`ConvexHull2`** — 2D convex hull (Andrew's monotone chain, O(n log n)); returns CCW
  strictly-convex hull vertices or indices, degrading gracefully on coincident/collinear
  input. The planar counterpart of the mesh engine's 3D quickhull.
- **Min-bounding fits** (`Fitting2d`, `Fitting3d`; g3: ContMinBox2/ContMinCircle2/
  ContOrientedBox3/OrthogonalPlaneFit3):
  - `Fitting2d.MinAreaBox` → `OrientedBox2d` — minimum-area oriented box via the
    calipers theorem (a side is collinear with a hull edge); evaluates every
    hull-edge-aligned box over the hull, O(h²) in the (small) hull size.
  - `Fitting2d.MinCircle` → `BoundingCircle2d` — Welzl minimum enclosing circle
    (iterative move-to-boundary form, deterministic seeded shuffle, expected O(n)).
  - `Fitting3d.FitPlane` → **`Frame3d`** — orthogonal-distance best-fit plane: origin
    at the centroid, Z the normal (smallest covariance eigenvector), X the dominant
    in-plane spread (largest) — a natural deterministic in-plane basis. Throws when
    the points don't determine a plane.
  - `Fitting3d.FitBox` → `OrientedBox3d` (a `Frame3d` + half extents) — PCA oriented
    box, re-centered to the tightest box with the PCA axes (good-fit heuristic, needs no
    hull, tolerates degenerate clouds).
  - `Fitting3d.MinVolumeBox(hullVertices, hullTriangles)` → `OrientedBox3d` — the
    **minimum-volume** oriented box. **The 2D calipers theorem does not lift to 3D**: the
    minimum-volume box need NOT have a face flush with a hull face (Freeman–Shapira, and a
    great many implementations, assume it does). The regular tetrahedron on alternate
    corners of [−1, 1]³ is bounded by that cube at volume 8 while every face-flush
    candidate measures 16 — locked by a test. What holds is **O'Rourke's**
    characterization: at least two ADJACENT box faces each contain a hull EDGE, which makes
    each pair of hull edge directions a one-parameter family (swept + golden-section
    refined; unordered, so only j > i is searched). Face-flush candidates are evaluated
    exactly on top of that — inside a face's plane the 2D calipers theorem *does* apply —
    and PCA + axis-aligned seed the search, so the result can never lose to `FitBox`.
    The **hull is the caller's to supply**, deliberately: Core owns no polyhedron type and
    the 3D quickhull lives in EngrCAD.Mesh because it speaks `HalfEdgeMesh`; passing the
    hull as plain data (`ConvexHull.Compute(points).Triangulated().ToIndexed()`, a B-Rep
    solid's planar faces, or one the caller already has) keeps the layering intact. Cost is
    O(E² · h) — measured 3.6 / 22 / 122 ms for 18 / 42 / 78-vertex hulls on 8 cores, with
    the edge loop parallelized deterministically (own slot per index, in-order reduction).
  - All built on an internal cyclic-Jacobi `SymmetricEigen3` (3×3 symmetric
    eigen-decomposition, unconditionally convergent).
- **`ParallelFor.Blocks(from, to, body, minBlockSize)`** — thin block-parallel-for over
  index ranges (g3's `gParallel.BlockStartEnd`): splits the range into a bounded number
  of large contiguous blocks so each worker touches a contiguous slice of the underlying
  SoA arrays. Supported pattern: every index writes only its own output slot, which
  makes results bit-for-bit deterministic regardless of scheduling. Used by the
  `SurfaceNets` sampling phase and the dense `GridSdf` bake.

## Deliberately not adopted (from the geometry3Sharp survey)

- **`DVector<T>`** (chunked growable array) and **`MemoryPool<T>`** — g3 needed them on
  old runtimes to dodge LOH resize copies and GC pressure. On modern .NET,
  `List<T>` with a capacity hint, exact-size arrays, and `ArrayPool<T>.Shared` (already
  used in the bakes) cover every current call site; no measured need exists in this
  codebase. Revisit only with a profile in hand.

## Conventions

- Public API passes structs by `in`; note that **expression trees cannot call methods
  with `in` parameters** — LINQ-visible wrappers live in `EngrCAD.Query`.
- Exact equality (`==`, `Equals`) is bitwise; geometric equality goes through
  `AreEqual(other, tolerance)`.
