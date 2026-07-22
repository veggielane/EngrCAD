# EngrCAD.Core

Foundation library shared by every engine: double-precision math types and spatial
acceleration structures. Has no dependencies and must stay free of geometry-engine and UI
concerns.

## Contents

- **Math types** (`readonly struct`, zero-allocation): `Vector2d`, `Vector3d` (implicitly
  convertible from tuples: `(1, 2, 3)`), `Matrix4d` (row-major storage, column-vector
  convention: `p' = M·p`, so `A*B` applies `B` first), `Quaterniond` (Hamilton product
  matching matrix composition order), `Aabb`, `Ray3d`, `Interval`.
- **`Tolerance`** — the central floating-point comparison policy. Kernel code never
  compares doubles with `==`; geometric predicates take a `Tolerance` (linear + angular).
- **`Spatial.Bvh`** — static bounding volume hierarchy (median split on the longest
  centroid axis, flat node array, allocation-free stack traversal). Queries: box overlap,
  ray, and `Nearest` (branch-and-bound with a caller-supplied exact distance function).
- **`Spatial.Octree`** — dynamic octree for incrementally changing content
  (insert/remove/query); prefer the BVH for static geometry.

## Conventions

- Public API passes structs by `in`; note that **expression trees cannot call methods
  with `in` parameters** — LINQ-visible wrappers live in `EngrCAD.Query`.
- Exact equality (`==`, `Equals`) is bitwise; geometric equality goes through
  `AreEqual(other, tolerance)`.
