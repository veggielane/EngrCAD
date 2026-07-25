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
- **Immutability after build — enforced structurally**: algorithms (subdivision,
  decimation, booleans) return new meshes, and every downstream consumer (booleans,
  welds, viewer caches, `MeshSdf`) relies on that reference semantics. Mutation lives
  in a separate **`EditableMesh` companion** (free-list SoA copied from the immutable
  mesh, compacted back via the manifold-validating `Build`) rather than behind a
  facade over shared storage — a facade would make the immutable contract enforceable
  only by discipline. Its five Euler operators carry g3's full guard sets (guards run
  before the first mutation; a refusal returns an enum reason and touches nothing),
  and undo is a **journal of slot writes** — the complete journal, including
  free-list links and counters, so do→revert restores bit-identical state and
  element IDs (g3's per-element add/remove records were rejected precisely because
  they don't restore IDs; replay verifies each slot's expected value before writing,
  so out-of-order application throws instead of corrupting).
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
- **The exact (imprint) boolean uses Euler operators + flip recovery, not per-face CDT.**
  `MeshMeshCut` finds intersection segments (BVH broad phase, Möller interval narrow
  phase) and `MeshImprinter` cuts them into both meshes with `EditableMesh.SplitEdge`
  (edge crossings), `PokeFace` (interior points), and constrained `FlipEdge` recovery
  (Anglada). The reason for operators over per-face triangulation: a `SplitEdge` updates
  **both** adjacent faces, so an intra-mesh T-junction cannot arise by construction,
  and every step is guarded and journaled — a failed imprint reverts bit-identically
  through `MeshChangeSet` instead of leaving a half-cut mesh. Classification is then
  **per patch** (flood-fill across non-seam edges, one winding-number probe at the
  largest triangle's centroid), because the intersection curve is an edge of both
  meshes, so no patch straddles the other surface. It stays opt-in
  (`BooleanMethod.Exact`) only because coplanar overlaps are rejected rather than
  classified; BSP handles those, so the default cannot flip until they are.
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
- **Batch evaluation is SIMD, and the layout decision is "transpose once at the root".**
  A lane-wise kernel wants x's contiguous; the public signature hands over interleaved
  `Vector3d` (right for callers, who all hold AoS arrays). So the base `Evaluate`
  deinterleaves into pooled scratch once at the AST root and drives an internal SoA seam
  that operators forward unchanged to their children — the transpose is paid once per
  batch, not once per node. Kernels use `Vector<double>` rather than per-ISA intrinsics
  so one kernel serves NEON/AVX2/AVX-512. The contract is **bit-for-bit equality with
  the scalar path** (same terms, same association order, scalar tail), which is what
  makes a fast path safe to enable unconditionally; transcendental-using nodes (gyroid,
  exponential falloff) are deliberately left scalar because no vector transcendental
  reproduces `Math.Sin`/`Math.Exp` exactly, and a silently divergent fast path is worse
  than no fast path.
- **Narrow-band grids** evaluate the field only near its surface and fill the rest by a
  distance transform. Two properties of *this* engine make it simpler than g3's
  mesh-specific version: the octree culling test is sound because distance is
  1-Lipschitz and an `Sdf`'s magnitude is a lower bound on the true distance (the
  engine's own contract), and no ray-parity signing pass is needed at all because an
  `Sdf` is sign-exact — which is also why it accelerates any expensive field rather than
  only meshes. The fill is a two-scan chamfer (causal + anti-causal = complete, no
  iteration to convergence), and the deliberate trade is an **over-estimating** outward
  magnitude (~13% worst case) rather than Borgefors-optimized accuracy, so the invariant
  "never reports nearer than the truth" holds.

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
- **A loft's blend is solved once at construction, not per-u.** `LoftedSurface` is
  P(u,v) = Σ αₖ(v)·Cₖ(uₖ) with α the cardinal basis of B-spline interpolation, inverted
  once. The tempting alternative — chord-length reparameterizing per u — gives every
  strip its *own* v mapping, so the rails two strips share no longer agree and the solid
  cracks along every junction. Three weld invariants then hold by construction rather
  than by tolerance: αₖ(v_j) = δⱼₖ is exact equality (so the end rows reproduce the
  section curves bit-for-bit, and those same curves are the caps' and neighbours'
  edges), the u-sampling rule lives on the surface because only it knows its sections,
  and rails evaluate the strip surface rather than re-interpolating junction points.
  A related lesson from its alignment search: **twist objectives must be
  centroid-relative** — leaving the sections' separation in makes the objective a large
  constant plus a tiny quadratic well, costing the minimizer ~8 digits and leaving
  residual twist past the weld tolerance.
- **Draft is a plane rotation about the neutral line, not a shear.** Each selected
  face's plane rotates by exactly the draft angle toward the pull direction and its
  anchor slides in-plane onto the neutral plane, so the neutral geometry provably does
  not move and drafting twice by θ/2 equals once by θ. Because the result is still
  `PlaneSurface` faces, a drafted solid stays selectable, further-draftable and
  STEP-exportable — which a ruled-loft implementation would have given up.
- **Polyhedral offset is exact; curved offset is blocked on corners.** An offset plane
  is a plane and an offset vertex is a three-plane intersection, so shelling a polyhedron
  is closed-form. A cylinder's or revolve's offset *surface* is equally analytic — but
  where three offset curved faces meet, the corner needs genuine surface–surface
  re-intersection. That is the same missing machinery as sharp-corner fillet patches, so
  the two problems should be solved together rather than twice.
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
- **A swept surface's inverse evaluation reduces to its generator's parameter.** The
  generic `Surface.TryProjectPoint` scans a 2D (u,v) grid and Gauss–Newtons in two
  variables, which is correct for an arbitrary surface and wasteful for a sweep: an
  extrusion `P = C(u) + v·d` has `v` fixed by the direction component, so only the
  component orthogonal to `d` constrains `u`; a revolve's `u` is the azimuth in closed
  form once `v` matches the generator's (radius, axial) profile. Scanning the generator
  alone and refining in 1D is not a micro-optimization — inverse evaluation is the
  inner loop of every face pullback, so it was essentially the entire cost of the B-Rep
  boolean (an order of magnitude on real models, output bit-identical). The general
  lesson: **when a surface is generated by sweeping a curve, project onto the curve,
  not onto the surface.** It does not extend to `SweptSurface` (RMF frames vary along
  the path) or `NurbsSurface`, which keep the base implementation honestly.
- **Bounded planar-carrier tier** — an extrusion of a straight generator *is* a plane,
  but a **bounded** one, so it cannot simply be promoted: the analytic line has to be
  clipped to the generator's parallelogram, not just to the caller's region
  (`TryPlanarPatch`, straightness decided by sampling the real generator, since
  `Underlying` is a type hint and not a position). Better, when the generator's plane is
  *parallel to the cutting plane*, the section is exactly the generator **translated**
  along `direction·v` (`TryPlaneExtrudedSection`) — exact for any generator shape
  (lines, slot arcs, glyph Béziers), and its endpoints come from the generator's own
  points, so adjacent profile segments share their corner bit-for-bit and the outline
  closes. This tier exists because the marching tier below cannot terminate a curve
  exactly on a boundary (see the next bullet), and pocket walls need exactly that.
- **Marching tier** for every other pair: grid-sample both surfaces, pair nearby samples
  with a BVH `Nearest` query, refine each pair onto the intersection with damped
  Gauss–Newton, then trace each branch with a tangent predictor (`n_a × n_b`) and a
  4×4 Newton corrector (3 closure equations + 1 step-plane constraint) over the
  parameter 4-tuple. Periodic parameter directions (cylinder/sphere azimuth, closed
  generators, full revolutions) are handled by wrapping, so branches crossing seams
  don't split; closed loops are detected by proximity to the start; consumed seeds
  prevent duplicate branches. Output is `PolylineCurve3d` — exact at the traced
  vertices (corrector converges to ~1e-10), chordal in between; step size derives from
  the region diagonal. **A tracer curve never ends exactly on a bounded generator's
  end**: the trace loop breaks the step *after* the corrector leaves the domain, so the
  polyline stops up to one march step short. That is fine for closed loops and for
  curves clipped by a region, and fatal wherever the curve must terminate on a boundary
  — which is why the bounded planar tier above exists (a pocket outline whose four cuts
  each miss their corners by a fraction of a millimetre never closes, and the boolean
  is then left with single-use edges, or worse takes the disjoint fast path and buries
  the tool as an internal cavity: closed, valid, and wrong).

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

- **The construction tree is the seam between an immutable graph and stateful UI.** A
  tree row is *a node reference plus a positional path*, and both halves earn their
  keep: `Shape` is an immutable, shared graph, so one sub-shape can appear at several
  paths (a pattern operand). The **path** distinguishes rows and carries expansion and
  selection state, so it survives a live reload that rebuilds the graph; the
  **reference** is what previews are keyed by, so a shared sub-shape lowers once no
  matter how many rows show it. Previews are line geometry only (a sketch flattened
  onto its plane, or a sub-shape's feature edges) — never meshes — built on a
  background task, because the one rule the viewer cannot break is that lowering never
  runs on the UI or render thread.
- **A smart component's local origin is its SEATING DATUM, not the host face.** That one
  choice is what makes the hardware library composable: `SeatDepth` says how far below
  the host's face the datum sits and `InsertedLength` how far the body reaches below it,
  so a counterbored screw and a proud one are the *same geometry* at different poses
  (one shared `Part`, many occurrences), and grip/engagement arithmetic for a two-body
  stack is a single consistent system rather than per-seating special cases. The second
  decision worth recording: the host preparation is a **`Feature`**, not a one-shot cut —
  which is why suppressing a placement removes its bore as well as its occurrence, and
  why a thickness change re-seats the fastener and re-cuts the hole.
- **Text maps onto the sketch vocabulary exactly, which is why it is cheap.** TrueType
  `glyf` outlines are lines plus quadratic Béziers, and `Sketch` already has `LineTo`
  and `QuadraticTo` — so a glyph converts with no flattening and inherits everything a
  sketch has: exact NURBS profiles for B-Rep, the exact 2D signed distance for the
  implicit engine, crisp tessellation for printing. The font reader is hand-rolled for
  the same reason `PngWriter` and the EGL binding are: kernel projects pack to NuGet and
  do not take third-party dependencies. Counter (hole) classification is deliberately
  containment-based rather than orientation-based — real fonts violate TrueType's
  CW-outer convention — and deliberately self-contained from `Region2d` so text does not
  couple to the 2D region engine. Glyph unions ride the boolean disjoint fast path (one
  shell per glyph), which is why a whole word lowers cheaply.
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

- **Surface Nets streams the grid in a window of x-slabs.** The dense sampler's *memory*
  was the wall on resolution, not its speed. Cells only ever need value slabs i and i+1
  and cell maps for i−1 and i, so the whole algorithm fits a sliding window; sizing that
  window to a memory budget makes the small case (window == whole grid) the *same code
  path* rather than a second implementation, which is the property that kept the change
  safe. The load-bearing subtlety is face ordering: the three quad passes are nested
  differently (X is i-major, Y j-major, Z k-major), so streaming by i must bucket Y quads
  by j and Z quads by k and concatenate at the end to reproduce the dense order exactly.
  Miss that and "bit-for-bit identical" quietly holds for vertices and fails for faces.
- **A deinterleaved batch entry exists because bulk producers *generate* their samples.**
  The interleaved `Vector3d` overload stays the general API, but forcing a procedural
  producer through an array of points costs 24 bytes per sample and a transpose the root
  immediately undoes. Both overloads drive the same `EvaluateBatch` seam with the same
  chunking, so they agree bit for bit — and a node that overrides the *interleaved* public
  entry to intercept whole batches would not see the deinterleaved one, which is exactly
  why `EvaluateBatch`, not either public entry, is documented as the seam that always sees
  every batch.
- **Two-level block index, chosen over hashing.** A hash table would also have made large
  sparse domains work, but two dense array indices are faster, need no key type, and avoid
  this repo's standing lesson about packing structured 3D keys into hashed integers. The
  idea is g3's `BiGrid3`; g3's own implementation is an unfinished stub with no value API
  and no in-repo consumer, so the idea was adopted and the code was not. Surveying a
  library is for ideas, not implementations — the same conclusion the hole-fill work
  reached independently.
- **The extruded-region node memoizes per (x, y), and that beats the SIMD underneath it.**
  A prism's field is constant along z and every bulk consumer samples z fastest, so a
  batch is normally a handful of long constant-xy runs. This is an *exact* memoization —
  same input, same double — and the run test is deliberately an identity comparison
  (`==` on the coordinates), not a geometric one: an ulp-different coordinate simply
  misses the cache and gets its own evaluation. Worth roughly 10× on engraving-shaped
  profiles, where the vector kernels beneath it are worth about a third of that. Naming a
  task "vectorize X" can point at the wrong lever entirely; the win was structural, in the
  *consumer*.
- **`SketchRegion` preserves segment order even though it need not.** The distance fold is
  a running minimum over non-negative results with no NaN and no negative zero (every
  distance comes out of `Math.Sqrt`/`Math.Abs`), so it is order-independent — but keeping
  construction order makes the batch path a literal transcription of the scalar loop,
  which is what makes "bit-for-bit" reviewable rather than merely asserted.
- **Why there is no mesh-specific narrow band.** The generic band derives its sign from
  the source, which is sign-exact by contract, under a provable culling argument
  (|d(centre)| − circumradius > band ⟹ the node cannot straddle). A mesh-specific band
  must find its own sign *outside* the band: SDFGen and g3 use ray-crossing parity, and
  propagating the band's sign outward through the chamfer scan is not sound, because the
  chamfer's argmin is not the Euclidean argmin — "the nearest band sample is on my side"
  is not a theorem. Trading a proof for a ray cast, on the one property that boolean
  classification depends on kernel-wide, is the wrong trade — even though 74–85% of such a
  bake's wall clock genuinely is source evaluation.
- **A sliver's normal is the boundary curve's binormal — which is why "harmless" zero-area
  triangles are not.** For three points at arc spacing h on a curve,
  (P₁−P₀) × (P₂−P₁) ≈ h³·T × K, and T × K = k_g·N + k_n·(T × N). A sliver clipped along a
  trimmed face's boundary therefore agrees with the surface only in proportion to that
  boundary's *geodesic* curvature. Wherever a trim curve is tangent to a neighbouring face
  — every fillet's tangency line, every miter ellipse endpoint — k_g passes through zero
  and the sliver's orientation is decided by rounding. That is the whole explanation for
  the folded lens at mitered fillet corners, and it is why the fix is structural (zip the
  paired chains) rather than a tolerance.
- **When a trimmed region's loop is a band, its boundary polylines are already paired, so
  the correct triangulation is a zip.** General polygon triangulation throws that pairing
  away. On a flat region that only costs quality; on a curved one it detaches facet
  normals from surface normals, per the bullet above. Two corollaries learned with it:
  anisotropic uv is a trap for any Euclidean heuristic (a mitered band is ~1.57 × 1.0 in
  parameter space while the surface is 3.14 × 30 in model units, so "shortest diagonal" in
  raw uv is not shortest on the surface — precisely why the clipper chose to eat the dense
  chains); and **refinement is not a convergence mechanism**, because the midpoint-split
  pass terminates on a monotone-decrease rule and keeps a coarse patch wherever that rule
  cuts a cascade. Get the base triangulation right.
- **Loud refusal over silent fallback, restated for tessellation.** A fallback is
  legitimate only when the fallback path computes *the same thing more coarsely*. The
  natural parameter grid covers the surface's whole rectangle, so for a trimmed face it
  computes something else entirely — falling back to it was not coarse geometry but wrong
  geometry, welding into an open mesh without complaint. Failure messages must carry the
  **sample counts**, because some failures exist only at high density and are invisible in
  a default-quality repro.
- **Remeshing constraints live on vertices, not edges — because of our topology.** g3
  keys its `MeshConstraints` by edge, and copying that would have been a latent
  correctness bug here: an undirected edge is named by the smaller of a twin pair, a
  collapse *merges* edge pairs so the survivor gets a different canonical index, and freed
  indices are recycled. An edge-keyed table therefore goes stale after the first collapse
  — or worse, silently aliases a different edge. Vertex indices never do, because a
  collapse always removes the *unpinned* end. Everything the edge flags expressed falls
  out of that: two pinned ends means neither collapse nor flip (a flip destroys the edge),
  while splitting stays legal and the midpoint inherits the pin, so a constrained chain
  keeps its geometry while gaining resolution. Boundary and crease pins are re-derived
  from geometry each pass and need no bookkeeping at all. A related tuning note worth
  keeping: the split/collapse thresholds are 1.33 L / 0.66 L rather than Botsch's 4/3 and
  4/5, which thrash — a fresh split lands *below* the collapse threshold.
- **Prefer the standard algorithm to the reference library's heuristic.** g3's
  `MinimalHoleFill` is four iterative edge-flip passes; its own comments describe strong
  ordering effects, non-convergence, a hard pass cap to stop oscillation, and a forced
  interior-vertex-removal stage with a debugger break left in. The Barequet–Sharir/Liepa
  dynamic program answers the same question deterministically and globally optimally in
  O(n³) time and O(n²) space, which is nothing at realistic rim lengths. Surveying a
  library for *ideas* is not the same as adopting its implementation choices.
- **2D offset is one algorithm, not two.** An outward offset is the region unioned with a
  slab per edge and a join per corner; the *inward* offset is that same dilation applied
  to the complement. Writing erosion as complement-dilation costs one bounding rectangle
  and buys the property that matters: self-intersection is not a case to detect and clean
  up, it simply does not arise. Shrink a plate through a narrow neck and the union returns
  two regions, or none — which is why `Offset` returns a list rather than a region. Round
  joins are *inscribed* polygonal arcs, matching `Sketch.ToRegions`' one-sided contract,
  so a circle offset by d lands just inside π(r+d)² and error never accumulates in the
  unsafe direction.
- **Two ULP-scale lessons from the 2D work, both of which silently destroyed geometry.**
  A miter apex must divide by `sum.LengthSquared`, never by `sum.Length` squared: at a
  right angle the former is exactly 2 and the latter 2.0000000000000004, which tilts the
  apex a few ULPs off both offset lines, stops the collinear T-junctions collapsing, and
  returns a mitered square with eight corners. And `Arrangement2d`'s hole assignment must
  be **structural, not metric** — a lone convex cell was adopting its own reversed
  perimeter as a hole, because the two shoelace sums differ by one ULP and every vertex is
  shared, so the containment probe sat exactly *on* the boundary and decided by luck. The
  cell cancelled to ~1e-16 and was dropped, silently removing a whole operand from a
  union. The fix is not a wider epsilon but the observation that loops of the same
  connected component can never nest — a loop reachable from the cell's own loop would
  have been traced as part of it.
- **Bulk 2D unions of projected geometry need relative quantization.** Two mesh vertices
  on the same feature line are only ULP-equal once projected, so edges that ought to be
  collinear sit ~2e-16 apart: too small for the arrangement to see as a T-junction, too
  large to ignore. The one-ULP sliver's interior sample rounds back onto its own boundary
  and the answer starts depending on merge order (measured: 60.42 vs 59.33 on the same
  torus silhouette; a finer one threw outright). Quantizing to 1e-12 of the outline extent
  — the scale-free tier — makes every merge order agree. The companion decision is
  performance: fold the unions through a balanced tree over *Morton-sorted* faces, 67 ms
  against 2.4 s unsorted and 259 s accumulated linearly, because merging face 1 with face
  900 produces two disjoint regions and cancels nothing.
- **A wedge is an extrusion, so it does not get its own code path.** `Shape.Wedge` carries
  a trapezoidal sketch-extrusion internally and every lowering delegates to it. The
  primitive is therefore native in all three representations, exact under any affine
  transform, and correct in the construction tree — for free, rather than through a fourth
  implementation that would have had to be kept in step with the other three.
- **Logging is `Microsoft.Extensions.Logging.Abstractions`, and that reversed an earlier
  decision.** The viewer originally defined a two-method `IEngrCadLog` seam *specifically*
  to avoid a `Microsoft.Extensions.*` reference, with adapter snippets in its README. The
  reversal is worth recording because the original reasoning was locally sound and
  globally wrong: to save one reference that nearly every .NET host already has
  transitively, the shim made *every* consumer write an adapter. Abstractions-only (no
  provider) keeps the substance of the original goal — consumers still choose their sink,
  and the kernel projects take no reference at all, so "kernel code carries no UI
  dependency" is untouched; a logging abstraction is not UI. What the standard interface
  bought that the shim could not: **levels** (a skipped part is a Warning, not an error
  sharing one channel with "nothing exported"), **structured templates** with named
  placeholders instead of pre-baked strings, and **stable event IDs** for sinks to key on.
  Two deliberate choices sit on top. The unconfigured default is a console logger rather
  than `NullLogger`, because a *library* defaults to silence but a *program's front door*
  does not, and `EngrCad.Run` is a model program's front door — `NullLogger.Instance` is
  available and explicit for anyone who wants silence. And the console logger resolves
  `Console.Out` on every call rather than caching the writer, so it follows
  `EngrCAD.Mcp`'s `StdoutGuard` when that repoints stdout at stderr; caching would
  reintroduce exactly the protocol corruption the guard exists to prevent.

- **Filleting** (`Filleting.FilletEdge`): closed circular rims where a planar cap meets a
  coaxial cylindrical band are replaced by an exact quarter-torus (`RevolvedSurface` over
  a `CurveSegment` arc), patching the cap and band in place through their loops.
- **A sharp rim corner mitres on an ellipse; it is not a ball.** The intuition that a
  fillet corner is a sphere of the fillet radius is wrong *for a rim*, and wrong in an
  instructive way: at a rim corner only **two** of the three incident edges are blended —
  the two side faces keep their shared sharp edge. A sphere is tangent to all three planes
  at single *points*, so at the tangency plane the cross-section would jump from rounded
  to sharp and the surface would not close. What the union of the two removed slivers
  actually gives is the face inset by δ(t) = r − √(r²−t²) with **sharp** corners: two
  equal-radius cylinders whose axes intersect — a bicylinder, whose intersection is two
  ellipses. The right branch is read off the two points the surgery has already computed
  (centre = top − up·r, semi-axes up·r and bottom − centre, perpendicular by
  construction), so no trigonometry gets a chance to round off; the circular junction arc
  that tangent-continuous rims use is exactly the |bottom − centre| = r specialization.
- **Rounding a whole solid is the morphological opening, not a cascade of booleans.**
  `FilletAllEdges` builds (K ⊖ B_r) ⊕ B_r directly: each face keeps its plane with a
  shrunk boundary, each edge becomes a cylindrical band about the **eroded** edge line,
  each vertex a spherical patch on the eroded vertex bounded by great-circle arcs. Nothing
  intersects anything, so there is no seam to seal and every face stays full-domain (the
  natural tessellation grid, not the trimmed path). Steiner's formula is the check, and it
  is a good one: the deficit falls by exactly 4.0 per halving of sample spacing, which is
  the quadratic convergence a correct surface must show and an approximate one will not.
  The restriction to corners where one incident face is perpendicular to the other two is
  not arbitrary — it is precisely the condition under which the spherical triangle becomes
  a lune closed by an equatorial great circle, i.e. an *exact* surface of revolution.
- **Corner arcs must be angle-parameterized.** Every arc bounding a corner patch is a
  `CurveSegment` over `Circle3d`, never a rational NURBS arc, because the patch is a
  revolve sampled at even *angles*. A NURBS arc traces the same curve but samples to
  different points, and the patch stops welding to its band. This is the same family of
  bug as the phase-alignment lessons elsewhere: two sides of a shared curve must agree on
  the *parameterization*, not merely on the point set.
- **Variable-radius fillets are blocked by the corner, not the band.** The band would be
  exact — a linear radius law between two equal-weight rational arcs is a degree-(2,1)
  NURBS whose v-sections are true circles, G1 with both neighbours. But two such bands
  meet in the intersection of two non-cylindrical surfaces, which is not a conic, so there
  is no exact miter to weld them on. Variable-*setback* chamfers escape this (the corner
  segment is a boundary ruling of both bilinear strips) and are therefore the cheaper next
  step, not the harder one.
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
- **Viewer section planes**: an axis-aligned clip (X, Y, or Z in v1; the shader takes
  a general axis vector + offset, `dot(world, uSectionAxis) > uSectionOffset`, so
  arbitrary `Frame3d` planes are a state-plumbing change, not a shader change),
  implemented as fragment-shader `discard` with `gl_FrontFacing` backface detection
  shading exposed interiors as a flat cut material (axis-agnostic by construction).
  The clipping-consistency rule: anything that *is* the model (fills, feature edges,
  wireframes, **and** point sprites) clips identically — the discard lives in all
  three model programs — while scene furniture (grid, axes) never clips. Changing the
  axis re-centers the plane (an offset along one axis is meaningless on another).
  Picking deliberately ignores the section plane in v1.
- **Per-part display modes** (`Part.DisplayMode`) live on the document model, not
  viewer-only state, so design code can set them and they survive tab switches and hot
  reloads (a reload rebuilds parts, so model-code modes win again — consistent with the
  camera-persistence model). Wireframe reuses the line program over every unique mesh
  edge (`WireframeEdges`); translucent parts draw after opaque, sorted back-to-front by
  center with depth-writes off and opaque silhouette edges on top — a per-part (not
  per-triangle) sort, so interpenetrating translucent parts can show blend-order
  artifacts (section mode stays the tool for exact interior inspection).
- **Global view style vs per-part modes**: the viewport-wide style (points / wireframe
  / shaded / shaded+edges) is *viewer* state (`ViewportControl.ViewStyle`), not
  document state — it is how you are looking, not what the model is. The precedence
  rule lives in exactly one place (`RenderModes.Resolve`, RenderCore.cs, used by both
  render passes): an explicitly non-default `Part.DisplayMode` overrides the global
  style; parts at the default (Shaded) follow it. `DisplayMode.Shaded` being the
  default means it cannot override — accepted as the honest reading of "default".
- **Headless offscreen rendering** (`EngrCad.RenderToImage` / `--render`) renders a
  scene to PNG with no window, so tests and agents verify viewer changes by inspecting
  pixels instead of screenshotting the live app. It creates a **direct EGL pbuffer
  context** over Avalonia's bundled ANGLE natives by P/Invoke (preferring D3D11
  hardware → WARP software so it survives CI and locked sessions), with no Avalonia UI.
  A `PngWriter` (dependency-free deflate + CRC-32) encodes the framebuffer. A lesson
  worth keeping: Avalonia's `av_libglesv2.dll` exports EGL entry points under an `EGL_`
  prefix (not the standard `egl*`), so the binding tries both spellings. Both passes
  share `RenderCore.cs` (shaders, camera math, mode resolution, furniture) — the early
  duplicated-shader phase drifted and was retired; the offscreen pass has full window
  parity (display modes, global view style, section planes), neutralizing only the
  selection highlight.
- **3D annotations (PMI)** — model-based definition: the model carries dimensions,
  notes, and datum labels instead of 2D drawings. Design decisions worth keeping:
  - **Data + measurement live in Modeling; drawing lives in the Viewer.** An
    `Annotation` resolves to a render-neutral `ResolvedAnnotation` (part-local
    anchors, placement offset, formatted text, measured value); the viewer poses it
    by the instance transform, so assemblies annotate for free and the kernel stays
    UI-free.
  - **Selectors, not stored values.** Auto-measuring dimensions store *semantic
    queries* (`Func<BrepSolid, BrepFace/BrepEdge>` in the `BrepQueries` vocabulary)
    and re-measure on every resolution — the same topological-naming answer the rim
    features use, so a dimension tracks parameter edits and `FeatureHistory`
    regeneration instead of going stale. `Resolve(Func<BrepSolid>)` takes the solid
    *lazily* so point-anchored annotations never force a B-Rep lowering.
  - **Failure is a diagnostic, not a crash**: `Part.TryResolveAnnotations` caches
    per-part success *or* error (a selector broken by an edit becomes a status-bar
    message); `Scene.PreMesh` pre-resolves so lowering stays off the render thread,
    mirroring the mesh-prep contract.
  - **Text is a stroke font, not a texture atlas** (`StrokeFont`, grown from the
    view cube's lettering): polyline glyphs through the existing line program — no
    new shaders, no font rasterization, resolution-independent, and the same table
    serves flat labels (cube faces) and billboarded annotation text. Dimension
    symbols (diameter, depth, counterbore, countersink...) are hand-built glyphs
    keyed by unicode escapes; source files stay pure ASCII (the ANGLE lesson).
  - **Billboarding is CPU-side and cached**: `AnnotationGeometry` rebuilds
    world-space segments only when the camera pose, viewport, or annotation set
    changes (`AnnotationCamera` value-equality is the key — a static view costs one
    struct comparison per frame; orbiting rebuilds a few hundred segments, far below
    one part draw). Screen-constant sizing = style pixels × world-per-pixel at each
    element's own depth (perspective) or the frustum constant (ortho).
  - **Always-on-top v1** (depth test off for the pass, never section-clipped):
    dimensions must read from any angle; occlusion-aware dashing is a follow-up.
    And unlike the view-cube widget, annotations **do** render in the headless pass
    — they are documentation content, which is exactly what offscreen renders are
    for (the docs example page exercises it).
  - The **measure tool** is interactive dimensioning, not a separate feature: two
    surface picks (the existing raycast, now returning the hit point) build a
    transient point-to-point `LinearDimension` through the same layer.
- **A protocol dependency lives in its own package.** `EngrCAD.Mcp` is separate from
  `EngrCAD.Viewer` for the same reason the viewer is separate from the `EngrCAD`
  meta-package: someone who wants a window should not inherit an MCP stack, and someone
  who wants the kernel should inherit neither. It also keeps `EngrCad.Run` untouched —
  `EngrCadMcp.Run` intercepts `--mcp` and delegates everything else.
- **The stdout-guard pattern for any stdio protocol surface.** Over stdio, stdout *is*
  the protocol channel, and a single stray `Console.WriteLine` corrupts every session.
  The rule: capture the raw stdout handle for protocol frames, repoint `Console.Out` at
  stderr, and only *then* run user code (here the scene factory) — a design program that
  logs while it builds is otherwise fatal, and that ordering is the whole trick. The
  limit is honest and documented: code that opens the standard-output handle itself, or
  writes to fd 1 natively, is beyond reach.
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
