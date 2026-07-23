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
- **Handles** (`Vertex`, `HalfEdge`, `Face`) — cheap struct wrappers designed for fluent
  LINQ traversal (`vertex.OutgoingHalfEdges()`, `face.AdjacentFaces()`, `face.Bounds`).
- **`MeshPrimitives`** — box, UV sphere, cylinder (true n-gon caps). Outward CCW winding.
- **`MeshBoolean`** — union/difference/intersection via BSP clipping (csg.js) plus a
  seam-zipping pass so results come out topologically closed.
- **`LoopSubdivision`** — triangle-mesh Loop subdivision with boundary rules.
- **`MeshDecimator`** — quadric error metric (Garland–Heckbert) edge collapse with link
  condition and normal-flip guards; boundaries are preserved exactly.
- **`MeshPlaneCut`** — slices a mesh by a plane, keeping the side the normal points away
  from (material below the plane, as when slicing for printing). Builds a new mesh:
  kept faces copied, crossing faces Sutherland–Hodgman-clipped with exact line-plane
  crossing points shared per undirected edge (bitwise-identical on both sides, so
  `Build` welds without tolerance). Returns the on-plane cut loops (ordered, wound CCW
  from the normal side); `cap: true` ear-clips each loop closed, with a zip pass
  re-inserting exactly-collinear loop vertices earcut filters. Nothing-removed cuts
  return the input mesh; remove-everything cuts throw; nested (annular) loops can't be
  capped per-loop and throw `NotSupportedException` — use `cap: false` and fill yourself.
- **`PolygonTriangulator`** — 2D triangulation with holes; a faithful port of mapbox
  earcut (minus z-order hashing).
- **`MeshWelder`** — polygon-soup → mesh via spatial-hash vertex welding, with optional
  T-junction seam zipping.
- **`RenderMesh`** — flat (per-face) or smooth (per-vertex) triangle extraction for GPUs.
- **`ObjWriter`** — minimal Wavefront OBJ export for debugging.
