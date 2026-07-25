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
- **Mesh import** — `StlReader` (binary + ASCII, autodetected: the exact
  84 + 50·n byte-size test runs *before* any `solid` prefix sniffing, because binary
  exporters routinely write "solid" into the 80-byte header; the prefix + a
  printable-text check only decide when the size doesn't match), `ObjReader`
  (v/f with all index forms incl. negative relative; vt/vn/materials/groups ignored and
  reported; polygonal faces triangulated in their Newell plane via
  `PolygonTriangulator` with a fan fallback when earcut filters collinear vertices —
  a dropped vertex a neighbor still references would open an unweldable T-junction),
  `OffReader` (OFF + variants; extra color/normal columns ignored with warnings), and
  the `MeshReader` extension-dispatch facade. All readers weld coincident vertices
  (spatial hash; representatives keep their exact file coordinates — welding never
  moves geometry; tolerance parameter defaults to the 1e-9 weld tier) and attempt the
  manifold `Build`. **Dirty files don't throw**: every reader returns a
  `MeshReadResult` carrying the welded indexed soup + `MeshReadDiagnostics`
  (non-manifold / inconsistently-wound / boundary edge counts, duplicate and
  degenerate faces, parser warnings, the `Build` failure message) with `Mesh` null,
  so the repair pipeline can take over; `RequireMesh()` throws with the diagnostics
  summary. Note binary STL is float32-quantized: coincident vertices are
  bit-identical (default weld is exact), and cracks below one float ulp cannot exist,
  so repair-time crack welding on STL needs tolerances at or above ~1e-7·|coordinate|.
