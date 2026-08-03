# API reference

Generated from the XML documentation comments in the `src/*` projects. This subtree is
built by DocFX; the rest of the documentation — the guides and the executable examples —
is [back at the site root](../).

<!-- docfx warns "InvalidFileLink: (~/)" on that link and that is EXPECTED, the same way
     it used to on docs/examples/web.md's ../live/ links: the target is the Astro Starlight
     build, which .github/workflows/docs.yml merges AROUND this subtree afterwards, so no
     such file exists while docfx runs. The link is emitted verbatim, which is all that
     matters. Keep it RELATIVE -- an absolute /EngrCAD/ path would bake the repository name
     into the source and break the moment the site moves to a domain root. -->


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
