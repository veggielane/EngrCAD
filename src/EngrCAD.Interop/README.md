# EngrCAD.Interop

Conversions between the three geometry representations. References `EngrCAD.Mesh`,
`EngrCAD.Implicit`, and `EngrCAD.BRep` — the only kernel project allowed to depend on all
engines.

## The conversion triangle

- **Implicit → Mesh**: `SurfaceNets.Polygonize(sdf, region?, resolution)` — manifold
  Surface Nets (dual contouring): one vertex per *connected component of inside corners*
  per cell (plain one-vertex-per-cell produces non-manifold edges on thin sheets and
  saddles), one quad per interior sign-changing grid edge, wound outward. Surfaces
  crossing the sampling region come out open there.
- **B-Rep → Mesh**: `BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples)` —
  each edge is sampled once into a shared polyline; planar faces (any number of loops)
  ear-clip via `PolygonTriangulator`; cylinder bands and the generated surfaces
  (extruded/revolved/swept) tessellate as parameter grids whose samples match the shared
  edge polylines exactly; everything is welded (with seam zipping to repair T-junctions
  from earcut's collinear filtering).
- **B-Rep booleans**: `BrepBoolean.Union/Intersection/Difference` — the full pipeline
  (face-pair intersection, seam-aligned splitting, SDF-probe classification, reversed
  subtracted faces, topological seam sealing via `TopologyEditor.SealSeams`). See
  design.md §5. v1 handles transversal cases; inputs are consumed; output passes
  `Validate()` with correct genus and exact volumes.

(The `Scene`/`Part` document model lives in `EngrCAD.Modeling`, which layers on top of
this project's conversions.)
- **Mesh → Implicit**: `MeshSdf(mesh)` — signed distance to a closed manifold mesh:
  branch-and-bound nearest-triangle search over a BVH (Ericson closest-point-on-triangle);
  sign from the angle-weighted pseudonormal of the closest feature (Bærentzen–Aanæs),
  exact for watertight meshes even at edges and vertices. The result is a first-class
  `Sdf` node composable with the whole implicit engine.
  The sign source is opt-in via `new MeshSdf(mesh, MeshSignSource.WindingNumber)`, which
  drives the fast generalized winding number (`MeshWindingNumber` in EngrCAD.Mesh) instead
  of the pseudonormal — same partition on watertight meshes, but also accepts **open**
  (non-watertight) meshes, where the distance is still to the existing surface and the sign
  degrades gracefully near holes. The default (`MeshSignSource.Pseudonormal`) is unchanged
  and still requires a closed mesh.
