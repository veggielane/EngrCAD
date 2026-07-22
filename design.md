# EngrCAD — Design

A hybrid CAD kernel for modern .NET supporting three geometry representations as peers —
**B-Rep** (parametric), **Implicit** (signed distance fields), and **Mesh** (discrete
half-edge) — with first-class conversions between all of them and LINQ-native geometry
querying. This document records the architecture and the reasoning behind the
load-bearing design decisions. Per-project summaries live in each project's `README.md`;
session status and conventions live in [CLAUDE.md](CLAUDE.md).

## 1. Architecture

```
                    ┌─────────────┐
                    │ EngrCAD.Core │   math structs · Tolerance · BVH/Octree
                    └──────┬──────┘
        ┌──────────┬───────┼────────┬───────────┐
   ┌────┴───┐ ┌────┴────┐ ┌┴───────┐ ┌────┴────┐
   │  Mesh  │ │ Implicit│ │  BRep  │ │  Query  │   three engines + LINQ layer
   └────┬───┘ └────┬────┘ └┬───────┘ └─────────┘
        └──────────┼────────┘
              ┌────┴────┐
              │ Interop │   conversions: the only project referencing all engines
              └────┬────┘
              ┌────┴────┐
              │ Viewer  │   Avalonia + Silk.NET OpenGL (only UI-dependent project)
              └─────────┘
```

Dependency rules: `Core` depends on nothing; each engine depends only on `Core`;
`Interop` may depend on all engines; only `Viewer` may reference UI/graphics packages.
Tests mirror projects one-to-one (xUnit).

Each engine uses the data structure its mathematics wants:

| Engine   | Mathematics            | Structure                          |
|----------|------------------------|------------------------------------|
| Mesh     | discrete linear algebra| half-edge over struct-of-arrays    |
| Implicit | SDF evaluation         | AST of `Sdf` nodes                 |
| B-Rep    | parametric geometry    | pointer-based topology graph       |

## 2. Core

- **Doubles everywhere; floats only at the GPU boundary** (`RenderMesh`).
- **`Tolerance` policy**: no kernel code compares doubles with `==`. Geometric predicates
  take a `Tolerance` (linear, in model units; angular, in radians) passed explicitly so
  callers control precision. Exact `Equals`/`==` on math structs is bitwise and reserved
  for hashing/dedup.
- **Matrix convention**: `Matrix4d` is row-major *storage* with **column-vector
  semantics** (`p' = M·p`; `A*B` applies `B` first). GL upload transposes to
  column-major arrays. `Quaterniond`'s Hamilton product composes in the same order.
- **`in` parameters** keep hot paths copy-free, with one important consequence: C#
  expression trees cannot contain calls to methods with `in` parameters, so any method
  meant to appear inside a LINQ predicate must take parameters by value — that is the
  reason `EngrCAD.Query.SpatialPredicates` exists.
- **BVH** is the workhorse index (static, median split, flat nodes, stack traversal,
  branch-and-bound `Nearest`); the **Octree** exists for incrementally-changing content.
  Construction may allocate; queries must not (beyond the caller's results list).

## 3. Mesh engine

- **Half-edge with explicit boundary half-edges**: every undirected edge is two
  half-edges; where a face is missing, a boundary half-edge with `face = -1` is created
  and `Next`-chained along the boundary loop. Consequence: `Twin` always exists and
  traversal code never branches on "is there a neighbor?". Manifoldness is enforced at
  `Build` time (duplicate directed edges = non-manifold or inconsistent winding; a vertex
  with two boundary fans = bow-tie), so all downstream algorithms may assume a manifold.
- **Storage is struct-of-arrays** (index lists), while the public traversal API is
  lightweight **handle structs** (`Vertex`, `HalfEdge`, `Face`) that read naturally under
  LINQ — this is the project's "LINQ-native" style at the topology level.
- **Immutability after build**: algorithms (subdivision, decimation, booleans) return new
  meshes. Mutation-based editing (edge collapse in place, etc.) can be added later behind
  the same handles.
- **Booleans are BSP-based** (csg.js): robust enough for well-conditioned inputs and two
  orders of magnitude simpler than exact intersection booleans. The known BSP weakness —
  the two sides of an intersection seam are tessellated independently, leaving T-junction
  cracks — is repaired by **seam zipping**: any directed edge with no reverse partner
  gets the other side's collinear crack vertices inserted, after which both sides carry
  identical subdivision and welding closes the surface. An exact-intersection rewrite
  remains on the roadmap for coplanar/tangent robustness.
- **`PolygonTriangulator` is a faithful mapbox-earcut port** (linked list, full recovery
  ladder: filter → cure local intersections → split; Eberly hole bridging with sector
  tie-breaking). Hand-rolling "most of earcut" was tried and failed in exactly the corner
  cases earcut's ladder exists for (multiple holes bridging to one vertex); porting it
  faithfully is the documented lesson. One earcut property to remember: it filters
  exactly-collinear vertices, so collinear boundary runs can merge — consumers that weld
  against neighboring geometry must zip seams afterwards.
- **Decimation** is Garland–Heckbert QEM with the manifold link condition, a
  normal-flip/degeneracy guard, and a hard rule that boundary vertices never collapse
  (open meshes keep their outline exactly).

## 4. Implicit engine

- A model is an **AST of `Sdf` nodes**; every node reports conservative `Bounds`
  (infinite for half-spaces/lattices) so meshing can auto-size its sampling region and
  interop/queries can prune.
- Primitive distances are exact (Quilez forms); smooth blends are lower-bound
  approximations — correct sign everywhere, exact away from blend regions, which is the
  contract Surface Nets needs.
- Set operators are overloaded (`|`, `&`, `-`) for fluent composition; transforms
  evaluate at inverse-mapped points (rigid + uniform scale keep distances exact).
- Batch `Evaluate(ReadOnlySpan<Vector3d>, Span<double>)` is the future SIMD seam; the
  scalar loop is the current default implementation.

## 5. B-Rep engine

- **Topology graph**: `BrepSolid → BrepShell → BrepFace → BrepLoop → BrepCoedge →
  BrepEdge → BrepVertex`, pointer-based (B-Rep is pointer-heavy by nature; SoA buys
  little here). Closed edges (full circles) have `StartVertex == EndVertex` (a seam
  vertex); periodic faces (cylinder side, surfaces of revolution) are represented with
  multiple loops of closed edges rather than seam edges.
- **Orientation conventions** (relied on by tessellation): face surfaces are constructed
  so their normal points **out of the solid**, and loops run **CCW around that outward
  normal** (holes CW). Validation is combinatorial (loop chaining; every edge used by
  exactly two coedges of opposite sense) plus the **Euler–Poincaré formula**
  `V − E + F − (L − F) − 2(S − G) = 0`, which correctly handles closed-edge topologies
  (cylinder: V2 E2 F3 L4) and genus (plate with n holes → genus n; full revolve → 1).
- **Modeling operations share one builder**: extrude, sweep, and partial revolve are all
  "profile × 1-parameter motion" — side faces per profile segment, **rail edges** at
  segment junctions (straight lines / RMF rails / circular arcs respectively), and two
  planar caps carrying one loop per boundary profile (outer + holes). Full revolve is the
  cap-less special case. `Profile` validates planarity/closure and each operation
  auto-corrects winding, so users cannot produce inside-out solids.
- **Sweeps use rotation-minimizing frames** (double reflection). The frames are computed
  at discrete samples and interpolated + re-orthonormalized against the exact path
  tangent between them; evaluation is exact *at* the samples, which is all tessellation
  uses. A hard-won numerical note: the default finite-difference `TangentAt` must be
  second-order at domain endpoints — a first-order one-sided difference puts ~1e-8 error
  into the start frame, which is larger than the weld tolerance and opens cracks.

### Surface–surface intersection

`SurfaceIntersection.Intersect(a, b, region)` is two-tiered:

- **Analytic tier** — exact curve objects for the common quadric pairs: plane/plane →
  clipped `Line3d`; plane/cylinder → `Circle3d`, exact `Ellipse3d` (semi-major
  r/|n·axis|), or two parallel lines; plane/sphere and sphere/sphere → `Circle3d`;
  parallel cylinders → two lines. Unbounded results are clipped to the caller's region.
  Tangential contacts are deliberately not reported (they are not curves).
- **Marching tier** for every other pair: grid-sample both surfaces, pair nearby samples
  with a BVH `Nearest` query, refine each pair onto the intersection with damped
  Gauss–Newton, then trace each branch with a tangent predictor (`n_a × n_b`) and a
  4×4 Newton corrector (3 closure equations + 1 step-plane constraint) over the
  parameter 4-tuple. Periodic parameter directions (cylinder/sphere azimuth, closed
  generators, full revolutions) are handled by wrapping, so branches crossing seams
  don't split; closed loops are detected by proximity to the start; consumed seeds
  prevent duplicate branches. Output is `PolylineCurve3d` — exact at the traced
  vertices (corrector converges to ~1e-10), chordal in between; step size derives from
  the region diagonal.

This is the gateway to trimming: the traced/analytic curves are exactly what face
splitting and B-Rep booleans will consume.

## 6. Interop

The conversion triangle is complete; each direction has a deliberately chosen algorithm:

- **Implicit → Mesh: manifold Surface Nets** (dual contouring without QEF). Chosen over
  marching cubes because the 256-entry MC tables are error-prone to reproduce and Surface
  Nets pairs naturally with the half-edge's n-gon support (quad output). The *manifold*
  variant — one vertex per connected component of inside corners per cell — exists
  because the naive version provably emits non-manifold edges on diagonal sign patterns
  (thin sheets, gyroids), which the strict `HalfEdgeMesh.Build` rejects.
- **B-Rep → Mesh: edge-consistent tessellation**. The invariant that makes welded output
  crack-free by construction: **every edge is sampled exactly once into a shared
  polyline, and every face's boundary sampling equals those polylines**. Planar faces
  (any loop count) ear-clip in plane coordinates; cylinder bands and generated surfaces
  tessellate as parameter grids whose u/v sample rules match the edge sampling rules
  (`Underlying` unwrapping picks 2-point sampling for lines, `segmentsPerCircle` for
  circles, `curveSamples` otherwise). A final weld with seam zipping repairs the one
  known exception (earcut merging exactly-collinear boundary runs).
- **Mesh → Implicit: `MeshSdf`** with angle-weighted pseudonormals (Bærentzen–Aanæs) for
  the sign — exact for watertight meshes even when the closest feature is an edge or
  vertex — over BVH branch-and-bound nearest-triangle search. Verified to match the
  analytic box SDF to 1e-9 across all feature regions.

## 7. Query layer

`SpatialCollection<T>` = items + a bounds *expression* + a BVH. Its `IQueryable`
provider rewrites expression trees at execution: a `Where` containing a
`SpatialPredicates` clause (`Within` / `WithinDistance` / `HitBy`) applied to the
registered bounds accessor gets its source replaced by BVH candidates, **keeping the full
original predicate** so interception is a pure optimization (results provably identical
to LINQ-to-Objects). Non-matching queries fall through untouched. The by-value
`SpatialPredicates` wrappers double as the recognizable vocabulary and as the workaround
for `in`-parameters being illegal in expression trees.

## 8. Testing philosophy

- Every geometric algorithm is tested against **analytic ground truth** where one exists
  (exact volumes for prisms/wedges/polygonal rings, Pappus for revolutions, 4/3πr³
  within tessellation error, NURBS conics on-radius to 1e-9) and against **brute force**
  where it doesn't (BVH/octree/query results vs linear scans on seeded random data).
- Topological invariants are asserted constantly: `Validate()`, `IsClosed`, Euler
  characteristic (including genus: torus 0, plate-with-two-holes −2).
- Tolerances in tests are derived from the discretization (e.g. chord error), not
  hand-tuned magic numbers, so failures mean something.

## 9. Known limitations / roadmap

- **B-Rep**: surface–surface intersection exists (analytic + marching), but faces cannot
  yet be trimmed/split along the resulting curves — so no B-Rep booleans or filleting
  yet. Next steps: curve-on-surface (pullback of intersection curves into parameter
  space), face splitting, and containment classification.
- **Mesh booleans**: BSP-based; coplanar-face and tangent cases remain fragile until an
  exact-intersection rewrite.
- **Full revolve of profiles with holes** produces multiple shells (outer + tunnel tori)
  and is rejected until multi-shell construction is wired up.
- **Performance**: SIMD batch SDF evaluation and SoA render extraction are designed-for
  but not yet implemented; BVH uses median split (SAH is a drop-in upgrade).
- **Viewer**: display-only; picking/selection (ray + query layer are ready) and a scene
  graph are next.
