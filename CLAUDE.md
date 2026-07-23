# EngrCAD

**Detailed design rationale lives in [design.md](design.md)** (architecture, conventions,
algorithm choices, numerical lessons, roadmap); each `src/*` project also has a
`README.md` describing its contents. **[todo.md](todo.md)** is the idea backlog
(largely harvested from a survey of geometry3Sharp at
`C:\Users\chris\projects\git\geometry3Sharp` — study the referenced g3 classes before
implementing an item). Keep all of these in sync when landing features.

A CAD application in modern .NET built around a **hybrid geometry kernel** that natively supports three representations:

- **B-Rep** — parametric surfaces (planes, conics, NURBS) wrapped in topology, for precision modeling
- **Implicit** — signed distance fields (SDF) as an AST of primitives and operators, for lattices and organic blends
- **Mesh** — discrete half-edge triangle meshes, for rendering, FEA, and 3D printing

A distinctive design goal is **LINQ-native geometry querying**: a custom `IQueryable` provider inspects expression trees and routes spatial predicates to spatial indexes (BVH/octree) instead of linear scans.

## Current status

Phases 1–4 plus Query v1 implemented (~248 tests): `EngrCAD.Core` (math + spatial incl. `Bvh.Nearest`), `EngrCAD.Mesh` (half-edge engine: booleans with seam zipping, Loop subdivision, QEM decimation), `EngrCAD.Implicit` (SDF AST), `EngrCAD.BRep` (curves/surfaces/topology foundations), `EngrCAD.Interop` (the full conversion triangle: implicit→mesh via manifold Surface Nets, B-Rep→mesh via tessellation, mesh→implicit via `MeshSdf` with angle-weighted pseudonormals), `EngrCAD.Query` (BVH-backed IQueryable), **`EngrCAD.Modeling` (the unified `Shape` API: model once with one vocabulary — primitives incl. `MakeSphere`/`MakeTorus`, extrude/revolve/sweep, booleans, smooth blends/offset/shell/lattice, transforms — then decide the representation at the end: `ToBrep()`/`ToImplicit()`/`ToMesh()`; lowering bakes accumulated transforms into construction inputs for exact B-Reps, bridges non-native nodes through tessellation + `MeshSdf` or Surface Nets, and `Explain(target)` reports each node as Native/Bridged/Impossible — see `src/EngrCAD.Modeling/README.md` for the support matrix; `Shape.From(...)` wraps raw engine geometry so designs can drop down to any engine API and come back). Modeling also owns the document model: `Part` (name + any-engine geometry + color + transform; `GetMesh(quality)` produces/caches the display mesh, `Scene.PreMesh()` runs it off the render thread) grouped into named `Tab`s in a `Scene` (`scene.Add(part)` shorthand targets a default "Model" tab; part names unique per tab; `Part` stays a leaf so assemblies can join tabs later)**. **The Viewer is a library, not an app**: design code builds a `Scene` and calls `EngrCad.Show(scene)` (blocking; one call per process — Avalonia lifetime constraint; `onViewportReady` callback + thread-safe `ViewportControl.SetParts` support custom hosts; the internal `SceneHost` renders the tab strip with per-tab cameras — auto-framed on first visit, remembered after, kept in place on live reload). Rendering is an OpenGL viewport (Avalonia `OpenGlControlBase` + Silk.NET over ANGLE/GLES3) with a laptop-friendly orbit camera (drag orbit, shift+drag pan, ctrl+drag/scroll zoom, keyboard fallbacks) and click-picking that reports part *names*. **CAD chrome (dark theme)**: toolbar (Fit, Front/Top/Right/Iso, perspective/orthographic toggle — ortho frustum sized to keep the target plane's apparent size), model tree (visibility checkboxes, two-way selection sync via `ViewportControl.SelectionChanged`/`Select`/`SetVisible`/`Frame`), properties panel (kind/faces/closed/volume/area/size), status bar, gradient background, adaptive 1-2-5 ground grid + RGB axes, and a feature-edge overlay (`MeshFeatureEdges` in EngrCAD.Mesh: boundary + sharp-dihedral edges over polygon-offset fills). The former hardcoded showcase now lives in `samples/EngrCAD.Demo` (console app calling the Scene API — the consumer experience). **The live-modeling loop is `dotnet watch` + hot reload, not a custom CLI**: `EngrCad.ShowLive(Func<Scene>)` registers a `MetadataUpdateHandler` (`HotReload.cs`) that re-invokes the factory after each patch and calls `SetScene` (camera preserved; factory exceptions keep the last good scene and surface in the overlay; camera pose persists to a temp file across rude-edit restarts, 30-min freshness). `EngrCad.Run(args, factory)` wraps model programs: no args → ShowLive, `--view` → static Show, `--export part.step|part.obj` → headless export (STEP per B-Rep-representable part via `Part.Source`, OBJ merged with transforms). `samples/EngrCAD.LiveDemo` is the parametric-bracket demo (`dotnet watch --project samples/EngrCAD.LiveDemo`); verified end-to-end: body edits hot-apply in ~0.1–0.7 s without restart. Remaining big rocks: SIMD passes, nuget.org publish.

- .NET SDK 10.0.302 installed **user-local** at `%USERPROFILE%\.dotnet` (win-arm64), on the user PATH with `DOTNET_ROOT` set. Build with `dotnet build EngrCAD.slnx`, test with `dotnet test EngrCAD.slnx`.
- Git repository initialized; commit only when Chris asks.
- Target framework: **.NET 10 (LTS)** via `Directory.Build.props`.
- **NuGet**: all `src/*` projects pack (shared metadata — `Version 0.1.0`, MIT, package READMEs — lives in `Directory.Build.props`; license/URLs are placeholders Chris must confirm before any nuget.org push). `src/EngrCAD` is a meta-package whose ProjectReferences become package dependencies (viewer kept separate so headless consumers don't pull Avalonia). Local feed workflow: bump `<Version>`, `dotnet pack EngrCAD.slnx -c Release -o C:\Users\chris\nuget-local`; the folder is registered as source `engrcad-local`, so consumers just `dotnet add package EngrCAD` (+ `EngrCAD.Viewer`). Note: NuGet caches by version — after repacking the *same* version, delete the cached copies under `%USERPROFILE%\.nuget\packages\engrcad*\<version>` (or just bump the version).

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

### Interop layer — conversion triangle complete
- Implicit → Mesh: `SurfaceNets.Polygonize` (manifold dual contouring)
- B-Rep → Mesh: `BRepTessellator.Tessellate` (shared edge polylines + ear clipping + welding)
- Mesh → Implicit: `MeshSdf` (BVH nearest-triangle + angle-weighted pseudonormal sign; requires closed mesh; composes with all `Sdf` operators)

### Query layer (LINQ) — v1 implemented
- `SpatialCollection<T>` (items + bounds expression + BVH) with `AsQueryable()`: a custom `IQueryProvider` rewrites expression trees, recognizes `SpatialPredicates` clauses (`.Within(box)`, `.WithinDistance(p, d)`, `.HitBy(ray)`) applied to the registered bounds accessor, answers them from the BVH, and re-applies the full predicate over candidates (interception is a pure optimization). Residual/non-spatial queries fall back to LINQ-to-Objects. `LastQueryUsedIndex` is the diagnostic.
- IMPORTANT: query predicates use the by-value `SpatialPredicates` extension methods, NOT `Aabb.Intersects` — expression trees cannot contain calls with `in` parameters, which the kernel API uses.
- Topology-traversal LINQ exists on mesh handles (`vertex.OutgoingHalfEdges().Where(...)`, `face.AdjacentFaces()`, `Face.Bounds`).
- Future: metadata indexes for B-Rep feature queries (cylindrical faces by radius), expression-tree→SDF compilation.

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
  EngrCAD.Modeling/   unified Shape API (rep chosen at the end via To*)
  EngrCAD.Viewer/     Avalonia + Silk.NET (OpenGL) viewer *library* (EngrCad.Show)
samples/
  EngrCAD.Demo/       console showcase: builds a Scene, calls EngrCad.Show
  EngrCAD.LiveDemo/   live-modeling loop: EngrCad.Run + dotnet watch hot reload
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
2. **Mesh engine** ✅ done — half-edge structure (`HalfEdgeMesh` + `Vertex`/`HalfEdge`/`Face` handles for LINQ traversal), manifold-validating `Build`, boundary loops, metrics (area/volume/Euler), primitives (box, uv-sphere, n-gon-capped cylinder), triangulation, Loop subdivision, booleans (`MeshBoolean`: BSP/csg.js clipping + seam zipping for closed results; exact-intersection rewrite is future work), QEM decimation (`MeshDecimator`: quadric edge collapse with link-condition and normal-flip guards, boundary preserved exactly), `RenderMesh` extraction, OBJ export; viewer renders meshes
3. **Implicit engine** ✅ done — `Sdf` AST (`Evaluate`/batch/`Normal`/conservative `Bounds`), primitives (sphere, box, cylinder, torus, capsule, half-space, gyroid lattice), operators (union/intersect/subtract with `|`/`&`/`-` overloads, smooth blends, offset, shell, translate/rotate/scale); `SurfaceNets.Polygonize` in Interop converts implicit→mesh (manifold variant: one vertex per inside-corner component per cell). Future: SIMD batch evaluation, expression-tree→SDF compilation for the Query layer
4. **B-Rep engine** 🔶 modeling operations done — curves (`Line3d`, `Circle3d`, `NurbsCurve` with exact rational conics, `ReversedCurve`/`TransformedCurve` wrappers with `Underlying` for sampling rules), surfaces (`PlaneSurface`, `CylinderSurface`, `SphereSurface`, `NurbsSurface`, `ExtrudedSurface`, `RevolvedSurface` with angle, `SweptSurface` with rotation-minimizing frames), topology (`BrepSolid`→…→`BrepVertex` with `Validate` + Euler–Poincaré incl. genus), `SolidFactory`: `MakeBox`/`MakeCylinder` + **`Extrude` (incl. shear + hole profiles), `Revolve` (full or partial turn, holes on partial, pipe elbows from closed profiles), `Sweep` (RMF along open paths, holes)** over `Profile` (planar closed chain; winding auto-corrected). `BRepTessellator` handles planar (multi-loop, earcut) /cylinder/extruded/revolved/swept faces with edge-consistent grid sampling and seam zipping. Notes: default `Curve3d.TangentAt` uses 2nd-order one-sided differences at domain ends (sweep frames are sensitive to start-tangent error); `PolygonTriangulator` is a faithful mapbox-earcut port — its collinear filtering can merge exactly-collinear boundary runs, which the tessellator's zip pass repairs. **Surface–surface intersection done** (`SurfaceIntersection.Intersect(a, b, region)`): analytic curves for plane/plane (line), plane/cylinder (circle, ellipse, or parallel lines), plane/sphere, sphere/sphere, parallel cylinders; general marching tracer (grid seeding + BVH pairing + damped Gauss–Newton refinement + tangent-predictor/Newton-corrector stepping, periodic-aware, multi-branch, closed-loop detection) for all other pairs, returning `PolylineCurve3d` (exact at vertices, chordal between). New curves: `Ellipse3d`, `PolylineCurve3d`. **Trimming groundwork done**: `Surface.TryProjectPoint` (inverse evaluation — exact for plane/cylinder/sphere, Gauss–Newton otherwise), `FaceGeometry` (curve pullback into parameter space with periodic-u unwrapping, `LoopSignedArea`, point-in-face by upward-v ray parity with periodic segment compaction), `FaceSplitter.SplitByClosedCurve` (closed interior curves → face-with-hole + disk sharing one manifold edge; `createDisk:false` hands the edge's second use to another face). End-to-end drill rehearsal in `DrillTests`: box + intersection circles + cap splitting + bore-wall assembly → valid genus-1 solid with exact volume. **Open-curve face splitting done**: `TopologyEditor.SplitEdge` (splits an edge at a parameter, patching every using loop — neighbor faces stay consistent), `FaceSplitter.SplitByCurve` (full parameter-space arrangement: polyline crossing detection + 2×2 Newton refinement, boundary-edge splitting with endpoint-vertex reuse, interior curve segments as shared two-use edges via `CurveSegment`, sub-face tracing by smallest-clockwise-turn walking, CW loops assigned as holes to their smallest containing CCW loop). Verified through whole solids: single cut (exact volume), cross cut through a split-created vertex (4 quadrants), annular cap cut beside its hole (hole follows the correct side). Known limitation: cutting *through* a hole splits the bore wall's closed edges, which grid tessellation of generated faces can't render yet (trimmed-face tessellation). **Automatic B-Rep booleans done** (`BrepBoolean.Union/Intersection/Difference` in Interop): per-face-pair `SurfaceIntersection`, seam-aligned splitting (each side takes the other's crossing params as mandatory breaks so tessellation welds), fragment classification by probing the other solid's `MeshSdf` (hybrid trick), reversed faces for subtracted tools (`BrepFace.IsReversed`, tessellator flips), wrap-splitting of periodic bands (`SplitBandByWrapCurve`: bore wall → two exactly-reconstructed sub-bands), and circle-extrusion→cylinder promotion for exact bore circles. Boolean output is **topologically sealed** (`TopologyEditor.SealSeams`: stale-use pruning, vertex unification, seam-edge merging; Difference re-winds reversed faces' loops) — results pass `Validate()` and Euler–Poincaré with correct genus. **Filleting**: `Filleting.FilletEdge` for closed circular rims (planar cap ↔ coaxial cylinder band) via exact quarter-torus `RevolvedSurface`; general fillet chains with corner patches are future work. **STEP export**: `StepWriter.Write/WriteFile` (AP214 `MANIFOLD_SOLID_BREP`, analytic surfaces/curves incl. rational NURBS, wrapper-curve simplification; swept surfaces not exportable). **Viewer picking**: click-select via unprojected ray + per-object triangle BVH + Möller–Trumbore, selection highlighted and named in the title bar (note: Avalonia's pointer stack ignores legacy synthetic `mouse_event` clicks — test picking with real input or by calling `Pick` directly). Remaining future work: coplanar/tangent boolean cases, trimmed-face tessellation for split *generated* faces (cut-through-hole bands), general fillets, STEP import, NURBS surface export. Numerical notes from the Modeling work: the generic `Surface.TryProjectPoint` wraps (not clamps) periodic u so Newton can cross the seam; plane⊥cylinder intersection circles are **phase-aligned to the cylinder's frame** (arbitrary-perpendicular frames caused tessellation cracks on rotated bores — and `DrillTests` winding depends on this); `BrepBoolean.ProbePoint` routes full-period-wrapping loops to the band path (projection jitter gives degenerate sliver loops nonzero area, and the planar path would probe on the fragment boundary where the SDF is 0); `SurfaceIntersection.Promote` sanity-checks that the generator lies on the candidate cylinder (a `TransformedCurve`-wrapped circle's `Underlying` is the untransformed circle).
5. **Interop completion** — remaining conversions, mesh↔SDF, robustness passes

The Query layer and Viewer grow alongside each engine as it lands, not as separate phases.

## Conventions

- C# `LangVersion` latest, `Nullable` enabled, `ImplicitUsings` enabled, file-scoped namespaces
- Root namespace `EngrCAD.*`, matching project names
- Tests: xUnit; every geometric algorithm gets tolerance-aware assertions
- Central build props (`Directory.Build.props`) for shared settings once scaffolded
