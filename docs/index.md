# EngrCAD

A CAD kernel for modern .NET built around a **hybrid geometry engine** that natively
supports three representations:

- **B-Rep** — parametric surfaces (planes, conics, NURBS) wrapped in topology, for
  precision modeling and STEP exchange.
- **Implicit** — signed distance fields (SDF) composed as an AST of primitives and
  operators, for lattices, shells, and organic blends.
- **Mesh** — discrete half-edge triangle meshes, for rendering, FEA, and 3D printing.

The unified [`Shape` API](examples/representations.md) lets you model once with one
vocabulary and choose the representation at the end:

```csharp
var body = Shape.Box(40, 30, 10) - Shape.Cylinder(4, 12).Translate(10, 8, 0);

BrepSolid    exact = body.ToBrep();      // precision modeling, STEP export
Sdf          field = body.ToImplicit();  // blends, shells, lattices
HalfEdgeMesh mesh  = body.ToMesh();      // rendering, FEA, 3D printing
```

## Where to go

- **[Getting started](getting-started.md)** — install, build a first model, and run
  the live-modeling loop (`dotnet watch` + hot reload).
- **[Examples](examples/toc.yml)** — one page per feature, each with runnable code and
  the render it produces. Every snippet is compiled, executed, and screenshotted by
  the documentation build itself, so the examples cannot drift from the code.
- **[API reference](api/index.md)** — generated from the source XML documentation.

## Highlights

- **LINQ-native querying** — a custom `IQueryable` provider routes spatial predicates
  to BVH indexes instead of linear scans; B-Rep topology is LINQ-queryable and drives
  feature selectors ([queries](examples/queries.md),
  [chamfer & fillet](examples/chamfer-fillet.md)).
- **Sketch-first modeling** — fluent 2D sketches (lines, arcs, béziers, primitive
  shapes, holes) consumed by extrude/revolve/sweep, exact in every representation
  ([sketching](examples/sketching.md)).
- **Parametric features** — FeatureScript-style history with `[Param]` properties,
  prefix-cached regeneration, and JSON parameter round-tripping
  ([features](examples/features.md)).
- **Standards-aware holes** — metric clearance / counterbore / countersink / tap
  pilot catalogs behind one `Drill` call ([holes](examples/holes.md)).
- **Modeled threads** — the ISO metric coarse catalog with real helical geometry
  and 3D-printing clearances ([threads](examples/threads.md)).
- **Assemblies** — shared parts posed by rigid frames, nested sub-assemblies, and
  flattened instances for viewers and exporters
  ([assemblies](examples/assemblies.md)).
- **A viewer that is a library** — build a `Scene`, call `EngrCad.Show`, or render
  headless PNGs for CI and agents ([viewer](examples/viewer.md)).
