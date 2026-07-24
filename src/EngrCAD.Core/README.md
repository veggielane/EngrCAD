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
  independent for boolean-safety); a future sketch engine builds on this class.
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
