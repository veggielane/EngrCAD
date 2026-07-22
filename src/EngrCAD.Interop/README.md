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

## The scene model

`Scene` is the document design code builds and the viewer displays: an ordered set of
named `Part`s (tessellated mesh + color + placement + original `Source` geometry).
`Scene.Add` accepts geometry from any engine — `BrepSolid` via `BRepTessellator`,
`Sdf` via `SurfaceNets`, `HalfEdgeMesh` as-is — with quality set by `SceneOptions`.
Parts added without a color cycle through `Palette`; names must be unique and non-empty;
`Scene.Bounds()` (transforms applied) drives camera auto-framing. UI-free by design, so
scripts, tests, and headless exporters can build scenes without Avalonia.
- **Mesh → Implicit**: `MeshSdf(mesh)` — signed distance to a closed manifold mesh:
  branch-and-bound nearest-triangle search over a BVH (Ericson closest-point-on-triangle);
  sign from the angle-weighted pseudonormal of the closest feature (Bærentzen–Aanæs),
  exact for watertight meshes even at edges and vertices. The result is a first-class
  `Sdf` node composable with the whole implicit engine.
