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
- **`HoleFiller`** — hole filling for open meshes, construct-new (g3 `SimpleHoleFiller` /
  `PlanarHoleFiller` / `AutoHoleFill` dispatch). Boundary half-edges are wound opposite
  their interior twins, so fill faces that follow the boundary walk order supply exactly
  the free directed edges and `Build` welds them manifold. `FillSimple(mesh, loop)` —
  centroid-vertex triangle fan (single triangle for 3-loops); refuses large wildly
  non-planar loops (plane deviation above a fixed fraction of the loop extent — a
  scale-free shape guard, not a tolerance) where a fan would self-intersect.
  `FillPlanar(mesh, loop[s])` — best-fit plane (`Fitting3d.FitPlane` → `Frame3d`),
  project, ear-clip via `PolygonTriangulator`, map back; multiple loops sharing one plane
  become polygon-with-holes fills (after projection, CCW loops are outers and CW loops are
  holes — walk orientation encodes nesting intrinsically; each hole goes to its smallest
  containing outer), which handles the annular case `MeshPlaneCut` refuses to cap.
  Earcut-dropped exactly-collinear vertices are re-expanded by a ring-aware chord zip
  (hole bridges are never chords). `FillAll(mesh, options)` dispatches per hole and
  reports a `HoleFillOutcome` per boundary loop: planar where the loop fits a plane
  within `PlanarityTolerance` (absolute, default weld 1e-9 — exact cut/tessellation rims
  qualify, curved rims miss by their sagitta), grouped by common plane; else simple under
  `MaxSimpleFillVertices`; else `Skipped` with the reason. The smoothed / minimal-surface
  fill tiers of g3's `AutoHoleFill` are future work.
- **`MeshExtrude`** — construct-new extrusion ops (g3 `MeshExtrudeFaces` /
  `MeshExtrudeMesh`). `Faces(mesh, faceIndices, offsetVector | distance)` pulls a face
  patch off the mesh: patch vertices shared with the rest (or on the open mesh boundary)
  are duplicated at the offset position, interior patch vertices move in place, and each
  patch-boundary half-edge a→b gains the wall quad [a, b, b′, a′] — exactly the two
  directed edges freed by moving the patch, so winding is correct by construction and
  closed meshes stay closed (multiple disjoint regions each get their own walls; input
  face indices survive, walls appended). The distance form offsets along area-weighted
  patch-only vertex normals. `Thicken(mesh, thickness)` turns a surface into a solid
  shell — the direct-mesh complement of SDF `Shell`: the input stays as the front skin,
  a reversed copy offsets <i>against</i> the vertex normals (material behind the
  surface), and each boundary loop is stitched with a quad band; open surfaces become
  closed slabs/shells, closed meshes become hollow two-shell solids.
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
- **`MeshRepair`** — repair pipeline v1 for dirty soups (construct-new; inputs never
  mutated): crack welding at `MeshRepairOptions.WeldTolerance` (default = the 1e-7
  seam tier — cracks are independently authored geometry on two sides of a seam) →
  degenerate-face removal (triangle minimum altitude / polygon Newell-area-vs-perimeter
  below the weld distance = numerically weldable noise) → duplicate-face removal
  (canonical vertex cycle, either winding; must precede orientation — duplicates
  overload edges past two uses and would block the flood) → orientation
  (per-component BFS flood crossing only clean two-use edges, then an outward vote:
  **signed volume for closed components** — the winding integral in closed form —
  and **generalized winding-number probes for open ones**, probing both sides of the
  largest faces and deciding by the sign wherever |w| ≥ 0.25; a single
  behind-the-normal probe is provably blind to inward-wound sheets, since the
  winding is ~0 on the exterior side under either orientation) → T-junction seam
  zip (`MeshWelder`'s pass) → manifold `Build`. `Clean(...)` overloads take a
  `MeshReadResult`, an existing `HalfEdgeMesh` (polygons preserved — also useful to
  right an inside-out but manifold import), or a raw soup, and return the repaired
  mesh + a `MeshRepairReport` (vertices merged, duplicates/degenerates removed,
  components, faces rewound, components flipped, T-junction insertions, closedness,
  notes). Defects needing topological surgery (fins, hole filling) still fail the
  final `Build` — loudly, with post-repair edge diagnostics — until the Euler
  operators land. Known v1 limitation: nested cavity shells are oriented outward
  like everything else. `MeshReader.ReadAndRepair(path, options?)` is the one-call
  import path (read welds exactly at 1e-9; repair applies the crack weld).
