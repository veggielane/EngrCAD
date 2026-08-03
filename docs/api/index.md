# API reference

Generated from the XML documentation comments in the `src/*` projects. This subtree is
built by DocFX; the rest of the documentation — the guides and the executable examples —
is [back at the site root](../). (The link is relative on purpose: it works whether the
site is served from a domain root or from a repository subpath.)

Start with these types:

- `EngrCAD.Modeling.Shape` — the unified modeling API (primitives, sketch operations,
  booleans, blends, transforms, patterns, holes, chamfer/fillet, `ToBrep`/
  `ToImplicit`/`ToMesh`/`Explain`).
- `EngrCAD.Modeling.Sketch` / `SketchBuilder` / `SketchPlane` — 2D sketching.
- `EngrCAD.Modeling.Scene` / `Part` / `Tab` — the document model.
- `EngrCAD.Modeling.Feature` / `FeatureHistory` — parametric features.
- `EngrCAD.Viewer.EngrCad` — `Show`, `ShowLive`, `Run`, `RenderToImage`.
- `EngrCAD.Implicit.Sdf` — the signed-distance-field AST.
- `EngrCAD.Mesh.HalfEdgeMesh` — the half-edge mesh engine.
- `EngrCAD.BRep.BrepSolid` / `SolidFactory` / `BrepQueries` — the B-Rep engine.
- `EngrCAD.Query.SpatialCollection<T>` — LINQ spatial querying.
