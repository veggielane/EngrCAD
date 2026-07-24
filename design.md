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
- **Plane cutting** (`MeshPlaneCut.Cut`) keeps the side the plane normal points *away*
  from and clips crossing faces with Sutherland–Hodgman. Crossing points are computed
  once per undirected edge in a **canonical edge direction** (lower vertex index first)
  so both faces sharing the edge get bit-identical intersection coordinates — welding
  then closes the cut without tolerance games. Boundary loops are returned ordered;
  optional caps go through earcut, whose collinear filtering is repaired by the same
  collinear-chord zip the booleans use. Non-convex faces that cross the plane three or
  more times are triangulated on their **Newell plane** (robust for near-degenerate
  polygons) before clipping — fanning from vertex 0 is only valid for star-shaped
  polygons and silently mis-clips otherwise.
- **Winding-number classification** (`MeshWindingNumber`) gives robust inside/outside
  for non-watertight meshes: `WindingNumber` sums signed solid angles
  (Van Oosterom–Strackee) exactly, `FastWindingNumber` is the Barill/Jacobson order-2
  (dipole+quadrupole) multipole approximation, thresholded at ½. It builds its **own**
  median-split hierarchy whose nodes each own a contiguous triangle range, rather than
  extending Core's `Bvh` — the shared `Bvh` permutes items into an internal array with
  no per-node range access, and the multipole coefficients need range scans. Coefficients
  are computed eagerly at construction (matching the immutable-after-build ethos, no g3
  timestamp-guarded lazy dictionary). It is wired as an opt-in `MeshSdf` sign source
  (`MeshSignSource.WindingNumber`) that, unlike the default pseudonormal, accepts open
  meshes; the default path is byte-for-byte unchanged.

## 4. Implicit engine

- A model is an **AST of `Sdf` nodes**; every node reports conservative `Bounds`
  (infinite for half-spaces/lattices) so meshing can auto-size its sampling region and
  interop/queries can prune.
- Primitive distances are exact (Quilez forms); smooth blends are lower-bound
  approximations — correct sign everywhere, exact away from blend regions, which is the
  contract Surface Nets needs.
- Set operators are overloaded (`|`, `&`, `-`) for fluent composition; transforms
  evaluate at inverse-mapped points (rigid + uniform scale keep distances exact).
- **N-ary operators** (`Sdf.Union`/`Intersection`/`SmoothUnion` over lists) evaluate
  children once per query in a flat loop instead of a deep binary tree. The N-ary
  smooth union **folds the pairwise polynomial smooth min** (bit-identical to chained
  binary for two children, exact hard min outside the blend band, transcendental-free
  for future SIMD — rejected log-sum-exp for all three reasons); order matters only
  inside the blend band, and bounds expand by max(k, (n−1)k/4). **Falloff blends**
  (`Sdf.Blend`, Wyvill/exponential kernels) bound their additive bump by the blend
  distance, so bounds expand by exactly that; Wyvill's compact support makes the
  result *exactly* the plain union outside the band. (Negative blend radii degrade to
  hard min/max; the smooth-op bounds clamp their expansion at 0 to stay conservative.)
- **Sampled-grid acceleration** (`Sdf.Sampled`) bakes any `Sdf` to a uniform-cell grid
  evaluated by trilinear interpolation — the standard way to make an expensive AST (e.g.
  `MeshSdf`) cheap to query. Storage is `double` (nodes reproduce the source exactly,
  unlike g3's float grid) and baking batches through the `Evaluate(ReadOnlySpan…)` SIMD
  seam. The fidelity contract is documented honestly: exact at nodes, O(h²) between where
  smooth, O(h) across creases, so the zero level set shifts by the same order and sign is
  reliable only when the cell size resolves features. Outside the baked box the value is
  the boundary interpolant plus Euclidean distance-to-region — continuous across the
  boundary and correct-sign whenever the solid is contained (the parameterless overload
  guarantees containment by baking `Bounds.Expanded(cellSize)`). A `LazyGridSdf` variant
  bakes 16³ blocks on demand (lock-free, first-publish-wins) and is the seam for the
  still-open sparse-grid and narrow-band work.
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
- **The derivative API is virtual and exact-by-default**: `Curve3d` exposes virtual
  `DerivativeAt`/`SecondDerivativeAt` (documented finite-difference fallbacks), and
  every analytic curve — now including `Parabola3d`/`Hyperbola3d`, completing the conic
  family — plus both wrappers override them exactly. This formalizes the repo's
  "no finite-difference tangents in weld-critical constructions" lesson at the API
  level: a consumer asking a curve for derivatives gets exact values unless the curve
  genuinely has none (`PolylineCurve3d`). `OffsetCurve3d` (planar offset as first-class
  geometry) derives its exact derivative analytically — O′ = (1 − dκ)·C′ with the
  signed curvature from the base curve's exact C′/C″ — rather than differencing, and
  deliberately does NOT validate |d| against the minimum radius of curvature
  (cusps/self-intersection are the caller's responsibility, matching OCCT).
- **STEP import reconstructs what the format doesn't store.** `StepReader` maps AP214
  back to `BrepSolid` with topology shared by entity identity (one edge per
  `EDGE_CURVE` — manifold sharing survives by construction). STEP stores no edge
  domains and no revolve angle/generator trims, so the reader rebuilds them exactly:
  closed-form phase angles for conic arcs, Newton with exact NURBS derivatives for
  B-spline trims, and revolve trims recovered by bisection on the exact (radius, axial)
  profile residuals — root solving, never distance minimization, which stalls at
  √ε ≈ 1e-8, past the 1e-9 weld tolerance.
- **Trimmed-face tessellation ear-clips exact coordinates — earcut is banned for
  pulled-back loops.** `PolygonTriangulator` filters exactly-collinear vertices, and
  iso-parameter boundary runs are exactly collinear in uv while NOT collinear in 3D:
  a dropped sample is a crack no zip pass can repair, and jittering the input breeds
  zero-area folds that refine into non-manifold welds. The landed design: an exact
  ear clipper (shortest-diagonal ear selection — first-found fans caused 60× triangle
  blowup — with an epsilon blocking band for inverse-evaluation jitter), a monotone
  strip-zip/pole-fan path for band-like regions, and Steiner points by *refinement*
  (midpoint-split oversized interior edges, evaluated on the exact surface) instead of
  upfront insertion — no point-in-region classification needed. Boundary vertices are
  always the exact shared edge-polyline samples, so welding invariants hold by
  construction; routing to the trimmed path requires a failed two-sided 3D match
  against the natural grid boundary, and trimmed-path failure falls back to the grid.
  Boolean-path lessons recorded from the same work: probe points must stay a
  triangle-diameter away from fragment boundaries lying on the other solid's curved
  surface (the SDF is only sagitta-accurate there), and both sides of a shared closed
  intersection curve must agree on every subdivision point including the wrap-split
  seam anchor at `Domain.Start`.
- **NURBS curves have exact analytic derivatives** (`DerivativeAt`/`SecondDerivativeAt`:
  The NURBS Book A2.3 basis derivatives + the generalized rational quotient rule, so
  non-unit weights are handled; `TangentAt` is overridden, leaving finite differences
  only for curves without an exact override). **`NurbsCurve.InterpolatePoints`** fits a
  cubic through points: chord-length parameterization, natural (zero-C″) ends, and a
  genuinely tridiagonal Thomas solve for the open case (collocation at a
  multiplicity-1 knot leaves exactly 3 nonzero basis functions); the closed case uses a
  periodic knot vector with wrapped control points, giving a C2 seam by construction.
  Two points degrade to a degree-1 chord.

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

### Trimming groundwork

- **Inverse evaluation** `Surface.TryProjectPoint(point) → (u, v)`: exact overrides for
  plane/cylinder/sphere; the base implementation grid-seeds and runs damped 2-unknown
  Gauss–Newton (finite domains only).
- **`FaceGeometry`** works in parameter space: `PullCurve`/`PullLoops` sample 3D curves /
  face loops and project them, unwrapping the periodic u direction stepwise so pulled
  polylines are continuous across seams. `Contains(face, point)` classifies by parity of
  an upward-v ray; periodic handling first compacts each segment (endpoints stored a
  period apart get rejoined) and then shifts it into the test point's period — the
  wrap-around segment of a pulled circle otherwise double-counts.
- **`FaceSplitter.SplitByClosedCurve`** handles the drilled-hole/boss case: a closed
  curve interior to a face becomes a hole loop (wound opposite the outer loop, decided by
  the pulled curve's signed area) plus a disk face, sharing one new closed edge — always
  two-manifold. `createDisk: false` leaves the edge's second use free for another face
  (e.g. a bore wall), which is how the end-to-end drill test assembles a genus-1 solid
  with exact volume.
- **`FaceSplitter.SplitByCurve`** handles curves crossing the face boundary — the real
  arrangement machinery:
  1. crossings found by sampling boundary coedges and the curve into parameter space and
     intersecting polylines, then refined by 2×2 Newton on (edge-param, curve-param);
  2. boundary edges split at the crossings via `TopologyEditor.SplitEdge`, which patches
     *every* loop using the edge — neighboring faces evolve consistently, which is what
     makes whole-solid tests (Validate + Euler + exact volume) possible after a split;
     crossings landing on an edge endpoint (e.g. a vertex created by an earlier split)
     reuse that vertex instead;
  3. interior curve stretches (classified by midpoint parity) become new edges
     (`CurveSegment` reparameterizes a piece of the curve), each used twice;
  4. sub-faces are traced from the planar graph by walking half-edges with the
     smallest-clockwise-turn rule; CCW traced loops bound sub-faces, CW loops (including
     uncrossed original holes) are assigned to the smallest containing CCW loop.
  Constraints: crossings must be transversal, and open curves must start/end outside the
  face. Known limitation: splitting the closed edges of a generated face (e.g. a bore
  wall's circles when a cut passes through the hole) outruns the grid tessellator —
  trimmed-face tessellation is the companion work item to booleans.

### B-Rep booleans (`BrepBoolean`, in Interop)

The pipeline, per operation: (1) capture both solids' `MeshSdf` before mutating anything;
(2) intersect every original face pair, recording — per curve — the *other* face's
crossing parameters; (3) split each solid's faces by its curves, passing those opposing
crossings as **mandatory seam breaks** so both sides subdivide the seam identically and
welding closes it; (4) classify each fragment by probing a strictly-interior point
(outer-loop triangle centroids, or the parametric midpoint for period-wrapping band
fragments) against the other solid's SDF; (5) keep fragments per operation, with
subtracted-tool faces marked `IsReversed` (the tessellator flips their triangles).

Booleans deliberately live in Interop, not BRep: classification rides on the mesh
engine's signed distance field — the hybrid kernel earning its keep.

Two supporting mechanisms: circle-extrusions along their axis are **promoted to analytic
cylinders** inside `SurfaceIntersection`, so drilled bores get exact circles rather than
marched polylines; and a closed curve whose pullback drifts a full period (a bore circle
on a band) is recognized as non-contractible and handled by `SplitBandByWrapCurve`,
which cuts the band into two bands with exactly reconstructed sub-surfaces.

v1 contract: transversal intersections only (no coplanar or tangent face pairs); the
input solids are consumed. Output is **topologically sealed** by
`TopologyEditor.SealSeams`: edge uses contributed by discarded fragments are pruned,
coincident vertices unify (edges have internally settable vertex references for this),
and each seam edge merges with its twin from the other solid — the twins match exactly
because both sides split their seams at the same mandatory break parameters. Difference
reverses B's kept faces *properly*: loops re-wound (order and senses) in addition to the
`IsReversed` normal flag, so seam edges are traversed oppositely by the faces meeting
there. Boolean results therefore pass `Validate()` and Euler–Poincaré with the correct
genus.

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
- **Planar iso-contours: `SdfContours.OnPlane`** (marching squares over a batch-sampled
  planar grid) lives in Interop rather than the viewer deliberately: it is UI-free,
  deterministic, and testable against analytic fields headlessly; the viewer only maps
  the section plane into each instance's space (inverse transform — an affine map takes
  the sample rectangle to a parallelogram, which the origin+two-sides parameterization
  represents exactly) and draws the segments. Cell-edge crossings are interpolated from
  the same two samples on both sides, so shared endpoints are bit-identical (loops chain
  by exact equality — the same construct-shared-geometry-exactly discipline as
  tessellation welds, at display scale); saddle cells resolve by the cell-center
  average. Used by the viewer's section-plane isolines (d = 0 exact cross-section,
  ±k·spacing field visualization).

## 6b. Unified modeling layer (`EngrCAD.Modeling`)

`Shape` is a representation-agnostic operation graph — the hybrid kernel's front door.
Design decisions:

- **A deferred AST, not eager geometry** (mirrors the `Sdf` design): primitives,
  extrude/revolve/sweep, booleans, smooth blends/offset/shell/lattice, transforms, and
  `From(engine object)` leaves. Nothing is computed until `ToBrep()`, `ToImplicit()`,
  or `ToMesh()` lowers the graph, so the *same* model can be lowered to all three.
- **Transforms bake into construction inputs, never into finished geometry.** The B-Rep
  lowering carries an accumulated matrix: boxes become extrusions of transformed
  profiles (shear included), cylinders extrude transformed `Circle3d`/`Ellipse3d` rims,
  spheres/tori take decomposed rigid+uniform-scale placement (`MakeSphere`/`MakeTorus`
  with center/axis), profiles wrap segments in `TransformedCurve`. This keeps rotated
  booleans exactly as accurate as axis-aligned ones. The implicit lowering decomposes
  the matrix into `Scale→Rotate→Translate` SDF operators (blend radii and offsets scale
  by the uniform factor); non-decomposable (sheared) subtrees bridge through a mesh.
- **Best-effort bridging with honest reporting** (Chris's chosen policy): nodes without
  a native form in the target bridge through another representation —
  extrude/revolve/sweep → implicit goes B-Rep → tessellation → `MeshSdf`; blends →
  mesh goes SDF → Surface Nets. Only truly impossible routes throw (`ToBrep` of a
  blend: there is no mesh→B-Rep import). `Explain(target)` runs the same classification
  as a dry run and labels every node Native / Bridged(route) / Impossible(reason);
  `ShapeConversionException` carries that report.
- **`ToMesh` picks the highest-fidelity whole-tree route**: (1) B-Rep-representable →
  one tessellation of the exact solid (crisp edges); (2) blends present → polygonize
  the SDF; (3) `From(mesh)` leaves in boolean trees → per-node `MeshBoolean`.
- **Escape hatches are first-class**: `From(BrepSolid/HalfEdgeMesh/Sdf)` wraps raw
  engine geometry, so a design can exit to any engine API for operations the graph
  doesn't surface (filleting, hand-written SDF fields, mesh repair) and re-enter.
- Hardening this feature fixed three latent robustness bugs (notes in CLAUDE.md):
  periodic-seam clamping in the generic `TryProjectPoint`, arbitrary-phase
  plane⊥cylinder intersection circles (now aligned to the cylinder frame so band grids
  and edge polylines weld), and `ProbePoint` triangulating jitter-degenerate wrap loops.
- **Sketching** (`Sketch.cs`/`SketchSegments.cs`/`SketchRegion.cs`): one closed 2D
  region (lines, arcs, béziers; holes by parity) with *every* lowering exact in its own
  way — B-Rep via `Line3d`/`NurbsCurve.Arc`/Bézier NURBS profiles, implicit via the
  sketch's own signed distance (exact segment distances; sign from even–odd parity over
  precomputed y-monotone pieces — arcs split at y-extreme angles, cubics at y′ roots,
  crossings solved exactly), mesh via the B-Rep tessellation. `Sdf.ExtrudedRegion`/
  `RevolvedRegion` (over `IPlanarRegion`, defined in EngrCAD.Implicit) use the standard
  exact slab/revolution combines, so sketch extrude + full revolve are implicit-Native —
  the "exact 2D-profile SDF" roadmap item. Area is exact (arc terms analytic, cubics by
  3-point Gauss quadrature — the integrand is degree 5, within quadrature exactness).
  Revolve convention: sketch x = radius, plane defaults to XZ (axis = world Z).
  Axis-touching profiles revolve in *every* representation on full turns: the B-Rep
  `RevolveFullTurn` drops on-axis stretches (they sweep zero area — Chris's
  observation), treats their endpoints as poles without junction edges (a disk face
  then has a single rim loop, exactly like `MakeSphere`'s hemispheres), and splits
  pole-to-pole generators at their midpoint so no face is left without a boundary
  loop. Tessellation already handles the pole rows via degenerate-cell filtering.
- **Holes** (`HoleSpec.cs`/`StandardHoles.cs`): every hole tool — simple, counterbore,
  countersink cone — is an axis-touching revolved sketch subtracted per placement
  point, so the feature inherits sketching's exactness in all three representations.
  Tools overshoot the surface (booleans never see coplanar faces; the countersink cone
  continues its slope so the surface diameter is preserved). `StandardHoles` carries
  the metric tables (ISO 273 / DIN 974 / ISO 10642 / coarse tap drills / Tappex
  Trisert — the insert rows flagged for datasheet verification). Kernel prerequisites
  built for this: analytic plane⊥revolved-surface circles, wrap-splitting of revolved
  bands with geometrically refined cut parameters (projection error would crack cone
  welds), and pole-aware boolean probe points.
- **Queries and rim features**: `BrepQueries` gives B-Rep topology the LINQ vocabulary
  (classification, adjacency, convexity, normal-directed face selection); `Shape.Chamfer/
  Fillet(amount, faceSelector)` run `Filleting.ChamferRim/FilletRim` topology surgery
  on the lowered solid. Design choices: all new rim edges are built in the rim face's
  traversal direction (every coedge sense follows mechanically); rim circle geometry
  comes from edge *samples*, never `Underlying` (wrappers lie about position);
  domain-driven neighbor surfaces (extruded/revolved) are trimmed when their rims are
  lowered, because their tessellation grids ignore loops. Fillet corners are avoided,
  not patched: chamfers miter (planar strips can), fillets require G1 rims so bands
  join along shared junction arcs — the honest v1 boundary until trimmed-band
  tessellation exists.
- **Patterns** are union-tree sugar; the boolean engine gained the robustness they
  need: a disjoint fast path (no intersection curves → whole-body classification,
  multi-shell unions, clone-reversed swallowed tools), face-bounds pre-filtering of
  carrier-surface intersections, and dedupe of identical curves from faces sharing
  carriers.
- **Parametric features** reify FeatureScript's idea in plain C#: `[Param]`-annotated
  classes (reflection metadata → validation, JSON overrides, future property-panel
  editing) with pure `Apply(FeatureContext)` bodies, composed in a `FeatureHistory`
  replayed with prefix caching (cache key = instance identity + parameter snapshot +
  upstream chain — fresh instances re-run, covering non-parameter inputs safely).
  Failure semantics mirror the live loop: validate first, stop at the first failure,
  keep the last good body, report per-feature statuses. Cross-feature geometry
  references are deliberately *selector queries* over the lowered body rather than
  persistent IDs — semantic references survive regeneration by re-running.
- **The document model lives here too** (`Document.cs`): `Part` is a self-contained,
  user-constructed object — name, geometry from any engine (including `Shape`), color,
  transform — with a lazily produced, cached display mesh (`GetMesh`;
  `Scene.PreMesh()` keeps tessellation off render threads). `Tab`s group parts (names
  unique per tab, palette colors assigned on add) and `Scene` holds named tabs
  (`Add(part)` shorthand targets a default "Model" tab). Design constraint kept
  deliberately: `Part` is a *leaf* and `Tab` the container, so assembly occurrences
  (placed instances of parts/sub-assemblies) can be added beside parts later without
  reshaping the API. The viewer's `SceneHost` maps tabs to a tab strip over one shared
  GL viewport with per-tab cameras.

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

## 9. Further capabilities

- **Filleting** (`Filleting.FilletEdge`): closed circular rims where a planar cap meets a
  coaxial cylindrical band are replaced by an exact quarter-torus (`RevolvedSurface` over
  a `CurveSegment` arc), patching the cap and band in place through their loops. General
  fillet chains (open edges, corner patches where fillets meet) are future work.
- **STEP export** (`StepWriter`, AP214): topology maps one-to-one to
  `MANIFOLD_SOLID_BREP`; analytic surfaces and curves export exactly (including rational
  B-splines via the complex-instance form); wrapper curves simplify to analytic forms or
  fall back to sampled degree-1 B-splines. Swept (RMF) surfaces and NURBS surfaces are
  not exportable yet; import is future work.
- **Viewer picking**: click-select by unprojecting the pixel through the inverse
  view-projection, querying each object's triangle BVH (`Bvh.Query(ray)`), and
  Möller–Trumbore on candidates; nearest hit is highlighted. Note for automation:
  Avalonia's pointer stack ignores legacy synthetic `mouse_event` clicks — exercise
  picking with real input.
- **Viewer section planes**: a horizontal clip at an adjustable world-z height,
  implemented as fragment-shader `discard` with `gl_FrontFacing` backface detection
  shading exposed interiors as a flat cut material. The clipping-consistency rule:
  anything that *is* the model (fills **and** feature edges) clips identically —
  the discard lives in both programs — while scene furniture (grid, axes) never
  clips. Picking deliberately ignores the section plane in v1.
- **Per-part display modes** (`Part.DisplayMode`) live on the document model, not
  viewer-only state, so design code can set them and they survive tab switches and hot
  reloads (a reload rebuilds parts, so model-code modes win again — consistent with the
  camera-persistence model). Wireframe reuses the line program over every unique mesh
  edge (`WireframeEdges`); translucent parts draw after opaque, sorted back-to-front by
  center with depth-writes off and opaque silhouette edges on top — a per-part (not
  per-triangle) sort, so interpenetrating translucent parts can show blend-order
  artifacts (section mode stays the tool for exact interior inspection).
- **Headless offscreen rendering** (`EngrCad.RenderToImage` / `--render`) renders a
  scene to PNG with no window, so tests and agents verify viewer changes by inspecting
  pixels instead of screenshotting the live app. It creates a **direct EGL pbuffer
  context** over Avalonia's bundled ANGLE natives by P/Invoke (preferring D3D11
  hardware → WARP software so it survives CI and locked sessions), with no Avalonia UI.
  A `PngWriter` (dependency-free deflate + CRC-32) encodes the framebuffer. Two lessons
  worth keeping: Avalonia's `av_libglesv2.dll` exports EGL entry points under an `EGL_`
  prefix (not the standard `egl*`), so the binding tries both spellings; and the
  renderer duplicates the small shader strings rather than sharing `ViewportControl`'s,
  a deliberate choice to keep that file untouched during concurrent viewer work.
- **Live modeling via `dotnet watch` hot reload** (chosen over a custom `.csx`
  scripting host: standard tooling, full IDE/debugger support, no Roslyn-scripting
  dependency). `EngrCad.ShowLive(Func<Scene>)` + an assembly-level
  `MetadataUpdateHandler`: dotnet watch patches method bodies in-place, the handler
  re-invokes the factory (debounced — it can fire several times per save) and posts
  `SetScene`; the camera is untouched and factory exceptions keep the last good scene.
  Rude edits restart the process, mitigated by persisting the camera pose per title.
  `EngrCad.Run(args, factory)` adds `--view` and headless `--export .step/.obj` so a
  model program doubles as its own exporter in CI.

## 10. Known limitations / roadmap

- **Booleans**: transversal cases only; coplanar-face and tangent configurations (both
  mesh/BSP and B-Rep pipelines) remain future work.
- **Trimmed generated faces**: splitting the closed edges of a generated band face (a
  cut through a bore) outruns the full-domain grid tessellator; needs loop-driven
  trimmed tessellation.
- **Full revolve of profiles with holes** produces multiple shells (outer + tunnel tori)
  and is rejected until multi-shell construction is wired up.
- **Performance**: SIMD batch SDF evaluation and SoA render extraction are designed-for
  but not yet implemented; BVH uses median split (SAH is a drop-in upgrade).
