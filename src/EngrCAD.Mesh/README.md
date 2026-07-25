# EngrCAD.Mesh

The discrete (mesh) geometry engine: a half-edge polygon mesh with construction,
traversal, metrics, algorithms, and GPU/export extraction. Depends only on
`EngrCAD.Core`.

## Contents

- **`HalfEdgeMesh`** — half-edge (DCEL) structure over struct-of-arrays storage.
  Boundary edges get explicit half-edges chained into boundary loops, so `Twin` always
  exists. `Build(positions, faces)` validates manifoldness (rejects non-manifold edges,
  inconsistent winding, bow-tie vertices); `Validate()` checks structural invariants.
  Metrics: surface area, signed volume (`Volume` requires closed topology,
  `SignedVolume` doesn't), Euler characteristic, boundary loops, bounds.
  Topology is immutable after `Build`; algorithms produce new meshes.
  `Transformed(matrix)` maps every position through an affine matrix in one call;
  negative-determinant maps (mirrors) reverse face winding so closed solids stay
  outward-oriented with positive volume.
- **Handles** (`Vertex`, `HalfEdge`, `Face`) — cheap struct wrappers designed for fluent
  LINQ traversal (`vertex.OutgoingHalfEdges()`, `face.AdjacentFaces()`, `face.Bounds`).
- **`EditableMesh`** — the mutable companion (index-based, like g3's `DMesh3`;
  `HalfEdgeMesh` stays immutable): `FromMesh` → guarded Euler operators → `ToMesh`
  (compacts live elements and re-validates through the manifold-checking `Build`).
  Same SoA half-edge layout with explicit boundary half-edges, plus alive flags and
  free lists chained through the dead slots (freed indices are recycled; live indices
  never move until compaction), and `Timestamp`/`ShapeTimestamp` counters so caches and
  spatial trees can invalidate. Operators return a `MeshOperationResult` and run their
  entire guard set **before** the first mutation — a refusal never touches the mesh:
  - `SplitEdge` (interior 2→4 triangles / boundary 1→2 + boundary-chain insert; exact
    lerp position at parameter *t* measured from the passed half-edge's origin),
  - `FlipEdge` (interior triangle pairs; refuses when the target edge exists, which
    also blocks all valence-3 endpoint flips),
  - `CollapseEdge` (destination merges into origin; link condition — shared neighbors
    must be exactly the opposite apexes — plus interior-edge both-endpoints-boundary
    bow-tie, duplicate-edge, isolated-tetrahedron, and last-triangle guards; the
    decimator keeps its own equivalent on its face-set scratch state),
  - `PokeFace` (any n-gon → fan around centroid or explicit point),
  - `MergeEdges` (welds two **boundary half-edges** head-to-tail — the crack-closing
    primitive; handles shared-endpoint seams and full slit closure, and automatically
    welds the doubled boundary edges the guards deliberately admit, g3-style).
  Every vertex keeps the invariant that its outgoing pointer prefers the boundary
  half-edge, so `IsBoundaryVertex` stays O(1) through arbitrary edit sequences.
  `Validate()` checks the full structure (twin involution, ring closure/completeness,
  single boundary fan per vertex, free-list integrity, count agreement) and runs
  automatically after every mutation in DEBUG builds.
- **`MeshChange` / `MeshChangeSet`** — undo/redo change records (g3 `DMesh3Changes`
  pattern implemented as an exact slot journal): while a change set is active
  (`BeginChangeSet`/`EndChangeSet`), every operation emits a reversible record of its
  primitive slot writes (old → new, free lists and counters included). `Apply`/`Revert`
  verify each slot before writing — out-of-order replay throws instead of corrupting —
  and do → revert restores the storage **bit-identically**. Timestamps are
  cache-invalidation counters, not state, and are excluded from the round-trip.
- **`MeshPrimitives`** — box, UV sphere, cylinder, cone frustum (`Cone(r1, r2, h)`;
  zero radius = apex fan; true n-gon caps). Outward CCW winding.
- **`ConvexHull`** — 3D quickhull over point sets → closed triangle mesh. One
  extent-scaled epsilon for all visibility tests; points within it of a face plane are
  absorbed (coplanar regions come out as consistent triangulations of true hull
  vertices, not sliver fans); degenerate inputs (&lt; 4 points, coincident, collinear,
  coplanar) throw with the reason. Output goes through the manifold-validating `Build`
  as a safety net.
- **`MeshBoolean`** — union/difference/intersection via BSP clipping (csg.js) plus a
  seam-zipping pass so results come out topologically closed.
- **`LoopSubdivision`** — triangle-mesh Loop subdivision with boundary rules.
- **`MeshDecimator`** — quadric error metric (Garland–Heckbert) edge collapse with link
  condition and normal-flip guards; boundaries are preserved exactly. Candidates live in
  Core's `IndexPriorityQueue` (one always-current entry per undirected edge, re-keyed in
  place on neighborhood changes — replaced the lazy stamped-duplicates queue at equal
  speed and equal-or-better quality). Optional `ProgressCancel` parameter reports
  progress and cancels cooperatively (`OperationCanceledException`). Gotcha preserved in
  a comment: never key edge maps by a packed `(min &lt;&lt; 32) | max` long — the default
  long hash is `lo ^ hi`, which collapses structured mesh-edge keys into a handful of
  hash buckets (measured 4× whole-algorithm slowdown); tuple keys hash properly.
- **`MeshPlaneCut`** — slices a mesh by a plane, keeping the side the normal points away
  from (material below the plane, as when slicing for printing). Builds a new mesh:
  kept faces copied, crossing faces Sutherland–Hodgman-clipped with exact line-plane
  crossing points shared per undirected edge (bitwise-identical on both sides, so
  `Build` welds without tolerance). Returns the on-plane cut loops (ordered, wound CCW
  from the normal side); `cap: true` ear-clips each loop closed, with a zip pass
  re-inserting exactly-collinear loop vertices earcut filters. Nothing-removed cuts
  return the input mesh; remove-everything cuts throw; nested (annular) loops can't be
  capped per-loop and throw `NotSupportedException` — use `cap: false` and fill yourself.
  Faces crossing the plane 3+ times (non-convex n-gons) are triangulated in their own
  plane via `PolygonTriangulator` before clipping — each piece is convex, so
  Sutherland–Hodgman never bridges separate kept regions (a vertex-0 fan only works for
  star-shaped polygons).
- **`MeshWindingNumber`** — generalized winding number (Jacobson et al. 2013) for robust
  inside/outside classification, including on **non-watertight** meshes (holes,
  self-intersections, duplicated patches) where normal/ray-parity tests fail.
  `WindingNumber` is the exact per-triangle signed-solid-angle sum (Van Oosterom–Strackee);
  `FastWindingNumber` is the Barill et al. 2018 fast approximation — triangles clustered
  in a median-split hierarchy, distant clusters evaluated by a second-order (dipole +
  quadrupole) multipole expansion of their winding field (β radius test, default 2),
  giving O(log n) queries with error far below the ½ decision threshold. `IsInside`
  thresholds at ½. Construction triangulates and indexes once; the mesh may be open.
- **`PolygonTriangulator`** — 2D triangulation with holes; a faithful port of mapbox
  earcut (minus z-order hashing).
- **`MeshWelder`** — polygon-soup → mesh via spatial-hash vertex welding, with optional
  T-junction seam zipping.
- **`RenderMesh`** — flat (per-face) or smooth (per-vertex) triangle extraction for GPUs.
- **`ObjWriter`** — minimal Wavefront OBJ export for debugging.
