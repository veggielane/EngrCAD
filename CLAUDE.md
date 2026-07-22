# EngrCAD

A CAD application in modern .NET built around a **hybrid geometry kernel** that natively supports three representations:

- **B-Rep** — parametric surfaces (planes, conics, NURBS) wrapped in topology, for precision modeling
- **Implicit** — signed distance fields (SDF) as an AST of primitives and operators, for lattices and organic blends
- **Mesh** — discrete half-edge triangle meshes, for rendering, FEA, and 3D printing

A distinctive design goal is **LINQ-native geometry querying**: a custom `IQueryable` provider inspects expression trees and routes spatial predicates to spatial indexes (BVH/octree) instead of linear scans.

## Current status

Phases 1–3 complete (118 tests): `EngrCAD.Core` (math + spatial), `EngrCAD.Mesh` (half-edge engine incl. BSP booleans with seam zipping), `EngrCAD.Implicit` (SDF AST), and implicit→mesh in `EngrCAD.Interop` (manifold Surface Nets). The Viewer renders a two-row demo scene — mesh primitives/subdivision/boolean and SDF-derived meshes (blend, torus, gyroid lattice) — through an OpenGL viewport (Avalonia `OpenGlControlBase` + Silk.NET over ANGLE/GLES3) with a laptop-friendly orbit camera (drag orbit, shift+drag pan, ctrl+drag/scroll zoom). BRep and Query are still stubs; remaining Interop work: B-Rep tessellation, mesh→SDF.

- .NET SDK 10.0.302 installed **user-local** at `%USERPROFILE%\.dotnet` (win-arm64), on the user PATH with `DOTNET_ROOT` set. Build with `dotnet build EngrCAD.slnx`, test with `dotnet test EngrCAD.slnx`.
- Git repository initialized; commit only when Chris asks.
- Target framework: **.NET 10 (LTS)** via `Directory.Build.props`.

## Architecture

Three engines with different mathematics and data structures, plus interop and query layers on top of a shared core.

### Core (foundation for everything)
- Zero-allocation math: `readonly struct` `Vector3d`, `Matrix4x4d`, quaternions, `AABB`
- Central tolerance/epsilon policy for robust floating-point comparison
- Spatial acceleration: BVH and octree (used by all engines and by the query layer)

### Mesh engine (discrete)
- Half-edge data structure for O(1) topology traversal
- Algorithms: booleans, decimation, subdivision
- Bulk data stored data-oriented (SoA) for cache locality

### Implicit engine (volumetric)
- SDF evaluator: `(x, y, z) → distance`
- Primitives (sphere, box, cylinder, …) and operators (union, intersect, smooth blend) composed as an AST
- SIMD-batched evaluation; later, compilation of C# expression trees down to SDF graphs / IL / compute shaders

### B-Rep engine (parametric) — hardest, built last
- Geometry: planes, cylinders, cones, NURBS surfaces/curves
- Topology wrapper referencing geometry: Solid → Shell → Face → Loop → Edge → Vertex
- Surface–surface intersection engine; booleans and filleting on top

### Interop layer
- Implicit → Mesh: marching cubes / dual contouring
- B-Rep → Mesh: tessellation (also feeds the viewer)
- Mesh/B-Rep → Implicit: distance querying against discrete geometry

### Query layer (LINQ)
- Custom `IQueryProvider` + `ExpressionVisitor` that intercepts spatial predicates (`Intersects`, `Contains`, `DistanceTo(...) < x`) and answers them from the BVH/octree in O(log N), falling back to LINQ-to-Objects for residual predicates
- Metadata indexes for feature queries (e.g. cylindrical faces by radius) so B-Rep queries avoid re-evaluating surface math
- Topology-traversal extension methods for fluent mesh/B-Rep navigation (`vertex.OutgoingEdges().Where(e => e.IsSharp())…`)

## Planned solution layout

```
EngrCAD.sln
src/
  EngrCAD.Core/       math structs, tolerances, AABB, BVH, octree
  EngrCAD.Mesh/       half-edge mesh engine
  EngrCAD.Implicit/   SDF primitives, operators, evaluator
  EngrCAD.BRep/       parametric geometry + topology
  EngrCAD.Interop/    conversions between representations
  EngrCAD.Query/      IQueryable provider, spatial/topology LINQ
  EngrCAD.Viewer/     Avalonia + Silk.NET (OpenGL) desktop app
tests/
  EngrCAD.Core.Tests/ (one xUnit test project per src project)
  ...
```

Kernel projects (`Core`, `Mesh`, `Implicit`, `BRep`, `Interop`, `Query`) must stay free of UI/rendering dependencies; only `EngrCAD.Viewer` references Avalonia/Silk.NET.

## Performance mandates (non-negotiable in kernel code)

- Math types are `readonly struct`; hot paths allocate nothing on the heap
- Use `Span<T>`/`Memory<T>`; temporaries come from `ArrayPool<T>` or `stackalloc`, never `new` per call
- Use `System.Runtime.Intrinsics` (SIMD) for SDF evaluation, ray/primitive intersection, and other batch kernels
- Bulk mesh data uses structs-of-arrays, not arrays-of-objects
- Never compare floats with `==`; all comparisons go through the central tolerance policy in `EngrCAD.Core`

## Roadmap (bottom-up — do not skip ahead)

1. **Core math & spatial acceleration** ✅ done — `Tolerance`, `Vector2d`/`Vector3d` (implicit conversion from tuples), `Matrix4d` (column-vector convention), `Quaterniond`, `Aabb`, `Ray3d`, `Bvh` (static, median-split), `Octree` (dynamic)
2. **Mesh engine** ✅ done — half-edge structure (`HalfEdgeMesh` + `Vertex`/`HalfEdge`/`Face` handles for LINQ traversal), manifold-validating `Build`, boundary loops, metrics (area/volume/Euler), primitives (box, uv-sphere, n-gon-capped cylinder), triangulation, Loop subdivision, booleans (`MeshBoolean`: BSP/csg.js clipping + seam zipping for closed results; exact-intersection rewrite and decimation are future work), `RenderMesh` extraction, OBJ export; viewer renders meshes
3. **Implicit engine** ✅ done — `Sdf` AST (`Evaluate`/batch/`Normal`/conservative `Bounds`), primitives (sphere, box, cylinder, torus, capsule, half-space, gyroid lattice), operators (union/intersect/subtract with `|`/`&`/`-` overloads, smooth blends, offset, shell, translate/rotate/scale); `SurfaceNets.Polygonize` in Interop converts implicit→mesh (manifold variant: one vertex per inside-corner component per cell). Future: SIMD batch evaluation, expression-tree→SDF compilation for the Query layer
4. **B-Rep engine** 🔶 foundations done — curves (`Line3d`, `Circle3d`, `NurbsCurve` with exact rational conics), surfaces (`PlaneSurface`, `CylinderSurface`, `SphereSurface`, `NurbsSurface`), topology (`BrepSolid`→`BrepShell`→`BrepFace`→`BrepLoop`→`BrepCoedge`→`BrepEdge`→`BrepVertex` with `Validate` + Euler–Poincaré), `SolidFactory.MakeBox`/`MakeCylinder`, and `BRepTessellator` in Interop (shared edge polylines + ear clipping + welding). **Still to do: surface–surface intersection, trimmed-face tessellation (holes, NURBS faces), booleans, filleting**
5. **Interop completion** — remaining conversions, mesh↔SDF, robustness passes

The Query layer and Viewer grow alongside each engine as it lands, not as separate phases.

## Conventions

- C# `LangVersion` latest, `Nullable` enabled, `ImplicitUsings` enabled, file-scoped namespaces
- Root namespace `EngrCAD.*`, matching project names
- Tests: xUnit; every geometric algorithm gets tolerance-aware assertions
- Central build props (`Directory.Build.props`) for shared settings once scaffolded
