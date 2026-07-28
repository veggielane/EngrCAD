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
  components, assigned to the smallest strictly-larger containing cell **from a different
  connected component** — a loop reachable from the cell's own loop would have been traced
  as part of it, so same-component nesting is structurally impossible; that rule is what
  stops a lone convex cell from adopting its own reversed perimeter as a hole when the two
  shoelace sums differ by an ULP), net `Area`;
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
- **`Geometry2.Region2dValidation`** — the simplicity guard every region consumer assumes and
  none of them used to check: does any pair of segments PROPERLY cross, inside one loop or
  between two? A self-intersecting outer loop is not a region at all — its "interior" depends
  on which fill rule you happen to apply, so `Area`, `Contains` and every boolean silently
  disagree — and before this, loops were checked against every *other* loop and never against
  themselves. `TryFindSelfIntersection` / `TryFindCrossing` report a `LoopCrossing` (which
  segments of which loops, plus the crossing point *for the message only* — the DECISION is
  exact `Predicates2d.Orient2dSign`, the coordinate is not); `Require` throws naming the loop
  in the caller's own vocabulary. Two rules worth knowing: **touching is not crossing** (a
  shared vertex or a collinear run stays legal, matching the convention `Region2d` already
  documented for hole-versus-outer contact), and **simplicity is checked BEFORE the
  enclosed-area test**, because a bow-tie with equal lobes has a signed area of exactly zero
  and would otherwise be refused for the wrong reason — or, in `FromLoops`, silently DROPPED
  by the zero-area filter. `FromLoops` checks only self-crossings, since its bag is unsorted
  and two loops in it are not yet known to share a region; loops that end up in one region are
  cross-checked by the constructor. Candidate pairs come from a `Bvh` over the segment boxes
  above 24 segments and from an all-pairs scan below it, so validation does not turn every
  sketch into an O(n²) pass.
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
  **`UnionAll(regions)`** unions MANY regions as a **balanced tree** rather than a linear
  fold: one arrangement is O(E²) in its total input, and a linear accumulate arranges every
  operand against the whole running union, whereas halving recursively keeps most
  arrangements small and lets each merge discard its children's interior edges before
  climbing. Union is associative, so the answer is identical — only the cost changes. Feed
  spatially sorted input when you have it.
- **`Geometry2.Region2dOffset`** — polygon offsetting (`Offset(region|regions, delta, join,
  miterLimit, arcTolerance)`), i.e. OpenSCAD's `offset(r=…)` / `offset(delta=…, chamfer=…)`
  and the geometry behind shells, pockets, clearances and cutter compensation. **The
  algorithm is a union, not an edge chase**: an outward offset by d is exactly
  `R ∪ (⋃ edge slabs) ∪ (⋃ corner joins)` — a point outside R but within d of it is nearest
  either to an edge interior (so it lies in that edge's slab, the rectangle swept d along the
  outward normal) or to a vertex (so it lies in that vertex's corner primitive) — and every
  primitive is a small convex polygon handed to `Region2dBoolean.UnionAll`. That is why
  offsetting had to wait for the arrangement-based boolean, and it is why **self-intersection
  is a non-issue**: there is no loop to invert, so an inward offset that eats through a neck
  simply returns two regions, or none. `OffsetJoin.Round` builds inscribed polygonal arcs
  (vertices exactly on the true offset circle, sagitta ≤ `arcTolerance`, so results sit just
  inside the true Minkowski sum — the same one-sided contract as `Sketch.ToRegions`
  flattening); `Miter` extends the two offset edges to their intersection, falling back to
  `Chamfer` past `miterLimit` (default 2, Clipper's); `Chamfer` bevels straight across.
  Straight-edge geometry is EXACT under every style — a mitered square is the larger square
  with four corners, not eight, which needs the miter apex computed from `sum.LengthSquared`
  and never from `sum.Length` squared (√2² is 2.0000000000000004, enough to tilt the apex a
  few ULPs off both offset edge lines so the collinear T-junctions stop collapsing).
  **Inward offsets are outward offsets of the complement** — `B \ dilate(B \ R, d)` with B =
  R's bounds grown by 3d — so there is no second algorithm and no special case for necks,
  islands, or holes merging. Cost is the union's: measured ~30 ms for a 16-gon and ~260 ms
  for a 512-gon outward round offset.
  **`Stroke(path, width, cap, join)`** dilates an OPEN polyline into a constant-width
  region — toolpath footprints, slots from centre lines, SVG strokes — by the same
  union: one full-width slab per segment, corner joins offered on BOTH sides of every
  interior vertex (the inner side's wedge is already inside its slabs, so only the
  outer gap changes the union — and a 180° reversal legitimately fills both, which is
  the round nose on a doubled-back path), and `StrokeCap.Butt/Round/Square` ends.
  Self-crossing paths just work (a union covers the overlap once) and a closed
  circuit (first point repeated) encloses its hole. With round caps and joins a
  stroke is the path's Minkowski sum with a disk, short only of the inscribed-arc
  sagitta; straight-segment butt/square/miter strokes are exact.

  boundary, so `Contains`'s closed-set convention never has to decide a tie. The clearance
  comes from a **`Bvh` over the arrangement's edges built once per boolean** (edges embedded
  at z = 0, so the branch-and-bound's box distance is exactly the 2D one), replacing a
  per-loop-edge linear scan that made classification O(E²) — **bit-identical**, because only
  the minimum DISTANCE is used and a minimum over doubles does not depend on visit order.
  Measured on a union of 120 overlapping 32-gons (7 776 arrangement edges, 1 969 cells):
  classification 367.7 ms → 8.8 ms, whole union 436.2 ms → 93.6 ms.
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
  input. The planar counterpart of the mesh engine's 3D quickhull. **The turn test is
  `Predicates2d.Orient2dSign`, not a raw cross product**: a chain is a sequence of
  orientation decisions that must be mutually consistent, and a naive determinant on
  points spread over a wide exponent range (where coordinate differences stop being
  exact) reports contradictory turns and pops vertices that belong to the hull —
  measured on ~7% of near-collinear mixed-magnitude clouds, and locked by
  `ConvexHull2Tests.NearCollinear_MixedMagnitudeSlivers_*`. Hull output is now verified
  against BigInteger ground truth (`ExactReference`) rather than a tolerance: strictly
  convex, enclosing every input point, exactly.
- **`PolylineSimplify`** — Douglas–Peucker simplification in 2D and 3D (g3's
  `PolySimplification2` role): `Simplify` for open chains, `SimplifyLoop` for implicitly
  closed ones, `MaxDeviation` to report what a simplification actually cost. It is the
  INEXACT companion to `Region2d.WithoutCollinearVertices`, which drops only vertices that
  provably change nothing — use this on traced intersection curves (`PolylineCurve3d
  .Simplified`), imported profiles, and any polyline whose sample density says more about how
  it was produced than about its shape. Guarantees: every dropped vertex is within the
  tolerance of the retained chord that replaced it, measured to the SEGMENT (a spike that
  doubles back along its own line is 0 from that line's extension and would vanish otherwise);
  endpoints always kept; splitting always at the farthest vertex, so it is deterministic with
  no ordering effects; and the output is a SUBSEQUENCE — retained points are bit-for-bit the
  originals. Not guaranteed: topology. Two far-apart stretches of a wiggly loop can be pulled
  onto each other, so a simplified loop may self-intersect where the input did not — which is
  why nothing in the kernel simplifies implicitly and why `Region2dValidation` catches it for
  callers who feed the result to `Region2d`. **The tolerance is absolute and in model units on
  purpose**: it is a deviation the caller CHOOSES to accept, not a degeneracy guard, and only
  the latter are relative per the epsilon ladder. The closed case cuts the loop at its first
  vertex and the vertex farthest from it (the standard anchor pair, so a near-degenerate first
  chord cannot decide the whole simplification) and never returns fewer than 3 points. The 2D
  and 3D bodies share one recursion through a struct-constrained chord metric, the
  `Bvh.Nearest<TMetric>` idiom — no interface dispatch, no boxing, and no second copy of
  Douglas–Peucker to drift.
- **`Arrangement2d` edge broad phase** — insertion used to test the new segment against
  EVERY existing edge, so a k-segment build was quadratic in exact-predicate calls (the
  workload `Region2dBoolean` feeds it: hundreds of chords per flattened loop). Edges are
  now bucketed in a uniform hash grid — each edge in the single cell of its bounding-box
  midpoint, cells sized at 4x the mean edge extent, rebuilt whenever the edge count
  doubles; edges longer than a cell go in an always-scanned overflow list — and a query
  walks the cells its own box covers plus one ring. Measured on a 30x30 line grid
  (12 640 edges): **9.1% of the edge-tests the full scan performed**.
  <br>Three properties keep it exact and cheap: an event needs a point shared by both
  segments, hence overlapping boxes, so the grid only removes edges that *provably*
  cannot interact (every survivor is decided by exactly the same predicates as before);
  `SplitEdge` only ever SHRINKS an edge, so a stale entry refers to a box containing the
  real one — over-approximation, never a miss; and nothing below `MinIndexedEdges` (256)
  builds an index at all, so sketch-scale arrangements are untouched.
- **`Distance3d`** — closest-point queries against 3D primitives, in terms of plain points
  so every engine can call them. `ClosestPointOnTriangle(p, a, b, c)` is Ericson's
  Voronoi-region form (Real-Time Collision Detection §5.1.5): six barycentric sign tests
  locate the feature and the answer is exact for it, with **no tolerance anywhere** — the
  only comparisons against a constant are exact-zero division guards (the epsilon ladder's
  algorithmic tier), which is what keeps a collapsed or sliver triangle returning a point
  on itself rather than a NaN. The `out TriangleRegion` overload also reports *which*
  feature (vertex, edge, interior) won, because a signed distance field picking the
  angle-weighted pseudonormal of the closest feature cannot reconstruct that from the point
  alone; the plain overload delegates to it, so the two can never disagree.
  `DistanceSquaredToTriangle` is the form branch-and-bound nearest queries want. This lives
  here because it had been written twice — privately in EngrCAD.Mesh's
  `MeshProjectionTarget` and again in Interop's `MeshSdf` — and the two copies had already
  drifted (only one carried the degeneracy guards).
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
    box, re-centered to the tightest box with the PCA axes. **A heuristic, not the
    minimum-volume box**, and not a small gap: PCA orients by how the points are
    DISTRIBUTED, so sampling density changes the axes even when the shape does not.
    Good fit, needs no hull, tolerates degenerate clouds; the exact method is
    `MinVolumeBox` below, which takes a caller-supplied hull.
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
  - All built on **`SymmetricEigen3`** (cyclic-Jacobi 3×3 symmetric eigen-decomposition,
    unconditionally convergent) — now public with **both orderings**
    (`SolveDescending` for fitting's dominant-first convention, `SolveAscending` for the
    principal-inertia convention), which is what let `EngrCAD.Mesh` delete the
    near-verbatim internal copy it carried just to re-sort.
- **`Solvers`** (namespace `EngrCAD.Core.Solvers`) — a small sparse linear-algebra
  library for symmetric positive-definite systems: the numerical substrate for the mesh
  engine's Laplacian smoothing/deformation, and deliberately dependency-free and
  mesh-agnostic (doubles + int indices only) because the future sketch constraint solver
  and FEA assembly will sit on the same three types.
  - **`PackedSparseMatrix`** — immutable CSR (row-start offsets + column indices +
    values, rows sorted by column), with an optional **symmetric-upper storage** form
    that keeps only the upper triangle of a square symmetric matrix and mirrors
    off-diagonals during `Multiply` (half the memory and bandwidth). Assembly goes
    through **`SparseMatrixBuilder`** (finite-element style: `Add(r, c, v)` accumulates
    duplicates; packing stable-sorts per row so assembly is deterministic for a
    deterministic add sequence; symmetric-upper packing *rejects* lower-triangle adds
    rather than mirroring them, since a mirror would double-count a convention-following
    assembly). Also: `Multiply(other)` (Gustavson row-merge SpMM — the bi-Laplacian L²
    construction), `ToGeneral()`/`ToSymmetricUpper()` (the latter takes the stored upper
    triangle as truth without comparing the lower, because the two halves of a
    numerically symmetric product differ in their last bits — same terms, different
    summation order), `Diagonal`, row-span accessors.
  - **`SparseSymmetricCG`** — Jacobi-preconditioned conjugate gradients. Deterministic
    (fixed-order sequential reductions — a parallel dot product would change last bits
    run to run). Convergence is a **return value, not a log line**: `SparseSolveReport`
    carries converged/iterations/residual, and a non-SPD search direction breaks out
    honestly instead of dividing by nonpositive curvature. The preconditioner's
    nonpositive-diagonal guard is a sign test, deliberately not a `Tolerance` comparison.
  - **`SparseCholesky`** — up-looking sparse Cholesky (elimination tree + per-row
    ereach, Davis ch. 4): factor once, forward/back-substitute per right-hand side —
    the shape of every Laplacian mesh solve, where x/y/z share one operator. Nonpositive
    pivots throw naming the column. **Natural ordering, measured rather than assumed**
    (Release, win-x64, 5-point grid Laplacians — the coherent-numbered mesh stand-in):
    fill is 17–80× nnz(A) but factor+solve stays cheap at deformation-ROI scale
    (2.5k unknowns: 4.7 ms factor / 0.3 ms solve; 6.4k: 20.7 / 1.5; 14.4k: 133 / 5.5),
    and past ~14k unknowns one-shot CG beats factor+3-RHS-solve (62.5k: CG 24.5 ms vs
    1 625 ms factor) — so the factorization is for many-RHS reuse (interactive
    deformation re-solves), CG for one-shot smoothing at scale, and an AMD/RCM
    fill-reducing ordering is filed follow-up work for FEA-scale systems.
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
