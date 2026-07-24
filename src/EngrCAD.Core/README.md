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
  centroid axis, flat node array, allocation-free stack traversal). Queries: box overlap,
  ray, and `Nearest` (branch-and-bound with a caller-supplied exact distance function).
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
