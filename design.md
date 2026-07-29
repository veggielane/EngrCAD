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
- **The sparse solver mini-library (`EngrCAD.Core.Solvers`) is shaped by three
  decisions.** (a) **CSR with an optional symmetric-upper form**, because every consumer
  in sight (Laplacian smoothing/deformation, the future sketch constraint solver, FEA
  stiffness assembly) builds symmetric positive-definite operators finite-element style —
  accumulate coefficient contributions, then solve — so the builder accumulates
  duplicates and packs deterministically, and symmetric storage halves memory and
  bandwidth by mirroring during multiply rather than storing both triangles.
  (b) **Two solvers on purpose, split by right-hand-side count**: `SparseCholesky`
  (up-looking, elimination tree + ereach) factors once and substitutes per RHS — the
  Laplacian shape, where x/y/z share one operator and an interactive deformer re-solves
  per drag — while Jacobi-preconditioned `SparseSymmetricCG` wins one-shot solves at
  scale. Measured (Release, win-arm64, grid Laplacians): natural-order factorization is
  4.7 ms at 2.5k unknowns and 133 ms at 14.4k, past which one-shot CG beats
  factor+3-solves (62.5k: 24.5 ms vs 1.6 s) — so **natural ordering suffices at
  deformation-ROI scale**, a measurement rather than an assumption.
  **AMD has since landed** (`SparseOrdering.Amd`, `AmdOrdering`) as an opt-in rather
  than a default, and the reason it is opt-in is the one worth writing down: a
  fill-reducing permutation changes the summation order, so an AMD solve is *not*
  bit-identical to a natural one, and every committed number upstream was measured
  natural. It is 4.6–13.4× on factor time and 3.5–8.3× on fill across 2D and 3D grid
  Laplacians from 2.5k to 64k unknowns, and it never loses — ordering costs single-digit
  milliseconds at 62 500 unknowns. **The direct-vs-iterative verdict turned out to depend
  on the operator's conditioning far more than on its size**, which the original
  measurement could not see because it only ever used L + I: that shift is strongly
  diagonally dominant, so CG converges in an *n-independent* ~35 iterations. Drop it —
  a pure Dirichlet Laplacian, the FEA regime — and at 62 500 unknowns CG needs 858
  iterations (750 ms) against AMD's 221 ms factor plus 5.8 ms per solve, so the direct
  solve wins on the FIRST right-hand side. In 3D the crossover is real but distant (52
  right-hand sides at 13 824 unknowns), because 3D fill grows like n² however it is
  ordered. Full table in the Core README. (c) **Convergence is a return value**
  (`SparseSolveReport`), and failure is honest: CG breaks out on nonpositive curvature
  instead of dividing by it, Cholesky throws naming the offending pivot column — the
  repo's report-what-happened convention applied to numerics. The library is
  deliberately dependency-free and mesh-agnostic (doubles + int indices), so the mesh
  engine adapts to it, never the reverse.

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
- **Booleans were BSP-based first** (csg.js): robust enough for well-conditioned inputs
  and two orders of magnitude simpler than exact intersection booleans, which made it the
  right thing to build before the mesh engine had an intersection curve at all. It has
  since been **retired outright** — see the exact-boolean bullet below for what replaced
  it and why. Two of its properties outlived it: **seam zipping** (any directed edge with
  no reverse partner gets the other side's collinear crack vertices inserted, so
  independently tessellated sides weld shut) survives in `MeshWelder` for the B-Rep
  tessellator, and every absolute epsilon it carried is the origin of this codebase's
  scale-free-guard rule.
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
  meshes, so no patch straddles the other surface. Coplanar overlaps — the last thing
  BSP did that this path could not — are classified by **normal agreement**
  (`CoincidentSurface`), which is what made it first the default and then the *only*
  boolean: `Csg.cs` and the `BooleanMethod` selector are gone. Maintaining two algorithms
  had stopped being a hedge and become a liability, since the measurement was one-sided
  in every dimension (a 32k+32k sphere union: 0.71 s closed here against 74.9 s for an
  *open* 347k-face shell, plus correct results at 1e-5 scale and under near-tangency
  where BSP's absolute constants failed outright).
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

- **Remeshing is exposed as a `Shape` node, not a `Part` display option** — the decision
  worth writing down, because both readings are defensible. A remesh looks like a display
  setting: the shape is unchanged and only the discretization moves. But that is only true
  of the *tessellation*, and the modelling layer's whole contract is about what is exact.
  A remeshed sphere is faithful to the mesh it was projected onto, not to the sphere, so
  `ToBrep()` genuinely cannot express it and `ToImplicit()` genuinely produces a different
  field from the child's. A `Part` flag would hide that behind a rendering knob; a node
  makes `Explain` state it (Mesh native, Implicit bridged through a mesh SDF, B-Rep
  Impossible) and lets the operation compose — `shape.Remeshed(2).ToMesh()` is a model, not
  a viewer setting, and it survives export, MCP description and the construction tree. The
  cost is that a design must say where the remesh happens, which is the honest requirement:
  put it in the middle of a graph and everything downstream inherits a tessellation.
  A `Part`-level display remesh remains a separate, smaller idea in the backlog.
- **Region remeshing rides on the region operator's seam contract, which had to grow
  first.** `MeshRegionOperator` originally refused any replacement that re-split a seam
  edge, since the neighbour still held the un-split edge (a T-junction). Carrying the split
  into the neighbours is what makes `RegionRemesher` and Loop subdivision round-trip, and
  the ordering of its two checks is load-bearing: *every original seam vertex must be shown
  present before any chain is walked*, because otherwise a replacement that MOVED a rim
  vertex is indistinguishable from one that removed it and inserted a new one nearby — and
  would be accepted as a refinement, welding a crack silently. Refinement is the feature;
  the presence check is what keeps it from being a hole in the contract.
- **Deformation/analysis foundation (Laplacian tools, exp map, ICP)** — the design calls
  worth recording. *Global implicit vs local explicit smoothing*: `LaplacianMeshSmoother`
  solves (M + λL)x′ = Mx in one sparse solve and deliberately does NOT replace the
  remesher's per-pass relaxation — the remesher equalizes triangle shape under a
  projection target (geometry preserved), the smoother changes geometry with fixed
  topology; both exist because they answer different questions. Its λ is
  `TimeStep · h̄²` so the option is dimensionless (scale-free rule). *Cotangent
  robustness*: a degenerate triangle's cotangent is noise, so the whole edge falls back
  to uniform weight 1 under the 1e-13-relative sliver guard (the `PolygonTriangulator`
  measure), and a negative cotangent SUM is sign-clamped to 0 — an indefinite L would
  make the SPD solve dishonest, and clamping merely stops diffusion across that edge.
  *Deformation is bi-Laplacian with SOFT handles*: `LaplacianMeshDeformer` minimizes
  ‖L(x − x₀)‖² + Σw²‖x_h − c_h‖² because a hard handle transmits C⁰ (a cone); rims and
  pins are hard-substituted and therefore bit-identical, which is exactly
  `MeshRegionOperator`'s seam contract, so ROI deformation composes with reinsertion for
  free. *The exp map rides Dijkstra*: `MeshLocalParam` (Schmidt-style DEM) averages
  upwind predictions in `DijkstraGraphDistance`'s settle order, transporting the seed
  frame by the trig-free minimal rotation `v·c + (axis×v) + axis(axis·v)/(1+c)` — stable
  for every c > −1 since (1−c)/sin² = 1/(1+c). *ICP refuses rather than regularizes*:
  point-to-plane normal equations go singular exactly when the pose is under-constrained
  (all-planar correspondences), and `MeshIcp` reports `Converged = false` instead of
  Tikhonov-damping toward an arbitrary minimum — `MateSolver`'s convention applied to
  registration. `MeshIsoCurves` re-applies the boolean seam lesson in miniature: one
  crossing per undirected edge, computed from the lower-indexed vertex, so adjacent
  triangles share endpoints bit-identically and chains assemble combinatorially.

## 3b. Tetrahedral meshing (`EngrCAD.Fea`)

**Why its own project.** A `TetMesh` is a genuinely different structure from a
`HalfEdgeMesh` — structure-of-arrays vertices, four vertex indices per element, tagged
boundary facets, no half-edge topology at all — and the algorithms that build it share
nothing with the surface engine's booleans, subdivision or decimation. Folding it into
`EngrCAD.Mesh` would put a volume representation inside the surface engine to save one
project reference, and Simulation has a lot of growing left (stiffness assembly, thermal,
results fields) that wants somewhere to grow. The dependency shape settles it: Fea needs
Core (predicates, BVH, solvers) and Mesh (the input surface, winding numbers), and nothing
needs Fea — a clean leaf.

**The pipeline.** Delaunay tetrahedralization of the surface's vertices (incremental
Bowyer–Watson over exact `Predicates3d`) → classification → boundary recovery → optional
quality refinement. Three decisions in it are load-bearing, and each was reached by a
failure rather than by design.

1. **Recovery works per planar PATCH, not per input triangle.** A Delaunay triangulation
   picks its own diagonal across a coplanar quad, and both diagonals are equally Delaunay
   when the four corners are cocircular — which they are on every box. Demanding the *input*
   triangle therefore cannot converge: every refinement of a cocircular configuration is
   cocircular again (measured: a unit cube exhausted a 500 000-point Steiner budget). A patch
   — union-find over edge-adjacent, coplanar, equally-tagged triangles — states the property
   that actually matters, that the skin equals the input surface *as a point set*, while
   leaving the triangulation free inside a flat region. A patch never straddles two tags, so
   boundary conditions stay attributable.
2. **Classification comes BEFORE the boundary.** The natural arrangement is the reverse —
   recover the triangles, flood-fill between them — and beyond the diagonal problem it has a
   second, subtler failure: an exactly-coplanar quad makes the tetrahedralization contain a
   **flat tetrahedron**, whose four faces are *both* diagonals at once. "The faces lying in
   this patch" then covers the patch twice, an area-coverage test reads exactly 2.0000×, and
   refinement never converges (measured: 40 of 72 patches on a 12×6 UV sphere, the excess
   halving per round and never reaching zero). Deriving the boundary from a classification
   decided **independently** — the winding number at each element's centroid — has neither
   problem: a flat tetrahedron has no volume, is never kept, and its two interior-facing
   faces fall out as the boundary with no tie to break.
3. **Refinement follows Ruppert's rule in Ruppert's ORDER.** A circumcentre that encroaches a
   boundary sub-triangle is never inserted; the sub-triangle is split instead. Inserting
   first and repairing the boundary afterwards is the same two operations in the wrong order
   and it cascades. Two guards make it terminate: the accepted batch must be **independent**
   (the packing bound that makes Delaunay refinement finite holds only against the
   triangulation as it was when a circumcentre was computed, so a stale queue voids it), and
   encroachment splitting has a **size floor** (a circumcentre sitting essentially on the
   surface would otherwise encroach forever, each split halving the balls while the point
   stays inside).

**What is guaranteed and what is not.** Guaranteed: the boundary is the input surface, the
volume identity holds to round-off, every element is positively oriented (checked exactly),
output is deterministic, and every refusal names what failed. *Not* guaranteed: sliver-free
elements. Radius-edge bounds provably cannot exclude slivers, so `TetQualityReport` reports
minimum dihedral beside radius-edge and counts what the first measure cannot see. Sliver
exudation is the named next step (todo.md).

## 3c. Structural analysis (`EngrCAD.Fea`)

Small-strain isotropic linear elasticity on those meshes, 3 displacement degrees of freedom
per node, assembled onto `EngrCAD.Core.Solvers`. Full numbers in the project README; this
records the decisions.

**One analysis view, not two element pipelines.** `AnalysisMesh` wraps either a `TetMesh`
or a `QuadraticTetMesh` and reduces the difference to two integers (`NodesPerElement`,
`NodesPerFacet`). Assembly, boundary conditions, load integration, stress recovery and
publishing are written once against it. Writing them twice would be two chances to get the
same thing wrong, and the tests would then have to be doubled to catch it.

**Stiffness in index form, quadrature at the cheapest exact rule.** For an isotropic
material `B'DB` collapses to `L·N_i,a·N_j,b + M·N_i,b·N_j,a + M·(gradN_i · gradN_j)·d_ab`,
which is the same matrix with the symmetry manifest instead of emergent; a test asserts it
against an independently written `B'DB`. The rule is one point for a linear element and the
four-point degree-2 rule for a quadratic one — exact **only** because a straight-sided
10-node tetrahedron has a constant Jacobian. That is a property of the mesh, not of the
element, so it is *tested* rather than assumed: the stiffness must be unchanged under an
independent degree-3 rule, with a negative control that displaces one mid-edge node and
checks the two rules then disagree. A caller's `BodyForce` gets a degree-5 rule instead,
because under-integrating a load caps a convergence study at the quadrature's order rather
than the element's — a limit that looks exactly like a formulation defect.

**Supports are eliminated, not penalised.** Constrained degrees of freedom are removed from
the system rather than given a large diagonal, so the reduced matrix is genuinely positive
definite, its conditioning is the model's own, and a prescribed non-zero displacement moves
cleanly to the right-hand side as `f_free -= K_fc · u_c`. A penalty stiffness has to be
chosen relative to the material, and choosing it wrong is invisible in the answer.

**An unrestrained body is refused before the factorization, per connected body, with the
surviving motion DESCRIBED.** The six rigid modes are built over each component's own
nodes, normalised, and restricted to the constrained degrees of freedom; the null space of
that restriction is exactly the set of motions the supports permit at zero energy. It is
found by Jacobi eigen-decomposition of the 6×6 Gram (floor 1e-12 on eigenvalues = a 1e-6
relative singular value, the sketch-constraint solver's rule, since a Gram's eigenvalues
are squared singular values), and each null vector is unpacked back into a translation and
a located axis. Three points about that:

- **Per body**, because a fully fixed part beside a floating one is singular in a way no
  whole-model rigid mode describes — the same reason `MeshRepair` votes orientation per
  connected component.
- **The null space, not the rank.** A first version reported which candidate modes a
  pivoted Cholesky had not eliminated, which for a model pinned at one node named three
  *translations* when the surviving motions were three *rotations*. A rank tells you how
  many; only a null vector tells you which.
- **An axis is a line**, so the quoted point is its closest approach to the body's
  centroid. Pin the centroid and the pinned node comes back; pin a corner and the same
  lines come back through a different point on each. Documented rather than special-cased.

Letting the factorization discover it instead gives "nonpositive pivot at column 4713",
which tells nobody anything — the same argument as `BrepBoolean.Verified` and the
trimmed-face refusals.

**Nodal stress is a volume-weighted average, and the element values stay public.** A
displacement-based element gives a stress field that jumps across element faces. Averaging
is what a colour map wants and what converges — and it also smooths a *genuine*
discontinuity at a material interface or a re-entrant corner. The jump between neighbouring
elements is the standard error indicator, so hiding it behind the average would remove the
one cheap way to see that a mesh is too coarse.

**Publishing samples onto the display mesh by exact match first.** A tet solve's vertex set
need not be the display mesh's, so `SampleOnto` matches by bit-exact position where it can
— which is essentially every vertex in the normal case, because the same mesh was fed to
the mesher and its vertices survive verbatim — and falls back to the closest point on the
nearest boundary facet, interpolated with that facet's own shape functions. The sampling
distance is a reported out-parameter, so pairing two meshes that are not the same body
exposes itself instead of returning a plausible field.

**Verification fixtures are structured, and that was forced by measurement.** The Delaunay
mesher on a 100 × 10 × 10 beam returns 32% slivers, a minimum dihedral of 0.000° and two
elements whose exact signed volume is positive while their double-precision volume is
exactly 0.0; the factorization fails. Verifying a solver against that would be measuring
the mesher. Kuhn's subdivision of a grid gives bounded element quality, exactly
geometrically similar refinement sequences (which is what a measured convergence order
needs) and box-face tags a boundary condition can name. It lives in the test project: a
structured mesher is not a feature this project is shipping.

**Two lessons the work paid for**, both recorded in CLAUDE.md's numerical notes: a
degeneracy guard must ask the assembly's own arithmetic rather than restate it (the first
version tested the corner triple product where assembly integrates the isoparametric
Jacobian — same quantity, different bits, and elements passed the guard then integrated to
zero), and an order measured against a *different model's* answer stalls at the modelling
difference (the cantilever's clamped-edge singularity caps it at 1.86, which is why the
order table comes from a manufactured solution instead).

## 3d. Thermal analysis (`EngrCAD.Fea`)

Heat conduction on the same tetrahedral meshes, `q = -k·grad T`, with **one temperature
degree of freedom per node** where the structural solve has three displacements.

**It is deliberately the same three types with the physics swapped** — `ThermalModel` →
`ThermalSolver` → `ThermalResults`, over the same `AnalysisMesh`, the same `Facets`
selectors, the same builder shape, the same refuse-an-empty-selection-at-the-call rule, the
same `MeshField`/`.vtu` publishing. `Fix` becomes `Temperature`, `Pressure` becomes
`HeatFlux`, `Force` becomes `HeatLoad`, `Gravity`/`BodyForce` become `Generation`. The point
is not symmetry for its own sake: an engineer who has written one model can write the other
without learning a second vocabulary, and every lesson already baked into the structural
side (loads reduced at the call so the reported resultant is the true one; a selector that
matches nothing refused where the mistake was made) is inherited rather than re-derived.

What is genuinely different is **three things, and only three**.

**Convection lands in the MATRIX as well as the load vector.** `q = h(T - Tinf)` has a term
proportional to the unknown, so `Convection` is the one condition not pre-reduced to nodal
heat — its two halves are meaningless apart, and keeping them as one stored condition makes
applying one without the other structurally impossible. Two films on one facet accumulate
(h adds, and so does h·Tinf), which is the physically correct composition of parallel paths
and makes the call order irrelevant.

**The capacity matrix needs a quadrature rule two degrees above the conductivity's**, and
this is the kind of error that hides. Conductivity integrates `grad N · grad N`, degree
2(p−1); capacity integrates `N·N`, degree 2p. Reusing the conductivity's rule on a 4-node
capacity gives every entry `rho·c·V/16` — a **rank-one, singular** matrix — while its total
is still exactly `rho·c·V`. See CLAUDE.md's numerical notes: the total is the check everyone
reaches for, and it passes.

**The undriven refusal is a boolean, where the structural one is an eigen-solve.**
Conduction's null space on a connected body is *exactly* the constants: add any constant to
T and every gradient, hence every flux and every boundary condition, is unchanged. So the
check per connected body is "is there a prescribed temperature or a convective facet
anywhere?" — a prescribed node kills the constant by elimination, a convective facet kills
it because its surface matrix is strictly positive on constants, and a pure heat flux does
not, being Neumann. The structural analogue needs a Jacobi decomposition of a 6×6 Gram
because six rigid modes can *partly* survive; one mode cannot. A transient of an insulated
body is deliberately NOT refused — the capacity term is positive definite on its own.

**Time integration is a theta scheme at a constant step**, and the constancy is a design
decision rather than a simplification: the stepping matrix `C/dt + theta·K` depends on the
step and nothing else, so it is factored **once** and every step is a back-substitution.
That is the "factor once, solve many right-hand sides" argument `FeaSolveMethod.Direct`
explicitly records as *not* yet applying to the structural solver; here it does, so the
exact default is also the fast one. Adaptive stepping would refactor at every change and is
filed rather than half-built.

**Backward Euler is the default for L-stability, and the counterweight is recorded beside
the claim** — which is the part worth keeping, because "backward Euler is monotone" is the
sort of thing that gets written down and is only true of a *lumped* capacity. Measured on a
quenched bar (h²/alpha = 0.1 s): at long steps backward Euler makes zero backward moves
while Crank–Nicolson swings back by up to 106% of the temperature step, but at short steps
both undershoot (5.8% and 7.1%) because that is the consistent capacity matrix, not the
scheme.

**The capacity is consistent, and the reason lumping is filed rather than offered is
concrete**: row-sum lumping gives a 10-node tetrahedron `-V/20` at every corner node — a
negative heat capacity — which is the same integral that already makes a quadratic element's
gravity load negative at the corners. Any quadratic lumping has to be a scaled-diagonal
scheme, a different approximation with its own error, so it does not belong under the same
name.

**A boundary condition wins over the initial condition at t = 0.** "The surface is suddenly
held at Ts" means Ts for every t > 0, and the erfc solution is derived under exactly that
reading. The alternative — letting the initial value stand and transitioning inside the
first step — was built and measured: it charges the surface node's entire heat-up to that
step, and because the consistent capacity couples it to its neighbours they are dragged down
with it (49% undershoot against 5.8%). Doing the snap where the initial state is BUILT, not
only inside the stepping, is what keeps the stored t = 0 state and the arithmetic that steps
away from it consistent — the whole-run first law closed at 1.1e-2 until it did.

### Coupling, and the half that is invisible when it is missing

`StructuralModel.ThermalLoad` applies `eps0 = alpha·dT` as an initial strain. The load is
`integral(B'·D·eps0)`, which for an isotropic material collapses to a pure hydrostatic
`E/(1-2·nu)·alpha·dT` against a shape-function gradient — three lines, no matrix product.

The half that gets forgotten is that **stress is then `D(eps - eps0)`, not `D·eps`**. A bar
free to expand develops the full thermal strain and carries *zero* stress; without the
subtraction the recovery reports `E·alpha·dT` — 126 MPa for steel at 50 K — on a body under
no load at all, a number that looks entirely reasonable. Both halves read one stored field,
so applying the load is what arms the subtraction and there is no second call to forget.
`ComputeNodalStress` inlines its own strain pass for speed and therefore has to *ask* for the
correction explicitly; it asks rather than restating it, which is what keeps nodal and
element stress from disagreeing under a thermal load.

Two properties make the coupling safe to compose. The load is **self-equilibrated by
construction** — the shape functions are a partition of unity, so their gradients sum to
exactly zero — so a thermal load adds nothing to the applied resultant and the equilibrium
check keeps its meaning. And the two models must share the same `AnalysisMesh` **instance**,
checked rather than assumed: a temperature field crosses by node index, and two meshes of the
same body can number their nodes differently, so the alternative is applying each node's
temperature to some other node.

### Shared rather than parallel-built

Three things moved out rather than being written twice, and each has a reason beyond tidiness.
`FeaGuards` holds the element-Jacobian guard because its whole point is that it asks the
ASSEMBLY's own arithmetic — a second copy would be the same defect waiting for a third
occurrence. `SurfaceSampler` holds the display-mesh correspondence because the mapping has
nothing to do with the physics, and two copies would be two chances for a structural plot and
a thermal plot of the same part to disagree about which node a display vertex is. And the
direct-vs-CG advisory is `StructuralSolver.AdvisoryFor`, asked rather than restated, because
a heuristic stated twice is a heuristic that will drift.

**Verification is the deliverable**, on the same structured fixtures for the same reason; the
tables are in `src/EngrCAD.Fea/README.md` and `docs/examples/fea-thermal.md`. Four of the
traps it found were in the TESTS rather than the solver, and all four are the same shape —
a fixture or a manufactured field that quietly makes the measurement exact — which is why
they are recorded in CLAUDE.md's numerical notes beside the geometry ones.

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
- **The exactly-collinear boundary run forces the ear clipper into a fan, and that is
  the normal case rather than a pathology.** A cross-drilled bore wall is a periodic band
  with two hole loops, so it routes to the band-with-holes tier and gets ear-clipped -
  but both ring loops of an `ExtrudedSurface` pull back to a *bit-identical* v (measured:
  distinct v bits = 1, 32 of 32 uv triples exactly collinear), so `IsEar`'s `<= 0`
  rejects every corner along both chains and only the unrolled rectangle's four corners
  are ever clippable. The result is a fan, and `Refine` then bisects its long chords into
  slivers. **No change to the shortest-diagonal metric could have helped** - the defect is
  structural, not a scoring problem. The existing merge walk was not the answer either: it
  pairs chains by u, so a dense breakout curve against a coarse ring is fanned from one
  far vertex and inverts where the curve turns back (measured uv cross -5.9e-4). The fix
  is a **slab sweep** - split each hole at its extreme-u vertices into two u-monotone
  chains, cutting the band into u-monotone slabs for the textbook stack sweep, sharing cut
  halves verbatim so watertightness is by index and no vertex is invented, with a global
  uv-area identity as the closing guard. It returns null and defers to the ear clipper
  whenever it cannot prove the decomposition, so it cannot be worse than what it replaces.
- **Interior rows: the base triangulation carries the curvature, refinement carries the
  residue.** A trimmed band spanning many natural steps in a curved cross parameter used
  to be swept boundary-to-boundary — every base facet spanned the band's whole height —
  and midpoint bisection then had to invent all the interior structure, inverting halves
  wherever the surface midpoint of a long chord left the chord (`Sphere(10) −
  Cylinder(3, 40)`: 43 948 facets, 12 folds, worst −0.2022, refusing outright at high
  density; a hand-built spherical band: base 94 facets at 0.99954 wrecked to 2 784 at
  0.1998 — refinement degrading a base that was already good is what proved a better
  bisection RULE would treat the symptom). The landed design puts the natural grid's own
  sample rows into the BASE: one constant-cross path per inside stretch of each natural
  level (crossings in key order alternate enter/leave, so levels thread between scallops
  and hole rims), anchored on existing boundary vertices — a boundary vertex is shared
  edge geometry and must never be invented — with interior vertices at the natural key
  values; each path cuts its piece in two and the resulting sub-bands (≤ ~1.5 steps) go
  through the same monotone stack sweep, which between two full rows emits exactly the
  untrimmed grid's quads. Winding bands get full-period rows with closure duplicates;
  their chain-adjacent strips get partial rows with the strip's seam chords PRE-SPLIT at
  the levels — legal because a seam chord is an unrolling artifact internal to the face,
  so both sides get bit-identical twins and still weld. Three supporting rules, each
  paid for: **`Refine`'s step metric is per-axis max-norm** (a grid cell's own diagonal
  is one step in each axis; under a 2-norm refinement bisects the very grid that defines
  the quality bar), **pole-fan edges are refinement-exempt** (the pole's u is arbitrary,
  so a fan edge's u-span is an artifact — refining a *flat* disk's fan folded it 467
  times), and **every rowed path closes with a uv-area identity and falls back to the
  rowless sweep**, so it can never be worse than what it replaces. Result: drilled
  sphere 3 244 facets / 0 folds / 0.9994 with volume ratios 4.35 / 5.08, and refinement
  measured IDLE on 16 of 19 corpus members' trimmed faces — the "refinement is not a
  convergence mechanism" lesson, now enforced structurally rather than remembered.
  as a visibly crumpled fan and had **zero** strictly-inverted triangles - before *and*
  after. What was wrong was a worst facet-vs-surface dot of 0.0198, an 88.9 degree sliver,
  which any inversion count calls clean. Nor is a count a convergence test: volume excess
  over the analytic value ran 61.19 / 18.60 / 13.40 / 11.25 at 32/64/128/256 segments per
  circle - ratios 3.29, then **1.39, 1.19** - stalling near 11 and never converging, where
  after the fix it runs 76.20 / 21.49 / 5.97 / 1.82 at ratios 3.55 / 3.60 / 3.27, the
  quadratic convergence the strip path is supposed to give. Independent check: the
  implicit route (Surface Nets at resolution 256) lands 3.79 *below* the same analytic
  value, so the two representations bracket it. This is the companion to the
  centroid-versus-vertex rule: pick a metric that can *see* the defect, then prove it
  converges.
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
- **Polyhedral offset is exact, and so is curved offset — the corner machinery is
  `SurfaceOffset` + `SurfaceCorner` + `CarrierBody`, built once for three consumers.**
  An offset plane is a plane and an offset vertex is a three-plane intersection, so
  shelling a polyhedron is closed-form; a cylinder's, cone's or revolve's offset surface
  is equally analytic, and the *corners* that blocked it are now solved. Three decisions
  are worth recording.

  **A corner POINT is never approximate, whatever the carriers are.** It is the root of
  a small square system, and Newton on exact carriers converges to machine precision, so
  the tiers differ only in how the residual and its gradient are obtained — three planes
  by Cramer, anything with a closed-form implicit distance by Newton on that. The
  residual is always returned, so a caller refuses a solve rather than building a solid
  around a point that is not on its own faces. Implicit rather than closest-point,
  because "be on all of these" needs a residual and a gradient and not a foot — and
  because a surface's `TryProjectPoint` answers "is this point ON me", which is false for
  every iterate of a corner solve.

  **The Newton step is MINIMUM-NORM, and that one choice deletes every special case.**
  The carriers routinely do not pin a point: a seam vertex has two incident faces because
  a closed rim starts and ends there, and a tangent junction has three faces of which two
  are the same carrier (a pipe elbow's profile circle split into two arcs offsets to two
  halves of ONE circle; a sphere's hemispheres to one sphere). Confining the step to the
  span of the carriers' normals makes the answer "the nearest point of their common
  locus", which reduces to the unique intersection when there is one — and reproduces
  every hand-written rule that would otherwise be needed: a cylinder's seam stays in its
  seam half-plane, an elbow's junction keeps its angle on the offset profile circle, a
  sphere's equator moves radially. Each of those was written down first, as a synthetic
  constraint plane, before the general rule replaced all of them.

  **A corner CURVE is where the exactness brand bites, and the decision is: refuse by
  default, opt in and be labelled otherwise.** Analytic pairs give a conic trimmed to its
  corners. Everything else is the marching tracer, whose chordal output carries a fixed
  sampling floor that no tessellation density can lower — the identical argument that
  refuses arc-rim sharp corners below. So `CornerPolicy.ExactOnly` is the default and no
  kernel consumer passes anything else; `AllowTraced` exists, reports
  `CornerCurve.Deviation`, and labels its `Tier`. The reason it exists at all rather than
  being deleted is that a *deviation you can read* is a different thing from a silent
  approximation, and a caller outside the solid-modelling path (a drawing view, a
  toolpath) may legitimately want it. Even then the curve is made to TERMINATE exactly:
  its ends are replaced by the solved corner points, so the chord error stays strictly
  interior and never reaches a vertex, which is the one place the weld tier is absolute.

  Two construction rules the consumers pay for. Curved rims are **constructed and then
  verified**, never intersected and trusted: a concentric circle about the original's own
  axis and phase lands on the offset carriers' grids by construction, while the same
  circle recovered from a surface–surface intersection is right where the analytic tier
  reaches and silently chordal where it does not. Every sample is then measured against
  both carriers — which is exactly what refuses a *sealed* pipe elbow, whose moved cap
  planes cut the offset torus in a quartic rather than a circle. And a **domain-driven
  carrier must be re-trimmed to its new loops**, because an inward offset moves a cone's
  rims axially as well as radially; the generator parameter for that trim is solved
  against the loop points' own axial coordinate on the exact carrier, never by
  projection (the vCut lesson — a 1e-7 projection error times the slope shifts the ring
  past the weld tier).
- **NURBS curves have exact analytic derivatives** (`DerivativeAt`/`SecondDerivativeAt`:
  The NURBS Book A2.3 basis derivatives + the generalized rational quotient rule, so
  non-unit weights are handled; `TangentAt` is overridden, leaving finite differences
  only for curves without an exact override). **`NurbsCurve.InterpolatePoints`** fits a
  cubic through points: chord-length parameterization, natural (zero-C″) ends, and a
  genuinely tridiagonal Thomas solve for the open case (collocation at a
  multiplicity-1 knot leaves exactly 3 nonzero basis functions); the closed case uses a
  periodic knot vector with wrapped control points, giving a C2 seam by construction.
  Two points degrade to a degree-1 chord.

### Where the 2D curve family meets the sketch and the profile

There are three vocabularies for the same planar geometry, and they exist for different
reasons: `Curve2d` (exact analytic curves — the biarc fitter's currency), `Sketch`
(a validated closed loop with a fluent builder — the user's vocabulary), and `Region2d`
(polygons with holes — the arrangement-based boolean's currency, deliberately flattened).
The bridges between them are chosen to be as small as possible, because every extra door is
another place for closure, winding and degeneracy rules to be answered differently:

- **`SketchSegment.ToCurve2d` / `Sketch.ToCurves`** — the way OUT of the sketch vocabulary.
  It is a re-expression, not a conversion: a `LineSeg` IS a `Line2d`, a cubic segment IS a
  cubic `BezierCurve2d`, and an `ArcSeg`'s signed sweep IS an `Arc2d`'s. That last one is the
  reason the 2D family made sweeps signed in the first place — orientation crosses the bridge
  as data rather than as a flag to be re-derived on the far side.
- **`Sketch.FromCurves`** — the way back IN. It maps the three shapes a sketch can hold and
  REFUSES anything else by name (a general `NurbsCurve2d`, a degree-4 Bézier). A quadratic
  Bézier is elevated to the equivalent cubic, which is a closed form rather than an
  approximation. Crucially it then hands the segments to the ordinary `Sketch` constructor,
  so weld-tier closure, relative-degeneracy area and winding normalization are validated in
  exactly one place. There is no 2D-curve-side copy of those rules; a second copy would be a
  second answer.
- **`Curve2d.ToCurve3d(plane)` / `Profile.FromCurves`** — the way into topology. `ToCurve3d`
  is ABSTRACT on `Curve2d`, for the same reason the derivatives are: every conversion is
  exact, and there must be no sampled fallback for a new 2D type to inherit by accident.
  Arcs lift the way sketch arcs already did (a full turn becomes a `Circle3d` on the arc's
  own start radial; anything less becomes a `CurveSegment` over a circle on the placement
  frame's axes), so `BrepQueries` classification, rim features and cylinder promotion see the
  `Underlying` circle they always have. `Profile.FromCurves` likewise just calls the ordinary
  `Profile` constructor.

The result is a lossless route from a drawn sketch to an exact analytic profile that never
touches `Region2d` — which matters because going through a region is the one deliberately
lossy step in the whole 2D pipeline.

### The curved 2D tier — and why it is a PARALLEL type

The lossy step above is now optional. `CurvedEdge2d` / `CurvedRegion2d` /
`CurvedArrangement2d` / `CurvedRegion2dBoolean` / `CurvedRegion2dOffset` (all in
`EngrCAD.Core.Geometry2`) carry **lines and circular arcs through the arrangement
unflattened**, and `Curve2d.TryToCurvedEdge` / `Curve2d.FromCurvedEdge`,
`Profile.FromCurvedRegion`, `Sketch.ToCurvedRegions` / `Sketch.FromCurvedRegion` are the
bridges. A fourth vocabulary is a real cost, so the reasoning for each decision is worth
recording.

**Parallel, not an extension of `Arrangement2d`.** The straight arrangement is
boolean-critical — `Region2dBoolean`, `Region2dOffset`, `Shape.Section`,
`Shape.Silhouette`, `Sketch.Offset`, and every rendered docs PNG sit on it — and teaching it
curves means changing three of its load-bearing rules at once: the vertex fan comparator
(positions → tangents), edge identity (a vertex pair → a vertex pair *plus a carrier*, since
two points on one circle are joined by two arcs and by a chord), and the area rule. This is
the same call §5 makes for `FaceSplitter`: **do not unify boolean-critical machinery**. The
curved type shares the exact predicates and the algorithms' shape; the straight path's diff
is empty, and `Region2dGoldenTests` pins its output bit for bit going forward.

**The tier stops at arcs, and that is a completeness result rather than a budget.** The cell
walk orders edges at a node by departure TANGENT, tie-broken by departure CURVATURE — which
follows from p(s) = v + s·d + ½s²κ·n̂, so two edges leaving along the same d separate at
second order and the larger signed curvature sits further counter-clockwise. For lines and
circles, agreeing in both means sharing a carrier: a line and a circle never osculate, and
two circles that osculate are one circle. The tie-break is therefore *complete* — the walk
never guesses, and the comparator refuses by name if it is ever handed a second-order tie
between different carriers. A third shape destroys that property: two Béziers can agree to
second order and separate only in the third derivative, so a sound rule would need a jet of
unbounded order and a subdivision tolerance underneath it. Béziers are consequently
flattened at the entry points and the flattening is stated in the API contract rather than
hidden.

**The tangency policy is SNAP, not refuse.** Every curve decision is posed so its threshold
is a LENGTH, and that length is the arrangement's own vertex snap tolerance — a line is
tangent to a circle when the centre's distance from it differs from the radius by less than
it, two circles when their centre distance differs from r₀ + r₁ or |r₀ − r₁| by less than
it, a point is on an edge when its distance is under it. There is no second epsilon in the
tier. Inside that band the answer reported is ONE touch point. Refusing would be useless (a
sketch is full of tangencies) and dropping the contact loses a node the tracing needs, while
two crossings a nanometre apart is a degenerate sliver cell whose classification is decided
by rounding. Snapping is area-neutral to O(τ^1.5) and always yields a valid arrangement,
**because a tangential contact is representable here**: the two edges leave the node with
equal tangents and different curvature, which the fan can rank. That is the same property
that makes the tier complete, used a second time.

**And the tangency policy has a second half, learned the hard way.** Snapping produces the
node; ordering the edges *at* it is a separate problem, and the first implementation got it
confidently wrong. The fan comparator sorts by the exact `Orient2dSign` of the two departure
directions — but where two edges are tangent, those directions differ only by arithmetic
noise, so the exact predicate decides a quantity that carries no information. A disc tangent
to a plate's straight edge from outside gave the arc a departure of (−1.22e-16, −1), whose x
sign is nothing but the error in `sin(π)`; that put it on the wrong side of the plate's
exactly vertical edge, the tightest-turn walk closed **no face at all**, and the union came
back EMPTY. `OrderTangentialRuns` re-orders each cyclic run of tangentially tied departures
by curvature afterwards, and **the tie band is derived rather than chosen**: a vertex may sit
up to the snap tolerance from the true tangency point, and displacing a point by δ along a
circle of radius r rotates its radial — hence its tangent — by δ/r = δ·|κ|, so the band is
`snap·max(|κ₁|, |κ₂|)` plus a few-ulp floor. That floor is all that remains for two straight
edges, so genuinely distinct line directions are still decided exactly and a near-parallel
pair keeps the orientation sign's answer. This is §5's `DepartureAngle` note in new clothes:
**Shewchuk exactness is exactness about the coordinates you hand it**, and a tangent computed
at a tangency is not one of them.

**What changed in the classification proof.** The straight interior sample takes the
boundary-edge midpoint with the greatest clearance and pushes half of it along the inward
normal; the disk of that clearance meets no other edge, so the pushed point is interior. For
arcs the push must also be capped by the edge's own CURVATURE RADIUS — otherwise a small
circular hole inside a large cell sends the sample past the circle's centre and out the far
side. With the cap, the pushed point sits at |r ∓ s| from the centre with 0 < s < r, so it
is off its own carrier by exactly s and off every other edge by more: the same proof, with
the straight case being the infinite-radius specialization. Classification then uses the
epsilon-free `ParityInside` rather than the closed-set `Contains`, whose on-boundary band
would answer "inside both" for a cell thinner than the weld tolerance.

**What it buys, measured.** A disc's area is πr² rather than an inscribed polygon's; two
overlapping discs' union, intersection and difference match the analytic lens formulas to
1e-10; an offset's round joins are exact sectors, which *retires* the inscribed-arc contract
instead of honouring it; and a 40×20 plate with a Ø12 bore extruded 5 mm comes out within
1e-6 relative of (800 − 36π)·5, against 3.6e-5 through the default-flattened route — an
error that is a FLOOR no tessellation density can lower, because it is baked into the
profile before any solid exists.

### Simplicity validation and simplification

Two passes that look similar and are opposites. `Region2dValidation` REFUSES loops that are
not simple: a self-crossing loop's interior depends on which fill rule you apply, so its
area, containment and every boolean disagree silently. `PolylineSimplify` (Douglas–Peucker,
2D and 3D) deliberately CREATES that risk in exchange for fewer points, which is why nothing
in the kernel simplifies implicitly and why simplified loops handed to `Region2d` get the
refusal for free. The tolerance in the first is not a tolerance at all — the decision is
exact `Orient2dSign` — while the tolerance in the second is absolute and in model units,
because it is a deviation the caller chose to accept rather than a degeneracy guard.

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

#### Assessment: should `FaceSplitter`'s tracing run on `Arrangement2d`? — **No**

The backlog has long carried "route `FaceSplitter`'s planar tracing through
`Arrangement2d` (deferred — boolean-critical)", on the reasonable-looking grounds that
`Arrangement2d` does the same dance with adaptive-exact predicates instead of a
floating-point angular guard. Assessed properly, the answer is no, and the reason is worth
recording so it is not re-opened on the same intuition.

**What would actually change.** Only the *tracing* step could move — steps 1–3 above
cannot. `Arrangement2d` intersects straight **segments in the plane**; `FaceSplitter`
intersects **curves on a surface**, and it deliberately does not do that in parameter
space: crossings are refined by 3D curve–curve Gauss–Newton because projected-uv Newton
fails near bounded domain edges, and tracer polylines are on-surface only at their
vertices, so a uv-space crossing is off-surface by the sampling sagitta (~1e-4 at display
density — the exact defect that made the cross-drilled bore silently return an unsplit
band). Feeding flattened polylines to the arrangement would replace the hardest-won part
of the pipeline with a flattened approximation.

**And the exactness would land on inexact inputs.** For tracing, the thing `Arrangement2d`
offers is `SortedIncidentEdges` — exact counter-clockwise order of the edges at a node via
`Orient2d`. But the quantity being ordered here is the *tangent of a curve*, which the
arrangement cannot represent: to use it you would hand it the chord to a point 2% along
the edge, which is precisely what `DepartureAngle` already computes. Shewchuk's predicates
make decisions exact **on the coordinates given**; when those coordinates are a 2%-chord
stand-in for a tangent, exactness buys nothing that the existing `1e-12` turn guard is
losing. (The angular *order* itself is safe under the uv anisotropy, incidentally: a
tightest-turn rule only needs the cyclic order of directions around a node, which any
orientation-preserving linear map preserves — so the anisotropic parameterization is not
the fragility here.)

**The regression surface is the rest of `TraceFaces`, and it has no counterpart.** The
walk is a minority of that method. The rest is: periodic **u wrapping** (loops whose pulled
area is meaningless are band boundaries, paired bottom-to-top by v, with unpaired ones
bounding pole-capped bands); **reversed faces**, where the handedness of the tightest turn
flips; and the reconstruction of **topology** — traced loops become `BrepLoop`s of
`BrepCoedge`s carrying `SameSense` and the original exact curves, which is what keeps
tessellation and downstream booleans on exact geometry. `Arrangement2d` models a
non-periodic plane and returns cells as polygons of 2D points; every one of those would
have to be layered back on top of it, inside the code path that carries the entire B-Rep
boolean regression surface.

**The smaller change that IS worth evaluating** is orthogonal to the arrangement: replace
the finite-difference `DepartureAngle`/`ArrivalAngle` with **exact analytic tangents**.
Every analytic curve now overrides `Curve3d.DerivativeAt`, so the 2% chord could become a
true tangent pulled back through the surface's Jacobian — removing the approximation the
`1e-12` guard exists to tolerate, without touching the graph, the periodicity or the
topology. It needs surface partial derivatives at the node, and a decision about what to do
where the Jacobian is singular (poles), which is why it is a work item rather than a patch.

Note that the 2D sketch path already gets the benefit this item was reaching for:
`Region2dBoolean` runs on `Arrangement2d`. It is the B-Rep *face* path that structurally
cannot, because its arrangement is not planar, not straight-edged, and not untopological.

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
cylinders** inside `SurfaceIntersection` — only when the generator sweeps a WHOLE turn, since
an extruded ARC merely *lies on* that cylinder and promoting it fabricates surface the face
does not carry — so drilled bores get exact circles rather than marched polylines; and a
closed curve whose pullback drifts a full period (a bore circle
on a band) is recognized as non-contractible and handled by `SplitBandByWrapCurve`,
which cuts the band into two bands with exactly reconstructed sub-surfaces.

##### The analytic family of a helical band

A thread's bands used to have exactly one exact intersection partner, the plane
perpendicular to their axis, and everything else fell to the marching tracer. That reading
was too narrow by three cases, and the derivation that widens it is two lines.

Write a `HelicalSurface` as r = r₀ + dr·v, z = z₀ + dz·v + rate·u (θ = u), and a **coaxial**
carrier whose (radius, axial) profile is a straight line as r = a + b·z. Substituting,

  v·(dr − b·dz) = (a + b·z₀ − r₀) + b·rate·u,

so **v is linear in u** — and therefore so are the radius and the axial coordinate. The cut
is a *conical spiral*: angle as parameter, radius and height each affine in it. `SpiralArc3d`
is now that shape, and the four cases are members of it rather than separate types:

- a **plane ⊥ the axis** (b-in-z = 0) leaves the axial rate zero — the planar cap cuts
  `MakeThreadedRod` has always built;
- a **coaxial cone** is the general form — a thread's 45° end chamfer;
- a **coaxial cylinder** (b = 0) makes v *constant*, so the cut is one complete iso-v
  helix — the runout-diameter case;
- **parallel profiles** (dr = b·dz) never cross transversally, and a tangential contact is
  not reported here by contract.

Two consequences worth stating. First, an end chamfer needs no traced curve at all, so the
`CornerPolicy.ExactOnly`/`AllowTraced` question that governs curved corners simply does not
arise for it. Second, the *non*-coaxial pairs — a cross-hole, a tilted face — are genuinely
transcendental and stay with the tracer, which at thread scale under-seeds them: an M8 rod's
crest flat is a 13-turn band 0.16 mm tall, and the (u, v) seed grid returns one branch of
five with every branch stopping short of the rails. That is a seeding-density problem, not a
trimming one, and it is filed as such.

##### A refusal that named the wrong stage

`SplitBandByWrapCurve`'s non-planar branch — a cut whose v *varies*, which is what a
cross-drill or a tilted plane leaves — used to refuse a plain `CylinderSurface` by name,
and the message named a missing capability: "the sub-bands would need trimmed cylindrical
tessellation with wrapping loops". The evidence for it was strong and, read literally,
correct: lifting the refusal made the split succeed and produced `Directed edge appears
twice` from the mesh builder three stages later, so the refusal was reinstated and the
capability filed.

**Both halves of the diagnosis were wrong.** The split was right, and the trimmed path
already pairs wrapping loops by pulled-back u. The defect was in `BRepTessellator`'s
ROUTING: a plain cylinder has no parameter grid, so its band is tessellated by pairing
ring sample j to ring sample j, and the gate for that path asked only how many loops the
face had. That is not the path's precondition. **The precondition is that the two loop
polylines sample the same azimuths in the same order** — true of two natural rings (both
circles on the cylinder's own frame at identical parameters, radial parts equal to a few
ulps), false of two independently traced cuts, whose phases have no relation at all.
Pairing those by index folds the band: measured, 18 of the tool band's 40 quads faced
inward at a worst facet-vs-surface agreement of −0.0000, and the duplicated directed edge
was that fold reaching the welder.

Two things worth keeping. **A tier's gate should be its own correctness condition, checked,
not a proxy for it** — `IsRingPairedBand` compares the paired samples and hands everything
else to the trimmed path, which is both the fix and the honest statement of when index
pairing is valid. And **"the failure moved downstream when I lifted the guard" is evidence
that the guard is load-bearing, not evidence about what it is guarding against**; the
recorded lesson at the time ("generalizing a refusal can make failure worse") was true of
the experiment and false as a conclusion, because the new failure was a *different* bug
that the refusal had been hiding.

A third mechanism exists for the curves the tracer DOES have to produce. **The tracer breaks
its step only after the corrector's parameters leave the domain**, so a traced curve always
stops up to one march step short of a bounded surface's edge. Where that edge also bounds the
face being split, the curve crosses nothing at all: face splitting finds zero crossings, the
face is whole-classified, and the result cracks along the whole boundary. `SnapTracerEnds`
therefore extends each traced polyline onto the exact solution of E(t) = S(u, v) — its
boundary edge against the other solid's carrier, a well-posed 3×3 Newton system seeded from
the polyline's own last vertex, which already lies on S so only t moves. **It runs once, on
the single curve object both faces share**, which is what makes the two solids agree: snapping
per face during splitting would give them endpoints a sagitta apart and open a pinhole at
every crossing. This closed booleans that cross a whole-solid fillet's bands, and — a family
nobody was aiming at — cuts that break out through a face boundary part-way, such as a bore
swallowing a rounded rectangle's corner.

#### Coincident (flush) planar surface

Transversal intersection is not the only way two solids can meet: they can *share*
boundary instead of crossing it. Flush embossing, stacked plates, blocks butted together
and a pocket whose floor is the host's own face are all everyday inputs, and step (4)
above simply cannot decide them — a fragment lying on the other solid's boundary reads
distance ~0 from its SDF, so the sign is rounding. It is the same hole the mesh boolean
found where the winding number is exactly ½, and the answer is deliberately the same one,
so the two engines cannot disagree about what a flush mate means.

**Classification is by normal agreement, and the surviving copy is always the first
solid's.** With outward normals AGREEING both solids lie on the same side, so the shared
surface bounds the union and the intersection (one copy of it) and vanishes from the
difference — locally A minus B removes all of A's material there. With normals OPPOSING
the solids mate back to back: union and intersection bury the surface inside the result,
while the difference leaves the first solid untouched and keeps its copy. Both solids
cover the region, so exactly one copy can ever survive; **choosing A's is an asymmetry on
purpose**. B's copy is redundant when the normals agree and back-to-front when they
oppose, so it is never the better choice; and a difference reverses B's faces anyway, so
keeping ITS copy would also be geometrically right — but keeping A's needs no special
case and leaves the surviving patch's edges the ones A's own neighbours already
reference, which is what lets `SealSeams` pair them.

**Scope is coplanar PLANES, and the boundary is a policy, not an oversight.** A plane is
the one carrier whose shared region this can decide without surface–surface
re-intersection: two coplanar faces overlap where their trims overlap, which is a 2D
question on a common parameterization. Coincident CURVED surface — a shaft in a bore of
its own diameter, a flange band tangent to a sheet — needs the shared region's rim
computed by re-intersecting the two trims on a curved carrier, the same missing machinery
that blocks curved shelling corners and general trihedral fillet patches. It is therefore
refused BY NAME, before any splitting, rather than approximated: `SurveyCoincidence`
recognizes coaxial equal-radius cylinders and says so, naming the axis and radius and
pointing at the working alternatives (a working clearance, or the implicit route).

Three supporting rules fall out, each gated on a shared plane existing so that a purely
transversal boolean is bit-for-bit unaffected. **A curve that never reaches a face's
interior must not split it** — when two solids mate, each neighbour face's own boundary
IS an intersection curve, and splitting a face along its own boundary is what the
arrangement tracer cannot close. **A face pair whose bounds meet in a single point is
dropped** — their carriers still cross in a full line, which is real geometry through a
contact that is not. **A shared plane disqualifies the disjoint fast path** — two stacked
plates of one footprint intersect only along their own boundary edges, so after the first
rule they look disjoint, and the fast path would hand them back as two touching shells,
which is exactly the fusion failure this tier exists to fix.

The rim itself has two sources, and needing the second is a genuine asymmetry in the
kernel rather than belt and braces. Usually the ordinary transversal path supplies it (a
`MakeBox` boss's wall is an unbounded `PlaneSurface`, and plane ∩ plane returns the rim
line), but a sketch extrusion's wall is a *bounded* planar patch and
`TryPlaneExtrudedSection` deliberately reports no section when the cutting plane is flush
with the generator's rim — splitting the wall there would only fabricate zero-extent
slivers. Embossed text is precisely that case. So a coplanar face also takes its
partner's OWN boundary curves as rim curves, skipped where an existing curve already
covers them. That is also the best available weld: the new edges ride the very geometry
the other solid references, so the seam pairs by construction rather than by tolerance.

v1 contract: transversal intersections, plus coincident PLANAR face pairs (coincident or
tangent curved surface is refused by name); the
input solids are consumed. Output is **topologically sealed** by
`TopologyEditor.SealSeams`: edge uses contributed by discarded fragments are pruned,
coincident vertices unify (edges have internally settable vertex references for this),
and each seam edge merges with its twin from the other solid — the twins match exactly
because both sides split their seams at the same mandatory break parameters. Difference
reverses B's kept faces *properly*: loops re-wound (order and senses) in addition to the
`IsReversed` normal flag, so seam edges are traversed oppositely by the faces meeting
there. Boolean results therefore pass `Validate()` and Euler–Poincaré with the correct
genus.

### The native B-Rep archive (`BrepArchive`, `.ecb`) — and why it is TEXT

STEP is the interchange format and will stay so; the native archive exists for the one
thing STEP structurally cannot do here, which is carry this kernel's own surface family.
`HelicalSurface` (every modelled thread), `LoftedSurface`, `SweptSurface`,
`OffsetCurve3d`, `SpiralArc3d` and `PhaseShiftedCurve` have no AP214 entity, so the STEP
writer either refuses them or samples them into a degree-1 approximation. A model
containing a modelled thread therefore has, until now, had no lossless file
representation at all.

**Text, not binary, and the reason is the testing culture rather than the geometry.** A
compact binary would win on size — an archive of a busy solid is a few hundred kilobytes
where a packed one would be tens — on files nobody has complained about. What it would
give up is the property this repo's whole verification approach rests on: things that
*diff*. Golden fingerprints in `Region2dGoldenTests`, byte-compared docs PNGs,
`BvhBuildOrderTests`' node-bit fingerprints and every "output is bit-identical" claim in
CLAUDE.md are the same technique — commit the artifact, compare it, and let a diff name
the regression. A committed corpus archive joins that toolkit directly; a binary one needs
a decoder written before anyone can look at it, and in practice that means nobody looks.
Exactness is not the trade-off it sounds like: .NET's round-trip `"R"` formatting is a
bijection on finite doubles, so a value written and read back is bit-identical, and the
tests assert the stronger property — **save → load → save is byte-for-byte a fixed
point** — across the whole corpus.

Structurally it is the STEP writer's entity model with none of the AP214 ceremony: a
numbered entity table, `#n` references, one entity per line, dependencies always defined
before use (the object graph is a DAG — nothing reachable from a surface leads back to an
edge), so reading is a single pass and a forward reference is a malformed file reported by
name.

**The entity table is keyed on REFERENCE identity, never on structural equality**, and
that is load-bearing rather than an optimization. `BrepEdge.IsClosedEdge` is literally
`ReferenceEquals(StartVertex, EndVertex)`, so two coincident vertices and one shared
vertex are *different solids*; a format that deduplicated by position could not tell them
apart and would silently change topology. The same rule gives the sharing for free — an
edge used by two faces is written once and referenced twice, and a seam curve shared by
two edges comes back as one object rather than two that are free to drift apart under any
later edit.

Two smaller decisions, both with the same shape as things already recorded here.
**Frames are rebuilt with `Frame3d.FromOrthonormal`**, the only factory that stores X and
Y verbatim and derives Z = X × Y; `FromXY`/`FromNormal`/`FromZX` all re-derive, would move
the axes by ulps, and the archive would stop being a fixed point — the `AxisRef`
never-re-normalize-a-unit-vector lesson, again. And **the version is refused by name**
rather than parsed hopefully, because a newer writer may have added entity forms and a
partial parse of a solid we cannot build is worse than a clear message.

Worth recording as a *measured* finding rather than an assumption: the two constructors
that re-normalize a stored direction (`RevolvedSurface.AxisDirection`,
`OffsetCurve3d.PlaneNormal`) turn out to be idempotent on their own output for every
corpus member, so the fixed-point property holds with no exact-construction back door.
That was not obvious in advance and is exactly why the fixed-point assertion exists rather
than a tolerance comparison — a tolerance would have hidden the question.

Scope: the archive is a **geometry** format, not a document format. It carries solids, not
scenes, poses, features or materials; the document envelope is a separate item (see the
OCAF assessment in `todo.md`), and smuggling scene structure into a B-Rep file would make
both harder to version.

### IGES: import only, and what "the useful subset" turned out to mean

The standing assessment in `todo.md` was that IGES is legacy-only, worth an importer and
never a writer. That held up, and the implementation added one thing worth recording: the
useful subset is decided by **which entities map onto surfaces the kernel already has**,
not by which entities are common. 118 ruled surface is a two-section `LoftedSurface`, 122
tabulated cylinder is an `ExtrudedSurface`, 120 surface of revolution is a
`RevolvedSurface` — each is a few lines because the target type exists. By the same test,
entity 186 (Manifold Solid B-Rep Object) and its 502/504/508/510/514 supporting entities
are **filed rather than built**: they are a second, parallel topology encoding inside the
same file format, and mapping them is the same size of job as the whole rest of the
reader — with the twist that a 186 file is the one case that would NOT need healing, so it
buys correctness we currently get from `ShapeHealing` anyway.

**The result is a face soup, and the design says so rather than hiding it.** IGES carries
no shared topology at all: every 144 trimmed surface owns its boundary curves, so two
faces meeting along an edge reference two coincident-but-distinct curves. The imported
shell therefore has edges used once, `Validate()` refuses it, and `IsFaceSoup` states
that as a return value. This is precisely the case `ShapeHealing` was built for (its own
doc comment names foreign STEP for the same reason), so the honest import pipeline is
three explicit steps — read, heal, wrap — and `Shape.From(path)` deliberately does not
learn `.igs`, because it would hand back geometry that fails at lowering.

Two decisions specific to a column-oriented format. **Record structure is validated up
front and refused by name**: column 73 is the section letter and 74-80 the sequence
number on *every* card, so a file without that shape is rejected before a parameter is
read — `StlReader`'s "run the exact size test before any content sniffing" rule, applied
to the first card-image reader in the codebase. And **Hollerith counts must be honoured
when splitting fields**, which is not a nicety: an author field containing the parameter
delimiter shifts every later Global parameter by one, and Global parameter 14 is the unit
flag, so the failure mode is a file that imports cleanly at the wrong scale. That has a
test.

One more, and it is the same shape as the STEP reader's closed-generator disambiguation:
**entity 104's conic type is classified from its own coefficients, not from its form
number.** The form field is routinely wrong in real files, the discriminant `B² − 4AC` is
exact arithmetic on data the file already gives, and a contradiction is reported as a
diagnostic with the coefficients believed. Reading the declaration is right when the
declaration is the only source (144's outer-vs-inner boundary); reading the geometry is
right when the geometry is independently checkable.

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
- **A document is its construction history; geometry with no recipe is a SNAPSHOT, named.**
  The saved-document envelope (`Document`, `DocumentFile.cs`) stores no exact geometry at
  all: a part backed by a `FeatureHistory` saves that history and regenerates on load, so
  the reloaded part is still parametric. The interesting decision is what to do with the
  parts that have no history — a raw `HalfEdgeMesh`, an imported `.stl`, an `Sdf`, a
  `Shape` graph built in code. Three options were on the table and only one is honest:
  drop them (silently loses bodies), pretend they are parametric (a lie the first edit
  exposes), or **embed the display mesh as an explicitly-labelled snapshot** —
  binary-exact base64, and `DocumentLoadResult.Snapshots` names every part that came back
  that way, so a host can say "these parts are not parametric" instead of a UI discovering
  it when a parameter refuses to change anything. Embedded rather than an external file
  reference, deliberately: a document that points at its neighbours is a *manifest*, and
  the reference breaks the first time the file is moved, renamed or emailed — with the
  failure surfacing as missing geometry rather than as a missing file. One file that
  reloads the model is the whole value of an envelope. (The case an external reference
  genuinely wins — a scan mesh large enough that inlining it is absurd — is filed rather
  than built; nothing in the repo produces such a part, and the resolver it needs is real
  design work that should follow a real need.)
- **The fixed point is the test that earns its keep.** `save → load → save` being
  byte-identical is not a tidiness property: it is the only check that catches a field
  written but never read, a default that reloads as a *different* default, or an ordering
  that is not purely a function of the model — all of which pass a volumes-and-poses
  comparison. It caught one such field immediately. A snapshot record wanted a `"source"`
  naming the geometry type it came from (`BoxShape`), which is genuinely useful to a human
  reading the file — and which *cannot* survive its own round trip, because a `BoxShape`
  snapshot reloads as a `HalfEdgeMesh` and the second save writes a different name. The
  rule that follows: **an informational field either round-trips or does not belong in the
  file.** The actionable half of that information lives in `DocumentLoadResult.Snapshots`,
  which is computed at load rather than stored. The honest scope of the claim is also
  worth stating: the fixed point holds for everything that round-trips, and a file
  carrying opaque records (a lambda-backed dimension, a `Shape`-graph boolean tool) is
  smaller the *second* time by exactly the records the load already warned about — then a
  fixed point from there. "A record was reported and dropped" and "the file is drifting"
  are different things, and the tests assert them separately.
- **Undo journals values, and the SERIALIZER is its test oracle.** `DocumentEdit` is
  `MeshChange` at document granularity: an edit captures whatever it is about to overwrite
  and restores that on revert, rather than recomputing what the previous state must have
  been. Two decisions are worth recording. First, **the oracle**: every undo test asserts
  that `Document.Save()` after the undo is byte-identical to the save before the edit,
  because a hand-written state comparison agrees with a broken revert exactly as happily as
  with a correct one — and the serializer covers list positions, occurrence names and the
  parameter values inside a feature history, which is precisely the surface a hand-written
  check forgets. That is what forced `Assembly.Insert` / `MateSet.Insert` /
  `Part.InsertAnnotation` to exist beside the `Remove`s: re-adding appends AND re-derives
  the occurrence name, so an undone removal would silently come back last and possibly
  renamed, which every field-by-field check would have passed. Second, **a refused edit is
  not history**: `Part.Regenerate` already keeps the previous complete body when a rebuild
  fails, but the bad parameter is still set, so an edit whose regeneration fails takes its
  own value back and rebuilds before throwing — and is neither pushed onto the stack nor
  allowed to discard the redo history, since the whole claim is that nothing happened.
  Regeneration caching then survives undo *by construction* rather than by extra
  machinery: the cache key is the parameter snapshot, so restoring the old value restores
  the old key and re-runs exactly the prefix a forward edit would.
- **The undo stack is session state, and that is why it stores edits rather than
  snapshots.** todo.md's OCAF assessment proposed "a document snapshot is a value, an edit
  produces a new one, and the viewer swaps scenes" — the hot-reload seam. Built out, that
  is the wrong granularity here: a `Scene` snapshot is not a value in any cheap sense (its
  parts cache display meshes, B-Rep lowerings, SDFs, feature edges, occlusion), so
  snapshotting per keystroke would either throw those caches away or share them into two
  documents that then diverge. An edit is a handful of captured doubles. Hot reload keeps
  its whole-scene swap because it genuinely rebuilds everything from source; interactive
  editing does not.
- **Topological naming: a tag names a SET, and the failure must be one-sided.** The
  selector story ("re-run the semantic query against the regenerated body") is the working
  answer and stays the default, but it has one structural blind spot: it can only say what
  a face *is*, so two identical bosses are indistinguishable to every query in the
  vocabulary. `Shape.Tag(name)` fills exactly that gap by saying where a face *came from* —
  the B-Rep lowering stamps the name onto every face of its child's solid
  (`BrepFace.Provenance`) and faces inherit it wherever one is derived from another
  (`BrepFace.DescendsFrom`). Three decisions carry the design.
  **(a) Set-valued, by construction.** A boolean can split one face into several, so "the
  face this step made" is not a well-formed request; `FaceSetRef.Tagged` therefore returns
  a set, and narrowing to one is an explicit claim (`FaceRef.One`/`Extreme`) that fails its
  cardinality contract loudly. A scheme that promised "the" face would have to guess which
  fragment, which is precisely how naming schemes come to misresolve silently.
  **(b) The failure direction is the whole safety argument.** Inheritance is implemented at
  the sites that derive a face from a parent; a site that builds a face from scratch, or an
  algorithm that rebuilds the solid wholesale, simply does not tag — so a query returns
  FEWER faces than the author expected, never a face from somewhere else. Landed: the
  boolean pipeline (untouched faces pass through by reference; every `FaceSplitter`
  fragment and every re-wound tool face inherits), `BrepSolid.Clone`, and therefore
  `Drill`, patterns, transforms and `Shape.From(solid)`. Not landed, and stated rather than
  hidden: `Draft`, `Shelling` and `FilletAllEdges` rebuild every face from scratch, and rim
  surgery rewrites the blended face and its trimmed neighbours — all four have a
  positional parent (`Shelling` already keeps a `Dictionary<BrepFace,int>` for exactly this
  reason), so threading provenance is mechanical rather than hard, and it is filed instead
  of done because the tests that pin the boundary are cheap and a half-propagated tag is
  worth less than a documented one. STEP carries no provenance either, which is a format
  limit rather than a choice.
  **(c) A tag is REFUSED, not sanitized, when the descriptor grammar cannot spell it.** The
  descriptor is the cache key and the serialized form, and it is parsed back through
  `RefLexer.ReadIdentifier` — so a tag containing a space or a comma cannot survive its own
  round trip. Sanitizing it (the rule an *opaque label* follows, where the marker is only
  ever read by a human) would turn `"boss top"` into a descriptor that resolves to nothing:
  a silent misresolve, which is the one outcome a naming scheme must not have. Refusing at
  the call site with a suggested spelling is the only version that cannot lie. The
  combinator `FaceSetRef.Within(scope)` was forced out by the first real use: a tag names a
  boss's cylinder AND its plane, while rim surgery wants a planar face, so composing the
  provenance query with a semantic one is the normal case rather than an advanced one.
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
- **Builder-style authoring: prototyped and declined (an honest no).** build123d has
  two APIs — algebra (`box - cylinder`, which `Shape` already is) and a builder
  (`with BuildPart() as p:` accumulating add/subtract/intersect) — and the parity
  survey asked whether the builder form earns a C# counterpart. It was prototyped
  against a real bracket (base + bosses in a loop − pocket − drilled holes) in three
  spellings, committed compilable as `BuilderPrototypeTests` in
  EngrCAD.Modeling.Tests. Verdict: **no new API**, for three reasons found by writing
  it rather than argued from taste. (1) Python's builder rests on context managers
  giving every nested call an ambient "active builder"; C# `using` gives no ambient
  anything, so a builder is either passed explicitly (a lambda, an indentation level,
  a second vocabulary for the same three verbs) or thread-static ambient state —
  which is todo.md's already-declined "implicit pending state". (2) C# already HAS
  accumulate-without-naming-intermediates: a mutable local with compound assignment
  (`bracket |= boss; bracket -= pocket;`) is the builder mode, natively, including
  loops and conditionals. (3) The one C#-specific trap a builder would fix — operator
  precedence, `a | b - c` parsing as `a | (b - c)` — is already fixed by the named
  `Union/Subtract/Intersect` methods, which chain left-to-right by construction. The
  transliteration also visibly breaks at non-boolean operations (`Drill`, rim
  features), which have no add/subtract mode and must sit outside the scope anyway.
- **Transforms bake into construction inputs, never into finished geometry.** The B-Rep
  lowering carries an accumulated matrix: boxes become extrusions of transformed
  profiles (shear included), cylinders extrude transformed `Circle3d`/`Ellipse3d` rims,
  spheres/tori take decomposed rigid+uniform-scale placement (`MakeSphere`/`MakeTorus`
  with center/axis), profiles wrap segments in `TransformedCurve`. This keeps rotated
  booleans exactly as accurate as axis-aligned ones. The implicit lowering decomposes
  the matrix into `Scale→Rotate→Translate` SDF operators (blend radii and offsets scale
  by the uniform factor); non-decomposable (sheared) subtrees bridge through a mesh.
- **`Shell(t)` and `Shell(t, openings)` are two operations, not one operation with two
  lowerings.** The SDF shell is the onion `|d| − t/2`: a symmetric skin straddling the
  surface, reaching t/2 *outside* the original body. The B-Rep shell
  (`Shelling.Shell`) hollows INWARD, keeping the outer surface exactly. Making one
  `Shape.Shell` pick per representation would give a design different walls in
  different lowerings — precisely the "representation-dependent results must be
  explained, not silent" failure — so the openings-taking overload is a distinct node
  (`BrepShellShape`) that bridges implicit through the exact shelled B-Rep, and the SDF
  shell's B-Rep-Impossible message names the exact overload as the way out. The same
  wiring pattern serves `Shape.Draft` and `Shape.RoundEdges`: classify Native under
  rigid + uniform scale with "constraints validate at lowering" (the rim-feature
  precedent — the selector needs the lowered solid, so shape constraints cannot be
  checked at construction), refuse shears with the metric quantity named (an angle, a
  wall, a radius), and let the kernel's own loud rejections surface verbatim.
- **A loft node is a graph LEAF whose inputs are placed profiles.** The sketch
  overloads bake each section's `SketchPlane` at construction and `LoftShape` stores
  `Profile`s, so lowering only wraps the accumulated graph transform around the section
  curves (`TransformedCurve`) — one baking rule instead of a per-section
  matrix-plus-sketch pair threaded through the compiler. B-Rep support is deliberately
  rigid + uniform scale even though the machinery could wrap any affine: the loft's
  mean-chord parameterization and least-twist alignment are METRIC choices, so lofting
  sheared sections skins slightly different in-between geometry than shearing the
  skin — the honest classification is Impossible, with the mesh route transforming the
  identity-placed tessellation (exact). `LoftAlong` (the evolution-law pipe shell)
  generates stations by scaling/twisting one sketch in `SweptSurface`'s own
  rotation-minimizing frames — the frames depend only on the path and the start x, so a
  law-free `LoftAlong` stations its sections on the same frames a sweep would use, and
  the generated profiles feed `Loft` unchanged rather than growing a second skinning
  path.
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

  **Drill points measure depth to the SHOULDER, not to the tip.** `WithTipAngle` adds
  the cone a real twist drill leaves, and the deepest *full-diameter* point stays at
  `depth` with the point reaching `(diameter / 2) / tan(angle / 2)` further. This is the
  drawing convention (ASME Y14.5 / ISO 129 dimension a blind hole excluding its point)
  and it is chosen for a stronger reason than convention: it makes the feature **strictly
  additive**. The same `depth` removes the same cylinder with or without a tip, plus the
  cone, so adding a point can never silently shorten an existing hole — which
  depth-to-tip would have done to every design that adopted it. The cost is that a blind
  depth which cleared the far face may not once the point is there, so `TipLength` is
  public and the docs say to check it. The default stays flat: not a drilled bottom, but
  what a model wants for a through hole or a reamed feature, and what every existing
  design already has.

  **Tool interference is validated by convexity, not by a boolean.** Two cutting tools
  that overlap or touch are degenerate boolean input, and the failure otherwise surfaces
  deep inside tessellation rather than at the call that created it. Within one plane the
  surface circles are coplanar, so centre distance against summed radii is the exact
  test; across planes the tools are solids of revolution, and each is covered by bounding
  cylinders about its axis. A pair is cleared by *either* of two sufficient conditions —
  the distance between the axis SEGMENTS exceeding the summed radii, or a separating axis
  (a finite solid cylinder is convex, and its support extent along a unit **d** is exactly
  `|n·d|·halfLength + r·√(1 − (n·d)²)`). **Neither subsumes the other**, which is the
  design point: segment distance settles skew and offset-parallel layouts in one test,
  but two COLLINEAR tools bored towards each other from opposite faces have axis segments
  at zero radial distance however much web is left between them, so only the axial
  projection separates those. Refinement, when the whole-tool bound is ambiguous, runs the
  same test slab by slab over the tool's silhouette breakpoints; it is **sound in the
  accept direction at any subdivision** and exact wherever a slab's radius is constant,
  which is all of a simple or counterbored tool. The residual error is therefore a
  conservative refusal in a near-tangent band — precisely the configuration a boolean
  cannot survive anyway. An exact solid intersection was rejected as the wrong shape of
  answer: it costs a full boolean to decide a question whose useful answer is "clearly
  apart" almost always.
- **Thread handedness is arithmetic, and `Mirror` is the same fact read backwards.**
  A left-hand thread shares every diameter with its right-hand twin — handedness is not a
  different thread — so it is carried as a flag on `ThreadSpec` rather than a second
  catalogue, and each representation spends one sign on it: `ThreadSdf`'s helical phase
  reads `z − h·P·θ/2π`, and `MakeThreadedRod` takes a signed axial rate. Because every
  formula in the factory is written in the band's own phase *u* with
  `z = z_generator + rate·u`, negating the rate makes *u* descend as the rod ascends and
  the rest follows mechanically: a rail's helix anchors on the top cap (`Helix3d`'s domain
  always starts at its own frame's plane, so a descending rail must start there), the
  `u = min` / `u = max` edges of a band swap which cap they are, and both cap loops chain
  the other way. Counts, Euler and the Pappus volume are untouched.

  **The trap worth recording: a left-hand rod is NOT a right-hand rod on some other
  frame.** Every right-handed frame is a rotation of every other, so no choice of pose can
  flip handedness — it *has* to enter the formulas. The near-miss that looks like it works
  is flipping two axes, which is a half-turn, not a reflection.

  That same identity, used backwards, is what makes `Mirror(thread)` exact rather than
  refused. A reflection across a plane *containing* the axis maps phase θ to −θ, which is
  precisely what negating the rate does — so writing a mirrored placement as
  `m = (m·FlipY)·FlipY` leaves a proper similarity placing a rod of the opposite
  handedness, and `Mirror(Mirror(x))` returns right-handed by construction. `FlipY`
  rather than the `FlipZ` the implicit path uses: `FlipZ` reverses the rod's own axis,
  which would move the caps and reverse the profile's axial order, where `FlipY` leaves
  both alone; any two reflections differ by a rotation, so choosing the convenient one is
  free. The refusal that remains is a genuinely different one — a sheared or
  non-uniformly scaled placement cannot re-place a helix at all.
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
- **Typed geometry inputs** (`GeometryRefs.cs`) give those selector queries a
  vocabulary, because "this feature needs a plane" had no way to be *said*. Five types —
  `PlaneRef` → `SketchPlane`, `FaceRef` → one face, `FaceSetRef`, `EdgeSetRef`,
  `AxisRef` → `Ray3d` — each carry how to find the geometry (named `BrepQueries`
  queries, nesting; an explicit frame as the escape hatch; a lambda as the last resort),
  and each carries **cardinality in the type**, which is the thing none of the five
  incumbent selector shapes could express. Three design decisions:
  - **The descriptor is the cache key is the serialized form.** Each reference renders
    as one canonical parseable term (`topPlane`, `planar([0,0,1])`,
    `extreme(planar([0,0,1]),[0,0,1])`) and `ToString` returns it, so `FormatValue`
    picks it up for the regeneration snapshot with no special case, and JSON
    round-tripping needs one line on each side of `FeatureHistory`'s closed type list.
    One string, three jobs, so they cannot disagree. Lambda-backed references print
    `opaque(label)` and decline to parse — a warning, matching `LoadParameters`' style —
    and stay sound as cache keys because the snapshot also carries instance identity and
    a fresh instance always re-runs. Two consequences worth writing down: the opaque
    label is sanitized to characters `System.Text.Json` will not escape (a quoted marker
    came back from a saved file as `'` noise), and an explicit axis keeps an
    ALREADY-unit direction verbatim instead of dividing again, because re-normalizing
    moves a unit vector by an ulp and the descriptor would stop being a fixed point.
  - **Timing is per-`Apply`, and nothing is memoized on the reference.** Resolutions
    cache on the `FeatureContext`, which is constructed fresh for every applied feature,
    so up-front validation and `Apply` share one query while an edited model still
    re-resolves from scratch. This is the deliberate opposite of `Mates`, which pins its
    references once at construction because a mate is a numerical constraint, not a
    query — the eager/lazy split is a property of the consumer, so it is chosen at the
    call site rather than legislated. `MateGeometry` now takes `FaceRef`/`AxisRef`
    overloads that make the eager choice explicit — same vocabulary, resolved once at
    construction, with the reference's `Descriptor` carried on the `MateRef` — which is
    what made mates serializable (`MateSet.SaveMates` writes the descriptor; loading
    re-resolves it eagerly, so construction time is load time; a lambda-backed selector
    saves its opaque marker and loads from pinned coordinates with a warning, matching
    `LoadParameters`' opaque contract).
  - **Validation resolves before `Apply`, all-or-nothing, naming the property.**
    `Feature.ValidateInputs` reflects over declared `GeometryRef` properties (no
    per-feature boilerplate) and `FeatureHistory` reports a resolution failure as
    `Failed` — "Plane: expected exactly one cylindrical face, found 0." — with the last
    good prefix intact, in `Filleting.RimFacesFor`'s naming style rather than the
    operation-named message the deferred rim selector used to give. The cost note is
    real and shaped the design: resolving forces `Lowered`, so a reference that needs no
    body (an explicit plane or axis) never triggers one, a feature declaring none pays
    nothing, and `[DeferredInput]` opts out inputs handed to the `Shape` graph's own
    late-resolved selectors — the rim features' face sets, where an early resolve would
    buy a whole extra B-Rep lowering per regeneration and learn nothing, since the
    selector runs against the compiler's own solid anyway.

  `FeatureContext.TopPlane` is now `PlaneRef.TopPlane` resolved against the context, so
  the hard-coded special case is gone while its world-axis-aligned `(0, 0, z)` origin —
  which drill coordinates depend on — is unchanged; `PlaneRef.OnTopFace` is the
  face-frame variant, making the open behaviour question an *option* instead of a fork
  in the road.
- **Mechanisms are the mate system, driven — the design calls worth recording.**
  *No second solver*: the whole layer is one internal seam, `AuxiliaryConstraint` —
  residual rows with ANALYTIC derivatives over evaluated mate-end world geometry,
  appended beside the mates so the rank/DOF machinery counts them like any rows.
  Screw pitch, gear/belt ratios, cam laws and drivers are all instances; with an
  empty extras list the solver is the old code path exactly. *The driver's angle
  encoding is the wrap-free pair* [c − cos τ, s − sin τ] (two rows, one constraint —
  the solver's usual redundant rotational style): a θ̂ − τ row would jump by 2π when
  an LM iterate crossed the wrap seam mid-solve and stall there, while cos/sin are
  continuous for any target and iterate; the branch is picked by proximity, which is
  exactly what continuation guarantees. *Unwrapped coordinates advance only on
  commit*: θ̂ = accumulated + wrap(measured − lastCommitted), state mutated only
  after a CONVERGED solve — inside one solve the residual is a continuous function
  of the poses however many iterations probe it, and a failed solve leaves state and
  frames alike. *Continuation is load-bearing* (seed from the previous converged
  pose, never the assembled one — a four-bar otherwise flips elbow mid-sweep), and
  it is the solver's write-nothing-on-failure contract that makes halving retries
  free. *A dead centre belongs to the driven VARIABLE, not the pose*: the same
  slider-crank pose that is singular driven from the slider is harmless driven from
  the crank, so the diagnosis probes the DRIVEN system — a zero-iteration rank probe
  with the threshold widened to 3% and compared against the sweep-start baseline
  (a sweep stalls NEAR a dead centre, where the Jacobian is almost, not exactly,
  deficient; the strict 1e-8 tier would never fire), and the widened number only
  names WHY a sweep already stopped — the hard stop never depends on it.
  *Accelerations are analytic because velocities were*: J·q̈ = −r̈₀ with r̈₀ from
  the composed rigid flow x(t) = Δ₁(t)(Δ₂(t)(x)) — centripetal terms per free chain
  link plus 2·ω_outer×(v_inner + ω_inner×r) cross terms — the same chain the
  Jacobian columns read, verified against the slider-crank closed form in velocity
  AND acceleration (finite differences would cap at 1e-8, the mate solver's own
  doctrine). *Interference skips jointed pairs by default* because a pin modeled at
  its bore's exact diameter interpenetrates once tessellated (polygon chords cross
  where exact surfaces touch) — a permanent false positive; and clash means
  TRANSVERSAL crossing only, so resting contact never reports. *A swept volume is a
  graph NODE* (`Shape.SweptOver`), not a union of transformed shapes, so `Explain`
  can say implicit-Native (child field lowered once, placed per pose) and B-Rep
  honestly Impossible instead of attempting N-way B-Rep booleans of rotated copies.
  *A swept volume's SAMPLING is a tolerance, not an inherited number*: unioning at the
  study's own frames means the scallop is whatever frame count the sweep happened to
  use, so `SweptVolume(path, maxTravel)` rigidly interpolates extra placements until no
  point of the part moves further than a stated length between consecutive ones — and
  travel is measured EXACTLY, as the largest displacement of the part's own
  bounding-box corners, rather than as a rotation angle times an assumed radius, so a
  body spinning about its centre costs few extra poses and one on the end of a long arm
  costs many. The recorded frames are all kept, so refinement can only add material.
  The interpolation itself is `MotionStudy.InterpolatePose`, shared with the animation
  layer's `MechanismTrack` across an assembly boundary, because two copies would be two
  answers to "where was the body halfway between these frames" and one of them would be
  the one users watch.
  *Several drivers at once is the general form, and the single-driver call is sugar over
  it*: a 2-DOF mechanism has no answer under one driver — the pose, and more obviously
  the RATES, are a family the solver would be picking a member of — so `SolveAt`,
  `Sweep` and `RatesAt` take lists, each driver contributing its own rows exactly as one
  does. Two calls worth recording. The same joint VARIABLE driven twice is refused by
  name: two rows demanding different values of one coordinate make the system
  inconsistent, and LM would report a residual rather than the modelling mistake it is
  (two drivers on the same JOINT driving different variables is the case the feature
  exists for and is fine). And a multi-driver sweep is a **straight line through driver
  space** — every driver runs its own From→To over one shared s — rather than a grid:
  one parameter means one step to halve, so the continuation, the dead-centre probe and
  the leave-the-last-good-pose contract are the single-driver ones unchanged instead of
  a second scheme.
  *A rack and pinion is a cam pair with a straight law*, not a fourth constraint class:
  `CamCoupling` already ties a slide to an UNWRAPPED spin through a law's exact slope
  and curvature, which is precisely Δz = r·Δθ with a constant slope — so a rack driven
  through three turns keeps advancing instead of resetting at every seam, for free. The
  dwell-rise-dwell law catalogue is where the engineering lives rather than the
  mathematics: the members differ in what happens where a rise meets a dwell (cycloidal
  and modified trapezoid end at zero acceleration and join C2; harmonic steps, the
  classic cam-noise source, and buys the lowest peak velocity), and the peak
  acceleration factors 2π / π²/2 / **8π/(2+π) = 4.8881** are asserted because ~22% under
  the cycloidal is the entire reason the compromise exists — the constant DERIVED by
  integrating the five-piece acceleration profile twice and requiring h(1) = 1, not
  transcribed. A rise **clamps outside its own span**, which is what lets `Segments`
  chain laws without knowing anything about them, and continuity across a joint stays
  the segments' business: smoothing it in the composer would hide the property the
  catalogue exists to let a designer choose.
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
- **Simulation results are DATA on a mesh, carried by the document** (`Results.cs` here,
  `MeshField`/`FieldRange` in EngrCAD.Mesh, `ColorMaps`/`FieldRendering`/`FieldLegend` in
  EngrCAD.Viewer.Core). Four decisions shape it. *The field type lives in the mesh
  engine, not here*, for the reason `StlWriter` does: a field is a property of a mesh,
  its exporter (`VtuWriter`) sits beside it, and a future solver can produce one without
  a reference on the whole modelling API — while `Part.Results`/`FieldDisplay` put the
  *choice* of what to draw in the document, so a script, a headless render and the
  browser client all show the same thing. *`FieldColorMap` follows `DisplayMode`'s
  precedent exactly*: the enum is a document-model choice here, the colour TABLES are in
  Viewer.Core beside the shaders, and neither half can drift because one is a choice and
  the other its only implementation. *Colour is a vertex attribute under the baked-AO
  rule* — `aFieldColor` at slot 3, a context constant when no buffer is attached, and a
  `uFieldColor` strength of 0 that makes `mix(uColor, vFieldColor, 0.0)` exactly
  `uColor`, so a part with no results renders **byte-identically** (the oracle is the
  docs suite: all 89 rendered PNGs unchanged across the shader change, which no unit
  test could have shown). *A deformed shape is GEOMETRY, not a pose* — so it cannot ride
  the matrices-only `SetInstancePoses` path the exploded view and the animation
  transport share, it re-uploads deliberately, it is kept off the animation path, and its
  facet normals are recomputed from the displaced positions (carrying the originals over
  would make the deformed shape look exactly like the original, which is the entire point
  of the plot). Two smaller rules earn their place: a zero-span range normalizes to
  **0.5**, because a constant field has no position to report and an extreme colour would
  read as a hot spot; and a merged VTU fills a part's missing array with **NaN**, VTK's
  own "no value", since dropping the array loses the result that exists and zeros show a
  fake safe region.

### Sheet metal (`SheetMetal.cs`, `SheetMetalFeatures.cs`, `BRep/SheetMetalSurgery.cs`)

The domain is large but its kernel demands are mostly things this kernel already had;
the genuinely new work is a **model**, not new surface types.

- **The declaration IS the model.** `SheetMetalBody` holds a base sketch, a
  `SheetMetalSpec` and an ordered tree of `EdgeFlange`s, and BOTH the folded solid and
  the flat pattern are derived from those same numbers. That is the whole reason the two
  cannot drift: there is no second description of the part to keep in step. It also
  decides where the API lives — a flange is an entry in a tree, so `EdgeFlangeFeature`
  refuses a non-sheet body by name rather than doing surgery on arbitrary geometry and
  leaving a part whose flat pattern is underivable.
- **One bend model, in one place.** `BA = θ·(R + K·T)` (bend allowance, the neutral
  axis's arc length) and `OSSB = (R + T)·tan(θ/2)` (outside setback) are static methods
  on `SheetMetalSpec`; bend deduction is `2·OSSB − BA`, derived rather than a third
  model. Everything else in the feature — flat lengths, tip positions, DXF bend lines —
  is those two numbers. K is stored per FLANGE with a per-body default and a
  `SheetMaterials` table transcribed and flagged verify-against-datasheet, exactly as
  `StandardHoles`' Trisert rows are.
- **The K-factor is deliberately absent from the geometry.** `SheetBendSection` — the
  cross-section the surgery builds from — carries thickness, radius and angle and no K,
  because K locates the neutral axis, which decides the developed LENGTH and nothing
  whatever about the folded shape. That separation is what turns the folded-versus-flat
  volume comparison into a real test rather than a tautology (below).
- **Bends are topology surgery, never booleans**, and the reason is the same one the
  hex-socket work hit: a bend meets both the parent sheet and the flange wall
  *tangentially* — cylinder and plane share a tangent plane along the entire bend line —
  which is precisely the coincident/tangent input the v1 boolean refuses. And there is
  nothing to compute anyway, since every face of a bend is a closed form. So the bend's
  two arc bands (exact `ExtrudedSurface`s over `NurbsCurve.Arc` generators, full-domain
  so they tessellate on the natural grid) and the flange's three planes are welded
  straight into the parent's loops, `Filleting`-style. A comment says so where a future
  reader would otherwise reach for a union.
- **Two rewiring cases, one cross-section.** A full-width flange REPLACES the wall's end
  edge in each neighbouring face's loop with the flange's cross-section chain; an inset
  one splits both rims in three (`TopologyEditor.SplitEdge` patches every using loop),
  splits the wall into two stubs, and closes the same chain against a new vertical edge
  as its own end cap. Both build the chain once. The full-width path has one non-obvious
  duty: the neighbour must be **re-surfaced as a `PlaneSurface`**, because the widened
  loop now reaches out past the flange's tip and would escape a domain-driven
  `ExtrudedSurface`'s parameter rectangle — the trim-the-neighbour rule from rim surgery,
  running the other way.
- **Every refusal fires before a single coedge moves**, which the rim features learned the
  hard way ("partial runs are rejected BEFORE any surgery — rim surgery rewrites loops in
  place, so a late failure would leave a half-edited solid"). `Locate` therefore checks
  more than it needs for its own job: that the wall descends exactly one sheet thickness
  at both ends, and — for a full-width flange — that the faces at both ends of the bend
  line are planar and square to it. Both were originally checked where they were USED, and
  both were then downstream of a mutation: the perpendicularity test sat inside the splice,
  which rewrites the Q0 neighbour's loop before it ever looks at Q1's, and the
  thickness test sat after the rim splits. Same defect, twice, from the same cause — a
  precondition written next to its consumer instead of next to the gate.
- **The unfold is bookkeeping.** Each node carries a rigid 2D frame in the blank and a
  3D frame on its own "top" face for the SAME local coordinates, both right-handed, and
  the recursion places a child from its parent's frame plus the bend section. The blank
  is then the base sketch with a detour spliced into each flanged segment — no 2D
  boolean, for the same reason there is no 3D one: the flange rectangle is exactly
  edge-adjacent, so the answer is known. Base-sketch holes carry through untouched
  because the flat pattern's coordinates ARE the base sketch's.
- **The oracle is an exact discrepancy, not an approximate agreement.** A bend's folded
  material is an annular sector, `θ·T·(R + T/2)` per unit width, while the blank spends
  `BA·T = θ·T·(R + K·T)`. So folded and flat volumes are **identical at K = 0.5** and
  differ by **`Σ width·θ·T²·(0.5 − K)`** everywhere else — measured to 8e-10 relative on
  a 6.1e3 volume, i.e. to the grade of the tessellate-then-Richardson mass properties
  themselves. A blanket "the volumes agree" would have been satisfied by a bend model
  with the K-factor wired to a constant.
- **Three conventions carry every dimension**, and each was a real choice. `Length` is
  measured from the OUTER VIRTUAL SHARP (the drawing dimension). The bend is placed
  **bend-outside** — its tangent line IS the named edge, so the material continues
  outboard through the bend; the alternative ("material inside", flange outer face flush
  with the edge) would make the base sketch mean something less than the blank's base
  region and complicate the unfold for no gain. And **a flange folds toward the face its
  edge is quoted on**, that face becoming the inside of the bend, which makes
  `Up`/`Down` mean one thing all the way down a chain.
- **v1 stops where corners begin.** Non-straight bend lines, closed corners, miters,
  bend reliefs, jogs, hems, louvres, two flanges sharing a stretch of edge, flanges on a
  flange's SIDE edges and multi-body sheets are all refused by name. The recurring shape
  of the refusal is instructive: a flange flush at ONE end only, and a second flange on a
  wall an earlier flange already reshaped, are both the corner case in disguise — the
  four-sided-wall check catches the second, and an explicit both-ends test the first.

## 6c. Drawings (hidden lines, sheets, drafting)

A drawing is a *document*, not a picture, and the whole design follows from that.

- **The v1 fidelity split is deliberate and stated in the output.** What gets DRAWN is
  exact wherever the kernel has it — a B-Rep part's feature edges are sampled from the
  actual edge curves, so a bore rim is a smooth circle however coarse the mesh — while
  what gets ANSWERED ("is there material in front of this point") comes from the display
  mesh. The gap between the two is one thing: a smooth surface has no modelled edge along
  its outline, so a cylinder seen from the side takes one from the mesh's view-dependent
  silhouette. That is the known upgrade (true B-Rep silhouette curves), and rather than
  hide it, `EdgeSource` labels every run so a consumer can see which fidelity it holds.
  The alternative — OCCT's `HLRBRep`, projecting exact edges AND silhouette curves and
  classifying against every face algebraically — is a project with the boolean's entire
  robustness surface; this rung gets usable drawings now and leaves the seam (a list of
  classified 2D polylines) already the right shape for the exact version to slot into.
- **The back-face test is exact and comes first.** The interesting design decision is not
  the ray cast but what happens before it: the surface immediately around a sample point
  is read from its own instance's mesh, and if every face there points away from the
  viewer the point is hidden with no ray at all. That settles the majority of a solid's
  edges exactly, and it costs one small box query. The ray only answers the genuinely
  non-local question — is some OTHER geometry in the way — which is the part a mesh can
  answer honestly.
- **The probe steps off along the most eye-facing local normal, not along the view.** A
  step toward the eye is useless in exactly the cases that matter, because they are
  tangencies: the ray from a point on a bore's bottom rim runs parallel to the bore wall
  and would scrape it for the wall's whole length. Stepping along the wall's own normal
  moves the probe radially into the void, and the ray then runs up an empty hole. The
  same step handles the inverse problem: an exact edge point on a concave surface sits
  INSIDE the inscribed mesh by up to the chord sagitta, and the step is what takes it
  out. That is why the bias is a fraction of the model and not a weld-tier constant — it
  must exceed the tessellation's own error, so a deliberately coarse mesh wants a bigger
  one.
- **Chaining before sampling is a correctness decision, not tidying.** A run can only be
  split where it is sampled, and a feature-edge segment is the smallest unit that carries
  a classification — so a rim delivered as 96 separate chords can only change visibility
  at a chord end. Measured against an occluder edge at x = 5, the boundary landed on 4.870,
  which is exactly the 52.5-degree sample; chaining the segments into one polyline lets
  the bisection find 5.000. The lesson generalizes: **whenever a refinement step exists,
  check that the thing being refined is not already quantized by its input.**
- **A run shorter than a pen stroke is dropped.** Within one bias step of a model VERTEX,
  "the surface near this point" legitimately includes the faces on the far side of that
  vertex, so a hidden edge reads visible for its last bias-length. There is no epsilon
  that removes this, because the ambiguity is geometric rather than numerical: a corner
  really is where several surfaces meet. Dropping runs too short to draw is the honest
  response, and it is the same judgement a drafting standard makes when it says
  coincident lines are drawn once.
- **A section view takes a POINT, not a plane.** A section view's cutting plane is
  perpendicular to its own view direction by definition — that is what makes the exposed
  faces project in true shape, and therefore what makes hatching and dimensioning them
  meaningful. Offering an arbitrary plane would let a caller produce a foreshortened cut
  face with dimensions that lie, so the API offers the depth and documents that an
  oblique cut is a view along the oblique normal.
- **Anchors are in model coordinates; anatomy is in paper millimetres.** A sheet
  dimension must measure the part (so its anchors and value live in the view's projected
  model space) while its arrowheads must be printable (so the drawn anatomy is sized in
  sheet millimetres). Keeping the two apart is what lets a view be rescaled or moved with
  its dimensions following and its values unchanged. The *proportions* are shared with
  the 3D PMI overlay rather than re-invented: `SheetStyle` states each length as a ratio
  to its text height, and those ratios are `AnnotationGeometry`'s pixel constants over
  its own text height, asserted by a test that reads both.
- **One `Compute()`, two writers.** The SVG and DXF writers consume the same
  `SheetContent` and differ only in spelling. The DXF side carries one rule worth
  stating: a file that NAMES a line type its layers use must also DEFINE it, or every
  reader falls back to solid lines and the visible/hidden classification — the entire
  point of the exercise — is silently lost in transit.

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

- **A BVH build's node numbering is not observable; its item permutation is.** Query
  results are appended in leaf-visit order, `Nearest` breaks distance ties by traversal
  order, and the imprint boolean interns seam points in `QueryOverlap` order — so a build
  that produces "an equally good tree" silently repermutes downstream geometry. Left and
  right children are adjacent by construction, so *relabelling* nodes changes nothing;
  that is what lets sibling subtrees be built concurrently and then renumbered into the
  canonical sequential order. Quickselect would have been faster still and was rejected
  for exactly this reason. Every future builder rewrite must reproduce
  `BvhBuildOrderTests`' fingerprints or argue, with a measurement, that the new tree is
  better.
- **The 2D rotating-calipers theorem does not lift to 3D, and this repo asserted that it
  did.** In the plane the minimum-area rectangle has a side collinear with a hull edge.
  The 3D analogue — a box face flush with a hull face — is *false*, and the counterexample
  is four vertices and a cube: the regular tetrahedron on alternate corners of [−1,1]³
  fits that cube at volume 8, while every face-flush candidate measures 16. O'Rourke's
  true characterization is that two *adjacent* box faces each contain a hull *edge*. This
  is a sibling of the epsilon lesson: a theorem that "obviously generalizes" is a claim to
  be tested, not repeated — `Fitting3d`'s own doc comment carried the false version until
  the implementation of it failed its first test.
- **Cancellation follows the cache, not the clock.** The rule is not "don't cancel long
  operations" but "don't abandon work whose result is cached". Tessellating an
  already-cached `BrepSolid` is downstream of the lowering, so it may observe a token; the
  lowering that produced it may not. `MeshSdf` and the winding hierarchy were measured
  (21.8 ms and 29.2 ms on 32 040 triangles) and left un-plumbed on purpose — viewer
  cancellation is granular to a whole part, so checkpoints inside a 20 ms constructor buy
  nothing.
- **Reuse the hierarchy you already have.** `Region2dBoolean`'s 2D nearest-edge query goes
  through the 3D `Bvh` with edges embedded at z = 0, so the branch-and-bound prunes with
  exactly the 2D box distance; a second 2D-only hierarchy would have to be maintained
  forever for no gain. It is bit-identical because only the minimum *distance* is
  consumed, never which edge attained it — and a minimum over doubles is order-independent.
  Worth checking that property explicitly before claiming any indexing change is free.
- **A swept surface's inverse evaluation is 1D too, but the reduction is geometric rather
  than algebraic.** Extrusions and revolves reduce because one parameter has a closed
  form. A `SweptSurface` has no such parameter — yet its points at path parameter v all
  lie in the frame plane at v, so `f(v) = (p − Path(v))·Tangent(v) = 0` determines v with
  no reference to u at all. The generalization: **when a surface is a sweep, look for a
  scalar condition the path parameter satisfies alone; it need not be a closed form.**
  Because f is multi-rooted on a curving path the solve is bracket-and-bisect rather than
  seed-and-Newton — the bracket is what guarantees convergence.
- **A seed table of the profile is not a seed table of the surface.** The generator a
  sweep carries is projected into the start frame before it becomes profile offsets, and
  that projection can be far thinner than the generator. Two branches then fit inside one
  seed interval, the sampled distance shows one broad minimum spanning both, and Newton
  from the single best seed converges to the *mirrored* parameter — an answer that is on
  the surface, passes every structural check, and is tens of millimetres wrong. Refining
  from every local minimum and its neighbours fixes it combinatorially, with no new
  epsilon.
- **Biarc fitting is offered, never applied.** Marching-tracer output stays a
  `PolylineCurve3d`; a caller opts in and receives the deviation the fit achieved, measured
  against the input samples. The metric deliberately says nothing about the true curve
  *between* samples — that is a property of the sampling, not of the fit — and non-planar
  input is refused rather than flattened. Two construction rules: the free parameter uses
  the conjugate-multiplied form `d = |v|²/(√disc + v·t)`, which removes the reference
  implementation's branch on a squared quantity by *being* both branches; and the second
  arc is built backwards from the end point so round-off concentrates on the interior
  joint, never on a data point a neighbouring piece has to hand over.
- **`BrepSolid.Clone()` is what makes "booleans consume their inputs" survivable.**
  Geometry is shared, not copied — curves and surfaces are immutable once constructed
  (trimming produces new `CurveSegment`s rather than editing carriers), so only topology
  needs duplicating and a clone is cheap.
- **Mass properties store the volume-weighted second moment, not the inertia tensor.**
  I = tr(S)·Id − S is a one-liner in either direction, but S is what transforms as a clean
  congruence and what adds under the parallel-axis theorem, so `Transformed`,
  `InertiaAbout`, `WithDensity` and `Combine` are two lines each instead of four special
  cases — and the stored quantity stays density-free. `Transformed` refuses shear and
  non-uniform scale: volume, centroid and inertia are well-defined under a general affine
  map but *surface area* is not a function of the input properties there, and refusing
  beats returning a silently-wrong area.
- **Never integrate moments about the world origin.** The divergence-theorem sum is over
  terms of size |r|³ that cancel down to the volume, so a 10 mm cube posed at
  (1e6, 2e6, 3e6) measures 6.5e-7 relative about the origin and 5.2e-12 about its own
  bounding-box centre. Re-centring costs one subtraction per vertex. The companion testing
  lesson: **an axis-aligned box at a round offset is a useless fixture for a cancellation
  test** — its coordinates are integers, its products are exact below 2⁵³, the errors
  cancel to zero, and the first version of the test "passed" while proving nothing. Rotate
  first.
- **`Validate()` is blind to geometric wire gaps.** It compares vertex *references*, so a
  sewn face soup passes it and then dies in the tessellator as a bow-tie vertex. This is
  the B-Rep analogue of the "closed but wrong" boolean lesson: a structural check that
  cannot see a geometric defect. Topological repair and geometric repair are different
  jobs, which is why healing has a separate refit pass and why its test measures the wire
  gap directly instead of trusting `Validate()`.
- **Healing repairs what it can prove and reports what it cannot; the line is drawn at
  inventing geometry.** The curved-edge pass re-TRIMS (foot-of-perpendicular parameters
  of the unified vertices — a local solve, since a merge gap is sub-tolerance by
  construction) but never re-FITS: the perpendicular residual that remains is exactly
  the information "this vertex is off this curve", and erasing it by deforming the
  curve would be a modelling decision made silently inside a repair. The same boundary
  shapes the shell-orientation vote: relative face orientation is PROVABLE from the
  opposite-sense manifold invariant, and the global side of a closed component from its
  boundary-loop fan volume against containment parity (voids point INTO their cavity) —
  but a component whose faces are all pole-bounded or closed bands (a two-face sphere)
  encloses no measurable loop volume, and a vote read from noise would flip whole
  solids at random, so the authored side is kept and SAID. One report, no log lines,
  bit-stable no-ops on well-formed input.
- **When a fast path can fail where the general path succeeds, defer — the invariant
  "the override is never worse than the base" should hold by construction, not by
  tuning.** The 1D inverse-evaluation reductions occasionally lose a query the 2D grid
  wins (aliased generators where no 1D seed's basin contains the answer but a damped 2D
  step wanders in); rather than chase seed counts, the overrides now fall back to the
  base grid on failure, which costs nothing on the hot path and turns a locked
  comparative test from an empirical observation into a structural guarantee. The same
  pattern as trimmed-tessellation tiers deferring downward — a fallback is legitimate
  exactly when it computes the same thing more generally.
- **Explode rides the flattening, not a second path.** An exploded view is a scalar
  composed into each occurrence frame's origin during `Flatten`; everything downstream —
  window, offscreen render, STEP export, BOM — is unchanged code. The load-bearing property
  is that the instance *list* (count, order, part references) is identical at every
  factor, which is what makes a matrix-only viewport update legal and keeps shared
  meshes, buffers and pick BVHs shared throughout an animation. And the datum is the
  largest body, never the centroid: a centroid-relative radial rule degenerates exactly
  when it matters, because on a spread-out assembly the centroid sits in empty space and
  the base flies away from nothing.
- **Animation is a pure function of t that must not touch geometry.** The exploded
  view proved the property (instance count/order independent of the parameter → a
  matrix-only viewport update is legal), and the `Animation` timeline generalizes it:
  a duration, an easing, and tracks mapping t ∈ [0,1] to instance poses or a camera.
  `At(t)` being pure is the whole design — scrubbing, reversing, window playback
  (`AnimationPlayback`, the UI-free transport machine), APNG/GIF/frame-sequence export
  and the docs build all evaluate ONE function instead of five re-implementations.
  Anything that re-meshes per frame is a different, far more expensive feature (the
  OpenSCAD `$t` item stays separate in todo.md for exactly that reason). Design calls
  worth recording:
  - **Placement by dependency direction**: pose tracks speak `Scene`/`Mechanism`
    (Modeling), camera tracks speak `CameraState`/`ViewCubeMath` (Viewer.Core), and
    Viewer.Core already references Modeling while the reverse would cycle — so the
    timeline lives in `EngrCAD.Viewer.Core`, and the accepted cost is that a `Scene`
    cannot carry its animation as a typed property (hosts pass it beside the scene:
    `EngrCad.Configure().WithAnimation(factory)`, re-invoked per live reload because
    tracks pose the occurrences they captured).
  - **At most ONE pose track and one camera track.** Two tracks each producing the
    full instance list cannot compose (whose matrices win?), so sequencing lives
    INSIDE a track, where it is well defined: every track has a clamp-semantics
    window on the shared timeline (hold the boundary value outside it — a finished
    explode stays exploded), and `ExplodeTrack.Stagger` gives per-occurrence windows
    over the new `Flatten(Func<Occurrence, double>)` overload (same walk as the
    scalar factor; exactly 0 leaves a frame bit-identical). Composing relative
    displacement tracks is future work, not half-supported.
  - **A `MotionStudy` is already the animation input format** — recorded pure poses —
    and `MechanismTrack` plays one back rather than re-solving, because a solve at
    arbitrary t from an arbitrary seed is the branch-flipping trap the sweep's
    continuation exists to avoid. Recorded frames are returned VERBATIM at their
    sample points (bit-exact, locked by test); between them each instance takes the
    chordal rigid motion: the delta b·a⁻¹ is rigid whatever the part transform
    carries (both matrices share it), its rotation slerps from identity
    (`Quaterniond.FromRotationMatrix`, Shepperd), and the origin travels the straight
    chord — M(s) = T(lerp(p_a, p_b, s))·R_s·T(−p_a)·a, exact at both ends.
  - **The purity is what made batching an export sound, not merely convenient.**
    `OffscreenRenderer.RenderSequence` holds ONE EGL context, one set of linked
    programs and one set of uploaded per-part buffers for a whole clip; only the
    per-instance matrices and the camera change between frames. That is legal for
    exactly the reason above — an animation moves poses, never geometry, so every
    frame draws the same parts and the upload cache keys on `Part` reference — and it
    is the offscreen restatement of what lets the window animate through
    `SetInstancePoses`. Measured on a 24-frame 480×360 export of a four-occurrence
    exploding assembly (win-x64): **1069 ms → 165 ms, 6.5×**, the saving being the
    context *plus* the per-part `RenderMesh.CreateFlat`, occlusion lookup and
    feature-edge/wireframe extraction that used to repeat per frame. The claim is
    pinned by an oracle rather than a stopwatch: the batched pixels are asserted
    **byte-identical** to one `Render` call per frame, because a speed claim about a
    render path is worth nothing without the picture beside it.
  - **"The model at t" has exactly one seam.** `EngrCad.PoseAt(scene, animation, t)`
    is it — used by the still overload `RenderToImage(scene, animation, t, …)`, by the
    MCP `screenshot` tool's `t` parameter, and (as `ViewportFrame.PoseByPath`, its
    browser-side matching half) by the web viewport's transport. The alternative,
    each consumer calling `At(t)` and posing for itself, is how a still and a scrub
    come to disagree about a frame nobody can tell apart from memory. Camera
    precedence is the clip's: the animation's own track, then an explicit camera, then
    the framing over first ∪ last bounds — never per-t framing, which would make a
    series of stills jump.
  - **Camera tracks reuse the view cube's primitives** (`ViewCubeMath.Ease`,
    `ShortestYawTarget` — naive yaw lerp sends the camera the long way round) rather
    than re-deriving easing; the turntable loops seamlessly under LINEAR easing with
    whole turns (t = 1 is t = 0), which is why easing is a timeline property the
    turntable defaults away from. A fly-through follows any `Curve3d`, and the orbit
    pose being Z-up means an RMF frame's roll is documented as dropped, not smuggled.
  - **Export ranking is a quality argument, not taste**: APNG first (three chunk
    types over the existing dependency-free `PngWriter`; lossless full colour —
    shaded renders are smooth gradients; the first frame is the PNG default image so
    the file ships as `.png` and degrades to a valid still), the numbered-PNG frame
    sequence always available (the ffmpeg escape hatch to MP4/WebM), GIF second and
    honestly documented to band on shaded renders (256 colours, no dithering — it
    fights the clean look; wireframe/flat GIFs far better). GIF's median-cut
    quantizer makes the PARTITION the mapping (a colour's index is the box it fell
    into), so no nearest-palette search can disagree with the split and ≤256-colour
    images reproduce exactly; the LZW encoder is locked by round-trip against an
    independently written decoder. One camera per clip when no track drives it,
    framed over the union of first and last frame bounds — never per-frame framing
    (the explode slider's camera lesson).
- **`$t` (time-parameterized GEOMETRY) — assessed and deliberately deferred.** OpenSCAD's
  `$t` re-evaluates the model per frame, which is the one thing the Animation section
  above is built never to do; this assessment records what it would actually take so the
  decision is a design fact rather than a lingering question. (a) **The shape of the
  feature is `Func<double, Scene>`**, not a track: geometry changing with t means the
  instance count, meshes and pick BVHs all may change per frame, so none of the
  matrix-only machinery (`SetInstancePoses`, APNG export's shared buffers, playback
  scrubbing at interactive rates) applies — every frame is a full `Scene.PreMesh`. The
  hot-reload loop already IS this pipeline for n = 1 (`SetScene` with camera preserved),
  so the honest v1 is a frame-stepping export/preview over `t => scene`, reusing
  `TabMeshLoader` per frame, NOT a live scrub. (b) **The cost model is the argument**:
  a typical drilled-plate scene lowers+tessellates in ~1–10 s; at 30 frames even the
  cheap end is a half-minute bake for one second of playback, so `$t` is an offline
  RENDER feature (bake frames → APNG/frame sequence, where the per-frame camera rule
  and writers already exist) and must never share the interactive transport UI, whose
  scrub contract is "pure function, instant". (c) **Caching is where the real design
  work lies**: a t-parameterized model usually varies few parameters, so the win is
  `FeatureHistory`'s prefix caching (unchanged-prefix features reuse their bodies) plus
  `Part` identity across frames for parts whose geometry did not change (bit-identical
  parameter snapshot → reuse the cached mesh/buffers). Without that, every frame pays
  the whole document; with it, a mechanism-plus-one-moving-boss model approaches
  pose-animation cost. (d) **What it buys over the existing Animation**: morphing
  geometry (a spring compressing, a bellows, parametric sweeps over time) — real, but
  every rigid-body use case is already covered better by `MotionStudy`/`ExplodeTrack`.
  Verdict: build it as a batch `RenderAnimation(Func<double, Scene>, frames, path)`
  when a concrete model needs it; do not pre-build.
- **Mates are a small dense nonlinear least-squares problem, deliberately.** Six unknowns
  per free occurrence, an analytic Jacobian, one global length scale making residuals and
  columns dimensionally uniform, and rank from a pivoted Cholesky. That is enough for the
  mates people actually use, converges to the weld tier, and — critically — can *report*
  what it did not pin. A general variational solver that occasionally converges would be
  worse. Angle and perpendicular mates have a genuine singular start (d/dθ cos θ = 0 at
  θ = 0); that is the derivative of a cosine, not a bug to engineer around, so the solver
  detects it and names the cause.
- **Mates across assembly levels: pick the variables by TARGET, parameterize them in
  WORLD space, and the chain rule costs nothing.** Three decisions make the multi-level
  solve small instead of general. (1) *Variable selection*: the unknowns are exactly the
  occurrences the mates target — the deepest link of each reference's occurrence chain —
  never "everything along the chain", which would hand the solver a gauge freedom (move
  the carrier or move the bolt inside it) that LM would resolve arbitrarily. Ancestors
  stay inputs unless some other mate targets them, in which case the general Jacobian
  covers the coupling for free. (2) *Jacobian composition*: a variable is a world-space
  rigid perturbation of one occurrence (rotation about its composed world origin), and
  simultaneous perturbations of a chain compose as Δ_ancestor ∘ Δ_target ∘ W — so the
  chain rule through the frame chain is NOT a product of derivative matrices, it is the
  one-level formulas (unit axes; axis × (point − origin)) with the moment arm read off
  each free link's composed world origin. The nonlinear update honors the same
  parameterization: apply the delta to the pre-step world frame and pull back through the
  pre-step ancestor frame (`moved.Then(ancestor.Inverse())`), ancestors snapshotted
  before any pose is written so a free ancestor and its free descendant read one
  consistent linearization. One-level chains keep their dedicated arithmetic and stay
  bit-identical to the single-level solver. (3) *The rigidity rule*: a sub-assembly no
  mate reaches into contributes no variables, so it stays rigid with zero code — and its
  internal mates need no re-solve because nothing inside it moved relative to itself.
  The one refusal that keeps the scheme honest: an `Occurrence.Frame` inside a
  sub-assembly is ONE object however many times the sub-assembly is placed, so a deep
  target whose owning assembly has multiple placements is rejected naming them (moving it
  would silently move geometry the mate never mentioned); a *chain*, by contrast, always
  names a unique placement, which is why `MateRef` carries the chain and why a bare deep
  `Occurrence` reference stays invalid. Per-instance internal DOF ("flexible
  sub-assemblies") is the follow-up, not a patch on this scheme.
- **STEP assemblies share products the way the display path shares parts.** Reference
  identity on the solid gives one PRODUCT and N occurrences; posing the geometry and
  writing it N times would throw away the structure the format exists to carry.
- **Extract, don't copy - the second time.** `RenderCore.cs` was created because the
  window and offscreen passes had drifted (the offscreen pass gained a scene-scaled
  frustum the window never got). A Blazor WebAssembly front end faces the identical
  temptation and *cannot* resolve it the same way: it cannot reference `EngrCAD.Viewer`
  without Avalonia and desktop Silk.NET. So the pure half became `EngrCAD.Viewer.Core`.
  The alternative - a WebGL2 client with its own copy of the shaders and camera math -
  is precisely the failure mode the file exists to prevent, and JavaScript would not
  have caught the drift.
- **The GL boundary is the extraction seam, and it is sharp.** Every type either takes a
  `GL` or does not; there was no third category to argue about. The seam's one cost is
  two forced class renames (linking and uploading split out of `ViewerShaders` and
  `RenderGeometry`), because a C# class cannot span assemblies.
- **Assembly name is not namespace, deliberately.** `EngrCAD.Viewer.Core` publishes types
  in namespace `EngrCAD.Viewer`. Nothing in .NET requires a namespace to live in one
  assembly, and `SectionPlane`/`ViewStyle` are public API with call sites in options,
  MCP, docs and tests. An assembly boundary is a packaging decision; a namespace is API.
  Renaming would have been a breaking change bought with zero user value.
- **A refactor of render code needs a PIXEL oracle, not just tests.** A shader or
  camera-math change survives all 1966 unit tests and still changes what users see. The
  DocsGen corpus - 50 rendered PNGs, byte-compared via `git status` - is the oracle that
  actually constrains this class of change, and it is what the extraction was verified
  against.
- **The web viewer puts no policy in JavaScript.** `engrcad-gl.js` owns the GL context,
  uploads what it is given and issues the draws it is told to; shader source, camera
  framing, section clipping and draw order all stay in .NET, shared with the desktop, and
  arrive as a plain frame description. The test of this rule is simple: if a question
  about what the scene *looks like* can be answered by reading the JavaScript, the rule
  has been broken.
- **WASM is a performance tier, not a port.** The kernel compiles unmodified and returns
  identical geometry; what changes is speed, and only by a constant: measured 18.9x
  slower than native interpreted, 4.3x with AOT. That makes "web viewer" a deployment
  decision (AOT is 4.4x faster for 2.4x the download) rather than an engineering fork,
  which is the whole reason the kernel was kept free of UI dependencies by mandate.
- **A feature-edge overlay DARKENS - "more lit pixels" is the wrong oracle.** The
  intuitive assertion for ShadedWithEdges versus Shaded is that it lights *more* pixels,
  and it is backwards: the overlay is near-black drawn *over* lit fill, so it lights
  fewer (measured 35 183 against 35 980). An assertion in that direction fails on correct
  code, which is the worst kind of test. The invariant that actually holds - and holds on
  both front ends, so it doubles as a parity check - is **darkened pixels > 0 and
  brightened pixels == 0**. Count the *direction of change* against the same scene without
  edges, never an absolute brightness total.
- **A pixel classifier has to survive the blend it is looking through.** Proving that a
  translucent part reveals the part behind it means classifying "did the hidden part show
  through", and the obvious classifier - count pixels where red exceeds blue, for a warm
  part behind a cool one - collapses under alpha: at 0.4 alpha beneath steel (blue 0.84),
  a `Palette.Coral` part lands at r - b = +8, indistinguishable from noise, and the reveal
  measured 1 478 pixels instead of 21 083. Pick the hidden part's colour so the classifier
  still separates *after* blending (amber, not coral), and trust **the ratio to the opaque
  case** rather than any absolute count.
- **Two render paths can disagree on line measures and both be right.** Comparing the
  browser client against `OffscreenRenderer`, fills, points and translucency agreed within
  2-10%, while wireframe did not (26 228 against 19 980). The cause is not the geometry:
  the offscreen pass renders at 2x and box-downsamples, so a 1-pixel line contributes
  about a quarter of a final pixel and falls below an absolute brightness threshold, where
  the browser draws 1-pixel lines at final resolution. Same primitives, different
  reconstruction filter. Resist "fixing" it by widening lines on one side - that trades a
  measurable, explainable difference for an invisible divergence in what is drawn.
- **A frame should be a VALUE, because that is what makes two render paths comparable.**
  The window pass and the offscreen pass drifted apart in the first place for one reason:
  each built its draws imperatively inside its own callback, so the only way to compare
  them was to look at pixels. `ViewportFrame.Build(instances, camera, bounds, aspect,
  furniture)` is the browser's counterpart and is a *pure function*, so draw order, clear
  colour, furniture ranges, per-instance matrices and the neutral shader state are all
  asserted directly as values. Extracting shared shaders and camera maths stopped the
  drift; making the frame a value is what makes drift *visible* without a screenshot.
- **Fills do not cull, and that looked like a bug until the section rung landed.** Both
  desktop passes leave face culling off deliberately: a section plane exposes a solid's
  interior as *backfaces*, which the shared fragment shader shades as cut material via
  `gl_FrontFacing`. Enabling culling looked completely fine for a rung and would have
  silently broken sectioning - exactly the kind of change that is impossible to
  attribute months afterwards, which is why it was asserted by a test rather than left
  as a comment, and the section rung landed against that test without touching it.
- **A JS-interop uniform needs a TYPE, and JSON cannot carry one.** The interop
  marshals every JSON number through `uniform1f`, which GL rejects on an `int` uniform
  with no visible error - so `uSectionCount` was deliberately never sent until the
  section rung, with a test asserting the absence. The rung added typed markers:
  `IntUniform` serializes as `{"int": n}` (dispatched through `uniform1i`) and
  `Vec4ArrayUniform` as `{"vec4": [...]}` (`uniform4fv` - needed because four packed
  section planes are exactly 16 floats, indistinguishable from a mat4 by shape). WHICH
  uniforms carry which type stays a C# decision; the JS dispatches on the marker's
  shape and contains no policy.
- **A published Blazor app is path-portable for the price of one tag.** Every asset
  reference the build emits is already relative - `./_framework/...` in the rewritten
  import map, `_framework/...` in the script tag - so `<base href>` is the *entire*
  difference between an app pinned to a site root and one that runs from any directory.
  Making it `./` is what lets the docs site serve the demo from `/EngrCAD/live/` with no
  `StaticWebAssetBasePath`, no post-publish rewrite step, and no repository name compiled
  into the artifact. Verified by publishing once and loading it from a subdirectory: zero
  404s, and the geometry identical to the root-hosted run.
- **A measurement beacon must not be able to fail.** The demo's `?report` timings were
  sent with `IJSRuntime.InvokeVoidAsync("fetch", url)`, which marshals the JS `Response`
  back across the interop boundary and throws when it cannot. That loses the measurement
  *and* trips Blazor's error UI - and it fails silently in the way that matters, because
  the thing it was carrying is the one number nobody has yet. A 1x1 `<img>` whose `src`
  is the beacon URL has no marshalling step and therefore no failure mode; the static
  server's access log records it either way.
- **An incremental Blazor WASM publish can silently ship a BROKEN runtime.** Publishing
  repeatedly into the same output without clearing `obj`/`bin` produced an app that was
  first merely slow (1 677 ms -> 2 765 ms on identical source, a 1.6x regression) and
  then aborted outright with `MONO interpreter: NIY encountered in method
  EngrCAD.Core.Vector2d:.cctor ()` plus an interpreter assertion - a static constructor
  containing nothing but four `static readonly` struct fields, so the named method is a
  red herring. The publish reports success at every step; nothing in the build log hints
  at it. The cause is the native relink being skipped or mismatched, leaving assemblies
  and runtime disagreeing. **Delete `obj`, `bin` and the output directory before any
  publish you intend to measure or deploy.** CI is safe by construction (fresh checkout
  into an empty workspace), so this is a local-iteration hazard - which is worse, because
  local iteration is where the numbers come from.
- **A number that moves when the source did not is an ARTIFACT story, not a machine
  story.** The above nearly put a wrong table on a public docs page: the no-AOT row was
  re-measured at 2 765 ms against a recorded 1 619.8 ms, and because this laptop genuinely
  does swing 2x, "stale measurement" was the comfortable explanation and the docs were
  duly "corrected". Two things should have stopped it sooner. The desktop and AOT rows
  reproduced *closely* while only one row moved - interference does not select a single
  row. And the demo's beacon had quietly stopped firing, which was read as a harness quirk
  when it was the crash. **Re-verify the artifact before believing the number**: a clean
  rebuild put the row back at 1 677 ms, confirming the original table. The rule is to
  rebuild from clean and reproduce a *disagreement* before publishing a correction, since
  a correction is far more expensive to unwind than a re-measurement.
- **Re-measure in ONE session, or you have not measured a ratio.** This machine
  (win-arm64 laptop) returned 88.7 ms and 185.7 ms from runs of the same
  Release binary on the same model - a 2.1x spread from thermal and background load
  alone. A desktop figure from one sitting divided by a WASM figure from another is
  therefore not a ratio, it is noise with units. The rule that follows: quote
  best-of-N for each side, taken back to back with the machine otherwise idle, and
  re-take the whole table whenever any row is re-taken. This is the same family as the
  JIT-tiering lesson (a single warm-up measured the same code at 1.4x slower and 0.84x
  faster on different runs) - the estimator has to be robust to interference, because
  interference is the normal condition.
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
- **A vector kernel that cannot be a transcription needs a *certainty band*, not a
  tolerance.** Two kernels in `SketchRegion` decide a branch the scalar code decides
  differently: a partial arc's in-sweep test is `Math.Atan2`, which has no bit-exact vector
  form, and the wedge test that replaces it (the signs of the cross products against the
  sweep's two boundary rays — `AND` up to a half turn, `OR` beyond it, because past π the
  *complement* is the narrow wedge) decides the same predicate by different arithmetic. Two
  such tests can only be made to agree where neither is near flipping, so the kernel refuses
  near the flip: since `c₀ = |o|·sin(δ)` and `c₁ = |o|·sin(δ − span)`, requiring both to
  exceed `1e-9·|o|` bounds the point a nanoradian off either boundary ray, and any lane
  inside that band sends its whole block back to `Atan2`. The band is five orders wider than
  everything the scalar path can contribute (`Atan2`'s own few ulps of a result bounded by
  π; the subtraction and the reduction by the *double* `2*PI`, both bounded because the arc
  is only classified as vectorizable when |from| ≤ 64 — the `%` itself is exact), which is
  what makes "outside the band they agree" a proof rather than a hope. **The point is the
  contract that buys: bit-identical for every input, not a bounded deviation.** A bounded
  deviation was available and would have been much simpler — the two branches of an arc's
  distance are continuous across the sweep boundary, so a disagreement there costs only
  O(r·ε) — but this field's *sign* drives boolean classification kernel-wide, and the
  repo's standing rule is that a silently divergent fast path is worse than none. Note also
  which inputs land in the band: a segment endpoint, shared bit-for-bit with its neighbour,
  sits exactly *on* a boundary ray, so the cases that most want exactness get the exact path
  by construction rather than by luck.
- **Reproduce a `break`, don't reason about it away.** The other non-transcribable kernel is
  the bézier's Newton refinement, whose scalar form breaks out of the loop when the
  derivative vanishes. The tempting vector answer is to let stopped lanes keep iterating on
  the grounds that Newton from a converged point is a fixed point. It is not: a vanishing
  `g′` makes the step infinite and the clamp turns that into 0 or 1 — a stopped lane would
  walk to an endpoint. Masking the *write* to the refined parameter with a sticky per-lane
  flag reproduces `break` exactly, and needs no argument at all.
- **A lane-wise kernel should substitute +∞ for a skipped lane, not skip it.** Both new
  kernels sit behind `SketchRegion`'s bounding-box reject, which is a proven-conservative
  *skip* in the scalar path. Reusing that proof to justify computing rejected lanes anyway
  ("the reject proves they cannot lower the minimum") works, but it makes the two paths'
  agreement depend on a second argument about the computed value's error rather than on the
  first. Blending +∞ into rejected lanes before the min-fold is what "skip" means to a
  running minimum, costs one select, and is identity by construction. The whole-block
  all-rejected fast path keeps the reject's actual performance value.
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
- **A general trihedral corner is a trimmed patch whose meridians are free.** Dropping
  `FilletAllEdges`' perpendicularity restriction did not need a new surface type: pick
  one face normal as the pole axis and the two great-circle arcs ending at its tangency
  lie in planes containing the axis — exact meridians, i.e. u-domain boundaries of the
  revolve — so only the third (diagonal) arc genuinely trims. The tessellation lesson is
  the durable part: the trimmed tier meshes the region as a structured column grid at
  natural density and **excludes every edge it builds from midpoint refinement**,
  because the refiner's flat `du/stepU` metric overstates a 3D chord near a pole without
  bound (u compresses as the parallels shrink) — measured, refinement cascaded midpoints
  into the apex fan (52 folds at 16/8, worst −0.99) and half-step slivers into the last
  rows (0.893 against a 0.924 floor at 48/24) on a base mesh that was already correct.
  A uv metric is only a proxy for arc length; wherever the parameterization is
  degenerate, refinement must defer to a base mesh built at honest density.
- **Variable-radius fillets are limited by the corner, not the band — and the band was
  the easy half all along.** The band is exact: a linear radius law between two
  equal-weight rational arcs is the RULED skin between them, whose v-sections are true
  circles because equal weights make lerping the points identical to lerping the
  homogeneous control points (the denominators cancel), and it is G1 with both
  neighbours. That is now implemented (`FilletRim`/`FilletEdges` law overloads,
  `Shape.Fillet(radiusAt, faces)`, `VariableFilletRimFeature`). What stays refused is a
  varying radius across a SHARP corner: two variable-radius bands are cones — the family
  of circles with linear centre and linear radius is exactly a cone with apex where the
  radius vanishes — and two cones that do not circumscribe a common sphere meet in a
  quartic, so there is no conic miter to weld them on. A CONSTANT law across such a
  corner makes both bands equal-radius cylinders again, which DO share an inscribed
  sphere, so the exact bicylinder ellipse is back; the refusal is therefore about the law
  and not about sharp corners, and it says so. One implementation rule earned it: the
  band's top and bottom boundaries must be RAILS on the band (`LoftRailCurve`) rather
  than free-standing lines, or the loft's v grid samples the boundary at a density the
  straight edge polyline does not and the face T-junctions against its neighbours.
  Variable-*setback* chamfers escape the corner problem entirely (the corner
  segment is a boundary ruling of both strips) and are implemented: the law is evaluated
  at rim corners and interpolates linearly along each edge, and two small theorems keep
  everything exact — a linearly varying perpendicular inset of a straight edge is still a
  straight *line*, and a constant top:side ratio keeps a strip's four corner points
  coplanar (s₀·d₁ = d₀·s₁), so the strips are planes, not merely bilinear patches. Arcs
  take the law only where it is constant along the arc, because a circle offset by a
  varying amount is an Archimedean-style spiral with no exact B-Rep form.
- **Sharp corners at ARC rim edges stay refused — a policy, not a gap.** The blend there
  is torus ∩ cylinder (or torus ∩ torus), which is not a conic: there is no exact corner
  curve to build, only a traced `PolylineCurve3d` with honest chordal error. The tracer
  route was considered and declined for the DEFAULT surgery: this kernel's brand is that
  construction operations are exact and approximation is always an explicit, labeled
  choice (the same rule that makes `BrepBoolean` refuse rather than silently fall back to
  the implicit route). Baking a traced polyline into a *primary feature's* B-Rep would
  put a fixed, non-refining sampling floor under every downstream tessellation — the
  cross-drilled housing's breakout curves show exactly what that costs — for a corner the
  designer can instead make exact by construction. The refusal therefore names the three
  exact escapes: make the rim tangent-continuous there (enlarge the arc to reach
  tangency, or insert a corner arc in the sketch), chamfer that face instead, or accept
  an approximate blend explicitly through the implicit representation. If a traced
  corner is ever added it must be opt-in and labeled, never the default.
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
  - **An angular face dimension measures the INCLUDED angle, not the normals'
    angle.** `AngularDimension.BetweenFaces` takes the two in-plane directions
    perpendicular to the planes' shared intersection line, each pointing from the
    line toward its own face's centroid: that is the angle a drafter dimensions (a
    10°-drafted side against the base reads 80°, where the outward normals span
    100°), and it also chooses the arc's branch automatically — the arc opens the
    way the faces do. The vertex is the intersection-line point nearest the
    centroids' midpoint, so the graphic lands beside the faces instead of at the
    line's arbitrary origin.
  - **Annotation picking is depth-blind on purpose, and tests the DRAWN segments.**
    The overlay renders always-on-top, so an annotation you can see must be
    clickable even when model geometry sits in front of its anchors — the pick
    (`AnnotationGeometry.Pick`) measures the ray's distance to the same segments
    `Build` emits (what you see is exactly what you can click), converted to style
    pixels at each segment's own depth. A claimed click never falls through to the
    part behind, mirroring the view cube's region-claims-the-click rule.
  - **Hole tables are GENERATED from the graph, never transcribed.** A
    `DrillShape`/`ThreadedHoleShape` node already carries its spec, points, depth
    and placement plane, so `HoleTable.For(part)` letters one row per call in call
    order (program order — stable across regenerations because the graph is rebuilt
    in program order) and `HoleAnnotations.AutoAttach` derives per-call callouts.
    Both are explicit calls rather than a flag on `Drill`: annotations belong to
    the PART (the document object), and a graph node cannot know which part will
    carry it.
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
- **Remote control of a running viewer is loopback TCP + newline JSON-RPC, served by
  the viewer, bridged by MCP** (todo.md's option (b), decided over the alternatives):
  stdio cannot serve a windowed app (option (a) is the separate headless server, which
  exists), and hosting MCP's HTTP+SSE inside the GUI (option (c)) puts a web server in
  the window for no gain while the bridge process already exists — it IS the MCP
  server, launched with `--mcp --viewer <port>`. **TCP over a named pipe** because a
  loopback socket is cross-platform, port 0 gives an ephemeral endpoint the viewer
  reports itself, and the test suite can drive a real connection with nothing but a
  `TcpClient`; newline-delimited JSON-RPC 2.0 because it is the same framing the MCP
  stdio transport uses one layer up. The implementation is three separable layers
  (`RemoteControl.cs`): `RemoteControlServer` (transport: framing, token gate, error
  envelope — binds `IPAddress.Loopback` with deliberately no way to bind wider),
  `RemoteViewerDispatcher` (the method vocabulary over `IRemoteViewer` — pure
  translation), and `ViewportRemoteViewer` (the only layer that knows Avalonia:
  every call marshals through `Dispatcher.UIThread`, and GL is never touched — a
  screenshot rides `SaveScreenshot`'s capture-on-next-frame path). That layering is
  what makes the stack testable without a window: transport and vocabulary are locked
  headlessly over real sockets with a stub viewer, and only the thin
  `ViewportRemoteViewer` wiring needs a live window. **Off by default, opt-in**
  (`WithRemoteControl(port, token)` / `--rpc [port] [--rpc-token t]`), because the
  endpoint moves cameras and writes files: loopback-only plus an optional
  per-request token is the honest local posture, not security theater.
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

- **Booleans**: the mesh pipeline handles coplanar and near-tangent configurations; the
  B-Rep pipeline is still transversal cases only, so coplanar-face and tangent
  configurations there remain future work.
- **Trimmed generated faces**: splitting the closed edges of a generated band face (a
  cut through a bore) outruns the full-domain grid tessellator; needs loop-driven
  trimmed tessellation.
- **Full revolve of profiles with holes** produces multiple shells (outer + tunnel tori)
  and is rejected until multi-shell construction is wired up.
- **Performance**: SIMD batch SDF evaluation and SoA render extraction are designed-for
  but not yet implemented; BVH uses median split (SAH is a drop-in upgrade).
