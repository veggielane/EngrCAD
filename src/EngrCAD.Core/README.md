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
- **`Spatial.Bvh`** — static bounding volume hierarchy (median split on the longest
  centroid axis, flat node array, allocation-free stack traversal). Queries (all
  zero-allocation per query, results appended to caller-provided lists):
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

## Conventions

- Public API passes structs by `in`; note that **expression trees cannot call methods
  with `in` parameters** — LINQ-visible wrappers live in `EngrCAD.Query`.
- Exact equality (`==`, `Equals`) is bitwise; geometric equality goes through
  `AreEqual(other, tolerance)`.
