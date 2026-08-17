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
- **Unit convention (`ModelUnits`) — mm / N / MPa / tonne / s, and the choice is about
  WHERE a conversion can live.** Nothing in the kernel carries a unit at runtime, so a
  model is only meaningful against one consistent system, and the repository now names
  exactly one: lengths in millimetres (which STEP export already assumed), forces in
  newtons, stresses in MPa, **densities in tonne/mm³** (structural steel `7.85e-9`), time
  in seconds, gravity `9806.65` mm/s².

  This settled a real 1000× discrepancy that had shipped: the simulation catalogue stated
  tonne/mm³ while the document model's mass properties documented kg/mm³ (steel `7.85e-6`).
  **Neither figure is wrong on its own**, which is precisely why nothing could catch a
  caller taking one for the other, and why a second catalogue in `EngrCAD.Modeling` would
  have baked the disagreement in rather than resolved it.

  The deciding argument is not "which unit is nicer" — it is that **a density is either a
  number an equation consumes or a number a report prints, and only the second can be
  converted after the fact.** FEA assembly multiplies density into a mass matrix that must
  balance against a stiffness in MPa and a length in mm; there is no slot for a factor,
  and the factor would have to reappear in the gravity load, the heat capacity, the natural
  frequency, the buckling load and the transient time constant, each of which is verified
  here against a closed form. Mass properties form exactly ONE product from the density
  (mass = ρ·V), so a report converts that single number at the end. **The convention
  therefore lives where it cannot be converted**, and kilograms and grams are accessors
  (`ModelUnits.MassToKilograms`/`MassToGrams`,
  `Material.DensityKilogramsPerCubicMetre`) rather than a second convention. A consistent
  internal unit with a documented accessor is not the same thing as two conventions.

  Two consequences worth stating. Catalogue densities are stored in tonne/mm³ but
  **asserted in kg/m³**, because the datasheet figure is the only form a human can check —
  a test that compares `7.85e-9` against `7.85e-9` verifies typing, not physics. And
  `MeshMassProperties` / `BrepMassProperties` stay deliberately unit-AGNOSTIC (density is
  the caller's, default 1 makes mass equal volume): the convention belongs to the document
  and simulation layers that own materials, not to the integrators underneath them.
- **`Material` lives in Core for a layering reason, not a thematic one.** It is the one
  type `EngrCAD.Modeling` (`Part.Material` → mass properties, the BOM, the default display
  colour) and `EngrCAD.Fea` (every solver) both need, and Core is their only common
  ancestor — the same call as `EngrCAD.Viewer.Core`, with the namespace moved as well
  since a modelling user must not need a simulation `using` to say what a part is made of.
  **The analysis properties are OPTIONAL and zero means "not stated"**, extending the
  convention the thermal fields already used, so a document material (a name, a density,
  perhaps a colour) is constructible — which it was not before, because the constructor
  refused `youngsModulus <= 0`. **The refusal moved to the point of use**, where the
  analysis that needs the property can name it: `StructuralModel`'s constructor and
  `SetMaterial` refuse a missing modulus, `ThermalSolver` a missing conductivity or heat
  capacity, `ModalSolver` a zero density. The modulus refusal matters more than it looks —
  without one, Lame's parameters are both *zero*, so the stiffness would be identically
  zero rather than merely wrong and the solve would report rigid-body modes for a model
  that has none. **The regression oracle for the whole move was the FEA verification
  suite**: every closed-form figure (cantilever tip 1.90494 mm, Kirsch K_tn 2.4216, modal
  frequencies which scale as 1/√ρ, Euler buckling loads, thermal transients) depends on
  density, so any unit slipping anywhere would have shown up as a moved number. None moved.
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
  ordered. Full table in the Core README.
  **A symbolic factorization is REUSABLE, for the loop that solves a family of matrices
  differing only in their values** — the topology-optimisation loop above all, whose reduced
  stiffness has an identical sparsity pattern every iteration while the per-element scales
  change. `SparseCholesky.AnalyzePattern` runs the ordering, elimination tree and column-count
  pass ONCE into a `SparseCholeskySymbolic`, and each `symbolic.Factorize` then runs only the
  numeric pass — the part `Analyze`'s table says the time is in. **It is bit-identical to a
  fresh `Factorize` of the same matrix by construction, not by measurement**: the reuse GATHERS
  the new matrix's values into exactly the slots `Factorize` would place them, then runs the
  SAME numeric pass — so the only operation touching a value is a copy, and the arithmetic and
  its order are identical. The gather map is `UpperCsc` and `SymmetricPermute` mirrored in INDEX
  rather than in value (row indices are distinct within a column, so the per-column sort orders
  slots identically whether it carries a value or a source-slot number), which is why a same-
  pattern matrix's values land where the fresh path would place them with no arithmetic. The
  saving is real and BOUNDED — the symbolic fraction of a factorization, shrinking as the
  numeric pass grows: 1.50× on a 2D grid Laplacian down to 1.02× on a 3D FEA stiffness, so it
  removes the ordering cost and cannot touch the numeric floor. The complementary reuse the
  todo names — a `PackedSparseMatrix` type carrying its own analysis, or warm-starting CG — is
  a different mechanism and is filed. (c) **Convergence is a return value**
  (`SparseSolveReport`), and failure is honest: CG breaks out on nonpositive curvature
  instead of dividing by it, Cholesky throws naming the offending pivot column — the
  repo's report-what-happened convention applied to numerics. The library is
  deliberately dependency-free and mesh-agnostic (doubles + int indices), so the mesh
  engine adapts to it, never the reverse. (d) **The symmetric-INDEFINITE factorization is
  a complex-capable LDLᵀ (`SparseLdlt`), and choosing it over a real Bunch–Kaufman was
  the design decision.** The consumer is the direct per-frequency harmonic solve
  `(K − ω²M + iωC)·u = f` — complex SYMMETRIC, its equivalent real form
  `[[A, −B],[−B, −A]]` symmetric indefinite by construction — which both incumbent
  solvers refuse correctly. Three candidates were weighed. COCG/QMR: rejected, the item
  asks for a DIRECT solve and a shifted system near resonance is exactly where Krylov
  convergence goes unpredictable. Real Bunch–Kaufman with 2×2 pivots: rejected on
  STRUCTURE rather than taste — a magnitude-searched 2×2 pivot merges two columns'
  patterns, so the symbolic pass stops predicting the numeric structure and the AMD
  ordering's counts go stale, which is why production sparse indefinite solvers are
  multifrontal machines with delayed pivots (a different order of project; filed for
  real systems that genuinely need pivoting). The complex spelling wins because **for
  this family the "2×2 pivots" are fixed by structure, never searched**: a complex pivot
  r + is is invertible whenever (r, s) ≠ (0, 0) — exactly the robustness a
  Bunch–Kaufman block buys on the real form's paired ±structure, where the real form's
  leading n×n block is K − ω²M alone and unpivoted elimination of it breaks down near
  every resonance, the regime a harmonic sweep operates in. Structurally-1×1 pivots are
  what let the elimination structure BE the Cholesky one on the union pattern: the
  symbolic pass (elimination tree, ereach, column counts) is `SparseCholesky`'s
  internals shared verbatim, AMD applies unchanged, and `Analyze` predicts this
  factorization too. Solvability is a four-line kernel argument recorded in the class
  doc: a singular leading minor of R + iS forces a vector annihilated by BOTH R_k and
  S_k, so with any positive-definite damping (Rayleigh damping is) no pivot can vanish
  at any frequency including resonances, and the remaining breakdown case — an entirely
  undamped subsystem exactly at one of its own resonances — is one where the physical
  steady state is unbounded too, so the loud refusal is the right answer. The real
  overload stays unpivoted with the caveat stated (factors iff every leading minor is
  nonsingular; a saddle system needs its constraints ordered last; AMD can reorder a
  structurally-zero diagonal early and turn a factorable matrix into a refusal —
  documented, natural default), refuses exactly-zero pivots by caller column, and
  reports its pivot-magnitude extremes so near-breakdown growth is visible rather than
  silent.
  **A PIVOTED real sparse indefinite factorization has been surveyed for a consumer and there
  is none, so it is a correctly-deferred decision rather than open work.** The whole repository
  produces exactly one indefinite system, `DirectHarmonicSolver`'s, and it takes the COMPLEX
  path; the real overload has no production caller at all (only a KKT test fixture). Nor is one
  latent: structural supports are ELIMINATED rather than penalised, so no solver here forms a
  Lagrange-multiplier saddle system; `TopologyOptimizer`'s "Lagrange multiplier" is a SCALAR
  found by bisection; and the sketch and mate solvers work on dense `JᵀJ`, which is PSD. The
  honest version remains a multifrontal/supernodal solver with delayed pivots — the shape
  recorded above, a project of a different order — so it is not started speculatively. The
  interim half-step if a consumer does appear is one round of iterative refinement on the
  caller's side, which `SmallestPivotMagnitude` already supports deciding.
- **The general NON-symmetric solvers (`Gmres`, `BiCgStab`, `Ilu0`) are the CFD campaign's
  stage 1, and three decisions shaped them — each is a place the obvious answer was wrong.**
  Advection makes a flow operator non-symmetric (and non-diagonally-dominant at any
  interesting Reynolds number), so `SparseCholesky` and `SparseSymmetricCG` do not apply and
  a Krylov method with a real preconditioner is the first thing to build; it is also
  independently useful. **(a) No new matrix type.** The obvious reading of "a non-symmetric
  solver needs the full matrix" is that a general CSR type must sit beside the symmetric-upper
  one — but `PackedSparseMatrix` was ALREADY general: `SparseMatrixBuilder.ToMatrix()` stores
  full CSR with rows sorted by column, `Multiply` already handles the non-symmetric case, and
  the assembly carries the counting-sort/packing lessons. So the storage decision is to reuse
  it in its general form; the only expand-to-full step is `Ilu0.Factorize` calling `ToGeneral()`
  (a no-op on an already-general matrix), because ILU walks both triangles while a symmetric-
  upper matrix only stores one. **(b) GMRES is RIGHT-preconditioned, and the reason is the
  verification bar rather than convenience.** The Krylov subspace is built on `A·M⁻¹` from
  `r₀ = b − A·x₀`, so the residual the Givens rotations track is the residual of the ORIGINAL
  system, not of a left-preconditioned one — which means the number the solver watches is the
  number a caller can recompute, and "converged on the wrong residual" (the classic silent CFD
  failure) cannot happen. It is not left to the reader's trust: the report recomputes
  `‖b − A·x‖` exactly at the close of every restart cycle, and a test asserts the reported
  residual equals an independently recomputed one. Happy breakdown (a near-zero new Arnoldi
  vector) is treated as convergence, not divided by; the un-restarted "converges in ≤ n steps"
  theorem is asserted (measured 12/35 at 2.7e-13). BiCGSTAB is the cheaper-per-iteration
  partner (constant storage, oscillating residual) because which of the two wins is
  problem-dependent and a flow code carries both; its two breakdowns (ρ≈0, ω≈0) are caught
  before the division and reported as `Converged=false`, never a silent NaN — the CG
  "report the non-SPD direction rather than divide by it" convention. **(c) `Ilu0` has NO
  ordering parameter, and that contradicts the staging note's guess that "AMD reduces fill for
  the ILU exactly as it does for Cholesky".** ILU(0) has zero fill BY DEFINITION — L and U
  carry exactly A's pattern and every fill is dropped — so a fill-reducing permutation has
  nothing to reduce; it would spend a symbolic pass to move round-off around for no saving AND
  break the "no fill ⇒ ILU(0) IS the exact LU" identity that verifies the factorization. A
  permutation there changes only WHICH entries are dropped, i.e. preconditioner ACCURACY, a
  different question wanting a different ordering (RCM for bandwidth, a multicolour ordering for
  parallelism) that only earns its keep once fill is admitted (ILU(p > 0), ILUT) — filed for
  that tier. So AMD does not apply; ILU(0) stays natural-ordered, hence deterministic. The one
  thing that DID transfer for free is that ILU(0) of a symmetric matrix with a symmetric pattern
  is itself symmetric (`M = L·U = L·D·Lᵀ`, since the dropped fill is symmetric too), so it is a
  legitimate conjugate-gradient preconditioner — added to `CgOptions.Preconditioner` additively,
  leaving the Jacobi path bit-identical when null — and it cut CG from 87 to 32 iterations on a
  Dirichlet grid Laplacian, GMRES 40→10 and BiCGSTAB 37→7 on a convection–diffusion operator.
  Verified against a dense partial-pivoting solve on a random non-symmetric matrix, an upwind
  and a high-Péclet central-difference convection–diffusion (the oscillatory, non-diagonally-
  dominant regime), the ILU-vs-exact-LU identity, and determinism bit for bit. What is NOT built
  is the rest of the campaign (Stokes, Navier–Stokes, stabilisation, turbulence) — a separate,
  larger project, staged in todo.md.
- **Space-filling curves (`Geometry2.SpaceFillingCurve`) — the name overpromises, so the
  API is built around saying what it really is.** A true space-filling curve is the LIMIT
  of a sequence and has infinite length; what exists is one finite member and the ORDER is
  the parameter. A caller states a *spacing* and is told the `Spacing` achieved beside the
  `RequestedSpacing` (the `BiArcFit.MaxDeviation` convention), never the request echoed back.
  **The interesting decision is which quantity quantises.** The order is fixed by one
  inequality, `side ≤ spacing·radix^n`, and the surplus has to land somewhere: hold the
  FOOTPRINT to the region and the spacing comes out finer than asked; hold the SPACING and
  the footprint comes out larger than the region. Both readings give the SAME order, so
  neither is cheaper and the choice is not about cost — the footprint is held because a curve
  is laid *over* a region, and a footprint overhanging by an arbitrary amount would put the
  pattern's phase somewhere the caller never stated, which for a layered infill is exactly
  the property that has to be reproducible. (The consequence is stated rather than hidden: on
  a long thin plate the achieved spacing is set by the LENGTH, since the footprint is the
  bounding square.)
  **The verification bar is why this is a Core type rather than a utility**: almost every
  claim is exact and combinatorial, so the tests are integer identities with no epsilon —
  sites counted in closed form and pairwise distinct (the check that catches a flipped
  recursion, which is the classic way these are got wrong), consecutive sites exactly one
  lattice step apart, Moore's closure asserted rather than trusted, `Length ==
  SegmentCount × Spacing` exactly. Only coverage is a measurement, and its bound is DERIVED
  from the cell's own circumradius rather than tuned (√2/2 measured exactly for the square
  families, 0.5738 against the triangular lattice's 1/√3 for Gosper).
  **Three things were measured rather than assumed and each changed something.** (a) The
  longest straight run SATURATES — 3 cells for Hilbert and Moore, 5 for Peano, 2 for Gosper,
  at every order from 3 upward — so "Hilbert is the isotropic member" is a number, and the
  reason to reach for Peano is 5 against 3, a real difference and a small one. (b) **Z-order
  is not a curve**, and its own arithmetic says by how much: exactly `2^(2n−1) − 1` of its
  `4^n − 1` steps are not lattice steps (half of them, minus one) and the largest jumps the
  full grid width, so it is offered as the bijective ORDERING it is and refused by name where
  a path is wanted. `Morton2d` holds the one interleave, which `PlanarSection`'s silhouette
  fold already sorted by — two spellings of one bijection would let a fold's merge order and
  a curve's visit order disagree about the same grid. (c) **Gosper does not tile a
  rectangle**, so it is placed by its own island's inradius, and that inradius is COMPUTED
  from the walk (the nearest unvisited site's distance from the centroid, less the triangular
  lattice's covering radius `1/√3` — a sound bound, since a point closer than that has its
  nearest site closer than the nearest unvisited one) rather than tabulated per order. The
  price is honest and reported: Gosper's achieved spacing runs 2–2.6× finer than the request
  where a square family's runs under 2×.
  The Modeling-side consumer is `SpaceFillingInfill` (§6b): the clip is a comparison against
  `SketchRegion`'s exact signed distance with no tolerance in it, coverage is measured through
  `Region2dOffset.Stroke` rather than inferred from the path length, and **both ways a fill
  can silently miss are refused by name** — a region the clearance erodes to nothing, and a
  connected piece of the eroded region the lattice's phase stepped over. What is deliberately
  NOT refused is a thin neck inside a piece that is otherwise filled: the piece as a whole
  catches passes, so the honest answer is a MEASUREMENT rather than a refusal the detector
  cannot justify — which is what `Region2dThickness` supplies (below).
- **The footprint decision has a stated cost, and `OverTiled` is the way out that keeps it.**
  Holding the footprint to a region's bounding SQUARE means a long thin plate has its spacing
  set by its LENGTH and most of the curve generated outside it (an 80 × 12 plate at spacing 3:
  1024 cells, 128 kept). `SpaceFillingCurve.OverTiled` lays a TILING of Hilbert blocks over the
  bounding RECTANGLE instead, and it is one continuous path for a structural reason rather than
  by arrangement: an order-n Hilbert block runs between two ADJACENT CORNERS of its own square,
  so the eight symmetries of the square supply whichever (entry, exit) pair each block's
  neighbours need and a boustrophedon over the block grid links them (`TiledHilbertLattice`,
  which moved down from Modeling once a second consumer wanted it — a tamper mesh and an infill
  both read it, and its block-count rule is now asked rather than restated in either). **The
  footprint is still what is held**, which is exactly what makes the cells anisotropic — each
  axis's cell size is that axis's extent over its cell count — so `Anisotropy` is REPORTED
  rather than bounded, and the trade it reports is real: small blocks fit an arbitrary rectangle
  closely and stretch the cells, large ones stay isotropic and quantise the fit coarsely, with
  `blockOrder: 0` the plain serpentine at the extreme. One block reproduces the square form site
  for site and bit for bit, and every incumbent construction reports
  `SpacingX == SpacingY == Spacing` bit-identically, so this is a generalisation rather than a
  second mode. Hilbert only: Peano's blocks end at the same two corners and would tile
  identically, Moore is a closed LOOP with no ends to link, and Gosper does not tile a rectangle.
- **`SpaceFillingCurve3d` is the volume member, and it is a PARALLEL type** — the call
  `CurvedRegion2d` makes against `Region2d`, since a 2D curve's data is `Vector2i`/`Vector2d`
  and a 3D one's is `Vector3i`/`Vector3d`, so the two share every convention and none of their
  data. It is **Hilbert only, deliberately**: the consumer is a single connected path through a
  volume, Z-order's 3D member is not a curve, Peano's is radix 3 (27 cells per level, so three
  times the spacing quantisation for nothing this wants), and Gosper has no 3D analogue — an
  enum of one member would only invite the other three to be filled in without a caller. The
  walk is **Skilling's transpose algorithm** for the reason the 2D file gives for Peano's digit
  rule: a closed form has no orientation table to get backwards, and the bijectivity test is
  what would catch one. Measured rather than taken from the literature: the two terminals are
  ADJACENT CORNERS of the cube (so 3D blocks would tile, if a consumer ever wants that), and the
  longest straight run saturates at 3 at every order — the SAME number the 2D curve reports, so
  "no preferred direction" carries into the volume rather than being claimed of it.
- **`Region2dThickness` is the local measure the connectivity refusals structurally cannot
  make** — the 2D twin of the wall thickness `Manufacturability` measures on a solid. It probes
  inward from every boundary segment and reports the PERPENDICULAR distance to the line of the
  segment it hits, not the raw ray length, which is exact wherever the opposing boundary is
  straight (for a polygon, everywhere) and is what makes a tapered slot read its true width. The
  probe starts exactly ON the boundary with no stand-off, because the source segment is excluded
  by INDEX — exact — and a stand-off biases every reading low by its own length, measured. Holes
  contribute their own segments, so the web between a bore and a wall is a neck like any other.
  What it is NOT is named rather than implied: not the medial axis (the largest inscribed disc
  is a better local width at a fillet and is a different computation, so it is an alternative
  rather than a silent upgrade — the same call the 3D twin makes) and not a refusal, since it
  measures and the consumer decides.

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
  **The `Part`-level display remesh that was filed beside it is declined rather than
  pending**, and for the reason above rather than for want of effort: the two use cases it
  named are already served without it. A caller who wants uniform triangles for display or
  for an FEA export writes `new Part(name, Remesher.Remesh(part.GetMesh(q), options).Mesh)`
  — one line, over public API, with the fidelity trade visible at the call site — while a
  flag on `Part` would have to negotiate `GetMesh`'s first-caller-wins cache and the
  `Scene` > `EngrCadOptions` > `MeshQuality` precedence ladder to buy exactly that line, and
  would put the trade back behind the rendering knob this decision exists to keep it out of.

- **A mutable in-place hole fill or extrude is blocked on Euler operators that do not
  exist, not on a caller** — the correction to the way it was filed ("once callers want
  them"), which reads as though the variants were an overload away. `EditableMesh` has five
  operators — `SplitEdge`, `FlipEdge`, `PokeFace`, `CollapseEdge`, `MergeEdges` — and every
  one of them REARRANGES existing material. Filling a hole makes a face that closes a
  boundary loop and an extrude makes both vertices and wall faces, so neither is expressible
  in that vocabulary at all; both would need new operators, each carrying the journaled
  `MeshChange` record that is what makes do→revert bit-identical by construction, which is
  the substantial half of the work rather than a rider on it. `HoleFiller` and `MeshExtrude`
  are accordingly SOUP-level (`ToIndexed` → append faces → the manifold-validating `Build`),
  which is the right shape for what they do: a fill invents geometry and wants revalidating.
  The demand side says the same thing — `MeshExtrude` has no consumer anywhere outside its
  own tests, and `HoleFiller`'s single production caller (`MeshRepair.AutoRepair`) would
  save one `ToMesh` round trip on the branch that runs only when a crack was welded.
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
output is deterministic, and every refusal names what failed. *Not* guaranteed by the MESHER:
sliver-free elements. Radius-edge bounds provably cannot exclude slivers, so `TetQualityReport`
reports minimum dihedral beside radius-edge and counts what the first measure cannot see.

**`TetSmoothing` is the post-pass that acts on it, and the choice of technique is the design
decision.** The two standard answers are sliver *exudation* (a weighted-Delaunay perturbation,
which changes the topology) and *optimization-based smoothing* (which moves points only). Only
the second keeps every guarantee above without re-deriving any of them: the boundary is
untouched, so the surface-fidelity contract and the volume identity hold **by construction**
rather than by measurement (measured drift 7.8e-15 … 2.1e-14, pure round-off — mathematically
the elements go on tiling the same region); the connectivity is untouched, so nothing has to be
re-classified or re-recovered; and every candidate position is accepted only if it leaves all
incident elements strictly positively oriented **by the exact predicate**, so `TetMesh`'s
invariant is preserved rather than re-checked. Exudation is stronger and remains the filed next
step; it is also the one operation that can invalidate all of that at once, which is why the
weaker technique went first. Measured on a 20³ box: **every sliver removed** at three sizes
(190 → 0, 399 → 0, 1 149 → 0) with the worst dihedral going 0.00° → 10–17°.

**But the residual is INPUT-DEPENDENT, and that is the caveat to keep.** The same 20³ box with
its faces triangulated by the B-Rep tessellator instead of by `MeshPrimitives.Box` starts from
the identical 190 slivers and finishes with **2** rather than 0. A pattern search is a heuristic
local optimizer: a small difference in the input changes which candidate wins a near-tie, and
with it the whole path. Two candidate causes were guessed before measuring and both are wrong —
it is not translation (the same primitive anchored at a corner and centred on the origin both
reach 0) and not the build (Release and Debug agree bit for bit), and both negatives are pinned
by test so the next reader does not re-guess them. So the guarantee on offer is *determinism for
a given input on a given build*, not sliver-freeness, and the tests assert a strict decrease
rather than zero because a fixture-specific zero is exactly the kind of claim that rots.

Two further things about it are worth keeping. **The mean minimum dihedral FALLS by 2–4° while the
worst rises**, because the objective is the worst incident angle and lifting it moves a vertex
away from what its other elements would have preferred — a real trade, reported rather than
buried, and the right one when the worst element is what conditions the matrix. And **a
deliberate boundary layer is frozen, not repaired**: every vertex touching an element stretched
past `TetQualityOptions.AnisotropyThreshold` is left alone, because a smoother that returns a
layer to isotropy destroys exactly the resolution it exists to provide. That inherits the
partition's honest limit unchanged — a layer element and an accidental sliver are affinely
equivalent, so freezing by measured stretch necessarily freezes accidental stretched slivers
too, and the count is reported instead of the ambiguity being wished away.

**What recovery actually wants of its input — the filed limitation was wrong in two
directions.** It read "recovery is not happy with an isotropic remesh, because near-uniform
vertex spacing has no structure". Measured, a remeshed sphere meshes in **zero** recovery
rounds at three target edge lengths once the remesh is Delaunay-clean, *with one patch per
triangle* — the exact configuration the explanation blamed; and a remeshed box with a **0.145°**
worst angle and a radius-edge ratio of **198** meshes while a remeshed sphere at **27.9°** and
**1.07** is refused, so triangle quality is not the criterion either. The real condition is
that the surface triangulation must **already be the boundary of the Delaunay tetrahedralization
of its own vertices**. Where the surface is flat a patch absorbs any diagonal and there is
nothing to recover; where it is curved every triangle is its own patch and must appear
verbatim — *the requirement the patch abstraction exists to avoid, arriving through the back
door because curvature leaves it nothing to group*. That is the honest statement of the gap,
and it is why red subdivision is not a weak version of the textbook fix but a different thing:
conforming to an arbitrary PLC needs protecting-ball segment and subfacet encroachment, which
carries a termination proof where a budget carries none.

Two consequences were landed rather than left implicit. **Non-convergence is detected instead
of spent on**: the offending count failing to improve on its best for five rounds ends recovery
with "more rounds and a larger budget will not help" — the monotone-decrease rule the
trimmed-face refiner already uses. Note *why* the obvious identical-set stall test does NOT
fire, because it is the interesting half: on a remeshed sphere the count sits at five from
round 4 to round 40, but they are five *different* faces each round, each smaller than the last,
until their three vertices agree to 1e-11 on a radius-10 sphere — refinement chasing its own
tail into degeneracy, which a set comparison reads as progress. **And the refusal measures the
input rather than blaming recovery**: it reports the worst minimum angle, the worst radius-edge
ratio and the fraction of triangles with no coplanar neighbour, and it no longer says "remesh
the surface", which was backwards for the input that most often reached it — `MeshPrimitives.Cylinder`'s
n-gon caps triangulate as a one-corner fan at 3.74°, and every remesh of it tried lands between
0.013° and 7.7°. Remeshing was *creating* the slivers it was being recommended as the cure for.

**`MaxElementSize` is a *minimum* element size, not a bound on the element count — and the
report field is the honest small change, not a refusal.** Filed as a hypothesis (a coarse
`MaxElementSize` might not bound the mesh in the presence of a fine curved feature) and
measured on the exact fixture that raised it, `Box(60,20,8) − Cylinder(4,40)` with the bore
held fixed and `MaxElementSize` swept: the element count is *non-monotone* (84 k → 68 k →
143 k → 90 k → 102 k for h = 20…6) where a size-bounded mesh has count ∝ h⁻³, so `count·h³`
FALLS from 671 M to 22 M rather than staying flat, and even the coarsest request (h = 20) gives
84 k elements — 66× a uniform edge-20 mesh. The cause is that `RefineBoundaryToSize` only
**splits** a boundary facet larger than the target and never coarsens a finer one, so where the
Ø8 bore is tessellated to ~0.5 mm facets the *surface*, not `MaxElementSize`, sets the local
element size; the count is boundary refinement (`bSteiner` = 152 k against `qSteiner` = 1.4 k at
h = 4), whose density the surface fixes. The two duties the todo named settle differently. A
**refusal is wrong** — the mesh is correct, merely finer than a "coarse" request implies — so
the change is a report field, `TetMeshDiagnostics.MinBoundaryFacetSize` (the finest boundary
facet's circumradius: the surface's own floor), which a caller compares to its request and, when
the gap is large, coarsens the *surface tessellation* rather than raising `MaxElementSize`. And
the **observability half was already met**: every mesh and solve entry point takes a
`ProgressCancel`, honoured down to the Delaunay build and the refinement loops, so "40 silent
minutes" is a caller passing no deadline rather than a missing seam.

**Feeding the mesher from the model, not just from a mesh — provenance is a by-product of
tessellation, not a second pass.** `TetMesher` takes a `HalfEdgeMesh`, so B-Rep face identity
reaches its boundary-condition tags (`TetMeshOptions.FacetTags`, keyed by `TetFacet.SourceTriangle`)
only if the caller threads a per-triangle tag array through — the "which triangle is on which
face" bookkeeping the selector vocabulary exists to avoid. `BRepTessellator` knows the answer,
and exposing it cost no change to the tessellation: `MeshWelder.WeldPolygons` gained a tagged
overload that rides a per-polygon tag onto the surviving faces (welding drops no non-degenerate
polygon and reorders no face), so `TessellateWithProvenance().Mesh` is **bit-for-bit**
`Tessellate`'s output and `FaceProvenance[f]` names the B-Rep face mesh-face `f` came from.
`TessellateForTetMesh` is the whole bridge to a tet mesher — a triangulated mesh plus
per-triangle tags, the triangle-per-face count read from the welded faces' DEGREES alone
(diagonal choice irrelevant, both triangles of a quad sharing a face), so the mesh is all
triangles and the mesher's own `Triangulated()` keeps the tags lined up with `SourceTriangle`.
The oracle is bit-identity twice over: the mesh equals a plain `Tessellate`, and a structural
solve whose support and load are named by `Facets.Tag(faceId)` is bit-for-bit the same solve
named by a geometric selector, because the two resolve to the same facet set (asserted, plus a
drilled-plate test that the bore wall tags to the cylindrical face and the caps to their planes).

### Anisotropic boundary layers, and the three decisions in them

A boundary layer is a graded stack of very flat elements marched inward from a wall selected
by a `Facets` predicate — **the same selector a boundary condition uses**, so a wall is named
once. The architectural point is that it adds no volume algorithm: the nodes are marched
first, and what is left over is bounded by an ordinary closed triangle mesh that `TetMesher`
already fills, so the volume identity, exact orientation, determinism and the refusal culture
are all inherited rather than restated.

1. **Prisms split into three tetrahedra, and the diagonal rule is COMBINATORIAL, not
   geometric.** `TetMesh` stores tetrahedra, so a prism's three quadrilateral side faces each
   need a diagonal, and two prisms sharing one must agree or the mesh is non-conforming and
   every solver silently integrates over a gap. The rule is Dompierre's — a quad's diagonal
   contains whichever of its two base vertices has the smaller index in the input surface —
   which is symmetric in the two, so neighbours agree without communicating.
   **`PolygonFan`'s shorter-3D-diagonal rule would be wrong here**, and not marginally: a
   layer quad on a flat wall is an exact rectangle, whose two diagonals are mathematically
   equal, so the choice would fall to round-off on essentially every element of the stack —
   the same trap that made 408 of a UV sphere's 960 quads flip on an ulp. This is the one
   place in the repo where the geometric rule is the wrong answer, and the reason is that a
   boundary layer is made *entirely* of the degenerate case that rule's tie guard exists for.
2. **The stage runs in TWO passes, because the party that decides the interface triangulation
   is the fill, not the layer.** Boundary recovery works per planar PATCH precisely because a
   Delaunay triangulation picks its own diagonal across a coplanar quad — so handing the fill
   an offset wall and assuming it comes back triangulated the same way is wrong on a box, and
   wrong *silently*. So: march the columns, hand over the surface, then read the interface
   triangulation back off the finished fill and build the prisms on that. The fill chooses;
   the stack conforms. And the weld is by exact position, which makes the conformity check
   fall out for free: each interface triangle is then used by two elements and vanishes from
   the combined boundary, so **"every boundary face has a known tag" IS the statement that the
   two meshes conform** — there is no separate check to keep in step.
3. **The quality report PARTITIONS rather than judging.** `TetQuality`'s sliver rule is tuned
   for isotropic elements and would call every legitimate layer element degenerate, and a
   report that cries wolf on correct output is worse than none. So elements are split by
   measured stretch: `SliverCount` and the radius-edge figures cover the isotropic ones only,
   while the stretched ones are counted and measured in their own metric
   (`MinStretchedDihedralDegrees` — the minimum dihedral after un-stretching along the
   element's thinnest principal axis; only that axis, because a full whitening maps *every*
   non-degenerate tetrahedron to a well-shaped one and so carries no signal, whereas scaling
   one axis restores a layer element and leaves a needle exactly as bad). A mesh with nothing
   stretched reports what it always did, number for number.
   **The honest limit is stated rather than implied**: a legitimate layer element and an
   accidental sliver are AFFINELY EQUIVALENT — the stack element is four nearly-coplanar
   points too — so no purely local geometric measure separates them. What distinguishes them
   is whether the thin direction is shared with the neighbours and with the physics, which is
   intent, not geometry. Hence `AnisotropicCount` sits beside the stretched quality rather
   than instead of it, and the layered mesher's own `ElementCount` is what to check it against.

**The frozen interface is the consequence to plan around.** Once the stack has elements
against its inner face, optional refinement must not insert a vertex there, so Ruppert's
encroachment rule blocks interior points inside those triangles' diametral balls — which on a
plain two-triangles-per-face box is the whole interior. That is not a defect but the standing
rule of boundary-layer meshing showing through: **the surface mesh sets the layer's in-plane
element size**. It is reported (`RefinementBlockedByFrozenBoundary`) rather than left to be
discovered. Note which refinement paths are frozen and which are not: the two OPTIONAL ones
are (both merely decline, and the loop is still bounded without them), while boundary RECOVERY
is deliberately left free — if a frozen patch must be split for the mesh to conform at all,
that is a real failure and the layer's own interface check names it better than a refusal to
try would.

**Which net catches which failure was worth measuring.** Three refusals sit in front of ever
producing an inverted element: a per-facet fold test, a trimmed-face inversion test, and the
leftover volume going non-positive; behind them is a global self-intersection test
(`MeshIntersection.WithinItself`). **Nothing reachable from a real body exercises that last
one**, and the reason is structural: two flat walls closing on each other never *cross* — they
swap places, and two parallel sheets that have swapped places are still parallel — so the face
between them turns inside out first; and where a wall is curved enough to make the offsets
genuinely cross, its facets fold before they get there. It stays as the backstop, with the
verdict locked by test — the `TrimmedFaceRefusalTests` pattern.

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

**`AnalysisBody`: the material-per-region seam is a LIST, and the list is the point.**
`TetMesher` tags each element with the index of the body it filled; `SetMaterial(region,
material)` assigns a material to a region id. Those two agree only while two separately
written lists stay in the same order, which is a convention rather than a fact — so one
`AnalysisBody` list (surface, material, name) now drives both, and `StructuralModel.For` /
`ThermalModel.For` read exactly what `TetMesher.Mesh` read. It is a list rather than a
method on `Part` for a layering reason: this project depends on Core and Mesh and *nothing
depends on it*, which is what keeps a simulation stack out of every modelling consumer, so
it cannot see a `Part` at all. What the two layers genuinely share is `Material` — which is
precisely why that type lives in Core (§2) — and `HalfEdgeMesh`, and a body is those two
together, so `parts.Select(p => new AnalysisBody(p.GetMesh(), p.Material, p.Name))` is the
whole bridge. A null material stays legal on a body (meshing needs none, and it may have
come straight from an unstated `Part.Material`) and is refused at the model, by name — the
same "refuse where the requirement is" rule the optional analysis properties follow.

**Verifying it needed a case whose answer is EXACT, and the exactness came from nu = 0.**
A bar of two materials in series under axial load has δ = (F/A)(L₁/E₁ + L₂/E₂) — but only
in one dimension. In 3D the two halves want to contract laterally by different amounts
(εₗ = −ν·σ/E, and E differs), so a nonzero Poisson's ratio puts a boundary layer at the
interface and there is no closed form left to check. **With ν = 0 in both halves there is no
lateral coupling at all**: the exact solution is a piecewise-linear axial field with
traction-free sides, which is *in* the linear-tet space, so the solve reproduces it to
round-off at any density rather than converging onto it. The thermal twin is the same
statement about a piecewise-linear temperature through two conductivities. And **the
assertion that has teeth is the INTERFACE value, not the total** — swapping the two
materials leaves the tip deflection and the through-flux unchanged, because series
resistances commute, so a test asserting only the total agrees just as happily with the
regions the wrong way round.

**Bodies must be DISJOINT, and the refusal is the honest answer rather than a missing
weld.** Two bodies mating along a face — the natural way to draw a bi-material part — share
vertices, which used to surface deep in `DelaunayTetrahedralization` as "points N and M are
exactly coincident; weld the input surface", true and unhelpful, since welding is exactly
what must not happen. Welding *would* make the input tetrahedralizable and the result would
look right and be wrong: `OffendingFaces` treats every inside-to-inside face as interior, so
an inter-body face is never recovered onto the input plane, and a tetrahedron straddling the
interface takes ONE region for its whole volume. The material boundary would then be a
jagged surface of the mesher's choosing rather than the plane the design drew — a different
geometry, not a coarser one. A conforming multi-material mesh needs the inter-body face
treated as a constrained boundary *and* a decision about whether a facet selector may name
it (it is visited from both sides, so a pressure applied there would double-count). That is
a feature; `TetMesher` now refuses mating bodies up front, naming the shared vertex.

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

### Flux at a material interface, and why this was the smaller half of §3i's job

`q = -k·grad T` is discontinuous at a bonded interface in exactly the way stress is, and for
the mirror-image reason: temperature is continuous, so the *tangential* gradient is continuous
and the tangential flux jumps with `k`, while the *normal* component is continuous by
conservation. `ThermalResults.NodalFlux` is indexed by node, so at such a node it holds one
value where the physics has two. The fix is §3i's verbatim — `ComputeNodalFlux` takes the same
`perRegion` parameter `ComputeNodalStress` does, accumulating into `AnalysisMesh`'s existing
(node, region) slots, and `NodalFluxIn(region, node)` is the honest value while `NodalFlux`
keeps blending, reports the blending through `InterfaceNodeCount`, and stays what a colour map
reads.

**The two claims the backlog made about the size of the job both hold, and the first one is
the interesting one.** There is no recovery on the thermal path at all — nothing fits a
polynomial over a patch — so nothing here has REACH, which was §3i's whole argument for
bothering: a cross-interface *patch* corrupts nodes a full element layer inside each material,
where a cross-interface *average* touches only the shared node. Every downstream consumer was
checked rather than assumed: `SampleOnto`/`Fields`/`WriteVtu` read the nodal array,
`ElementFlux` is per element, `TemperatureIn` and `StructuralModel.ThermalLoad` read
TEMPERATURE, which is continuous. So the defect is confined to interface nodes and the fix is
one accessor. Second claim: the slot table already existed, so no new machinery.

**The oracle needed the same care and lands the other way round from the structural one.** In
SERIES — the interface perpendicular to the flow, which is what this project's recorded
two-material thermal fixture builds — the flux is purely normal, so both materials carry the
*same vector* (measured 200 mW/mm² through both, with the interface at exactly 80 K) and any
averaging reproduces it: measured, the two accessors differ by **1.6e-11 of 2000**. Note this
is the exact complement of the structural case, where series makes the *stress* continuous and
the *strain* jump; here series makes the *flux* continuous and the *gradient* jump. The
arrangement that can see it is **parallel** — the interface CONTAINING the flow — where both
materials span the same length under the same end temperatures, so both carry the same gradient
and the flux jumps with `k`. `T = T_hot·(1 - x/L)` is then the exact solution with nothing
manufactured: linear (so every element order reproduces it exactly), divergence-free in each
material, and with `q·n = 0` on both sides of the interface, so the interface condition holds
identically. Measured on a 4 mm cube at `k` = 200 and 50, exact flux 5000 and 1250 mW/mm²: the
per-region value is exact to **4.2e-15 (linear) / 2.1e-14 (quadratic)** at every one of the
150/810 (node, region) slots, while the one value the node-indexed field reports is wrong by
**75% to 225%** for one of the two materials at every interface node.

**And §3i's fixture trap recurs verbatim**, which is why it is measured here rather than
restated: prescribing that linear field on the whole boundary of a SERIES bar imposes the same
gradient on both halves — the parallel condition wearing the series geometry — and the
mis-loaded bar duly reports a **75.0% jump**, i.e. it would look like it was proving the rule
while measuring its own boundary data. The series control is therefore driven by end
temperatures only, with the other four faces adiabatic, which is what the series arrangement
*is*.

Neutrality and reachability are pinned exactly as §3i's are: a single-region mesh computes the
two arrays independently through an internal `AveragedFlux(bool)` seam and they agree bit for
bit at both element orders, and a two-body `TetMesher` mesh has `InterfaceNodeCount == 0` with
both accessors returning the same bits at every node (`ThermalInterfaceFluxTests`).

### Superconvergent flux recovery, which is the same machinery at three components

The half §3d left open — whether the thermal side wants a *superconvergent* recovery of its own
— landed, and it is the SAME machinery §3i runs, not a parallel one. `q = -k·grad T` is one
derivative down from temperature exactly as stress is from displacement, so a flux recovery is a
smaller version of a stress recovery: three components rather than six, over the same
`(node, region)` slot table, with the same region rule and the same boundary fill.
`FluxRecovery.Direct` / `Superconvergent` on `ThermalResults` is the twin of
`StructuralResults.Recovery`, `Direct` the default for §3i's reasons.

**What made it one machinery rather than two copies is a generic over the value type.**
`SuperconvergentRecovery.Recover<TValue, TField>` accumulates into a `TValue` (`SymmetricTensor3`
for stress, `Vector3d` for flux) through an internal `IRecoveryField<TValue>` that provides the
sampling and the value arithmetic (`Add`, `Scale`, `Zero`, `FromComponents`), and a `struct`
field constraint monomorphizes it so each instantiation compiles to the same operations in the
same order. That is what keeps the structural path **bit-identical** — the stress instantiation's
`field.Add(a, b)` is `a + b` and `field.Scale(a, s)` is `a * s`, so the generic IL is the
hand-written recovery's IL — which is asserted the way the recovery's own tests already assert
it (exact recovery to 1e-9, the switch-back bit-for-bit, the convergence rates), all unmoved.

**The convergence table it had to earn.** On the same manufactured conduction solution (a cubic
temperature, so a quadratic flux, so neither element order reproduces it), the recovered flux
converges one order faster than the averaged one: measured rates **2.34 (linear) and 2.66
(quadratic)** against direct evaluation's 1.43 and 2.00, **15.3× / 7.8×** lower nodal error at
the finest mesh, and an effectivity index approaching 1 (0.979 → 0.998 from below for linear,
1.035 → 1.020 from above for quadratic). The quadratic 2.66 sits below theory 3 for the same
reason the structural 2.76 does — the p+1 cap of §3i, whose interior sub-domain study now
measures the boundary fill as the dominant cause. `ThermalResults.ErrorEstimate` comes with it,
the ZZ figure with the compliance replaced by the scalar thermal `1/k` (which is `k·|grad T|²`
by another name), NaN where no patch can be assembled (`ThermalFluxRecoveryTests`).

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

### Directional conductivity is `ElasticLaw`'s thermal twin, beside `Material` for §3h's reason

A conduction solve read `Material.ThermalConductivity`, a scalar, so a carbon laminate strained
orthotropically in a structural solve (§3h) and conducted isotropically in a thermal one — the
two halves of one part disagreeing about what it is made of. `ConductivityLaw` closes it: a
symmetric positive-definite 3x3 conductivity TENSOR with a material frame, set per region with
`ThermalModel.SetConductivity`, so the heat flux is `q = -K·grad T`.

**The type boundary is §3h's, applied verbatim, and the SEPARATE-type decision was the repo
owner's.** A conductivity needs a frame, a frame is a property of how the stuff was laid into
*this part* rather than of the stuff, so it is per-region analysis data that composes with a
`Material` rather than widening it — the density a modal solve integrates is still the density a
BOM weighs the part with. And it is a distinct type from `ElasticLaw`, not a combined
`MaterialLaw` carrying both halves: the two laws share the frame CONCEPT but not a frame VALUE (a
laminate's conduction axes need not be its stiffness axes), so one object carrying both would
make the name a claim it cannot keep, while a second per-region dictionary that a
`SetConductivity` fills is exactly the shape `SetElasticity` already established.

**The rotation is SIMPLER than the elastic one, and that is the whole of what differs.** A
conductivity is an ordinary rank-2 tensor relating two vectors (flux and gradient), each of
which transforms by R, so the tensor transforms by the congruence `K_global = R·K·Rᵀ` — no Voigt
vector, no engineering-shear convention, none of the trap the elastic 6x6 Bond transformation
carries. It is stored rotated into global coordinates and `ThermalModel` caches the resolved law
per REGION, so the rotation is paid once per model, the same "invert the basis once" call
`ElasticLaw` makes.

**The isotropic path is untouched, bit for bit, and everything reads the law now.** Every
consumer branches on `ConductivityLaw.IsIsotropic` (only `FromMaterial` sets it): the conductance
assembly delegates to the scalar `k·∫grad N·grad N`, the element flux to `grad·-k`, the averaged
and superconvergent recoveries and the `ErrorEstimate`'s complementary energy norm `q·K⁻¹·q` to
their `|q|²/k` forms — so an isotropic model assembles and post-processes exactly the arithmetic
it always did (asserted as a bitwise element comparison and a bit-identical whole solve; the
general tensor path is *separately* asserted to agree with the scalar form to round-off on a
`k·I` tensor). Positive definiteness is a Cholesky naming the failing minor and symmetry is
refused by name (Onsager reciprocity), while `FromMaterial` deliberately does not check — a
zero-conductivity document material is legal — so that refusal stays at the solve where it names
the property.

**Verified against closed forms, exactly, and the oracle shares no line with the production
congruence.** A uniform gradient prescribed on a bar's whole boundary is a constant-gradient
field in the linear-tet space, so the off-axis effective conductivity `kx·cos²θ + ky·sin²θ` is
reproduced to round-off (40 / 31.25 / 22.5 / 5 at 0/30/45/90°), and the CROSS-CONDUCTION — the
flux carries a component across the imposed gradient (q_y = −37.9 / −43.8 at 30/45°, exactly zero
on-axis) — is the one behaviour no isotropic law can produce and the one a transposed congruence
would lose while leaving the axial conductivity intact, the exact thermal analogue of §3h's
shear-extension coupling. The oracle rotates the gradient into the material frame, applies the
diagonal K, and rotates the flux back — three-by-three matrices sharing nothing with `R·K·Rᵀ` but
the physics. A manufactured-solution study with a fully off-diagonal rotated tensor keeps the
orders unchanged (linear 2.01/1.01, quadratic 3.04/2.02 in L2 and energy) — a constant
directional conductivity changes the field, not the element order.

## 3e. Modal analysis (`EngrCAD.Fea`)

`K·phi = lambda·M·phi` over the same `AnalysisMesh`, materials and supports a
`StructuralModel` carries. Four decisions were made here and each is a departure from the
obvious.

**The mass matrix is the thermal capacity matrix under another name**, both being
`integral(constant·N_i·N_j dV)`, so `TetElement.ConsistentMass` is the single implementation
and `ThermalElement.Capacity` delegates to it; the structural assembly replicates each scalar
entry onto the 3x3 identity block, since an isotropic inertia couples no two axes. What that
sharing buys is the quadrature rule: `TetQuadrature.ForMass` is **two degrees above**
`TetQuadrature.For`, and it is now impossible for the two physics to disagree about it. The
reason to care is that under-integrating is *silent* — an n-point rule gives a matrix of rank
n (1 of 4 for a linear element, 4 of 10 for a quadratic one, singular either way) while
`sum_ij N_i N_j = 1` exactly, so the total is still exactly `rho·V` and the obvious sanity
check passes. The control that has teeth is the **rotational inertia**, compared against
`MeshMassProperties`' closed-form polyhedral moments in another project: 2.2e-16…9.4e-15
relative for the production rule against **−2.4e-27 for a true 1.4e-10** for the cheap one.

**Lumping is offered only in the form that is not wrong.** Row-sum lumping is refused by name
for 10-node elements, whose row sums are `−V/20` at every corner — a negative mass, the same
integral that already makes a quadratic element's consistent gravity load negative there.
`MassLumping.Hrz` scales the consistent matrix's strictly positive diagonal to preserve the
element mass, works at both orders, and coincides with row-sum exactly on a 4-node tet. The
reason to offer any lumping is that consistent and lumped **bracket** the answer (+0.134% /
−0.186% on a 16-element bar), not that either is better.

**The eigensolver is shift-and-invert Lanczos, and this is where a direct factorization
finally amortises.** §3c records honestly that "factor once, solve many right-hand sides"
does not apply to the static solver, which factors and discards; here one factorization of
`K − sigma·M` serves one back-substitution per Lanczos step (18–23 of them for three to eight
modes on this project's fixtures), so the exact default is also the only practical one and no
iterative linear solver is offered on this path. Three rules make it trustworthy. Full
reorthogonalization stops round-off manufacturing ghost eigenvalues, but it also makes a
genuine multiplicity invisible — a single-vector Krylov space holds one vector per eigenspace,
and a square shaft's two identical bending modes are a common real case — so converged modes
are **locked into the deflation set and the run restarted**, and the solver targets one more
mode than asked for so a missed copy has a run to appear in. Acceptance is a **contiguous
converged prefix**, never "whatever has converged": Ritz values converge from the extreme end
inwards but not in lock step, and accepting out of order returned 4 997.9 Hz for a first
bending mode of 834.9 Hz. And convergence is **measured** (`K phi − lambda M phi`) rather than
taken from the `beta_m·|y_m|` bound, which describes the shifted, inverted operator's residual
— one transformation away from what a caller cares about.

**Multiplicity three and above is BLOCK Lanczos (`ModalSolveOptions.BlockSize`, buckling
likewise), and building it produced three measured findings.** The scalar machinery's limits
were measured for the first time on synthetic exact-multiplicity pencils (no mesh fixture can
carry one — real meshes SPLIT their theoretical multiplicities, this project's beam pairs by
0.04–0.13%): lock-and-restart plus the one-extra target recovers an exact DOUBLE — the
recorded claim, previously reasoned, now measured — and stops exactly one copy short of a
TRIPLE, returning `{1, 1, 2}` for a truth of `{1, 1, 1}` with every returned pair a genuine
eigenpair, which is why nothing inside the iteration can notice. A block of size b carries up
to b vectors of each eigenspace in its start block's exact-arithmetic span, so a multiplicity
up to b is recovered by construction; b = 1 takes the incumbent scalar path byte for byte
(the neutrality rule). The findings: **(a) an unconverged COPY hides high in the spectrum,
where the contiguous prefix cannot shield it** — its Rayleigh quotient mixes toward the
complement, so it surfaces beyond the next DISTINCT eigenvalues, the walk accepts those, and
a +1 target fills while a copy is still buried (a block of four on a quadruple stopped at
`{1, 1, 1, 2}` believing itself done, four iterations short of its own budget) — hence the
extra targeting scales with the block: one extra per vector advanced per step, which reduces
to the incumbent +1 at b = 1. **(b) A block start must not come from the scalar pattern
family**: the pattern is piecewise AFFINE in the index with one shared slope, so any few
consecutive components of every seed lie in span{constant, ramp} plus a wrap jump, and a
block's projections onto a coordinate-concentrated eigenspace are structurally near
rank-deficient however the seeds are chosen — block starts use a 64-bit LCG stream instead,
deterministic and well mixed. **(c) The stream must be CENTERED**: a first draft mapped
components to [0.5, 1.5) so none could vanish, which put every column near the all-ones
direction (pairwise cosine ~0.9) and starved the block's later principal directions — the
quadruple's fourth copy started with a factor-20 component penalty and missed the tolerance
at the run cap. Orthogonality to an eigenvector, the thing a start vector must actually
avoid, is measure-zero for a mixed stream either way. Rank deficiency in the block QR is a
BREAKDOWN (return what converged, restart), not an adaptive block-shrink — restarting is
slower, never wrong, and the refinement waits for a fixture that wants it.

**Rigid-body modes are separated, not refused.** The static solver refuses an unrestrained
body because its answer is not unique; a modal analysis of one is well posed and its six
zero-frequency modes are part of the answer. `RigidBodyModes.Surviving` was extracted from
`StructuralSolver` so that one computation serves both — a refusal and a mode listing cannot
then describe the same physics differently. They are deflated out of the Krylov space,
`VibrationMode.Number` starts at 1 on the lowest mode that stores strain energy, and their
`Eigenvalue` is reported as the *measured* Rayleigh quotient of the exact rigid field: zero in
exact arithmetic, and in practice a conditioning measurement of that model (2.4e-12 of the
first elastic eigenvalue on the free-free beam). Because `K` is singular when they exist the
factorization takes a small negative shift, reported and escalated on failure; a fully
restrained model's shift is **exactly zero** and its factorization is literally the static
solver's.

A mode shape has no amplitude and no sign, so both are conventions stated rather than assumed:
`Shape` is mass-normalised (the scale the modal identities need, and *not* a displacement),
the published `MeshField` is rescaled to a peak nodal magnitude of exactly 1 model unit and
labelled `"mode shape"`, and the sign is pinned by making the largest component positive so
two solves agree bit for bit.

## 3f. Buckling, stress stiffening and frequency response (`EngrCAD.Fea`)

Linear buckling is `(K + lambda·Kg) phi = 0` with `Kg` the geometric stiffness a prior static
solve's stress field produces. The element matrix is the easy half and the eigensolver is the
interesting one.

**The geometric stiffness has the consistent mass matrix's shape, and that is a fact about the
physics.** `Kg_ab = integral(grad N_a · sigma · grad N_b dV)` is a SCALAR per node pair
replicated onto the 3x3 identity block — exactly `TetElement.ConsistentMass`'s structure —
because an isotropic inertia couples no two axes and one stress tensor contracts each of the
three displacement components identically. So the assembly loop is the mass matrix's with a
different integral in it, the rule selector `TetQuadrature.ForGeometric` joins `For` and
`ForMass` at degree `3(p-1)` (between their `2(p-1)` and `2p`), and the prestress is read
through `StructuralResults`' own recovery seam rather than through a constitutive law restated
here — which is what makes thermal buckling right rather than nearly right. `Degree3`'s
negative centroid weight, a defect for a matrix that must be positive definite, is harmless for
one that is indefinite by nature; exactness is all that is required and it is checked against
the 15-point degree-5 rule.

### The indefinite right-hand side changes the shift STRATEGY, not the shift

§3e's modal solver puts a small NEGATIVE shift under the spectrum so `K − sigma·M = K + |sigma|·M`
is positive definite and Cholesky-factorable. That works for one reason only: `M` is positive
semi-definite, so adding a positive multiple of it can only help. **`Kg` is indefinite** —
tension stiffens, compression softens, and one bending prestress field does both at once — so
`K − sigma·Kg` is a definite matrix plus a *signed* multiple of an indefinite one, and no sign
of sigma makes it reliably factorable. The modal escalation loop is worse than inapplicable:
multiplying a failing shift by 100 pushes `K − sigma·Kg` FURTHER from definiteness, so a solver
reusing it would spend its retries getting steadily more wrong before refusing.

**The free parameter is not the shift; it is the metric.** Lanczos requires only that its inner
product be positive definite, and `A^-1 B` is self-adjoint in the A inner product AND in the B
inner product whenever A and B are symmetric — `<A^-1 Bx, y>_A = x'By = <x, A^-1 By>_A`, and
the same in B. A modal solve runs in M's because M is the definite matrix there. In buckling the
definite matrix is **K** — an unrestrained body is refused up front through the same
`RigidBodyModes` computation the static solver refuses on, and the factorization of K itself is
what establishes the rest, refusing by name when it will not go through. So the iteration runs
in the **K inner product** with the operator `K^-1(-Kg)`, and the matrix that gets factorized is
**K itself** — the same matrix a direct static solve of the model factors, on every model, with
nothing to choose.

**And once the metric is K the shift has no work left to do.** Shift-and-invert exists to turn
INTERIOR eigenvalues into extreme ones; the reciprocal substitution has already done it. The
operator's eigenvalues are `theta = 1/lambda`, so the smallest critical load factor — the only
one an engineer wants — is the operator's LARGEST eigenvalue, which is what Lanczos converges
to first with no transformation at all. `sigma = 0` is the answer rather than a fallback, and
`BucklingSolveReport.Shift` exists to say so. ARPACK reaches the same place from the other side:
its "buckling mode" uses `OP = (A − sigma·M)^-1 A` with metric `B = A` and REQUIRES `sigma != 0`,
because at zero that operator is the identity — inverting the other matrix removes the
requirement along with the choice.

`LanczosEigen` therefore took exactly one generalization: **the metric became a parameter
separate from the right-hand matrix.** Passing the same matrix for both takes the modal path's
arithmetic operation for operation (the two products alias one buffer), so its output is
unchanged to the bit — the same neutrality rule every optional feature here carries.

Two consequences are stated rather than discovered. **Only positive factors are reported**: a
negative theta is a factor at which the *reversed* load case buckles, so the descending-theta
walk stops at the first non-positive value rather than skipping it — §3e's contiguous-prefix
rule, applied to a different family. And **the indefiniteness caveat is about the general case,
not about a column**: under uniform uniaxial compression the element integral collapses to
`u'Kg u = -s·integral(|du/dx|²)`, non-positive for every displacement field, so `-Kg` is
positive semi-definite and the pencil is definite. That is why the Euler cases converge as
cleanly as they do; the machinery above is what makes a bending or mixed prestress work too.

### The residual floor, and a default chosen from a measurement

`BucklingSolveOptions.Tolerance` defaults to **1e-7**, two decades looser than the modal
solver's, and the reason is structural rather than cautious. The measured residual is a TOTAL
cancellation of `K phi` against `lambda Kg phi`, so its floor is about `eps·kappa(K)` relative
to a smooth mode's own energy. A modal solve escapes it because its Krylov vectors come out of
`K^-1 M`, which SMOOTHS — the high-frequency content `K` amplifies is suppressed before `K`
ever sees it. This operator is `K^-1 Kg`, and a geometric stiffness is derivative-like, so the
two halves roughly cancel in frequency content and the Lanczos vectors keep the part that sets
the floor. Measured on a slender column: every mesh up to 9 310 DOF reaches 1e-10 and a
23 166-DOF one stalls at 1.76e-9, so a 1e-9 default would refuse an ordinary refinement of an
ordinary model. Nothing is given up — an eigenvalue is accurate to roughly the SQUARE of the
residual over the spectral gap, and that same column accepted at 1e-5 returns 15 437.12 N
against a finer mesh's 15 437.99 N.

**Whether a better residual MEASURE removes the floor was an open question, and the answer is
measured: half yes, and it does not matter — because a measure that escapes the cancellation
also stops measuring.** The candidate was the K^-1 norm, `|K^-1(K phi - lambda Kg phi)|/|phi|`
— one extra back-substitution through the factorization that already exists — and it does
escape: on the same 23 166-DOF column the standard measure stalls at 1.9e-9 while the K^-1
measure of the SAME vector reads 8.2e-11, and where the standard floor grows with `kappa(K)`
across refinements (3.1e-10 / 9.1e-10 / 1.9e-9 at 3 550 / 10 486 / 23 166 DOF) the K^-1
figure sits near 1e-10 at every one (5.5e-11 / 9.1e-11 / 8.2e-11). The structural reason is
also the disqualification: `K^-1·r = -lambda·(T phi - theta phi)` is exactly the shift-invert
OPERATOR's residual — the quantity the textbook `beta·|y|` bound describes and the acceptance
test deliberately declined — and full reorthogonalization drives it to round-off within a
dozen steps, after which it reads the back-substitution's noise rather than the pair's
quality: at 12 Lanczos steps it already sits at 8.9e-11 while the standard measure still
reads **1.2e-7**, and it does not move again through 120 steps. An acceptance measure that
saturates before convergence cannot distinguish a 1e-7-grade vector from a 1e-9-grade one,
which is worse than a looser measure that still measures. And nothing was on the table: the
eigenvalue drift between the earliest acceptance and the most converged one is
1e-15…4.8e-13 relative on all three meshes — the residual-squared-over-gap claim, confirmed —
so the 1e-7 default stands as chosen and the reported residual keeps its honest
backward-error meaning. (`FeaBenchmark.WhetherAKInverseNormResidualEscapesTheBucklingFloor`;
the historical 1.76e-9 reads 1.87e-9 on today's code — same fixture, summation orders have
moved since.)

**An empty eigen-result has two unrelated causes**, and an empty list cannot tell them apart:
either the spectrum genuinely holds nothing wanted, or a candidate was there and the tolerance
was in the way. They want opposite responses from a user, so `LanczosEigen` now reports the best
candidate it saw but did not accept, and both solvers' refusals name which case they hit.

### Damping is a per-mode RATIO, and no damping matrix is ever assembled

`C = alpha·M + beta·K` is offered because it is the case in which the UNDAMPED modes still
diagonalise C: `phi' C phi = diag(alpha + beta·omega_n²)`, so the equations separate and
`zeta_n = alpha/(2·omega_n) + beta·omega_n/2` is the whole of what any consumer needs. Forming
`C` in order to project it back down to those numbers would be arithmetic with nothing to show
for it, so the MODAL route needs no damping matrix and that is a design statement. (This
paragraph used to carry a prediction that direct time integration would be "the one future
consumer that genuinely wants the matrix". For the proportional RUN OPTION it does not — §3g
says why. But the non-proportional damping a MODEL carries has no per-mode form, so it is a
matrix wherever it is consumed: `FeaAssembly.Damping` is the one place it is built, now factored
by §3f's direct harmonic solve AND folded into §3g's transient effective stiffness.)

**What proportional damping excludes is stated precisely rather than implied away**: a discrete
dashpot, two materials with different loss factors in one model, viscoelasticity, joint friction
and hysteretic damping all leave `phi' C phi` with off-diagonal terms, at which point the damped
modes are no longer the undamped ones and `(lambda²M + lambda·C + K)phi = 0` is a QUADRATIC
eigenproblem — complex eigenvalues and eigenvectors, solved by a `2n` state-space linearization
in a non-symmetric matrix pair. Neither `SparseCholesky` nor `LanczosEigen` applies. It is a
different solver, not a bigger version of this one.

### Frequency response: modal superposition first, and why the direct solve is a different question

Each mode is a scalar oscillator, so `q_n(W) = F_n / (w_n² − W² + 2i·zeta_n·w_n·W)` and a whole
sweep is one dot product per mode plus a complex division per (mode, frequency) pair — nothing
assembled, nothing factorized. The DIRECT alternative factorizes `(K − W²M + i·W·C)` per
frequency and is the only option in three cases, none of which this can express: damping that is
not proportional, material properties that vary WITH frequency (the modal basis itself would
change per point), and a load whose spatial distribution changes with frequency. It is a second
METHOD rather than a better one, and it is now built — the next subsection.

### The direct per-frequency solve, and where the damping vocabulary had to live

`DirectHarmonicSolver` factors `(K − W²M + i·W·C)` at every sweep point over Core's
`SparseLdlt` (complex symmetric LDLᵀ — built for exactly this system; its class doc records
why the complex spelling beat a real Bunch–Kaufman). Three decisions are worth recording.

**The damping lives on the MODEL, and that is the deliverable rather than a convenience.**
The modal route's damping is a per-mode RATIO (a property of a modal basis) and the
transient's a run option; what neither can carry is damping attached to GEOMETRY — a
dashpot at a node, a lossier region — which is precisely the case the direct solve exists
for. So `StructuralModel` gained the vocabulary (`SetDamping` model-wide and per region,
`Dashpot` grounded and node-to-node), the direct solver reads ONLY the model, and the two
solvers that cannot integrate a model-carried C — the modal harmonic route and the
transient — REFUSE a model that carries one, naming `DirectHarmonicSolver`, rather than
silently ignoring it: one statement per model, and no consumer that quietly drops it. A
node-pair dashpot's coupling block can land where the stiffness pattern has no entry at all
(two nodes sharing no element), which is the union-pattern case `SparseLdlt` factors and
the test fixture exercises deliberately.

**The "no damping matrix" statement gained its one exception, and the exception proves the
rule.** §3g's finding — everywhere a C's uses are products or scalar folds, assembling it
buys a slower operation — stands untouched. This factorization is different in KIND: it
consumes the VALUES of `i·omega·C` as a matrix, there is no product to decompose into, and
a non-proportional C is data rather than a projection of ratios. So `FeaAssembly.Damping`
is the one place a viscous damping matrix is BUILT, per-element (`sum_e alpha_e·M_e +
beta_e·K_e` + dashpot blocks) through ONE assembly path — which is what makes "every region
states the same value" bit-identical to "the default states it once", asserted rather than
hoped — and it is now consumed twice (this solver, and §3g's transient).

**Hysteretic (structural) damping is the second model the imaginary part carries, and it is
this solver's alone.** A loss factor `eta` enters the steady-state equation as a
frequency-INDEPENDENT imaginary stiffness `i·eta·K` — the complex modulus `K(1 + i·eta)` —
where a viscous `C` enters as `i·omega·C`, rising with frequency. That is why it lives on the
direct solve and nowhere else: it has **no causal time-domain form** (a constant-magnitude
imaginary stiffness cannot be written as any `M·a + C·v + K·u`, so the transient refuses it) and
**no per-mode real ratio off resonance** (the modal route refuses it). The vocabulary is a
SEPARATE `SetLossFactor` rather than a `SetDamping` overload, because a loss factor is
dimensionless where a viscous coefficient is N·s/mm and one method cannot mean both; the two
compose additively, the imaginary impedance being `omega·C + eta·K`. `FeaAssembly.HystereticStiffness`
assembles `sum_e eta_e·K_e` — the K assembly scaled per element by the region's loss factor — so
its sparsity is K's exactly and it factors beside `K − omega²·M` with no wider pattern. **The
verification is the scalar fixture and it has three teeth.** At a mode's resonance the response
amplifies by exactly `1/eta` (25 for eta = 0.04, against `1/(2·zeta)` viscous — so `eta = 2·zeta`
matches the peak and nothing else), and the factorization does NOT refuse there, because `eta·K`
is the imaginary part keeping the pivot away from zero: a hysteretically-damped structure has a
steady state even at its own natural frequency, which is the whole reason to model it. A sweep
against `f/sqrt((k − omega²m)² + (eta·k)²)` agrees to **3e-16** — the statement that the
imaginary part stayed CONSTANT rather than scaling with frequency, which is what a viscous term
substituted by mistake would fail everywhere but at the tuning point. And a hysteretic model and
a viscous one tuned to the SAME resonant peak CROSS OVER off resonance (hysteretic smaller below,
larger above, because `eta·k` exceeds the shrinking `omega·c` below resonance and falls short
above) — a mutation a viscous-term-in-disguise could not produce. One caveat is left as the
model's own answer rather than special-cased: at `omega = 0` the complex modulus makes a "static"
response slightly complex (magnitude `1/sqrt(1 + eta²)` of the true static), the well-known DC
anomaly of hysteretic damping, negligible for the loss factors it is used at.

**An undamped model is accepted, and the refusal at an exact resonance is the physics.**
The modal route requires an explicit damping statement because `None` makes every resonance
infinite; here the model's damping state IS the statement, an undamped sweep is the
standard real FRF, and an undamped model driven exactly at a natural frequency hits
`SparseLdlt`'s exactly-zero pivot — wrapped naming the frequency and the fact that no
steady state exists there (the response grows linearly in time forever). Reaching that
refusal in a test took reproducing the solver's own pivot arithmetic VERBATIM — the
assembled matrix entries through the hertz→omega→coefficient chain, scanned over a family
of fixtures because one ulp of hertz moves the pivot by ~5–9 ulps of the stiffness (the
measured stiffness `probe/deflection`, ulps away from the assembled entry, found no refusal
at all: the recomputation-must-be-bit-reproducible lesson, in a test fixture). Verification
is three-way: agreement with the corrected modal route at 3.5e-6 relative on a real mesh
(the gap IS the truncation), 1e-9 on a 1×1-reduced model whose one mode is the whole basis,
and hand-built complex Cramer oracles for the dashpot and per-region cases — each free DOF
of the per-region fixture its own closed-form oscillator, so a per-region map applied to
the wrong region fails at the right magnitude where any total-only assertion would agree
with the regions swapped.

**Truncation is turned into a correction rather than left as a caveat.** The mode-acceleration
form `u(W) = u_static + sum_n phi_n F_n [1/D_n − 1/w_n²]` has a bracket that vanishes at
`W = 0`, so the response is exactly the static answer there however few modes were kept, and the
missing modes' static flexibility rides along at every other frequency. It costs one static
solve the caller has usually already done. `TruncationError` is NaN without one — because then
it is not small, it is unknown.

**Base (support) excitation is a load-vector spelling, not new mathematics.** A shaker or
seismic input drives the model through its supports, and in RELATIVE coordinates the equation is
`M·u'' + C·u' + K·u = -M·iota_d·a_g`, whose modal force is exactly `-phi_n'·M·iota_d·a_g =
-Gamma_d·a_g` — the participation factor `VibrationMode.ParticipationFactor` already carries. So
`HarmonicSolveOptions.BaseExcitation` reuses the whole modal machinery, replacing the nodal
projection `phi'f` with `-Gamma_d·a_g` and nothing else. Three decisions the item flagged were
all real. **The answer is RELATIVE displacement** (measured from the moving support), which is
the right quantity for STRESS since a rigid ground motion carries none — so `IsRelativeToBase`
names it apart rather than silently changing what `Displacement` means, the absolute value being
the relative one plus the rigid ground field. **The whole base moves TOGETHER** — `iota_d` is a
rigid translation only for a single foundation, so v1 takes the uniform case and states it
(independent support-group motion needs a quasi-static solve per group, which this does not do)
rather than detecting a grouping the model does not carry. And **which quantity is held constant
matters** — an acceleration input has a frequency-independent modal force, a velocity one scales
by `omega` and a displacement one by `omega²` (`a_g = omega·v_g`, `omega²·u_g`), so `BaseMotionKind`
states it and the sweep carries the scaling. The oracle is the SDOF fixture's exact equivalence:
a base acceleration `a_g` produces the same relative response as the inertial nodal force `m·a_g`
(both project to one modal force), measured to 2.2e-16, with the resonant relative displacement
on the closed form `a_g/(2·zeta·omega_n²)`. A model with both nodal forces and base excitation,
or base excitation with a static correction, refuse by name.

Three smaller decisions worth recording. **The load comes from the modal model's own applied
forces**, since every load type reduces to consistent nodal forces when applied, so there is no
second place for a load to be specified and forgotten (a thermal load is refused by name,
because it enters as an element integral and would be silently dropped). **Damping is required
rather than defaulted**, because a default would be this project inventing a material property.
And a **free-free model is refused**: a rigid mode's `F_n/(0 − W²)` grows without bound as the
frequency falls, which is a true statement about a body that accelerates away and a useless one
to plot.

## 3g. Transient dynamics (`EngrCAD.Fea`)

Direct time integration of `M·a + C·v + K·u = f(t)` by the Newmark / HHT-alpha family. The
physics is the structural solver's and the plumbing is the thermal transient's; what is new is a
scheme parameter with two stability conditions in it, an initial condition that has to be
SOLVED, and an energy statement that is an identity rather than a check.

### It is a different question from modal superposition, not a slower answer to the same one

§3f's harmonic solver answers "what does this do under a steady sine at each of these
frequencies" by projecting onto a handful of modes. This answers "what does it do next" for an
arbitrary load history, needs no modes at all, and is the route a nonlinear solve would
eventually wrap — the residual iteration a contact or plasticity model needs goes INSIDE a step
of this loop, and there is nowhere to put it in a modal sum. The price is symmetrical: it
computes every instant whether or not anything is happening, where modal superposition computes
a whole frequency at a time.

### The filed prediction about the damping matrix was wrong, and the correction is the finding

todo.md filed this as "the ONE consumer that genuinely wants `C` as a matrix rather than as
per-mode ratios, so it is where `RayleighDamping` would first be assembled", and §3f repeated
it. Building the solver shows it is false. Under proportional damping every appearance of `C`
is one of exactly two things, and neither wants the matrix:

- a **product**, `C·x = alpha·(M·x) + beta·(K·x)`. Those are two matrix-vector products the
  stepper already performs against matrices it already holds — and CHEAPER than one product
  against an assembled `C` would be, because a mass matrix's node-pair block is a scalar times
  the identity while a stiffness's is full, so `M` has far fewer stored entries than `K` and
  an assembled `C` would inherit `K`'s sparsity;
- a scalar **multiple** folded into the effective stiffness, which collects as
  `[a0 + (1+alpha)·a1·alpha_R]·M + [(1+alpha)(1 + a1·beta_R)]·K` — one `FeaAssembly.Combine`
  with two coefficients, which is why that overload exists.

So forming `C` for the proportional RUN OPTION would cost a third sparse matrix to buy a slower
operation, and it never is. The general shape is worth keeping: **a prediction about what a
future consumer will need is a hypothesis, and the consumer arriving is the experiment.** Four
agents in a row have found a filed diagnosis wrong; this is the fifth.

### But model-carried damping IS the matrix, and the transient now integrates it

The finding above is about the proportional run option. **Non-proportional damping the MODEL
carries — a discrete dashpot, per-region coefficients that differ — has no product form**, so
it is exactly the case that wants the matrix, and the transient assembles it (`FeaAssembly.Damping`,
the one place a `C` is built, now with two consumers: this solver and §3f's direct harmonic).
The total damping is `C = alpha·M + beta·K + C_model` and the two halves are handled differently
on purpose: the proportional halves stay products, while `C_model` folds into the effective
stiffness as `(1+alpha)·a1·C_model` and enters each step's right-hand side and the reaction as
the one `C_model·x` product there is no way around. A model that states no damping assembles
nothing, so the common Rayleigh path is bit-identical to what it always was — the transient's
own refusal of model-carried damping is gone, replaced by integration.

**The verification is the single-degree-of-freedom fixture again, and it earns its keep because
the two paths are different arithmetic.** A grounded dashpot along the one free axis puts `c` on
that DOF alone, so the reduced 1×1 damping is exactly `[c]` and the modal ratio is
`zeta = c/(2·m·omega)` with no fitting; the free-vibration decay lands on the damped closed form
to the trapezoidal rule's own predicted phase error (0.1513% measured against 0.1513% predicted)
and the energy-balance identity — the dissipation `∫v'Cv dt` equalling the energy that left the
system — reads **9.6e-14** with an assembled `C` in it rather than products. The check with the
most teeth is that a UNIFORM Rayleigh damping stated on the MODEL (which assembles the matrix)
reproduces the same statement on the OPTIONS (which takes products) to **2.2e-13** relative:
the general assembled path IS the special product path by another route, which is what makes
the non-proportional feature trustworthy rather than merely plausible.

### The initial acceleration is a solve against M, and assuming it away is a modelling error

`a(0) = M⁻¹(f(0) − C·v(0) − K·u(0))` is the equation of motion at `t = 0` read for the one
quantity the caller does not supply. It is a solve against **M**, not against K, so it is a
SECOND factorization — and the temptation to skip it is real, because `a(0) = 0` is what a body
"starting at rest" sounds like. It is not: a body released from a displaced position, or one
whose load is already on at `t = 0`, is accelerating at that instant. Starting from zero puts a
spurious half-step into the answer whose symptom is a startup wobble that decays, which is
indistinguishable from physics by inspection. It is skipped only when the right-hand side is
EXACTLY zero — an exact-zero test, since the acceleration is a linear image of that vector — and
`Factorizations` reports which happened, so the cost is visible rather than mysterious.

### Several load patterns with independent histories: one factorization, superposed

`LoadFactor` scales one spatial pattern by one law; the case it cannot express is gravity held
constant while a shaker runs, `f(t) = sum_i g_i(t)·f_i`. `LoadPatterns` is a `(model, law)` list,
each pattern's model carrying its own loads and its own time law, all sharing the operator with
the solve model — so `K` is one matrix and the run factors once, exactly `SolveAll`'s contract
(`RequireOneOperator` reused). The single-pattern form IS a one-entry list: `ComputeLoad`
overwrites with the first pattern and adds the rest, so for one pattern it is
`Scale(pattern, law(t))` byte for byte, which is what keeps every incumbent transient number
unchanged (asserted, and the phase-error predictions would have caught a drift). When patterns
are given the solve model provides only the operator and the initial conditions — its own loads
and `LoadFactor` are refused, since the loads live on the patterns and one law spec is enough.
The oracle is LINEARITY: a linear system from rest responds to a sum of loads with the sum of the
responses, so the two-pattern run equals the two single-pattern runs added at every step (7e-14
relative), a mutation a dropped or mis-scaled pattern could not survive.

### Base excitation: the relative formulation, kept over the absolute one because it is measured

A shaker or seismic input drives the model through its supports, and `TransientSolveOptions.BaseMotion`
states it as a ground ACCELERATION `a_g(t)` along a direction. There are two ways to realize it,
and the todo asked which is cleaner:

- **RELATIVE** (kept, public): in relative coordinates `M·u'' + C·u' + K·u = -M·iota_d·a_g(t)`
  with `iota_d` the rigid translation along the base direction — so it is one more load pattern,
  `-M·iota_d` scaled by `a_g`, over the supports left fixed. The answer is RELATIVE displacement
  (`TransientResults.IsRelativeToBase`), the right quantity for stress since a rigid ground
  motion carries none. It needs no per-step operator change and takes the ACCELERATION a seismic
  record already is — no integration.
- **ABSOLUTE** (internal seam `AbsolutePrescribedMotion`): prescribe `iota_d·u_g(t)` at the
  supports and solve for total motion. It recomputes the `-Aeff·u_c` correction each step (the
  effective operator depends on the step, so this genuinely re-does work), needs the seismic
  acceleration DOUBLE-INTEGRATED to a displacement (with the baseline-drift hazard that implies),
  and gives absolute displacement that is only `relative + iota_d·u_g`.

So the relative form is cleaner on three counts (a load pattern rather than a per-step operator
touch, the stress-correct quantity, and the no-integration seismic input), and it is kept. **The
measurement that proves they are the same physics is round-off agreement through the internal
seam**: for an UNDAMPED body (so `C·iota_d = 0`) under average acceleration, substituting
`u_absolute = u_relative + iota_d·u_g` into the absolute discrete equation reduces it to the
relative one exactly, when the relative load uses the same Newmark-consistent ground acceleration
the absolute run produces — so the two integrations agree to **6.1e-12** relative, which is the
"if you offer both formulations they agree to round-off" oracle the todo asked for. Two more
oracles carry it: the transmissibility closed form as an AMPLIFICATION, so the fixture's
participation factor (its consistent-mass inertial load is NOT its reduced mass) cancels — a
single-degree-of-freedom oscillator's steady relative response over its static base deflection is
`1/sqrt((1-r²)² + (2·zeta·r)²)`, measured **1.94403 against 1.94257** (r = 0.7, zeta = 0.05,
0.075%) and the resonant amplification **9.9938 against 1/(2·zeta) = 10** (0.062%); and a ZERO
base motion reproduces a plain run bit for bit (0 differing bits across 401 states). The whole
base moves TOGETHER (independent foundations are a larger construction, stated not detected).

### Adaptive stepping: a small dyadic set, factored per size

The constant step is what lets one factorization serve the run, so the honest adaptive form is
NOT a continuously varying step (which refactors at every change) but a SMALL DYADIC set,
`TimeStep / 2^L` for `L` in `0..Levels-1`, with each size factored at most once and cached
(`SolveAdaptive`). A multi-scale run — a sharp start then a long ring-down — then spends the fine
step only where the local error demands it while paying for at most `Levels` factorizations. The
times run on the finest dyadic grid so the coarse and fine steps interleave and still land on the
endpoint exactly; the step is chosen from a local displacement-error estimate `dt²·|a(n+1) - a(n)|`
against `Tolerance` (over-tolerance rejects and refines; comfortably under coarsens).

**The oracle is fuzzier than a closed form, so it is pinned in two exact parts and one measured
one.** Exact: a single-element size-set (`Levels == 1`) reproduces the constant-step `Solve` run
BIT for bit, which is guaranteed by SHARING the step — the entire per-step body was extracted
into one `NewmarkStep` that both `Solve` (constant coefficients) and `SolveAdaptive` (per-level
coefficients) call, so there is no second spelling of the step arithmetic to drift — with the same
factorization count. Measured: a damped free decay whose amplitude falls ~1000x is matched to the
uniform-fine reference to **0.008%** while taking **816 steps against the fine grid's 1920 (58%
fewer)** and factoring **4 matrices** (three sizes plus the initial-acceleration mass solve) where
a continuously varying step would factor about a thousand — which IS the whole point of caching
per size. The tuning finding worth keeping: the local-error tolerance is ABSOLUTE, so it must be
set to the response's own scale, and the multi-scale split needs damping high enough that the
ring-down genuinely goes quiet (the under-damped fixture oscillates the whole run at the natural
frequency, so even the small-amplitude tail needs steps per period and the saving is modest;
zeta = 0.12 makes the tail quiet and the saving dramatic). A prescribed support motion and an
iterative solve are refused on this path (the first fights per-size caching, the second has no
factorization to reuse).

### An unrestrained body is accepted, where the static solver refuses one

`StructuralSolver` refuses a free body by name and describes the surviving rigid motions,
because `K` alone is singular. The effective stiffness is `a0·M + … + K` with
`a0 = 1/(beta·dt²) > 0`, and a consistent mass matrix is positive definite, so the stepping
matrix is positive definite **whatever the supports do**. A free body under a transient load
flies away, and that is the answer rather than an error. It is the same exemption §3d makes for
an insulated body's transient — in both cases the time-derivative term supplies the definiteness
the steady operator lacks — and in both cases the refusal that remains belongs to the STEADY
problem, where nothing pins the answer's level. The non-refusal is pinned by a test so that
nobody copies the static guard across on the reasonable-looking grounds that the two solvers
share a model type.

### The scheme is one value, and both stability conditions are enforced rather than documented

`TimeIntegration` carries `beta`, `gamma` and `alpha` together. The alternative — an enum beside
two loose doubles — lets a caller name a member and supply coefficients that contradict it, and
leaves the solver to decide which to believe. Every member here is a named factory that computes
its own coefficients, so a scheme is either one of the families or a pair the constructor has
checked against `2·beta >= gamma >= 1/2`. Both halves refuse by name:

- **`gamma < 1/2` has NEGATIVE numerical damping.** The amplitude grows step after step at every
  step size, so it is not an accuracy-for-cost trade but an answer that diverges while looking
  like a resonance.
- **`2·beta < gamma` is conditionally stable.** Central difference and linear acceleration are
  legitimate schemes; what this library cannot do is tell a caller whether their step is inside
  the limit, because that needs the largest eigenvalue of `K·phi = lambda·M·phi`, which nothing
  here computes. Refusing beats returning something that silently explodes.

**Explicit integration is refused for a structural reason rather than for effort.** Its whole
appeal is that a DIAGONAL mass matrix turns a step into a division. This library has no diagonal
mass to offer for the element it recommends: §3e refuses row-sum lumping for 10-node tetrahedra
because the corner row sums are `−V/20`, a negative mass, and HRZ is a scaled approximation
whose error would then be inseparable from the integrator's. An explicit scheme over a
CONSISTENT mass would solve a system every step, which is what explicit integration exists not
to do.

**HHT is the member that buys dissipation without losing an order.** Newmark damps by raising
`gamma`, which unbalances the velocity update and costs the second order outright. HHT keeps the
update relations and instead evaluates the internal and damping forces at a weighted point
between the ends of the step, with the load at the matching instant `t(n+1) + alpha·dt`; the
`gamma` it then chooses is above 1/2, but the weighting cancels the leading error term. At
`alpha = 0` beta = 1/4 and gamma = 1/2 fall out of its own formulas, so it IS the default
member — asserted as a value AND as bit-identical output, which is what every `alpha != 0`
exact-zero branch in the stepper exists to guarantee.

### The energy balance is an identity, which is why it is the strongest lever here

For the trapezoidal member the update relations collapse to
`u(n+1) − u(n) = (dt/2)(v(n) + v(n+1))` and `v(n+1) − v(n) = (dt/2)(a(n) + a(n+1))`, from which

```
E(n+1) − E(n) = (dt/4)·(v(n)+v(n+1))'·[M(a(n)+a(n+1)) + K(u(n)+u(n+1))]
```

and substituting the equation of motion at both ends turns the bracket into exactly the
trapezoidal work of the load minus the trapezoidal viscous dissipation. Nothing is approximated,
damped and loaded cases included, so `EnergyBalanceResidual` is a defect detector rather than a
tolerance — the same status §3d's first law has, reached by the same route. For a dissipative
scheme the identical number becomes the MEASUREMENT instead: it is the energy the algorithm
removed, which is what a user of numerical damping wants reported. One quantity, two readings,
and `IsSecondOrder` / `SpectralRadiusAtInfinity` say which a run is in.

The denominator needs one care the thermal version also needed: a free vibration does no work
and dissipates nothing, so a scale built only from the flows would be zero for exactly the run
that most wants the check. Adding the peak energy makes that case read as the relative energy
drift.

### Two fixture traps, and both were found only because a measured order was impossible

**(a) Where a convergence error is SAMPLED decides the order it reports.** A Newmark run's error
has two components — a phase lag and an amplitude decay — and a single-instant probe sees
whichever one that instant exposes. Read at a whole number of periods the exact cosine is
STATIONARY, so a phase lag `d` enters as `u0·d²/2` rather than `u0·d`: an `O(dt²)` scheme
measured **3.9997** against a theory of 2. Moving the probe to a quarter period fixes that and
breaks the other half — there the amplitude multiplies zero, so the FIRST-ORDER control, whose
entire job is to prove the study can tell 1 from 2, measured **1.344**. The error has to be a
norm over the run on a time grid common to every refinement level. The rule generalises past
this fixture: **a convergence probe measures the error component its sampling point exposes, and
the control that exists to catch a broken study can be broken the same way.**

**(b) HHT's amplification matrix is DEFECTIVE in the high-frequency limit.** A two-point energy
ratio put the spectral radius **4.138% high at every radius** and converged to that offset
rather than drifting — which ruled out "not asymptotic yet" and pointed straight at the
`alpha`-to-`rho` relation, the kind of transcribed formula this project has been caught getting
wrong before. Deriving the limit settles it the other way: eliminating the acceleration leaves a
2x2 block with trace `2 − 4/(1−alpha)` and determinant `((1+alpha)/(1−alpha))²`, and its
discriminant `trace² − 4·det` is **identically zero for every alpha**. So there is a double real
eigenvalue at `−rho_inf` with a single eigenvector, the state decays as `n·rho^n` rather than
`rho^n`, and the energy as `n²·rho^(2n)`. Dividing the `n²` out is the whole correction, and it
is exact: `(30/20)^(2/20) = 1.041380`, the measured offset to six digits. The formula was right
and the estimator was not — which is the same shape as §3e's residual-floor finding, where the
number that looked like a solver defect was a property of the measurement.

### Verified against closed forms with no discretization error at all

The fixture is a finite element model whose reduced system is exactly `1 x 1`: every degree of
freedom restrained but one. That is the idiom §3e records as WRONG in dynamics, and the
distinction is worth stating rather than assuming. That warning is about MODELLING — a
single-node restraint on a real part creates a genuine spurious mode whose frequency belongs to
the mesh rather than to the part. Here nothing is being modelled: the system is made scalar
deliberately so that `m·q'' + c·q' + k·q = f(t)` is not an approximation of the finite element
problem but IS it, which is the only way to check a time integrator against a closed form with
no space discretization in the way. Its stiffness comes from a static solve and its frequency
from a modal solve, so the reference is never the transient's own arithmetic.

The predictions are derived rather than bounded, which is what separates "the integrator works"
from "the integrator is THIS integrator": Newmark's amplification eigenvalues are
`(1 − W²/4 ± iW)/(1 + W²/4)` with `W = omega·dt`, of modulus exactly 1 (hence the preserved
amplitude) and argument `W − W³/12`, so the algorithmic frequency is `omega·(1 − W²/12)` and
the error after N periods is `amplitude·2·pi·N·W²/12`. Measured against that: step
amplification exactly **2.0000**, damped step **1.8544825** against 1.8544679, and the four
phase-error cases within **0.1%** of prediction. The companion check is a single MODE seeded on
a real mesh, where the response provably stays in the mode, leaking **8.5e-11** and matching the
predicted algorithmic frequency ratio to **eleven digits** — a check the transient and the
eigensolver can only both pass if both are right.

## 3h. Directional materials (`EngrCAD.Fea`)

Orthotropic and fully anisotropic elasticity, as `ElasticLaw` — a 6x6 stiffness matrix in
global coordinates plus the thermal strain that goes with it. The mathematics is textbook; the
decisions worth recording are about WHERE the type lives and what it does not do.

### The type boundary is where the frame is, not where the anisotropy is

The obvious move is to widen `Material`, and it is wrong. `Material` is in `EngrCAD.Core`
because the document model needs the same one (§2) — a part says what it is made of, a BOM
weighs it, a viewer takes its colour — and those consumers want a name, a density and a handful
of scalars. But the deciding argument is not "they would not use the extra fields": it is that
**an anisotropic law needs a FRAME, and a frame is not a property of the stuff.** Which way the
fibres run, which way the plate was rolled, which way the layers were printed — that is a
property of how the stuff was laid into *this part*. So it is per-region analysis data, which is
exactly what this project is, and the law COMPOSES with a material rather than replacing one:
the density a modal solve integrates is still the density a BOM weighs the part with.

The same argument settles what the law does NOT carry. Density stays on the `Material`
(`ModalSolver` reads it through `FeaGuards` and knows nothing about laws), because a directional
material has one density; and a law with no stated expansion inherits nothing, because a scalar
coefficient on a directional material is the quiet mismatch the split exists to prevent —
`ThermalLoad` refuses by name and says where the coefficient belongs.

### The frame is applied once, and the isotropic path is untouched bit for bit

D is stored **rotated into global coordinates**, so the element loop never sees a frame and the
laws are derived and cached per REGION — the 6x6 inversion and the rotation are paid once per
model. That is `LoftedSurface`'s "invert the cardinal basis once at construction" call, for the
same reason.

`TetElement.Stiffness` then branches on `IsIsotropic`, which only the `FromMaterial` factory
sets, so a model stating no law assembles through exactly the index-form arithmetic it always
did. That is asserted as a **bitwise** comparison rather than a tolerance, and it is what makes
the feature safe to add under a suite whose headline numbers are quoted to twelve digits. The
general `B'DB` path is then *separately* asserted to agree with the index form to round-off on
an isotropic law, so the two cannot drift in MEANING while staying deliberately separate in
ARITHMETIC — the neutrality rule every optional feature here carries (`LanczosEigen`'s metric
parameter, `SurfaceIntersection`'s second seeding pass, `PolygonFan`'s tie guard). The
thermal-load path makes the identical split for the identical reason, and measured **0.0**
difference on its fixture, which was better than the argument needed.

### The transformation is derived, because the engineering-shear convention is a trap

`D_global = K · D_material · K'` with K the Voigt **stress** transformation. The
K-on-both-sides form (rather than `K D K⁻¹`) follows from the strain vector transforming by
`K⁻ᵀ`, which itself follows from the strain energy `s'e` being invariant. That derivation is
two lines and worth doing, because the engineering-shear convention makes the stress and strain
Voigt vectors transform by DIFFERENT matrices and swapping them is the classic error in this
formula — one that leaves a symmetric, positive-definite, plausible matrix.

Which is also why the verification oracle is built by **3x3 tensor rotation**: rotate the stress
tensor into the material frame, apply the compliance there straight from the engineering
constants, rotate the strain tensor back. No Voigt vector, no engineering shear, no Bond
transformation — two derivations sharing nothing but the physics, where a test re-running the
production rotation would agree with a broken one just as happily. The classical off-axis
modulus formula is then a third reading of the same number, and the **shear-extension coupling**
is asserted separately because it is the one behaviour no isotropic law can produce and the one
a transposed rotation would lose while leaving every symmetric measure intact.

### Positive definiteness is a Cholesky, because the Cholesky IS the statement

The classical orthotropic restrictions — `|nu_ij| < sqrt(E_i / E_j)` plus a determinant
condition coupling all three — are precisely "the compliance matrix is positive definite", and
checking them one at a time re-derives that in a form easy to get subtly wrong. **The pairwise
bounds are not sufficient**, which the fixture pins: three equal moduli at `nu = 0.7` satisfy
every pairwise bound (`sqrt(1) = 1`) and still give a negative determinant. Without the check a
plausible datasheet transcription yields a material that releases energy under some strain, and
the symptom is a factorization failure deep in the solver rather than a message about the
material — the `BrepBoolean.Verified` argument again. The minor Poisson's ratios are DERIVED
from the compliance symmetry rather than accepted as inputs, for the neighbouring reason:
supplying both is how a transcription comes to contradict itself.

One implementation note that cost a debugging round and generalises: the Cholesky is shared
between the 3x3 compliance block (inside a 6x6 array) and the whole 6x6, so it must be TOLD its
stride rather than infer it from the order. Inferring gave a perfectly valid isotropic
compliance a "not positive definite" refusal — a routine reading the right numbers in the wrong
places, which is what a shared helper's implicit assumption looks like from the outside.

### Scope, stated rather than implied

`Materials` gains no composite entries: the catalogue is isotropic engineering metals and
plastics, and a lamina's constants are a layup decision rather than a stock number.

The two things a composite user reaches for next — a **laminate** (a stack of plies at
different angles) and **failure criteria** measured against per-direction allowables — landed
as §3l, on top of this law rather than inside it.

## 3i. Stress recovery and the error estimate (`EngrCAD.Fea`)

Zienkiewicz–Zhu superconvergent patch recovery, opt-in behind `StructuralResults.Recovery`.
The algorithm is standard; what is worth recording is the boundary, which is where the whole
of the difficulty lives, and the honesty rule the estimator needed.

### The estimator is the deliverable; the accuracy is the by-product

todo.md filed this as "a refinement rather than a defect" and then said the interesting part
out loud: *an error estimator is worth more than the accuracy it buys*. That is right and it
decides the shape of the feature. A solve returns a number whatever the mesh, and nothing in
this library previously answered "is this mesh good enough" — the convergence tables answer
it for the fixtures, which is not the same thing. `ErrorEstimate` answers it per element,
which is also the input an adaptive refinement loop would consume. The one-order accuracy gain
is real (14x and 11x at the finest fixture meshes) and is the smaller half.

### The boundary caps the rate, and the signature is a HALF order

A one-sided patch at a boundary node is poorly conditioned, and including such patches caps
the convergence rate rather than merely adding noise: the quadratic sequence measured 2.77
then **2.47** — drifting DOWN, away from theory — and excluding boundary patches put it on
3.08/2.91. The diagnostic worth keeping is the *arithmetic* of it: a boundary layer of
elements is an O(h) volume fraction, so an error one order worse there enters a global L2 norm
as `h^(p+1) · sqrt(h)`, i.e. **half an order**. A rate heading for 2.5 rather than 3 is what a
boundary-limited recovery looks like, and it is distinguishable from a formulation defect
(which would cost a whole order) by exactly that half.

But excluding boundary patches leaves the nodes near a box's edges and corners with no value
at all — 228 of about 2 000 on the fixture — and **those are where a peak stress usually
sits**, so the feature would be worst exactly where it is wanted. The textbook answer, and the
one taken, is to fill them from the NEAREST patch by a breadth-first walk over the
share-an-element graph, extrapolating a patch polynomial a short way outside its own elements.
That is sound for the same reason the fit is: the polynomial approximates the stress over a
neighbourhood rather than only over the convex hull of its samples. Nearest in graph distance
rather than any patch, because the approximation degrades with distance.

### The p+1 cap, and which of the two causes dominates

The quadratic recovered rate settles near **2.76** against a theory of 3, and there were two
candidate causes with no measurement separating them: the boundary FILL above, which
extrapolates a patch polynomial to nodes outside its own elements and is only second-order
accurate in the extrapolation distance; and the SIMPLEX superconvergence theory itself, which is
weaker on tetrahedra than on the hexahedra SPR was developed for — a tetrahedron's Gauss points
are not the tensor-product Barlow points, so the p+1 claim is asymptotic at best. Separating them
is a measurement, not a redesign: restrict the recovered-error norm to a fixed central sub-domain
box, away from the fill, and see whether the rate rises toward 3 there.

It does, and the finding is that **the boundary fill is the dominant cause**. On a
[3, 4, 6]-division quadratic sequence the whole-domain rate is **2.758** (reproducing the
recorded 2.76) and the central [0.25, 0.75]-box rate is **2.883** — closing about half the
measured gap to 3 (0.242 → 0.117). That agrees in direction with the earlier experiment that
excluding boundary patches *entirely* reached 3.08/2.91, essentially theory. The small residual
below 3 that remains in the interior box is within the noise of a single last-pair rate and of
the same size as that recorded excluded-boundary energy rate (2.91), so the study does NOT
establish a separate simplex-theory cap: if the non-Barlow tetrahedral Gauss points cost
anything, it is within measurement noise of the fill's effect. The same rate deficit lands on
the thermal flux recovery (§3d), whose quadratic rate is 2.66 — one estimator, one cap, because
it is one shared machinery: `SuperconvergentRecovery.Recover<TValue, TField>` is monomorphized
over a `SymmetricTensor3` stress and a `Vector3d` flux, with the structural path kept
bit-identical. `StressRecoveryTests.TheInteriorSubDomainRateSeparatesTheBoundaryFillFromSimplexTheory`
pins it.

### An estimator that cannot estimate must say so, not return zero

With no interior corner node there is no patch, the "recovered" field IS the finite-element
field, and the energy-norm distance between them is the distance from something to itself.
Measured on a 24-element box: **9.9e-15 against a true error of 0.313**. That is not a small
error, it is no measurement — and reporting the arithmetic would call the mesh perfect on
precisely the mesh too coarse to assess, which is the worst possible direction for a
mesh-quality indicator to be wrong in. `RelativeError` is NaN there, following the spelling
`HarmonicResponse.TruncationError` already established for "not small, UNKNOWN", and
`FallbackNodes` counts the partial case so a recovery that quietly did not happen cannot look
like one that did.

### A patch may not span a material interface, and the argument is REACH

todo.md filed this as "recovery makes it worse [than averaging] and the fix is standard and
small", which is right about the fix and understates why it matters. **Averaging is local**:
it blends the shared node and touches nothing else, which is the smoothing the section above
already documents. **A patch is not local**: it is fitted over every element at its node and
then written to *every node of every one of them*, so a single fit taken across a genuine
stress jump puts a ramp into nodes a whole element layer inside each material — nodes that
touch ONE material and have an unambiguous right answer. That is a different kind of error
from a blurred interface value, and it is the one worth fixing.

Measured on a **parallel** bi-material cube (see the fixture note below), where the exact
stress is piecewise constant and a region-pure fit therefore reproduces it exactly: with the
regions collapsed, **34 single-material nodes are wrong, the worst by 92.9%**, every one of
them in that single adjacent layer and none beyond it; with the rule, every node in every
material is exact to 4.5e-15. The estimator has its own stake: the manufactured jump inside an
interface element is booked as discretization error, so the ZZ figure reads **20.6% on a field
whose true error is zero**.

So patches are assembled at corner nodes touching exactly one region, contributions accumulate
per **(node, region) slot**, and the boundary fill of the previous section walks *within* a
region — without that last part the exclusion is undone one round later, by extrapolating the
other material's polynomial into this one. `AnalysisMesh` owns the slot table
(`RegionsAt`/`InterfaceNodeCount`), so the averaging pass and the recovery ask one rule rather
than restating it, and an element reads its own material's values in the error estimate.

**The decision the entry left open was what a node-indexed field then reports.** A node on the
interface has one right answer per material and `NodalStress` has one slot for it. Three
shapes were weighed. *Per-region nodal fields* (`NodalStress(region)` as whole arrays, the
entry's own guess, mirroring the per-region laws) costs nodes x regions of storage where only
the interface nodes are multi-valued at all, and needs an answer for every node outside the
region — NaN would be the repo's spelling, but most of the array would then be NaN. *Indexing
by (element, node)*, the discontinuity's own indexing and always single-valued, is the
VTK cell-data idiom and would mirror `ElementStressAtNode` — but it makes every caller iterate
elements to ask about a node. What shipped is the third: **`NodalStress` keeps one value per
node and averages the materials there, because a display field must have one value per node
and nothing else is honest about being a display field; the multi-valued answer is reachable
as `NodalStressIn(region, node)`; and the blending is REPORTED rather than left to be
discovered, by `AnalysisMesh.InterfaceNodeCount`.** Two properties make the pair safe to mix:
away from an interface the blend is a blend of one value and is therefore the per-region value
*bit for bit* (a sum started from the first slot rather than from a zero tensor, since adding
a zero is not the identity on a negative zero), and asking for a region a node does not touch
is refused by name rather than answered with a neighbouring material's number.

**Scope, and both boundaries are stated because both are easy to get wrong.** This fixes the
*recovery* and not `Direct` averaging, and the reason is not deference to the default: for a
node-indexed field there is nothing per-region averaging could change, since only the
interface node itself is affected and it must still publish one value. What the region work
adds to `Direct` is the per-material *accessor*, which it now has. And a connected
multi-region mesh is **not reachable from the public API today** — `TetMesher` refuses mating
bodies, disjoint ones share no node, and `TetMesh`'s region-carrying constructor is internal —
so this is a precondition for conforming interfaces rather than a live defect. Both ends are
pinned by test (`MaterialInterfaceRecoveryTests`), the `TrimmedFaceRefusalTests` pattern.

### The recorded oracle could not see it, and the fixture that can

CLAUDE.md's two-material oracle is a bar in **series** — the interface perpendicular to the
load — chosen with Poisson's ratio zero in both halves so the exact field is in the linear-tet
space. It is exact and it is useless here: force equilibrium makes the axial stress `F/A` in
*both* halves, so the STRAIN jumps and the stress does not, and a recovery reproduces a
uniform field exactly whatever it does at the interface. Measured: the two recoveries differ
by 2.1e-13 of 62.5 MPa.

The arrangement with a stress jump is **parallel** — two materials side by side under a
uniform stretch share the strain, so the stress jumps with the modulus. The same nu = 0 device
carries it: `u = (eps·x, 0, 0)` is then the exact solution with no body force, because the
stress `(E_i·eps, 0, 0)` has zero divergence and the traction across the interface
(`sigma_xy`, `sigma_yy`, `sigma_yz`) is zero on both sides. A nonzero nu would make
`sigma_yy` jump, which is a traction discontinuity — not a solution at all.

One trap paid for while building it, of the recorded fixture family: **prescribing that same
uniform stretch on a SERIES bar's boundary is not the series problem**, because it imposes the
same strain on both halves, which is the parallel condition wearing the series geometry. The
control fixture duly measured a 25.4% difference between the two recoveries and reported that
the series bar *does* see the interface rule; driving it by a force instead — which is what
makes the stress uniform — put it at 1e-15. **A fixture named after an arrangement must be
loaded the way that arrangement is defined, or it is the other arrangement.**

### Direct stays the default

For `FeaSolveMethod.Direct`'s reason rather than a new one: every verification figure this
project quotes was measured through the simple path, and promoting the better answer would
silently move all of them. There is also a real counterweight — a recovered field is smooth by
construction, so at a genuine discontinuity it smooths harder than averaging does, and
averaging already smooths more than `ElementStress` does. A *material* interface has stopped
being an example of that; a re-entrant corner has not and never will be, since the true stress
there is singular and no polynomial fit can say so. The option is a better answer for the
common case, not a better answer.

### One fixture trap, of the recorded family

The exact-recovery consistency test prescribed a QUADRATIC displacement field on the boundary
with no body force — and a quadratic field is not a solution of the elasticity equations with
zero body force, so the solve returned a different field entirely and the test reported a 20%
"recovery error" that belonged to the fixture. The linear half passed from the start precisely
because a linear field's second derivatives vanish, so its body force really is zero. Same
family as §3g's probe-point traps and §3d's graded-mesh one: **a manufactured field must be
manufactured, including the load that makes it exact, and the case that needs no load is the
one that hides the omission.**

## 3j. Fatigue post-processing (`EngrCAD.Fea`)

S-N life and Goodman/Gerber safety factors over two static load cases — filed as
"arithmetic, not a solver", and built as exactly that: `FatigueAnalysis.Evaluate` consumes
two `StructuralResults`, an `SnCurve` and an options record, and touches no matrix. The
two cases are the extremes of one PROPORTIONAL load history, which is the pair
`StructuralSolver.SolveAll` returns from one factorization — the consumer that entry
point was built for, arriving.

### The scalar is the signed von Mises, and its blind spot is structural

A proportional history collapses to one scalar per node only if the scalar is SIGNED —
a magnitude reads a fully reversed load as pulsating and halves its severity. The sign
convention is the hydrostatic TRACE, chosen over the sign of the absolutely largest
principal for two reasons recorded on the method: the principal convention needs an
eigensolve and JUMPS when two nearly equal principals of opposite sign swap magnitude
under a smoothly varying load, while the trace is linear in the stress components (its
sign boundary is a fixed plane in stress space); and in the uniaxial state the S-N data
was measured in, the two conventions agree exactly.

The case no convention can serve is the finding worth keeping: **a reversed pure shear
cycle is invisible to ANY scalar signed equivalent**, because negating a pure shear
tensor is a rotation of it — s and −s have identical principal values, so no invariant
can tell the two halves of the cycle apart and the decomposition reads zero amplitude.
That is not a defect of the trace convention but the structural boundary of scalar
equivalence, i.e. precisely where critical-plane methods (Findley, Fatemi–Socie) begin —
so the multiaxial refusal is a named non-goal with a proof attached, not a caveat.

### The catalogue derives its endurance limit from its own line

`SnCurve` is Basquin (`sigma_a = sigma'_f·(2N)^b`) plus the ultimate strength the
corrections anchor on. A steel's endurance limit is NOT stored beside the line — it is
the line evaluated at its own 10⁶-cycle knee, and the curve is flat beyond it. Storing
both would be the fine-pitch tap-drill mistake: a second column that can drift with
nothing to catch it. The aluminium rows state no knee at all, because the material has
no endurance limit — a metallurgical fact the API keeps rather than smoothing over, with
a consequence downstream: a safety factor against infinite life does not exist for
aluminium, so `FatigueOptions.DesignLife` is required there and refused by name without.

The constants are stored in datasheet form (MPa — which is also the model unit, so
unlike the density lesson there is no conversion for a transcription test to hide
behind), flagged verify-against-datasheet like `StandardHoles`' Trisert rows. The
transcription tests pin the line at its own anchors bit-exactly (`StressAt(0.5)` IS
`sigma'_f`, since `Math.Pow(1, b)` is exactly 1; the knee is on the line), and the
checks with independent teeth are physics-flavoured: the derived steel endurance/UTS
ratios land in the classical band, which four independent transcriptions must conspire
to hit.

### The spellings are the design: NaN, a floor, and a log

Three publishing decisions, each forced by a consumer's actual behaviour rather than
taste. **Infinite life is NaN** — the VTU writer's established "no value" spelling,
which `FieldRange.Of` already skips — so a part that mostly lives forever still ranges a
usable legend over the nodes that do not. **A sub-cycle life floors at one cycle**
(log10 = 0): the ranging machinery skips only NaN, so a −∞ from `log10(0)` would poison
the legend's minimum, and anything below ~10³ cycles is outside Basquin's high-cycle
validity anyway — the floor errs conservative and covers the static-failure branch
(mean at or beyond S_ut, where the corrected amplitude is +infinity). **Life publishes
as log10(cycles), stated in the units string**, because lives spread over decades and
`FieldRange.Normalize` is linear — raw cycles would spend the whole legend on the
longest-lived node. The native log-scale display mode has since landed as
`FieldDisplay.LogScale` (see the field-display record in §6b's neighbourhood), the
complementary spelling for a field carrying RAW decade-spanning values; the fatigue
convention stands unchanged, since a field already publishing logs wants linear colours.

The corrections carry their own exactness anchors: zero mean is the identity EXACTLY
(the non-tensile branch returns the amplitude verbatim — no division that happens to be
by one), the allowable amplitude at a mean of exactly S_ut is EXACTLY zero (S_ut/S_ut
is 1.0), and a compressive mean takes no benefit by default — compression genuinely
retards crack growth, but crediting it requires confidence the mean stays compressive at
the crack-starting surface for the whole service life, which is why every standard tool
defaults to none and the default is stated rather than silently safe.

### The safety factor's definition is its own oracle

The factor is the RADIAL (load-multiplier) form: under proportional loading, amplitude
and mean scale together, so n solves `n·amp/strength + n·mean/S_ut = 1` (Goodman;
Gerber's mean term is squared and solved as the quadratic's positive root). Because the
definition is "the multiplier that reaches the line", the verification is to APPLY it:
re-solve both cases with the loads scaled by the measured factor, and the critical node
must land exactly ON the line — the minimum factor reads 1.0 to 1e-9, and the scaled
amplitude equals the allowable at the scaled mean. An independently restated formula
would agree with a broken implementation that made the same transcription mistake; the
definition cannot. (The R = −1 test's mean measures exactly 0.0, and the reason is worth
keeping: negating a load negates every solve and recovery output bit for bit — IEEE
round-to-nearest commutes with negation — and the signed von Mises is odd in the stress,
so the two halves cancel identically rather than to round-off.)

### Marin factors: a derivation with a pivot, not an edit and not a scale

`SnCurve.WithFactors` makes the polished-specimen rows honest for a real part, and two
shapes of the feature were rejected before the one that landed. Editing the catalogue
(a "machined 1045" row) is the fine-pitch tap-drill mistake — a second stored column
that drifts with nothing to catch it — so the transcription stays pristine and the
corrected curve is DERIVED. And scaling the whole line (multiplying sigma'_f) is wrong
physics wearing simpler arithmetic: surface finish, size and scatter govern crack
INITIATION, which dominates at long life, while at 10³ cycles plastic strain dominates
and the factors classically do not apply. So the construction is the classical
two-anchor re-fit — through the pristine line's own 10³-cycle value (Basquin's own
validity floor, already stated in the class remarks, now load-bearing as the pivot) and
through `k·S_e` at the unchanged knee — which is Shigley's construction with the
curve's own low-cycle point as the anchor rather than a second transcribed constant
(`f·S_ut` would be one more datasheet number to verify). S_ut is untouched because a
finish does not change a static failure; a factor of exactly 1 returns the pristine
object verbatim (re-deriving would move the coefficients by ulps for nothing — the
exact-zero-semantic-test tier); and the refusals are named: a knee at or below the
pivot, diameters past the correlation's 254 mm data (the classical 0.6 floor is an
assumption, not a fit, and this library does not assume it silently), and reliabilities
off the standard table, since interpolating a normal quantile through it would invent
precision the underlying 8%-scatter assumption does not have. The transcription tests are
the textbook's own worked values (0.798 / 0.858 / 0.814), not the formulas re-typed — a
re-typed formula agrees with its own transcription mistake — and the first run caught
exactly that: three hand-approximated expected values were off in the third decimal and
the worked-value anchors (690 MPa machined, 32 mm) were not.

**The knee-less (aluminium) correction landed as a SEPARATE overload, and the separation
IS the design.** `WithEnduranceFactor` anchors on the endurance limit, which aluminium does
not have — so `WithEnduranceFactorAt(factor, referenceLife)` / `WithFactorsAt(finish,
referenceLife, …)` apply the factor at a STATED reference life instead, re-fitting through
the same 10³ pivot. The reference life is REQUIRED rather than defaulted, because a
knee-less line falls forever so "the endurance strength" only exists at a stated life (5×10⁸
is the rotating-beam convention), and the reference life IS the claim being made — defaulting
it would put a number the user did not state into the answer. The corrected line stays
knee-less (the correction does not invent an endurance limit the material lacks), and the
oracle is the defining identity: at the reference life the corrected strength is exactly
`factor·(pristine there)`, the pivot is untouched, and a knee'd curve is refused by name (its
reference IS its knee).

**Miner–Haibach landed as an S-N MODE** (`WithHaibachSlope`), a derived curve leaving the
transcribed row pristine, and the design turns on what removing the flat line does to the rest
of the machinery. The flat endurance line is a constant-amplitude idea — a sub-limit amplitude
arrests small cracks and does no damage — so `D(k)` has a STEP where a cycle crosses the limit,
which the spectrum factor (§below) reports as a crossing rather than a solution. Haibach
continues the line past the knee at the shallower slope `b' = b/(2+b)` (the classical
`k' = 2k−1` for the Wöhler slope `k = -1/b`), so a sub-limit cycle carries a small finite
damage and `D(k)` becomes continuous (measured 1.125e-4 on a 0.9·limit spectrum where the flat
line reads exactly 0; identical to the flat curve BELOW the knee). The load-bearing consequence
is that the endurance PLATEAU is gone, so a Haibach curve reports `HasEnduranceLimit = false` —
and that makes the infinite-life refusal ONE rule rather than two: the fatigue machinery already
refuses an infinite-life factor for a knee-less material and requires a design life, so a
Haibach curve reuses that path verbatim (the "one rule instead of two" the backlog predicted).

### Rainflow over a transient run, and the two design questions it answered in code

Variable amplitude landed as `Rainflow.Count` — ASTM E1049's three-point algorithm
verbatim (X the range under consideration, Y the previous one, S the starting point;
the standard's Fig. 6 worked example transcribed as a test, cycle for cycle, because a
counting that gets the totals right can still pair the wrong points and the
total-variation identity would pass it) — plus
`FatigueAnalysis.Evaluate(TransientResults, curve, options)`, Miner's rule over each
node's signed von Mises history with `EquivalentAlternating` reused verbatim per
counted cycle. The filed entry named two design questions to settle at build time, and
both are answered by arithmetic rather than preference.

**(a) Every node, one pass, no hot-spot preselection.** The per-node footprint is one
scalar per stored state (8 bytes), which is small beside the states the transient
already retains — each holds full displacement, velocity, acceleration and reaction
fields — so preselection would save a rounding error of memory and cost the "which node
is worst" answer the published field exists to give. The honest caveat is the SAMPLING:
the counting sees the STORED states, so a reversal falling between stored steps is
never counted — a run that feeds fatigue stores every step, and the remarks say so.

**(b) The open end is an option because E1049 names two honest readings.** One-shot by
default: a transient is a load event with a beginning and an end, and the residual
ranges the counting cannot close are the standard's HALF cycles, no more and no less.
`AssumeRepeating` reads the history as one period of a repeating load — rearranged to
begin at its largest-magnitude extremum (the standard's own prescription, under which
every count closes) and the residual halves paired into full cycles by EXACT
(range, mean) key, a pairing rather than a tolerance because both flanks of one closed
excursion are computed from the same two point values. The rotation is of the RAW
samples, not the turning points, because the turning-point structure at the seam
differs between the two orderings. The two modes agree on damage for a
constant-amplitude history; what changes is the reported cycle structure, which is the
option's contract.

**The degeneracy oracle runs through the real seam.** Internally-built transient states
alternating between the `SolveAll` pair's two `StructuralResults` ARE a
constant-amplitude history, and the counted cycle's amplitude and mean come out
BIT-equal to the static pair's decomposition (same values through the same arithmetic —
IEEE addition is commutative, so `0.5·(a + b)` cannot differ), making the damage
exactly `count/life` as an identity rather than a tolerance. The second oracle with
teeth is the total-variation identity `sum(2·count·range) = sum|Δ|` over the turning
points, exact on a pseudo-random history — it holds for ANY input, so it catches a
dropped or double-counted range on histories nobody hand-checked.

### The spectrum safety factor, and the closed form's exact boundary

The filed entry called the variable-amplitude factor "an iteration against a stated target
life", which is right about the mechanism and wrong about the shape: there are TWO targets,
and only one of them iterates. `FatigueAnalysis.LoadFactor` answers both, published per node
as `RainflowFatigueResults.SafetyFactor`, and `RainflowFatigueOptions.DesignRepetitions`
picks between them exactly as `FatigueOptions.DesignLife` does one layer up — null measures
against INFINITE life, a stated R against a Miner damage of `1/R`.

**Whether a closed form exists is a property of the spectrum, and stating the boundary is
most of the design.** Damage is a sum of power-law terms, `2·n·(sigma_ar/sigma'_f)^(-1/b)`,
so IF every cycle's equivalent amplitude were linear in the multiplier the whole sum would
scale as `k^(-1/b)` and the factor to a damage target would be exactly `(R·D)^b` for the
unit-load damage D — one line. Two entirely ordinary things break the linearity. The
endurance KNEE makes cycles JOIN the sum as the multiplier grows, so the coefficient is
piecewise and a closed form read off the unit-load damage overstates the factor; and a
tensile mean under Goodman or Gerber makes the equivalent amplitude
`k·a/(1 - k·m/S_ut)`, which is not a power of k at all and diverges at `k = S_ut/m` — a hard
static-failure ceiling the factor can never reach, and which the bracket meets naturally
because the damage there is `+infinity`.

So the implementation is ONE bracketed solve for every case rather than a closed form with
conditions, and **the closed form is kept where it is worth more — as the test oracle.** On
a knee-less curve with no mean correction engaged it agrees with the solve BIT FOR BIT
(1.0329781076872908 both ways); on a steel spectrum entirely above its knee, to two ulp; and
on the two spectra that break it, it misses by a measured 4.5% (knee) and 12.9% (mean) while
the solve lands on the target. A closed form used as the implementation would have made
those tests tautologies.

**The infinite-life target IS closed form throughout, and the reason is that it is not an
accumulation.** "The multiplier at which damage first appears" is per-cycle: each counted
cycle reaches the endurance limit at its own static radial factor, and the spectrum reaches
it at the smallest of them. That is literally `FatigueAnalysis.SafetyFactor` evaluated per
cycle against the endurance limit — the same private helper the static pair's field is built
from, so there is one rule rather than a second formula free to drift, and a ONE-cycle
spectrum answers bit-identically to the static pair by construction. It also prices the two
targets apart: the default costs one more pass over cycles the counting has already
produced (3.5 ms on a 135-node, 241-state run), a stated target about sixty damage
evaluations per node (18.2 ms, 5.2x — `FeaBenchmark.WhatTheSpectrumSafetyFactorCosts`).

**The verification with the most teeth is neither of those.** A spectrum of ONE cycle counted
once reaches a damage of `1/R` exactly when that cycle's life is R, i.e. when its equivalent
amplitude equals the curve's strength at R — which is precisely the static radial factor
against that strength, and the algebra reduces term for term to `n = 1/(a/S + m/S_ut)` for
Goodman and to the same positive root for Gerber. So a bisection over a Miner sum and a line
intersection must agree, for every mean and both corrections, INCLUDING the tensile-mean
region where the general spectrum has no closed form at all: measured ≤ 1e-9 relative across
means of -100…400 MPa, both above and below a factor of 1. Beside it, the apply-it oracle
runs through the solver (re-solve the whole history with every load case scaled by the
measured factor, re-count, re-accumulate: the critical node's damage lands on 1.0000e-4
against a 1e-4 target and its factor reads 1.0 to 1e-9), the infinite-life factor is
bracketed from BOTH sides (damage exactly 0 a nanometre under it, `count/10^6` a nanometre
over), and the factor is exactly inverse in the load scale.

**Two boundaries are named rather than smoothed.** The endurance knee puts a genuine STEP in
the damage function — a crossing cycle goes from contributing nothing to contributing
`count/10^6` — so `D(k) = target` has no solution when the target lands inside a step; what
is reported is then the crossing itself (the smallest multiplier at which the target is
reached), and the step belongs to the flat-line S-N model rather than to this solve — and
`SnCurve.WithHaibachSlope` is the standard remedy that removes it (a sloped continuation past
the knee makes `D(k)` continuous, so a target inside the old step now has a solution; see the
Miner–Haibach subsection above). And a node whose history never MOVES carries no
counted cycle at all, so it reads NaN however large its steady stress: rainflow measures
cycles, and a steady load is a static-strength question — the static pair answers it, since
two identical cases still carry a mean and report the `S_ut/sigma_m` margin. The refusals
transfer verbatim from the static path: a curve with no endurance limit has no infinite life
to measure against, so `DesignRepetitions` is required there and named in the refusal.

**That second NaN has two causes and only one of them is honest**, which is the recorded
pure-shear blind spot arriving through the spectrum rather than a new one: negating a pure
shear tensor is a rotation of it, so both halves of a reversed shear cycle read the SAME
signed von Mises and the series is constant — indistinguishable, at this layer, from a node
that genuinely never moves. The factor therefore inherits the scalar equivalence's blind spot
exactly as the damage field already does; it neither adds one nor repairs one, and the
statement a NaN makes is "this history carries no cycle IN THE SCALAR EQUIVALENT". Worth
stating explicitly because the factor's NaN reads stronger than the damage field's zero — an
absent mechanism rather than an unconsumed life — while both are the same claim and both are
wrong in the same case.

### Refused by name

Welds (hot-spot / nominal-stress category methods are their own discipline — a weld's
life is governed by its detail class, not the parent metal's line) and multiaxial
criteria beyond von Mises equivalence (above). Mixed inputs refuse loudly: results on
different `AnalysisMesh` instances would pair unrelated nodes, and results answering
with different `Recovery`/`Averaging` settings — including across one transient's
stored states — would book the recovery gap between two fields of different accuracy as
alternating stress.

## 3k. Topology optimisation (`EngrCAD.Fea`)

Compliance minimisation by SIMP over the tetrahedral meshes, with an optimality-criteria
update and a filter. It was filed as small and it is: the stiffness assembly, the
factorization and the element strain energy already existed, so **the feature is the LOOP**,
and every decision worth recording is about what the loop may assume.

### The sensitivity was almost free, and "almost" is the finding

The backlog said `dc/drho_e = -p·rho^(p-1)·u_e' k0 u_e` "IS the element strain energy, which
`StructuralResults` already computes for its stress recovery". Half right, and worth stating
precisely because the correction changed nothing about the size of the job and everything
about where the code went. `StructuralResults.ComputeErrorEstimate` does accumulate a
per-element energy — and then DISCARDS it: the per-element array it fills holds the estimated
ERROR, and the energy survives only as a global total. It is also integrated at
`TetQuadrature.Degree3`/`Degree5`, chosen to be exact for the estimate's degree-2p integrand,
where the quantity SIMP wants is `u_e' k0 u_e` for the `k0` the ASSEMBLY built — which means
the assembly's own rule, `TetQuadrature.For(order)`, and no other. So the energy is computed
in the optimiser at that rule, and the reward is an identity worth more than the saving would
have been: `sum_e rho_e^p·E_e` and `f'u` are then the same number (measured **1.5e-15**
relative), two constructions checking each other.

### One shared path was touched, and only one

`FeaAssembly.Stiffness` gained an optional per-element scale. Passing null SKIPS the multiply
rather than multiplying by 1.0 — bit-identical for every incumbent caller, and the two are
different statements even though only one is free. Nothing else changed: because
design-dependent loads are refused (below), every load is already reduced to nodal forces on
the model, so the right-hand side is a gather assembled ONCE for the whole run and the
optimiser needs no second assembly implementation. `StructuralModel` gained one internal
`HasVolumeLoad` flag, set where the load is applied rather than recovered by matching a
`Conditions` message string, which is prose for a human and would break on a rewording.

### Refusals protect a PROPERTY of the load, not the formulation

Compliance is self-adjoint only while the load does not depend on the design, and that is what
makes the sensitivity free. Each refusal names a case where it stops being merely inaccurate
and becomes wrong: `Gravity`/`BodyForce` (self-weight is integrated over the full-density body
once, so a run would minimise one structure's compliance under a different structure's
weight), a prescribed non-zero displacement (the force moves with the stiffness, so minimising
`f'u` maximises COMPLIANCE — the sign of the whole problem flips), and a thermal load
(`alpha·dT` enters through `D`, so it scales with `rho^p`). Local stress constraints remain a
NAMED non-goal, because aggregation (p-norm or Kreisselmeier–Steinhauser) needs a parameter that
CHANGES THE ANSWER, so it is a separate feature with its own verification.

### Passive regions and several load cases landed, each a per-element or per-case addition

**Passive (non-design) regions** are a per-element bound rather than a new algorithm: `SolidRegion`
/ `VoidRegion` are `Func<Vector3d, bool>` centroid predicates (a VOLUME selector, matching
`SizingField`, since `Facets` selects boundaries), and a matching element is pinned at 1 or the
floor and dropped from the optimality-criteria redistribution — the bisection then shares the
remaining budget among the free elements, so the constraint still holds exactly with the pinned
material counted toward it. The oracle is two-fold and both parts are mutation-proof: pinning is
EXACT (no filter, a pinned element ends at 1 or the floor at every element, the whole-domain
fraction met to round-off), and a passive constraint can only RAISE compliance because it
shrinks the feasible set — any passive-constrained design is also a valid free design at the
same volume, so the free optimum is at least as good (measured: forcing a hole through a
cantilever's tension corner cost +54.8%, forcing material into the idle tip +6.2%). A no-passive
run is the incumbent path bit for bit.

**Several load cases** with a stated weighting now minimise `sum_i w_i·u_i'Ku_i`. The refusal
was about the WEIGHTING being a design decision, never about the arithmetic — all cases share
mesh, supports and materials, so `K(rho)` is one matrix and each case is a back-substitution
(exactly `SolveAll`'s contract, `RequireOneOperator` reused), the compliance and the
sensitivity are weighted sums, and the loop is unchanged. So the multi-case `Minimize` IS the
implementation and the single-model form is `Minimize([case(model, 1)])` bit for bit, the same
sugar-over-the-general-form shape `Solve` = `SolveAll([model])[0]` follows. What stays refused is
a min-max (worst-case) formulation, which is a genuinely different problem, filed. The oracle is
SYMMETRY, chosen because a plausible truss is exactly this feature's failure mode: two
mirror-image loads, each optimising ALONE into an asymmetric structure, combine equally-weighted
into a mirror-symmetric one (the two-case mirror difference falls to 18% of a single case's, and
the residual is the Kuhn tet mesh's own non-mirror-symmetry — the recorded diagonal lesson —
rather than the solver); a 3:1 weighting leans the mass toward the heavier case.

**Releasing a topology with islands** gained `KeepLargestComponentOnly` — off by default,
because silently deleting material the optimiser placed is a stated act, and even on it leaves
`ComponentCount` reporting what the extraction FOUND. "Largest" is by enclosed volume; verified
on a two-blob field where the smaller blob is dropped by exactly its own volume.

### The constraint is on the PHYSICAL volume, and the closed form is what caught it

A row-normalised filter is not volume-preserving: near a boundary an element's neighbourhood
is truncated, so the column sums of `W` are not the element volumes. Constraining the DESIGN
variables therefore lets the structure hold more material than was asked for — and on the
uniform-bar fixture, whose convex optimum at `p = 1` is exactly `c0/f`, that form returned
**1.79·c0** against the closed form's **2.00·c0**: a compliance BELOW the true optimum, which
is only reachable by spending volume the constraint said was not there. No picture would have
shown it and no "the volume fraction is 0.4" assertion would have failed.

It costs nothing to fix, because the filter is LINEAR: `V(x) = v'(Wx) = (W'v)·x` is exactly
linear in the design variables, so one transpose at the start gives both the gradient and an
exact evaluator and the bisection never applies the filter at all. `DesignVolumeFraction` is
reported beside the physical one so the gap stays visible.

### The filter's radius is an engineering input

`r_min` sets the minimum member size, so it is a manufacturing statement (the thinnest wall a
printer holds, a cutter's diameter) and it has **no default** — a default there would be a
manufacturing decision made by a library. Weights carry element VOLUME, which the published
uniform-grid forms do not have to: on a tetrahedral mesh a patch of small elements would
otherwise out-vote a neighbouring patch of large ones by being numerous, and including `v_j`
reduces exactly to the published form when the volumes are equal.

`TopologyFilter.Density` is the default over `Sensitivity` on a verification argument rather
than a convergence one: the density filter is a genuine change of variables, so the reported
sensitivity is the EXACT gradient of the compliance that was computed, which is what makes a
finite-difference check of it meaningful (measured 9.9e-8 through the chain rule). Sigmund's
sensitivity filter is a heuristic — its filtered sensitivity is the gradient of nothing — and
is offered as one, with its real advantages stated (22 iterations against 89, and a crisper
result). `None` is not a setting but the defect, kept public so the defect can be MEASURED:
28x the neighbour variation and an order of magnitude more mesh dependence.

### Extraction marches the mesh's own tetrahedra, NOT Surface Nets

The backlog proposed `Sdf` + `SurfaceNets`, on the reasoning that a density field is an
implicit field and the repository already has that route. It has a hard obstacle:
`SurfaceNets.Polygonize` culls blocks using the **1-Lipschitz** property, and a density field
is not 1-Lipschitz and has no useful bound — it goes from 0 to 1 across one element, whatever
that element's size — so the cull would drop surface silently, which is the one failure a
completeness argument exists to prevent. Two further reasons agree: the field is DEFINED on
this mesh, so marching its own tetrahedra is exact for the piecewise-linear nodal field and
adds no second discretisation; and `EngrCAD.Fea` is a leaf that cannot see `EngrCAD.Implicit`
at all, while `HalfEdgeMesh` is a type both layers share and `Shape.From(mesh)` is one call.

Two rules make the surface weld by INDEX with no tolerance: a crossing is computed from the
LOWER-INDEXED end of its edge (the exact-boolean seam rule, so every tetrahedron containing
that edge produces bit-identical coordinates), and a crossing landing exactly on a node is
interned as that NODE, or the interior march and the boundary cap would put two vertices at
one position. Orientation is read off the field — the level set of a linear function is planar
with normal `grad g`, so one dot product winds all sixteen sign cases and none of them needs a
table.

**A B-Rep is refused by name**: the level set is a faceted surface with as many facets as the
mesh has crossings, and there is no parametric surface in it to recover. Offering one would
mean fitting, whose tolerance would silently become part of the answer.

### A density field cannot be displayed on the design space's own mesh

Learned from the first docs render, and it is a general statement about this result type
rather than about that fixture. Every other FEA page samples its field onto the part's display
mesh, which for a box is EIGHT vertices — and a stress field is smooth enough that eight
corners still read plausibly, while a density field varies at element scale BY DESIGN. The
first render duly came back showing a smooth gradient over the range 0.002 to 0.116 on a field
whose true range is 0.001 to 1: a picture of nothing, and a convincing one. The fix is to
sample onto `TetMesh.BoundaryMesh`, whose vertices ARE analysis nodes so every sample matches
exactly.

### Releasing the result is TWO measured stages, and the measuring is the design

`TopologyResult.Release` turns the density field into a usable, exportable solid, and the whole
of it is the decision to REPORT what each stage costs rather than return one opaque "cleaned up"
mesh. The extracted iso-surface is the exact level set and a poor part — thresholded on
tetrahedra, so stair-stepped at element scale, with every triangle shape (the MBB beam: 3 908
faces, 2 950 slivers, smallest angle ≈ 0). Two stages fix it, and both MOVE the surface, so both
are measured: (1) **smoothing** fairs the stair-steps (`LaplacianMeshSmoother`, implicit
fairing) and (2) **remeshing** re-triangulates to a uniform edge length (`Remesher`).

**Smoothing is deliberately GENTLE, and the failure mode is why.** A stair-step is material on
the convex side of the mid-surface, so fairing a THIN optimised structure necessarily removes
some — and a full step at the smoother's own "visible" strength of 1 melts thin members
(measured −42% of the volume in three steps on the docs beam). The default is three steps at
strength 0.1, which fairs the steps for a reported ≈−6% and moves the farthest vertex ≈0.73 mm.
A smoothing that shrinks the part silently is the thing to avoid, so the volume delta and the
max/mean displacement come back as values (`SmoothingVolumeDelta`, `SmoothingMaxDisplacement`).

**Remeshing REDISTRIBUTES rather than moves**, which is what makes it the stage that makes the
part usable without spending more of the shape. Its projection target is the SMOOTHED mesh, so
the remeshed vertices sit on that surface to round-off (measured within 2e-14 of it) and the
whole benefit is triangle SHAPE: the sliver count drops 2 950 → 73 and the mean smallest-angle
rises 21° → 48° (`TriangleQuality`, before/after via `IsoSurfaceQuality`/`FinalQuality`), with
the volume barely changed (−2%). Feature-angle detection is OFF by default here, per the
recorded "feature detection reads the mesh you give it, not the surface you meant" lesson: an
optimised part is an organic shape with no CAD creases, and a tessellated blob's facets meet at
large dihedrals, so the remesher's usual 30° default would pin most of the mesh and leave the
slivers this stage exists to remove.

**The order is smooth-then-remesh**, and it is the right one: fair the surface, then redistribute
vertices onto the faired surface. The other order would remesh the stair-steps and then have to
fair the uniform result, fighting the resolution the remesh just established. The three stages
(`TopologyReleaseStage.IsoSurface`/`Smoothed`/`Remeshed`) are honestly-different outputs each
reachable on its own with its own cost — the `Shape.Remeshed`-is-a-graph-node-with-an-honest-
`Explain` argument, one project over.

**`Release` returns a `HalfEdgeMesh`, NOT a `Shape`**, because `EngrCAD.Fea` is a leaf that
references only Core and Mesh and cannot see `Shape`/`Part` in `EngrCAD.Modeling` — the same
sibling-layer constraint `ExtractSurface` already lives under, so `Shape.From(released.Mesh)` is
the one line that crosses back, and from there the part flows through `--export` and
`EngrCad.Show` unchanged.

**Islands are reported, not tidied.** Extraction keeps every tetrahedron above the threshold, so
a disconnected island survives as its own component (`ComponentCount`) rather than being
silently deleted — keeping only the largest is one call over `MeshConnectedComponents` and is
the caller's, since deleting material the optimiser put there is exactly the tidying that should
be a stated act. **Verified by trades, not pictures** (`TopologyReleaseTests`): the delivered
solid is `Validate`-clean and closed and round-trips through a real binary STL by
signed-tetrahedral volume (which catches a wrong winding or a hole) to float precision; the two
stage volume deltas add up to the whole iso-to-deliverable difference exactly; and the MBB beam
is checked by the property it must have — left–right SYMMETRY, which a wrong support breaks by
3× — plus a fixed-`r_min` refinement study onto the same structure.

### Thermal SIMP: the loop measured physics-blind

`MinimizeThermal` is the same OC iteration over a density-scaled CONDUCTANCE:
`FeaAssembly.Conductance` learned the optional per-element scale `Stiffness` already
carried (null skips the multiply, incumbent assemblies bit-identical), and the loop itself
was extracted into one shared `RunOptimization` behind an `ITopologyEvaluator` seam — the
structural path routes through it verbatim, so its 59 committed topology tests
(continuation determinism included) are the extraction's regression oracle, and all passed
unchanged. The thermal evaluator mirrors the structural one point for point
(symbolic-factorization reuse included), with the per-element THERMAL energy
`E_e = T_e'·k0_e·T_e` built from the SAME `ThermalElement.Conductivity` arithmetic the
assembly uses — which is what makes `f'T = Σρᵖ·E_e` structural rather than approximate
(measured equal to twelve digits). The refusals carry the self-adjointness argument to its
thermal spellings: CONVECTION is a film on a boundary the optimisation is reshaping (the
load moves with the design — the self-weight refusal one physics over), and a NONZERO
prescribed temperature makes the coupling term `K_fc·T_c` scale with the density of the
elements touching the sink; a ZERO sink contributes nothing and IS the volume-to-point
convention, with GENERATION staying fixed because the heat is the DOMAIN's, not the
material's. Verified in the incumbent style: the p = 1 and p = 3 uniform closed forms
(`c = Q²L/(kA)/fᵖ`) met EXACTLY (ratio 1.0), the FD sensitivity through the production
evaluator at 9.2e-8 unfiltered and 6.5e-8 through the density filter's chain rule, and the
volume-to-point fixture's dendrite at 25.3% of the uniform design's compliance in 80
iterations with zero rises and the volume met to 1e-10. `TopologyResult` carries its mesh
directly with `Model`/`ThermalModel` nullable twins (exactly one set), so `Release` serves
both physics unchanged. A process note recorded by being bitten: the record you are
reading missed its own commit once, because the patch that wrote it threw inside a
BACKGROUND gate chain whose heredoc newline broke the `&&` sequencing — the suite and
DocsGen still ran and the wait keyed on their markers, so the failure was silent. Patches
run in the FOREGROUND now, and their prints are checked before the gate starts.

### Heatsink sizing: the correlations with the FEA as their referee

`NaturalConvection` + `HeatsinkSizing`: the sizing side is the Bar-Cohen & Rohsenow
composite Nusselt over the Elenbaas optimum (El = 54.3, with the classic Nu = 1.31 DERIVED
from the composite at that optimum rather than stored — a second copy could only drift)
and the adiabatic-tip fin efficiency tanh(mH)/(mH), every constant a ⚠ datasheet-form
transcription; units are SI at the correlation boundary (the form a datasheet states and a
human checks) and converted once, visibly, at the mm-world edges. The verification is the
feature: the fin efficiency is held against an INDEPENDENT finite-difference solve of the
1D fin equation (equal to eight digits — two constructions sharing no line), and the
discriminating row puts the 1D closed form against a REAL 3D conduction solve of the same
fin through `ThermalSolver`'s own Convection films (base at the rise, films on the two
faces, tip adiabatic so both constructions describe ONE fin): measured η = 0.9594, closed
form 2.21048 W against FEA 2.21071 W, ratio 1.0001. The Elenbaas spacing pins its own
quarter-power scaling exactly where it is exact (S(16L) = 2·S(L) to twelve digits; the ΔT
quarter-power drifts through the film-temperature β and is asserted as the 1.9–2.1 band it
honestly is), fin height is found by bisection on a PROVABLY monotone quantity
(d/dH[tanh(mH)/m] = sech² > 0), and an envelope that cannot meet the rise refuses naming
both the asked and the achievable watts. Orientation is VERTICAL only — the transcribed
case, offered by name.

### Penalty continuation, and the finding that a ramp with no dwell is not one

`PenaltyContinuation` (opt-in, `TopologyOptions`) starts the penalty at 1 and steps it up to the
target rather than holding it at the target from the start. The argument is standard: the `p = 1`
compliance problem is CONVEX (a half-dense element is exactly as efficient per unit volume as a
solid one, so nothing pushes toward solid-or-void), so it has a unique, start-independent
optimum, and stepping the penalty up from there carries that independence into the non-convex
regime — a local minimum reached from a good state rather than an arbitrary one.

**The load-bearing detail is the DWELL, and it was measured rather than assumed.** The first
schedule was a fast linear ramp — increase `p` a little every iteration — and it reduced start
dependence NOT AT ALL (measured: on a cantilever the two-start spread was 0.014 with the ramp
against 0.0025 fixed, i.e. slightly WORSE). The convex phase never settled: spending one
iteration at `p = 1` and immediately climbing establishes no start-independent state to carry
forward. The schedule that works HOLDS at each level (`PenaltyHoldIterations = 20`, stepping by
0.5) so the convex problem — and each intermediate one — actually converges before the penalty
rises, and the change-tolerance stop is deferred until the target is reached so a run cannot
freeze at a low, blurred penalty. Same shape as several fixture lessons: the obvious cheap
version of the idea measures nothing, and the measurement is what says which knob mattered.

The oracle needs a MULTIMODAL fixture, because the claim is empty on one that is not: the MBB
beam at volume fraction 0.3 and filter radius 5 IS start-dependent, and a top-biased start traps
a fixed-`p` run at compliance **85.99** while a bottom-biased start reaches **46.63** (two-start
spread 0.202). Continuation reaches **46.70 / 46.68** from the two starts (spread 0.001, 193×
less) — escaping the bad basin AND landing on essentially one structure. It is OFF by default
because it moves every committed number; with it off, `EffectivePenalty` returns the target at
every iteration, so the run is bit-identical to the incumbent path (asserted through
`DoubleToInt64Bits` on the density field, and pinned by every existing closed-form test still
passing).

### Cost, and reusing the symbolic factorization — the numeric pass is the floor

One factorization per iteration is the cost, and the reduced stiffness has an IDENTICAL sparsity
pattern every iteration: the mesh connectivity and the eliminated DOFs are fixed, and only the
per-element scales change. So the loop analyses the pattern ONCE (`SparseCholesky.AnalyzePattern`
→ `SparseCholeskySymbolic`) and every iteration reuses it — the ordering, the elimination tree
and the column-count symbolic pass are skipped and only the numeric pass runs. It is a pure
speedup because `symbolic.Factorize` is **bit-identical** to a fresh `Factorize` of the same
matrix by CONSTRUCTION (see §2 for the value-gather argument), so it moves no number and every
topology test passes unchanged.

**The saving is real and BOUNDED, and the bound is the point.** `Analyze`'s own table already
says the numeric pass is where the time is, and the reuse can only remove the symbolic fraction
around it. Measured per-factorization on the loop's own reduced stiffness (win-x64,
`TopologyReuseBenchmark`): **1.13×** at 1 152 elements (840 free DOF, where the symbolic pass is
a meaningful slice of an 8.7 ms factorization) down to **1.02×** at 10 800 elements (6 600 free
DOF, where the 20 ms analysis is nothing beside a 1.2 s numeric pass). The reuse's factor time
essentially IS the numeric-pass floor, which is optimal — a factorization cannot beat its own
arithmetic. The complementary lever the todo names, a preconditioned CG warm-started from the
previous iterate, is a different mechanism (it attacks the numeric cost, not the symbolic one)
and is filed.

## 3l. Laminates and directional failure (`EngrCAD.Fea`)

`Laminate` (classical lamination theory over a ply stack) and `LaminaStrength` /
`FailureAnalysis` (Tsai–Wu, Tsai–Hill, maximum stress). Both sit on §3h's `ElasticLaw` and add
no element type, no assembly path and no solver: one produces a law, the other reads a solved
result. What is worth recording is where each decision was forced.

### A laminate is a property derivation, so it rides SetElasticity unchanged

`Laminate.ToElasticLaw` returns an ordinary `ElasticLaw` and goes in through
`StructuralModel.SetElasticity`, exactly as a hand-stated orthotropic law does. Nothing about
the assembly, the solvers, the recovery or the persistence changed — which is what makes the
feature reviewable, since the whole of it is arithmetic over inputs and one verified output.

### The homogenisation is mixed, and it is the PCB thermal smear's physics

Plies are bonded, so they share the in-plane STRAIN; they stack, so they share the
through-thickness STRESS. That is the same parallel/series split `PcbThermal` uses on copper
(§6d stage 8) — Voigt in plane, Reuss through thickness — and here it is exact rather than a
mixing rule, because the per-ply condensation can be written down: with the out-of-plane
indices condensed, `sigma_i = Qbar·eps_i + W·sigma_o` per ply, and averaging by thickness
gives a 6x6

```
C* = [[P + Q·R^-1·Q',  Q·R^-1],
      [R^-1·Q',        R^-1  ]]
```

with `P = <Qbar>`, `Q = <C_io C_oo^-1>`, `R = <C_oo^-1>`. Two properties fall out rather than
being arranged. It is **symmetric by construction**, because `<C_oo^-1 C_oi>` is the transpose
of `<C_io C_oo^-1>` — so Maxwell reciprocity holds as an energy statement, not because the
last bits were averaged. And **its plane-stress reduction is exactly `A/h`**: setting
`sigma_o = 0` collapses the whole thing to `P`, which IS the CLT extensional stiffness divided
by the thickness. So the 3D law a solid element carries and the CLT a design was done with
cannot disagree in plane. Measured: condensing the assembled 6x6 reproduces `A/h` with a worst
difference of **0.0**, and a solved bar returns `Ex = 95 991.30` against CLT's 95 991.30.

### The rotation is ASKED, not restated

Each ply's 6x6 comes from `ElasticLaw.TransverselyIsotropic` on a frame rotated by the ply
angle — the one Voigt rotation in the project, already verified against an independent tensor
oracle (§3h). `Qbar` is then the static condensation of that matrix, which IS the plane-stress
reduction a thin ply's free surfaces impose. Writing a second trigonometric `Qbar` expansion
would have been a second chance to make the engineering-shear mistake §3h exists to record.

The measurable price is stated rather than hidden: the condensation's arithmetic involves
`nu23` even though the answer does not, so doubling `nu23` moves `D` by an ulp where a
trig expansion would be bit-identical. That ulp is the *evidence* that the independence is a
cancellation — a theorem — rather than an accident of which terms were typed.

### A ply angle's sine and cosine come from its magnitude

Quarter turns are read from an exact table (the repository's standing "a quarter turn is a
sign swap, never a `cos`" rule) and a negative angle takes `sin(|θ|)` negated. Both halves buy
an EXACT identity where a tolerance would otherwise be needed: a cross-ply's `A16`, `A26`,
`D16` and `D26` read exactly `0.0`, and a balanced ±θ stack's `A16` cancels bit for bit at an
angle no table covers. "Balanced means no shear–extension coupling" is then assertable with
`==`.

### What smearing drops is REPORTED as a number

A solid element carrying one law has no memory of the stacking sequence, so:

- **Bending–extension coupling cannot be represented at all.** `ToElasticLaw` REFUSES an
  unsymmetric layup by name (naming the largest `B` entry and the coupling ratio) rather than
  returning a law that is quietly wrong about warping.
- **Flexural stiffness survives only where `D` agrees with `h²A/12`.**
  `FlexuralDiscrepancy` measures exactly that: **0.401** for a `[0/90]s` cross-ply, whose
  outer plies dominate bending. Reported rather than refused, because refusing it would refuse
  every real laminate.
- Interlaminar stress and delamination are outside the model — a smeared law has no ply
  interfaces to separate.

### The failure index is load-normalised, and that is the whole comparability argument

Tsai–Hill's and Tsai–Wu's left-hand sides are quadratic, so their raw values are comparable
neither with each other nor with max-stress nor with themselves at another load. The published
`FailureIndex` is `1 / StrengthRatio` — the load fraction of failure — which makes all three
linear in the load and makes the uniaxial reductions exact: a pure fibre-direction tension
gives `R = Xt/sigma` for every criterion, which for Tsai–Wu is algebra (the discriminant
`F1² + 4F11` collapses to `(1/Xt + 1/Xc)²`) rather than an arrangement. The definition is then
verified through the SOLVER — scale the traction by `R`, re-solve, index 1.000000000000 — the
oracle `FatigueAnalysis.SafetyFactor` already uses, and the one an independently rewritten
formula could not provide.

`F12*`, the one Tsai–Wu coefficient no uniaxial test determines, is a stated parameter with a
default of −0.5 rather than a constant in the evaluator, and `|F12*| >= 1` is refused by name:
past that the quadratic form stops being positive definite and the failure surface opens into
a hyperboloid, so some arbitrarily large biaxial state would read as safe. That is §3h's
"a Cholesky IS the statement" argument one dimension down, in closed form.

### The frame is read from the law, and the strengths never restate it

A directional strength is quoted along the same 1-2-3 axes the moduli are, so
`LaminaStrength` carries no frame: `ElasticLaw.Frame` is retained and
`ElasticLaw.ToMaterialFrame` is the one way to ask. A second copy would be a second spelling
of one fact, free to drift from the stiffness it describes. `ToMaterialFrame` evaluates
`e_i·(sigma·e_j)` — six dot products, no Voigt vector — so it shares nothing with the
stiffness rotation next door, which is the §3h oracle rule applied to the production code
rather than only to the test.

### Evaluated per (node, region) slot, worst-wins at an interface

Both the frame and the allowables belong to a region, so a material interface has one honest
answer per material (`FailureIndexIn`) and the published per-node field takes the WORST. That
is deliberately different from how the stress field blends there (§3i): a failure index is a
max-type quantity, and averaging two materials' indices reports a number neither carries.

### Out-of-plane stress is measured globally, never per node

The criteria consume `sigma1`, `sigma2` and `tau12` only, so
`FailureResults.MaxOutOfPlaneFraction` says whether that idealisation is defensible. It is the
largest out-of-plane magnitude ANYWHERE over the largest in-plane magnitude ANYWHERE, and
dividing per node instead is the small-denominator trap: a lightly stressed node makes the
quotient large and meaningless, which measured **4.4** on a tension panel loaded purely in its
plane against **0.029** for the global form.

### No strengths at all is a refusal; some is NaN

Asking for a criterion when no region states a `LaminaStrength` is refused by name — an
all-NaN field looks like a solve that ran and found nothing. Where some regions state one, the
others publish NaN, the no-value spelling ranging and the colour map already skip; zero there
would paint the safest colour on a part nobody has checked.

### Not in v1, named

Thermal and moisture loads on a laminate (CLT's `N_T`/`M_T`), progressive first-ply failure
with stiffness degradation, Hashin's mode-separated criteria, interlaminar/delamination
criteria, and buckling of a laminated plate from `D` (which needs a shell element, not a
smeared solid).

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
- **Batched gradients (`Sdf.Normals`) are the Hermite seam**, added because dual
  contouring wants a normal at every surface crossing and a gradient is six evaluations:
  one batch of six times the length through the same SoA seam, and bit-for-bit identical
  to the scalar `Normal` for the same reason the distance batch is. The overload reporting
  **|grad|** is the interesting half — the unit normal throws that away, and it is exactly
  what a caller needs to turn a field VALUE into a distance. It is 1 for every exact
  distance field and less for the lower-bound fields the smooth operators document, so it
  is the engine's own contract made measurable rather than a new one.
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
- **Domain operations, and the property they cost.** A translate, a rotate, a mirror and a
  repetition are ISOMETRIES, so composing a field with them leaves a distance a distance —
  that is why those sit beside the set operators with no caveat attached. A **twist, a bend
  and a taper are not**: they shear or stretch space, so the composed value changes faster
  than the query point moves. What survives is the SIGN, exactly, because the solid is
  exactly the pre-image of the child; what does not is the magnitude, which becomes an
  over-estimate. `Elongate` joins the isometries in effect (its map is 1-Lipschitz per
  component: exact outside the stretched body, a strict lower bound inside the clamped
  core). `Displace` is a fourth case and the odd one — it adds a value rather than moving a
  point, so it is not a distance at all and the solid is `{d + ripple < 0}` by definition.
- **`Sdf.LipschitzBound(region)` is what keeps the non-isometries safe, and its shape was
  forced by the geometry rather than chosen.** Three consumers reason "a value of |d| proves
  no surface is within |d| of here" — the Surface Nets block cull, the narrow-band octree,
  the projection target's step — and each would drop geometry SILENTLY under a sheared
  field. So the node reports the factor and each widens by it. It takes a REGION rather
  than being a scalar because a twist's factor grows with distance from the axis, so no
  finite constant is valid over all of space; a consumer knows the region it is about to
  sample and asks once, and an infinite answer means "cull nothing", which is always
  correct. Default 1, so nothing that existed before pays anything — verified the only way
  that can be, by every committed docs PNG staying byte-identical. The design cost worth
  naming: this is the first thing in the engine a node can get wrong by saying NOTHING, so
  the guard is a measurement rather than a review convention (secants over the whole
  catalogue with a twist buried inside every wrapper, plus a wrapper built to forget and
  asserted to be caught).
- **One derivation serves all three non-isometries**, which is worth more than three
  formulas. Every one of their Jacobians reduces — after an orthogonal change of basis,
  free because singular values are invariant under one — to `[[g, w], [0, 1]]` beside an
  untouched unit direction. `DomainMath.ShearedScaleNorm` is that matrix's spectral norm in
  closed form; twist supplies (1, rate·r) and recovers the tidy `(k + √(k²+4))/2`, bend
  supplies the same matrix TRANSPOSED (a matrix and its transpose share singular values),
  taper supplies (1/f, r·f′/f²). The norm increases in both arguments, so substituting each
  one's largest magnitude over a region bounds it over that whole region.
- **Repetition visits two cells per repeated axis, and that is the correctness condition
  rather than an accuracy refinement.** The single nearest-cell map every shader
  implementation uses is DISCONTINUOUS at a cell boundary whenever the child is not
  symmetric about its cell centre, because the map jumps by a whole spacing there — and a
  discontinuous field is Lipschitz at no constant at all, so the cull could not be widened
  to cover it and would report surface where there is none. Visiting both neighbours makes
  the field continuous AND makes the sign exact, given one enforced precondition: the
  child's bounds must fit inside one cell. Outside it a query point can lie inside an
  instance the evaluation never visits, which is a wrong SIGN — refused by name with the
  measured span, not documented as a caveat. Verification is an identity rather than a
  tolerance: a lattice must equal an explicit `Sdf.Union` of translated copies **bit for
  bit**, since both spell `child(p − spacing·n)`.
- **AST compilation (`Sdf.Compile()`) is bit-identical by construction, and where that
  construction lives is the decision.** Each node emits its own expression, term for term,
  through an internal virtual — NOT a type switch inside the compiler, which would be a
  second copy of every formula free to drift from the one it claims to mirror. A node with
  no expression form emits a call back into its own `Evaluate`, so compilation always
  succeeds and is always exact; it simply stops paying for that subtree. Measured, it buys
  1.02–2.67× over the scalar walk in proportion to how much of the cost is DISPATCH, and
  **loses to the SIMD batch path by 1.2–3.4× in every case** — so it serves callers stuck
  with per-point queries and is not a faster way to sample a grid. The asymmetry with
  vectorization is the interesting part: the gyroid COMPILES (an expression tree calls
  `Math.Sin` itself, so it is bit-identical) where it deliberately does not vectorize, so
  the two are not the same trade.
- **Lattices are two families with two distance contracts, and keeping them apart is the
  design.** A **TPMS** is a level set of a trigonometric polynomial — neither a distance nor
  1-Lipschitz — so the field divides `|F|` by the GLOBAL `max |grad F|`, which is exactly
  what makes it 1-Lipschitz and therefore a lower bound on the distance (a 1-Lipschitz
  function vanishing on the surface satisfies `|g(p)| = |g(p) − g(nearest)| ≤ |p − nearest|`).
  Dividing by anything smaller would break the assumption `SurfaceCull`, the narrow-band
  octree and the projection target all rest on, in the direction that drops geometry
  silently. A **strut lattice** is a periodic union of capsules, whose distance is exact, so
  its `LipschitzBound` stays 1 and its diameter means what it says. They therefore live
  behind different factories rather than one `Lattice(kind)` that would hide which contract
  a caller just bought.
  - **Every TPMS constant is DERIVED, and the last three share ONE derivation.** √3 for
    Schwarz P, Schwarz D and the gyroid; exactly 7 for Neovius; 3√3 for I-WP; and then the
    **diagonal lemma**, which is worth more than three formulas: every polynomial here is
    CYCLIC in (x, y, z), so the diagonal x = y = z = t is invariant under the cycle, the three
    partials are equal on it, and |∇F| there is |F_diag′(t)|/√3 — a ONE-VARIABLE problem. For
    the shape Lidinoid and Split P share it gives |∇F| = 2√3·|sin 2t|·|A cos 2t + E| with
    A = a − 2b and E = −e, maximized where 2Ac² + Ec − A = 0; Lidinoid falls out as exactly
    3√3/2 (which the file had recorded as "no derivation attempted, the scan lands on it") and
    Split P as the quadratic surd at c* = (√454 − 2)/30. **Fischer–Koch S is the one member
    whose maximum is not on the diagonal** (there it is only √3), so it has its own invariant
    family (t + 3π/2, t, π/4), on which F vanishes identically — the maximum sits ON the
    surface, as Schwarz P's does — and |∇F|² collapses to a degree-6 polynomial G(sin t) whose
    derivative, after v = √2·u, factors as √2(v+1)(3v⁴ + 7v³ − 11v² − 7v + 4): the maximizer is
    the root of an INTEGER QUARTIC, hence solvable in radicals, so the constant is an explicit
    algebraic number rather than "no closed form found". **The stored constants are unchanged**
    — they already round up at the sixth figure, the safe direction, worth three parts per
    million of wall, where re-storing them would move every rendered lattice for nothing. The
    tests check each closed form against a global scan AND the load-bearing STEP of each
    derivation (the diagonal identity on all eight kinds, the family reduction, the
    factorization), because a value can agree by coincidence where a structural claim cannot;
    the slope test asserts **at most 1 and at least 0.99** — sound *and* tight, because a loose
    constant costs wall thickness in direct proportion.
  - **The cost of that constant is the wall, and the excess is INHERENT — no choice of divisor
    fixes it.** The sheet's local half-thickness is `(bound / |grad F|)·t/2`, so the requested
    thickness is a guaranteed MINIMUM and the excess is how far the local gradient falls short
    of the global maximum — measured area-weighted on the level set from 1.15 (gyroid) to 2.32
    (Neovius), worst 37.7 there and 27.4 at Lidinoid's near-critical point. **A sheet is a BAND
    of a level set and a band's width is 2L/|∇F|**, so it varies wherever the gradient does:
    dividing by a different CONSTANT (the surface maximum rather than the global one) only
    rescales the distribution, and dividing by the LOCAL gradient would make the wall
    first-order uniform at the cost of the 1-Lipschitz contract the field exists to keep —
    twenty-odd times steeper at Lidinoid's near-critical point, so the cull would widen by that
    and buy nothing. So the wall is REPORTED (`Tpms.WallThickness` → a `SheetWall`) and
    `Tpms.SheetForWallThickness` solves the nominal thickness for a stated median (a 1.0 mm
    wall asks for 0.43 on Neovius and 0.87 on the gyroid), verified POINT BY POINT against a
    direct march of the sheet's own field rather than distribution against distribution — two
    medians can disagree merely by being taken over different measures of the surface. Under 3%
    inside the regime the first-order relation claims (the band locally parallel, i.e. an
    excess factor under two), with the points past it COUNTED rather than quietly dropped, and
    `SheetWall.Maximum` documented as an upper bound that over-states exactly there.
    `Tpms.SheetForVolumeFraction` and friends still solve for the fraction and report what
    they ACHIEVED (the `BiArcFit.MaxDeviation` convention), measured on a grid sharing no
    sample with the one the parameter was solved on.
  - **Graded lattices grade the PARAMETER, never the cell, and that scoping is the design**
    (`LatticeGrading`; graded overloads of `Sdf.TpmsSheet`/`TpmsSolid`/`StrutLattice` plus
    `Tpms.GradedSheetForVolumeFraction` and `StrutLattices.GradedForVolumeFraction`). Grading a
    thickness leaves the structure underneath exactly as it was — a TPMS's polynomial is still
    periodic, and a strut lattice's fold and three-wide candidate neighbourhood are arguments
    about the strut AXES, which do not move — so the exact sign, the completeness of the
    visited neighbourhood and the periodicity are inherited rather than restated, and the only
    cost is the Lipschitz bound, which becomes 1 + the grading's own. Grading the CELL SIZE
    would be a different and much larger feature (the fold stops being a fold and there is no
    sound evaluation to fall back on), so it is refused by omission rather than approximated.
    **The grading's constant is STATED, never measured** — `Along` and `Radial` derive theirs
    exactly, since a coordinate along a unit direction and a distance from a point are both
    1-Lipschitz and the clamp cannot steepen either, while `FromFunction` makes the caller say
    it, because sampling a function proves nothing about it between the samples and a constant
    that is too small drops geometry silently. A volume-fraction grading rides the same
    measured cell distribution the uniform solves use, as a piecewise-linear ladder, so the
    composed constant is exact (the map IS the ladder) and a query is a lookup rather than a
    bisection. **A constant grading reproduces the uniform field BIT FOR BIT**, which is the
    identity saying this is one geometry; what a graded field gives up is the volume-fraction
    ESTIMATOR's premise, since "sample one cell" needs a periodic field, so no achieved
    fraction is reported and what the grading states is the fraction the LOCAL cell would
    carry. A CONFORMAL lattice is already expressible by composing `Twist`/`Bend`/`Taper`,
    each of which reports its own factor; a general free-form warp is a different feature.
  - **One term table per surface is the single source of the scalar evaluator AND the emitted
    expression**, so the compiled form is bit-identical by construction rather than by
    testing — eight formulas rather than sixteen copies free to drift. It is also what let
    the gyroid move out of `Primitives.cs` with its field bit-for-bit unchanged.
  - **`Sdf.Repeat` cannot express a strut lattice, and that is a finding rather than a gap.**
    A lattice's struts span the whole cell, so a capsule's bounds overhang by the strut
    radius and the fits-in-one-cell precondition — the thing that makes the
    two-cells-per-axis window sound — refuses it, correctly. Shortening the axes so the
    solids fit would make consecutive copies meet at a tangent POINT rather than joining. So
    the node folds the query point and visits a three-wide neighbourhood, with the pruning
    done ONCE at construction into per-sub-cell candidate lists (exact, since the distance to
    a segment is convex so its maximum over a box is at a corner). Two cheaper strategies
    were measured and lost; see the implicit README's table for the numbers.
  - **The strut batch path vectorizes by grouping the POINTS, and neither obvious shape is
    how.** The obstacle is that the candidate list is chosen per point, so four lanes can want
    four different lists; padding every sub-cell's list to the longest throws away exactly the
    pruning that made a query affordable (648 segments against a handful), and gathering per
    lane needs a width-agnostic gather `Vector<double>` does not have. What is left is to make
    the lanes AGREE — partition the batch by sub-cell with a counting sort, then walk each
    bucket's points a register at a time with the strut broadcast as a scalar, so the struts
    become constants and the points become the vector. Measured 2.0–2.8× over the six kinds
    (win-x64), bit-identical by construction: the fold and the bucket index go through ONE
    shared rule both paths ask, the kernel mirrors the scalar term for term with the segment's
    own `LengthSquared` broadcast so the division is the identical double, and `Vector.Min`'s
    ±0 tie-break cannot reach a result whose every quantity is a sum of squares squared again.
    A GRADED lattice takes the scalar loop (the radius is a delegate call per point).
- **The two primitives whose published formulas did not survive measurement** are recorded
  under the numerical lessons in CLAUDE.md: the ellipsoid (a genuine lower bound outside, an
  over-report of depth inside, with the error table against an exact Lagrange-multiplier
  oracle — and the finding that the first oracle convicted a *sphere* of an 86% error), and
  the square pyramid (Quilez's closed form is exact for the pyramid with its base REMOVED,
  reading 5.831 against a true 3.0 below the base centre, so `Sdf.Pyramid` takes the
  minimum over its own boundary triangles instead).
- **`Sdf.ConvexPolyhedron` takes the pyramid's route too**, and the split is where the
  arithmetic changes rather than where the code is convenient: INSIDE, the max over the face
  half-spaces IS the distance (for a convex body the nearest boundary point lies on the
  nearest face plane) and is returned unchanged, bit for bit; OUTSIDE it understates wherever
  the nearest feature is an edge or a corner, so the node takes the minimum over its own
  boundary TRIANGLES — grouped per plane from the vertices it was already enumerating for
  `Bounds`, ordered by angle in the plane and fanned, with winding irrelevant since a distance
  does not read it. The cheap form stays nameable as `ConvexDistance.HalfSpaceBound` (two
  estimators answering one question must both be nameable) because the query then costs a
  Voronoi-region test per triangle rather than one dot product per plane.
- **The ellipsoid's Lipschitz bound is derived per AXIS**, which is what makes it exactly 1
  for a sphere where the earlier `2 + (rmax/rmin)²` reported 3: every component of the
  gradient carries the same factor `w_j = p_j/r_j²`, so `|∇V| ≤ max_j |(2 − μ) + u(μ − 1)|`
  with `u = 1/k0` and `μ = ρ²/r_j²` — bilinear, hence four corner evaluations over a region's
  own `k0` range. **And the `k0 ≥ ½` regime the derivation always carried is a real
  restriction, now measured**: the field is genuinely DISCONTINUOUS at the centre of a
  non-spherical ellipsoid, tending to `−|Ad|/|A²d|` along direction d, which is −rmax down the
  long axis and −rmin down the short one. So no finite constant covers a region containing the
  centre, `u` is capped at the regime rather than reporting infinity, and what the consumers
  rest on there is the weaker property the field does keep — its magnitude never exceeding a
  bounded multiple of the true distance — so the cull's conclusion holds where its stated
  premise does not.
- **`Displace`'s bounds needed no fix, and the reason is that the property they rest on is
  narrower than it looks.** Material appears wherever the child reads below the amplitude, so
  what `Bounds.Expanded(amplitude)` needs is `{child < t} ⊆ Bounds.Expanded(t)` — the child
  never reporting LESS than the per-axis escape from its own bounds — which is weaker than
  "the field is a true distance" and which the whole CSG family satisfies by induction (an
  exact primitive gives at least its own escape; a union's minimum is at least the escape from
  the union of the boxes; an intersection's maximum is the per-axis maximum of its operands'
  escapes, which IS the escape from the intersected box). So the standing counterexample, a
  difference near its tool's fictitious faces, is covered. What is NOT is a child whose own
  bounds were widened relative to the field it reports, i.e. the non-isometries:
  `Sphere(1).Taper(1, 3)` reads 0.667 at a point 1.0 outside its box. Hence
  `Displace(amplitude, frequency, bounds)`, the stated-region overload, with both halves
  pinned by test.
- **A VECTOR-kernel compiler is a separate project rather than a residual, and the ceiling is
  measured.** It would remove the virtual call per node per chunk and the pooled scratch each
  operator writes and its parent reads, and it cannot remove the AoS→SoA transpose or the
  arithmetic. On a union chain the MARGINAL cost of one more node is flat at ~1.36 ns/point
  from depth 4 to 48 while a lone sphere — carrying the whole transpose by itself — costs
  1.85 ns, so the per-node plumbing has already been amortized to below the arithmetic and
  what a vector compiler could remove is a fraction of 1.36 ns. Against that: every node's
  expression rewritten against `Vector<double>`, plus a per-node masking fallback for the
  deliberately scalar ones, to which the recorded "block granularity destroys per-lane
  savings" rule then applies.

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
  √ε ≈ 1e-8, past the 1e-9 weld tolerance. A partial revolve of a SINGLE closed NURBS
  profile (an elbow with a one-curve tube section) has no segment junctions, so the sweep
  traces no axis-centred rail arc anywhere and the multi-segment angle recovery finds
  nothing — this used to come back SILENTLY as a full turn (2π for a 1.2 rad sweep, zero
  diagnostics), refused by the tessellator's full-domain gate three stages later.
  `TryAngleFromRotatedCopy` reads the angle in closed form as the azimuthal rotation between
  corresponding samples of the two congruent boundary curves (the generator and its rotated
  copy), and the closed-generator diagnostic is exempted there since the face genuinely
  covers the whole generator. **What remains unreachable is a closed NURBS generator used
  PARTIALLY under a partial sweep with no rims** — nothing exports one (the boolean would
  have to split such a face, and those faces refuse tessellation before any boolean sees
  them), so it is a documented boundary of the `TrimmedFaceRefusalTests` pattern rather than
  open work.
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
- **"Refinement is not a convergence mechanism" was the right lesson and only half the
  rule; the other half is that refinement may not make a face WORSE.** Interior rows
  demoted `Refine` to residue duty, which is where it belonged — but residue duty still
  let it do damage wherever the base's quality is capped by something refinement cannot
  see. The measured case is a boundary COARSER than the interior grid: a marching-tracer
  rim keeps whatever sample count the tracer's arc-length step gave it however fine the
  grid becomes, so an interior edge from that rim to a dense natural row is oversized by
  the step metric and gets bisected — and lifting the midpoint onto the surface swings
  the two halves past it, turning a correct facet into an inverted one. Refusing the
  split leaves the parent facet, oversized and correct: exactly the fidelity trade
  `Refine` already documents, now taken deliberately. The test compares each child's
  facet-vs-surface agreement against `min(parent, 0)` — no constant, and it states both
  halves at once (an agreeing facet may not become opposing; an already-opposing one may
  not get worse). **The reason this is worth recording is what it says about diagnosis,
  not about refinement**: two residuals had been filed against the BASE triangulation —
  the torus-with-a-bore as "the periodic-band tier pairs its chains by u and falls to the
  inverting merge walk", the drilled sphere implicitly by being audited only where it
  looked clean — and driving the same faces with `refine: false` showed the base
  fold-free at every density tried while refinement inflated the torus's tube halves ×4.1
  and inverted 53 facets, and gave the drilled sphere 127 folds at 192/96. The merge walk
  was reached **zero** times. A tier was blamed for a stage that ran after it, and the
  measurement that settled it was simply turning the later stage off — the same move that
  settled the ear-clipper's convergence stall, and worth reaching for earlier: **when a
  pipeline stage is suspected, run the pipeline without it before theorising about it.**
- **A degeneracy rule stated as "exactly zero" is a rule that does not fire, and the
  monotone sweep's turn test was one.** The sweep pops at a convex turn and deliberately
  does NOT treat collinear as a turn, because a ring's samples are collinear in uv and
  popping there emits the zero-area facets the whole trimmed tier exists to avoid. That
  intent was right and the test was `cross > 0`, i.e. exact — so on a constant-parameter
  boundary run, where the true cross is exactly zero, the decision fell to the pullback's
  own round-off. The consequence is not a harmless degenerate facet, and this is the part
  worth keeping: **uv-collinear is not 3D-collinear**, so three consecutive samples of a
  curved rim span a REAL facet whose normal is the rim's binormal rather than the
  surface's — the same trap the tier order was designed around, arriving through the tier
  that was supposed to be the cure, and invisible to every sliver guard because the facet
  is degenerate only in uv. It was found by a fingerprint rather than by a symptom: every
  fold on a threaded rod's 45° lead-in chamfer measured facet-vs-surface agreement of
  **exactly −0.7071**, which is −cos 45°, the angle between the cone and the end plane the
  fan was lying in. The fix is to test the dimensionless SINE of the turn
  (`|cross| ≤ 1e-9·|b−a|·|c−b|`), and the reason that is not a tuned constant is that
  dividing by the two edge lengths *separates* the populations instead of shrinking both —
  the noise is absolute in uv while a genuine turn scales with the chord, so the measured
  gap is ~4e-12 against ~1.6e-2, ten orders. Radians being dimensionless is why this guard
  is deliberately absolute where the ladder's default is relative. **The oracle that
  proves it exact is the facet COUNT, not the fold count**: over 76 scanned chamfer
  depths, exactly the 10 that folded changed and the other 66 stayed byte-identical, with
  every changed row keeping its facet count to the unit — so the guard adds and removes no
  geometry and only stops arithmetic from choosing a diagonal. Same shape as the
  `PolygonFan` tie guard (408 of 960 UV-sphere quads decided by an ulp) and as
  `MeshDecimator`'s absolute-epsilon-on-an-area: a predicate is only as meaningful as the
  scale it is compared against.
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
- **Surface fitting is the tensor-product generalization, and the shared parameterization
  IS the design** (`NurbsSurface.InterpolatePoints` / `Approximate`, The NURBS Book §9.2/§9.4;
  the approximation half of `GeomAPI_PointsToBSpline`, curve fitting having existed). Both
  compute chord-length parameters along every column and every row and AVERAGE them per
  direction (eqn 9.7), so the two directions share ONE parameterization — the loft-crack
  rule stated for a grid rather than a strip: a per-line reparameterization would put every
  column on its own v mapping and the fit would no longer pass through the grid. The surface
  is then SEPARABLE — interpolate/fit each column in u, then the resulting control rows in v
  (A9.4/A9.7) — so no 2D surface solve is needed, and because the parameters and knots are
  shared, each direction's collocation or normal-equations matrix is built and factored ONCE
  and re-solved per line. Interpolation reuses the curve's natural-end cubic verbatim (two
  points degrade to a straight segment); approximation FIXES the two endpoints of every 1-D
  fit — so the four corners interpolate their corner points and the boundary curves fit the
  boundary rows — and solves the interior control points from `NᵀN·P = R`, a small symmetric
  banded SPD system carried by a dense Cholesky (a fit's control count is small by design, so
  the CSR assembly of a general sparse solver buys nothing). The net is sized either by
  explicit per-direction control counts or grown to a tolerance (`GeomAPI_PointsToBSpline`'s
  automatic mode), which always terminates because a full-count net is a determined system
  whose residual is round-off. **The oracles are exact rather
  than convergence bands, which is what a fit lets you have**: interpolation passes through
  every grid point to round-off and a coplanar grid stays exactly planar (control points are
  affine combinations of coplanar data); approximation reproduces every grid point to
  round-off at the full control count (a determined system has zero residual) and — because
  least squares is LINEAR in the data — a coplanar grid stays exactly planar however coarse
  the net, since an affine relation among the coordinates survives the fit. Nothing downstream
  consumes surface fits yet; periodic grids are the named gap.

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

**Elliptical arcs joined the family, and what they cost is instructive.** `Ellipse2d`
beside `Arc2d`, `EllipseSeg` beside `ArcSeg`, both storing the centre and both semi-axis
**VECTORS** rather than (rx, ry, rotation) — so a rotated ellipse is the ordinary case
instead of a third parameter, an affine map of the axes is an affine map of the curve, and
the form is `Ellipse3d`'s verbatim so the lift invents nothing. Four of the five things a
segment must supply stayed closed form: the signed area is `½(C × (End − Start) +
(A × B)·sweep)`, which *reduces exactly* to the circular formula at A = (r, 0), B = (0, r);
the bounds solve `dx/dθ = 0` analytically instead of sampling; the flattening bound is
`(|A| + |B|)·Δ²/8`; and the y-monotone parity pieces invert `y − C.y = R·sin(θ + φ)` in
closed form, so an ellipse's ray crossing is as exact as a circle's rather than a bisection
like a bézier's.

The fifth is the **distance**, and it has no elementary closed form (point-to-ellipse is a
quartic). Rather than write a fifth scan-and-Newton, `EllipseSeg.Distance` delegates to the
`Curve2d` base's — one implementation, one documented contract (every candidate is a real
point ON the curve, so the answer can only over-estimate), and the segment and the 2D curve
family cannot disagree. The cost is stated rather than hidden: an elliptical sketch lands in
`SketchRegion`'s `General` tier where every other kind has a lane kernel, filed with the note
that the batch contract there is bit-identity, not a bounded deviation.

Three further decisions. `Ellipse2d` does **not** override `TryToCurvedEdge`, so it
correctly refuses the curved 2D arrangement — that tier's completeness argument is that
agreeing in tangent *and* curvature means sharing a carrier, which stops being true the
moment ellipses join (a circle osculates an ellipse at its axis ends). `EllipticalArcTo` is
SVG's `A` command verbatim, flags and out-of-range rule included, because that is the only
widely-shared spelling of this curve and matching it means an SVG path crosses either way
with nothing re-derived; it is solved by mapping the ellipse to the **unit circle**, where
the problem is the circular one already solved, and mapping back — legal because the map is
linear, so it carries centres to centres and the parameter across verbatim. And an ellipse
with equal semi-axes deliberately stays an `Ellipse2d` rather than collapsing to an arc:
silently changing a caller's type would make `IsCircular` and cylinder promotion depend on
whether two doubles happened to be equal.

**Point-on-object needed no new residual, and noticing that is the design.** A sketcher's
point-on-line is the point-to-line DIMENSION at zero — legitimate because that residual is
the *signed* offset `d̂ × (p − a)`, which passes smoothly through zero and is first order
there, whereas the point-to-POINT distance's zero is a cone point (which is exactly why
`Distance(point, point, 0)` is refused in favour of `Coincident`). Point-on-arc is
`ArcEndpointConstraint` — the row the solver already applies internally to an arc's own two
endpoints — asked with an arbitrary point index. So the whole feature is two public methods
and no new mathematics, and the two spellings of "|p − c| = r" cannot drift because there is
only one. Two policy choices ride on top: the carrier is INFINITE (a line's whole carrier, an
arc's whole circle), because a point-on-object that refused to let the point pass the drawn
stretch would be a branch selector wearing a constraint's name; and a point drawn exactly at
an arc's centre is refused BY NAME, since `|p − c| − r`'s gradient there is the undefined
direction `(p − c)/|p − c|` — the same stationary-configuration rule that names an `Angle`
mate between exactly-parallel directions rather than nudging it. What is NOT offered is
point-on-bézier or point-on-ellipse, and the reason is structural rather than effort: those
carriers have no closed-form signed residual, so the foot parameter would have to become a
solver VARIABLE, which is a different mechanism and is filed as such.

**The defect it exposed is the more valuable half.** `BRepTessellator` handed the
`segmentsPerCircle` density to `Circle3d` and nothing else, so an ellipse — whose parameter
is equally an angle over one turn — fell to the generic `curveSamples`. An elliptical prism
measured **0.64% under πabh at "256 segments per circle": the deficit of a 23-gon**. That is
not an accuracy tolerance but a wrong answer to the density the caller stated, and it was
already reaching real geometry, since `SurfaceIntersection` returns an `Ellipse3d` for an
oblique plane through a cylinder. The gate is now `IsAngularlyParameterized` — the condition
itself rather than a type that happens to satisfy it, the same "a tier's gate should BE its
correctness condition" rule the helical-band and ring-paired-band work recorded. With it,
the prism matches the *discrete* truth `(n/2)·a·b·sin(2π/n)·h` as an **identity**, which is
only available in closed form because nothing along the chain is flattened — the strongest
form of test this feature could have.

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

**The open-path STROKE completes the offset family, and it is where "exact" stops being an
adjective and becomes an equality.** `CurvedRegion2dOffset.Stroke(path, width, cap, join)`
sweeps a chain of lines and arcs — a toolpath footprint, a slot from its centre line, an SVG
`A`-command stroke — through the same union of primitives the offset uses: one FULL-WIDTH
slab per edge, corner joins offered on BOTH sides of every interior joint, end caps. Two
primitives are new and both are closed form. An arc's slab is the **annular sector** between
radii r ± w/2 over the arc's own angular span, which is precisely the set of points whose
nearest point on the path is interior to that arc, and whose area is
`(sweep/2)((r+w/2)² − (r−w/2)²) = sweep·r·w` — the squares cancel, so the test is an equality
rather than a bound. When `w/2 ≥ r` the band swallows the centre and the slab becomes the pie
SECTOR of radius r + w/2, still exact because every point of that sector sits at radius
between 0 and r + w/2 and so within max(r, w/2) = w/2 of the circle. Round caps are exact
half-discs, so with round caps and round joins the result **IS** the path's Minkowski sum with
a disc, where the polygonal twin's documentation has to say "short of it by the inscribed-arc
sagitta". That difference is a floor and not a tolerance, and it is measured: the same quarter
arc flattened to 4/8/16/32 chords and stroked polygonally approaches the curved answer
strictly from below and is still 1e-3 short at 32 chords.

**One contract deliberately differs from the polygonal twin, and the reason is the input
vocabulary.** A chain that returns to its start is stroked as a CIRCUIT — the closing joint
gets its joins, no caps are added — because a chain of EDGES makes closure structural, where
the polygonal `Stroke` takes POINTS and can only have closure spelled by repeating the first
one. Closure is read at the same weld tier the chain's own continuity is checked at, so "is
this a chain" and "is this a circuit" cannot be answered by two different tolerances. It
changes nothing under round joins with round caps — a full disc at the closing vertex contains
the join wedge, so the two readings agree as sets — and it is exactly what stops a
butt-capped circuit carrying a notch or a mitered one losing its last corner. The notch is
MEASURED rather than asserted in prose, and pinned by a test so the residual filed against the
polygonal twin cannot rot into a guess: a 10×10 square at width 2 with miter joins comes back
79 through the points spelling against 80 through the edge one, short by exactly the 1×1 outer
corner square at the repeated start point.

**The test that earns its keep is not an area formula.** Stroking a simple closed loop by w is
the SAME SET as growing the region it bounds by w/2 and taking away the region shrunk by w/2 —
and `Stroke` and `Offset` reach that set through different primitives (two-sided full-width
slabs and two-sided joins against one-sided slabs, plus the complement trick for the shrink),
so agreement is two constructions checking each other rather than one checking its own
arithmetic. Asserted for round, miter and chamfer joins on a square and for a disc, where the
annular-sector slab and the one-sided offset sector have to agree about the same curved band.

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
- **The straight-generator revolve family, from both ends.** A full-turn revolve whose
  generator is a straight line has exactly three shapes, and each is a surface the kernel
  already knows spelled differently: perpendicular to the axis it is a PLANE restricted to
  an annulus (`TryRevolvedDisk`), parallel to it a CYLINDER restricted to an axial band
  (`TryCylinderCarrier`), and slanted a cone. The first two matter because a `Shape.Drill`
  tool is ONE axis-touching revolve, so a drilled hole presents a `RevolvedSurface` for its
  flat bottom AND its wall where a `Shape.Cylinder` presents a `PlaneSurface` and a
  `CylinderSurface` — the same solid arriving at the analytic tier in a form it did not
  recognize, and falling to the tracer. **Recognizing the wall is what closed the last
  fixed-sampling floor in the drilled-breakout family**: a plane parallel to the axis cuts
  the band in two straight lines, and until the carrier existed those arrived as a
  49-sample tracer polyline whatever the caller's density — the corpus's `drilled breakout`
  measured a worst facet-vs-surface agreement of 0.107 / 0.694 / 0.840 against a
  `Shape.Cylinder`'s 0.9992 / 0.9999 / 0.99998 on the same cut. The recognizer tests the
  radial VECTOR rather than the radius, which is what keeps "IS the carrier" honest about
  the PARAMETERIZATION as well as the point set: constant radius makes the swept set a
  cylinder, constant radial direction makes the generator straight and axis-parallel, so
  the band's own u IS the promoted cylinder's azimuth — a generator merely at constant
  radius (a helix) sweeps the same points under a different parameterization and is refused
  by the same test with no separate guard. The band's own axial extent then clips the
  answer (`PlaneRevolved`'s "no circle above a blind bore's end" rule, in the parallel
  member), and an oblique plane's conic is accepted only when it lies wholly inside that
  extent — one comparison, since the axial coordinate along a conic ranges over its centre
  ± hypot of the two semi-axis components — falling through to the tracer otherwise.
  **Each new arm is placed AFTER the incumbent ones**, so every circle a drilled cap
  already produced keeps its own arithmetic bit for bit and only cases that previously
  marched can move; that ordering is the whole safety argument for touching this switch.
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

#### Clipping a conic to a bounded patch — the harmonic, and why the ends weld

A bounded planar carrier meeting a quadric (`TryPatchQuadric`) gets the same exact conic a
real `PlaneSurface` gets, and for a long time it was accepted only when the conic lay
WHOLLY inside the parallelogram; a bore whose rim ran off the wall it pierces fell through
to the tracer, which could not close the boolean at all. Clipping it needs no new
machinery, because **the patch coordinates are affine in the point and a conic is affine in
(cos θ, sin θ)**, so each coordinate along the conic is exactly one harmonic
`c + a·cos θ + b·sin θ` and each of the patch's four edges is one equation
`R·cos(θ − φ) = level − c`, whose two roots are `φ ± acos(…)`.

Three decisions carry it.

**Membership is decided at interval MIDPOINTS, not by an inequality over the crossing
list.** The four edge constraints are independent — two crossings of `s = 0` can bracket a
stretch that leaves through `t = 1` — so the runs are not a simple alternation. Between two
consecutive roots the conic is strictly inside or strictly outside *every* constraint, so
one midpoint decides an interval exactly and no epsilon enters the test at all.

**A conic wholly inside is returned as ITSELF, by reference.** That is what makes the change
free for everything that already worked: an accepted input produces bit-for-bit what it did
(the whole suite and all 135 rendered docs images are unchanged), and a closed curve stays
closed, which the wrap-splitting and hole-splitting paths key on. A surviving run that
straddles the seam comes back as ONE `CurveSegment` running past the domain end — legal
precisely because the base is closed, and necessary, or the seam would leave two edges where
the geometry has one.

**Closed form is not an accuracy preference, it is what makes the endpoints weld.** A
clipped end becomes a VERTEX shared with the neighbouring face: a bore breaking out of a
wall's top edge ends exactly where the top face's own intersection curve begins. Measured on
a Ø6 bore off a wall's top edge, the arc's ends land on the patch boundary **exactly**
(`|z − top| = 0`) and within **0 and 4.4e-16** — 0 to 2 ulps — of the top plane's own
`±√(r² − d²)`, seven orders inside the weld tier, where the tracer stops up to one march
step short of the boundary and never reaches it. The result is that the same solid built
with BOUNDED walls (a sketch extrusion) and with UNBOUNDED walls (a box) now measures the
same at every density (1e-11), which is a stronger statement than either convergence table:
the clip reproduces the plane's own answer rather than merely converging near it.

The guard against the recorded `Promote` trap — "lies on the carrier" is not "IS the
carrier" — is geometric and measured rather than a type test: every sample of a clipped arc
must project into the extrusion's own `[0, 1]²`, and does, with escape **0.000**.

**The one place a tolerance is unavoidable is a TANGENCY, and the rule there is derived
from which mistake is cheaper rather than from a measured epsilon.** Near a tangency the
`acos` argument is within round-off of ±1, where `acos` has a square-root singularity, so
the two roots come back ~1e-7 rad apart however exact the geometry is, and the midpoint
between them reads inside or outside by round-off — neither reading more accurate than the
other. They are not equally *safe*, though, and the asymmetry is exact: dropping a run of
angular span δ removes a chord of `scale·δ` of genuine curve — an outright gap in the
boundary — while keeping it leaves the curve at most `scale·(1 − cos(δ/2))` outside the
patch, which is *second order* in δ. At a real tangency that is 2.7e-15 against a lost
8e-8, six orders apart. So a dropped run is flipped to kept whenever the excursion it admits
is within the weld tolerance, kept runs are never dropped, and a touching conic comes back
as the closed conic it is. This is the clip's own version of the standing "err toward
KEEPING" rule that `ClipToFace` states one layer up, with a derivation instead of a policy.

**And whether the round-off falls the safe way without that rule is ALIGNMENT rather than
tolerance**, which decides the shape of the test: two hand-picked tangencies both passed
with the rule disabled, and only a family sweep shows it firing — 62 of 480 configurations
come back with a pinhole in them without it, 0 of 480 with it. The same lesson as the
Surface Nets ambiguous face and the torus silhouette's pinholes; a single fixture would have
locked in a coincidence.

**And the entry that asked for this predicted the wrong failure mode**, which is worth
keeping. It expected the recorded fixed-sampling-floor signature (a non-converging error
whose sign flips). Measured, the case the tracer CAN reach — a blind bore off one wall —
converges quadratically through the tracer too, because `SnapTracerEnds` and `SampleEdge`'s
baked-carrier refinement already remove that floor once a usable branch exists. What the
tracer could not do was produce a usable branch at all when both walls are crossed, so the
failure being fixed is a REFUSAL rather than a floor.

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
- **A one-sided parity ray is correct exactly where the trim CLOSES in the direction it is
  cast, and a POLE is the one place it does not** (`FaceGeometry.ParityRayPointsDown`).
  Which way that breaks is decided by the generator's DIRECTION and by nothing else: a
  profile leaving the axis puts the rim at v = max, so the upward ray crosses it and an
  axis-touching revolve's flat cap behaves — which is why every drill tool has always
  worked — while a profile *returning* to the axis puts the rim at v = min, the upward ray
  crosses nothing, and the identical cap reads as having no interior at all. Measured on a
  cylinder built as ONE full-turn revolve, whose two caps differ in exactly that:
  `SplitByCurve` returned a single fragment (the rim edge split, no interior edge made) for
  56 of 112 (offset, azimuth, curve-kind) chords, and they were precisely the 56 on the cap
  whose generator ends on the axis.
  **The face splitter's arrangement asks the rule; `Contains` itself deliberately does not,
  and the boundary is a measurement rather than caution.** Made pole-aware there, the
  parity changes which STRUCTURE a split produces rather than merely which points read as
  inside: a sphere's cap then accepts an interior bore circle as a HOLE, where the incumbent
  reading sends the same cut down the band path — and a pole-bounded face carrying a hole is
  a trimmed tessellation tier that does not exist, so three documented constructions stopped
  meshing, by name. An algorithm that can only trade one refusal for another is not reached
  at all, so that correction waits on the tier and the upward ray there is load-bearing
  rather than merely incumbent. `ContainsTwoSided` is a third thing again and not a
  substitute: it errs toward INSIDE on purpose, which is right for a keep-or-drop decision
  and wrong for an arrangement, where accepting a segment that should have been dropped puts
  a phantom edge into the trace.
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
- **`FaceSplitter.SplitByCurves`** owns the choice between a **cascade** (curve by curve
  over the fragments the previous curves produced) and **one simultaneous arrangement**
  over all of them. The cascade is what booleans have always done and stays bit-for-bit
  what they get; it works because a curve entering and leaving through the face boundary
  closes its own arrangement, and a later curve then meets an earlier one's segments as
  ordinary boundary edges. It structurally cannot work when a curve TERMINATES INSIDE the
  face — the first curve applied has nothing to end on, and a dangling edge cannot be
  traced — which is the shape an intersection curve takes once it is clipped to the other
  face's trim. The simultaneous path nodes every curve against the boundary, against every
  other curve, and at coincident ENDPOINTS (a junction, not a crossing: two clipped curves
  meeting end to end touch, so no transversality test may be asked to decide it), then runs
  the same boundary splitting, parity-filtered segment construction and `TraceFaces` walk.
  The routing gate is evaluated before any topology is touched, because `SplitEdge` patches
  neighbouring faces' loops and a refusal halfway would leave them half-edited — and it
  requires a PARTNER at the terminus, which is what separates a clipped curve (whose corner
  the neighbour's curve continues from) from a tracer-truncated one (whose end is an artefact
  with nothing to meet, and which the arrangement could only refuse differently, one stage
  earlier and less informatively than the boolean's own two-manifold check).
  `BrepBoolean.SplitAll` hands each face its whole curve list, so the routing decision lives
  in one place.

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

**And the smaller change that looked worth evaluating is now REFUSED, by the curved-2D tier's
own finding.** The long-standing follow-up was to replace the finite-difference
`DepartureAngle`/`ArrivalAngle` with **exact analytic tangents** — every analytic curve
overrides `Curve3d.DerivativeAt`, so the 2% chord could become a true tangent pulled back
through the surface's Jacobian, removing the approximation the `1e-12` guard exists to
tolerate. Building `CurvedArrangement2d` established that this is backwards.

A 2%-along-the-edge chord is a **SECANT**, and a secant over a finite span encodes CURVATURE:
writing a curve as `p(s) = v + s·d + ½s²κ·n̂`, the chord to arc length s points along
`d + ½sκ·n̂`, i.e. the tangent rotated by about `½sκ`. So two edges leaving a node TANGENTIALLY
— which is not exotic here but the normal case, since every fillet band meets its neighbour
tangentially — have secants separated by `½s|κ₁ − κ₂|`, first order in the curvature
difference, while their exact tangents are separated by nothing but round-off. On an r = 2
fillet band against a planar neighbour at a band length of ~3, that is about 0.015 rad against
the `1e-12` guard: comfortably decided. With exact tangents the two would tie, the guard would
read the tie as the back-along-the-same-edge case and add a full 2π, and the walk would choose
by round-off — which is precisely the failure `CurvedArrangement2d` had to add a
**curvature tie-break** to fix (a disc tangent to a plate's edge gave the arc a departure of
`(−1.22e-16, −1)`, and the union came back EMPTY).

So the "improvement" would delete the information the comparison is running on and then have to
re-derive it, inside the code with the widest regression surface in the repo. The chord is doing
the tie-break's job for free, the `1e-12` guard is tolerating an approximation that is
load-bearing rather than merely acceptable, and the item is retired rather than deferred. What
would genuinely be a change here is the curved tier's own two-key comparator — departure tangent
with departure curvature as the tie-break — but that is a different, larger piece of work with
no reported defect asking for it.

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

A fifth member joins from the other end and is spelled separately for a reason: a **coaxial
annulus** — a revolve of an axis-PERPENDICULAR generator, which is a shoulder face, a washer
seat, or the flat that bounds a chamfer tool — has no `r = a + b·z` form at all, because its
b is infinite. It is therefore recognized on its own (`TryCoaxialDisk`) and cut as the
axis-perpendicular PLANE it is, clipped to its own radial extent, sharing the one
implementation of that cut with the `PlaneSurface` case rather than being folded into a
homogeneous `αr + βz = γ` form that would obscure which member is which. Recognizing it at
all is not a nicety: a chamfer tool's flat is exactly this surface, and without the arm the
pair fell to the marching tracer, whose polyline hugged the annulus's own v = 0 edge where
its rim sat on the crest cylinder and ended strictly inside the band — which face splitting
refuses by name, three faces from the cause.

**And the dr = 0 case must be computed in closed form, not left to the general expressions.**
A band with dr = 0 is a strip of a coaxial cylinder — a thread's crest or root flat — and a
coaxial cone meets one in a CIRCLE: the radius stays r₀ and the axial coordinate is the
single z where a + b·z = r₀. The general form reaches that circle only up to rounding
(dz·(b·rate/(−b·dz)) + rate is mathematically −rate + rate and lands ~1e-17 off for a pitch
whose ratios are not binary-exact), and `SpiralArc3d.IsPlanar` is an **exact-zero** test that
`BRepTessellator.IsFullHelicalBand` and every other downstream tier reads. So whether a crest
band's chamfer cut was recognized as the cap-SHAPED cut it is came down to which way the last
bit fell: the same 0.3 mm chamfer tessellated at one end of a rod and welded non-manifold at
the other, with nothing geometric between the two. Same family as the `Orient2d`-on-round-off
lesson — an exact predicate applied to a quantity that is round-off answers confidently and
wrong.

Two consequences worth stating. First, an end chamfer needs no traced curve at all, so the
`CornerPolicy.ExactOnly`/`AllowTraced` question that governs curved corners simply does not
arise for it. Second, **a thread RUNOUT is a member of this family rather than a new shape**:
a runout is what an incomplete (washed-out) thread is — the crests truncated progressively
toward the shank — which is a coaxial cone at a shallow angle, and nothing in the derivation
above mentions the angle. So `MakeThreadEndConeTool` takes the radial drop and the axial
length separately and `MakeThreadEndChamferTool` is it at equal drop; the runout needed a
parameter, not a surface. Two details carry it. The overshoot that keeps every OTHER face
of the tool clear of the rod is taken as a quarter of each extent SEPARATELY, which is the
same number twice at 45° and therefore leaves a chamfer tool bit-for-bit what it was. And
the cone drops to the **pitch** diameter rather than the minor one, because a cone reaching
the minor diameter is tangent to every root band along the end plane — the coincident
curved-surface input the full-depth chamfer default is already refused for.

`Sdf.Thread` learned the same cone SLOPE in the same pass, and that is the point rather
than a side effect: a runout modelled in one representation and not the other would make
one `ThreadShape` two geometries, which is exactly what the vertex-against-the-field check
exists to hold. The 45° arithmetic stays on its own exact-1 branch so every thread field
already in the repository is bit-identical (`a * InvSqrt2` and `a / Math.Sqrt(2)` are not
the same double).

##### The clearance profile, and why the generator had to learn arcs

A printing CLEARANCE was the one thread feature with no exact B-Rep counterpart, and the
reason is worth stating precisely because it is not "the geometry is hard": a clearance is
a **distance-field offset** of the (radius, axial) profile — that is what `Sdf.Thread`'s
own clearance is — and eroding the material MITERS its crest corners while ROUNDING its
root corners into arcs of the clearance radius. So the eroded solid is still ONE
boolean-free helical sweep; its generator simply mixes straight pieces and circular arcs.

**The miter-only alternative was refused rather than unexplored.** Offsetting every flat
and flank perpendicular to itself and mitering all four corners needs no arcs at all and is
a perfectly reasonable clearance convention — and it would make the B-Rep and the implicit
route two geometries, which is the one thing this kernel does not do. Either both change
together or neither does, and there is no reason for the field to change.

`HelicalSurface` therefore takes either generator, and the design decision was to EXTEND it
rather than add a sibling. The alternative reads safer (a sibling simply would not match
`TryCoaxialProfileLine`'s pattern, so the coaxial family would decline it and fall to the
tracer) and is worse where it counts: `IsFullHelicalBand`, `NaturalSteps`, `BrepSelection`,
`BrepArchive` and `GeometryTransform` all switch on the TYPE to reach helical-specific
machinery, and a sibling missing from any one of them falls silently into a generic path.
Extending puts the question where it belongs — `IsStraightGenerator` is an exact-zero test
on the arc radius, and the two consumers whose own derivation assumes straightness ask it
by name.

**The refusal `HelicalSurface` gained is the correctness condition of the cut, not
caution.** An arc generator's axial coordinate must be strictly monotone — equivalently
cos φ keeps one sign over the sweep. Substituting the arc into a coaxial carrier
α·r + β·Z = γ gives `ρ·cos(φ − ψ) = D + slope·u`, whose two arc-cosine branches separate
exactly when the arc stays inside one half-turn about ψ; a cap plane's ψ is π/2, so
"single-branch cap cut" and "z monotone" are one statement. It is also the contract
`MakeThreadedRod` already stated for its corners, read along the piece rather than only at
its ends. The branch itself is read off the arc's own angular range against the
representative of ψ nearest it, because an arc ending exactly at δ = ±π — its extreme
radius, which is where a coaxial cylinder meets it — merely touches that boundary, while an
arc reaching δ = 0 or ±π in its INTERIOR is tangent to the carrier there and is declined.

`SolidFactory.OffsetPitchProfile` is the erosion, and its corner rule is one expression:
`offset × turn` decides miter versus arc, so eroding an external thread and growing the tool
that cuts an internal one are the same code with opposite signs rather than two cases.

**A flat can vanish, and treating that as ordinary rather than as an error is what makes
the feature usable.** A 60° crest flat loses `|offset|/tan(30°)` of width per side, so an
M6×1's 0.125 mm crest is gone by a clearance of 0.108 — inside the 0.1–0.25 mm an FDM
printer wants — and the eroded thread is correctly a POINTED ridge where the two offset
flanks cross. That segment's offset half-plane has become redundant, which is exactly what
"its offset length went non-positive" measures, so it is dropped and its neighbours mitered
directly. The drop is sound only where both of its corners miter (the region is locally
convex there, so the erosion really is the intersection of the offset half-planes), and
anything else refuses by name.

**Two things the verification caught that a shape comparison would not.** The oracle is the
field: every lateral tessellation vertex of an eroded M6×1 rod reads |sdf| ≤ 2.0e-15 against
`Sdf.Thread`'s own clearance field at clearances 0.02 through 0.25, while the SAME vertices
read up to 0.2495 against the uncleared one — the control is what makes the first number
mean something, since a bound alone would pass a rod that had never been eroded. And the
*tessellation* had a real defect that only a facet-quality audit sees: **v is not linear in
u on an arc band**, so sampling the cap cut at uniform u — which every other curve here
wants — and pairing those samples with grid rows at uniform v shears every quad against the
cap it neighbours. Measured on a 0.05 clearance rod at 16 segments per circle: 308 folded
facets, worst normal agreement −0.366, and a residual that GREW with density (0.230 at 32,
0.529 at 48, 0.801 at 96) instead of converging. Sampling at uniform generator ANGLE
instead — `HelicalArcCut3d.ParameterAtAngle`, with the two ends taken from the domain
verbatim so the shared rail vertices stay bit-exact — fixes it, and the same rod then reads
0.365 / 0.690 / 0.916 and the corpus member (a 0.2 clearance) 0.457 / 0.979 / 0.995 against
floors of 0.383 / 0.924 / 0.981.

The band grid's interior shear needed the matching correction and it is the same fact from
the other side: the incumbent lerp between the two rails IS the exact shear for a straight
generator (u_left(v) is affine there) and a CHORD for an arc, whose sagitta measurably
exceeds one column, so the first interior column would land outside the cap it neighbours
and the mesh would poke past the end face. The arc path uses the exact axial form
`u(v) = u(0) + (z(0) − z(v))/rate`; the straight path keeps the lerp verbatim, so every
threaded rod already in the repository tessellates bit-identically.

**One residual is stated rather than filed**, because it is a property of the density a
caller asks for rather than a defect: a band's u chord sagitta must not exceed the band's
own height, or the facet normals are dominated by the sagitta. At radius r and n segments
per circle that is `r(1 − cos(π/n)) < ρ·|sweep|`, so a 0.05 clearance on a P = 1.25 thread
(ρ|sweep| = 0.052 mm at r ≈ 3.3) needs n > 18 and folds at 16. The default is 32 and every
figure in the docs uses 48 or more; a small clearance at a very coarse density is the one
configuration that does not hold, and it converges rather than sitting on a floor.

##### Terminating a traced branch on a bounded band's rail

The *non*-coaxial pairs — a cross-hole, a tilted face — are genuinely transcendental and
stay with the tracer. Two things had to be fixed for that to be usable at thread scale and
they are different mechanisms: **finding** the branches (the anisotropic second seed pass,
recorded in the performance mandates) and **terminating** them.

The march breaks its step only AFTER the corrector's parameters leave the domain, so an
open branch always stops up to one whole step short of the rail it was running into. On
ordinary geometry that shortfall is cosmetic. Here it is not, because the step is scaled to
the QUERY REGION while the band is not: an M8 crest flat is 0.156 mm tall against a
0.161 mm step over a 24 mm box, so ONE step crosses the whole band. Measured, branches came
back spanning v = [0.481, 0.819] of a band whose rails are v = 0 and v = 1 — reaching
NEITHER — while others were discarded outright by the three-point minimum. A curve that
reaches neither rail cannot split the face it lies on, which is why a cross-drilled thread
refused at every bore.

`TryLandOnDomain` solves the terminus rather than extrapolating to it: the tracer's own
Newton with one coordinate PINNED at its boundary value — three unknowns against the three
components of S_a − S_b = 0, spelled as `Solve4` with the plane row replaced by
`delta[k] = 0`, so the pinned coordinate keeps the boundary value bit for bit and the
landing lies ON the rail rather than near it. The seed usually cannot come from the
corrected parameters, because the corrector usually **refused** the step: `Eval` clamps a
non-periodic parameter, so a step past a rail evaluates the rail's own point, the partials
across it collapse to zero, and the corrector fails on a singular pivot without the domain
test ever running.

**The scope is the surface PAIR, not the seed**, and that is the decision rather than a
detail. Scoping by seed is the tidier reading of the additive contract the second seed pass
carries, and it leaves the isotropic grid's OWN branches on an anisotropic band ending
strictly inside the face: a cross-drilled M8 flank band still refused with six of them. The
condition that hides a branch from the isotropic grid and the condition that makes the
region-scaled step exceed the surface's width are the same condition, and it is a property
of the surface. Measured on an M8×1.25 6 mm rod: cross-drilling refused at 13 of 13 bores
from 0.6 to 3.0 and now builds Validate-clean, closed, converging solids at 8 of them.

Two riders. `BrepBoolean.SnapTracerEnds` must refuse a candidate landing **behind** the
trace: on a thread band the two rails are a fraction of a millimetre apart, well inside its
two-step reach, so a curve that now terminates exactly on one rail is within reach of the
other, and appending that doubled the polyline back over itself (domain [0, 0.479] whose
second half retraced the first). The incumbent "it is already on the boundary" test cannot
cover it, because it reads the MINIMUM over candidates and is therefore silent whenever the
true landing's own Newton misses — a rail helix seeded from 32 samples over five turns can
converge to another root entirely. And **halving the step and retrying a refused interior
step was built, measured and reverted**: it takes cross-drilling to 10 of 13, and it also
reaches whole-solid FILLET bands, which are anisotropic too (long, and only r·π/2 wide),
where it broke seven tests and took the tilted-plane family from 1 of 4 to 0 of 4. An
algorithm that can only trade one refusal for another should not be reached at all.

##### Tracing through a fold: the same remedy, scoped by its own condition

A refused step is one of two things and only one of them is a rail exit. The other is a
**fold** — the curve turns back within one step, so the corrector's constraint plane, taken
perpendicular to the tangent a whole step ahead, has no solution near the curve — and there
the branch stops mid-face with nothing to land on, which `FaceSplitter` then refuses by
name. Halving the step *is* the remedy for that; what was wrong with the reverted attempt
was not the remedy but the scope, since aspect ratio says nothing about which of the two a
refusal is.

`RetryThroughFold` is therefore gated on the condition that DEFINES a fold: the refused
step's own linearization lands strictly inside every bounded domain, so no boundary exists
to land on. That is the same test `TryLandOnDomain` makes before it does anything else —
asked (`LeavesDomain`) rather than restated, so the two cannot disagree about which case a
refusal is — and it is only ever reached where the trace previously stopped with nothing
appended, which is the bit-identity argument for every branch that meets no fold. Past the
fold the step walks back up to the pair's own, the standard continuation rule, so a fold
early in a long branch cannot spend its step budget and truncate it.

Measured on the same bore sweep: **two** bores stranded a branch inside a face (1.2 and
1.6) and now **one** does. The 1.6 bore's failure moved DOWNSTREAM to two unpaired edges
where a helix rail and its coincident cut segment run between the same two points — a
different defect the fold refusal had been hiding, and the one the 2.8 bore already had.
The remaining 1.2 case is not a corrector refusal at all: raising the halving budget from
5 to 14 leaves it byte for byte, so its branch stops for one of the trace's other reasons
(a tangential contact, a branch jump, the step cap) and a shorter step is not its remedy —
which is the useful half of a negative measurement, since it says where NOT to look next.

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

##### Clipping a carrier curve to the pair's shared trim — and why the rule is ASYMMETRIC

Step (2) intersects CARRIERS. A carrier is unbounded (a plane) or bounded only by its own
parameter rectangle — a helical band's domain is the bounding rectangle of a
parallelogram-shaped face — so the curve it returns runs past both faces. Step (3) already
discarded the stretches outside the face *it* was splitting; nothing discarded the stretches
outside the *other* one, so a face was split along geometry the pair does not share. The cost
is visible two ways: a pocket tool's four wall lines cut a host face into **9** fragments
where the tool's footprint asks for **2**, and a chamfer cone's cut ran past a threaded rod's
cap and arrived at the cone face as a dangling edge no arrangement can trace.

The obvious rule — hand both faces the stretches inside both trims — is wrong, and wrong in
the direction that matters. **Wherever the two faces share a boundary, clipping to the partner
cuts the curve exactly ON this face's own boundary**, turning a transversal crossing into a
tangential touch and leaving the arrangement an endpoint no boundary edge owns. Measured on
`Box(20,20,10) & Box(10,30,10)`, whose side walls meet along their full height: every vertical
curve stopped exactly on both walls' rims and tracing did not close. So the rule is
**asymmetric — each face drops only the stretches that lie inside ITSELF and outside its
partner**. Keeping the stretches that lie outside this face costs nothing (the splitter drops
them by loop parity) and restores the crossing, so the clip removes exactly the over-split it
exists to remove and nothing else.

The SYMMETRIC rule the boolean rejects is exactly what `BrepBoolean.Section` wants — and that
is not a contradiction, because a section asks a different question. A section is a WIRE, the
shared boundary of the two bodies, so a stretch outside either face is not on it; there is no
splitter to hand a dangling endpoint to, so the tangential-touch failure the asymmetric rule
exists to avoid cannot arise. `ClipToBothTrims` is therefore `ClipToFace`'s twin over the same
breakpoints and the same err-toward-INSIDE containment test, keeping `inside(fa) AND
inside(fb)` where the boolean keeps `inside(fa) AND NOT inside(fb)`. It consumes nothing (the
inputs are only measured) and its honesty is stated in the API rather than hidden: analytic
pairs give EXACT endpoints (a plane∩cylinder circle comes back as one closed curve), tracer
pairs sampling-resolution ones, so a section is a display/query answer, not sealed topology.
The oracle is a closed form where the curve is analytic — a drilled-through plate sections to
its two bore-rim circles, each sample on the radius to the weld tier (proving the wire is the
circle, not a chorded polyline) at the two cap heights, total length the closed-form `2·2πr`.

Two properties keep the two solids welding. The **breakpoints are shared**: one list per face
pair, the union of both faces' exact `CrossingParameters`, so wherever the pair genuinely
shares a stretch the two sides cut it at identical parameters. And a **curve that survives
whole is returned as ITSELF**, not as a full-domain segment, because wrap-splitting and
hole-splitting both key on `IsClosed` — which is also what makes every transversal boolean
bit-for-bit unchanged.

**The keep/drop test errs toward KEEPING, and that direction is the safety argument**: a
stretch wrongly kept only reproduces the un-clipped behaviour, while one wrongly dropped loses
a seam silently and the boolean returns two touching shells. It errs three ways — a probe that
does not project onto the surface at all counts as inside; parity is two-sided
(`FaceGeometry.ContainsTwoSided`), so a POLE-BOUNDED face answers instead of calling every
point on itself outside; and a stretch running ALONG the boundary counts as inside, because
where two solids mate the shared rim IS a face boundary on one side. Without the pole rule a
sphere-through-a-box union lost its whole seam curve and came back at Euler 4.

Three downstream mechanisms had to learn the same fact — **that a closed curve may now arrive
clipped**. The closed-curve seam anchor is conditional on both sides still seeing the closed
curve (a slot through a bore shares with the slot's floor only the arc inside the slot's
width, so the bore wall is cut there and never wrap-splits while the floor still sees the full
circle; the stale anchor left the +x arc as two edges against the wall's one). A closed CHAIN
that wraps the period must go to the arrangement rather than to `SplitByClosedCurveChain`,
since the two sides of a wrapping cut are two bands and neither is a hole in the other. And
that chain's pulled polyline must be unwrapped stepwise across its junctions, or the winding
comes out by luck (see the BRep README).

**The oracle had to be re-argued, and the honest answer is that it moved.** "All rendered docs
PNGs byte-identical" cannot be the bar for a change that legitimately re-decomposes faces: a
plate with a flush pocket went from 18 faces to 11 and a two-bore plate with a pocket from 20
to 13, and although the display mesh keeps its polygon count and its volume to nine decimals,
the *triangulation* of a face-with-one-hole is not the triangulation of eight rectangles. What
survives as an oracle is what the faces MEAN: fewer, better-shaped faces with the same volume
is an improvement; a changed silhouette would not be. Seven of the 106 rendered PNGs moved,
every one a B-Rep-boolean scene and none with a changed silhouette — including a
construction-preview overlay, whose exact B-Rep edges are pixel-for-pixel where they were. The
committed ambient-occlusion fingerprints were re-taken for exactly the two fixtures whose face
count fell, with the measurement recorded beside them.

One process note, because it nearly became a wrong conclusion: the first DocsGen pass was
polled *while it was still running* and reported four movers, so three more appeared on the
next run and looked like nondeterminism in the renderer. Two further runs from a clean
checkout produced all seven bit-identically (SHA-256 equal), which settles it the way this
project settles such things — re-verify the artifact rather than reason about the code. Never
read a build's output directory before the build says it is done.

#### Probing a pole cap that has been CUT — and a filed diagnosis that named the wrong stage

Step (4)'s probe has a special path for POLE-BOUNDED faces: a disc of an axis-touching
revolve has only its rim loop, so averaging the loop would probe ON the rim, and the probe
moves halfway toward the pole instead *and skips the parity check*. Skipping is legitimate,
and for a reason worth stating as a theorem rather than as a convenience: a single loop that
WRAPS the periodic direction separates the pole from everything else, so such a face is the
pole's side and every v strictly between the pole and the loop is inside AT EVERY u.

The same sentence says what the code had wrong. "The loop" has to be its **closest approach
to the pole**, not its average. The two coincide exactly when the loop sits at one v — every
pole cap bounded by nothing but its own rim — and part company the moment another solid CUTS
the cap, which leaves the loop wrapping but no longer level, so the average names a v the
face no longer reaches everywhere.

**That configuration is not exotic; it is what a blind `Shape.Drill` makes.** A drill tool is
ONE axis-touching revolve, so its flat end is a `RevolvedSurface` pole cap where a
`Shape.Cylinder`'s end cap is a `PlaneSurface` — and when the bore is blind that cap lands
inside the body, so the face the bore breaks out of cuts it. Measured on a Ø6 blind hole in a
40×30×10 plate with its axis 1 mm below the top face: the average put the probe **0.106 above
that top face**, i.e. in the fragment on the far side of the cut, so the piece that should
have been kept was classified away and the boolean refused with *"3 of 19 edges are used by 1
face(s)"* — the crack running the whole rim.

**The two-sided parity is not the fix**, which is worth recording because it is the obvious
reach: `FaceGeometry.ContainsTwoSided` errs toward inside by design (its own tie-break
resolves a disagreement to *true*), and on a cut cap the ray away from the pole crosses
nothing while the ray toward it crosses the cut — a disagreement — so it accepts precisely
the bad point. The rule above needs no parity test at all.

**And the filed entry that asked for this named the wrong stage.** It read: *"the fix is a
third planar-carrier recognizer — a full-turn revolve of a radial straight generator
perpendicular to its axis is a planar ANNULUS"*, on the reasoning that the pair fell to the
marching tracer. It does fall to the tracer, and that turns out not to matter: the
intersection there is a straight CHORD, and a traced polyline along a straight curve lies on
that curve at *every* point rather than only at its vertices, so the only defect the tracer
introduces is truncation at the ends — which `SnapTracerEnds` already removes. The recognizer
was built, and with it the whole boolean's output on this family is **bit-identical**.

Establishing that took the project's own rule — run the pipeline without the suspected stage
before theorising about it — and the recognizer was then measured on a sweep of breakout
depths rather than on the one fixture. On its own it **trades one refusal for another** and
so was not reached at all (`FaceSplitter.SplitByCurves`' gate makes the same call for the
same reason): it fixes the exactly-diametral cut and breaks two shallower ones, which fail in
trimmed-face tessellation on the disc fragment. That blocker was then established by
subtraction too, and it is not in the recognizer: feeding the SAME exact chord as a 25-point
polyline — identical geometry, identical endpoints, different density — passes all three. So
a straight `Line3d` boundary gets 2 samples from `SampleEdge` while the disc's
parameterization is ANGULAR and the chord crosses many u columns: a density rule in the
tessellator, not a gap in the intersector.

#### The two halves landed TOGETHER, and each is a no-op without the other

The density rule is the next section's subject; what belongs here is why the pair had to be
one change. `BRepTessellator.SampleEdge` now gives a straight edge the **angular density of
any face whose azimuth it crosses**, and the shape of the defect is that *a straight curve is
described exactly by its endpoints while the face it bounds may not be*: a chord's two ends
both sit on the disc's RIM, at the same v the arc completing the loop already occupies, so
the pulled-back loop is a zero-area sliver out along v = 1 and back — a winding structure the
trimmed tessellator refuses *however fine the grid around it becomes*.

Measured across an eleven-row sweep of breakout depths (Ø6 blind bore, 40×30×10 plate,
axis height z0, top face at 10):

- **Baseline**: z0 = 10 refuses at every density with *"Open splitting curves must start and
  end outside the face"*; the other ten rows pass.
- **Recognizer alone**: z0 = 10 passes; 11.5 and 10.5 now refuse with *"the loops' winding
  structure is unsupported"* on a 2-coedge / 65-sample disc loop — the sliver above.
- **Density rule alone**: bit-identical to baseline on all eleven rows, z0 = 10 included.
  With the tracer route the chord is polyline-backed, so the straight-edge branch is never
  reached for it at all.
- **Both**: all eleven pass, and the volume error stays one-signed and converging.

The density-alone row is the interesting one, because it is what makes "land them together"
a measurement rather than a preference: a rule that changes nothing on its own is not worth
landing on its own, and an intersector arm that trades refusals is not worth landing at all.

**One row's tessellation legitimately changed, and it is worth stating which way.** At
z0 = 11.5 the disc's chord had been reaching the boolean as a traced polyline and the bore
WALL was taking a coarse route with it — 222 facets at 64 segments where its neighbours at
10.5 and 9 take 2 094 and 2 046 — so the exact chord puts that wall on the same route the
rest of the family already uses. The cost is stated rather than hidden: the volume error goes
2.5e-2 → 8.0e-2 at 64 segments (still one-signed, ratios 6.91 / 4.08 / 2.96), the worst
facet-vs-surface agreement 0.99997 → 0.98054 at 96/48, and the degenerate slivers at 16/8
go 2 → 0. Both readings now sit beside z0 = 10.5's own (0.97901) rather than apart from
them, which is the point: **the change moves one anomalously-lucky row onto the family's
known residual** — the drill's `RevolvedSurface` bore wall — whose own numbers (0.107 /
0.694 / 0.840 at z0 = 9) were untouched by it. The disc fragment itself got strictly better
on both counts: 141 → 102 facets at 64 segments and 3 305 → 426 at 256, at exactly the same
area.

**That residual is now closed, and it was the WALL's carrier rather than anything about the
breakout.** A bore wall is a full-turn revolve of an axis-parallel straight line, i.e. a
cylindrical band, and until the analytic tier recognized it (see `TryCylinderCarrier` in the
intersection section above) a plane *parallel* to the axis — which is what a face a bore
breaks out through is — reached no analytic arm at all, so the two straight cuts arrived as a
49-sample tracer polyline at every density. With the carrier in place the same construction
reads 0.994 / 0.957 / 0.989 on 92 / 220 / 424 facets, `drilled breakout` is an ordinary
Corpus member, and z0 = 11.5 reclaims its old numbers exactly (2.5e-2 at 64 segments,
0.99997 at 96/48) with its wall measuring 94.2403 against an exact 94.2478, i.e. inscribed,
where the family's walls used to bulge past their analytic area.

#### An OPEN angular edge: why the count is the MAXIMUM and not the tidier replacement

The straight-edge rule above has an exact twin one case over, and the twin is the more
instructive because the obvious form of it is wrong. `SampleEdge` asked
`IsAngularlyParameterized` only on the CLOSED path, so a circle or an ellipse cut into arcs
by a boolean — every split rim — fell to `curveSamples` and carried the same count at every
density. That is a FLOOR rather than a coarseness: raising `segmentsPerCircle` refined the
grid around such a rim and never the rim itself (measured on a threaded rod's end-chamfer
cone, whose three spiral cuts scaled 5/9/17/33 with the density while the cap circle's arc
sat at 25 at 32, 64, 128 **and** 256).

The tidy fix is to give the open case the rule the closed one already has, replacing
`curveSamples` outright — a fixed count on an angular curve being the wrong knob in both
directions, since it gives a 10° arc the same 24 segments as a 350° one. **Measured, that
makes the default density worse**, because at the default 32/24 a sub-half-turn arc is finer
under `curveSamples` than under the angular count, so the replacement COARSENS every split
rim in the repository: a partial revolve's tessellated volume stopped matching its exact
closed form (2.35451265 against 2.35146969 — a discrete identity turned into an
approximation), a slot pocket left its stated chordal-error band, and 19 of 632 Interop
tests moved.

So the count is the maximum of the two. That is not a compromise between two rules but the
only form in which a change to a rule TWO FACES SHARE can be argued at all: the maximum is
monotone, so no edge anywhere gets coarser and the change can only add fidelity. With it,
one test moves and it is the one that documented the floor.

**The residual is a boundary rather than an omission, and it is worth stating because the
filed fix for it is not expressible.** The same chamfer strip measures 0.1301 against a
floor of 0.8315 at 32/24, because its four boundary edges are sampled by two DIFFERENT
rules — the helical family's pure angular count against `curveSamples` — so the strip zips a
25-point chain against a 5-point one and fans, which is the tier-order lesson (the sliver's
normal is the boundary's binormal rather than the surface's). "A density rule that measures
a trimmed face against its own uv extent" cannot be built: an edge polyline is sampled ONCE
and shared by both its faces, and that sharing IS the welding invariant, so a density can be
a property of an EDGE and never of a face. Equalizing the two rules upward was built and
measured and is a trade rather than a fix — it moves the 0.1301 onto the thread BAND as
0.5204 and triples the mesh (4450 → 15772 facets at 32/24).

#### A thin fragment is probed by stepping off its OWN boundary

`ProbePoint`'s last resort — when neither the loop centroid nor the band path lands inside —
used to be a uniform 12×12 grid over the pulled loops' uv bounding box. That is a statement
about the BOX, and a fragment thin anywhere slips between its samples however isotropic the
box happens to be: the recorded *"a sampling grid in parameter space says nothing about
coverage in model space"* lesson, here about a region's SHAPE rather than a band's aspect
ratio. Measured on a bore grazing a plate's top face at a half-chord of 0.35: the fragment
being classified away is an L — a 0.23 rad wedge joined to a 0.048-tall ring — and the grid's
0.63 × 0.083 step lands in neither arm, so the whole boolean refused for want of one point on
a face it was about to discard.

The loops ARE the region's own resolution, so the fallback now steps off the fragment's own
boundary: perpendicular in uv from each edge midpoint, **both signs** (which removes any
orientation convention to get wrong — a reversed face's loops wind the other way), on a
geometric ladder of step fractions so an arbitrarily thin fragment is still reached, with the
widest clearance winning so it only ever stands close to a boundary when there is nowhere
else to stand. It runs strictly after the grid and therefore only where the code previously
threw, which is the whole safety argument.

Together with the cylindrical-band carrier this takes the grazing breakout family from
refusing at a half-chord of 0.35 to closing at 0.24 — **exactly where the `Shape.Cylinder`
route stops too**. What remains is one near-tangency (the bore's entry rim touching the
plate's top EDGE, a circle against a line) rather than two limits for two reasons, which is
the useful form for a limit to be in.

#### An exact ray-parity classifier is rigor without a customer, and the reason is a measurement

The obvious way to remove the `MeshSdf` bridge from the boolean is an EXACT B-Rep point
classifier — cast a ray and count parity against every trimmed face — so that a fragment's
inside/outside is decided against the exact surfaces rather than against a tessellated field.
It is not built, and the decision is a scoping one rather than a gap: exact ray∩surface exists
for planes and quadrics but not for trimmed NURBS or swept faces (that would want a surface-ray
march with `SurfaceIntersection`'s own rigor), and parity THROUGH a trimmed face still needs the
crossing classified against the trim, so every pole/parity lesson `FaceGeometry.Contains` carries
(the both-directions rule, `ParityRayPointsDown`) reappears per ray. Against that cost, the
`MeshSdf` probe's one known weakness is sliver fragments near the surface, and the
largest-triangle-centroid rule already mitigates it — so the classifier buys robustness the
kernel does not currently lack. **The verdict is checked rather than assumed**: every recent
`ProbePoint` fix (the pole-cap loop measured by its closest approach, the thin-fragment
boundary step-off above) was traced to a parity or coverage rule INSIDE the mesh-probe
framework and fixed there, not to a case the mesh cannot decide. So the exact classifier is
worth building only when a boolean failure is traced to a probe misclassification the mesh
CANNOT fix; until then it is rigor without a customer.

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

### Direct editing (`DirectEdit`) — the operation an imported body needs

Every other modelling operation here edits a RECIPE. An imported STEP or IGES body has
none, so the only handle on it is its faces: push one, translate one, delete a feature.
Three decisions carry it, and two of them are reductions rather than constructions.

**(a) Face offsetting is `Shelling` under a selective law, and the missing piece was one
overload.** The backlog predicted "the machinery already exists"; it was right, and the
gap was smaller than it supposed. `Shelling.Shell` already took a per-face wall thickness
and already held its openings still at ZERO — a non-uniform offset array, in production,
with exact corners — while `Shelling.Offset` took only a scalar. So the whole of face
offsetting is `Shelling.Offset(solid, Func<BrepFace, double>)`, the twin the file did not
have, with `DirectEdit.OffsetFaces` supplying a law that returns zero off the selection.
Both tiers inherit unchanged: all-planar solids keep the three-plane Cramer path (the
uniform overload delegates with a constant law, producing the identical array, so its
output is untouched) and anything curved takes `CarrierBody`, where a face whose law
returns zero keeps its carrier object VERBATIM. No refusal was restated either — carriers
with no same-family offset, non-circular curved edges and non-concurrent higher-valence
vertices all still refuse where they always did.

**Offsetting a CURVED face of BOOLEAN output is Native now, and the fix is one sign in one
place.** `CarrierBody` used to refuse a reversed face outright, so a curved offset reached a
primitive and an imported body (faces forward-oriented from the file, the case the feature
exists for) but not a bore this kernel cut — a difference marks the subtracted tool's walls
`IsReversed`. The refusal read as though the offset DIRECTION were ambiguous, and it is not:
an offset moves a face along its OUTWARD normal, and `SurfaceOffset.TryOffset` moves a
surface along its own normal (∂u × ∂v), which is the outward normal only for a forward face
— so a reversed face's surface is offset by `−distance`. `CarrierBody.Lift` spells exactly
that, and the sign lives THERE once because every consumer already states the offset in
outward terms (a positive `OffsetFaces` grows the solid; a shell's inner layer is a negative
outward offset). The refusal is gated behind `CarrierBody.Recognize(solid,
allowReversedFaces)` — the offset path passes `true` while SHELL and DRAFT keep it, because
their carrier construction and cavity rules (the `Flipped` twin, `Draft.Taper`'s lean read
off the surface normal) are not yet sense-aware. So forward-face offsets, every shell and
every draft are bit-identical, and only a reversed face on the offset path takes the new
branch. Verified in `DirectEditVolumeTests`: a `Cylinder(20,30) − Cylinder(9,40)` housing's
bore wall pushed +2 shrinks the bore to r7 and −2 grows it to r11, each changing the volume
by the exact annulus `π(9² − r'²)·30`, Validate-clean at genus 1 and re-tessellating closed.
The MOVE of a reversed curved face and the SHELL of one stay filed (both want the same
sense-aware carrier/cavity pass).

**(b) A MOVE of a planar face is an offset, by derivation, so it is implemented as one.**
A plane is invariant under translation within itself, so the plane reached by displacing a
face by `v` is exactly the plane an offset of `v·n̂` reaches. Writing it as that reduction
rather than as a second algorithm makes two behaviours facts instead of arrangements: a
face moved parallel to itself does not move at all, and several faces moved by one vector
each take their own projection. It is also what makes the Native-under-mirror
classification a theorem — the operation is a dot product, and an orthogonal map preserves
dot products, so a reflected move pushes by the same amount. **A curved face is refused**,
and the reason is specific rather than caution: `CarrierBody.ConcentricRim` rebuilds each
rim as a circle concentric with the ORIGINAL, which is exactly right for an offset (which
leaves the axis where it was) and false for a translation (which moves it). Left
unchecked, the hypothesis fails three stages downstream in `OnBothCarriers`; declining it
at the call names the real cause.

**(c) Delete-and-heal is a CONDITION, and the entry's own verification clause turned out
to be the gate.** Call an edge *wound* when one of its two faces is deleted and the other
kept. The deletion heals by DROPPING loops exactly when every wound edge lies on a
complete interior loop of a kept **planar** face — a boss, a pad, a pocket liner, a
counterbore's step. The planar clause is not a convenience: a plane is bounded by its
outer loop alone, so an interior loop really is a hole and dropping it leaves the face
covering exactly the right region. On a cylinder or an extruded band a second loop is
routinely the far END of the band, and dropping it opens the solid into an infinite tube
which satisfies **both** `Validate()` and Euler–Poincaré (measured: a cylinder minus its
top cap comes back V=1, E=1, F=2, L=2, and `V − E + 2F − L = 2`). So no downstream check
could catch it, and the first version of the gate — "any loop that is not `Loops[0]`" —
would have shipped that silently. The entry's verification sentence said *"restores the
base solid bit-for-bit **when the neighbours are planar**"*, which is the correctness
condition written as a test case.

What that buys is the strongest available oracle: because the operation shares geometry
and rebuilds only topology, a deletion does not merely *resemble* the body a feature was
added to — it reproduces it, asserted bit for bit against a plate that never had the hole,
and against the closed-form volume on a plate whose boss came from a real boolean.

**What is refused by name** rather than attempted: a wound that only PARTLY bounds a
neighbouring loop. Healing that means EXTENDING the two neighbours until they meet in a
new edge, which is a different operation and can have no answer at all — a box's four
sides extended past its deleted top never meet. `SurfaceCorner.TrySolveCurve` could supply
the curve; what is missing is the topology rewiring and a soundness gate, so it is filed
rather than guessed at.

**One measured property worth keeping**, of the signed-zero family: a re-solved corner
reproduces every nonzero coordinate BIT FOR BIT and can return −0.0 where the original
held +0.0, because the three-plane Cramer solve divides by a determinant of −1. It
compares equal to 0.0 under every value test and differs only in the sign bit, so it is
invisible to everything except a bit-level fixture placed at the origin — which is exactly
the assertion the "did this face really not move?" check reaches for. The fixture moves
off the origin and the property is pinned by its own test, so it cannot rot into a
mystery. Same shape as `PolygonFan`'s tie guard: a comparison is only as meaningful as the
scale (here, the representation) it is made against.

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

- **Implicit → Mesh: manifold dual contouring with Hermite data.** Chosen over marching
  cubes because the 256-entry MC tables are error-prone to reproduce and the dual scheme
  pairs naturally with the half-edge's n-gon support (quad output). The *manifold* variant
  — one vertex per SHEET a cell's inside corners bound — exists because the naive version
  provably emits non-manifold edges on diagonal sign patterns (thin sheets, gyroids),
  which the strict `HalfEdgeMesh.Build` rejects.
  - **Vertex placement is a quadric, and the reason is that averaging is wrong rather than
    imprecise.** Every crossing lies ON the surface, so their mean lies strictly INSIDE any
    convex corner: a polygonized box is chamfered by construction and no resolution removes
    it (measured, the nearest vertex to a box corner sat half a cell away at every
    resolution and did not converge). The field's own tangent planes carry the feature, and
    the minimiser of their summed squared distance IS the corner. So the vertex goes at
    `x = m + A⁺(b − A·m)` over `A = Σ n nᵀ`, with the mass point `m` — the incumbent
    averaged vertex — supplying every direction the samples do not constrain. The solve is
    a REGULARISED NORMAL-EQUATIONS solve rather than an SVD and that is not a compromise:
    A is symmetric PSD by construction so its SVD IS its eigendecomposition, which
    `SymmetricEigen3` already computes, and the usual objection (normal equations square
    the condition number) is bounded here because the rows are unit normals — κ(A) is a
    function of the ANGLES alone, and the truncation is what answers an ill-conditioned
    angle by declining to resolve it.
  - **The threshold is an ANGLE, derived rather than chosen.** Two unit normals separated by
    α give A the eigenvalues 1 ± |cos α|, so the singular-value ratio is exactly tan(α/2)
    and a stated feature angle converts. The direction of the risk decides the default:
    raising it returns the incumbent chamfer, lowering it inverts a direction the samples
    barely constrain and sends the vertex a long way out — a worse mesh against a broken
    one.
  - **The Hermite POINT must be projected onto the surface, and a symmetric fixture cannot
    see that it was not.** A grid crossing is where the LINEAR INTERPOLANT along a cube edge
    vanishes, which is on the surface only where the field is linear along it — and at a
    feature it is not, a box's field near a corner being a max of three linear pieces. One
    Newton step along the gradient fixes it exactly wherever the field is locally linear
    (every planar face, every hard-CSG corner). What makes this institutional memory rather
    than an implementation note is HOW it hid: a box sharing its centre with the sampling
    region puts its corner at the same fractional position on all three axes, and only then
    does the linear crossing land on the surface by itself — so the symmetric fixture read
    EXACTLY zero while an offset one read 3.5e-2. The recorded "a mirror-symmetric hostile
    fixture can be secretly benign" lesson, in a third place.
  - **Where a vertex may go is `ClampCells`, and both textbook answers are wrong.** A strict
    cell clamp chamfers a ROTATED box's edges by a quarter of a cell — a cell that sees both
    faces of an edge need not contain the edge, so the minimiser on the edge LINE is
    legitimately just outside and refusing it there chamfers exactly the feature the quadric
    found. No clamp at all is unbounded: an under-resolved gyroid throws a vertex 4.3 cells
    out. The default of one cell is the neighbourhood a cell's own crossings can speak
    about — a fit's own data rather than an extrapolation past it — and measures exact on
    every box placement while bounding the gyroid. Half a cell is measurably not enough,
    which is why the bound is a measurement rather than a comfortable number.
  - **Placement never changes TOPOLOGY, and that is the manifoldness argument.** Which
    crossings belong to which vertex is decided before any position is computed, so the
    index buffer is bit-for-bit unchanged and every combinatorial property with it —
    including the recorded pinch-vertex residual, which this neither creates nor repairs.
    The golden fingerprints are split to say so: a TOPOLOGY hash asserted for BOTH placement
    rules from one row, and POSITION hashes per rule.
  - **Adaptive output is BOTTOM-UP, and that is a completeness decision.** The textbook
    adaptive contouring is a top-down octree that stops subdividing where the field looks
    flat, which would save the visit as well as the faces — and it cannot certify that no
    feature hides between the samples it took, which is verbatim the argument `SurfaceCull`'s
    own remarks make against seed-and-flood. Collapsing cells the uniform walk HAS visited
    inherits that argument unchanged and adds no new one; the cost is stated rather than
    implied, namely that it saves faces and nothing in evaluation. Cracks are then
    structurally impossible because the connectivity is the uniform walk's face buffer
    RE-INDEXED and never re-derived — there is no T-junction to make. Manifoldness is the
    one thing that is checked rather than argued (contracting a connected vertex set is a
    manifold quotient only when its induced subcomplex is a disk), and the check terminates
    for a stated reason: reverting a cluster only ever splits vertices apart, and splitting
    cannot create a violation, so every round strictly removes merges and the empty
    clustering is the original mesh.
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
- **Mesh → B-Rep: `MeshToBrep` reconstruction — the fourth edge, and the only one that puts
  information BACK.** Implicit→mesh, B-Rep→mesh and mesh→implicit are all controlled
  discretisations; this direction reconstructs the surfaces a tessellation came from. **The
  headline metric is the FACE COUNT**, because the fake version of "STL to STEP" — one planar
  STEP face per triangle — is nearly free and worthless (a 100k-face solid no CAD system can
  edit), so a drilled plate coming back as SEVEN faces rather than five thousand is what
  separates the feature from its impostor. Two phases. **(1) Segment + fit** (`MeshToBrep`):
  region-grow triangles across every edge that is not a sharp crease (feature angle reads the
  MESH, so a coarse tessellation over-segments and the face count is the honesty check), then
  plane/cylinder/sphere per region with the worst residual REPORTED. The cylinder axis is the
  SMALLEST eigenvector of the area-weighted facet-normal covariance (a cylinder's normals span
  a great circle ⊥ axis), and the radius is an algebraic circle fit in the plane ⊥ axis —
  **exact for points ON a circle, which is the whole point**: an inscribed n-gon's vertices
  lie on the cylinder, so the fit recovers the TRUE radius at every density where an
  inscribed-radius fit `r·cos(π/n)` would be measurably wrong (0.024 low at 32 segments), and
  that distinction is the first test written. **(2) Assemble** (`SolidAssembler`): a region
  boundary becomes the EXACT surface∩surface intersection (a `Line3d` through the snapped
  corners for plane∩plane, a `Circle3d` for a plane∩cylinder rim) rather than the chordal
  polyline the mesh carried, and a triple point is snapped to the exact meeting of three
  surfaces (`SurfaceCorner`) — the stage that decides whether the solid CLOSES. Shared edges
  are built once and referenced by both faces so the result is a manifold directly;
  `ShapeHealing.Heal` repairs shell orientation and `BrepSolid.Validate()` is the oracle. The
  verification bar needs no external data (box/cylinder/drilled-plate reconstruct to valid
  closed solids with matching volumes and 6/3/7 faces). **v1 is the tessellated-CAD case, said
  out loud** — vertices ON the surface, so a fit's residual is the chord error — and cone,
  torus, freeform (a NURBS surface fitter is the genuinely new numerical work), noisy scan
  data, and a seamless closed surface with no boundary edge (a whole sphere is one face with no
  edge) are all refused BY NAME. A spherical face WITH a boundary (a dome cap, whose rim is a
  plane∩sphere circle) is reachable through `SurfaceIntersection`'s analytic branch but is not
  yet tested; that plus cone/torus/NURBS and the seamed single-face solid are the honest
  remaining work.

### Dual-contouring and tessellation residuals — dispositions

The dual-contouring/sharp-feature and trimmed-face work left a cluster of residuals. Their
findings live here so the todo entries could be retired without losing them; each is a
measured decline, a cost note, a pinned residual, or a cross-layer future feature rather than
a live defect.

- **The adaptive simplifier's tolerance is loose on CURVATURE.** `SurfaceNetsSimplify.Collapse`
  merges cells whose combined `SurfaceQef` residual stays under the tolerance and CLAMPS the
  merged point to the cluster's bounding box — so on a nearly-tangent smooth surface (a large
  sphere) two adjacent planes have a tiny quadric residual whatever the point does along their
  near-intersection, and what actually bounds the damage is the bounding box rather than the
  stated length (measured 0.018% of volume at tolerance 0.001 cells). **The cheap true-bound
  fix is one field evaluation per accepted cluster** — reject the merge when `|d(x)| > tolerance`
  at the merged point `x`, which makes the tolerance a real error bound (conservative for the
  lower-bound fields, exact for hard CSG). Filed rather than built: the adaptive mode is opt-in,
  the bounding-box clamp already bounds the damage, and it would move `polygonize-adaptive.png`.
- **The adaptive path gives up the uniform walk's O(cross-section) streaming memory bound**
  (it retains a `SurfaceQef` and cell index per vertex = O(surface)), so it cannot reach the
  1024³ grids the uniform walk can. A stated cost of the opt-in path.
- **The repair loop reverts GLOBALLY per level** (a cluster in a manifoldness violation is
  reverted and the whole face buffer rebuilt, up to eight rounds). It has never needed more
  than one round on any fixture, so an incremental check (only the faces touching the reverted
  cluster) is a cost note, not a correctness one.
- **The Hermite-point projection assumes |grad| = 1 where the field is a lower bound.**
  `Sdf.Normals` reports the true |grad| and the polygonizer divides by it, so the Newton step
  is already exact for hard CSG; inside a smooth blend it lands NEAR rather than ON, but there
  is no sharp feature to resolve there and the bound is one cell either way — a note, not a
  defect.
- **The ambiguous-face split is ONE-SIDED and PINCHES the minus-side cell.** The + side owns
  the test (the sliding window cannot promise a cell the slab beyond its + neighbour), so the
  minus cell keeps one vertex against its neighbour's two and its link falls into fans — the
  only source of non-manifold pinch vertices measured after the sheet fix (240 on
  `Sphere(10).Shell(0.6)` at res 44, 642 on `Box(10) & Gyroid(8,0.2)` at 56, 1686 on a lattice
  sphere; pinned by `SurfaceNetsPinchTests`). **Two closures were built and both measured WORSE**
  (the asymptotic-decider on the bilinear saddle, and cutting the ambiguous face's pairing where
  the cube already links its two arcs): both drove the pinch count to zero AND produced open
  meshes with bow-tie vertices, because a cell's grouping must match what its NEIGHBOURS do and
  a face-local pairing does not make two cube interiors agree. The unverified third approach is
  to make the resolution a function of the MINUS-side cell alone (which the + cell can already
  read — `previousFarJoins` for x, a neighbour flood for y/z) so both cells decide the same way,
  with the + cell's cycle also cut at that face. Filed.
- **The grid WALK is now the polygonizer's dominant cost** (~175 ms of a 213 ms res-384
  polygonize; assembly is 15–18%). The candidates are the per-cell `int[12]` crossing map (one
  heap allocation per mixed cell), the crossing interpolation, and the three quad passes
  re-reading `values` through `Corner()`. A perf lever, re-measure before choosing.
- **`MeshSdf` batch queries: two levers measured, both declined** (seeding the branch-and-bound
  1.12–1.20×; a packet query 0.30–0.86× on the collinear rows the batch seam delivers). The
  full findings are in CLAUDE.md's performance mandates; a third attempt needs a lever that is
  neither the initial bound nor the traversal amortization.
- **`SdfProjectionTarget` stalls on a CSG difference's fictitious faces** (documented in the
  class: two branches trade a probe back and forth, |d| not decreasing). Harmless for remeshing
  (near-surface points only). The scoped future fix is an Implicit-layer `Sdf.TryClosestPoint(p,
  out c)` virtual — primitives answer in closed form, operators UNION their children's
  candidates rather than combining distances, and only candidates where the whole field reads
  `|d(c)| ≈ 0` survive (a fictitious face sits strictly inside kept/removed material) — which is
  exact for hard CSG over closed-form primitives and does NOT cover smooth blends/offsets, so
  the API would have to report which answer the caller got. Not offered as a general
  closest-point query today; the fix belongs to `EngrCAD.Implicit`.
- **Trimmed-face residuals are bounded and pinned by tests** (`Box(20,20,20) − Sphere(12)` below
  the corpus floor at a rim's vertical-tangent column; a sub-depth chamfer cone whose 740-aspect
  band the sweep cannot improve; a coarse marching-tracer rim where refinement makes the face
  worse). The two open levers are a ROW PATH reaching the rim's turning vertex (so the base is
  right there and refinement stays idle) and making the tracer's sample count follow the
  tessellation density rather than its own arc-length step. `TriangulateRegion` (tier 4) still
  ear-clips, so a non-wrapping region with an exactly-uv-collinear boundary run would hit the
  forced fan — nothing exercises it, so the slab sweep was not widened to it. Recorded by
  `SpherePiercingEverySide_HasNoFoldsAndABoundedResidual`, `SubDepthChamfersCarryNoFoldsAtAnyFraction`,
  `TrimmedBandGapTests` and `TrimmedFaceRefusalTests`.
- **The adaptive octree simplifier and `MeshDecimator` are NOT interchangeable in kind**, so
  the unmeasured "uniform polygonize + decimate reaches the same face count more cheaply than
  octree clustering" comparison would be informational rather than decision-driving: the
  octree collapse is EXACT for planar regions (provably lossless there) and keeps quads, where
  QEM decimation is a triangle approximator. Seeding `MeshDecimator` with the field's quadrics
  (rather than its own face planes) would need its `Quadric` made public and its `TryOptimize`
  taught the rank-deficient regularisation dual contouring lives on; filed, not built.
- **A quad n-gon (n > 4) is still corner-0-fanned, not optimally triangulated**, which is wrong
  geometry on a NON-convex n-gon — but nothing in the kernel produces one today (planar faces
  earcut before they reach here, and the `PolygonFan` shorter-diagonal rule handles quads), so
  it is where the next defect of that family would live rather than a live one. The soup/import
  fans (`MeshRepair`, `MeshSoupOps`, `StlReader`) are deliberately untouched for the same
  reason — they decompose soup that is not a mesh yet, where the fan is the documented fallback
  for input earcut declined — worth revisiting only if a dirty-import case is traced to a fan
  diagonal.

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
- **A writer whose default arm THROWS needs a coverage test, not a convention — and the
  round-trip test it already had could not see the gap.** `InputJson`'s sketch writer maps
  the `Curve2d` vocabulary case by case and throws on anything else. Elliptical arcs became
  first-class after the envelope landed; `Sketch.FromCurves` learned the `Ellipse2d` case
  and the writer did not, and because `Document.Save` has no catch around `SaveHistory`,
  **any document holding an elliptical sketch feature could not be saved at all** — not one
  feature degraded to a snapshot, the whole file. The existing round-trip test passed
  happily throughout, because it was a *fixture* (a line, an arc, a Bézier, a hole) and a
  fixture only ever tests what someone thought to put in it. The replacement is a COVERAGE
  claim: enumerate the concrete segment types from the assembly, assert one fixture sketch
  uses every one of them, then round-trip it. A new segment kind now fails a test in
  Modeling instead of failing `Document.Save` in a user's session. The same shape of check
  guards the catalogue-component writer, whose default arm returns null rather than
  throwing — the two arms differ because a component outside the catalogue is a legitimate
  thing to hold (a user's own `HardwareComponent`) while a curve kind the sketch builder
  can produce is not.
- **A catalogue item is keyed by its ARGUMENTS, never by its designation.** `ComponentFeature`
  serializes its `HardwareComponent` as a kind plus the factory arguments, the way
  `HoleSpec` already serializes: the geometry is derived from a standards table, so the
  arguments *are* the component and storing anything else stores a copy of the table. The
  designation is the tempting key and is precisely wrong — "ISO 4762 M6×20" says nothing
  about the clearance fit, the seating or whether the hex socket is modelled, so a
  designation-keyed reload comes back as a plausible *different* screw wearing the right
  name, which is the silent-misresolve failure the topological-naming work exists to
  prevent, in another guise.
- **A feature whose input has no data form is opaque BY NAME, not blocked.** `TextFeature`
  carries a `TrueTypeFont` — a binary blob, not a value — so it cannot round-trip through
  `SaveHistory`; the choice is the `ComponentFeature`-over-a-non-catalogue-component rule,
  `SaveInputs` returns null so the type/name/`[Param]` values are still written honestly and
  a load skips it with a warning unless a resolve hook rebuilds it. And the regeneration
  CACHE is a separate question the existing convention already answers: the font is a
  constructor input on a fixed instance, and "a fresh instance always re-runs" covers it, so
  the parameter snapshot never has to name a value it could not serialize anyway — the item's
  stated worry dissolves rather than needing a font-hashing cache key.
- **An optional parameter's SPELLING is decided by its editor, not by the serializer.** A
  nullable `[Param]` (`double?`, `int?`, `bool?`, `enum?`) round-trips: null is a value JSON
  has, the cache key renders it `"null"`, and a range does not fire on a value that is not
  there. That closes the gap todo.md named — `FeatureHistory.Convert` threw on
  `Nullable`1` and `ApplyParameters` swallowed it into a warning, so an optional value was
  silently dropped on load. But the backlog's conclusion, that the sentinel `0` in
  `EdgeFlangeFeature` was therefore a serializer workaround, is only half true. The
  properties panel offers a SLIDER exactly when `[Param(Min=, Max=)]` is finite at both
  ends, and a slider is a total function onto its range with no way to say "unset" — so an
  optional parameter behind one can be moved off "inherit" and never back, while its text
  box shows an unset value as empty and takes `null` back. The rule: **a parameter whose
  editor can express absence takes the nullable type; one whose editor cannot keeps a
  sentinel outside its own legal range.** `Width` and `BendRadius` are unbounded above and
  became `double?`; `KFactor` keeps its 0, which `SheetMetalSpec` refuses as a K-factor
  anyway (so it costs no legal value) and which sits at the slider's own minimum (so
  "inherit" stays one drag away). The general form is worth keeping: an API shape that
  looks like a workaround may be carrying a constraint from a layer that does not appear in
  its signature.
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
- **A re-placement is a POSE only while the map is an ISOMETRY — and the binding constraint
  is bookkeeping, not geometry.** `BrepSolid.Transformed` moves a solid by mapping each
  surface and curve in its own family and carrying the topology over verbatim. The scope is
  proper rigid motions, and that is the exact condition under which nothing downstream has to
  be re-derived: every parameterization in this kernel is built out of lengths and angles, so
  an isometry leaves edge trim domains, seam phases, revolve angles and grid samples alone,
  and they are copied rather than recomputed (asserted BITWISE, since "close enough" is what
  the design exists to avoid). The interesting refusal is **uniform scale**, which the
  backlog had filed as admissible "where the type allows". It is not, and the type is not the
  party that decides: `PolylineCurve3d` is parameterized by CUMULATIVE CHORD LENGTH, so
  scaling its points scales its DOMAIN — while a `BrepEdge` stores its trim domain separately
  from its curve and a `CurveSegment` stores base parameters in the base's units. Scaling the
  curve alone desynchronizes them with no symptom at the point of failure, and every
  tracer-produced edge is polyline-backed, so this is the common case rather than an exotic
  one. Shear and non-uniform scale are refused for the ordinary reason (they change the
  surface FAMILY — a sheared cylinder is an elliptic cylinder), and reflection because it
  reverses orientation, so loops would need re-winding and the handedness-carrying types
  their own rules; `Shape.Mirror` already does that correctly one layer up by baking the
  reflection into construction inputs, and a second route would be a riskier way to reach an
  answer the kernel already has. Two smaller rules came with it. **Curves never refuse and
  surfaces must**: `TransformedCurve` is exact in position and parameterization for any
  affine map and keeps its `Underlying` type, so it is a sound fallback, whereas there is no
  surface wrapper — so an unrecognized surface type is named rather than approximated.
  And **curve OBJECTS are mapped once and reused**, keyed on reference identity: a seam curve
  backs two edges and a carrier backs many, so mapping per edge would split one carrier into
  several numerically-equal copies — which is how a solid stops welding without any count
  changing, and is the same reason `BrepArchive` keys on reference identity rather than on
  structural equality.
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
  the sites that derive a face from a parent; a site that builds a face from scratch simply
  does not tag — so a query returns FEWER faces than the author expected, never a face from
  somewhere else. Landed: the boolean pipeline (untouched faces pass through by reference;
  every `FaceSplitter` fragment and every re-wound tool face inherits), `BrepSolid.Clone`,
  and therefore `Drill`, patterns, transforms and `Shape.From(solid)` — **and every
  wholesale-rebuild site**: `Draft`, `Shelling`, `Filleting`'s `FilletAllEdges` and rim
  surgery, and `ShapeHealing`. Each of those discards its input's face objects and
  constructs replacements, and each already walks its parents positionally, so the fix was
  simply that every derive point ASK the existing `BrepFace.DescendsFrom` — no new API, and
  a second inheritance helper would have been the mistake rather than the missing piece.
  Two corrections to the shape this had been filed in are worth keeping. **It was recorded
  as four sites and is really six derive points**, because `Draft` and `Shelling` each have
  a planar path and a curved one and the two curved halves share `CarrierBody.Rebuild` /
  `CarrierBody.Shell` — so a third of the work lives in a file naming neither operation,
  which is exactly what a per-operation checklist misses. And **the reason recorded for the
  work being easy was not the reason**: `Shelling`'s `Dictionary<BrepFace,int>` is never
  consulted for this, since a positional loop counter already IS the correspondence.
  What still carries nothing is a statement about the geometry rather than a gap — a fillet
  band, a corner patch and a partial run's termination face descend from no single face, so
  attributing them to one of the two surfaces they join would be a guess. The one case that
  answers with MORE faces than the design named is shelling, where a wall and its cavity
  twin both inherit from one parent; that is honest (the cavity wall exists only because the
  outer one does) and it is *representable* precisely because (a) made a tag set-valued.
  STEP carries no provenance either, which is a format limit rather than a choice.
  **The boundary tests had to become measurements to stay worth having.** Asserting that a
  tagged solid comes back with N tagged faces passes just as happily when a parent array is
  off by one — the count is right and the meaning is wrong, which is the silent misresolve
  (a) exists to prevent — so each site is now tested by tagging exactly ONE face and
  asserting *which* output face carries it, located by `Bounds().Center` (`IsPlanar`'s origin
  is an arbitrary in-plane point and a circular loop's face-frame origin is its seam vertex;
  both read the rim, and both would make the assertion agree with a wrong answer).
  **Edges inherit face provenance as a DERIVED query, and the open decision was UNION.**
  `BrepQueries.Provenance(edge)` / `DescendsFrom(edge, tag)` / `solid.EdgesTagged(tag)` report
  the union of the (up to two) faces an edge borders — "an edge is *of* a step whenever it
  touches a face of that step." The note left "both? either?" open, and the motivating query
  settles it as EITHER: "fillet the edges of the boss" wants the boss's BASE rim, where its
  cylinder meets the plate it stands on, and that rim borders a boss face and a non-boss one —
  an INTERSECTION would drop precisely the edge a caller most wants to blend. Nothing new is
  stored (decision (a)'s set-valued tag is walked on demand from `edge.Uses` → `Loop.Face` →
  `Provenance`), so it stays correct through every rebuild with no second table, and it inherits
  the same one-sided safety — a step that tagged no face contributes no edge. `EdgeProvenanceTests`
  measures the decision by tagging two adjacent faces and asserting their shared edge reports BOTH,
  which an intersection could not.
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
- **A catalogue component's `Material` carries the SUBSTANCE, and an ISO 898-1 property
  class is not one.** A catalogue fastener knowing what it is made of is what makes a bill
  of materials of bought-in parts weigh itself, but the obvious reading of "grade" is a
  trap: 8.8, 10.9 and 12.9 name a proof and a tensile stress, and all three are steel at
  7850 kg/m³ — an M6×20 weighs the same whichever it is. So the class stays in the
  `Designation` (and in a strength calculation this kernel does not do), while
  `FastenerMaterials` distinguishes only what genuinely moves a mass: carbon steel, alloy
  steel, stainless A2 and A4 (~2% heavier), brass (~8% heavier again), bearing steel. That
  claim is asserted rather than asserted-about — `CarbonSteel.Density ==
  AlloySteel.Density` bit for bit, so the two differ in name, modulus and conductivity and
  in nothing else. Where `Materials` (Core) already states the alloy, the fastener entry
  **delegates and only renames** (`StainlessA2` *is* `StainlessSteel304`), because two
  spellings of one density is exactly the discrepancy the material consolidation removed.
  **The bearing states no material at all, and that is an answer rather than a gap**: its
  v1 body is two rings with the balls and cage missing, so density × volume is measurably
  less than the real mass, and the bill of materials' own rule — an unknown mass is an
  empty cell, never a zero a spreadsheet sums silently — makes an honest "unknown" better
  than a confidently light number. `component.ToPart().Of(...)` overrides it (and is also
  how a design states a stainless variant of any entry), which works because `ToPart`
  caches one part per component, so one assignment covers every occurrence.
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

  **A coincidence test measures to a PLANE, never along a convenient axis.** The depth
  guard asks whether the tool's flat bottom lies in a body face's plane, which is two
  conditions — the planes are parallel, and a point of one lies on the other — and the
  second must be `n̂·(origin − bottom)` with the FACE's normal. Measuring
  `axis·(origin − bottom)` instead looks equivalent, and is, exactly at zero tilt; away
  from it the answer depends on *which* in-plane point `IsPlanar` reports, and that point
  is arbitrary by contract (a box cap's is a CORNER; a boolean fragment inherits its
  parent surface's, which can sit outside its own trim). Inside the guard's own 0.081°
  parallel band an in-plane offset `L` therefore leaks `L·sin θ` into the measurement:
  on a 200×150 plate tilted 0.057° that is **0.075 model units**, three decades past the
  1e-6 threshold, so the guard silently failed to fire on a genuinely coplanar bottom
  *and* refused a blind hole with 0.075 of real floor left. Both directions are pinned by
  tests, and both fail on the old form. Two riders. The angle is deliberately not
  widened, and now for a geometric reason rather than as a deferral: past that band the
  bottom disk CROSSES the face in a chord rather than lying in it, which is ordinary
  transversal boolean input, so a wider gate would start refusing legal models. And the
  distance stays one tier looser than `CoplanarFaces.SamePlane`'s 1e-7, because this is a
  refuse-EARLY guard where a conservative refusal costs a nudge and a missed coincidence
  costs a "Directed edge appears twice" from inside the tessellator. The general lesson is
  the one this repo keeps re-learning: **the kernel already had the well-formed version of
  this test** — `CoplanarFaces.SamePlane`, same arithmetic, one layer down — and the guard
  restated it rather than asking it. Restating a shared rule is how all four recorded
  occurrences of this went wrong.

  **The coplanar-fusion tier does not retire the guard, and the reason is structural
  rather than a matter of degree.** `CoplanarFaces.For` collects only faces `IsPlanar`
  recognizes, while a drill tool is ONE axis-touching revolve — chosen that way precisely
  to keep boolean input transverse — so its flat bottom is a `RevolvedSurface` pole cap
  and never enters the tier. A flush *cylinder* tool, whose caps are `PlaneSurface`s, does
  fuse. So "coplanar booleans landed" narrows what the guard is for without removing it.
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
- **Most of the mirror work was an identity; the last five nodes needed none.** Revolves
  conjugate (`F∘Rot(d, φ)∘F = Rot(−F·d, φ)`), threads negate a rate, sweeps rely on RMF
  transport being intrinsic. `Draft`, `Shell(t, openings)`, `RoundEdges`, `Loft` and the
  pure taper have no such structure to fix, because each is defined by **lengths and
  angles alone** — an offset by a distance, a rounding by a ball, a skin whose
  parameterization and alignment are metric, a taper by an angle — and every isometry
  preserves all of those. So the operation applied to the mirrored child simply IS the
  mirrored operation, and the change is a gate: `Decompose` (proper only) becomes
  `DecomposeSimilarity`. Three points worth keeping.

  **A pull direction is transported; a revolve axis is conjugated.** `Draft` is the only
  one of the five with a direction to carry, and it takes `m.TransformVector(pull)` with
  **no negation** — the negation in the revolve case comes from conjugating a rotation,
  which a plain vector does not undergo. Proper placements keep their original spelling
  (`rotation.Rotate(pull)`) so existing geometry stays bit-identical; only the reflected
  branch is new, exactly the asymmetry the reflected revolve branch already had.

  **What makes the draft claim true rather than merely plausible is that `Draft.Apply`
  chooses its rotation SENSE by measurement** (build both candidates, keep the one leaning
  further toward the pull direction) instead of from a cross-product convention. A
  handedness convention anywhere in there would have flipped under the reflection.

  **The oracle has to be an analytic volume, not a mirrored twin.** A reflection is an
  isometry, so a wrongly-signed pull still yields a closed, valid solid of a plausible
  size — and comparing mirrored against unmirrored would pass it, since both would be
  wrong the same way. Drafting a 20×12×6 block 5° about its BASE separates the cases by
  construction: narrowing gives `abh − (a+b)t h² + (4/3)t²h³` = 1341.42 and widening
  1542.99. (The taper is folded in for a different reason: it *lowers as* a two-section
  loft, so leaving it refused while `Loft` was Native would have been one operation
  disagreeing with itself.) `SheetMetalBody` stays refused and is not an oversight: a
  flange tree is ORDERED and quoted on named edges, so a reflection reverses the sense of
  every bend and the body would have to be rebuilt the other way round rather than
  re-placed — which is what its refusal message has always said.
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
  *Roller and offset followers needed no offset curve and no root find, because the
  signed distance already IS the offset* (`CamFollower`,
  `CamLaw.FromSketch(profile, follower)`, `CamLaw.PressureAngle`). The filed shape of
  the problem was parametric: build the profile's planar offset q(θ) at the roller
  radius, then solve arg(q(θ)) = ψ per query with implicit-function derivatives of the
  root. All of that dissolves against the machinery `FromSketch` already stands on —
  the sketch's signed distance is a TRUE planar distance outside the profile, so the
  roller-centre curve is exactly the isolevel sd = R, and the follower's position is
  the outermost crossing of that isolevel along its travel line: the same outside-in
  march and bisection as the point follower (which is literally the isolevel-0 member,
  bit-identical since reach + 0.0 and an isolevel of 0.0 change no bits), with slope
  and curvature still the C² spline's own calculus. An offset follower is the same
  march along a line that misses the pivot; what it buys is the PRESSURE ANGLE, so
  that is reported too — tan φ = (slope − offset)/(centre distance), from the
  instant-centre construction, with ONE sign convention stated on `CamFollower`
  (offset positive to the RIGHT of travel, cam angle counterclockwise — the choice
  that makes the textbook "positive offset reduces the rise-side pressure angle"
  hold) and shared by placement and analysis. The verification is the eccentric
  circle, where everything is closed-form because the offset of a circle is a circle,
  and the pressure angle gets an INDEPENDENT oracle — the contact normal of a roller
  on a circle passes through both centres, so cos φ = |t̂·(p − c)|/(a + R) — sharing
  nothing with the formula but the physics, which is what pins the two conventions
  as consistent (the formula misreads by the full offset term if either sign flips).
  The measured reason the feature exists: the radial shortcut r(θ) + R misses the
  true roller law by 0.12 on that fixture at θ = π/2, three orders above the law's
  own fidelity, and the discriminating test asserts the shortcut DOES miss — without
  that half, the fixture would pass a roller law wired to the shortcut.
  *Mechanism persistence restates nothing that is derived, and saves everything that
  is history* (`MechanismPersistence.cs`). A joint's mates are a deterministic
  function of its two ends, so the file stores the ends (the mate-end vocabulary
  verbatim — path, pinned coordinates, query descriptor — through the same
  `MateSet.SaveEnd`/`LoadEnd`, so the two files cannot drift) and loading re-runs the
  constructor. Two things are NOT re-derivable and ride as data: the axis joints'
  perpendicular reference directions, whose re-derivation at load would move the
  angle coordinate's zero (the `MateRef` constructor keeps an already-unit direction
  verbatim, so they round-trip bit-for-bit), and `JointSweepState` — the unwrapped
  accumulated angle is a history of how many turns the crank has taken, which no
  pose can recover. Loading re-ADDS each joint through the ordinary `Add`, which
  re-asserts its nominal DOF against the solver's measured rank: a file valid when
  written can legitimately fail on a changed model, and that is a load WARNING
  naming the joint (the joint skipped, couplings referencing it skipped by their
  SAVED index — the index list keeps nulls so a skipped joint never shifts its
  neighbours under a coupling). Couplings save their FACTORY and arguments rather
  than their implementation — a gear as tooth counts, a rack-and-pinion as itself
  rather than the straight-law cam it is built as — and their construction zeros are
  deliberately absent: a coupling constrains the CHANGE since its construction, and
  for any pose that satisfies it (a saved converged pose does, by the solver's own
  contract) re-zeroing at load is algebraically the same constraint, so saving the
  zeros would be storing a number the arithmetic already implies. Cam laws follow
  the `Feature.SaveInputs` precedent: catalogue factories stamp their kind + args on
  the law they return (`FunctionCamLaw.Identity`), `Segments` recurses,
  `FromSketch` saves its sampled lifts — the law IS the samples, and the spline
  rebuild is deterministic so the loaded law evaluates bit-identically — and a
  `FromFunction` lambda writes an `opaque` marker that loads as a warning naming
  the coupling unless the caller's `resolveOpaqueLaw` hook supplies the instance.
  The oracle is the byte-identical save→load→save fixed point, with the
  FeatureHistory rider asserted separately: a file carrying an opaque record is
  smaller the second time by exactly the record the warning named, then a fixed
  point.
  *Involute gears draw the teeth the couplings already constrain* (`Gears.cs`), and
  four calls carry the design. **The flank is a fit, not a new curve type, because
  the measurement said so**: the todo entry left the door open for a first-class
  involute `Curve2d` if the fit tier proved inadequate, and it did not — a biarc
  chain (recursive bisection over `BiArcFit.TryFit` with EXACT endpoint tangents,
  which the involute supplies in closed form: the tangent at roll t is the base
  radial at θ₀+t) meets a module·1e-4 tolerance in ~16 arcs per flank, with the
  deviation measured at 512 samples and REPORTED (`GearProfile.MaxFitDeviation`).
  Arcs also keep the profile in the vocabulary every representation is exact for,
  where an involute segment would have joined the bézier's "General" distance tier.
  **The root fillet tangency is a closed form, found by writing |C|² out**: with
  fillet centre C = P(t) + ρ·n̂, the cross term P·n̂ is exactly r_b·t, so
  |C|² = (r_b·t + ρ)² + r_b² and |C| = r_f + ρ inverts to
  t* = (√((r_f+ρ)² − r_b²) − ρ)/r_b — no root find, and the case split falls out of
  the same expression (t* < 0 means the tangency would sit below the base circle, so
  the flank continues as a RADIAL line, which is tangent-continuous with the
  involute because the involute's cusp tangent IS radial). **Undercut is refused,
  not trochoid-trimmed**: below z_min = 2(h_a* − x)/sin²α a generating cutter eats
  into the involute, a mating tooth physically sweeps through the region this
  factory would have drawn, and an honest refusal naming z_min and the clearing
  x_min beats an unverified flank (the message teaches the way out — teeth, pressure
  angle, or shift). **Conjugate action is verified from CONTACT, and deliberately
  not from the mechanism solver**: `Coupling.Gear` ENFORCES the ratio, so a
  solver-based "test" would assert its own constraint — instead two generated gears
  sit at an EXTENDED centre distance (real backlash makes drive-flank contact a
  transversal zero of the clearance, where the zero-backlash standard mounting
  touches both flanks at once and leaves nothing to bisect; the involute's ratio is
  centre-distance-invariant, so the extension costs no correctness) and the wheel is
  rotated into contact by bisecting the minimum of the pinion sketch's exact signed
  distance over the wheel's sampled outline. Measured: 9.3e-6 rad of transmission
  variation through tooth handover against an asserted 6e-5 derived from the fit
  tolerances; and the instrument is mutation-checked — a 25° wheel against a 20°
  pinion reads 5.6e-3 rad (wrong base circle), a 5e-2-tolerance flank reads
  5.6e-4 ≈ deviation/r_b (the textbook transmission-error relation), so it can see
  a bad FLANK, not just a bad ratio.
- **The rack is not one more gear, it is the DEFINITION** (`Gears.Rack.cs`). As z→∞
  the base circle recedes and the involute flattens into a straight line at the
  pressure angle, which is why ISO 53 defines the whole tooth system by a basic
  RACK. So the rack is the one member with no fit tier at all — straight `Line2d`
  flanks, exact `Arc2d` root fillets — and `RackProfile` deliberately carries **no**
  `MaxFitDeviation` for there to be, where `GearProfile` must report what the biarc
  chain cost. That flows into the assertions rather than only the prose: the
  pitch-line thickness and the flank angle are held to 1e-9 off the sketch's own
  region and the area is an EQUALITY against the closed form at 1e-12, where the
  spur gear's equivalents carry the fit deviation as a band. `RackSpec.MatingGear`/
  `RackSpec.For` convert both ways so a pair cannot claim one tooth system and mean
  two; the profile shift does not travel, because it says where a GEAR sits against
  this rack rather than what the rack is. Conjugate action is measured the same way
  as the gear pair's and for the same reason (`Coupling.RackAndPinion` would enforce
  what the test asserts), with the pinion LIFTED to open backlash — legal because an
  involute against a straight rack transmits the same ratio at any mounting height,
  the flank normal being fixed so its supporting line is tangent to the base circle
  and translates by exactly r_b·dφ. Measured 6.94e-5 mm of advance variation over
  1.2 tooth pitches against 1.21e-1 mm for a 25° rack on a 20° pinion — a 1740×
  separation, so the instrument reads flank FORM.
- **The worm is a thread; the wheel is honestly an approximation** (`Gears.Worm.cs`).
  A ZA-form worm is straight-sided in the AXIAL plane, which makes its body one
  helical sweep of a trapezoidal (radius, axial) profile — the family
  `SolidFactory.MakeThreadedRod` already builds every modelled thread from — so it is
  exact and boolean-free for the thread's own reason: the root lands are part of the
  sweep, so no core cylinder and no coaxial tangent seam exists. **Multi-start needed
  no new machinery at all**, which is the finding worth keeping: a helical sweep
  repeats every LEAD, so the profile handed over covers one lead and simply contains
  z₁ teeth. The WHEEL is where the honesty is spent. A true worm wheel is throated,
  and its flank is the ENVELOPE of the worm's motion — hobbing kinematics with no
  closed form to draw — so what is offered is a helical gear at the worm's LEAD
  angle: the exact geometry of a crossed-helical pair, which meshes, transmits the
  stated ratio, and touches at a POINT rather than along a line. That is stated in
  the API docs, the type remarks and the docs page rather than buried, because the
  difference between point and line contact is the difference between a motion drive
  and a load-carrying reducer. The throated envelope is filed as
  assessed-not-promised. **Two verification decisions are worth recording.** The
  worm's geometry is measured from its own FIELD, and the tessellation's chord bias
  is DERIVED rather than allowed for — the helical bands are chorded in PHASE only
  (the generator is straight, so a v-chord is exactly on the surface), so a measured
  axial thickness is narrowed by 2·r(1 − cos(π/n))·tan α, 0.0267 mm predicted against
  0.0275 measured. The consequence is the pretty part: a tooth CENTRE is the mean of
  two flank crossings, so that bias cancels EXACTLY and the lead reads to 1e-14,
  while a thickness is their difference and doubles it. And the handedness
  cross-check earns its keep because the worm is a helical sweep in the B-Rep kernel
  while the wheel is a twisted extrusion in the modelling layer: two independent
  constructions that must agree about what "right-handed" means, or a correctly
  specified pair would be BUILT meshing the wrong way (the standing lesson that
  handedness cannot enter through a pose and has to enter the arithmetic). Both are
  read off the geometry — the worm by the sign of its quarter-turn advance, the wheel
  by ONE probe on the pitch cylinder at +twist, inside for a right-hand wheel and
  outside for a left-hand one because twice the twist exceeds the tooth half-angle.
  *Herringbone and crossed helical are the same geometry paired differently*
  (`HerringboneGears.cs`, `CrossedHelicalGears.cs`, `HelicalGearGeometry.cs`), and
  three decisions carry them. **A herringbone's apex is a WELD and the symmetry is
  what makes it one**: both opposite-hand halves share the transverse section at the
  mid-plane, so the twist law is Λ-shaped in z, the mid-plane is a plane of exact
  mirror symmetry, and the upper half IS the lower half reflected — which turns the
  junction from "a boolean over a large coincident planar region" into a weld BY
  INDEX. Three exact facts carry it and none is a tolerance: the apex ring's z is
  `Height·1.0`, the reflection z → 2a − z fixes it bit for bit (2a is exact and
  2a − a = a), and every wall facet spans two rings, so "every vertex is at the
  apex" is an exact test for the two cap facets to drop. Reflected faces keep vertex
  0 in place (`[a, d, c, b]`), the recorded rule that a polygon's winding may be
  reversed freely and its fan diagonal may not. The verification is the identity
  itself — the vertex set is invariant under z → W − z BIT for bit, where a
  tolerance would accept a weld that had drifted — plus the two halves' helix angles
  read off REAL transverse sections of the built solid as +20.000 and −20.000
  degrees, that instrument mutation-checked by seeding it from the 20° law on a 30°
  solid. **The apex relief groove is filed rather than shipped, and the entry
  carries the measurement**: a groove is material genuinely REMOVED, so it wants a
  boolean rather than another weld, and subtracting an axial band from a gear fails
  in both engines (the exact mesh boolean's imprint at every relief diameter, gap
  width and density tried; the B-Rep boolean as an unclosed solid with 1522 unpaired
  edges for the SAME band against an ordinary spur gear — which is what shows it is
  gear geometry rather than the herringbone's weld). What it wants is a
  mixed-section ring stack, a construction rather than a parameter. **A crossed pair
  needs no geometry, only arithmetic — and the signed form is one rule where the
  textbook states two**: Σ = β₁ + β₂ over SIGNED helix angles reproduces both "β₁ +
  β₂ same hand" and "β₁ − β₂ opposite hands" once the second gear's hand rides in
  the sign of its own angle, and construction then VERIFIES what it placed rather
  than trusting the formula, by requiring the two tooth traces to be the same line
  at the contact point (the geometric content of Σ, and the one thing a sign slip
  breaks). The trap worth a test is that **the ratio follows the TEETH**: on
  parallel axes z₂/z₁ and r₂/r₁ coincide and the habit is harmless, while on skew
  axes r = m_n·z/(2·cos β) puts them cos β₁/cos β₂ apart — 46% for a 20°/50° pair.
  **And a defect came out of building it, of the "two conventions each individually
  right" family**: the ISO 53 rack coefficients and the profile shift are quoted
  against the NORMAL module because a hob cuts them, while `GearSpec` reads them
  against the TRANSVERSE one — and they are all RADIAL LENGTHS, so every one must be
  divided by m_t/m_n. Unscaled, a 0.38 fillet reads 1.34× too large at 45° and a
  24-tooth member is refused outright for overlapping root fillets: a plausible pair
  that cannot be drawn. The conversion is checked by identities rather than by
  inspection (transverse thickness = normal thickness / cos β; undercut limit =
  2·h_a*_n·cos β/sin²α_t). **The helical pair's conjugate test is the
  transverse-section argument, made into a measurement of the half that could be
  wrong**: at every section a helical pair IS a spur pair, since each member's
  section is its own spur profile rotated by ψ(z) and rotating both rigidly moves
  the PHASE of contact and not the ratio — so the spur pair's contact-measured
  conjugacy carries over, and what is left to check is that a real transverse
  section of the built solid, rotated back by ψ(z), lands on the exact spur region's
  zero level. The bound is DERIVED (arc-flattening sagitta + wall-panel chord +
  biarc fit deviation, summed because all three are systematic) and the instrument
  mutation-checked at a 5% twist error, which reads over 100× the bound.
  *Cycloidal profiles* (`CycloidalGears.cs`, `CycloidalDrives.cs`) follow the involute
  file's shape — a fit into the arc vocabulary with the deviation reported — and add
  four decisions of their own. **The shape parameter belongs to the PAIR, not to the
  gear**: an involute gear is conjugate to any other of its module and pressure angle,
  but a cycloidal wheel's epicycloidal face rolls against a pinion's hypocycloidal
  flank only if ONE describing circle traced both, so `CycloidalGears.Mesh` refuses a
  mismatched pair by name and `Pair` hands both members the same circle. That is also
  why a single `GeneratingCircleDiameter` is the right field rather than a face/flank
  pair: sharing one circle across a set satisfies conjugacy in both directions at once,
  which is exactly the interchangeable-set practice. **The radial-flank identity is
  reached, not special-cased**: ρ = r/2 makes the hypocycloid's y-term vanish
  identically, so the general formula returns a straight line and the general biarc fit
  returns literal `Line2d` pieces — and the test therefore MEASURES straightness off the
  generated sketch (crossings within 1e-12 rad of one ray) instead of asserting the
  formula it was built from, with a hypocycloidal flank reading 1e-3 on the same
  instrument. **The centre-distance sensitivity is measured and documented rather than
  warned about**: a cycloidal pair's describing circle must roll on BOTH pitch circles,
  so unlike an involute pair it is not centre-distance invariant, which forces the
  conjugacy test to the DESIGN distance and forces backlash to come from thinning the
  teeth (exact — a cycloid rotated about its own pitch centre is the same cycloid at
  another phase) rather than from mounting long. Measured 4e-6 rad of spread at the
  design distance against 1.3e-3 at +0.3 mm, and the same 300× separation for a wheel
  cut with a 1.6× describing circle — one instrument, two mutations, both seen. And
  **the drive disc's pin locus is derived rather than transcribed**, which settles more
  than it was asked to: substituting the orbit and the disc rotation into
  `Rot(−λφ)(P_j − O(φ))` collapses to one curve for EVERY pin exactly at λ = −1/(N−1),
  giving the lobe count, the 2e depth and the counter-rotating rate for free — and
  repeating it for a lobe difference d leaves the pin phase at 2πj/d, a whole number of
  turns only at d = 1, so the one-lobe-difference restriction is a THEOREM the refusal
  can state rather than a v1 limit. The cut profile then costs nothing extra because the
  cam roller-follower work had already found the shape of the answer: an offset curve's
  unit tangent IS the base curve's, so the fit gets exact tangents free and the same
  `(1 − R_r·κ)` factor states the cusp refusal. The verification is that derivation's own
  identity — every pin reads exactly the pin radius from the disc sketch's signed
  distance at every input angle, measured residual equal to the fit deviation to a ratio
  of 1.002 — which is simultaneously the clash check and, swept over candidate rates,
  the ratio MEASUREMENT.
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
  test could have shown). *A deformed shape looked like GEOMETRY and is now an attribute
  too* — see the record below, which is where the animation of a structural result comes
  from. Two smaller rules earn their place: a zero-span range normalizes to
  **0.5**, because a constant field has no position to report and an extreme colour would
  read as a hot spot; and a merged VTU fills a part's missing array with **NaN**, VTK's
  own "no value", since dropping the array loses the result that exists and zeros show a
  fake safe region. *A LOG-SCALE field is declared by its producer in the units string,
  and the legend renders the declaration* — `log10(cycles)` (the `FatigueResults`
  convention) makes `FieldLegend` print anti-logged tick labels on integer decades
  (`TickMarks`/`TryLogUnits`), with the end ticks always stating the true range and a
  title in the base units tagged `LOG SCALE`. Deliberately the units string and NOT a
  `FieldDisplay.LogScale` boolean beside it: a flag next to a units string that also
  says log10 is two spellings of one fact, free to drift (the units-consolidation
  lesson), whereas the declaration already round-trips through the document format so
  persistence comes free. And it does not violate the `SymmetricAboutZero`
  never-apply-silently rule, because no colour moves — linear colour over log values IS
  log-colour — the legend is typesetting what the field itself states, where re-centring
  a range changes what the numbers mean. *The first-class mode has since landed beside
  it* — `FieldDisplay.LogScale` for a field carrying REAL decade-spanning values (raw
  cycles, contact pressure) — and landing it did NOT reopen the flag-vs-units argument,
  because the two spellings answer different questions: the units string says the
  VALUES are already logged (colours stay linear over them), the flag says the COLOURS
  should log raw values, and a display wants one or the other, never both. The colour
  position becomes `(log₁₀v − log₁₀min)/(log₁₀max − log₁₀min)` in the shared
  `FieldRendering.SourceColors` (CPU-side, so all three front ends and the glTF
  `COLOR_0` export inherit it with no shader change); a non-positive value maps to NaN
  and hence the no-value grey (see the record below), and a
  display whose range is not strictly positive is refused BY NAME when it resolves —
  painting every node the bottom stop would be the silent version. The composition
  claim was made checkable rather than trusted: `FieldLegend.TickMarks` converts the
  flag case's raw range to log10 and then runs the units case's OWN decade-tick
  arithmetic — one tick builder, so the two spellings provably print the same ticks
  for the same data (asserted as array equality) — and the flag rides the document
  file write-only-when-set, the persistence rule everywhere else follows.

### Animating a deformed result — and the exception that turned out not to be one

The animation layer's load-bearing rule is that **an animation must not touch geometry**:
instance count and order are independent of t, so a viewer animates with matrices alone
and picking keeps working. A deformed-shape plot looked like the counter-example — it is
genuinely new vertex positions per frame — which is why the first version built the
displaced mesh on the CPU, re-uploaded it, and was documented as *off* the animation path.

**It does not have to be an exception.** Send the displacement ONCE as a vertex attribute
and let the vertex shader apply `position + uDeformScale * displacement`; then a whole
result animation changes **one float uniform per frame**. Three decisions carry it, and
each was a real choice.

**(a) The attribute, under the constant-when-absent rule — established twice already.**
Slots 4–7 (offset plus three normal coefficients, one interleaved buffer), the same rule
`aOcclusion` and `aFieldColor` follow: no buffer means zero, so `aPos + s*0` is `aPos` for
*any* finite uniform and the normal expression takes its `aNormal` fallback. A part with
no displacement result therefore renders byte-identically however the scale is driven —
which is what let the change land with 102 of 103 rendered docs PNGs untouched. That
precedent existing twice is why the design is credible rather than hopeful: the property
was already proven at pixel scale before this used it.

**(b) The CPU deformation path RETIRED from rendering rather than living on beside the
shader one.** Two paths computing the same displaced shape would have disagreed in the
last bits forever, and the verification bar — *an animated frame at t must equal a static
render of the same configuration, byte for byte* — is only meaningful if there is one
renderer. What made retiring it free is an identity worth keeping: a triangle whose
vertices move linearly in `s` has edges `a + s·α` and `b + s·β`, so its facet normal
`(a×b) + s(a×β + α×b) + s²(α×β)` is **exactly quadratic in s**. Three coefficient vectors
therefore reproduce the displaced facet normal at every scale, which is precisely what the
CPU path recomputed. Only the direction matters (the fragment shader normalizes), so the
coefficients are scaled purely for float32 conditioning, and an all-zero result is the
shader's signal to fall back to `aNormal` — the CPU path's own exact-zero rule for a
collapsed facet.

Reusing the source normals instead was the cheap alternative and was rejected on a
measurement, not a feeling: a cantilever at its own 40× exaggeration turns its surface
**9.9°**, matching the analytic tip slope `atan(40·3·tip/2L)` — a ~12% shading error under
a 45° key light. Small enough that guessing would have got it wrong in either direction,
which is why the test asserts the analytic value rather than a threshold. Cost of the
switch, accounted honestly: **both committed deformed renders moved, and nothing else** —
the FEA bracket by 31 bytes of 7.17 million (max channel delta 14) and the modal blade by
3 bytes (max delta 1), float-vs-double rounding in the same formula. The bracket moves
more because it is the one with curved faces, where a face normal and a facet normal
genuinely differ. The cantilever in `fields.md` is byte-identical.
<br>Worth keeping as a rule about the accounting itself: this sentence first read "exactly
one render moved", which was true on the branch and understated on main, because the modal
page arrived from a sibling *between* the branch forking and its merge. **A render count is
a property of the merge, not of the branch** — so it has to be re-measured at the merge,
which is where the second figure above came from.

**(c) Picking deliberately does NOT follow the animation, and that is stated rather than
discovered.** A pick is answered by a BVH over triangles; a spatial index cannot be a
uniform, so it is built once at the part's own `DeformScale` — the animation's factor-1
configuration. A click is therefore exact on a static plot and at a load ramp's peak, and
off by the difference in exaggeration in between. Rebuilding a spatial index per frame is
exactly the cost this design exists to avoid, so the mismatch is documented instead of
paid for. `FieldRendering.Deform` survives for this one job, which is not a second render
path but a different question asked of the same formula. (The feature-edge overlay used
to be governed by the same reasoning — dropped whenever a part *carries* a displacement,
so the draw list never depends on t — and has since been RESTORED the way the fills were:
the edges carry their own displacement attribute and follow the same scale, so they are
drawn at every factor and correct at every factor, and the draw list still never depends
on t. See the "deformed edges" record below.)

The legend follows the effective factor, because its title states the number: a bar
reading `40X DEFORMED` over a frame drawn at 20× is exactly the lie the title exists to
prevent. And a `DeformationTrack` returning a scalar is what keeps the no-geometry rule
intact with nothing weakened — `LoadRamp` is honest for a **linear** solve (a linear
result scales exactly, so intermediate frames are the answers rather than a tween) and
`Oscillate` is the mode-shape law, whose caveats are the interesting part. Two are the
expected ones — a mode shape has no physical amplitude and its sign is a convention — and
the third is the one that actually misleads: **a mode does not animate at its own
frequency, and the formula that says it can is dimensionally correct.**
`cycles = frequency × duration` reads like the obvious binding and produces nonsense for
every real part, because a steel blade 80 × 20 × 6 mm rings at **783 Hz**
(`f₁ = (1.875²/2π)·√(EI/ρAL⁴)` with E = 210 GPa; the WIDTH cancels, since `I/A = h²/12`
for a rectangle, which is why the docs quote only the length and the thickness), so a
two-second clip would ask for
~1570 cycles — hundreds per rendered frame, aliasing into blur, and no frame rate fixes a
mode that is genuinely faster than video. Stiff metal parts run from hundreds of hertz to
tens of kilohertz; the structures slow enough to animate at true speed are things like
tall buildings. So the API takes a small fixed cycle count and the docs state the slowdown
factor. **Rank the caveats by what a reader will believe**: arbitrary amplitude and sign
are things people half expect, whereas "the animation runs at the mode's frequency" sounds
exactly like something a solver would arrange — which is why it is the one printed in bold
beside the figure.

**The cross-check that was supposed to catch this instead CONFIRMED it, and that is the
lesson worth more than the caveat.** The wrong formula was reported by a reviewer, and it
was verified before being acted on — correctly, since a correction believed rather than
checked is the same defect facing the other way. The hand calculation returned 764 Hz
against the reported 783, a 2.5% gap, which was read as the expected difference between
beam theory and a 3D solve and taken as corroboration. It was nothing of the kind: the
hand calculation used a textbook 200 GPa where `Materials.Steel` is 210, and
√(210/200) = 1.0247 reproduces the entire gap to 0.05%. A **false confirmation** — an
independent check that agreed to a few percent *and supplied a fluent physical story for
the residual*, which is what stopped the enquiry.

Two discriminators, and **which of them actually works here is the surprise — the
plausible one is the weak one.** *Size* has teeth: mode 1 of a slender cantilever must
agree with Euler–Bernoulli to well under 1% (the modal suite's own converged measurement
on a 100 × 10 × 10 bar is −0.07%), so 2.5% is out of family in either direction. *Sign*
sounds stronger and is not: a 3D solid has shear deformation and rotary inertia that beam
theory omits and both SOFTEN it, so the **converged** answer lies below the
Euler–Bernoulli one — measured −0.07%, −4.34%, −9.98% for bending modes 1, 2, 3,
monotonically further below as the wavelength shortens.

But "an FE bending result above EB is the wrong direction" is FALSE, and
`docs/examples/fea-modal.md`'s own refinement table prints the counterexample: against
EB's 835.5 Hz that cantilever reads 852.10 (+1.98%), 838.30 (+0.33%), 834.92 (−0.07%) and
833.78 (−0.21%) at 5×1×1, 10×2×2, 20×2×2 and 30×3×3. **Two of the four levels sit above
EB, legitimately.** Both are upper bounds on the truth and neither bounds the other:
displacement-based FE is Rayleigh–Ritz on a subspace, so its eigenvalues bound the true
ones from above; and EB is itself a kinematically constrained model that also drops rotary
inertia, so it does too. On a coarse mesh the discretization stiffening simply wins. The
sign test is therefore a statement about a CONVERGED mesh — decisive at the higher modes
where the softening is percent-level, worthless at mode 1 where it is 0.07% — and +2.5% is
a value the coarsest mesh there genuinely produces, so **a sign-only reading of this error
would have shrugged and moved on.** Only the magnitude caught it.

**The transferable rule: a cross-check that lands within a few percent AND supplies a
ready explanation for the gap is the most dangerous kind — so check the MAGNITUDE against
a converged reference before reading anything into the sign, because the sign of an
unconverged comparison is a property of the mesh rather than of the physics.** Same family
as the tests-that-pass-for-the-wrong-reason traps recorded elsewhere here, and a better
example than most, because nothing failed: the number was close, the story was fluent, and
the only thing wrong was a constant nobody re-read. Where a solver is in the room, quote
its own answer for the exact mesh the page renders rather than hand-calculating at all.

**What deliberately did not ride along: transient thermal playback.** Temperature per time
step is a *colour* animation, and colour has no single-uniform form — it needs the colour
attribute re-uploaded per frame, or n attributes uploaded once and indexed. Assuming it
rides along because displacement does would be the mistake; it is scoped separately.

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
- **A flange's two ENDS are independent, and that is the whole of v2's surgery.** Each end
  is either FLUSH with the wall's own corner — where the flange's cross-section chain
  REPLACES the wall's end edge in the neighbouring face's loop — or INSET, where the rim is
  split (`TopologyEditor.SplitEdge` patches every using loop), the leftover wall becomes a
  stub and the same chain is closed against a new vertical edge as an end cap. Both build
  the chain once. **v1 refused one of each as "the corner case in disguise" and it is not
  one**: the two rules touch no common coedge, so a flange running to one end of a plate is
  simply one of each, and the code went from branching on a `FullWidth` flag to settling
  each end on its own. The flush path keeps its non-obvious duty: the neighbour must be
  **re-surfaced as a `PlaneSurface`**, because the widened loop now reaches out past the
  flange's tip and would escape a domain-driven `ExtrudedSurface`'s parameter rectangle —
  the trim-the-neighbour rule from rim surgery, running the other way.
- **A bend relief is a change to the BLANK, which is why it needs no surgery at all.** The
  obvious reading of a relief is a pocket subtracted from the folded body, i.e. a boolean;
  the right one is that a relief notches the base flange's own OUTLINE
  (`SheetMetalBody.BaseOutline`), which is what the sheet is extruded from AND what the
  flat pattern starts from. So one declaration produces both views, exactly as an edge
  flange does, and `SheetMetalSurgery` needed no change whatever — because between its two
  notches a relieved flange runs the full width of the shortened wall, arriving as an
  ordinary FLUSH flange whose neighbours are the notches' own walls. Two riders: with no
  relief declared `BaseOutline` **is** `BaseSketch` by reference, so nothing that already
  worked can move; and the notch curves are emitted through the SAME frame-mapped code the
  flat pattern uses, which is sound because the base node's flat frame is the identity —
  "the blank's coordinates ARE the base sketch's" restated as an implementation.
- **A relief on a flange's TIP means the same thing by a different route, and the earlier
  refusal had the premise right and the conclusion wrong.** It read "a relief notches its
  parent's OUTLINE, and a flange's wall is built as a plain rectangle rather than from a
  sketch" — both halves true. But there being no outline to notch does not mean there is
  nothing to do: the notches travel with the PARENT's own construction instead
  (`SheetTipNotch`, `Node.TipNotches`), and a parent is built before its children, which is
  exactly why it has to know about their reliefs. `BuildTip` then emits one four-sided
  planar piece per surviving stretch of tip plus a band per notch segment, so between its
  notches the child runs the full width of a tip face that is still four-sided, planar and
  square to the bend line, and arrives at `AddEdgeFlange` as an ordinary FLUSH flange —
  the base-edge relief's own trick, one level in. With no notches the same calls in the same
  order produce the same edge pair and the same face, so an un-notched flange is untouched
  down to the bit. **A notch position is stated as its two points on the flange's own
  tangent line and read as their component ALONG the span**, because that is the one thing a
  coordinate convention could get wrong here: `Order` swaps `q0`/`q1` for an Up flange and
  not for a Down one (since `a = Outward × Inside` is `−T` in one case and `+T` in the
  other), so it is measured rather than derived.
- **A notch is a DETOUR in a loop, so one that runs out of its parent is silent** — and
  measuring that is what put the guard in. On an 80×50 plate a 200-deep relief leaves a
  self-intersecting blank whose SIGNED area still reads exactly base-minus-notches (2800 =
  4000 − 2·600, because a Green's integral over a bowtie is not the enclosed area) and
  whose extrusion still passes `Validate` with 18 faces. So every point of the notch below
  the surface is required to lie strictly inside the parent's own region
  (`SketchRegion.SignedDistance`), which also catches a notch reaching into a HOLE. The
  guard claims no more than it proves: a notch passing clean through a hole and out into
  material on the far side has its own corners inside and is not caught.
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
  with the K-factor wired to a constant. **A bend relief EXTENDS that oracle rather than
  needing a new one**: a relief takes the same material out of the folded body as out of
  the blank, so the discrepancy is unchanged and the relief contributes exactly nothing to
  it — which is the assertion that catches a relief reaching only one of the two views,
  the failure an approximate-agreement test waves through. Beside it sit two exact
  statements: the blank's area falls by exactly the notch's closed form (which is also
  what catches an inward arc swept the wrong way, since a mis-swept dome ADDS area), and
  the folded volume falls by exactly `area × thickness` per notch.
- **Three conventions carry every dimension**, and each was a real choice. `Length` is
  measured from the OUTER VIRTUAL SHARP (the drawing dimension). The bend is placed
  **bend-outside** — its tangent line IS the named edge, so the material continues
  outboard through the bend; the alternative ("material inside", flange outer face flush
  with the edge) would make the base sketch mean something less than the blank's base
  region and complicate the unfold for no gain. And **a flange folds toward the face its
  edge is quoted on**, that face becoming the inside of the bend, which makes
  `Up`/`Down` mean one thing all the way down a chain.
- **The optional-parameter rule reaches its enum form.** `EdgeFlangeFeature.Relief` is a
  `SheetReliefOption` (`None`/`Rectangular`/`Obround`) rather than a nullable
  `SheetReliefKind`, because `ParamEditors.KindFor` gives an enum a DROPDOWN whose rows are
  the type's own members — so it can no more say "unset" than a slider can, which is the
  same argument that keeps `KFactor`'s sentinel 0. The price of a second spelling is drift,
  paid the way this repo pays it: a test drives EVERY member through a real regeneration
  and reads the kind back off the flange tree BY NAME, and asserts the two enums' member
  sets agree, so a kind added to one and not the other fails there rather than quietly
  meaning something else.
- **A closed corner is ONE operation over two flanges, and its miter is a closed form.**
  v1 and v2 refused it as "the two bends' bands have to be trimmed against each other",
  costed alongside curved-face shelling as needing surface–surface re-intersection. It
  needs no intersection at all: two bends of the same radius quoted on the same face have
  axes that MEET over the sheet's corner, and two equal-radius cylinders with intersecting
  axes meet in two ELLIPSES — the same fact `Filleting`'s sharp-corner miters already
  stand on. Each band's cut is an exact `Ellipse3d` whose centre is the shared axis point,
  whose one semi-axis is the inward radial and whose other reaches where the two
  OUTWARD-OFFSET bend lines cross, itself the closed-form bisector
  `R(ŵ_A + ŵ_B)/(1 + ŵ_A·ŵ_B)`. **And the two flanges' cut chains are the SAME curves** —
  the configuration is symmetric under reflection in the miter plane, which swaps them —
  so they are built once and used by one face of each: nothing lies in the miter plane,
  which is what "closed" means, welded rather than butted across a gap. The reason it is
  one DECLARATION is topological rather than geometric: a full-width flange consumes its
  wall and splices its cross-section into the faces at both ends, so a second flange
  declared afterwards on a neighbouring edge finds a wall that is no longer four-sided —
  which is precisely the refusal this replaces — and locating both before either is built
  is the whole fix, which also lets the sheet's own corner edge fall away once both walls
  are consumed. **It moves the volume identity, by exactly the material it shares**: each
  flange now runs to the miter plane instead of stopping at the corner, so the folded body
  GAINS that flange's cross-section's first moment about the corner line,
  `((R+T)³ − R³)/3` from the annular sector plus `T·L·(R + T/2)` from the wall, while the
  blank is untouched — which is what "an unrelieved corner shares material" means, since
  the blank has none to give. Measured 10713.244229 against a predicted 10713.242157,
  1.9e-7 relative, the tessellate-then-Richardson grade; exact at 70°, 90° and 110°. Two
  build lessons: a mitred band's PARAMETER RECTANGLE has to be lengthened past the span
  (the trim-the-domain-driven-surface rule that already re-surfaces a spliced neighbour as
  a plane, running the other way), and the reach must be measured from the arc's own
  SEMI-AXIS rather than from its two endpoints, because past a square bend the ellipse's
  furthest point is at t = 90°, strictly inside the trimmed range — endpoints alone
  under-reach and the tessellator refuses with the loop 0.04 outside the rectangle.
- **A hem, a jog and a curl are already expressible; what was missing was the
  declaration.** Each is two or more ordinary bends, so none needed new geometry — and a
  hem in particular is two bends the SAME way rather than one 180° fold, which has no
  geometry in this model at all (its outside setback `(R+T)·tan(θ/2)` diverges there) and
  whose return leg flat against the sheet is exactly the coincident boundary the tangency
  argument refuses. Two hits is also how a press brake makes one, so the model and the
  process agree, and the intermediate leg IS the open gap: `gap = 2R + L`, so a gap of
  `2R` or less is a CLOSED hem and refuses by name. A jog's leg is closed form,
  `L = (offset − (2R + T)(1 − cos θ)) / sin θ`. A curl is honestly POLYGONAL and says so
  in its own API doc rather than approximating a continuous roll.
- **A flange CUTOUT did not need the wall rebuilt from a sketch, and that correction is
  the finding.** The backlog framed flange-local holes as "one change, two features" —
  the wall's outline generalised from a rectangle to a `Sketch`. Measured, a HOLE needs
  none of it: it is additive surgery on the wall's two planar faces (a hole loop apiece)
  plus one band face per profile segment, with the rectangle untouched; only a change to
  the wall's OUTLINE (a relief on a tip, a corner tab) needs the generalisation, which is
  therefore still open and named. Cutouts are declared in the flange's own local
  coordinates — the same coordinates its rigid 2D frame in the blank and its 3D frame on
  the wall both place — so one declaration reaches both views exactly as a relief does,
  and which of the wall's two faces a cutout lies on is DERIVED from its own plane normal
  rather than declared (it is the inside face for an Up flange and the outside for a Down
  one, so stating it would be one more convention to get wrong).
- **A mirrored placement is re-DECLARED, not re-placed.** The refusal was real and its
  cause was NAMING: a flange tree is ordered and quoted on named edges, so a reflection
  has to move the names. `MirroredInPlane` rebuilds the tree the other way round and the
  compiler places THAT on a proper frame — `P = P′·FlipX` with `P′` proper, so placing the
  reflected body on `P′` IS placing the original on `P`, and the reflection is spent once,
  half in the declaration and half in the frame, never twice. Three remaps, each forced by
  the reflection's own arithmetic: `Sketch.Mirrored` restores winding by REVERSING the
  loop, so a segment at index `i` of `n` lands at `n − 1 − i`; an edge's parameterization
  reverses with it, so a span `[s, s+w]` of an edge of length `L` becomes `[L−s−w, L−s]`;
  and cutouts are quoted in the flange's own local x, so each mirrors and slides back by
  the width. **FlipX rather than FlipZ** is load-bearing: it leaves the sheet's own +Z —
  the face every bend line is quoted on — exactly where it was, so `SheetBendDirection`
  keeps meaning one thing. Verified by vertex SETS through the reflection (a volume
  comparison passes a tree flipped the wrong way round) plus `Mirror(Mirror(x))` being the
  original, which is what proves the remaps are an involution.
- **Multi-body sheets and welded assemblies needed no sheet-metal work**, and saying why
  is the deliverable: a sheet part is ONE flange tree and ONE blank by design, and a
  weldment is several PARTS in an `Assembly` — which the document model already expresses,
  the BOM already counts and mates already position. What was missing was the seam that
  cuts them all (`SheetMetalFeatures.UnfoldAll`, read by both the `--flat` CLI verb and the
  viewer's Flat button so neither can grow a second opinion) and the statement of the
  boundary: folding several bodies into one `SheetMetalBody` would make `FlatPattern` mean
  two things at once, and a BOOLEAN of two sheet solids is a solid rather than a sheet part
  — a union node carries no flange tree, so it has no blank.
- **A curved bend line is refused by a THEOREM, not by a missing surface**, and the
  backlog's premise ("a curved bend line sweeps a developable band ... needs a new swept
  surface") was wrong in exactly the way that invites someone to go and build it. Folding
  a sheet along a curve is NOT AN ISOMETRY of the sheet, so no flat blank produces it:
  along a circular bend line the band is a torus segment, whose Gaussian curvature is
  non-zero everywhere a flat sheet's is zero, so the material must stretch or shrink.
  That is FORMING rather than bending, it has no bend allowance, and building the surface
  would have bought a solid whose flat pattern was a lie — the one thing "the declaration
  IS the model" exists to prevent. Measured rather than asserted: a straight bend of width
  w spends exactly `w × BA` of blank and has exactly that much neutral-surface area, while
  the same bend run round a circle has a Pappus area about 3% different — the material a
  fabricator would have to find, and if the two agreed the refusal would be wrong.
- **A cutout may CROSS the bend line, and the exact tier is the RECTANGLE ALIGNED with it —
  a complete answer rather than a budget.** The filed framing expected a new curve type or
  a sampled edge ("a general curve wrapped on a cylinder"); neither is needed, and the
  reason is the isometry the curved-bend-line refusal already stands on, read the other way
  round. Bending preserves the sheet's intrinsic geometry, so a straight cut running ALONG
  the bend line stays straight (it becomes a ruling of the cylinder) and one running ACROSS
  it becomes a circular ARC — and the wall each sweeps through the thickness is a PLANE. A
  cut at any other angle wraps to a HELIX, and an arc in the blank wraps to nothing with a
  closed form at all. So the aligned rectangle is exactly the family the kernel can carry
  exactly, and the construction needs no new curve type, no new surface type, no trimmed
  face and no boolean: each slot splits both bend bands into THREE (the two full-height
  stretches either side plus a short one under it, whose arc simply sweeps less — all
  ordinary full-domain `ExtrudedSurface` bands), splits the rims they weld to, and notches
  both wall faces, which is a NOTCH in their outer loop rather than a hole because the slot
  reaches their own boundary.
- **A crossing cutout costs the K-factor's independence from the FOLDED shape, and it
  cannot be otherwise.** `SheetBendSection` deliberately carries no K, because K decides
  developed LENGTH and nothing about the folded shape — and that separation is what makes
  the volume comparison a real test rather than a tautology. A crossing cutout is the one
  feature that genuinely breaks it: the cutout is declared FLAT (punched in the blank and
  then bent, which is exactly why holes near a bend deform), and the only map from the
  blank to the band is the NEUTRAL-AXIS map, which K parameterizes. The resolution is to
  put the conversion where K already lives: the modelling layer turns the cutout's flat
  depth into an ANGLE and the surgery takes that verbatim, so the geometry stays pure and
  the ARGUMENT is what K decided. **The identity is then the bend's own formula restricted
  to the angle the slot takes away** — blank `w·(y₁ − y₀)·T`, folded `w·(y₁·T + (θ −
  φ₀)·T·(R + T/2))`, and since `−y₀ = (θ − φ₀)(R + K·T)` the difference is exactly
  `w·(θ − φ₀)·T²·(0.5 − K)`, a SLICE of the very discrepancy the K-factor owns. Two
  orientation errors were made and caught by `Validate`, both the same mistake: OUT of the
  material at a slot wall points INTO the slot, so the low wall's normal is `+axis` and not
  `−axis`, and the top wall's is `−u` and not `+u`; the tell each time was "an edge's two
  uses must have opposite sense" against the wall face sharing the edge.
- **A LOUVRE is an interior bend line plus a lance, and the surgery it needs is none.** The
  declaration is genuinely new — an edge flange's bend line is an EDGE of the sheet and its
  material grows outboard of the blank, while a louvre's is interior to a face and its
  material comes out of the sheet — but once the parent gives up the tab's footprint, that
  bend line is an ordinary edge of an ordinary four-sided planar wall, which is exactly the
  flush case `AddEdgeFlange` already builds. **A LANCE has a WIDTH, and that is a theorem
  rather than a modelling choice**: at zero width the tab's own side face is coincident
  with the wall of the opening it came out of, everywhere the bend band still lies inside
  that opening — two coplanar faces with opposite normals touching over an area, which is
  not a manifold boundary (measured: the mesh came back OPEN with a boundary loop on
  exactly those faces). So the clearance is strictly positive and zero refuses by name.
  **What each view loses then differs on purpose, and that difference IS the lance**: the
  blank keeps the tab and loses only the U-shaped KERF, while the folded parent loses the
  whole opening because the tab has left the plane and comes back as its own flange.
  Because the kerf leaves both views identically (the bend-relief rule), the volume
  identity is UNCHANGED and independent of the clearance — one more `W·θ·T²·(0.5 − K)`
  term, with the lance contributing exactly zero.
- **Generalising `RequireSquareNeighbour` was forced by the louvre and is a `gate should BE
  its correctness condition` case.** It asked for a SIGNED normal, which is a proxy: the
  sign says convex-or-reflex, and every corner of a HOLE is reflex, so every louvre failed
  it. The correctness condition is perpendicularity alone — the tab's cross-section rises
  out of the neighbour's plane on the far side of the shared end edge either way. And the
  companion question answers itself: which way that neighbour's loop walks is fixed by the
  WALL's own orientation, which is identical in both cases, so the chain direction needed no
  change at all. Edge flanges gained reflex corners for free.
- **What is still refused, with reasons rather than deferrals**: a crossing cutout that is
  not an aligned rectangle, or that runs THROUGH the bend into the parent (it would notch
  the parent's own faces, a change to the parent rather than to this flange) or floats
  wholly inside the band (it would leave the band a trimmed face with a hole in it); a
  crossing slot on a MITRED flange (a corner band already reaches past its span, and the
  two edits want the same parameter rectangle); two flanges sharing a stretch of edge; and
  flanges on a flange's SIDE edges. The **tear relief** is a documented absence rather than
  a refusal — it is what happens when no relief is cut, and its shape belongs to the press,
  exactly as spring-back does.

Frames & weldments (`Frames.cs`) follow the same doctrine — a declaration (profile +
skeleton) from which the members, the trims and the cut list are all derived — and
four decisions carry it:

T-joints completed the joint vocabulary by REUSING the butt cut rather than growing a
new one: an endpoint on another run's interior leaves the through member untouched
(the butt joint's own through-run role) and trims the abutting member by the facing
wall plane — the arithmetic factored into one `WallCut` both joints ask, bit-identical
for the incumbent butt path because the facing projection `a − b·(a·b)` is EVEN in
b's sign and IEEE negation is exact, so the T's sign-free raw axis and the butt's
signed kept direction compute the same bits. Detection refuses the shapes with no one
honest answer before any geometry: collinear landings (overlap, not a joint), an
endpoint on two interiors (ambiguous wall), the three-member confluence, and — for
free, through the shared `FlatWallOffset` — a round-walled through member (the coped
saddle again). The volume oracle stays the prism-cut identity, now met at a mid-run
wall: a perpendicular T keeps exactly A·(L − w/2) and a 45° one exactly A·(the
centroid fiber's distance to the wall crossing), both planar identities at 1e-9.
Mixed profiles per skeleton then cost one array: `Build`/`Path` take a per-run
override list (null = the default), every consumer of "the profile" reads the
member's own — the reach, the margin, the name, the area — and the wall offset
reads the THROUGH member's, which is the one place two profiles meet in one
formula. The miter needed nothing at all: the bisector plane is pure axes, and the
mismatched weld face a smaller section leaves against a larger one is the welder's
gap, not the kernel's problem.

- **The miter plane's normal is `a − b` and nothing is ever divided.** For unit
  leave-directions `a`, `b` at a joint, the plane with that normal through the joint
  contains both the bisector (`(a+b)·(a−b) = 0`) and the axes' common normal — so the
  recorded miter-apex trap (`sum.LengthSquared`, never `sum.Length` squared) is avoided
  STRUCTURALLY rather than carefully: there is no apex arithmetic at all. Both members
  read one canonical joint point and one normal (an exact negation apart, and
  normalization commutes with negation bit-for-bit), so the two cut faces lie in the
  bit-identical plane.
- **Every joint cut is a transversal boolean by construction** (the `Drill` overshoot
  doctrine): the member extrudes overlong past the joint by the cut plane's exact reach
  across its own section — an affine functional of the profile point, whose extremes
  over a line/arc outline are closed form — and a box tool whose base face lies exactly
  ON the plane subtracts the stub. No boolean ever sees coplanar or tangent input, the
  two halves of a joint are separate parts that merely meet, and the cut curves are all
  analytic (plane∩plane; plane∩cylinder ellipses on mitred tube, verified to CONVERGE
  on the closed form where a tracer polyline would be a fixed floor).
- **The volume oracle is the prism-cut identity**: a prism cut by end planes has volume
  `A · (axial distance between the planes' crossings of the CENTROID fiber)` — exact
  for any section because the crossing is affine over the section. It is stronger than
  the rectangular-wedge closed form (which drops out as a special case), it makes a
  closed frame exactly `A · perimeter`, and it has teeth on the angle profile, whose
  heel-datumed run line puts the centroid OFF the run so a wrong functional would miss
  by the centroid offset.
- **One part per member, and the rollup key is the NAME.** Sharing a `Part` between
  "identical" members would need an equality judgement over cut planes expressed in
  local frames that differ by rotation round-off — a near-tie the codebase refuses to
  let ulps decide — so each member is its own part and identical members share the name
  `designation x cut length`, which is exactly the `Bom.ByItem()` contract. The cut
  length rides `Part.CutLength` → `BomLine.CutLength` (the `Material` follow-the-part
  pattern), write-only-when-stated in the document envelope. Coped tube-on-tube saddles
  are refused with the tracer under-seeding reason on `FrameJointStyle.Cope`; T-joints,
  multi-member joints, zero-angle joints, consumed members and Bézier-outline trims are
  refused by name.

### Bevel gears: which approximation, decided by measuring all three

A straight bevel's flanks are ruled surfaces through the pitch apex, so the SOLID is
exactly a two-section ruled loft and the only real decision is what the section is.
Tredgold's back-cone approximation says the tooth on the back cone is the developed
spur involute of a *virtual* gear with `z_v = z/cos δ` teeth — which is generally not
an integer, so it cannot be drawn by `Gears.Spur` and has to be reached some other way.
Three candidates, all built or costed, and **the ranking inverted the intuition**:

- An **equivalent planar gear** — z teeth at `arctan(tan α / cos δ)` with tooth
  proportions scaled by cos δ — matches the projected back-cone tooth in pitch, arc
  thickness, tip and root radii *and* flank slope at the pitch point, and `Gears.Spur`
  draws it verbatim for free. It measures **1.4e-2 … 7.5e-2 module** from the tooth it
  approximates, which is **2–8× Tredgold's own error**. A second approximation that
  dominates the first is not a shortcut, it is the answer; dropped after being built.
- **Axial projection** of the back-cone trace reproduces the textbook
  `d_ae = d + 2·h_a·cos δ` directly, which is seductive, but the flank it makes has a
  back-cone trace that is *not* the Tredgold curve: 5e-3 … 3.7e-2 module from the true
  spherical involute.
- **Central projection from the pitch apex** — what a ruled-to-the-apex tooth does by
  definition — measures **7.2e-4 … 2.9e-2**, and reproduces the standard cone angles
  `δ ± arctan(h/R_e)` as an IDENTITY to 3e-16 rad when read back off the section's own
  radii. It wins on both counts, and the textbook diameter is recovered as a reported
  property rather than as the section's own extent.

The general rule is the one the numbers taught: **when a construction stacks a cheap
approximation on top of a principled one, measure the two SEPARATELY before assuming
the cheap one is subordinate.** The flank fit sits two orders below the method's error
by design, which is the relationship to aim for and the one the first attempt inverted.

Two limits are consequences rather than omissions and are therefore *reported*. A loft
section must be planar, so the end faces are planes and not the back and front cones —
the teeth are the correct cones, so every angle and the pitch diameter are exact, but
the heel SECTION is deeper than the real back-cone tooth (×2.4 at δ = 65°), which is
also what caps the usable cone angle near 68° with the ISO 53 profile-A root fillet.
The refusal there names the cause and a verified remedy rather than stopping, which is
the difference between a limit and a dead end.

### Planetary sets: the arrangement is the design, and the ratio is emergent

Three decisions carry `PlanetaryGears`. **The ring count is derived, not accepted**, so
coaxiality cannot be violated by a caller. **The internal ring is not a boolean**: an
internal tooth space is bounded by the same involutes as an external tooth of the same
spec with tip and root swapped, so the ring's bore is exactly a "cutter" gear's outline
used as a hole — which keeps it lines and arcs, hence exact in all three
representations, where a boolean would have cost a 2 000-segment intersection and
delivered less.

The third is the one worth generalizing. `Coupling.Gear` enforces a ratio, so asserting
a gear ratio against it is circular — the involute work already recorded that, and
measures conjugate action from contact instead. A planetary set is the **exception, and
precisely because it is an arrangement**: the couplings are per-MESH (sun–planet,
planet–ring) and the train value is what they compose to through the carrier, a third
body neither coupling mentions. So the Willis relation and the held-ring reduction are
genuine tests of the topology. What they test is the decision that the sun and ring pin
to the **carrier** rather than to the housing, so that every coupling is written on
coordinates already relative to the rotating line of centres — and the failure mode if
that is wrong is the dangerous kind: every individual coupling stays satisfied, the
mechanism solves, and the assembled ratio is quietly wrong.

### The meshing phase (`GearMeshing.cs`): one derivation, and the sign that was invisible

Every gear layout in the docs phased its pair by hand — `RotateZ(π − π/z)` — which is a
convention re-derived at each call site and checkable nowhere. `GearMeshing` states it
once, and the shape of the statement is the design decision: **the rule depends on the
tooth COUNTS and the drawing datum, not on the tooth form.** Two gears mesh when the
pitch circles roll together and a tooth of one sits in a space of the other, and neither
clause mentions the flank curve — so one helper serves involute, cycloidal and anything
else drawn to the same datum, which is why the primitive takes tooth counts and a stated
centre distance while the `GearSpec` overload is the convenience on top. The datum is
part of the contract rather than an assumption (`Gears.Spur` draws a TOOTH on +X,
`PlanetaryGears.RingProfile` a tooth SPACE), and both halves are MEASURED off the
regions' own signed distance rather than trusted.

The derivation is one idea used three times. A gear of z teeth turned by φ from a datum
whose tooth centre lies at τ has the **tooth-index coordinate** `u(θ) = z(θ − φ − τ)/2π`
along a direction θ in its own frame — integer at a tooth centre, half-integer at a
space centre. What makes a mesh a CONSTRAINT rather than a coincidence is that a
combination of the two members' coordinates is invariant under rolling, and *which*
combination is decided by how they roll: an external pair counter-rotates, so
`u_A(ψ) + u_B(ψ+π)` is constant; an internal pair co-rotates and engages along ψ from
BOTH centres (the pinion's engaging tooth points outward, away from the ring's centre),
so `u_R(ψ) − u_P(ψ)` is constant; a rack ties `x = −r·φ`, so the pinion's coordinate at
−π/2 minus x/p is constant. Each condition is `≡ ½` (or `≡ 0` for the rack) and solves
in closed form, the rack's reducing to `φ = −π/2 − x/r`.

**The finding.** `PlanetarySet.PlanetPhase` already had this solved — and what it had
solved was the INTERNAL half. The external rule depends on the azimuth with the opposite
sign, and the two differ by `2ψ(z_A + z_B)/z_B`, which is a whole tooth pitch — i.e. the
same placement — for every planet exactly when N divides `z_sun + z_ring`. **That is what
the planetary assembly condition IS**, and it is why one number can satisfy both of a
planet's meshes there, and equally why the sign could not be observed in the code that
carried it: inside a legal planetary set the two rules are indistinguishable. Away from
it they are not, and a general external pair at a general azimuth is exactly where the
difference bites. `PlanetPhase` now delegates bit-identically; both directions are pinned
(three assembling sets agree to 1e-9 of a pitch, a five-planet set violating divisibility
lands 16.8 pitches apart), and `PlanetarySet.RingPhase` keeps its `(z_p − 1)π/z_r` closed
form with a test asserting it IS the general rule at azimuth 0 — kept because it is the
simpler statement, and the test is what stops the two drifting.

**Which flank drives is the caller's.** The phase returned is the symmetric one: the
tooth centred in the space, which at the standard centre distance and standard
proportions is zero-backlash contact on both flanks at once. That is a placement, not an
operating condition, so `GearMesh.RolledBy(radians)` is how a design says which side it
runs on, and nothing guesses. The same reasoning settles the profile-shift refusal:
`x₁ + x₂ = 0` is what keeps the standard centre distance (the two pitch-circle
thicknesses then sum to the circular pitch, so one member's tooth exactly fills the
other's space and the centres coincide), any other sum needs the involute-function solve
for the operating distance — and since the PHASE rule does not depend on the distance at
all, the refusal names the tooth-count overload, which takes it as an argument.

**A helical pair needs nothing extra**, and the proof is short enough to keep: a twisted
extrusion's transverse section at height z is the drawn section turned by
ψ(z) = z·tan β / r, so the mesh condition holds at every section at once iff
`ψ_A·z_A + ψ_B·z_B` is constant in z — which, since ψ·z = 2z·tan β / m, is exactly the
requirement that the helix angles be equal and opposite. Phase a correctly paired set at
its drawn section and it is phased everywhere; measured at 0.141 mm of clearance at 0, ¼,
½ and the full face width.

Verification is from **contact** throughout and never through `Coupling.Gear`, which
enforces a ratio and says nothing whatever about phase — the involute file's own rule,
applied to the thing a coupling structurally cannot witness.

### A still only has to be right at one instant

A wrong phase or a wrong ratio is invisible in a static render that was phased by hand:
the picture was approved at the one angle it was drawn at. `GearTrainMotionTests` drives
a meshed pair through a `MotionStudy` and probes the exact profiles at **every recorded
pose**, with two mutations that fail in different ways — half a tooth pitch of phase
error reads −1.66 mm at every frame (the ratio still exact, so the coupling is perfectly
satisfied and only the geometry objects), and one tooth of RATIO error starts clear at
+0.14 mm and walks into −0.39 mm by the end of the sweep, which is the failure a single
still structurally cannot show. `MotionStudy.CheckInterference` is cross-checked on the
same rig and agrees; it stays the secondary instrument because it answers a boolean off
the display tessellation where the probe answers a depth off the exact profile.

**And a gear animation aliases, which is not a cosmetic problem — it misreports the
ratio.** The docs' planetary clip wants to be a seamless loop, and the smallest carrier
turn that is one is 120° (the three planets swap places, each having advanced a whole
number of teeth). At 24 frames that puts the sun at 1.17 and a planet at 1.08 tooth
pitches per frame; both alias to a slow forward creep and a viewer reads the sun as
turning *slower* than the carrier it drives at 3.5×. Restoring the same comfort takes
`z_planet + z_ring` = 78 frames (bare Nyquist would need 52), three times the committed
file for a clip that is no more informative — and it cannot be fixed by choosing better
tooth counts either, since the
requirement `z_sun + 3·z_planet ≤ 24` is below the undercut limit for any real pair. So
the clip runs 30° and does **not** loop, exactly as `animate-explode` does not: the
honest reading beat the seamless one. Same family as `DeformationTracks.Oscillate`'s
"a mode does not animate at its own frequency" — a clip's timing is a viewing parameter,
and the general rule is to **check the fastest periodic detail in the scene against the
frame step before choosing a clip length**, and where both cannot be had, say which was
given up.

### Design studies: driving `[Param]` values by an optimizer (`DesignStudy.cs`)

`DesignStudy.Minimize` moves a part's `[Param]` values to minimize a measured objective
under measured limits. Everything under the loop already existed — `FeatureHistory`
regenerates with prefix caching, `[Param(Min =, Max =)]` already declares the box, and any
measurement a caller can write over a `Part` is an objective — so the feature is the loop,
and every decision in it is about *what the loop is allowed to assume*.

**Derivative-free is not a preference, it is forced.** A regeneration can change TOPOLOGY:
raise a plate's thickness past a blind hole's depth and the hole breaks through; raise a
fillet radius past half the face and the rim surgery refuses. A finite-difference gradient
taken across such a step is not noisy, it is *meaningless* — the two sides are different
solids. So the objective is treated as a black box that may also simply refuse.

**Hooke–Jeeves rather than Nelder–Mead**, and the three reasons are all about this problem
rather than about convergence rates. (a) *The box is the point.* `[Param(Min =, Max =)]` is
where a feature already states what it accepts, and a great many real answers sit ON a
bound; a simplex has no way to rest on one, and clamping its reflections collapses it,
whereas a compass poll clamps to the bound and keeps polling. (b) *A refused design is
just a poll that does not improve* — a simplex vertex that cannot be evaluated has to be
given a fictitious value and can degenerate the simplex. (c) *The step size IS a distance
in parameter space*, so the stopping rule states a bound on the ANSWER rather than on the
objective spread. That last one is what makes the verification possible at all: the search
halves every step together, so the last poll that improved nothing did so at a step `s`
with `tol < s <= 2·tol`, and it moved the incumbent by ±`s` along each axis with neither
direction better — so for an objective unimodal along the axis the optimum is within
`2·tol`. `StudyResult.OptimumTolerance` is exactly that, and it returns **infinity** when
the search stopped on its evaluation budget, because then no such claim exists.

**Constraints are a feasibility FILTER, not a penalty.** A penalty needs a weight, and a
weight silently trades one gram against one micrometre — a number nobody can justify —
but the deciding argument is the other one: a penalized search *returns* the minimizer of
`f + mu·g`, which for any finite `mu` is infeasible, so the study would hand back a beam
that fails its own deflection limit and merely scored well. The filter compares
`(violation, objective)` lexicographically, so a feasible point always beats an infeasible
one and the answer meets its limits or the study says by name that it could not; while
nothing feasible is known the search descends on violation alone, which is what gets it in
from an infeasible start. Violation and margin are relative to each constraint's **own**
limit, the only dimensionless ranking that needs no weight from the caller.

**What stopped the search is a measurement, not a judgement.** A bound is binding when the
answer's value *is* the bound — exact, no epsilon, because the clamp assigns it verbatim.
A constraint is binding when, in the final poll round, it refused a neighbouring design
whose objective was strictly better; that is the definition of "this is what stopped it",
read off the search's own last act rather than inferred from a tolerance on a margin. The
complete poll (every direction, every round) is what makes that reading available, and is
why the poll is not opportunistic.

**Three findings came out of building it.**

*(1) A `Shape` graph is LAZY, so a geometric refusal usually does not surface at
`Regenerate`.* A feature's `Apply` typically only builds a graph node, so the kernel's
refusal ("the rim feature consumes the edge … its mitered corner offsets cross") is raised
by the LOWERING — which happens the first time something measures the part. Measured: a
`FilletRimFeature { Radius = 11 }` on a 30×20 plate regenerates with
`RegenerationResult.Succeeded == true` and throws from `GetMesh`. A study watching only
`Succeeded` would call that design fine. Hence two outcomes rather than one
(`RegenerationFailed` / `MeasurementFailed`), both carrying the kernel's own words, and the
study does NOT force a lowering of its own, because only the caller's measures know which
representation they need.

*(2) Halving every step together freezes the diagonal directions' SLOPE, and that is what
stops a pattern search on an active constraint.* On a constraint coupling two variables the
descent direction runs ALONG the boundary — a beam wants to be deeper *and* narrower — so
no single-axis move helps and the poll needs diagonals. But a diagonal `(+s_i, −s_j)` points
at slope `s_j/s_i`, and a shared halving leaves that ratio fixed for the whole run, so every
diagonal is either always too steep (leaves the feasible set) or always too shallow (heavier).
Measured on a two-variable beam whose depth should reach its ceiling of 25: with a shared
step it stops at **21.92**. The fix is to search the RATIO — halve one axis's step at a time
— which makes the reachable slopes a dyadic grid `s_j·2^a / s_i·2^b` that refines as the
search does, and it reaches the analytic answer (depth exactly 25, width within tolerance of
the closed-form 4.876). It runs **only** when a constraint refused an improving poll, which
is the only situation the ratio can matter in, and both halves of that claim are pinned by
test: the 21.92 through an internal `AdaptStepRatios` seam, and bit-identical trajectories
where no constraint holds the answer. The direction set is still finite, so this is not a
completeness claim — a boundary whose slope never lands between two reachable ones can still
stop the search, and the honest fix is a direction set that becomes dense (OrthoMADS, whose
Halton generator is deterministic), filed rather than guessed at.

*(3) A study is an ANALYSIS, not an edit.* It restores the part to the values it started
from and returns the answer as data. Two reasons: a search evaluates hundreds of designs
and none of them is history, so pushing them onto an `UndoStack` would be absurd; and
adopting the answer has to be a deliberate act, which `StudyResult.Edits(part)` makes one
undoable `SetParameters` per driven feature through the same JSON seam as `SaveParameters`
and MCP `set_param`. The study writes through that same seam internally rather than
composing `DocumentEdits`, because one edit per feature would rebuild once per feature.

**Verification is against closed forms, not against "it improved."** The cantilever's
minimum-mass depth for a stated tip deflection is analytic — `delta = 4PL³/(E·b·d³)` gives
`d* = cbrt(4PL³/(E·b·delta))` — and the study lands on 15.6201 mm against 15.6179, from the
FEASIBLE side and inside the optimizer's own criterion, with "tip deflection" named as
binding; a second limit lands on its own closed form, which is what separates agreement
from coincidence. A monotone objective rests EXACTLY on the box edge (asserted with `==`,
since the clamp assigns it) with the bound named; an unreachable limit is
`NoFeasibleDesign` by name with the miss quantified; a kernel refusal appears in the
trajectory and the search converges onto the refusal's own analytic boundary
(`Shape.Drill`'s "centres must be more than one diameter apart"); and determinism is
asserted on the WHOLE trajectory bit for bit rather than on the answer, because two
searches can reach one point by different routes and only the routes would show it.

Not in v1, stated rather than implied: discrete (`int`) parameters are refused by name
rather than rounded; there is no memoization of repeated design points, so the trajectory
is exactly the list of evaluations performed (which is what makes the determinism
comparison mean what it says); and there is no `Maximize` — negate the objective, so a
report cannot disagree with itself about which way is better.

### Configurations: one `FeatureHistory`, N named parameter sets (`Configurations.cs`)

A configuration is a NAME plus a `[Param]` value dictionary, and **the seam is the whole
design**: the values are the `FeatureHistory.SaveParameters` JSON verbatim, so applying one
is `LoadParameters` and nothing else. That matters because this repo has been strict about
it — a saved parameter file, an MCP `set_param`, `DocumentEdits.SetParameter`, the
properties-panel typed editors and a design study's `StudyResult.Edits` all write through
that one seam precisely so a value cannot mean two things. A configuration joins them as a
consumer; it is never a second way to apply a value.

**Which pattern a switch wants was a deliberate call, and it follows `DesignStudy`'s.**
`StudyResult.Edits` writes through the seam internally rather than composing `DocumentEdits`,
because one edit per feature would rebuild once per feature. A configuration is the same
shape: `Activate` is one `LoadParameters` over the whole set plus ONE `Part.Regenerate`, and
`DocumentEdits.SetConfiguration` wraps THAT as a single undoable edit. Composing it out of
per-feature `SetParameter`s would be N rebuilds for one user action.

**It lives on `Part`, not on `FeatureHistory`**, which reads backwards until you ask what a
configuration is FOR. The history owns the parameter vocabulary and is where the entry's
"one `FeatureHistory`" points, but a configuration is only meaningful once something can
rebuild from it, and `Part.Regenerate` is the one call that swaps the fresh body in AND
clears every derived cache (mesh, B-Rep and SDF lowerings, feature edges, resolved
annotations, construction tree). A set on the history could load values and leave every
consumer looking at stale geometry.

**A configuration carries VALUES only — it cannot add, remove or suppress a feature.** Two
things follow. It is what makes the switch exact: the feature INSTANCES never change, so the
regeneration cache key (instance identity + parameter snapshot) is restored by restoring the
values. And per-configuration SUPPRESSION, which is the obvious next want (a variant without
the boss), is filed rather than smuggled in — suppression is not part of the `SaveParameters`
vocabulary, so it would arrive as a second field beside the parameter object with its own
capture, compare and round-trip rules, which is exactly the drift the one-seam rule exists to
prevent. It is a real absence and is named as one.

**The bit-identity claim needed a correction, and the correction is the finding.** The
verification the backlog asked for is that switching away and back regenerates bit-identical
geometry, "the cache-key property the undo stack already asserts". It does hold — but NOT
because the cached body comes back. `FeatureHistory`'s prefix cache holds ONE entry per
feature INDEX, overwritten every regeneration (the same property that makes memoizing a
design study's repeated points impossible), so on the way back the feature whose parameter
moved re-runs and returns a fresh, structurally identical `Shape`. What the cache buys is the
PREFIX — the plate above the change reports `Cached` on every switch, which is why a
configuration switch costs the tail of the history rather than all of it — and what makes the
geometry identical is the contract the cache is BUILT on: `Apply` is a pure function of its
parameters. So the test asserts both halves separately (a `Cached`/`Applied` outcome pair,
then every vertex compared through `DoubleToInt64Bits`) rather than an object identity that
would have been wrong.

**The active configuration is DOCUMENT state, and the undo stack's reasoning is what settles
it.** The stack is session state because it records HOW the document got here; the active
configuration records WHERE IT IS — it names the parameter values the model currently
carries, and those are saved with the history either way. Dropping the name would leave a
reloaded document whose values match "M6" exactly, unable to say so. Two consequences: the
name round-trips (which is also the test for whether an informational field belongs in a file
at all — the snapshot `"source"` rule), and the LOAD restores it WITHOUT re-applying. That
second half is not an optimization. A document may legitimately be saved MODIFIED (active
"M6", one parameter since edited), and re-applying at load would silently snap the model back
onto the configuration and discard the edit; restoring the name alone makes it come back
modified, which is what happened.

**Activating does not write back either**, and for the same family of reason: editing a
parameter while "M6" is active leaves the model modified against it rather than quietly
redefining it, so a configuration's values are a function of the document rather than of the
order in which someone clicked. `ActiveIsModified` is exact and needs no tolerance, because
both sides come from one serializer — the model's current values through `SaveParameters`,
the configuration's as stored — and it compares only the keys the configuration STATES, so a
partial set says nothing about what it omits.

**Staleness follows the `HistoryLoadResult` convention with one deliberate split.** A
configuration naming a feature that has been removed is a WARNING at the moment it matters
(`Activate` surfaces `LoadParameters`' own messages) plus a pre-flight (`Validate`) a UI can
show without applying anything — never a silent drop, and the configuration is KEPT, because
the feature may come back from an undone removal and a file that quietly loses a variant is
worse than one that reports it. The split is that the TYPED authoring overload
(`Add(name, (feature, parameter, value)…)`) refuses by name instead: its caller holds the
feature OBJECT, so a bad parameter name is a bug at the call site, where the JSON overload
names features by string and is in exactly `LoadParameters`' position.

**The BOM per configuration is a family table, and "per configuration" needed defining for a
document whose parts are shared.** A `Bom` groups by part REFERENCE and a configuration
changes a part's parameters rather than replacing the object, so the configured part is one
line in every row and its quantity never moves. What CAN differ is the rest of the model — a
`ComponentFeature` places catalogue hardware, so an M4 variant lists M4 screws and a
suppressed placement drops its occurrence — which is why `Bom.ByConfiguration` re-flattens
per row rather than reusing one captured instance list, and why it takes a `Scene`/`Tab`/
`Assembly` (or a `Func<Bom>`) rather than an instance list. Like a design study it is an
ANALYSIS: it restores the part's live values and active name in a `finally`.

One trap the shape of the API had to answer: **`BomLine.UnitMassGrams` is a LAZY projection
over the part's current geometry**, so a mass read off a returned row after the walk has
restored the part reports the restored configuration's mass for every row. Item names,
quantities and paths are captured at `Bom.For` time and are safe; the mass is not, so
`ConfigurationBom.TotalMassGrams` is measured INSIDE the loop and the caveat is stated on
both types rather than left as a footgun. That the BOM's own design note already calls mass
"the only part of a bill of materials that evaluates geometry" is precisely why it is the
only field that could go wrong here.

### Manufacturability checks: two fidelities, said out loud

`Manufacturability` (draft angle, overhang area, wall thickness) had one deliverable
shape to choose and three decisions inside it, and all four are about **honesty rather
than geometry** — the arithmetic in each leg is a dot product.

**The deliverable is a report PLUS a field, and the two come from different places on
purpose.** The *verdict* — does this part pass — is read from the most exact source the
part has: the B-Rep's own faces for draft (a plane has one normal, so its angle carries
no discretization at all), closed-form facet arithmetic for overhangs, a ray against the
display mesh for thickness. The *picture* is a `MeshField` over the display mesh, which
the existing `FieldDisplay` machinery colours with no new rendering code — the whole
point of results being data on a mesh. Reading the verdict off the mesh instead would
have been simpler and is wrong in the direction that matters: an inscribed facet is
steeper than the surface it approximates, so a 3° drafted CONE reads 2.92° at display
quality and a check with a 3° minimum would fail a part that is correct. A report that
cries wolf on correct output is worse than no report (`TetQuality`'s rule), so the two
sources are kept apart and each is labelled.

**A threshold is compared on the dot product, never on the derived angle.** `asin` is
monotone, so "the angle exceeds the threshold" and "the dot product exceeds the
threshold's sine" are the same statement — and they are not the same *computation*. A
wall built at exactly 45° reports a steepest angle of **45.000000000000007**, because
`asin` round-trips `1/sqrt(2)` an ulp high; a degrees comparison therefore reports a wall
drawn at exactly the stated self-supporting angle as an overhang, complete with 848.5 of
area. The sine form carries one fewer rounding and is the one the counts come from, with
degrees kept for humans. This is the `PolygonFan` tie guard's lesson in another costume —
**a predicate is only as meaningful as the arithmetic it is evaluated in** — and it is
pinned by a test asserting BOTH halves, so nobody can quietly rewrite the rule in
degrees.

**An unmeasurable point takes the conservative end of the scale, not NaN.** The thickness
ray can genuinely fail to find an opposing surface — a rib end, a boss over a
through-hole — and NaN is the obvious spelling for "not applicable". It is the wrong one:
`FieldRange` skips NaN when ranging, but a NaN still paints as the colour map's BOTTOM
stop, which on a thickness plot is the colour of the thinnest wall in the part. The
picture would show the exact defect the check exists to find, at a point where the check
declined to look. So those points carry the model's own diagonal ("at least this thick")
and are COUNTED in the report, where a number can be acted on. The general rule: **a
field a human looks at has no "not applicable" colour, so an absence must be spelled as a
value plus a count, never as a value alone.**

**The wall-thickness estimator was assessed before it was promised**, which the backlog
entry asked for and which changed what got built. Three candidates: a ray cast opposite
the normal, a medial-axis (largest inscribed ball) distance, and twice the interior
distance field. The last is not an estimator at all — the field is zero *on* the surface,
so it only becomes one by turning into the second. Between the other two the deciding
argument is not accuracy but **what each is exact ON**: the ray cast, corrected by
`|n · n_hit|`, is the perpendicular distance from the point to the *plane of the facet it
hit*, which is EXACT wherever the opposing surface is planar — plates, ribs, bosses,
webs, shelled prisms, i.e. the geometry a thickness check is run on — and it needs only
the display mesh, so it works on an imported `.stl` where the implicit lowering can fail.
The ball is the better answer at a fillet or an inside corner and is filed as a named
alternative rather than a silent upgrade, because two estimators answering one question
must both be nameable. What the shipped one does wrong is stated in its own API docs with
the direction of each error: under-reporting against a locally convex opposite,
over-reporting against a concave one, and — since every vertex of the whole surface is
probed — the conservative reading is the one the minimum keeps.

One measurement is worth keeping for its shape rather than its subject. A **cone's**
lateral area under an overhang threshold converges quadratically on `sqrt(2)·pi·r²`
(4.0e-3 / 1.0e-3 / 2.5e-4 / 6.3e-5, ratios 4.00) while a **sphere cap's** does not
converge at all. The difference is not the surface but where the region's BOUNDARY falls:
on the cone it is a model edge, so the whole face is in or out and nothing is quantized;
on the sphere it is a level set crossing the interior of a face, and a facet is
all-or-nothing, so the answer snaps to a facet band and the error is first order with a
sign set by where the cutoff happens to land. **An integral over a facet-classified
region converges like its boundary, not like its area** — so a closed form for such a
region is a sanity band, not a tolerance.

### Build-plate packing: rotation and outline nesting (`Packing.cs`)

v1 was a shelf packer over `Shape.Silhouette` BOUNDS. The two follow-ups the backlog
named — 90-degree rotation and nesting to the true outline — are both about giving the
packer more freedom, and the design question in each is *what the freedom is allowed to
cost*: `PackOptions`' default value is the v1 contract, and a default pack reproduces
the committed v1 placements bit for bit (asserted against hex fingerprints taken before
the change, the rule every optional feature here follows — `ShadingStyle.Lit = 0`,
`PreventLongEdgeFlips`, AMD ordering).

**A quarter turn is exact, and that is why it is the only rotation offered.**
`(x, y) → (−y, x)` is a sign swap, so a turned outline, its bounds and the matrix
`Apply` hands the `Shape` graph all agree to the last bit — the glTF Y-up-root rule
(`cos(-pi/2)` is 6.1e-17 and geometry should not carry that). `PackRotation.Free` is
**refused by name**: a continuous orientation has no finite candidate set, so the
search could be neither exhaustive nor deterministically tie-broken, and it wants a
no-fit polygon per part pair per angle or an optimiser with a stated stopping rule.
Sampling a handful of angles and calling it free rotation would be a search that is not
the one it claims to be.

**For box nesting a quarter turn is not a per-part decision.** It only TRANSPOSES the
footprint, so the four poses collapse to two — and which of the two is better depends on
the other parts, because a shelf is as deep as its deepest member. The classical rule
("orient everything landscape, so every row is as shallow as it can be") is right most
of the time and measurably wrong sometimes: 40 x 10 bars on a 50-wide plate fit side by
side upright and one-per-row landscape. So the packer runs the WHOLE plate under both
global preferences and keeps the shallower, tie-breaking on used width then on the
landscape preference — two packs, both cheap, and the rule is stated rather than
heuristic. It is still a heuristic in the large: a mixed-orientation assignment neither
preference reaches can exist, and the refusal says so rather than pretending.

**Outline nesting needed no new predicate, which is what made it tractable.** Each
silhouette is grown by HALF the gap through `Region2dOffset` — dilation by a disk — so
"two grown outlines are disjoint" IS "these two parts are at least `gap` apart",
symmetric and with nothing to keep in step. The grow is the expensive step (measured
9–200 ms on real silhouettes, against 0.7–2.4 ms for ONE exact region intersection), and
dilation COMMUTES with a rotation, so it runs once per part and the four poses are exact
turns of its result.

**The search is a conservative raster, and the cost measurement is what chose it.** An
exact overlap test through `Region2dBoolean.Intersection` costs 0.7–2.4 ms on the
regions these silhouettes produce (131–292 vertices after growing), so any search
visiting thousands of candidate positions is out of the question by three orders of
magnitude. Instead each grown outline is rasterized ONCE per pose into a bitmask and the
plate carries an occupancy mask, so a candidate placement is a shifted word-wise AND —
tens of nanoseconds, early-exiting on the first collision. Rasterization is
**conservative**: a cell is set if the grown outline touches it at all, so an empty AND
proves the regions are disjoint at any cell size and a coarse grid can only refuse a
legal placement, never accept an illegal one (the cross-plane hole validation's
sound-in-the-accept-direction rule). Interior cells are filled by even-odd parity at
cell CENTRES and boundary cells by walking each segment at half-cell steps; **the
soundness argument fixes the block size and it is 2x2, not 3x3** — a boundary point is
within a quarter cell of some sample, so it lies in `[s ± h/2]` on each axis, an
interval of width `h` spanning at most two cells. The obvious 3x3 block is sound too and
dilates every mask by a whole cell on each side, which measurably costs about one cell
of clearance per part — exactly what a tight fit runs out of.

**Three things the measurements said, all worth keeping.**

*Bottom-left-first nests only when the plate is tight.* With room to spare the lowest
free position is BESIDE the previous part rather than inside its concavity, so a roomy
plate reproduces row packing and outline nesting buys only its own quantization loss —
measured, six L brackets on a 140-wide plate came out 78.97 deep against the shelf
packer's 77.00. The same six on an 86-wide plate go 132.0 (box + quarter) to 108.9. So
the feature's own fixtures are tight plates, which is also when packing is worth doing.

*A finer raster is not monotonically better.* It refuses fewer placements, but it also
changes which placement the greedy search meets first: cell sizes 4 / 2 / 1 / 0.5 / 0.25
give depths 120.0 / 106.0 / 112.0 / 109.5 / 108.2 on one fixture. It is a cost/quality
knob, not a convergence parameter, and the docs say so.

*The quantization has a stated failure and it is a refusal.* Four 40 x 10 bars turned
upright span exactly 50 on a 50 mm plate — zero slack — and the raster cannot land them
however fine it is, because a mask's width always rounds up to a whole cell. Pinned by a
test asserting the box packer takes it and outline nesting refuses, rather than left to
be discovered.

**The oracle is an exact clearance check, not a picture.** Every placed outline is grown
by half the gap through the same `Region2dOffset` and every pair intersected through
`Region2dBoolean`; a non-empty intersection is a clearance violation. That is the
strong form (it would catch a part inside another's hole that is too close to the bore
wall, which a bounds test cannot see), and it is measured through the region machinery
rather than by inspecting a render. Beside it, `PackedArea`/`FootprintArea`/`UsedDepth`/
`Utilisation` are reported so two settings are comparable on one number that means
something — and the fixture is asserted to HAVE room to win (`PackedArea < 0.6 x
FootprintArea`) before the comparison is believed.

### The tamper mesh: the deliverable is a guarantee, not a pattern

`TamperMesh` draws the conductive serpentine an enclosure wall carries for anti-drill tamper
detection. Anyone can draw a squiggle; what makes it engineering is the number it ships with,
so every decision here is about what that number is allowed to rest on.

**The derivation, and why the bound is the answer rather than a bound.** Every cell of the
lattice has the route through its centre, so no point of the footprint is further from the
route than a cell's circumradius `R = ½·hypot(pitchX, pitchY)`. A drill of diameter `d`
centred at `c` misses the copper exactly when `dist(c, route) > d/2 + w/2`, which gives two
thresholds: the largest drill that CAN pass is `2R − w`, and the smallest that MUST cut a net
wherever it lands — its disc spanning the trace's full width at some point of the centreline —
is `2R + w`. Between them it is position-dependent, so the honest answer is a **band**, and
the design equation is the sever end, `pitch ≤ (d − w)/√2`. That is only a guarantee if the
bound is ATTAINED, which is a claim about the route rather than about the cell, so it is
**measured rather than assumed**. A dual-grid corner is only `h/2` from the route whenever one
of the (up to four) cell pairs meeting there is consecutive on it, and the full circumradius
when none is — the **blind corner**. Both cases exist: the footprint's own four corners touch
a single cell and always reach it, and blind INTERIOR corners appear from block order 3 and
multiply (0 / 1 / 9 / 47 at orders 2–5 on a plain Hilbert block, counted). Below order 3 the
interior worst case really is `h/2` and only the boundary reaches `√2`; the guarantee is the
same either way, and knowing which is which is what `WeakestPoint` is for.

**The measurement is certified, not sampled.** `DrillGuarantee` comes from a branch and bound
over the footprint: distance to a polyline is 1-Lipschitz, so a cell whose centre reads `d`
can hold nothing above `d + halfDiagonal` — the argument `SurfaceCull` already stands on — and
`Uncertainty` is the bracket rather than a hope. It is held against the closed form by test
AND against an independent dense scan, which is what stops a plausible formula shipping wrong.

**The route is a TILING of Hilbert blocks, which is what makes it cover a wall.** A block
enters and leaves at two adjacent corners of its own square, so under the eight symmetries of
the square it can be asked for whichever entry and exit its neighbours need, and a
boustrophedon over the block grid links them into one Hamiltonian path over a rectangle —
the "tiled Hilbert blocks with their ends linked" the infill residuals name, and the reason
the achieved pitch lands near the request instead of the next power of two. `1 × 1` is Core's
own Hilbert lattice site for site, which is the reduction that makes it a generalisation
rather than a second curve. The block order is then a **stated trade**: small blocks fit an
arbitrary rectangle tightly and keep the cells nearly square, large blocks are more isotropic
and quantise the fit coarsely (reported as `Anisotropy`, and the guarantee follows
`hypot(pitchX, pitchY)` rather than `√2·pitch` once cells stop being square).

**Hilbert's locality is a liability here, and the design says so.** The open curve is right
because a continuity monitor needs two terminals — Moore's closed loop would have to be cut —
and both ends land on the footprint's outer boundary where a connector wants them. But points
near in space are near in path order, so an attacker who exposes a small window can bridge
across a break with a short wire, and **no choice of curve fixes that**. The countermeasure is
two or more interleaved nets watched for continuity AND mutual isolation, built as the same
route offset evenly across each corridor so the interleaving is structural and every gap
between neighbouring conductors is one number (`IsolationGap`). They are deliberately NOT sold
as independent detectors: they run parallel half a pitch apart, so a drill that cuts one cuts
the other. What they buy is the SHORT — any conductive bridge wider than the gap joins them.
A symmetric offset set (`(k + ½)/N − ½` of a cell) was chosen over one that keeps a net on the
route: it centres the pattern on the footprint, and the measurement says it also narrows the
gap rather than widening it (0.78 / 0.73 / 0.71 of the single-net gap at 2 / 3 / 4 nets), so
the bound that the on-route spelling would have preserved by construction is not needed.

**What the curve actually buys is a CHANNEL, and that is the honest comparison.** A plain
serpentine at the same achieved pitch measures the SAME circumradius — the bound depends only
on the cell and on every cell being visited — so the drill guarantee is not the reason. The
difference is that a serpentine's free space contains a straight channel the width of the
wall, where a tiled Hilbert route's longest straight run is 4 cells at every block order above
zero. Block order 0 IS the serpentine (every block one cell), which is what makes the
comparison a member of the family rather than a strawman.

**The copper is built, not booleaned.** `TamperNet.Outline` is one simple polygon from the two
±w/2 mitered offsets, because `Region2dOffset.Stroke` unions a slab per segment and is `O(E²)`
in the arrangement — minutes of work at mesh scale for a shape with a closed form. The oracle
is an identity rather than a tolerance: a mitered ribbon's area is exactly `length × width`,
since at every corner the outer miter triangle is congruent to the inner notch, and that only
holds if the ribbon does not overlap itself — so the area check IS the simplicity check.

**Scope, refused rather than approximated.** A wall that is not a rectangle breaks the route
into runs, and a broken net cannot be monitored at all, so it is refused by name and pointed
at `SpaceFillingInfill`, which reports runs honestly. Conformal placement on a doubly-curved
wall is not offered: `MeshLocalParam`'s exp map carries 2–5% distortion, which would land
directly in the pitch and therefore in the guarantee, and a guarantee derived from a distorted
pitch is not one. (That refusal is unchanged by `SurfaceDecoration` below, and the contrast is
the point: a decoration REPORTS its distortion and lets a caller size a bead around it, where a
tamper mesh's whole deliverable is a bound, and a bound built on a distorted pitch is not a
bound.)

### Filling a volume, decorating a surface, and one linker

The two remaining space-filling consumers, and the seam they share.

**`SolidInfill` is the volume fill**, and it rides the 2D consumer's seams rather than inventing
its own: the clip is a COMPARISON against an exact signed distance (an `Sdf` is sign-exact, so
"is this point at least `clearance` inside" needs no tolerance), and everything reported is
measured. What is genuinely new in 3D is the PLACEMENT question and it is stated rather than
solved — the footprint is the body's bounding CUBE, so a long thin part wastes the curve as a
long thin plate wastes the 2D one, and `Waste` reports it as a number (under 50% on a cube, over
85% on a 20 × 4 × 4 bar) so the tiled 3D footprint stays a decision with evidence rather than a
guess. The two silent misses are refused by name here too, with the INSTRUMENT stated: there is
no 3D counterpart to `Region2dOffset`, so "is there room at all" is answered by a probe grid at
half the achieved spacing, and a solid too thin for the clearance and one the lattice's phase
stepped over get different messages because they have different fixes. The per-LAYER alternative
is a documented recipe rather than a wrapper (`Shape.Section` then `SpaceFillingInfill.Fill`),
because one path per layer and one path through the part are different deliverables.

**`SurfaceDecoration` reports the map's distortion instead of averaging it away**, which is the
whole design. `MeshLocalParam`'s exp map is exact on a plane, near-exact on a developable surface
and genuinely distorted where Gaussian curvature concentrates, so a conforming curve carries that
into its own SPACING — the number a bead width is chosen from. What makes the report honest is
that the EXTREMES are the answer and the mean rides beside them: measured on a 20-radius sphere
cap, `MinScale` 0.9441, `MaxScale` 1.0014, `MeanScale` 0.9870 — a mean departure of 1.2% against
a worst pass 7.4% tighter than drawn, so a mean-only report would call the map faithful. (The
same curve on a developable tube measures 3.5e-4, which is the entire content of the word
"developable".) The measurement is taken on the curve that was actually laid rather than quoted
from the map's own published figures, and a flat point past the map BREAKS the run and is counted
rather than extrapolated, because continuing it would mean inventing surface. The inverse of the
map — which `MeshLocalParam` gives per VERTEX and a decoration needs per POINT — is a BVH over
the triangles in (u, v) plus barycentric interpolation, legal because a triangle's own map is
affine both ways.

**One linker, not three** (`RunLinker`). A clipped 2D infill, a clipped solid infill and a
decoration broken at the map's edge all leave the same artefact — a set of runs whose travel is
the caller's business — so there is one deterministic greedy nearest-endpoint linker over
(start, end) pairs, dimension-agnostic, with `PathLinkage.Reorder<T>` applying the order
generically. Three properties carry it: it is a PERMUTATION by construction, so it can shorten
the travel and cannot lose a pass; it is deterministic (ties break on the lower run index, then
on not-reversed) because a toolpath has to be reproducible, which is also why no randomised
improvement is offered; and it is a HEURISTIC that says so, reporting `TravelLength` beside
`SourceOrderTravelLength` rather than claiming an optimum, since ordering runs to minimise travel
is the open travelling-salesman problem. **The measured finding is why greedy is right here**: on
a space-filling fill the linker reverses NOTHING, because the curve order already leaves each run
pointing at its successor — the incumbent order is a good tour and the linker's job is picking up
the ends it left behind. The reversal capability is therefore pinned on a hand-built case rather
than expected from a fill.

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
- **One `Compute()`, three writers.** The SVG, DXF and PDF writers consume the same
  `SheetContent` and differ only in spelling. The DXF side carries one rule worth
  stating: a file that NAMES a line type its layers use must also DEFINE it, or every
  reader falls back to solid lines and the visible/hidden classification — the entire
  point of the exercise — is silently lost in transit.
- **The PDF writer is built for the byte fixed point, and every choice follows from
  it.** A drawing revision should diff like its model, so the file is uncompressed
  ASCII (legal PDF; the `BrepArchive` text argument — Flate would buy kilobytes and
  cost the inspectability every assertion here rests on) and deliberately carries **no
  /Info dictionary and no /ID**: both are optional per the spec, and their natural
  values — a CreationDate, an MD5 salted with the clock — are precisely the fields
  that would make two writes of one sheet differ. Three other decisions carry
  reasons. **(a) No y-flip exists**, because PDF user space has its origin at the
  bottom-left with y up — the sheet's own convention — so the SVG writer's whole
  flip apparatus (text-outside-the-flip included) simply does not apply; the one
  transform in the file is a single `cm` mapping millimetres to points
  (`PdfDrawing.PointsPerMillimetre`, the ONE 72/25.4 constant), which keeps every
  coordinate in the content stream the model's own millimetre value verbatim.
  A transform you do NOT need is worth recording. **(b) Text is the standard-14
  Helvetica over WinAnsi, not the stroke font** — the stroke font lives in
  EngrCAD.Viewer.Core, which Modeling cannot reference, and duplicating its glyph
  table would be the two-copies drift `SheetStyle`'s ratio convention exists to
  avoid; the SVG sheet writer's real precedent is system-Helvetica `<text>`
  elements, and PDF's standard 14 are the same idea with a spec guarantee (every
  conforming reader carries them, so naming /Helvetica satisfies the
  name-it-define-it rule without embedding). Anchoring needs advance widths, which
  PDF delegates to no one — they are transcribed from the Adobe Helvetica AFM,
  flagged verify-against-datasheet. **(c) Characters outside WinAnsi are REFUSED by
  name, with one deliberate substitution**: a silent `?` in a dimension is a wrong
  drawing (the descriptor sanitization rule), but the drafting diameter sign U+2300 —
  which the dimension layer itself emits — travels as O-stroke (U+00D8), its
  standard typographic stand-in, documented and pinned by test. The verification is
  the OFF/LZW twin-decoder pattern: an independently written parser in the test
  suite walks the xref (verifying every offset points at its object), follows the
  object graph to the page, tokenizes the content stream, and asserts every
  polyline's coordinates round-trip BIT-identically ("R" is a bijection on finite
  doubles; the one formatting caveat is that PDF's number grammar has no exponent
  form, so sub-1e-4 magnitudes take fixed notation — a grammar constraint, not a
  tolerance). Poppler's `pdftotext` independently recovers every text run.
  Deliberately absent: `Add(Sketch)` (PDF paths are lines + cubics, so a circular
  arc has no exact form and an overload would silently flatten), layers (PDF needs
  optional-content groups for those; filed), and a CLI route (sheets are produced by
  code and docs fences, not by `--export`, for SVG and DXF alike).
- **One frame, shared by the mechanical AND the schematic sheet, extracted ADDITIVELY.**
  The paper, the border and the title block used to be carried twice — once by the
  mechanical `DrawingSheet` and once, deliberately re-implemented, by the ECAD
  `SchematicSheet` — so a drawing and a schematic of one project could look inconsistent
  and could drift. They are now ONE value type, `DrawingFrame` (`DrawingFrame.cs`), a
  pure function of its parameters: `Compute()` returns the border and title-block
  geometry, and two sheets given the same paper, the same `TitleBlock` fields and the same
  frame options produce byte-identical furniture because they call one function. That is
  the oracle the filing asked for — *the two sheets provably cannot disagree because it is
  one function* — and it is checked directly (the same frame options handed to a
  mechanical sheet and a schematic sheet give identical geometry). **The extraction is
  ADDITIVE, and byte-identity is the other oracle**: the two title blocks differ *today*
  (the mechanical `EngineeringTitleBlock` is a three-band layout on `SheetLayers`; the
  schematic `SchematicTitleBlock` a two-band one on the ECAD schematic layers), so the
  frame carries BOTH parameterisations — a `TitleBlockLayout` strategy plus the border and
  title-block layer names — and each sheet passes the ones that reproduce its OWN look,
  which was verified by hashing every mechanical and schematic SVG/DXF/PDF before and after
  and finding them identical. **The strategy (two subclasses) was chosen over a
  declarative grid** precisely because the byte-identity bar is unforgiving: transcribing
  each incumbent's own arithmetic verbatim is the safe way to reproduce it to the last bit,
  where a single declarative model powerful enough to spell both would be a large surface
  for a floating-point drift. **The schematic keeps its own BODY, deliberately** — the
  frame is the paper's furniture, and a schematic's line work is caller-placed while a
  mechanical sheet's is projected views, so unifying the bodies is not what "share the
  frame" means. Two smaller things landed with it, both additive: `SheetFormat` gained the
  ISO 216 B series and the ANSI/ASME Y14.1 A–E sizes (`SheetFormat.All` is the one table
  the frame reads), and `FrameStandards` is the opt-in ISO 5457 border — a zone grid
  (column numbers, row letters, I and O omitted) and centring marks drawn in the margin
  band so they never touch the drawing area, **OFF by default** (`FrameStandards.None`) so
  nothing existing moves. The ISO 7200 field layout is filed (a full new layout wants its
  datasheet); the zone COUNTS here come from a nominal field size rather than ISO 5457's
  exact per-size table.

## 6d. ECAD — schematics and the connectivity data model (`EngrCAD.Ecad`)

The first stage of the ECAD campaign (schematic → board → placement constraints → copper
DRC → routing → MID/LDS 3D routing; the later stages are filed in `todo.md`). Stage 1 is
**connectivity only** — the graph and its exact verification — and it deliberately builds
nothing geometric. `EngrCAD.Ecad` is kernel-tier: it references Core (math) and Modeling
(the optional `Func<Shape>` body hook) and nothing that touches a viewport.

### The one load-bearing decision: the object graph IS the netlist

A netlist is a graph — components, pins, nets — and the failure mode of every ECAD/MCAD
bridge is two models drifting: a net the copper does not connect, a part the schematic does
not place. So there is **one source and everything derives from it**. A `Schematic` holds a
`List<Component>` and a `List<Net>`, and those ARE the connectivity; there is no second
editable netlist to keep in step, and a `Netlist` view (`pin → net`, `net → pins`) is a
DERIVED, read-only projection computed FRESH by `ToNetlist()` — a method, not a cached
property, precisely so there is no stored copy that could go stale. This is the same "the
declaration is the model" doctrine `SheetMetalBody` and `FeatureHistory` already enforce,
and it is the decision the whole campaign turns on: a DRC or a router that disagrees with
the schematic is then a bug in one derivation, not an unresolvable difference between two
hand-kept files. The API is fluent and code-first — `Schematic.Add` / `Connect` / `Stub` /
`NoConnect` — exactly as `Sketch` declares curves and `Scene` declares parts.

### The types, and where the value/identity split falls

A `Pin` is a value (number + optional functional name + `PinType`) describing a terminal of
a part TYPE. A `PartDefinition` (a type: name, prefix, ordered pins, an optional
`Footprint` data placeholder, an optional `Func<Shape>` body) is instanced by many
`Component`s (a placed instance: reference designator + value). The thing a `Net` connects
is a `PinRef` — a `(Component, pin number)` pair, a REFERENCE into the component graph and
not a copy, which is what the persistence layer must reproduce (and does, by resolving a
saved `(refdes, number)` back to the same `Component` object). A `Net` has a `NetKind` —
`Signal`, `Stub`, or `NoConnect` — and **NoConnect is a first-class state, never a null**:
a pin is covered iff it is on a signal net, a stub, or a no-connect, and the kind is what
makes the floating check meaningful (a lone `Signal` pin is a mistake, a `Stub` or
`NoConnect` pin is not).

**Net names are globally unique across kinds**, and that was a real finding rather than an
obvious choice: the first cut kept one name map for signal/stub nets and left NoConnect
nets unnamed in that map, so a signal net could quietly shadow a no-connect name. One lone
name map, checked in `Connect`/`Stub`/`NoConnect` (and the auto `NCn` names skip any a user
already took), is the fix — the same "one shared rule only holds if every caller asks it"
lesson. `Connect` create-or-extends the signal net of its name (so a rail is declared
incrementally), but refuses to shadow a non-signal net of that name.

### Verification is combinatorial and exact, and every guard is shown to fire

`Schematic.Check` is the DRC of connectivity, and every list NAMES its offenders (a check
that only said "invalid" would be useless). **The counting identity** is the spine:
`TotalPins == PinsCoveredOnce` with no over-assignments means every terminal of every
component is on exactly one net. The two counts are exposed so the identity can be asserted
NUMERICALLY, and the two lists it splits into name which way it failed — `UnassignedPins`
(a floating pin) and `MultiplyAssignedPins` (a short across two nets, or a pin both wired
and marked no-connect). Beside it: no `Signal` net with fewer than two terminals
(`FloatingNets`, stub/no-connect exempt by kind), and no empty net. The tests drive a
floating pin, a short, a lone signal net and an empty net and assert each produces a
non-`Ok` report naming the offender — the guard-must-fire rule.

### Persistence — the document model's seam, and a byte fixed point

`Save`/`Load` follow the `Document`/`SaveParameters` conventions verbatim: a JSON tree
serialized with `WriteIndented` (Modeling's own `FeatureHistory.JsonOptions` is internal
and unreachable across the assembly boundary, so `EcadJson.Options` replicates the
convention rather than being a second one), **write-only-when-stated** optional fields
(a component's value, a net's kind when it is not the default `Signal`, a pin's functional
name, a footprint), and **no informational field that cannot round-trip**. A
`PartDefinition` used by many components is written ONCE and shared by identity — one
definition record the components reference by a deterministic id (`d0`, `d1`, … assigned in
component declaration order, the same interning `Scene.AllParts`/`DocumentWriter` use for
parts) — so a net referencing a pin stays a reference and not a copy. The verification bar
is the strong one: `save → load → save` is a **byte-identical fixed point** (which catches a
field written but never read, a default that reloads as a different default, or an ordering
that is not a function of the model), and two loads of one file produce structurally
identical graphs.

Two things do not travel and each is handled by name rather than by drift. The `Body` is
code (a lambda over the modelling API), so it is NOT serialized; a `PartLibrary` re-attaches
it by definition name on load — the `ResolveOpaqueFeature` pattern — and a data-only load is
honest and complete for connectivity, so it warrants no warning. And a structural
inconsistency is refused BY NAME at load: a component whose definition the file does not
contain (the `HoleSpec`/catalogue rule — the definition is the source, so an instance
without one is not loadable), a net naming a missing component or pin, an unknown format or
version. These throw (a malformed file), where the document model's soft-degrade-with-a-
warning path has no analogue here because a schematic's connectivity carries no opaque
records — the one opaque thing, the body, is optional and simply absent.

### Component interchange — the symbol, and loading from KiCad (`Symbol`, `ComponentLibrary`)

A `PartDefinition` used to carry two views of a part — its pins and its footprint — and a
schematic was pins wired into nets. The piece a component was MISSING is its 2D schematic
**`Symbol`**: the drawn shape a schematic sheet places, wires to and labels. A `Symbol` is
graphic primitives (`SymbolPolyline`/`SymbolRectangle`/`SymbolCircle`/`SymbolArc`/`SymbolText`)
plus one **`SymbolPin`** per terminal, and the load-bearing property of a `SymbolPin` is its
**`Anchor`** — the connection point where a wire lands — plus a `SymbolPinDirection` (which way
the pin points from that anchor into the body, the KiCad angle convention) and a length, so the
filed "schematic drawing output" consumes a symbol whose pins already say where a wire attaches.
`PartDefinition` gains the `Symbol` as an OPTIONAL last constructor parameter, so every existing
positional construction is byte-for-byte unchanged and a symbol-less definition is unaffected.

**The three representations are ONE identity by pin NUMBER** — symbol pin `"1"` == footprint pad
`"1"` == netlist pin `"1"` — and that is the whole point of loading a symbol and a footprint
together. `PinIdentity.Check` verifies it, anchored on the definition's `Pin` list as the
authoritative terminal set: it names every declared pin with no symbol pin or no pad, every
symbol pin that is not a pin, and every pad that is not a pin, so all four lists empty is a proof
that the three number-sets are equal. Representations that are ABSENT (no symbol, or no footprint
— connectivity needs neither) are simply not cross-checked; the check is only as strong as what
the part carries. It is the schematic's one-declaration source-of-truth rule extended to the
drawn symbol.

**Loading is the real use, and KiCad is the interchange** (`ComponentLibrary.Load`/`Read`,
`KiCadSymbolReader`, `KiCadFootprintReader`). KiCad's `.kicad_sym`/`.kicad_mod` are the primary
open ubiquitous library formats, S-expression text, so the readers are a hand-rolled
dependency-free S-expression parser (`SExpr`) in the `StepReader`/`IgesReader` ethos: structure
validated up front (unbalanced parenthesis, unterminated string, top-level atom, wrong root tag,
absent named symbol — each refused BY NAME), the **common subset** mapped, and everything else
ignored or approximated **with a named diagnostic** rather than mis-read silently (a bezier
graphic, an alternate pin function, a `no_connect`/`free` electrical type with no exact
`PinType`, a pad rotation, a `trapezoid`/`custom` pad shape, an oval drill). A pin's electrical
type maps to the SAME `PinType` the netlist uses (so the symbol's electrical type IS the pin's),
a pin's `at x y angle` gives the anchor and the direction, and — because a KiCad pad's
coordinates are STATED in the file — pad centres and sizes are carried EXACTLY, not to a
tolerance. `ComponentLibrary.LoadFromPretty` resolves the `.kicad_mod` from a `.pretty` directory
by the symbol's referenced footprint name, and a `LoadedPart` carries the assembled
`PartDefinition`, its `PinIdentityReport` and the readers' diagnostics.

**No change to `Footprint`/`Pad`/`PadShape` was needed**, which is the finding: the drill diameter
a through-hole pad wants was already added additively in stage 2, and KiCad's `circle`/`rect`/
`roundrect`/`oval` map onto the existing `PadShape`. So the board side that READS footprints
compiles and behaves unchanged, and the stage-1 SMD footprint round-trips byte-identically — the
KiCad loader adds a reading path, not a data-model change. A pad is constructed via the record
constructor rather than the validating `Pad.ThroughHole` factory, so a malformed pad IMPORTS and
the DRC reports it rather than the reader throwing on dirty interchange (the "readers never throw
on dirty geometry" culture).

**Persistence extends the seam.** A loaded `Symbol` is DATA now, so `SchematicFile` serializes it
write-only-when-stated — graphic primitives by `kind`, pins with number/name/anchor/direction/
length/type — and the writer's default arm THROWS on an unknown graphic kind (the
Feature-persistence rule: a kind the reader learns and the writer does not takes the document
down rather than degrading one symbol). A `PartDefinition` carrying a symbol is a `save → load →
save` byte-identical fixed point, and a symbol-less definition writes no `"symbol"` key at all, so
every stage-1..5 schematic file is byte-identical to what it always was. The verification bar is
the interchange one (higher, since interchange fails plausibly): a transcribed real 0805 resistor
and SOIC-8 parse with the pin count/names/numbers round-tripping, the symbol pins and footprint
pads sharing the numbers, a deliberately mismatched footprint REPORTED by number, pad geometry
matched EXACTLY to the file, the symbol's primitives and pin anchors matched, malformed input
refused by name, the persistence fixed point, and determinism. What stays filed: IPC-7351 footprint
GENERATION from a designation (a generator, not an import) and EDIF.

### Component interchange — the 3D model, the third view (`ComponentModel3D`, `ModelPlacement`)

The trinity's third view is the **3D model**, and the change that makes it a first-class peer of
the symbol and footprint — rather than the bare `Func<Shape>` the legacy `Body` was — is
`ComponentModel3D`: a body SOURCE unified with a `ModelPlacement` relative to the footprint origin,
the KiCad `(model …)` shape. `PartDefinition` gains `Model` as an OPTIONAL last constructor
parameter (so every positional construction is byte-for-byte unchanged), and the legacy `Body` stays
as the spelling of a **code model with the identity placement** — the two seat bit-identically, and
the seating resolves `Model ?? (Body as identity-placed model)`.

**Two source kinds, and the split is the design.** A **file** reference — `.stl`/`.obj`/`.off`/
`.wrl` via `Shape.From`, `.step` via `StepReader` — travels through the schematic/board file as DATA
(the path plus the placement) and loads on demand; a **code** model (a `Func<Shape>`) stays OPAQUE
and is re-attached from a `PartLibrary` by definition name, exactly as `Body` is. Constructing a
model never touches the filesystem, so a data-only load that only references a path is honest and
complete for persistence and connectivity — loading is an explicit act (`TryLoad(out error)` soft,
`Load()` hard), and a missing/unreadable file or an `.igs`/`.iges` (a face soup needing
`ShapeHealing`, filed) is RECORDED but refused BY NAME, never a data-load crash (the "readers never
throw on dirty geometry" culture). An unloadable model leaves the assembly without a 3D occurrence
— the pads are still placed — exactly as a body-less component does. **A `.wrl` (VRML, KiCad's
default 3D model format) LOADS now** — `VrmlReader` in EngrCAD.Mesh reads the VRML97 mesh subset
(every `IndexedFaceSet` through the `Transform`/`Group` hierarchy, `DEF`/`USE` instancing, `Switch`
by `whichChoice`, `LOD` at its most detailed level; the winding rule is one XOR, ccw against the
transform's mirror determinant, so a clockwise set and a mirrored instance both come back OUTWARD;
appearance/normals/colours ignored, a non-mesh geometry and an external `Inline` skipped with a
NAMED note; a missing/V1.0 header, `PROTO` and a truncated file refused by name), and the reader
reads coordinates VERBATIM because VRML is unitless — **the KiCad convention (1 VRML unit = 0.1
inch = 2.54 mm) is applied at the ECAD consumer** (`ComponentModel3D.TryLoad` scales a `.wrl` body
by 2.54), the format/convention split that keeps the mesh-tier reader honest for a non-KiCad file.
The oracles are geometric — a unit cube's exact closed volume through every code path (the
transform stack including `center`, instancing, both halves of the winding XOR) — because a
scene-graph reader's classic failure is a plausible mesh under the wrong transform.

**The placement seats into the pose, and a quarter turn is exact.** `PcbLayout.ToAssembly` bakes the
`ModelPlacement` (translate · rotate · scale) into the body BEFORE the side reflection and the
placement pose, so it is applied in the footprint's own frame and a bottom-side component's model is
reflected along with its footprint — verified as a closed form: on the bottom, a model offset
(dx, dy, dz) moves the seated body by (dx, dy, −dz), the placement applied before the reflection.
An IDENTITY placement applies **no transform at all** (which is what keeps the legacy body path
bit-identical — the seated body mesh is bit-for-bit the raw body's), and the struct default IS the
identity: a zero scale component reads as unit, since no scale collapses geometry so a stored zero
can only mean "unspecified". A rotation that is a multiple of 90° is built from sign swaps
((x, y) → (−y, x)), never a `cos` — so a 90° rotate about Z TRANSPOSES the footprint-plane bounds
to the last bit (the packing/glTF exact-quarter-turn rule), the offset shifts the seated bounds by
exactly that offset, and a scale scales them by exactly the factor.

**Persistence and KiCad follow the seams.** A file-referenced model round-trips as
`{ path, offset?, rotate?, scale? }` (write-only-when-stated) — a `save → load → save` byte-identical
fixed point through the schematic AND the embedded board file — while a code model (opaque) and a
model-less definition write no `"model"` key, so a pre-model file is byte-identical. The stored
offset/rotate/scale ride VERBATIM (the `AxisRef` never-re-derive rule). On the KiCad side, the
footprint reader (`KiCadFootprintReader`, and the whole-board `KiCadPcbReader`) turns the filed
`(model …)` reference into a `FromFile` model carrying the path plus KiCad's placement — offset in
mm (a legacy inch `at` is converted with a note), rotate in degrees, scale unitless — so a
`ComponentLibrary.Load` part arrives with its 3D model REFERENCE, not force-loaded (an empty library
directory is normal). What stays filed on the model side: IGES (`.igs`)
3D-model loading, and Eagle 3D package models (Eagle's `<packages3d>` reference a model by URN —
materially more than the classic `.lbr` carries); the VRML (`.wrl`) reader landed (above).

**The Eagle `.lbr` reader is the SECOND interchange, and its structure — not effort — is the
finding** (`EagleLibraryReader`, `EagleLibrary`; the KiCad reader's twin). An Eagle library is one
XML file, so it rides the BCL's `XDocument` (dependency-free, the `ThreeMfWriter`/`AmfWriter`
precedent for XML formats) rather than a hand-rolled parser, and it produces the SAME `LoadedPart`.
**The `<connect gate pin pad>` map is what unifies the three, where KiCad's file numbers the pins
directly.** An Eagle symbol's pins are named in the symbol's own vocabulary (`"1"`, `"VCC"`), a
package's pads are numbered, and a **deviceset**'s `<connect>`s bind them (`"VCC"` → pad `"8"`) — so
the loaded pin's NUMBER is the pad, its NAME is the symbol pin's name, and its symbol pin, footprint
pad and netlist pin all carry that pad number, which is exactly what `PinIdentity.Check` verifies.
`Read(xml)` returns the library's `Devices` (each named by its deviceset + device); `Load(deviceName)`
resolves one. Eagle stores coordinates in the XML in MILLIMETRES, so pad centres and pin anchors are
carried EXACTLY, and a pin's `rot` gives its direction (`R0` points +x into the body, confirmed
against a real `rcl.lbr` R-EU resistor whose left pin is `R0` and right pin `R180`) and its `length`
token its length (short/middle/long = 0.1/0.2/0.3 inch). **The deliberately-inconsistent fixture is
REPORTED, not refused, and comes from the PACKAGE**: the footprint is built from the package's own
pads while the pins come from the connects, so a device whose connects name pad `"8"` while the
package defines only `"1".."7"` and a stray `"99"` surfaces as a `PinIdentity` mismatch by number —
the KiCad missing-pad-8 fixture's exact analogue. What IS refused by name (structural, not a report):
a multi-gate deviceset (a gate array), a symbol pin with no `<connect>` (an unmapped pin — an Eagle
symbol pin has only a name, so with no connect it has no number and cannot become a pin), malformed
XML, a file whose root is not `<eagle>`, and a `.brd`/`.sch` rather than a `<library>`. **No additive
change to `Symbol`/`Footprint`/`PartDefinition`/`PinIdentity` was needed** — the Eagle primitives all
mapped onto the existing vocabulary (the same finding KiCad reported for `Footprint`/`Pad`) — so the
KiCad path is BIT-IDENTICAL by construction, nothing shared having moved. **Whole Eagle `.sch` import
landed** (`EagleSchematicReader.Read`/`ReadFile` → `EagleSchematic`), and the structural finding is
that it is the KiCad importer's OPPOSITE: where a `.kicad_sch` only DRAWS its netlist (connectivity
reconstructed from wire geometry by union-find), an Eagle schematic DECLARES it — every `<net>` lists
its `<pinref part gate pin>` terminals — so the import is a RESOLUTION, not a reconstruction, and the
wire geometry is never consulted. Parts resolve through the schematic's own embedded `<libraries>`
(each the same content as a `.lbr`'s, read by the shared `EagleLibraryReader.ReadLibraryElement` —
the pin/pad/symbol unification verbatim, definitions interned per (library, deviceset, device)); a
part whose device cannot be assembled (typically a SUPPLY symbol, whose deviceset has no connects) is
reported and skipped, its nets surviving because an Eagle net carries its OWN name. **A pinref names
the SYMBOL PIN, resolved by NAME first** — the discriminating case being `pin="VCC"` landing on pad
"8" (the .lbr connect map made the pad our pin NUMBER and the symbol pin's name our pin NAME), which
a number-blind resolver cannot do — falling back to the number for symbols whose names are the pads.
Nets group by NAME across every sheet and segment (Eagle nets are global to the schematic); one
resolvable pin is a `Stub`, a pin claimed twice keeps its first net with a report, and unloadable
parts / unknown pinrefs / netless nets are all reported never thrown. Refused by name: malformed XML,
a non-`<eagle>` root, and a `.lbr`/`.brd` handed here (with the `.lbr` reader signposting back — the
two-way redirect pinned by test). **Whole Eagle `.brd` import landed too**
(`EagleBoardReader.Read`/`ReadFile` → `EagleBoard`), the board twin of the same structural fact: a
`<signal>` DECLARES its terminals (`<contactref element pad>`), so the synthesized schematic is the
file's own intent — and that is what makes the import CHECKABLE rather than hopeful, the strong
oracle being `PcbConnectivity` confirming that the imported copper (layer-1/16 wires as traces,
`<via>`s as through-vias) actually JOINS the declared pads, which a wrong placement transform, a
wrong side or a wrong via would each break. Elements reference PACKAGES directly (a board has no
deviceset), resolved through the embedded `<libraries>` via the shared `ReadLibraryElement`, each
(library, package) pair interned as one data-only `PartDefinition` whose pins are the pad names —
the `KiCadPcbReader` pattern verbatim; the outline is the layer-20 `<plain>` wires CHAINED end to
end (arriving in any order and either direction; an unclosed chain refuses by name, since a board
needs a closed outline to build); a rotation `MR…` is MIRRORED and lands the element on the BOTTOM
side with the angle carried as stated; an absent via diameter takes Eagle's own auto-restring rule
(pad = drill + 2·max(25% drill, 0.254 mm), a ⚠ transcribed nominal). **A signal `<polygon>` becomes
a `CopperPour`** — `isolate` is the pour clearance, `orphans="on"` keeps dead copper,
`thermals="off"` direct-connects pads, and Eagle's RANK (1 = highest priority) maps to
`Priority = 6 − rank` (⚠ nominal) — with EngrCAD deriving the fill exactly as the KiCad zone import
does, and the oracle is the plane's own purpose: a GND polygon whose net carries NO trace joins its
pads through the pour alone, with the polygon-less twin the mutation that proves it (GND then reads
as an unrouted ratsnest). Airwires (layer 19 — the
ratsnest, intent not copper, which the contactrefs already carry), inner-layer wires and curved
wires are reported and skipped/flattened BY NAME — the covered copper
subset is the two-layer board — the thickness is assumed 1.6 mm with a note (a `.brd` keeps it in
the fab profile), and a signal with copper but no resolvable terminal has its copper skipped with a
note rather than thrown three calls later by the layout's own unknown-net gate. The DRC on an
imported board runs at the KiCad-import convention (acute floor 45°, since a thin trace entering a
pad makes near-90° junctions). All three Eagle readers signpost each other at the root, so a user
holding any Eagle file is pointed at the right door (pinned by test in every direction). Still
filed: Eagle 3D package models and the newer Eagle/Fusion XML variants beyond the classic schema.
Docs `examples/ecad-library.md`. **IPC-7351 footprint GENERATION landed as the importers'
complement** (`Ipc7351` + `StandardBodies`; docs `examples/ecad-library.md`): a land pattern from
the component's OWN datasheet dimensions rather than a library file — one formula family for every
leaded shape (`Zmax = Lmin + 2·J_toe + √(C_L² + F² + P²)`, `Gmin = Smax − 2·J_heel − √(C_S² + F² +
P²)` over the arithmetic heel-span range — the conservative reading toward fillet, documented as a
choice — and `Xmax = Wmin + 2·J_side + rms`), the toe/heel/side FILLET GOALS per `LandDensity`
being the ⚠ verify-against-datasheet transcription (nominal IPC-7351B figures, the `StandardHoles`
convention). **The verification is what a transcription cannot protect on its own**: with every
tolerance zero the formulas reduce to the bare goals EXACTLY (the check that catches a swapped
min/max), density and tolerance move Z/G in known monotone directions (a wider length band grows Z
and shrinks G; a denser level grows Z and eats G through the heel — with the honest note that the
0.02 mm side-goal step sits below half the 0.05 land quantum, so adjacent densities can
legitimately round to ONE width and the width assertion is ≤ per step, < across the range), Z/G/X
round to the quantum and the pads derive EXACTLY from the rounded values so `Z = G + 2·(pad
length)` is an identity, and a generated SOIC-8 + 0805 placed on a real board pass the layout's own
pin-covering `Check` and the default DRC end to end. Families: `Chip` (1608 metric and larger — the
small-chip goal row is not transcribed, refused by name), `DualGullwing` (SOIC numbering, 1..n/2
down the left then n/2+1..n up the right), `QuadGullwing` (QFP counter-clockwise from pin 1 at the
top of the left side, the rotated rows swapping pad width/height), `Sot23` (pins 1–2 below at the
stated pitch, 3 above on the centreline), and `Bga` (JEDEC row letters skipping I/O/Q/S/X/Z then
AA/AB/…, the land the ball reduced by the ⚠ nominal collapsing-ball percentage per density). A land
whose inner gap CLOSES (G ≤ 0 — the pads would merge) refuses naming the number, as do overlapping
leads, inverted dimension ranges (`DimRange` validates at construction, since a swapped min/max
silently flips every formula's direction), odd dual pin counts and bad pitches; courtyard and
silkscreen are deliberately absent from a `Footprint` and derive downstream. Filed by name: the
small-chip row, QFN/DFN, MELF, chip arrays, thermal-pad paste divisions.

### Stage 2 — the board and its parts (`PcbBoard`, `PcbLayout`, IDF import)

Stage 2 turns a schematic into a board, and the load-bearing rule carries over verbatim: **one
declaration produces both**. The schematic graph is the single source; the board copper, the
footprint placement and the 3D bodies all DERIVE from it — a pin and its pad are one identity
(pin `1` ↔ pad `1`), which is the seam DRC and routing (later stages) will consume.

**The plate is built with the existing `Shape` API, and its volume is a closed-form oracle.** A
`PcbBoard` is a polygon outline + thickness + a `PcbStackup` (two copper layers by default, N via
`Layers`) + its own `BoardHole`s (mounting holes and vias) and `KeepOut`s. `Plate()` extrudes the
outline and drills the holes (`Shape.Extrude` + `Shape.Drill`), so it is an exact B-Rep whose
volume is `outline area × thickness − Σ πr² × thickness` (`ExpectedPlateVolume`) — the tessellated
volume approaches it from below by each round hole's inscribed-polygon chord deficit, matched to
~1e-4 relative through `Part.MassProperties`' Richardson route, while the un-drilled prism is exact
to 1e-6. The outline is stored as a polygon (the common board shape, and exactly what IDF carries)
so it round-trips exactly.

**The placement transform is the assembly's own transform math, and the bottom flip is a genuine
reflection on the part.** A `PcbLayout` is a schematic + board + `PcbPlacement`s, each a
`(x, y, rotationDegrees, side)` pose naming a component. The board occupies z ∈ [0, thickness];
a top placement seats at the thickness with its body extending up, a bottom placement seats at 0.
`PlacementPose` is a proper `Frame3d` (translate + rotate about Z); `PartTransform(side)` is
identity on top and a reflection across the board plane (`Matrix4d.CreateScale((1,1,-1))`) on the
bottom, which lives on the component's `Part.Transform`. So `WorldOf(placement)` is exactly
`OccurrenceFrame.Then(worldXY).ToMatrix() * partTransform` — **bit-identical to the
`PartInstance.World` the assembly's `Flatten` produces** (both call `Posed(occ, 0).Then(root)…`,
and `Posed` returns the frame untouched at explode 0), which is the oracle for #6. The reflection's
square is the identity (`Mirror(Mirror(x)) == x`), the world determinant flips sign under it, and
the body genuinely hangs below the board — the FlipX-not-FlipZ doctrine in this domain being that
the reflection is spent on the PART while the pose stays a proper frame, so the board's own +Z
(world up) is never negated. **A through-hole pad keeps the same world `(x, y)` on both faces** (a
plated hole serves both), because the reflection across the board plane leaves x, y untouched — so
a bottom-placed header's holes line up with a top-placed one's, which is why the plate drills from
`PlacementPose` (no mirror needed for the xy). A through-hole component drills the plate by exactly
its hole cylinders; **an SMD component drills nothing** (its plate is bit-identical to the bare
board, since the through-hole set is unchanged).

**The one-declaration identity check is the geometric lift of the schematic's pin count.**
`PcbLayout.Check` establishes that every pin of every placed component resolves to exactly one
placed pad at a known copper location — `PlacedPinCount == PlacedPadCount` with every pin covered
once — and names which way a failure fell (a pad with no pin, a pin with no pad, a pad off the
board outline, a via/through-hole in a keep-out, a component with no footprint). `PadsOfNet(net)`
resolves a net's pins to their copper regions. `Place` refuses an unknown reference or a repeated
one by name at declaration time (the `Schematic.Add` pattern); the softer conditions are named in
the check. `CopperLayers()` assigns SMD pads to their placement side's outer copper and through-hole
pads to every layer (a plated hole spans them all) — the copper model the routing/DRC stages read.

**`ToAssembly` is one part per (definition, side), N occurrences.** Identical components on one
side share a `Part` (so `Bom.For` counts occurrences correctly); a bottom-side part carries the
reflection on its `Part.Transform`. A body-less component still places its pads — it just has no 3D
occurrence. The board is the base part; the whole thing flattens to `PartInstance`s the viewer, the
BOM and every exporter already consume.

**Persistence embeds the schematic (it is the source, not a copy).** `PcbLayout.Save`/`Load`
extends the schematic seam — one seam refactored so `SchematicWriter.BuildRoot`/`ReadObject` build
the schematic as an OBJECT the layout nests — with the board, placements, holes and keep-outs
alongside, write-only-when-stated throughout (a default two-layer stackup, an identity board frame,
a top-with-no-rotation placement, an empty hole/keep-out list are all omitted). `save → load → save`
is a byte-identical fixed point. The `Pad` extension is backward-compatible for exactly this reason:
`Kind`/`DrillDiameter` are written only when non-default, so a stage-1 SMD footprint saves
byte-identically (the `PinType.Unspecified = 0` convention, applied to `PadKind.Smd = 0`).

**IDF import, honestly.** `IdfReader` reads an IDF 3.0/4.0 board (`.emn`) into a `PcbImport` —
outline, thickness, drilled holes, placements, keep-outs — honouring the header's unit (MM/THOU →
mm, recorded in `Diagnostics`, the `IgesReader`/`$INSUNITS` lesson), validating the
`.SECTION`/`.END_SECTION` nesting up front and refusing a malformed file BY NAME (the
`StlReader`/`IgesReader` rule). **IDF carries no connectivity** — no pins, no nets — so `ToLayout`
synthesizes a data-only schematic (a component per placement, named by its package) to hold the
placements against, which is honest: the layout's identity check then reports the components have no
footprints rather than pretending pins resolve to copper. `IdfWriter` closes the loop in canonical
millimetres, so `read → write → read → write` is a byte-identical fixed point for the geometry IDF
carries (the outline, holes, placements and keep-out polygons round-trip as data; the synthesized
keep-out names and the fixed header date do not, because IDF has no field for them). v1 scope is
stated: straight-segment outlines/keep-outs (a nonzero arc angle is flattened to a chord, reported),
outline cutout loops dropped, the `.emp` library accepted but not modelled.

### KiCad `.kicad_pcb` whole-board import (`KiCadPcbReader`)

`KiCadPcbReader.Read(text)`/`ReadFile(path)` → a `KiCadPcb` (the reconstructed `PcbLayout` +
diagnostics) is the **board twin of the KiCad component reader**, and it is a pure reading path over
the SAME hand-rolled `SExpr` parser, the SAME covered-subset / refuse-by-name discipline, and — the
finding — **no additive change to any board type at all**. Everything builds through the existing
public constructors (`PcbBoard`/`PcbStackup`, `Schematic.Add`/`Connect`/`Stub`,
`PcbLayout.Place`/`AddTrace`/`AddVia`/`AddPour`), because the one thing an IDF board lacks — a
schematic to hold placements against — a KiCad board **already carries in the pads' own net tags**.

**The load-bearing decision is that the pads' `(net n name)` tags ARE the reconstructed schematic**,
not a hint toward one. Each `(footprint)` becomes a data-only `PartDefinition` (one `Pin` per distinct
pad number), and the reader groups pads by net — a multi-pad net becomes a `Signal` net (`Connect`), a
single-pad net a `Stub` — so the synthesized schematic's connectivity IS what KiCad intended. That is
what makes the headline oracle a real check rather than a hope: `PcbConnectivity` then answers whether
the imported COPPER (tracks, vias, zones) actually joins the pads those tags say belong together, and
`PcbDrc.Check` answers whether the geometry is manufacturable. An import that connected the wrong pads
would show up as a net the copper does not join — the silent failure this stage exists to make loud.

**The coordinate convention is verbatim-no-flip, and that is what "exact from the file's mm
coordinates" requires.** KiCad stores Y downward; a Y-flip would make `pad.Y == −file.Y`, which is not
"exact from the file's coordinates", so the reader imports coordinates verbatim into the board frame
(noted in `Diagnostics`). The choice costs nothing because it is INTERNALLY CONSISTENT — pads, tracks,
vias, zones and the Edge.Cuts outline share one frame — and handedness is invisible to connectivity,
the clearance DRC and a Gerber (which is just artwork); a footprint rotation is taken as a CCW rotation
in that frame, matching `PcbLayout.PlacementPose`. Covered: `(general)` thickness, the copper
`(layers)` stackup (any `.Cu`-suffixed layer, F.Cu first at z = thickness), the `Edge.Cuts` outline
(`gr_line` chained by endpoint, `gr_rect`/`gr_poly`, `gr_arc` flattened to a sampled polyline), the
`(net)` table, `(segment)`/`(arc)` tracks, `(via)`s (type derived from the layer span), and `(zone)`s
as `CopperPour`s carrying their outline, net and `(priority)` (which maps straight onto
`CopperPour.Priority`, so an imported overlapping-zone board resolves the same way KiCad drew it) whose
FILL EngrCAD re-derives (KiCad's stored `filled_polygon`/hatch geometry is not read — the "hatch/fill
best-effort with a note" boundary). Ignored / refused BY NAME: keepout / rule
areas, teardrops, dimension graphics, 3D-model references, a netless track/via/zone, and a
non-`(kicad_pcb ...)` root — including a `.kicad_sym` or `.kicad_mod` handed here (the head-tag check).
The reader NEVER throws on dirty per-element geometry (a bad via is caught and noted, the
`StepReader`/`IgesReader` culture); only a malformed S-expression or a wrong root refuses.

Verified higher than usual (interchange fails plausibly): the net connectivity matches KiCad's intent
(each multi-pad net connected via its tracks/via/zone, the GND zone joining every GND pad — and the
MUTATION that proves it, removing the zone leaving GND an unrouted ratsnest of two islands); the board
is DRC-clean with a known-violation fixture (a copper short between two nets) FOUND; pad centres exact
from the file's mm coordinates including a 90°-rotated footprint; the imported copper round-trips to
Gerber and re-reads (the twin-decoder oracle, by area and symmetric difference); determinism (two
reads give byte-identical Gerber); the refusals by name; and the **component reader stays bit-identical
by construction** (a new file, nothing shared moved), pinned by re-asserting a component-load fixture's
exact geometry. Filed: custom pad
primitives and differential-pair / length-tuning metadata. Docs: `examples/ecad-pcb.md`.

**EXPORT of our board to `.kicad_pcb` landed** (`KiCadPcbWriter.Write`/`WriteFile`), and the design
is that the READER IS THE ORACLE: the writer emits exactly the reader's covered subset — the copper
`(layers)` stack, the `(net)` table, each placement as a `(footprint)` with its pads on their nets,
`(segment)` tracks one per trace chord, `(via)`s, `(zone)`s from pours (outline + net + priority +
`(connect_pads (clearance))`; KiCad re-fills, exactly as EngrCAD re-derives a fill on import), the
`Edge.Cuts` outline and the title block — so "the exported board is the same board" is asserted
THROUGH the reader (net partition, poses, exact pad centres, copper counts, the DRC verdict) and
**`write → read → write` is a BYTE fixed point**. Earning that fixed point forced the one
non-obvious decision: the writer numbers nets in the reader's own PAD-ENCOUNTER order (placements
in order, each footprint's pads in order, stragglers after), because the reader reconstructs its
schematic from the pads rather than from the net table, so any other numbering breaks on the
second write. It also drove two ADDITIVE reader improvements (a reader learning from its twin):
the footprint's Value property now imports (a value would otherwise die on the first round trip),
and a zone's `(connect_pads (clearance))` maps onto the pour's copper clearance. Layer names
already ending `.Cu` export VERBATIM — import → export → import stability — while EngrCAD-native
names map positionally (F.Cu / In1.Cu… / B.Cu); coordinates are written verbatim (the reader's
no-Y-flip convention run the other way). Refused BY NAME (geometry the file cannot spell without
lying): an embedded or inner-seated placement, and a board carrying free `PcbBoard.Holes` — the
KiCad idiom for a mounting hole is an NPTH footprint pad, which this kernel would re-import as a
PLATED pad, a silent copper change (filed). Reported, never silently dropped: a stated fabrication
spec, mask/silk/paste settings, teardrops, and NoConnect nets (a KiCad pad with no net is what a
no-connect pad means there).

**The reader also populates the board's FABRICATION SPEC** (`PcbLayout.Fabrication`, a
`PcbFabricationSpec`) from the `(setup (stackup ...))` block — BEST-EFFORT and WRITE-ONLY-WHEN-STATED,
which is what keeps a no-stackup board byte-identical: it maps only the fields the file actually gives
and returns null when the file states nothing, so `Fabrication` stays null and the saved layout has no
`fabrication` key (the reader only ADDS the spec). The mapping is the stackup's TOTAL (sum of every
stated layer thickness) → finished board thickness; the first copper layer's thickness ÷ **0.035 mm**
(1 oz = 35 µm, the industry rounding of 34.79 µm and KiCad's own 1 oz thickness — ⚠
verify-against-datasheet, so a KiCad 0.035 mm copper reads exactly 1 oz) → copper weight; the first
dielectric layer's `(material ...)` → base material; `(copper_finish ...)` → a named
`PcbSurfaceFinish` (substring-mapped so KiCad's HAL/HASL and lead-free spellings resolve, lead-free
checked BEFORE plain HAL so a leaded `HAL SnPbHAL` → `Hasl`; an unmapped string → `Other` carrying the
verbatim name, noted); the outer mask/silk layers' `(color ...)` → the mask/silk colours; and any
legacy default net class's `trace_width`/`clearance` → the minimum trace / clearance (KiCad 6+ keeps
these in the project file, so a modern board simply states none). **Every numeric field is gated
finite-and-positive**, so a garbage stackup value is dropped rather than crashing the import (the
readers-never-throw-on-dirty-geometry culture) and a stated field never trips `PcbFabricationSpec`'s
own validation. The populated spec **round-trips** as a `save → load → save` byte fixed point through
the layout file with no writer change (persistence already carries the spec), verified alongside the
per-field population, the finish-string mapping (ENIG/HASL/HAL-lead-free/OSP/immersion, ENEPIG →
Other), the copper-weight conversion (0.035 → 1 oz, 0.070 → 2 oz), the write-only-when-stated
partial-stackup case, the byte-identical no-stackup case, and determinism. Filed under the fab-drawing
entry: a per-fabricator stack-up CATALOGUE that would let a caller pick a house stack-up rather than
reading one. (The IPC-class → `DrcRuleSet` preset and the spec-vs-class cross-check filed here LANDED
— see the fabrication-spec paragraph below.)

### KiCad `.kicad_sch` whole-schematic import (`KiCadSchReader`)

The SCHEMATIC twin of the board reader — `KiCadSchReader.Read(text)`/`ReadFile(path)` reconstructs a
whole `Schematic` (components + nets) from a `.kicad_sch` over the same `SExpr` parser, the same
covered-subset / refuse-by-name discipline, and the SAME symbol-parsing core as the `.kicad_sym`
reader. That core (`KiCadSymbolReader.ParseSymbolList`, over one `(symbol …)` list) was FACTORED OUT
of `KiCadSymbolReader.Read` rather than duplicated — a schematic's embedded `lib_symbols` are the
same grammar as a symbol library's symbols — and `Read` now delegates to it, so the `.kicad_sym`
path is unchanged (pinned by the component-load fixtures re-asserting the resistor's exact symbol).

**The load-bearing decision is that a schematic never states its netlist — it DRAWS it.** A board
tags every pad with its net (which is why `KiCadPcbReader` can synthesize a schematic from the pad
tags); a schematic has no such tag, so the reader RECONSTRUCTS the netlist from the geometry with a
union-find over the connection POINTS — the same "two things are one net iff they touch" rule
`PcbConnectivity` uses on copper. A wire joins its two endpoints; a pin anchor, a label, a
power-symbol pin or a junction lying ON a wire joins that wire (so a junction at an X-crossing joins
BOTH wires, while a plain crossing with no junction stays two nets — the junction dot is the
schematic convention); same-name labels are one net; a `no_connect` marks an isolated pin. **This is
exactly the rule `SchematicDrawing.Verify` asserts, INVERTED** — the drawing writer proves a sheet
joins the pins the netlist connects, and the reader recovers the netlist a sheet joins.

Two smaller decisions carry it. **Points coincide at a 1e-4 mm weld** (not exact equality): KiCad
coordinates are exact grid decimals and a placed pin anchor is an exact isometry of them, so points
that should coincide differ only by IEEE round-off (~1e-13 mm), far below 1e-4 and far below the
coarsest real connection spacing — the weld welds exactly the points that are the same point. The
isometry is the **library-Y-up → sheet-Y-down flip plus the instance rotation** (a `Device:R` at
`(x, y, 0)` puts pin "1" at `(x, y − 3.81)`); the flip is what makes the connectivity the ORACLE for
the transform — a wrong sign lands the pins off the wires and the partition breaks, which is what
the mutation test measures. And **power symbols are net-name markers, not components** (their `Value`
is the net name at their pin anchor), so the schematic's components stay the real parts. Docs:
`examples/ecad-library.md`.

**Multi-unit symbols merge.** A dual op-amp is ONE physical package (one footprint, one reference
designator) drawn as SEVERAL schematic symbols — amp A, amp B, a power unit. **A `PartDefinition`
gains `Units` — one `Symbol` per unit, each with its own pins at its own anchors — while `Pins` is
their UNION** (the netlist terminals of the whole package), and the pin NUMBER identity spans the
units (`PinIdentity.Check` takes the union of every unit's symbol pins). The restructure is minimal
and byte-identical: `units` is an OPTIONAL LAST constructor parameter passed INSTEAD of `symbol` (both
is refused), `Symbol` stays the FIRST unit (or null), and a single-unit / symbol-less definition
derives `Units` from `symbol` so every incumbent construction is unchanged (asserted). **The
`.kicad_sym` reader splits the unit sub-symbols by their `<name>_<unit>_<style>` suffix** — unit `0`
is common to every unit (its graphics/pins go into all of them), a `style` ≥ 2 (De Morgan alternate)
is ignored with a named diagnostic, and two units disagreeing about one pin are reported by name and
reconciled to the first (a reader never throws on dirty input). The single-unit case reproduces the
old flatten exactly, because a lone unit's symbol is `common ∪ unit`, which is what the flatten
already produced. **The `.kicad_sch` reader MERGES the same-refdes `(unit N)` instances into one
`Component`** (keyed by reference designator, the hierarchical refdes in the multi-sheet path), and
places ONLY that instance's unit's pins at THAT instance's location — so a net wired to amp A's output
and one wired to amp B's input are distinct nets on one IC, and a net that physically spans the two
amp units is the discriminating test (had the merge placed both units at one location, the wire
between their true positions could not reach both). This REPLACES the old "duplicate reference →
separate component with a note" behaviour; a repeated placement of one unit, or two different symbols
under one reference designator, is reported and skipped (no rename — one reference designator is one
component). **The board side is unaffected**: a multi-unit component is one component with one
footprint and all pads, since `Pins` is the union and units are a schematic-drawing/placement concern.
Persistence writes the per-unit symbols under a `units` key (a single-unit definition keeps the
incumbent `symbol` key, so it saves byte-identically); `save → load → save` is a byte fixed point.
**Multi-unit schematic DRAWING landed** (`SchematicSheet`): `SchematicPlacement` keys poses by (refdes,
1-based UNIT), so `Place(refdes, pose)` places unit 1 (the whole single-unit API unchanged and its output
byte-identical) while a multi-unit part places EACH unit at its own sheet location; the sheet draws one
symbol per unit (labelled `U1A`/`U1B`/…, the value once under the first unit) and resolves each pin to the
unit whose symbol carries it. **The connectivity reconstruction is UNIT-AGNOSTIC and that is what makes it
tractable** — it reads the DRAWN wire geometry (`AnchorOf` just needs the right per-unit anchor), so a net
across two amp units of one package draws as two symbols wired together and `Verify()` reconstructs it as
one net, with no change to the reconstruction itself. A multi-unit part with a unit unplaced is refused BY
NAME (`U1B`); verified by the cross-unit net being joined, the two units drawing well apart, and the
single-unit path staying byte-identical. **De Morgan / alternate unit BODIES are now CARRIED** (not
drawn): the reader collects the `_1_2` (`unit_style` 2) sub-symbols per unit in PARALLEL and builds each
unit's `Symbol.Alternate` (same pin numbers, a different drawing) rather than discarding them with a
diagnostic, and it round-trips through the schematic file write-only-when-stated (a symbol with no
alternate saves byte-identically; one with an alternate is a save→load→save fixed point, the recursion one
level deep since an alternate never nests). **DRAWING the alternate landed too**: `SymbolPose` gained an
`Alternate` toggle and `SchematicSheet` draws `symbol.Alternate` when a placement asks for it, through one
`EffectiveBody` helper the symbol rendering AND the pin resolution (`AnchorOf`/`LeaveDirection`) both ask —
so a pin's world anchor moves to the ALTERNATE body's own anchor and the wire follows it (a partial
alternate lacking a pin falls back to the primary). Verified by the pin anchor moving between the two
bodies (95 → 93 on the fixture) with both drawings `Verify()`-clean; single-unit / primary-body drawing is
byte-identical (the toggle defaults off). So De Morgan is complete end to end — carried, round-tripped and
drawable.

**Single-sheet BUS import.** A bus is a labelled bundle of signal nets — a bus-VECTOR label
`DATA[m..n]` on a `(bus …)` wire declares the members `DATA`+m..`DATA`+n (`DATA[0..7]` is
DATA0..DATA7; a reversed `DATA[7..0]` is the same eight, honoured in the drawn direction) — and a
`(bus_entry …)` rips a member off the bus onto a signal wire. **The load-bearing finding is that a
ripped tap's net is its OWN local label, so the bus's connecting role is subsumed on a flat sheet.**
KiCad requires the ripped wire labelled with a member (`DATA3`), and same-named labels are already
one net by local-label equivalence — so the signal union-find is UNCHANGED (a member `DATAi` is
reconstructed like any other labelled wire, and two same-named taps ride one net), and the bus model
does two things only: (a) it DECLARES the member namespace by expanding the `NAME[m..n]` label, which
is what stops a bus-vector label being mistaken for a signal net (`DATA[0..7]` is never a net), and
(b) it VALIDATES each tap against the members of the bus it rips off — a bundle being a connected
component of bus wires, tracked by a separate union-find so a bus point is never a signal net, and a
tap's member checked by reading the ripped wire's net label back off the signal graph. The connecting
role becomes load-bearing ACROSS sheets (hierarchical bus pins carrying a bundle over a sheet
boundary), and that is **now supported too** (see the hierarchical-import entry); an anonymous bus GROUP
(`{A B DATA[0..1]}`) is now SUPPORTED — its members are the whitespace-separated tokens, each a bare
signal or itself a bus vector (expanded in turn), declaring the namespace exactly as a vector does, so
the tap validation and the connectivity reconstruction are unchanged (a group is a bundle of member
nets; on one sheet its connecting role is subsumed by local-label equivalence). A named bus ALIAS is
supported too — a `(bus_alias "PCI" (members …))` builds an alias TABLE, and a bare label matching an
alias is read as a bus declaring those members (each a bare signal or a vector, expanded). A NESTED group
stays out of scope and is refused by name, as is a malformed range
(`DATA[]`, a non-integer bound — stricter than KiCad, which would treat those as ordinary labels, but
the refuse-by-name ethos), while a dangling bus entry (its bus side or wire side touching nothing) or
a non-member tap is REPORTED not thrown. **The oracle is the member partition asserted exactly plus a
RELABEL mutation** — moving a tap's label moves its pin to a different member (a positional /
membership-blind importer would pass the partition test and fail this), with reversed-range parsing
(forward and reversed expand to the same eight members, so all eight taps validate clean),
plain-net non-contamination (a bus beside a plain net leaves it exactly what it was, structural since
the signal union-find never sees a bus point), and the bad-range / non-member / dangling reports each
pinned. **Anonymous groups AND named aliases since landed** with the same oracle — the member partition (bare
signals AND a vector token expanded inside the group / alias), a non-member tap reported by name, and a
nested group refused by name. **Buses ACROSS sheets landed too**: a BUS sheet pin (a sheet pin whose name
is a vector / group / alias, resolved through the PARENT's alias table falling back to the CHILD's — the
pin is drawn on the parent, the hier label it matches lives in the child) is kept OUT of the signal
machinery entirely (its position sits on the parent's BUS wire, which the signal graph never contains) and
matched with the child's hierarchical BUS label of the same raw name; the stitch is then MEMBER-BY-MEMBER —
for each member M, the parent's local net named M joins the child's local net named M (only LOCAL labels
need it, global/power already span; a member unused on one side stitches nothing, which is normal, not a
defect). Per-sheet tap validation reuses the flat `ValidateBuses` verbatim, generalized with an `instance`
parameter whose flat value 0 IS the flat `Intern(p)` (bit-identical by construction). The oracle is the
cross-sheet member partition (DATA0 spans, DATA1 spans, the two members stay DISTINCT — the stitch is per
member, never a bundle short) plus the rename mutation (the child's hier bus label renamed off the port
splits the members, with BOTH dangling directions reported by name). Docs: `examples/ecad-library.md`.

**Hierarchical / multi-sheet import** (`KiCadSchReader.ReadProject(rootPath)` /
`ReadProjectFrom(rootFile, sheetsByFile)`) flattens a real KiCad hierarchy — a root `.kicad_sch` plus
the sub-sheet FILES it references through `(sheet … (property "Sheetfile" …))`, resolved relative to
the root's directory, recursively — into ONE `Schematic`; the in-memory map overload is the testable
core the disk entry wraps, and the single-sheet `Read` is untouched (it still refuses a hierarchy by
name, so a flat import cannot silently drop a subsheet). **The load-bearing decision is that the flat
union-find GENERALISES with an INSTANCE dimension rather than forking** — `Graph.Intern(instance, p)`
keys the weld cell by `(instance, x, y)`, so two sheets with a wire at the same `(x, y)` do NOT join
(the flat path is bit-identical: it interns everything at instance 0, so node ids are assigned in the
same order), and every leaf geometry/naming helper (`OnSegment`, `TryWire`/`TryLabel`, `ReadPlacement`,
`ReadLibSymbols`, `MakeUnique`) is reused verbatim — only the multi-instance orchestration is new,
because the flat path knows exactly one sheet. **Cross-sheet stitching is NAME-matching layered on the
geometry**: a parent SHEET PIN (drawn on the `(sheet …)` node in the parent's coordinate space, so it
joins the parent net by position) joins the sub-sheet's `hierarchical_label` of the SAME NAME (scoped
to that child instance) — so the parent net and the child net become one; a `global_label` or a power
symbol joins ACROSS every instance by name; a local `label` stays WITHIN its instance. **The scoping
is the crux, and it is not enough to scope the union — the NAME must scope too**, since two sheets'
"CLK" local nets named "CLK" would be re-merged by `Schematic.Connect`'s create-or-extend-by-name
rule; so a local net's name is QUALIFIED by its sheet path (`"SubB/CLK"`), which keeps the two nets
distinct both in the graph and in the flattened schematic. Components take **hierarchical reference
designators** (`"PowerSupply/U1"`, the `PartInstance` occurrence-path convention), so a sheet placed
TWICE gives distinct instances (`"Amp1/U1"`, `"Amp2/U1"`) with distinct internal nets. Naming
precedence: power &gt; global label &gt; stitched sheet-pin/hierarchical-label PORT name (used bare —
a clean hierarchical net name) &gt; hierarchical / local label (qualified by path) &gt; a generated
`Net-(minPin)`. Refused / reported by name: a RECURSIVE sheet reference (a sheet including itself,
detected by a Sheetfile chain and refused — it cannot be flattened); a missing/unreadable sub-sheet
file (reported, subtree skipped — the readers-never-throw-on-dirty culture); a dangling hierarchical
port / an unmatched sheet pin (both reported). **The oracle is the reconstructed cross-sheet partition
asserted exactly PLUS the mutation that proves the stitch is name-matched** — rename the sub-sheet's
hierarchical label off the parent sheet pin's name and the cross-sheet net SPLITS (a name-blind
stitcher would pass the first assertion and fail this), with the local-vs-global scoping tested both
ways (two local "CLK" = two nets, two global "CLK" = one) and a sub-sheet placed twice giving four
distinct components and two distinct internal nets. **Buses across sheets landed** — a BUS sheet pin
carries its members over the boundary, stitched member-by-member (see the bus-import entry above for the
mechanism and the oracle); multi-unit symbols merge in the hierarchical path too, keyed by the
hierarchical refdes. Docs: `examples/ecad-library.md`.

### Stage 3 — placement constraints (`ConstrainedLayout`, `PcbConstraintSolver`)

Stage 3 places components by CONSTRAINT rather than by typed coordinates: a rough drawn layout is
the SEED, and `layout.Constrain()` builds a `ConstrainedLayout` whose `Solve()` returns a NEW
`PcbLayout` at the poses satisfying the relations. The one-declaration rule carries over verbatim —
the copper, drills, nets and 3D bodies all DERIVE from the moved placements, so `Solved.Check()`
still passes and `Solved.PadsOfNet` returns the moved copper.

**The load-bearing decision is to build a FOCUSED solver rather than reuse the Modeling one, and
the reasoning is about the VARIABLE MODEL, not effort.** The prompt's instruction was to prefer
reusing an existing LM seam — but the sketch and mate LM engines are internal/private to
`EngrCAD.Modeling` and each is bound to its own variable model: `MateSolver`'s `Solver` is a private
partial class over `Occurrence`/`Frame3d` with 3D 6-DOF rigid perturbations, and `SketchLevenberg`
is internal over free 2D POINT coordinates (a sketch's joints, arc centres and radii). A PCB
placement is a rigid 2D pose `(x, y, θ)` whose rotation moves the WHOLE footprint about the
placement origin — which is neither: not free points (the footprint is rigid, its pads are not
independent variables), and not 3D 6-DOF (wrong dimension, different rotation encoding). Neither
engine exposes a reusable generic LM core, so `PcbConstraintSolver` is that core rebuilt at 2D,
following the MateSolver doctrine EXACTLY: an analytic Jacobian; every residual a LENGTH (angular
residuals scaled by the board diagonal, and the rotation variable divided by it, so one linear
tolerance covers the system and every Jacobian column is O(1)); rank and DOF from a diagonally
pivoted Cholesky of JᵀJ at the **1e-6 relative floor** (the sketch-constraint floor, not the mate
1e-8 — at layout sizes 1e-8² sits below pivoted elimination's own round-off, the recorded lesson);
the drawn layout as seed AND branch selector; an under-constrained layout reported with its
remaining DOF; contradictions and stationary configurations NAMED; a failed solve leaving the
source layout bit-identically unchanged (a fresh layout is produced only on success). No Modeling
solver was touched. **A shared generic 2D-rigid LM core could be factored later** if a third
consumer appears — but there is none today, and factoring one now would risk the two Modeling
solvers' bit-identity for no gain, so it is FILED rather than built.

**The rigid-body model is what makes `Group` a theorem rather than a special case.** Each placement
belongs to one rigid BODY — a singleton by default, or several placements a `Group`/`Cluster` ties
together — carrying three variables (its pose); each member carries a FIXED offset from the body
frame captured off the drawn layout, so a singleton reproduces the placement's own pose exactly
(bit-identical seed) and a group moves as one through both translation AND rotation (verified: a
group rotated 90° carries a member's (5,0) offset to (0,5), preserving the relative pose exactly).
`Lock`/`Fix` grounds a body (its members stay at drawn poses); a placement no constraint mentions is
left where drawn and reported. Body creation order is a pure function of the layout, so the whole
solve is deterministic — asserted by two identical solves producing byte-identical solved layouts.

**Inequalities are handled honestly, as active-set residuals — not fake equalities.** `InsideRegion`
and `ClearOf`/`ClearOfRegion` want `g ≥ 0` (a footprint's bounding circle inside a zone, or a
distance clear of another footprint / a keep-out polygon), so their residual is `min(g, 0)`: it
pushes only when violated and its Jacobian is the constraint's gradient only while active. A
converged solve reports feasibility (no violation past the tolerance), and an inactive inequality
adds no rank, so it leaves DOF free — which is the honest report. A footprint's extent is modelled
by the smallest circle about its origin enclosing its pads (rotation-invariant, so proximity
residuals see translation only; conservative, so keeping the circle clear keeps the copper clear —
verified against the true pad regions). `ClearOf`/`InsideRegion` use one point-to-polygon signed
distance (nearest boundary point plus an even-odd inside test) that works for any simple polygon.

**Verification is the deliverable, and higher than usual because ECAD fails plausibly.** A satisfied
set converges to the weld tier with the DOF reported (a Distance leaves 2 of 3 free, named); an
`AlignEdge` makes the two edges EXACTLY parallel (cross → 0) and collinear (the component point on
the board edge line) — geometric assertions, not "close"; a stated `Spacing` between two pads is met
exactly; a `ClearOfRegion` leaves every pad disjoint from the keep-out with the footprint standing
the full clearance clear (measured against an independent point-to-polygon distance); a contradiction
(two Distances) is NAMED and the layout untouched (asserted through the serializer); a stationary
start (`Perpendicular` on already-parallel edges) is NAMED rather than nudged; the solve is
deterministic to the bit; the one-declaration identity survives (Check passes, PadsOfNet returns the
moved copper); and the whole thing is scale-invariant (a 1000× board solves to relative 1e-9).

**Persistence extends the stage-2 seam.** `ConstrainedLayout.Save`/`Load` adds a `constraints` array
to the layout JSON, write-only-when-stated: a layout with NO constraints saves byte-identically to a
stage-2 file, and a constrained one is a `save → load → save` byte-identical fixed point. Every value
a constraint captured — a signed `PointOnLine` offset, an `AlignEdge` side read off the drawing —
rides as DATA (the branch selector, not a rule to re-run), and a stored direction is kept VERBATIM
rather than re-normalized (the `AxisRef` ulp rule), so a reload reproduces the exact constraint.

### Stage 4 — the copper DRC (`PcbDrc`)

Stage 4 is the geometric design-rule check over a board's copper (`DrcRuleSet`, `PcbCopperModel`,
`CopperFeature`/`DrilledHole`, `PcbDrc.Check` → `DrcReport`). It is an exact 2D-region query the
exact curved 2D machinery (§5) answers with no tolerance, and every rule NAMES, LOCATES and
MEASURES its offender against its limit — the `PcbLayoutCheck`/`SchematicCheck` house style, higher
than usual because ECAD fails plausibly.

**The load-bearing rule is that the DRC reads the NETLIST to decide what should connect** — a SHORT
is copper of DIFFERENT nets electrically connected; copper of the SAME net touching is the INTENDED
connection and is never flagged. That is the one-declaration identity doing real work: a pad's net
IS its pin's net, so `PcbCopperModel.FromLayout` resolves each pad's clearance group from the
schematic. Only Signal/Stub nets JOIN their pins; a NoConnect terminal and an unconnected pin carry
a NULL net (each its own group), because two floating pieces of copper are electrically distinct and
must clear each other. A drill never clears its OWN pad (that is the annular ring), skipped by
source rather than by net so an unconnected through-hole pad is not flagged against its own copper.

**Clearance is the tamper-mesh construction** — group each net's copper, grow it by HALF the
clearance (`CurvedRegion2dOffset`), and require different-net grown regions DISJOINT
(`CurvedRegion2dBoolean.Intersection` empty PROVES the clearance). A positive-area overlap of the
UNGROWN copper is a SHORT (the stronger failure); an overlap only once grown is a near miss. A
broad-phase AABB gap ≥ the clearance is a sound skip (an AABB is a superset of its region, so the
region gap is at least the box gap), so the exact curved boolean runs only on close pairs, and the
MEASURED gap for the report is a bisection over the SAME grow-and-intersect the pass/fail rests on
(run only on the few violations) so the number and the verdict cannot disagree. Annular ring is
arithmetic; drill-to-copper grows the drill disc and intersects other-net copper on every layer
(cross-layer); copper-to-edge subtracts the board outline shrunk by the clearance (an exact polygon
inward offset) from each feature; trace width is `Region2dThickness`' opposing-wall neck; the
acute-angle / acid-trap rule is the wedge angle between two copper edges at a joint (measured either
side, so a copper spike AND an etchant-side notch both flag), with a smooth arc joint and a 90° pad
corner passing under the default 90° threshold.

**Multi-layer:** clearance, shorts, width and acute angles are PER LAYER; drill-to-copper is
CROSS-LAYER. **The ratsnest is the INVERSE of a short** — a signal net whose copper is more than one
connected region is UNROUTED (union each net's copper across layers; a through-hole pad overlaps
itself, so a via connects layers) — and it is INFORMATION, not a fault, because a bare-pads board
before routing is unrouted, not wrong; `DrcReport.Ok` ignores the ratsnest.

**Traces arrive in stage 5, and the DRC needs no change to reach them.** Trace width and the
acid-trap rule genuinely want conductors; the copper today is pads, so those rules run on whatever a
layer carries and fully engage once a trace — a stroked centre-line region through the same
`CopperFeature` type — exists. `PcbDrc.Violates(model, candidate, rules)` is the incremental seam a
router costs a candidate route with (clearance/short on its layer, edge, width, acute), so the DRC
is a routing cost function rather than only a final gate.

**Scope decision on persistence.** A `DrcRuleSet` is a standalone checking parameter (a plain record
of six numbers), NOT baked into the layout file — a board's design rules belong to the fabricator's
capability sheet, and the caller passes a rule set to `Check`, so the stage-2/3 layout files stay
byte-identical (nothing in the persisted layout changed). The defaults are ⚠ verify-against-datasheet
nominal figures (0.15 mm clearance/width/ring, 0.2 mm drill-to-copper, 0.25 mm copper-to-edge, a 90°
acid-trap threshold), flagged like `StandardHoles`/`SheetMaterials`.

**Verification is the deliverable.** A known clearance violation is FOUND and a near miss at
clearance + ε PASSES, measured against the closed-form gap (two Ø1 pads, gap = centre distance − 1);
the clearance proof is asserted DIRECTLY (the grown regions' intersection empty on a passing board,
non-empty on a failing one); a short NAMES BOTH NETS while two SAME-net overlapping pads are never
flagged (and two unconnected pads ARE — the one-declaration assertion with teeth); annular ring,
copper-to-edge and drill-to-copper are checked from both sides of their limit; a rule set and board
that pass still pass after a 1e-3× and a 1e3× uniform scale (relative tolerances); the check is
deterministic; and the placed stage-2 fixture is DRC-clean with an unrouted ratsnest of both its
signal nets.

### Stage 4b — multilayer stackups and embedded components (`LayerStackup`, `Embedding`)

Stage 2's `PcbStackup` is copper-only — a list of named copper PLANES at stated z-heights. Stage 4b
generalizes it to the full physical build-up while keeping the copper-only path **byte-identical**.

**`LayerStackup` is an ordered list of copper AND dielectric `StackLayer`s, each with a thickness,
top-most first.** The board is extruded through the whole build-up, so `TotalThickness` (the sum of
every layer's thickness) is the board thickness, and each copper plane's z is DERIVED rather than
stated. **The z-derivation is one CONTACT rule applied uniformly**: a copper plane sits at the
surface at which it is contacted — the two OUTER copper layers at the board's faces (top at
`TotalThickness`, bottom at 0), an INNER copper (reached through the dielectric from either side) at
its own midplane. For the standard copper–dielectric–…–copper builds this puts the outer coppers
exactly on the faces (so an SMD part still seats on the surface) and the inner coppers between,
which is the oracle. **The accumulation is bottom-up so the endpoints are EXACT**: walking the
layers in reverse from z = 0 puts the bottom copper's plane at exactly 0 and, since the total is the
same bottom-up sum, the top copper's at exactly the total — where accumulating from the top and
subtracting would leave the bottom face at `total − Σt`, which is 0 only to round-off (measured:
−2.2e-16 on a six-layer stack). `LayerStackup.CopperStackup` is the derived `PcbStackup`, so every
stage-2..4 consumer reads a multilayer board through the exact seam it already reads; a board built
the copper-only way carries a null `LayerStackup`, and its path is unchanged.

**A placement generalizes from `(x, y, rot, side)` to also carry a seat `Layer` and an `Embedding`.**
`Place` stays the surface API (Layer = null, Embedding = Surface — the struct default, so the record
serializes byte-identically); `Embed(reference, layer, x, y, embedding, cavityClearance, side)` seats
a component on an inner copper layer, at that layer's z (`SeatZ`), inside a cavity milled into the
plate. **The cavity is a box tool subtracted from the plate**, sized to the footprint (and body)
bounding box grown by the clearance, at the component's depth — and the two embedding styles differ
only in the tool's z-extent: an ENCLOSED cavity is a box fully interior to the plate (an internal
void — the build-up above and below stays intact, so the die is buried with no external access), an
OPEN cavity is a box that overshoots the placement's surface (a well breaking that face). Both are
**exact** because a box tool cuts planar faces the B-Rep boolean handles at machine precision
(measured rel ~1e-16, closed manifold), and the removed volume is a closed form (lateral area ×
depth-inside-the-board), so `ExpectedPlateVolume` stays the plate's own oracle less each cavity.

**Three properties carry the design.** (a) **The containment oracle is against the outer extruded
prism**: an enclosed body's world AABB is strictly inside `outline × [0, total]` (0 < min.z,
max.z < total) — buried — while a surface body is proud (max.z > total) and an open cavity reaches a
face (its z-range touches 0 or total); each is a crisp measured assertion. (b) **Overlap is a 3D
test — z-intervals AND the rotated lateral rectangles (a separating-axis test on the two OBBs)** —
so two cavities on DIFFERENT inner layers (disjoint z) with overlapping footprints are allowed
(stacked dies), while two on one layer are refused. And this yields an EMERGENT minimum-spacing
property worth stating: a cavity is footprint-plus-clearance, larger than the pads, so two embedded
parts whose cavities do not overlap have pads at least `2·cavityClearance` apart — which is why a
short between two embedded parts on one layer is not reachable through `Embed` (the overlap refusal
fires first). (c) **Every refusal fires at `Embed`, before the placement is recorded** (the
declaration-time `Place` pattern): an unknown layer, a non-negative clearance, a missing
footprint/body, an enclosed cavity that would breach a surface, a cavity off the outline, or an
overlap with an already-embedded cavity — all BY NAME.

**The one-declaration identity holds across layers, and the DRC is N-layer aware for free.** The DRC
already iterates `model.Layers` (all stackup coppers) and groups per layer, and through-hole copper
already lands on every layer — so once `PcbCopperModel.FromLayout` puts an embedded SMD pad on its
inner seat layer (`TargetLayerName`), inner-layer clearance and shorts are checked with no new code
(measured: an inner-layer clearance violation found, an inner short named, a clean multilayer board
reporting zero). One rule is genuinely new — `DrcRule.CavityClearance`: an embedded cavity's wall is
a milled edge, so OTHER copper on its seat layer must clear it by the copper-to-edge minimum (the
embedded part's OWN pads sit inside their own cavity by construction and are exempt). v1 checks the
SEAT layer only (the layer the pads occupy); spanning layers is the microvia-stitching boundary. And
that boundary is stated rather than hidden: v1's identity is per the pad's own layer, so a net whose
pads sit on different layers reads as unrouted (a ratsnest) until routing — cross-layer via/microvia
stitching is a later stage. Persistence extends the seam write-only-when-stated (a full `layerStackup`
array when present, else the copper `stackup`; the placement's `layer`/`embedding`/`cavityClearance`
only when non-default), so a stage-2..4 file is byte-identical and a multilayer/embedded one is a
`save → load → save` fixed point, with a placement naming a missing layer refused BY NAME at load.

### Exploded views — the layer decomposition (`PcbLayout.ToExplodedAssembly`)

A copper-only board is one plate; a `LayerStackup` board is a SANDWICH, and the exploded view is
where that pays. `ToExplodedAssembly(spacing?, name?)` slices the plate into ONE slab per physical
`StackLayer` — the outline extruded over that layer's own z-range, drilled by every through hole and
milled by every cavity that reaches it — and assembles them with the placed components, fanned along
the stackup normal. It is the SIBLING of `ToAssembly` (the board as one part), leaves it untouched,
and returns an ordinary `Assembly`, so the exploded-view slider, the `ExplodeTrack` animation and
every exporter drive it with NO new machinery: the offsets are `Occurrence.ExplodeOffset`/
`ExplodePath`, Modeling-level values, so the decomposition needs no viewer dependency.

**The per-layer z-ranges are reused, not recomputed.** The `LayerStackup` constructor already
accumulates the build-up bottom-up to place the copper planes; it now exposes the same `[low, high]`
pairs as `LayerStackup.Extents`, so a slab is placed at the exact z its copper plane came off. That
is what makes the reassembly EXACT: the extents tile `[0, TotalThickness]` (endpoints on the faces
exactly, contiguous with no gap), so the slabs are DISJOINT and their union IS the plate —
`Σ slab volume == ExpectedPlateVolume()` as a closed form (each slab carries the same holes per unit
height and its share of each cavity), verified to the mass-properties grade on the tessellated slabs.

**The explode is decided by the one relationship a board has — its z-stacking — and every offset
falls out of it:**

- **Layers fan up from the BOTTOM layer as the datum** (it stays put, `ExplodeOffset = null`). The
  natural datum, because the stackup itself accumulates from the bottom face at z = 0; deriving it
  from a centroid, as the mechanical `AutoExplode` does, is meaningless for a sandwich. A layer's
  offset is `n · gap · rank` counting rank from the bottom, so STACK ORDER IS EXPLODE ORDER (a layer
  above another when assembled is above it when exploded — the final z, original mid plus offset, is
  monotone in stack order). And because the offset adds to the layer's ORIGINAL (contiguous)
  position rather than replacing it, the layer thicknesses cancel and `gap` is the clean EMPTY gap
  between consecutive exploded layers whatever their thickness — a 4 mm core and a 35 µm film both
  get the same gap. The default `gap = 2·T/(L−1)` opens the fully-fanned stack to about 3× the board
  thickness, big enough to read and small enough to frame.
- **Surface components lift off their face** — a top part up clear of the whole fan, a bottom part
  down below the datum. Pure Z.
- **Embedded components come out of their cavity along Z FIRST, then spread aside** — an `ExplodePath`
  DOGLEG. Its first leg is pure ±n (straight out of the cavity), and its FINAL offset carries a
  lateral step so the die does not tunnel straight up through the layers directly above it (a
  diagonal reads as "insert at an angle", the recorded explode-path lesson). This is the reason an
  embedded offset is the one that is NOT pure Z: the lateral leg IS the dogleg, and a pure-Z endpoint
  with a "straight up first" leg is provably a collinear no-op. Layers and surface components fan
  purely axially; only embedded parts carry the lateral spread.

**Factor 0 is the assembled board, bit-identically.** The component occurrences are built exactly as
`ToAssembly` builds them (same `OccurrenceFrame`, same `PartTransform`), and `ExplodeDisplacement(0)`
is exactly zero, so at factor 0 every component's world transform is bit-identical between the two
assemblies — the animation opens FROM the board and closes back TO it. The instance count and order
are independent of the factor (matrices only), and the whole thing is deterministic (bit-identical
offsets on repeated builds; no ordering that is not a function of the model). A board built the
copper-only way (null `LayerStackup`) is refused BY NAME — there is no modelled dielectric to slice,
so `ToAssembly` is the single-slab answer — as is a negative spacing. Explode offsets are a
VIEW/analysis concern, so they are NOT baked into the layout file (the save→load→save byte fixed
point is untouched, matching how `DrcRuleSet` is kept out); `ToExplodedAssembly` recomputes them.
Docs: `examples/ecad-pcb.md` (with a committed APNG of a 4-layer board fanning open, the buried die
rising last out of its cavity once the layers above have cleared).

### Vias and the net-connectivity engine (`Via`, `PcbConnectivity`)

Vias are the precursor to autorouting, and the net-connectivity engine underneath is the thing an
autorouter reuses. A `Via` is a net-carrying cross-layer connection at `(x, y)` spanning the copper
layers `[From, To]`, with a drill and an annular pad diameter. **The via TYPE is DERIVED from the
span, not stored twice** (the `SheetBendSection`-carries-no-K discipline): `ViaGeometry.Resolve`
against the board's stackup decides `Through` (outer face to outer face) / `Blind` (one outer face)
/ `Buried` (neither face) / `Microvia`, and **the precedence is the finding** — THROUGH is decided
first (both outer), then MICROVIA (a single dielectric hop, adjacent copper layers) takes precedence
over blind/buried, because a microvia is a physically distinct single-hop via however its ends fall.
There is no explicit `Microvia` a caller can assert into being across non-adjacent layers; the type
is always derived, and the `AddVia(..., require:)` overload validates an INTENT against the derived
type and refuses a mismatch BY NAME (the named "non-adjacent-for-microvia" refusal), a validation
discarded rather than a second stored copy.

**Via copper is fed into `PcbCopperModel`, so most of the via DRC is free.** A via places an
**annular pad** (a disc of the pad diameter with the drill removed — a `CurvedRegion2d` whose one
hole is the drill circle, so its `Area` is exactly `π(pad² − drill²)/4`) on EVERY copper layer it
touches, tagged with its net, plus one `DrilledHole`. So the general clearance rule already spaces a
via pad against different-net copper (a via pad is copper), the drill-to-copper rule already spaces a
via drill against different-net copper (a via drill is a drilled hole), the annular-ring rule already
checks the via (a via is a drilled pad, the drill carrying the pad diameter), copper-to-edge reaches
it, and a same-net via touching its own copper is the intended connection and is never flagged. The
ONE genuinely new rule is `ViaToVia` — the minimum WEB between two drilled holes, applied to all via
pairs regardless of net (a manufacturing spacing; different-net via PAD clearance is already the
copper-clearance rule) — measured by the SAME grow-and-intersect the clearance rule uses (grow each
drill disc by half the minimum, require them disjoint), so a web AT the limit passes robustly the way
a tangent contact is no region. `MinViaToVia` rides `DrcRuleSet` as a value with a default (NOT on
the positional constructor), so a stage-4 caller building the six-argument way is unaffected.

**`PcbConnectivity` is the heart, and it CLOSES the multilayer caveat.** It builds a per-net graph
over the net's copper features (component pads, via pads, and — later — traces): two features join
when they TOUCH on the same layer (an exact `CurvedRegion2dBoolean.Intersection`, no tolerance — the
same query the DRC calls a SHORT between different nets) OR are the two ends of a PLATED BARREL. The
barrel rule is the finding that needed no new machinery: **features sharing a source across layers
are one plated connector** — a via (its annular pads on every touched layer) OR a through-hole pad
(its per-layer copies) obey the identical rule, so no flag on `CopperFeature` is needed. A net is
CONNECTED when all its COMPONENT PADS (not the via pads — those are connectors, not terminals, so a
floating/redundant via never makes a connected net read unconnected) lie in one connected component.
`PcbDrc.Ratsnest` now DELEGATES to this engine, so the stage-4b caveat ("a net whose pads sit on
different layers reads as an unrouted ratsnest until routing") is answered by geometry: a via that
touches each pad is a real connection. **The old ratsnest was a 2D-projection union across layers**
(right for a through-hole pad by luck, wrong in general — it would connect two SMD pads on different
layers at the same (x, y) without a via); the layer-aware graph is strictly more correct, and it is
bit-compatible for every no-via board (through-hole barrels and same-layer touch reproduce the old
answer, verified by the whole stage-4 suite passing). A via on the WRONG net does not connect it
(only same-net features are nodes); a via elsewhere does not connect distant same-layer pads unless
its copper reaches them.

**Vias are LAYOUT TRUTH, so they round-trip in the layout file** (unlike the view/analysis
`DrcRuleSet` and explode offsets, which do not): a `vias` array write-only-when-stated, so a via-free
layout is byte-identical and a via one is a `save → load → save` fixed point. Verified higher than
usual because ECAD fails plausibly: the type derived for every span (order-independent); a through via
touching all N copper layers and a buried via only its inner span; the annular pad area exact to the
closed form on every touched layer; every refusal by name; the connectivity headline from BOTH sides
(with a via CONNECTED and the ratsnest empty, without it UNROUTED and named); wrong-net / floating /
third-location vias not connecting; connectivity as EXACT region touch (a tangent point does not
join, an overlap does); the via annular-ring / via-to-copper / via-to-via rules from both sides of
their limit; a same-net via on its own copper never flagged; scale invariance and determinism. **v1
does not cut the via drill into the 3D plate B-Rep** — vias are modelled in the copper /
connectivity / DRC (the plate stays the mechanical outline + mounting holes, bit-identical), which is
the safe scope and satisfies every layer-touching oracle; drilling the plate is a later refinement.
Docs: `examples/ecad-pcb.md`.

### Stage 5 — the autorouter (`PcbRouter`, `PcbTrace`)

The genuinely hard stage, and the one where the verification culture earns its keep: **an autorouter
that connects while violating clearance is the classic silent failure**, so the router is built so
that outcome *cannot* happen. `PcbRouter.Route(layout, rules, options)` → `RoutedResult` is a
DRC-aware grid/maze A* router — a uniform routing grid `(x, y, layer)`, through-vias to change
layers, 2-pin MST decomposition of multi-pin nets, and rip-up-and-reroute.

**The load-bearing rule is that the grid is an ACCELERATOR and the exact DRC is the SOURCE OF
TRUTH.** A candidate route is committed only after the exact `PcbDrc.Violates` (plus the drill and
via rules the incremental seam does not carry — drill-to-copper, via-to-via, mirroring `PcbDrc.Check`
so the two cannot disagree) confirms it adds NO violation to the board. So the grid may over- or
under-block a cell — that only costs a detour or a commit-time rejection — but a grid rounding error
can never produce a violating trace, because the trace is not committed until the exact check passes.
If the exact check disagrees with the grid, the exact check wins and the candidate is rejected, not
shipped. This is what makes the two mandatory guarantees structural rather than hoped-for: every
committed trace is DRC-clean by construction, so the PARTIAL result of a board it cannot fully route
is still clean, and a net it cannot route cleanly is reported UNROUTABLE by name.

**A trace is the DRC's own clearance model.** `PcbTrace` is a net's routed copper on one layer — a
polyline centre-line of a width — whose copper region is the polyline's exact STROKE
(`CurvedRegion2dOffset.Stroke`, round caps and round joins). That is the Minkowski sum of the
centre-line with a disc of radius `width/2`, which is *precisely* the region the clearance rule grows
against, so a trace built here and the rule it is checked with cannot disagree; round joins also mean
the copper carries no sharp corner, so a routed trace passes the acid-trap rule with nothing arranged.
The trace feeds `PcbCopperModel` and `PcbConnectivity`, which reads a trace (and a via) as a
CONNECTOR, not a terminal — so a net is CONNECTED when its component PADS end up in one copper
component, which is what makes "the router connected the net" a geometric fact rather than a claim.

**Rip-up is negotiated congestion, not reorder-restart** — the choice was measured. A first design
re-routed all nets in a new order when a net failed, and a search over thousands of small congested
boards found it almost never completes a board a greedy pass cannot, because A*'s own per-net detour
already resolves a 2-net conflict (the loser detours) and where it cannot, a reorder cannot either.
Rip-up-the-blocker does complete them: a blocked net is routed ACROSS the traces that block it (at a
high cost so it prefers clean cells), those traces are ripped up and re-queued, and the net is
re-routed cleanly without them — and the ripped nets find new routes around it. The blocker traces
are SOFT obstacles in the grid (a per-cell bitmask of committed nets); the base pads and the board
edge are HARD (never rippable). Each rip-up is bounded per net, so a truly boxed-in net terminates and
is reported unroutable rather than looping.

**Verified higher than usual because ECAD fails plausibly** (`PcbRouterTests`): a 2-pin net connects
and passes DRC with the ratsnest empty; several parallel nets all route clean; a net that MUST cross
another gets a via and both come out clean and connected (and WITHOUT vias the crossing is reported
unroutable by name, the rest clean); a congested board where a greedy pass leaves a net unrouted is
COMPLETED by rip-up (both clean, `RipUps ≥ 1`) — the measured demonstration that rip-up earns its
place; a pin walled in by other-net copper is reported unroutable by name with the rest routed and
clean; a dense knot of crossing nets on one layer cannot fully route, and whatever the router manages
is still DRC-clean with the failures named (the never-a-silent-violation guarantee); two runs are
byte-identical (determinism); a board with nothing to route returns byte-identical (`Save()`); routed
traces round-trip through `save → load → save` as a fixed point; and the whole thing is scale-invariant
at 1e-3× and 1e3×. Traces are LAYOUT TRUTH, so a `traces` array rides in the layout file
write-only-when-stated (an un-routed layout stays byte-identical).

**v1 scope, each boundary stated**: through-vias only (spanning all copper layers — always valid,
exactly right for a 2-layer board; blind/buried/microvia routing is a later stage); 45°/90° grid;
rip-up-reroute; 2-pin MST decomposition. NOT in v1: topological / shove / push-and-route; differential
pairs; teardrops; and cavity walls as routing obstacles. Docs: `examples/ecad-routing.md`.

**Length matching (serpentine tuning)** landed on top of the router (`LengthMatch`). A routed trace
is lengthened to a target by REPLACING its longest segment with a serpentine — a comb of `N`
rectangular BUMPS, each rising at 90° from the baseline, running flat, and dropping back. The bump
geometry is the load-bearing choice: a naive up-then-down SQUARE WAVE puts a 180° hairpin at each
tooth tip, which the acute-angle rule and the pinched neck between anti-parallel copper rightly flag
(measured — the square-wave version reached only 3.5 mm of an 8 mm ask before violating), whereas a
90°-cornered bump with a baseline between bumps is DRC-clean and a round-joined 90° corner passes the
acute rule with nothing arranged. Each bump adds exactly `2·A` of centre-line length, so `N` bumps add
`2·N·A` and setting `A = (target − current) / (2N)` hits the target BY CONSTRUCTION — verified by
MEASURING the built polyline, never by a claimed number. `N` is MAXIMISED (subject to a pitch floor of
four trace widths, so a bump's two vertical sides clear at `cell/2 ≥ 2·width`), because more teeth
means a smaller amplitude and hence the DRC-friendliest comb; the whole tuned trace is then committed
only after `PcbDrc.Violates` certifies it adds no clearance violation against the board's OTHER copper
(the router's exact-DRC-is-truth rule, so a tuned trace is DRC-clean or the tuning is refused). The
trace's endpoints and net never move — only the middle lengthens — so connectivity is unchanged, and
the tuner does NOT mutate the layout (it returns the tuned trace; `PcbLayout.ReplaceTrace` applies it,
the deliberate-act rule). `MatchGroup` tunes a set to the longest member, each checked against the
others' current (tuned-so-far) copper so two serpentines from one group cannot collide unnoticed.
Outcomes are named rather than fudged: `Refused` (a target shorter than the current — a serpentine
only adds), `Unchanged` (already within tolerance), and `Untunable` (no DRC-clean room, reporting how
much it COULD add via a bisection on the largest clean amplitude). v1 scope: one uniform comb on the
one longest segment, teeth to alternating sides; filed — spreading over several segments, teeth to
only the OPEN side, ripping up a neighbour to make room, and differential-pair coupled tuning
(matching within a pair while holding the gap). Docs: `examples/ecad-routing.md`.

**Differential pairs — analysis + skew matching, NOT coupled routing** (`DiffPair`, `DiffPairs`). A
`DiffPair` carries the two properties a diff pair IS judged by — a target coupling gap and a skew
tolerance — and `DiffPairs.Check` MEASURES both over a routed layout: it resolves each net to its
single trace (reporting *not checkable* by name when a net is unrouted or split, never throwing), and
reports the two lengths, the skew `|P − N|`, the median nearest-neighbour gap, and the **coupled
fraction** — the arc-length share of the + trace whose nearest point on the − trace lies within the
gap tolerance of the target gap (1.0 for a perfectly parallel pair, driven low the moment the pair is
judged against the WRONG gap, which is what makes the measurement a real test rather than a
tautology). Coupling is a point-to-polyline distance sampled by arc length, so it needs no boolean.
`DiffPairs.MatchSkew` equalises the halves by handing the two traces to `LengthMatch.MatchGroup`, so
the skew serpentine is DRC-gated for free and cannot collide with its partner. The deliberate v1
boundary is that this ANALYSES and skew-tunes a pair that is already routed — COUPLED routing (routing
the two together while holding the gap) is the hard research stage and is filed, as are per-segment
skew tuning that preserves coupling and impedance from the stackup. Docs: `examples/ecad-routing.md`.

**Shove (push-and-route) insertion** (`ShoveRouter`). Where a direct trace is blocked, a DETOUR
router routes the new trace around; a SHOVE router pushes the blocker aside and keeps the new trace
straight — which is the whole point, since the direct path is the short one. `ShoveRouter.Insert`
places a new trace and JOGS any parallel blocker out of its corridor: the stretch of the blocker
alongside the new trace's longest run is offset perpendicular to the target clearance with a ramp in
and out, while the blocker's ENDPOINTS stay put — so its pads and its connectivity never move, and the
new trace itself does not move at all. The commit rule is the router's own exact-DRC-is-truth: the
WHOLE candidate (the new trace plus every shoved blocker) is checked with `PcbDrc.Check` and committed
only if clean, so a shove can never ship a clearance violation — a shove that would push a blocker into
a THIRD trace fails that check and is `Refused` rather than propagated (v1 does NOT cascade). Two
numerical points earned by the geometry: the jog is a rectangular offset with 90° ramps (a round-joined
90° corner passes the acute rule, the length-match lesson), and the blocker is pushed a HALF TRACE
WIDTH past the bare clearance because the round-join BULGE at the new trace's corners — where its
lead-ins converge on the run — reaches toward the blocker, so pushing exactly onto the clearance limit
measured 0.283 against a 0.3 minimum (MEASURED, then fixed by the margin rather than guessed). v1
shoves a single straight parallel blocker per obstacle that extends past the run far enough to ramp
(anything else — a bent blocker, a non-parallel one, one too short — is refused BY NAME); filed:
cascading shoves, bent blockers, and full push-and-route inside the maze search. Verified: a board
where the direct trace is DRC-blocked is made clean by shoving the blocker aside (with the mutation —
without the shove the same trace violates), the blocker's endpoints are unmoved and both nets stay
connected, the no-cascade guard refuses a shove that would collide with a third trace, and the bent /
no-shove-needed / determinism cases. Docs: `examples/ecad-routing.md`.

**Coupled routing of a differential pair** (`CoupledRouter`). Where `DiffPairs` ANALYSES a pair that
is already routed, this ROUTES the two nets together: given a shared centre-line, the pair is its two
parallel offsets at ±`gap/2`, so it holds the gap EXACTLY along the whole run BY CONSTRUCTION — a pair
of parallel offset curves stays a constant distance apart, mitred through every bend — which is what
makes the result well-coupled with no search, verified by feeding it straight back through
`DiffPairs.Check` (well-coupled, and low-skew on a straight run). The offset is a mitred perpendicular
polyline offset (interior vertices are the intersection of the two offset segments, a butt join where
they are parallel), so a straight pair is perfectly length-matched while a BENT pair picks up the
inside-corner skew a real diff pair has — which is exactly what `DiffPairs.MatchSkew` then tunes out,
the two features composing. The whole pair is committed only if `PcbDrc.Check` of the board with both
traces added is clean, checked DIFF-PAIR-AWARE (below) so a TIGHT pair routes. The deliberate v1
boundary is one and it is stated: the caller supplies the centre-line (routing it is a FAT-NET maze,
filed); the gap must still exceed the trace width or the two traces merge (refused by name). Verified:
a straight coupled route is well-coupled + low-skew + DRC-clean + both nets connected, a bent one
routes clean and stays coupled, the gap-too-small and centre-too-close refusals, and determinism.
Docs: `examples/ecad-routing.md`.

**Diff-pair-aware DRC** (`DrcRuleSet.MinDiffPairGap`; `PcbDrc.Check`/`Violates` take an optional
`IReadOnlyList<DiffPair>`). A controlled-impedance pair runs at an intra-pair gap tighter than the
general copper clearance, and the general clearance rule would flag that — so the two nets of a
differential pair EXPLICITLY named to the DRC are checked at the tighter `MinDiffPairGap` instead. The
decision is that WHICH nets pair is a DESIGN fact (the `DiffPair` list, passed to the check) while the
tighter floor is a FABRICATOR-capability number (a value-with-default on `DrcRuleSet`, ⚠ verify, off
the positional constructor and scaled/ForIpcClass'd like `MinViaToVia`) — the same split the whole
`DrcRuleSet` rests on. It is SURGICAL: the clearance for a pair comes from one `ClearanceFor` helper
that returns `MinDiffPairGap` for a named pair (either order) and `MinCopperClearance` otherwise, so
**with no pairs named it is the general clearance for every pair and the DRC is bit-identical to a
stage-4 run** (the null path). Three things it deliberately does NOT relax: the exemption is
INTRA-PAIR only (each half still clears every OTHER net, and the OTHER pair, at the general clearance,
so naming a pair does not make either net "special"); a SHORT is decided independently of the
clearance value, so the two halves touching is still a short; and a gap below even the diff-pair floor
still flags (the fab's own minimum). The incremental `Violates` seam is diff-pair-aware too, so a
future maze router can route a tight pair the same way `CoupledRouter` does. Verified by the mutation
that proves the exemption earns its place — the SAME tight geometry flags un-named and passes when
named — plus: the exemption reaches nothing but the pair (a third net too close to one half still
flags), a short within a named pair is still a short, a gap below the floor still flags at the
tighter limit, null/empty/an-unrelated-pair are all the stage-4 run, and `CoupledRouter` routes a
tight pair the general clearance would refuse. Docs: `examples/ecad-routing.md`.

### Drawing the schematic sheet (`SchematicSheet`, `SchematicDrawing`)

The human-readable VIEW of a schematic — placed symbols, orthogonal wires, junction dots, net
labels, reference designators + values, a border and a title block, to SVG/DXF/PDF — and it
**replaces `Netlist.ToText()`**. It is deliberately a VIEW: a `SchematicDrawing` is a
**deterministic function of the graph and the placement**, so it cannot disagree with the
netlist (the one-declaration rule), and the same schematic and placement produce byte-identical
SVG. It consumes the drawing writers Modeling already carries (`SvgDrawing`/`DxfDocument`/
`PdfDrawing`, the same pens and line classes the mechanical `DrawingSheet` writes through), but
is NOT a `DrawingSheet`: a schematic is line work placed by the caller, not an orthographic
projection of 3D geometry, so it owns its own 2D sheet type rather than forcing symbols through
a projection machine.

**The verification is the deliverable and reads the drawn PRIMITIVES, not the router's
bookkeeping.** `SchematicDrawing.Verify()` reconstructs the connectivity from the wire segments,
the pin anchors and the net labels — union-find over the wire graph, plus label equality — and
asserts BOTH directions: every signal/stub net's pins are joined (by a wire path or a shared
label), and no two pins on different nets are joined. A drawing that omitted a connection the
netlist has, or invented one it does not, fails; that is what makes the sheet a faithful view
of the graph rather than a picture that might lie. A pin's world anchor is where its wire lands,
computed from an EXACT quarter-turn pose (a sign swap, never a `cos`), so the anchor coincides
with the wire endpoint to the bit.

**Three decisions carry the router.** A **rail is drawn as LABELS, not a wire**: a `GND` net is
not one long wire across the sheet, so a net with a `Power`/`Ground` pin (or a recognised rail
name, or more pins than a fanout threshold) is drawn as a net-name label at each pin, and
labelled pins join by the shared label — which is what keeps a label and a wire from
cross-joining two different nets (a labelled pin never takes the wire branch). A **wire is
orthogonal**: two pins take an L, three or more a horizontal trunk at the pins' median height
with a vertical stub from each pin, so an interior stub makes a T the **junction dot** renderer
marks (multiplicity ≥ 3 of wire endpoints — a mid-segment CROSSING of two nets is not a
junction, the schematic convention). And a **junction dot is a filled disc in SVG/PDF but an
outline circle in DXF**: the stroke-only writers get a filled mark from a round-capped
zero-length stroke, which DXF (no line width, no fill in the 2D writer) cannot spell, so the
marker differs per format — exactly the kind of spelling difference the drawing writers already
carry.

**Refused by name at construction**: a component with no `Symbol` (it cannot be drawn), a net
that connects a pin the component's symbol does not draw (the "pin with no anchor" case — the
symbol and the netlist disagree about the part's pins, a `PinIdentity` failure), and a component
the placement does not cover. **Placement is hand-done in v1** — a `SchematicPlacement` value,
or `Grid(...)`, a deterministic grid stand-in clearly labelled as such; a real auto-placer
that produces a *good* layout is a different problem and is deliberately not invented, and the
v1 wire router may cross a symbol or another net (an obstacle-avoiding route is likewise
separate).

**Buses are a caller-declared LAYER, and the load-bearing decision is that a bus connects
NOTHING** — the mirror of the KiCad bus IMPORT's finding, run the other way: on one sheet a
ripped tap's net is its OWN local label, so a bus is just how a bundle of already-connected
member nets is DRAWN. A `SchematicBus` (passed to the
sheet with `buses:`) is a base `Name` + a vector range `[first..last]` (members `NAME`+i, the
KiCad `DATA[m..n]` notation, reversed ranges honoured in the drawn direction), a thick bundle
`Path` and diagonal `Entries` (`SchematicBusEntry`, the 45° rips) — caller-placed, never
auto-routed, exactly as `SchematicPlacement` gives symbol poses. It draws on a new `bus` layer:
the wire with a WIDER pen (`SchematicSheetOptions.BusWireWidth`, default 0.8 mm against a wire's
0.5), the entries at the wire pen, and the vector label as text — following the junction-dot
precedent (a dedicated `DrawnBus` list drawn by the writers with their own pen, not folded into
the generic per-layer loop). **The bus line-work is kept OUT of the wire graph** (`_wires`), so
`DrawnConnectivity`/`Verify()` never see it — a bus wire touching two member wires cannot merge
their nets — and the same sheet drawn with a bus reconstructs EXACTLY the same nets as the
plain-wire sheet (the member nets do the connecting; the bus is only how they are drawn as a
bundle). Buses are OPT-IN, so a sheet declaring none is BYTE-IDENTICAL (asserted). **Group / alias
bundles draw too**: `SchematicBus.Group(label, members, path, …)` takes the drawn label as arbitrary
text (`"{SDA SCL}"`, an alias name) with the member names stated EXPLICITLY (the drawing does not parse
the label — what the group means is the caller's declaration, the import side's rule mirrored), riding
the same thick-pen/entries/Verify-exempt machinery as a vector bus; the vector constructor is unchanged
and byte-identical. So buses are complete on BOTH sides — the import reads vectors/groups/aliases
single-sheet and across sheets, and the sheet draws vector and group/alias bundles. Docs:
`examples/ecad-schematic-sheet.md`.

### Stage 6 — Gerber (RS-274X) + Excellon fabrication export (`PcbGerberExport`)

The fab output that makes a routed board manufacturable — one Gerber per copper layer, a
board-outline Gerber, and an Excellon drill program. `PcbGerberExport.Write(layout, dir)` writes the
set (and reports what it wrote); `Generate` returns it as text. `Write(layout, dir, includeNetlist:
true)` also drops the IPC-D-356A netlist (`<name>.ipc`) beside the Gerber set for the board house's
net-compare — opt-in, so with it off the Gerber / drill files are byte-identical (the netlist adds a
file, it does not touch the copper). **Gerber X2** rides opt-in on the same call (`includeX2: true`):
each copper object gains a `%TO.N,<net>*%` object attribute (the net-compare datum, read straight from
the Gerbers), and EVERY Gerber gains a `%TF.GenerationSoftware%` attribute and its `%TF.FileFunction%`
role — `Copper,L<n>,<side>` for a copper layer (stackup position/side), `Soldermask,<side>` /
`Legend,<side>` / `SolderPaste,<side>` for the mask / silk / paste (the side read off the stackup's top
copper), and `Profile,NP` for the non-plated board outline — so the WHOLE package is self-describing and
its per-file roles match the `.gbrjob` manifest's. X2 changes no geometry, so the oracle is that
stripping the attribute lines recovers the plain Gerber byte-for-byte (on the non-copper files too), off
is byte-identical, and the reader ignores X2 attributes (metadata, not geometry) so an X2 file (copper OR
mask) round-trips its geometry exactly. The per-Gerber role was threaded through `MaskLayer` / `PasteLayer`
/ `Silkscreen` / `Outline` (a `NonCopperFileFunction` helper reading the top-copper side), so the same
`GerberBuilder` that already emitted the copper role now emits every layer's — no new emission path. The
**`.gbrjob` job file** rides opt-in on the `Write` disk path (`includeJobFile:
true`): the JSON manifest a fab reads to identify the set — board size/thickness, copper-layer count,
surface finish (from the `PcbFabricationSpec`), and every Gerber file with its `FileFunction` (`GerberJobFile`,
a pure formatter; the roles gathered one level up from the whole set, using the SAME role strings the
Gerber content now carries). It is DETERMINISTIC (the two
clock/random-salted fields the spec allows, `CreationDate` and the project `GUID`, are OMITTED — the same
`PdfDrawing`-no-`/Info`-date reasoning), so it is a byte fixed point, and the oracle is that every file it
lists was actually written. Each **component pad** flash on a copper layer also carries the X2
`%TO.C,<refdes>*%` and `%TO.P,<refdes>,<pad>*%` ASSEMBLY attributes, tying the copper back to its
component pin (an AOI / placement-verification datum). The pad identity is looked up by the feature's
SOURCE (`"R1.1"`, which IS `PlacedPad.Name` — no string parsing), so a via / trace / pour, which carries
no such source, gets none; the `%TO.C` / `%TO.P` lifecycle mirrors `%TO.N`'s (set on change, `%TD` on
clear), and a rounded / rotated pad that region-fills carries them on its contour just as a flashed pad
does. Each copper APERTURE also declares its `%TA.AperFunction` role (`SMDPad,CuDef` / `ComponentPad` for
a component pad by its kind, `ViaPad`, `Conductor` for a trace, `Profile` for the outline), which changed
the aperture DEDUP to key on (shape, function): a via pad and a trace of the SAME diameter but different
role split into two D-codes under X2, while OFF the function collapses so the key reduces to the shape and
the dedup — hence the whole file — is byte-identical. The discriminating test builds exactly that
collision (a via pad and a trace both Ø0.3) and asserts one `%ADD` off, two on. A pour region-FILLS so it
has no aperture and no `%TA`. A mask WINDOW and a paste APERTURE over a component pad also carry the
`%TO.C` / `%TO.P` assembly datum, looked up by the opening's own `Source` (`MaskOpening`/`PasteAperture`
already carry it) — what an AOI / SPI tool reads — while the writers stay layer-clean by taking the pad
identity as a plain tuple rather than an ECAD type; a via window carries none. A silk refdes / value /
courtyard stroke also carries the `%TO.C` of the component it marks (`SilkStroke.Source` IS the refdes,
so no lookup — an assembly-documentation datum), a generic Mark (a fiducial / logo, belonging to no
component) carrying none; `.C` was decoupled from `.P` in the writer (a `GObject.Component` field beside
`Pad`, with `.C` = `Component ?? Pad?.Reference`) so a silk stroke gets `.C` without a spurious `.P`. So
the X2 OBJECT attributes are now complete across every layer (copper `.N`/`.C`/`.P`, mask/paste `.C`/`.P`,
silk `.C`). Filed: `%TA` aperture functions on the mask / paste (a non-copper aperture-function is less
standard). Pads flash (`D03`),
traces draw
(`D01`/`D02` with a round aperture, whose swept stroke IS the copper model's trace region), via pads
flash as solid discs, and anything else — a rotated pad, a copper pour — is a region fill
(`G36`/`G37`), exact for any shape.

**The oracle is the TWIN-DECODER round trip** — the repo's rule, that the geometry must survive the
round trip and not merely a structural validator. Alongside the writer is a matching reader
(`GerberReader.Read`, `ExcellonReader.Read`); the copper written is parsed BACK and the recovered
copper must equal the copper model's on each layer to the region-area grade — by area AND by a
symmetric-difference check through the DRC's own `CurvedRegion2dBoolean` — verified on both a
hand-built and an AUTOROUTED board, while the decoded drill hits equal the board's holes exactly.

**The imaging order is the whole design, and it reproduces a UNION exactly.** The copper on a layer
is a UNION of feature regions, so a via drill (or a pour hole) is a hole in the copper ONLY where
nothing covers it. A via under a routing trace, or a via directly under a pad (a via-in-pad), is
FILLED — the model's union has no hole there. So the writer lays all the SOLID copper down (dark),
then clears exactly the HOLES OF THE FINAL UNION (which the caller already computed): a via disc
becomes its annular ring only where the drill is genuinely exposed, and a covered drill stays solid,
matching the model set for set. This is the correct, always-faithful way to reproduce a union with
Gerber's order-dependent dark/clear polarity — the naive "clear every via drill" opens a hole the
model fills at a via-in-pad, which the crossing-board fixture (whose SIG via lands under a pad, and
whose other via is covered by the trace ending on it) demonstrates: it has ZERO exposed drills, so a
correct exporter emits no clear on it at all. **A copper POUR sharpened this rule**: a pour's
clearance hole around an OTHER-net pad contains that pad as a copper ISLAND, so the true air is a
RING, not the whole hole. Clearing the whole hole (the naive "the union region's holes") erases the
pad the writer drew (measured: a poured board's Gerber came back one anti-pad's worth of copper short
per other-net pad). So the air the writer clears is the union's holes MINUS the copper — the ring —
and the writer clears the ring's OUTER contour and re-DARKENS its inner loops (the island), restoring
the pad; a via drill (no island) is the same computation with the island empty, so a non-poured
board's Gerber is byte-identical.

**The coordinate format is derived from the board's own magnitudes** (`GerberFormat.For`), so the
resolution stays a fixed fraction of the model at any scale — the epsilon-ladder property, in a file
format. Each `%FS` digit field is a SINGLE digit, so the two counts are anti-correlated (large
coordinates → fewer fractional digits) and their sum stays ≈ 11–12, well inside a long's
exact-integer range; a first version allowed a two-digit fractional count and overflowed the
single-digit field, decoding a 1e-3-scale board's coordinates 10^5 too large (the recovered area came
back at 48000 against a model 5.28e-6 — a 10-orders-of-magnitude tell that reads as a format bug, not
a tolerance one). Shape recognition is GEOMETRIC — a pad's region is read for a disc / axis-aligned
rectangle / axis-aligned obround and flashed, a rotated one falls to a faithful region fill — so a
placement rotation never has to be threaded through, and correctness never depends on recognition
(an unrecognised feature region-fills, exact for any shape). Refused BY NAME: a Bézier copper
boundary (RS-274X region contours carry only lines and circular arcs), and — in the reader, the
round-trip oracle scoped to what the writer emits — a truncated file, a missing format/unit spec, or
an aperture macro.

**Solder mask and silkscreen complete the manufacturable set** (`PcbMask`/`PcbMaskSettings`,
`PcbSilkscreen`/`PcbSilkscreenSettings`/`SilkFont`; both emitted through the SAME `GerberWriter` and
verified by the SAME twin-decoder oracle). The mask covers the whole board EXCEPT a window over each
solderable pad, and **the window is EXACT — the pad grown by a stated EXPANSION** (`CurvedRegion2dOffset`,
round joins): a round pad's window is a disc of radius `r + e`, so its area is `π(r+e)²` to ~1e-12
relative (the offset of a disc is an exact disc, not the region grade), and a rectangular pad's is a
rounded rectangle. **Which features get a window is read from the copper model's own tags** rather than
re-decided — a trace and a pour stay covered, a via is tented or opened by policy — so the mask cannot
disagree with the copper about what a pad is, and a through-hole pad (on every layer) gets a window on
both outer faces. **The Gerber convention is the standard positive-openings form** (as KiCad/Eagle):
the mask images the WINDOWS as dark, so a decoded mask Gerber recovers the openings, not the coverage —
which is what makes the round-trip oracle the same one the copper uses. **Silk is line-work** because a
Gerber has no text primitive: a reference designator (and optionally a value) is drawn in a single-stroke
vector font (`SilkFont`, transcribed from the viewer's stroke font since the ECAD side cannot reference
the viewer) and a body/courtyard outline from the pads' bounding box, all drawn with a round aperture
EXACTLY as a trace draws, so silk strokes back through the reader too. **Silk on a solderable pad is a
real defect**, so `PcbSilkscreen.OverExposedCopper(mask)` intersects the silk footprint (stroked to its
pen width) with the mask windows and reports every overlap BY NAME (the silk element and the pad) — a
check the caller runs, like the DRC, not a throw. **The mask/silk are ADDITIVE**: derived from the
copper model and the placements without touching the copper path, so the copper Gerbers, outline and
drill are byte-identical whether or not they are present (asserted); their settings ride on the layout
as LAYOUT TRUTH (`PcbLayout.MaskSettings`/`SilkscreenSettings`, write-only-when-stated, a save→load→save
fixed point, a layout that states none byte-identical to a pre-fabrication one). Refused BY NAME: a
mask/silk on a non-outer layer, a mask window entirely off the board (a pad placed off it), and a
negative mask expansion (a mask-defined pad).

**Solder paste (the stencil) completes the fab set** (`PcbPaste`/`PcbPasteSettings`/`PasteAperture`;
`GerberWriter.PasteLayer`; `FabricationOutput.PasteLayers`, `GerberExportResult.PasteLayerCount`), so a
routed board can be fully manufactured AND reflow-assembled. It is the mask's SIBLING — the same
`GerberWriter`, the same twin-decoder oracle, the same offset-of-a-pad machinery — with two deliberate
differences that ARE the design. **(1) It covers SMD pads ONLY** (the SMD-only rule, whose classic bug
is pasting a through-hole pad): a through-hole/plated pad is wave- or hand-soldered and a via would wick
solder down its barrel, so both get NO aperture. Which pads are SMD is read from the copper model exactly
as the mask reads what a pad IS — a COMPONENT pad (not a trace, pour or via) that carries **no drill**
(its source is not among `PcbCopperModel.Drills`), so a through-hole pad, which the mask *does* window,
is excluded here by its drill and a via by its source, with no via policy consulted at all (unlike the
mask, paste never opens a via). **(2) The default expansion is slightly NEGATIVE** — `-0.05 mm`, a stencil
aperture is a hair *smaller* than the pad to control the paste VOLUME (a paste brick as wide as the pad
bridges and slumps) — so paste ALLOWS the negative expansion the mask refuses (a shrink is the point,
`CurvedRegion2dOffset.Offset`'s deflate path), a round pad's aperture is a disc of radius `r + e < r`
(area `π(r+e)²` to ~1e-12), a rectangular pad's a smaller rounded rectangle, and a negative expansion
large enough to consume a pad simply leaves no aperture. The Gerber images the APERTURES dark (the mask's
positive-openings convention — the stencil is cut where the Gerber is dark), so it strokes back through
the same reader. Paste is ADDITIVE (the copper/mask/silk Gerbers are byte-identical with or without it,
asserted), LAYOUT TRUTH (`PcbLayout.PasteSettings`, write-only-when-stated, a save→load→save fixed point,
a no-paste layout byte-identical to before), and refused BY NAME on a non-outer layer or with an aperture
entirely off the board — but NOT for a negative expansion, the one refusal the mask keeps and paste drops.
Verified the fab house way: an aperture equals the SMD pad grown by the expansion (area `π(r+e)²` to 1e-12,
region grade via symmetric difference; a negative expansion measurably shrinks it and zero is the pad
exactly), the through-hole and via pads carry NO aperture (the SMD-only assertion with teeth), the paste
Gerber round-trips, the full set writes/re-reads (`-Top_Paste.gbr`/`-Bottom_Paste.gbr`), an all-through-hole
board writes a valid EMPTY paste, and determinism.

**Step (multi-level) stencils landed** (`PasteStencil`/`PasteStep`/`PasteLevelSelector`): a real board
with mixed geometry needs a foil milled to DIFFERENT thicknesses in different zones — a fine-pitch part
(a 0.4 mm QFN) wants a thin foil / reduced aperture to hold the paste volume, a large thermal pad or
connector wants a thick foil / more paste — and since each thickness is a separate milling depth, the fab
consumes ONE PASTE GERBER PER LEVEL. A `PasteStencil` is an ordered list of `PasteStep` LEVELS, each a foil
thickness (which NAMES its Gerber file, `_100um`), an aperture expansion, and a `PasteLevelSelector`. **The
load-bearing decision is that the foil thickness is DELIBERATELY absent from the aperture geometry** — a
level's aperture is the pad grown by that level's EXPANSION through the SAME exact `CurvedRegion2dOffset`
machinery the single stencil uses, so a level only changes WHICH expansion and never HOW an aperture is
computed; the thickness is the level's IDENTITY (its filename), nothing more, which is exactly what keeps
the aperture-equals-pad-plus-expansion oracle unchanged in both modes (the K-factor-is-absent-from-the-bend
separation, one domain over). **Every SMD pad is on EXACTLY ONE level (a partition)** — a pad is assigned to
the FIRST step whose selector covers it (overlapping zones resolve by first-match, a STATED rule not an
error), else the DEFAULT level (a step with no selector, which every stencil must declare, so no pad is
ever printed on no level); so no pad is printed twice or dropped, and the union of the levels equals the
flat single-stencil pad set (asserted by count conservation and set equality). The SMD-only rule survives
on every level (a through-hole pad and a via get no aperture on ANY level — a step stencil must not start
pasting a through-hole pad). The three selector kinds: a ZONE (`InRectangle`/`InZone`, a pad whose CENTRE
lies in it), an explicit PAD SET (`Pads`/`Component`, every pad of a footprint), and the opt-in `FinePitch`
HEURISTIC (a pad at or below a size threshold) — whose threshold is a REQUIRED engineering input with NO
silent default (the minimum-member-size rule; a default there would be a process decision made by a
library). **Backward compatibility is byte-identity, two ways**: passing no stencil is EXACTLY the flat
path (nothing about it moved, so the whole fab set is byte-identical), and a ONE-LEVEL step at the default
expansion produces byte-identical paste GERBER CONTENT (the Gerber comment names only the side, so it does
not carry the level token — the FILENAME does, `-Top_Paste_100um.gbr`), asserted both ways. **A step
stencil is a FABRICATION-PROCESS parameter, so — like a `DrcRuleSet` — it is passed to the export
(`PcbGerberExport.Generate`/`Write` gained an optional trailing `PasteStencil`), NOT baked into the layout
file**, which is why a layout that declares none saves byte-identically (persisting the step declaration is
filed: a full serializable grammar for its zones/selectors is a separate, larger job than generating the
stencils). Verified the fab-house way (a stencil that double-prints or pastes a THT pad is a real defect):
the no-stencil and one-level byte-identity, the partition (count conservation + set equality + no source
twice + each pad on the RIGHT level), per-level expansion by closed form (a thin level `-0.08` shrinks and
a thick `+0.05` grows the SAME Ø1.0 pad to `π(0.4)²`/`π(0.6)²` against the default's `π(0.5)²`), the SMD-only
rule on every level, one Gerber per NON-empty level each round-tripping through the twin decoder, the file
name carrying the foil thickness, an empty level emitting no file, determinism, and every construction
refusal by name (non-positive thickness, no default level, two levels of one thickness, a non-finite
expansion, an empty stencil, a non-positive `FinePitch` threshold). Docs: `examples/ecad-fabrication.md`.

**Not in v1** (each filed): PERSISTING a step-stencil declaration in the layout file, a per-fabricator
foil-thickness catalogue, paste-volume optimisation, window-paning of
large apertures, fine mask tenting control beyond
the tented/opened via policy, curved conformal mask/silk/paste on a MID surface (refused for the
tamper-mesh distortion reason), a lowercase silk font (a value's lowercase advances as a blank), the
Gerber X2 `.C`/`.P`/`%TA` on the mask / silk / paste layers (the X2 net attribute, the per-Gerber
`FileFunction`, and the `.C`/`.P` component/pad attributes and `%TA` aperture functions on the copper are
done), and a Gerber IMPORT of a foreign board (this is export). Docs:
`examples/ecad-fabrication.md`.

### Assembly pick-and-place (the centroid file) (`PcbPickAndPlace`)

The copper Gerber/Excellon set builds the bare board; the **pick-and-place (centroid) file** is the
assembly-side output a P&P machine reads to *populate* it — one row per placed component (reference
designator, X, Y, rotation, side, value/package). `PcbPickAndPlace.Compute` projects the layout's
`PcbPlacement`s into `PickAndPlaceRow`s, and **one `Compute` feeds both writers** (the drawing-sheet
rule — a CSV centroid and a KiCad-style `.pos` cannot disagree about a pose): `ToCsv` writes the
ubiquitous `Designator,X,Y,Rotation,Side,Value`, `ToPos` a KiCad-style aligned
`Ref Val Package PosX PosY Rot Side`, and `Write` drops both to disk.

**The pose is the placement, not the 3D body.** A machine places by the footprint origin, which is the
layout's `PcbPlacement` pose independent of any 3D-model offset, so a row is a pure projection of the
placement (exact by construction). Board-frame X/Y are reported **verbatim** (the same coordinate honesty
the KiCad import states — no flip), units are mm and degrees CCW-positive.

**The one real decision is the bottom-side rotation, and it is a mirror.** A bottom part is physically
reflected (the layout realises it as the `FlipZ` part transform); a P&P machine populating the bottom
flips the board to reach it, reflecting the board-frame angle — so a bottom row's rotation is a
reflection while a top row is the placement angle verbatim. It is a **sign swap, never a `cos`**, so a
quarter turn is exact. The flip AXIS is a per-fabricator convention, so it is a `BottomFlipAxis`
parameter on `Compute`/`ToCsv`/`ToPos`/`Write`: X (the default, `(360 − rot) mod 360`, negate — every
prior emission) or Y (`(180 − rot) mod 360`). Both are exact on a quarter turn (X: 90 → 270; Y: 90 → 90,
0 → 180), and the default is byte-identical to before (asserted). Rows are in placement (declaration)
order, so the output is a deterministic function of the layout (two emissions byte-identical).

**The oracle is the twin-decoder round trip** (the repo's fab-file rule): `ParseCsv` reads back what
`ToCsv` wrote and recovers the designator, X, Y, rotation, side and value exactly — coordinates written
round-trippable, fields RFC-4180 quoted only when they carry a comma / quote / newline, so a value like
`10k, 5%` survives — and it refuses a wrong header / field count / number / side / unterminated quote by
name (the reader scoped to what the writer emits). `Package` is the component's footprint name, or its
definition type name when it carries no footprint. `Write` drops the whole board as one pair;
**`WriteBySide`** drops a SEPARATE top and bottom pair (`<name>-top-pos.csv` / `.pos` and the `-bottom-`
pair) — the assembly-house need, since populating each side is a different machine setup. It is a
PARTITION of the same `Compute` rows filtered by side (nothing re-projected), one pair per POPULATED side
(a single-sided board yields exactly one, an honest empty rather than a stub for the empty side), so the
oracle is that the union of the two side files' parsed rows is the combined file's pose for pose and each
side file carries only its own side. Docs: `examples/ecad-fabrication.md`.

### IPC-D-356A netlist (electrical test / net compare) (`PcbIpc356`)

The Gerber set builds the bare board and the centroid populates it; the **IPC-D-356A netlist** is the
board-house **electrical-test / net-compare** deliverable. It lists, per NET, every conductive **access
point** — every component pad and every net-carrying via — with its net name, refdes + pin, board-frame
midpoint (X, Y), layer/access code, drill (drilled features) and feature kind. A fab net-compares it
against the copper Gerbers; a test house programs a flying-probe / bed-of-nails tester from it.
IPC-D-356**A** is the netname-carrying revision (the original IPC-D-356 carried none). `PcbIpc356.Write`
returns the text, `WriteFile` writes `<name>.ipc`.

**Every pad's net is resolved through `PcbCopperModel.FromLayout`** — the SAME tagging the DRC and
connectivity read, so the netlist cannot drift from the copper (the one-declaration identity). The
record is a fixed identity prefix (op `317`/`327`, net, refdes, pin) plus a letter-prefixed geometry
token stream (`A<access> X<±µm> Y<±µm> [D<µm>]`), with `C`/`P` header records and a `999` end.

**Four conventions, each a real choice and each stated so it cannot drift.** (1) **Units are metric
micrometres** (`P UNITS CUST 2`), coordinates written as an explicit sign plus an integer µm magnitude —
the file's OWN quantum, so the round trip is EXACT (a parse recovers the same integers a write emitted)
and a wrong scale (mm-integers instead of µm) is a 1000× coordinate-magnitude tell, the Gerber-`%FS`
lesson. (2) **Coordinates are board-frame VERBATIM** (no Y-flip — the coordinate-honesty rule): a
bottom-side access point keeps the same board (x, y) as a top one (a plated hole serves both faces), and
which SIDE it is probed from is the ACCESS code, not a coordinate flip. (3) **Access** is `A00` = all
layers (a through-hole pad or a through via reaches both outer faces), else the 1-based number of the
top-most copper layer the feature is accessed from — the general IPC layer convention, which reduces to
the classic `top = 1 / bottom = 2 / all = 0` on a 2-layer board. **A blind/buried via ALSO carries its
full layer SPAN**, because the single 2-digit access code cannot: a feature reaching more than one copper
layer but NOT both outer faces writes an explicit `L<from>-<to>` geometry token (the 1-based inclusive
copper range), so a buried In1→In2 via is `A02 … L02-03` — the per-inner-layer encoding — recovered into
`Ipc356AccessPoint.FromLayer`/`ToLayer`; a through feature and an SMD pad write NONE (their reached set is
implicit in the access code), so those records are BYTE-IDENTICAL to the narrow format. (3b) **An
over-width identity rides a `379` continuation record**, never a truncation: the fixed fields are 14/6/4
chars (net/refdes/pin) and a longer identity is carried IN FULL by a preceding op-`379` record
(letter-tagged `N`/`R`/`P` tokens for the fields that overflow) while the fixed field holds its HEAD, so
an over-14-char net name is CARRIED (not refused), the columns stay valid, a legacy reader still gets a
usable — if truncated — name, and `Parse` applies the continuation to the record it precedes so the
net-reconstruction oracle groups by the full name. Both are the repo's own additive tokens (the standard's
single fixed field cannot spell a range or an over-width name) in the same letter-prefixed stream the
format already uses; a board that needs neither is byte-identical, so both mechanisms only change the
records that require them. (4) **A drilled feature is op `317`** (a through-hole pad, or a via — which carries a BLANK
component reference, the reader's tell), an SMD pad is op `327` with no hole; every `317` carries a `D`
drill token and every `327` carries none.

**What is included, and the null-net decision.** Every component pad and every net-carrying via. An
unconnected / no-connect pad is each its **own single-point net**, given a unique `N/C-######` name —
which is exactly how the copper model treats a null-net feature, so the reconstructed partition matches.
Board mounting / legacy holes are EXCLUDED (they carry no net — a netlist lists NET access points), and
conductor (trace, op `378`) records are OPT-IN (below).

**The oracle is the twin-decoder round trip plus a NET RECONSTRUCTION**, because a netlist that mislabels
an access point is a silent fab failure that a structural validator waves through. `PcbIpc356.Parse` reads
the output back, and the partition of component pads it induces (the SET-OF-SETS grouped by file-net)
EQUALS the board's OWN partition — the copper model's, with a null-net pad its own singleton — with a
dropped or relabelled record making them differ (the mutation that proves it bites). Coordinates recover
as an exact integer fixed point at every scale, and `Write(Parse(Write(pts))) == Write(pts)` byte for byte.

**Refused by name — an identity is never sanitized, because it IS the reconstruction key** (the
topological-naming rule, one level down at the fab file): a net name over the 14-char field / a refdes
over 6 / a pin over 4, whitespace in an identity, or a real net colliding with the `N/C-######`
namespace, all refuse rather than silently squash two nets into one. A drill that rounds below the file's
1 µm quantum is refused too (a drilled record must spell a positive drill, not an unparseable `D0`). The
reader refuses an unknown record code / units / a `317` with no drill / a `327` with a drill / a malformed
layer span / a dangling-or-unknown-token continuation by name (scoped to what the writer emits).

**Conductor records (op `378`) are OPT-IN** (`Write(layout, includeConductors: true)`; `ComputeConductors`,
`Ipc356Conductor`, `ParseFile`/`ParseConductors`): one per routed trace — its net, its 1-based copper
layer (a trace is copper on ONE layer, so its access code IS that layer), its width, and its ≥2-point
centre-line path as an `A<layer> W<µm> X<±µm> Y<±µm> …` token stream (the same self-delimiting geometry
stream the access-point records use; an over-width net rides the SAME `379` continuation with an `N`
token). This is the more-thorough net-compare the access-point list does not carry — the conductor
topology, not just the pads and vias. **The load-bearing decision is that it is opt-in and empty-by-
default**: `Write` with no conductors emits no `378` records, so the default netlist is BYTE-IDENTICAL to
before, which is what lets the feature add nothing a caller did not ask for; and `Parse` skips `378`
records into the conductor half so a file carrying them still parses its access points unchanged. The
oracle is the same twin decoder: a conductor round-trips its net, layer, width and WHOLE path exactly
(coordinates in the file's µm quantum, so integer-exact), asserted per conductor and as a byte fixed
point, with the mutation that a dropped midpoint would fail; malformed `378` records (a missing access /
width, mismatched X/Y counts, fewer than two points, an unknown token) are refused by name. Docs:
`examples/ecad-fabrication.md`.

### The fabrication drawing (the shared frame's third consumer) (`PcbFabricationSheet`)

The Gerbers and the Excellon program are what a board house *machines* from; the **fabrication
drawing** is what a board house *reads* beside them. `PcbFabricationSheet` → `PcbFabricationDrawing`
(`PcbFabricationDrawing.cs`) is a drawing SHEET for a `PcbLayout`: the board OUTLINE at a fitted scale,
a **drill map** (a symbol at every drilled feature), a **drill table** — a keyed LEGEND — grouping the
board's holes, vias and through-hole pad drills by size, a **layer stackup** table, and a
**fabrication notes** block — on the SHARED
`DrawingFrame` (§6c). It is that frame's **third consumer** after the mechanical `DrawingSheet` and the
ECAD `SchematicSheet`, and the decision that makes it one is that a fab drawing IS an engineering
drawing: it uses the SAME three-band `EngineeringTitleBlock` on the SAME `SheetLayers`, so
`sheet.Frame().Compute()` given the same paper and title-block fields is **byte-identical to a
mechanical `DrawingSheet`'s frame** (asserted directly — the payoff of one shared frame, that a
drawing and its fab drawing of one board cannot draw different furniture). `Compute()` feeds the
**SVG / DXF / PDF** writers from one set of primitives (the one-`Compute` rule), so the three cannot
disagree.

**It reads the board; it never edits it** — the outline, holes, placed vias and stackup all come from
the layout's own public read surface, so the drawing cannot disagree with the board it documents (the
one-declaration rule, applied to a drawing).

**The drill table is a closed-form PARTITION, and stating it that way is most of the design.** Its
rows group the board's holes, vias AND through-hole COMPONENT PAD drills by an exact `(diameter,
plated)` key — a mounting hole is NPTH, a board via / every placed via / every through-hole pad are
PTH, and the diameter is the board's own value carried verbatim, so **exact equality IS the right
partition** (a data-derived value, not a computed one; the exact-semantic rung of the epsilon
ladder). **The through-hole pads are the same SMD-vs-THT distinction the solder-paste layer uses**: a
through-hole pad HAS a drill (`PcbCopperModel.FromLayout` adds one for exactly the
`PadKind.ThroughHole` pads) and a surface-mount land does NOT, so `Σ row.Count` equals the count of
holes + placed vias + through-hole pads, each row's count equals the features of that size and
plating, and **adding a board hole OR a through-hole pad adds exactly one to its row** (an SMD pad
adds none) — the oracle, with the mutation that proves it bites, where "a picture looks right" would
prove nothing. Sizes sort ascending (then NPTH before PTH), so the symbol assignment is a
**deterministic function of the board**. **The table is a keyed LEGEND**: each distinct size takes a
distinct `Index`/`Symbol` — a LETTER (`A`, `B`, …), the always-distinct key drawn in the `SYM` column
— beside a `DrillGlyph` from the CANONICAL, ordered `PcbFabricationSheet.DrillGlyphPalette` (the map
marker, cycled past the palette length with the letter as the distinguishing suffix; a board with
more distinct drill sizes than the `A`–`Z` alphabet holds, `MaxLegendSizes` = 26, is refused by name,
since that is a manufacturing red flag anyway). The **drill map** places one `DrillMark` per
feature at its own location — `mark.SheetLocation == drawing.Project(mark.BoardLocation)`, the SAME
board→sheet transform the outline is drawn by — so the map cannot omit a hole nor invent one, asserted
as an identity rather than eyeballed. The **stackup table** lists the physical `LayerStackup.Layers`
(copper + dielectric); a copper-only board carries no physical stackup, so its table is empty and a
note states the copper count instead. The **notes** are write-only-when-stated: finished thickness,
copper-layer count, copper foil thickness (only when a stackup gives one), the drill summary, and any
mask/silk/paste the layout declares — a value nothing carries is **omitted, not invented**.

**The fab-package fields the geometry cannot carry come from a `PcbFabricationSpec`** — the board's
FABRICATION REQUIREMENTS: base material, finished thickness, copper weight, surface finish
(`PcbSurfaceFinish` + an `Other` name), solder-mask and silkscreen colours, IPC-6012 class, minimum
trace width and clearance, and free-form notes. **Every field is optional** and `null` (or an empty
notes list) is "not stated", so `PcbFabricationSpec.Default` is valid and states nothing — the same
write-only-when-stated convention the drawing's own notes and the layout file already use. It rides in
the layout as **layout truth** the same way the mask/silk/paste settings do (`layout.WithFabrication`
→ `layout.Fabrication`), which is the deciding call: a fab spec is a fabrication PARAMETER of the
board, the same KIND of thing those settings are, so it lives beside them rather than on `PcbBoard`
(the geometry). The drawing reads it write-only-when-stated (a stated field prints its note, e.g.
`MATERIAL: FR-4.` / `SURFACE FINISH: ENIG.` / `COPPER WEIGHT: 1 oz (35 µm).` / `FABRICATE TO IPC-6012
CLASS 2.`; an unstated one is absent), and it persists on the mask/silk/paste seam verbatim — written
only when non-null, each field write-only-when-stated inside — so a layout stating no spec saves
byte-identically to a pre-spec file and a stated spec is a `save → load → save` byte fixed point.
**A stated finished thickness OVERRIDES** the modelled plate thickness in the ONE finished-thickness
note (the delivered stackup thickness including copper and finish is what a fabricator quotes to, and
duplicating it as a second note would confuse); with no spec that note is the modelled thickness
exactly as before, so a no-spec drawing is byte-identical (asserted: no-spec == empty-spec notes AND
`ToSvg()`). Every stated value is validated at `WithFabrication` and refused **by name** — a
non-finite/non-positive thickness / copper weight / minimum trace / minimum clearance, an IPC class
outside {1, 2, 3}, or an `Other` finish with no name.

**The stated IPC class now ACTS — an `DrcRuleSet.ForIpcClass(1|2|3)` preset and a spec-vs-class
cross-check.** The load-bearing decision is the DIRECTION: a DRC minimum is a FLOOR the design must
clear, and a stricter class requires MORE copper (a larger clearance, annular ring and edge keep-out),
so **every minimum GROWS with the class and class 3 is the strictest** — the DRC flags progressively
more, which is the IPC-6012 direction for a minimum annular ring exactly (Level C leaves the most
copper), and is what makes "class 3 is strictest" mean "hardest to pass" for a floor-style rule. The
naming is a producibility LEVEL A/B/C ↔ class 1/2/3 nominal convention; the values are ⚠ transcribed
figures asserted in the datasheet form a human checks (the class minimums), monotone-increasing per
rule with the acid-trap angle constant at 90°, and **class 2 is field-identical to the Class-2-ish
`Default`** (asserted, so the preset spreads around a rule set that already shipped). Because a preset
is an ordinary `DrcRuleSet` it **drives `PcbDrc` with NO change to the check** (verified: one board's
0.18 mm gap clears class 2's 0.15 floor and fails the stricter class 3's 0.20). `DrcRuleSet.CheckSpec`
reads the spec (the cleaner home per the task, since the class it claims resolves through
`ForIpcClass`) and compares the spec's OWN stated `MinTraceWidthMm`/`MinClearanceMm` against that
class's floor → an `IpcClassCheck`: a spec claiming a strict class but stating a minimum LOOSER (finer)
than the class permits is `NonConforming` with each offender named (**the stated value AND the class
minimum**, the `PcbLayoutCheck` house style), a spec whose stated minimums meet its class is
`Conforming`, and a spec with no class — or a class but no minimum to compare against — is
`NotCheckable` with a reason (never invented into a pass or a fail, the write-only-when-stated rule one
level up). `Default` is field-identical to before (the preset is a new factory, the cross-check a new
static method; every existing DRC path untouched). Docs: `examples/ecad-drc.md`.

**A named house-spec CATALOGUE closes the last fab-spec residual** (`StandardFabSpecs`): a caller picks
a common preset — `TwoLayerFr4Hasl` / `TwoLayerFr4Enig` / `FourLayerFr4Enig` / `FlexPolyimideEnig` —
instead of typing the fields, the `StandardHoles` / `SheetMaterials` / `ForIpcClass`
verify-against-datasheet pattern. **The load-bearing decision is that a catalogue entry is an ORDINARY
`PcbFabricationSpec`**, not a new type or a new application path: it is a value a caller passes to
`WithFabrication`, so it persists (a `save → load → save` byte fixed point) and drives the fab drawing
through exactly the machinery above (verified through those seams, no new drawing/persistence code). ⚠
The figures are transcribed nominal defaults asserted in the datasheet form a human checks (the values
themselves — a re-typed formula agrees with its own mistake). **The spec carries no LAYER COUNT — the
board's stackup does** — so the "2-layer"/"4-layer" names describe the intended board while the SPEC's
honest differentiators are FINISH (the two 2-layer entries differ only there, HASL vs ENIG, the single
most common real distinction) and CLASS (the 4-layer entry is a higher-reliability class-3 build with
wider required minimums). Every entry states all nine core fields (so a new one cannot be added
half-filled — asserted by enumerating the catalogue) and every entry's stated minimums MEET the class
it claims (`DrcRuleSet.CheckSpec` reports each `Conforming` — a house standard must not contradict its
own class), and the coverage is a CLAIM: a reflection test asserts `All` lists exactly the published
properties, so a new entry that is not in `All` fails. Vary a preset with a record `with` expression.
Docs: `examples/ecad-fab-drawing.md`.

**The drill map now carries a canonical symbol set + legend, and the drill table includes through-hole
component pad drills** (board holes + placed vias + THT pads, grouped by (diameter, plated); an SMD pad
has no drill and contributes no row — the paste layer's SMD-vs-THT distinction reused). Symbols are a
defined ordered set assigned deterministically by ascending diameter, and the drill-table
closed-form oracle EXTENDS unchanged: `Σ row.Count == board holes + placed vias + THT pads`, every
feature in exactly one row, adding a THT pad moving exactly its row by +1.
Docs: `examples/ecad-fab-drawing.md`.

**The per-layer PLOTS complete the fab sheet set** (`PcbFabricationPlots` / `PcbLayerPlot` →
`PcbLayerPlotDrawing`, `PcbLayerPlot.cs`): the human-readable plot per layer a fab package ships
beside the Gerbers. `PcbFabricationPlots.For(layout)` returns one `PcbLayerPlot` per copper layer (in
stackup order), then one per **declared** mask / silk / paste side — write-only-when-stated, so a bare
copper board plots just its copper layers (the same "if present" convention the notes and persistence
use). **The load-bearing decision is that a plot consumes the copper model's OWN regions rather than
re-deriving copper**: a copper plot draws exactly the `PcbCopperModel`'s features on that layer (via
pads and traces included — they are copper features like any other, already in `model.Copper`), a mask
plot the mask windows, a paste plot the SMD apertures, a silk plot the line-work — the SAME geometry
`PcbGerberExport` consumes — so a plot and its Gerber cannot disagree (the one-declaration rule applied
to a plot). **The correspondence is the oracle, and it needs no union re-computation**: the plot
carries its layer's own `Regions` (or silk `Strokes`), so `drawing.Regions.Count` equals the copper
model's feature count on that layer and `drawing.PlottedArea` (the sum of each region's OWN `Area`)
equals the model's total — a plot showing more or fewer regions than its layer carries is the bug, and
drawing exactly the copper model's region objects is what "the plot IS the layer's geometry, not a
re-derivation" means. Each plot rides the SAME shared `DrawingFrame` (§6c), so `plot.Frame().Compute()`
given one paper and title is **byte-identical** to a `PcbFabricationSheet`'s frame (asserted the fab
drawing's own way — reconfigure both frames to one shared `EngineeringTitleBlock` and compare line-work
and text, the difference by default being each sheet's own fitted scale). **A bottom-side layer is
MIRRORED** — plotted *viewed from the bottom*, the fabrication convention that a bottom layer is read
looking through the board — so the transform reflects X about the sheet centre (same Y) for a
bottom-side copper / mask / silk / paste layer, the top and inner layers viewed from the top; each plot
STATES its `ViewSide` and `Mirrored`, and the mirror is asserted directly (`bottomPlot.Project(p).X`
reflected about the shared `ColumnCenter.X`, `.Y` equal). `Compute()` feeds the SVG / DXF / PDF writers
from one primitive set (the drawing-sheet one-`Compute` rule). Docs: `examples/ecad-fab-drawing.md`.

### Copper pours — ground / power planes (`CopperPour`, `CopperPourBuilder`)

A copper pour floods a layer on one net. It is **layout truth** (`layout.AddPour`), it round-trips in
the file, and `PcbCopperModel.FromLayout` DERIVES it into copper features the DRC and the connectivity
engine read like any other copper — so a GND pour **joins every GND pad it touches** and the GND
ratsnest empties. Nothing new was needed downstream; the pour is just copper.

**The fill is the tamper-mesh construction and clears every other net by construction.** The region =
(the board area, or a stated outline) inset from the edge by the pour's `EdgeClearance`, **minus** the
union of every OTHER-net copper feature on the layer grown by `Clearance`, **minus** every OTHER-net
(or netless) drill grown by `DrillClearance` (cross-layer), all through `Region2dOffset` /
`CurvedRegion2dOffset` / `CurvedRegion2dBoolean` with no tolerance. Growing the OBSTACLE by the
clearance keeps the pour at least that far from it — the same offset the DRC's clearance rule grows
with — so a poured board passes `PcbDrc.Check` and the grown-region intersection with any other net is
EMPTY (the proof, asserted directly). **The clearances live on the pour, not on a rule set**, which is
the load-bearing simplification: the fill is a pure function of the pour and the base copper (nothing
threads a `DrcRuleSet` through `FromLayout`), and the defaults exceed the `DrcRuleSet.Default`
minimums so a default pour passes the default DRC with margin.

**Thermal relief is where the design earns its keep.** A same-net THROUGH-HOLE pad must stay
solderable, so instead of flooding over it (which sinks its heat into the plane) the pour carves an
annular gap `disc(padR + Gap)` around it and unions back thin radial SPOKES (four on the diagonals by
default) that overlap the pad copper (inner) and the flood (outer). The two classic bugs are a relief
that DISCONNECTS the pad and a pad that FLOODS, so a test asserts BOTH directions: the pad is
CONNECTED to the plane through the spokes (a pour component overlaps the pad → they touch → one
component in `PcbConnectivity`) AND a point in the annular gap BETWEEN the spokes carries NO copper.
The spoke overlap is a robust AREA overlap by design (a tangent touch is measure-zero and would NOT
connect — the same exact-region-touch rule the connectivity engine uses). SMD pads and vias are
direct-connected (flooded); `ThermalRelief.None` floods a through-hole pad too. One honest cost is
STATED: a spoke meets the plane's clearance circle at a ~90° corner, so the acid-trap (acute-angle)
rule's default strict-`<`-90° threshold is borderline on thermal reliefs (a realistic board sets it
well under 90° anyway), where a same-net-SMD pour is smooth and passes the default.

**Islands are DEAD copper.** After the fill, each connected component (each returned `CurvedRegion2d`)
that touches NO same-net feature is a piece the net cannot reach — removed by default (kept only under
`DeadCopperPolicy.Keep`) and always REPORTED (`PouredPour.DeadCopperArea`). **Each kept component gets
its OWN source** (`pour{i}.{j}`), so two disjoint pieces stay disjoint in the connectivity graph
(they join only by geometric touch, which they do not have) — a pour never force-connects pads its
copper does not actually bridge, which a single shared source (the plated-barrel union) would. The
pour sources are CONNECTORS (like traces and via pads), not terminals, so a floating pour piece never
makes a net read unconnected. `PourFill.Hatched` intersects the fill with a crosshatch grid (the
region ∩ a line pattern, `CurvedRegion2dOffset.Stroke`d strips) for a lighter, more flexible plane;
the spokes are unioned AFTER the hatch so a hatched pour's pad connections stay solid.

**A pour exports to Gerber** as a `G36`/`G37` region fill (it flows to `PcbGerberExport` as an
ordinary copper feature, region-filled because it is neither a recognised flash nor a via/trace) and
round-trips by area — see the Gerber imaging note above for the anti-pad-island fix a pour forced.
**Verified higher than usual because ECAD fails plausibly** (`PcbPourTests`): the GND-pour headline
(every GND pin connected, ratsnest empty); the poured board DRC-clean with the empty grown-
intersection asserted directly; the THT relief connected-AND-gap; the island removed and reported
(and kept under the opt-in); the pour area a closed form of board/clearance/hole; determinism (a pure
function → bit-identical area); the Gerber round-trip by area and symmetric difference; a
save→load→save byte fixed point (pour-free byte-identical); the refusals by name; and scale
invariance (area ∝ s²). **Pour PRIORITY resolves overlapping pours** (`CopperPour.Priority`): two
different-net pours flooding one area would short, so the HIGHER-priority pour fills first and keeps
its copper, and the lower-priority one is carved back by its own clearance around it (same-net pours
merge). The IMPLEMENTATION is not a new algorithm — `FromLayout` fills pours highest-priority-first
(ties by declaration order) and feeds each already-filled higher pour into the base copper the next
one sees, so a lower pour treats it as ordinary OTHER-net copper and the existing other-net
subtraction in `Fill` does the carve. So a single pour, or pours that do not overlap, are UNAFFECTED
(the base copper they see is the same), and only the shorting case changes; the source ids stay keyed
by DECLARATION index, so they do not depend on fill order. Priority rides in the layout file
write-only-when-stated (a priority-0 pour writes no key, so a pre-priority file is byte-identical).
Verified (`PcbPourPriorityTests`) by the mutation that proves it: which net covers the shared area
FLIPS with the priority and NEITHER configuration shorts, ties break by declaration order, disjoint
pours keep equal per-net area whichever has priority, and the persistence fixed point. Custom relief
geometry beyond the spoke default is filed; and conformal placement on a doubly-curved wall is not
offered (the distortion would land in the pitch, the tamper-mesh lesson). Docs: `examples/ecad-pcb.md`
(Copper pours).

### Teardrops — drill-breakout relief (`TeardropSettings`, `TeardropBuilder`)

The tapered copper a trace gains where it meets a ROUND pad or a via of its own net, relieving the
drill-breakout crack at the sharp junction. It is the pour's integration verbatim — LAYOUT TRUTH
(`layout.WithTeardrops()`), DERIVED by `FromLayout`, off = byte-identical, rides in the file
write-only-when-stated — with one deliberate choice: each teardrop carries its TRACE's source (a trace
already shares one source across its stroke regions), so it merges into the trace's copper and the
connectivity engine reads it as a CONNECTOR not a terminal, needing no new source kind and leaving the
net's pad count unchanged.

**The GEOMETRY is where the finding is, and a first attempt got it wrong.** A teardrop must fill the
CONCAVE CORNERS where the trace edge leaves the pad circle — copper OUTSIDE the pad — and the naïve
straight chamfer from the pad's perpendicular diameter (±R) to the trace edges (±w/2 at length L) lies
ENTIRELY INSIDE the pad∪trace union for a trace ending at the pad centre (the common case, since routers
route to pad centres) and adds ZERO copper: a broken teardrop that looks plausible. The correct shape is
the CONVEX HULL of the pad DISC (sampled) and the two trace-edge points at `length·dir ± (w/2)·n` — the
hull is the pad plus a wedge reaching those points, so unioned with the pad and trace it fills the
corners. The pad arc is sampled into the hull, so the added copper's pad-side boundary is a fine polygon
that lies UNDER the exact pad disc — the visible teardrop boundary, the two flanks, is exact straight
copper. The oracle is therefore that the teardropped layer's union AREA strictly EXCEEDS the plain one
(a no-op teardrop fails it), which the naïve chamfer would not pass. The length is `LengthRatio·R`
(default 2, one pad diameter) clamped to the trace's first segment, and it must exceed the pad radius or
the trace-edge points do not reach past the pad. Each teardrop is same-net (never shorts its own pad) and
DRC-gated against OTHER-net copper (grow-and-intersect at its `Clearance`; dropped if it would violate),
so teardrops never turn a clean board dirty. Round pads/vias only (a rectangular/oval pad is skipped, its
`Round`-shape gate the same the copper model uses); a pad no wider than the trace gets none.

Verified (`PcbTeardropTests`): the area strictly exceeds the plain one (the oracle with teeth), the net's
PAD COUNT is unchanged (connector not terminal) and the net stays connected, a teardropped board is
DRC-clean, off is unchanged, a rectangular pad gets none (area unchanged), a clean board stays clean with
teardrops on (the gate's guarantee), and the persistence fixed point. Docs: `examples/ecad-pcb.md`
(Teardrops).

### Stage 7 — enclosure fit (the MCAD/ECAD boundary) (`PcbEnclosure`)

Does the placed board go in the box? An `Enclosure` is a housing built from the ordinary `Shape`
API — a shelled box with the panel cutouts drilled, deliberately NOT a new solid type — carrying a
rectangular interior cavity, a wall/floor thickness, a board seating height (the standoffs), a lid
at a stated height (the headroom ceiling), named panel cutouts and interior keep-out volumes.
`EnclosureFit.Check(enclosure, layout)` returns a `FitReport` naming, locating and measuring every
problem (the `DrcViolation`/`PcbLayoutCheck` house style — a report that only said "does not fit"
would be useless).

**It reuses the LANDED clash machinery rather than a new one** — the entry's own framing, that
"enclosure fit is `Bvh.QueryOverlap` + `MeshIntersection.Crosses` + the mechanism sweep's clash
reporting, already landed and already knowing a SEATED part is not a clash." Every body is pulled
into the enclosure's interior frame and tested with an instance-bounds broad phase then the
transversal `MeshIntersection.Crosses` narrow phase, so **a part resting flush on the lid or seated
on its standoffs is not a clash** (the meshes touch — a coplanar top face, a one-sided side
contact — but do not interpenetrate, which `Crosses` reads as `Transversal = false`). The one place
that rule is exercised at the boundary — a part whose top is exactly the lid underside — reads
headroom exactly 0 and no collision.

**Where the geometry is a plane or a rectangle the number is closed-form, not meshed**, because
that is the exact tool: the board outline against the four cavity walls (each too-large wall NAMED
with its overhang), and a part's top against the lid (the clearance deficit is `top − lidZ`, exact
for a box body, and always reported as the scalar `Headroom`). The mesh clash is used where the tool
is right — a component body against an arbitrary wall/floor, and a keep-out (surface crossing OR
full containment via a winding-number test, since a small part wholly inside a keep-out crosses no
surface). Panel connectors are EXCLUDED from the wall clash — passing through a wall is what they
are for — and checked instead against the cutout whose `For` names them: the body must reach the
wall AND its cross-section fit through the opening, or it is named (the centre offset, the reach
deficit).

**One declaration, one geometry.** `Enclosure.SeatFrame()` is where the board mounts; passing it as
the layout's `boardFrame` seats the board in the cavity, so the fit check reads one geometry rather
than two hand-kept poses — and because the fit pulls every body back through `Enclosure.Frame`, the
enclosure's own placement and the board's need not be the same and nothing is assumed about where
either sits. `Enclosure.SmallestFor(layout, clearance, standoff, headroom, wall)` sizes AND places
the smallest box the layout fits in place — a starting point to refine, not an enclosure generator
(this is a fit-check stage). Refused by name at construction: a seat outside the cavity, a lid below
the seat, non-positive dimensions, a non-positive cutout. Round panel cutouts are checked against
their bounding box in v1 (exact round-hole corner fit is filed). Docs: `examples/ecad-enclosure.md`.

### Stage 8 — thermal coupling (the ECAD/thermal boundary) (`PcbThermal`)

Where does the heat go? A powered board is a heat-conduction problem, and `PcbThermal.Solve(layout,
spec)` solves it on the **landed FEA thermal solver** (`EngrCAD.Fea` — a clean leaf, Core + Mesh
only, so `EngrCAD.Ecad` references it with no UI dependency) rather than a lumped estimate, so the
answers are verifiable against closed forms. Each component's dissipation becomes a volumetric heat
source; the copper spreads it; a held cold edge or a convecting face carries it away; the result is
a temperature field the `FieldDisplay` colour map picks up and a hot-spot temperature per component.

**v1 is the standard board-level model: an effective conductivity over a slab, and the mixing rule
is the physics of the sandwich.** The copper is not meshed as discrete traces/planes — it is SMEARED
into the slab's conductivity, high in-plane and low through-thickness, and each half is the classical
composite bound: the copper layers are PARALLEL heat paths for in-plane flow, so `k_in = f·k_Cu +
(1−f)·k_FR4` (the area-fraction rule of mixtures / Voigt bound), and they are in SERIES through the
thickness, so `k_th = 1 / (f/k_Cu + (1−f)/k_FR4)` (the harmonic mean / Reuss bound). `f` is the
copper VOLUME fraction — (total copper thickness × average coverage) / board thickness — the one
honest knob (`PcbThermalSpec.CopperFraction`, or `.FromCoverage(board, coverage)` off the stackup).
This is the standard model precisely because it needs no conforming multi-material copper mesh (which
`TetMesher` refuses anyway); a future stage refines it by meshing the copper. **A bare board (`f = 0`)
collapses both effective values to the dielectric's own conductivity — an isotropic slab, the
verification simplification** — and the model takes the scalar conductivity path bit-for-bit
(the anisotropic `ConductivityLaw.Orthotropic` is only set when the two values differ). The board is
a SLAB with NO holes: the smear ignores the copper geometry, so it ignores the drills too, which is
what keeps the closed-form oracles clean.

**One unit crosses the ECAD/model boundary and it is done once, where it belongs.** Power is stated
in WATTS (nobody specs a chip in milliwatts-of-model-units) and converted to the model's mW at the
boundary — the `ModelUnits` discipline that the input a caller states is converted at the edge while
the field the equation integrates is native; a film coefficient likewise is stated in SI W/(m²·K)
(natural air ~10) and converted ×1e-3. Both conversions are pinned by a test that a 1 W uniform
source gives the hand-calc resistance rise (a forgotten factor of 1000 shows immediately). Held
temperatures and ambients carry no length or mass, so they cross verbatim in the caller's scale.

**Per-component power is a `Generation` load, and the diffuse case is exact.** A component's watts
are spread uniformly over its footprint × thickness as a step field (its resultant reported by the
solve, since a step straddles elements); a board-wide `BoardPower` is an exact uniform generation
(a constant integrates exactly). Boundary conditions (`PcbThermalBoundary.FixedTemperature`,
`.Convection`) name a `BoardSurface` (Top/Bottom/Edges/All) or a raw `Facets` selector — the escape
hatch that lets a single edge of a rectangular board be clamped, which the four-wall `Edges` cannot
single out.

**Verified against closed forms, in the FEA house style** (`PcbThermalTests`, since an ECAD thermal
answer fails plausibly): a **uniformly-dissipating board** to a fixed cold edge settles into
`T(x) = T0 + (q/2k)(L²−x²)`, a PARABOLA in the quadratic element space reproduced to **3.16e-12
relative** (round-off), with the stated watts coming out as exactly `P × 1000` mW of applied heat
(the units check). A **single hot component** past a cold edge carries all its power as a constant
flux, so the far-field profile is the series-resistance line `T0 + Q(L−x)/(kA)` — matched to
**3.6e-5** (the honest accuracy: a localized step source on an unstructured tet mesh is not exactly
1D, so the departure is its 3D discretization), with the ENERGY BALANCE exact to round-off (all the
generated heat leaves the cold edge). **Copper raises spreading**: the same 0.3 W source over real
FR4 (k = 0.3) against FR4 with 2.6 % copper lifts the effective in-plane conductivity 0.3 → 10.4 and
drops the peak rise **1129 K → 32.6 K (34.7× lower)**, with the far-field rise ratio exactly
`k_copper / k_bare` (a ratio, not a hand-waved direction). Convection is verified by the film unit
(a near-isothermal high-k board lands within 0.14 % of the lumped `P/(hA)`, so a forgotten ×1e-3
shows as a 1000× miss) plus the exact energy balance (convected = generated). A **no-boundary** board
is an undriven conduction problem refused BY NAME (the `ThermalSolver` convention); a **zero-power**
board is isothermal at its held temperature exactly; a solve is **deterministic** to the bit. Docs:
`examples/ecad-thermal.md`.

### Stage 9 — MID / LDS 3D surface routing (`MidSurface`/`MidBoard`/`SurfaceTrace`/`MidRouting`/`Mid3dDrc`)

The flagship novel capability: routing conductors and seating components on a MOULDED, doubly-curved
surface (an LDS housing carrying its own circuit on its shaped wall) rather than on a flat board. **It
works on ANY surface — a torus, a bumpy blob, a whole closed shell — not one exp-map chart**, which is
the generalisation that removes the earlier version's two recorded failures: a single global exp map
from a seed + radius distorted far from the seed and WRAPPED a closed surface onto itself where it
degenerates (whole-tube `MaxDistortion` 22.5, whole-sphere 0.99).

**The load-bearing decision is that the surface is modelled INTRINSICALLY, with LOCAL charts per
query.** A `MidSurface` wraps an arbitrary triangle mesh (triangulated, per-vertex normals, a 3D BVH)
and answers the routing's three questions with no global chart: `Locate(worldPoint)` snaps to the
nearest surface point (a `SurfacePoint` — position, interpolated normal, face + barycentric weights),
`Frame` gives the tangent frame a seated component poses in, and `Chart(centre, radius)` builds a
LOCAL `LocalExpChart` — `MeshLocalParam`'s exp map made per-point, with the FORWARD map (a `SurfacePoint`
→ `(u, v)`, the barycentric weights carrying over) the DRC needs to express a feature's copper in the
chart, the INVERSE (a `(u, v)` → surface point + normal) the conductor lift needs, and the local
`ScaleBand`. The chart is the geodesic-distance approximator: its `(u, v)` planar distance equals the
geodesic exactly on a developable patch (an isometry) and differs by the map's local scale where
curvature concentrates. **Because the chart is local and per query, a closed surface never wraps** — no
chart is ever asked to cover the whole part. The single global chart the earlier board required is now
just the degenerate case where one chart covers every feature, kept (`MidBoard.OnSurface`) for a
developable patch where it gives EXACT numbers, but no longer a requirement (`MidBoard.OnMesh` is the
general path).

**On an intrinsic board the clearance is a GEODESIC surface distance, and the three-valued verdict is
CERTIFIED both ways.** The broad phase is a theorem: a 3D chord is never longer than a surface geodesic,
so a chord edge-to-edge distance at or above the clearance PROVES the surface clearance (CLEAR, whatever
the curvature) — and that is also what lets a far pair be certified with no chart, and what makes the
per-pair chart only ever cover a genuinely-close pair. A closer pair is projected into a LOCAL chart
seeded at the pair midpoint (grown until it covers both, kept as TIGHT as possible since an over-large
chart on a small closed surface wraps and its `(u, v)` degenerates), and the SAME grow-and-intersect the
flat copper DRC uses runs there with the local scale band folded in — Violation (too close even
best-case), Uncertain (the band straddles), or Clear. **The distortion that matters is the scale
variation over the GAP being measured**, not over a feature, so the band is probed across the separation;
and a DEGENERATE band (a chart that wrapped a tightly-curved patch, spread > 2.5×) is not a measurement,
so it is refused cleanly as "too curved to certify" rather than reported as nonsense. This is the honest
all-geometry behaviour: at small clearance scales on smooth surfaces the geodesic measure is CONFIDENT
(the exp map is second-order accurate, and the separation is measured along the chart's exact radial
direction), and it refuses only where the clearance is comparable to the curvature radius — a small
sphere with a clearance a large fraction of its radius reads a band `[0.947, 1.04]` straddling the
`1.0` limit (distortion 10%) and is refused, while the same pair on a plane is certified.

**The decisive PRECISION oracle stays the developable one, preserved bit for bit.** Where a single
chart IS an isometry (a flat or developable patch) `MidBoard.OnSurface` authors features in one exp
map's `(u, v)` and the DRC runs there, so a cylindrical board's 3D DRC verdicts and measured separations
equal the UNROLLED flat 2D DRC's, bit for bit — measured with the reported separation bisected up to a
BAND-INDEPENDENT cap (capping at the distortion-dependent threshold would make a developable board's
clearance differ from its unrolling by the bisection's last bits, which is exactly what the oracle would
then miss). Numbers: cylinder exp-map distortion `1.2e-3`, plane `6.7e-15`, verdicts and `(u, v)`
separations identical (`0.04999980926513672 == 0.04999980926513672`), the cylinder folding the
distortion only into the reported surface-clearance BAND (`[0.075, 0.0751]` where the flat reports
`0.075`). The INTRINSIC route reaches the same cylinder answer to the discretisation grade (measured
`0.07474` against an arc gap `0.075`), and a SPHERE geodesic matches its great-circle closed form `R·θ`
(edge-graph path within `[0.98, 1.10]·R·θ`, a chord-inscribed polyline that can fall a chord below the
smooth arc and staircases up to ~8% above). This keeps the two readings — "bit-for-bit where one chart
applies" and "to the weld tier on any surface" — as two statements about the same geometry rather than
one loosened.

**Traces are laid as GEODESICS.** `MidRouting.Connect` between two pads runs `DijkstraGraphDistance`'s
shortest EDGE path (which stays on the surface) and then STRAIGHTENS it toward the true geodesic — a
curve-shortening relaxation, each interior point drawn halfway to the midpoint of its neighbours and
snapped back onto the surface, endpoints pinned to the pads — which removes the edge path's staircase
(up to ~8% long) and is the straightest-geodesic smoothing the task's own note anticipated. A
`SurfaceTrace` holds the lifted centre-line either way (a `(u, v)` polyline through a global chart, or
a surface polyline directly), REPORTS the distortion it carried (`MinScale`/`MaxScale`, measured against
a local chart on the intrinsic path), and exports as a thin conductive `Shape` ribbon that round-trips
through STL/STEP. A component seats at a world position (`board.Seat(component, worldPoint)`) or a
`(u, v)`; a raw `Shape` body (an MCU, an LED, a connector modelled as a small solid) seats the same way
for the showcase. **The showcase** is a moulded wearable dome — a wide low ellipsoid carrying an MCU,
two LEDs, a connector and passives seated on the shaped surface, wired by geodesic conductors — that
SELF-VERIFIES (its render throws if the nets do not route, connect or pass the DRC) and lands as
`examples/ecad-mid.md`'s `ecad-mid-wearable` render, which now **auto-routes** rather than
place-and-verifies.

**The surface AUTO-router landed (`SurfaceRouter`/`SurfaceRouteOptions`/`SurfaceRouteResult`,
`MidRouting.Route`) — the geodesic analogue of the flat `PcbRouter`, and the doctrine is the flat
router's verbatim.** Each unrouted net (its pads not yet all joined per the ratsnest) is decomposed into
2-pin connections over an MST and routed as a geodesic maze search over the mesh VERTEX GRAPH (edge
weight the geodesic edge length, an A\* whose 3D-straight-line heuristic is admissible because a chord
never exceeds a geodesic), straightened, and committed. **The load-bearing rule is that the vertex graph
only ACCELERATES; the exact 3D DRC is the source of truth** — a candidate geodesic is committed only
after `Mid3dDrc.RouteCandidateClears` (the incremental twin of `Check`, sharing the same certified
broad phase and grow-and-intersect) certifies it adds no violation, with an `Uncertain` pair treated as
not passable (the same conservative rule `Ok` applies), so a graph-resolution error can never ship a
clearance-violating trace, a boxed net is reported UNROUTABLE by name, and the partial board is always
clean. **Because the DRC decides every commit, the obstacle model may safely OVER-BLOCK**: a vertex is
hard-blocked for a net when laying its copper there comes within `clearance + width/2 + otherHalf +
margin` of an other-net PAD, measured as the 3D CHORD (a lower bound on the geodesic, so blocking is
the safe direction), or when it is on the mesh boundary; a committed other-net TRACE is SOFT (a per-net
bitmask, crossable at a high cost in a rip-up route and then ripped up). **The margin is exactly HALF a
longest edge, and that is what makes the raw edge-graph path DRC-clean BY CONSTRUCTION** — a point on a
mesh edge between two free (unblocked) vertices is within half an edge of the nearer one, so its chord
to other copper is ≥ clearance, which is precisely the certified broad phase's Clear condition; the
straightened path moves OFF the edges so that guarantee lapses there, which is why straightening is
VALIDATED with the exact DRC and falls back to the raw path when it drifts across an obstacle.
**Rip-up-and-reroute is the flat router's negotiated congestion verbatim** (a net with no clean geodesic
routes across the traces blocking it, rips those up, re-routes cleanly without them, and re-queues them,
bounded so a truly boxed net terminates) — reproduced exactly so a ripped victim is either re-routed
against the ripper or left unrouted, never restored as stale geometry that no later commit certified.
**Over-blocking costs COMPLETENESS, not correctness** — it can refuse a route that a finer search would
find (a named refusal), never accept a violating one; and a narrow gap that the over-block seals is one
a clean trace genuinely cannot fit anyway, so the refusal is usually real rather than merely
conservative. The router runs on an INTRINSIC board (`OnMesh`); a global-chart board (`OnSurface`,
kept for the developable DRC oracle) is refused by name with a pointer to `OnMesh`, because an
intrinsic surface trace is not what the global `(u, v)` DRC reads. **The cylinder-vs-flat cross-check
is the developable oracle applied to ROUTING, and it is a CONNECTIVITY invariant rather than a
bit-identity**: a cylinder MID board and its unrolled flat sheet route the SAME net list, both
fully-routed, both DRC-clean, both connected — a search need not be bit-identical (the two meshes
differ), so the invariant is what the two produce (connectivity + cleanliness), not the exact
geodesics. Filed by name: **topological / shove** routing on the surface (v1 detours around obstacles
but does not push them), **cross-shell auto-routing** and **length matching** — beside the
conformal-mask/pour follow-on (multi-shell MID has since landed, below).

**One trap worth keeping**: the exp map measures the geodesic ACCURATELY along the separation direction
(the chart's radial coordinate IS geodesic distance from the seed), so the intrinsic clearance rarely
refuses at ordinary board scales — a fact that inverts the naive "curvature shrinks the clearance"
expectation. The geodesic between two points is ≥ their chord ALWAYS (curvature makes the surface FARTHER
apart, never closer); the exp map's tangential SHRINK (`MinScale < 1`) is the PARAMETER overestimating the
geodesic, not the geodesic dropping below the chord. So a robust refuse needs the clearance comparable to
the curvature radius (or a folded region a chart cannot cover), which is exactly when a designer should
not trust a flat approximation — the honest boundary rather than a knife-edge one.

**Two smaller decisions carry over.** A trace's WIDTH is checked against the authored width folded
through the local scale band rather than re-measured off the region (round joins never pinch a width and
an opposing-wall measure under-reports on a round cap). A single conductive surface has no drills or
edges of its own; topological / shove routing on the surface, cross-shell auto-routing, length matching
and a conformal solder mask / pour (the distortion reason copper pours already refuse curved walls) are
filed by name. Docs: `examples/ecad-mid.md`.

**Multi-shell MID landed (`MidStack`/`SurfaceVia` in `MidShell.cs`).** A real LDS part carries copper on
the OUTER moulded wall AND on an INNER shell, stitched by through-shell vias — the multi-layer analogue
of a `MidBoard`. **The inner shell is the outer mesh with every vertex offset inward by a dielectric
thickness along its ANGLE-WEIGHTED vertex normal** (the boundary-layer / `MeshSdf` pseudonormal
convention — never a raw face normal, which tears a shared vertex). **Keeping the same mesh TOPOLOGY is
the load-bearing decision**: an outer surface point (a face + barycentric weights) has a corresponding
inner point (the same face + weights, on the offset mesh), so a via is exactly "tie the outer point to
its corresponding inner point" and needs no matching pass — the correspondence is computed from the
outer surface's face vertices and the shell's offset, and lands exactly on the inner face by
construction. **Each shell is its OWN `MidBoard` with its own exp-map machinery** (an inner shell's
geodesic distances differ from the outer's, because offsetting a curved surface changes them), so the
existing single-surface placement / routing / DRC runs PER SHELL unchanged and the per-shell DRC measures
geodesic clearances through THAT shell's charts. **The DRC and connectivity span shells, and most of it
falls out for free**: a via places a real `MidPad` on each shell it touches, so a via's clearance to
other-net copper on both shells is the per-shell DRC's ordinary clearance rule (a via pad IS copper on
its shell); the multi-shell `Check` adds only the inter-shell VIA-TO-VIA spacing rule (the drill web,
all pairs regardless of net) and the CROSS-SHELL ratsnest. `Connectivity` reconstructs each shell's
copper with the existing per-surface touch rule and joins a via's own pads across shells (the plated
barrel), so a net whose copper lies on two shells is ONE connected net iff a via of that net ties them
(the `PcbConnectivity` cross-layer rule, lifted to surfaces; via pads and traces are connectors, user
pads are the terminals that must connect), reusing the `NetConnectivity`/`ConnectivityReport` types.
**A single-shell stack is a plain `MidBoard`**, so its multi-shell `Check` delegates to
`Mid3dDrc.Check(the one shell)` verbatim — bit-identical, prefixing only the shell name.

**The decisive oracle is the DEVELOPABLE one and it is EXACT.** On a cylinder the inward normal offset is
an isometry, so the inner shell is a concentric cylinder of radius `r − t` to round-off — MEASURED at
8.9e-16 over every vertex including the free rim (the angle-weighted vertex normal is exactly radial by
symmetry, which was not obvious — a rim vertex has its faces on one side only, yet its corner-angle
weights cancel the azimuthal tilt). **An INWARD offset self-intersects only where the surface is CONVEX
and the thickness exceeds the local convex curvature radius** (a concave region offset inward merely
diverges — the naive "concave" intuition is BACKWARDS), and detecting it took TWO complementary
exact-sign checks because the obvious one misses the primary case: **a raw cross-product fold test
(`n1·n0 ≤ 0`) catches a DEVELOPABLE inversion** (a tube offset past its section radius scales one
direction, so the linear sign flips) **and any concave-side fold, but CANNOT see a doubly-convex
inversion** — a sphere's uniform inward offset is a uniform SCALE, and a cross product is invariant under
point inversion (`(−u)×(−v) = u×v`), so `n1 = ((R−t)/R)²·n0` keeps its sign even when the sphere turns
inside out. **The escape is the SIGNED VOLUME**, which is not cross-product-invariant: a closed shell
that turns inside out flips its signed-volume sign (`(−s)³·V < 0`), so a sphere offset past its radius is
refused by name. (An OPEN doubly-convex cap over-offset — no signed volume, no linear-sign fold — wants
a curvature-reach check, filed; a real dielectric is far thinner than a housing wall's curvature radius.)
**Verified higher than usual**: the developable exactness, the via mutation (a VOUT pad on the outer and
one on the inner tied by a via is one connected net; remove the via and they split), an inner-shell
same-shell clearance violation FOUND, a via too close to other-net copper on EITHER shell FOUND, a
via-to-via web violation, a clean board clean, the single-shell bit-identity, the self-intersection
refusal by name, the via barrel a closed solid, the endpoints corresponding (and a non-corresponding via
refused), and determinism. **Filed**: per-via partial spans of a &gt; 2 shell stack, a curvature-reach
check for open convex caps, and a conformal shell mask / pour.

**Cross-shell auto-routing landed (`CrossShellRouter` in `MidCrossShellRouter.cs`, `MidRouting.Route(stack,
…)`).** It is the surface analogue of the flat PCB router's LAYER-CHANGING via, and the design is exactly
that idea lifted, so it reuses everything. **The search graph is the UNION of both shells' vertex graphs
plus "via edges" tying corresponding vertices** — a node is `(shell, vertex)`; a mesh edge on shell `k`
carries that shell's geodesic edge length; a VIA EDGE connects `(k, v)` to `(k±1, v)` at a fixed via
PENALTY. **The via edge is trivial to enumerate precisely because the multi-shell decision kept the shells'
mesh TOPOLOGY shared** — vertex `v` corresponds across shells — so the layer-change is one edge, not a
matching pass. ONE A\* over this graph both routes a net between shells and CHOOSES where to change shell;
where the chosen path uses a via edge, the router places a through-shell via at that vertex (the existing
`AddVia` machinery, full-stack on a two-shell stack) and splits the route into per-shell traces — so a
same-shell net gets NO via, a net with pads on two shells ONE, an obstacle hop TWO (out and back). **The
straight-line heuristic stays admissible** because every edge costs at least the 3D chord it spans and the
via penalty is at least the barrel chord (the derived default guarantees it). **The exact multi-shell DRC
is the source of truth** (the flat router's own rule, lifted): a candidate route + via is committed only
after each per-shell trace CLEARS the existing other-net copper on its shell (`Mid3dDrc.RouteCandidateClears`),
each new via pad CLEARS other-net copper on every shell it touches (`Mid3dDrc.RouteClearanceClears`, the
new clearance-only gate so a via pad is not measured against the trace-WIDTH rule), and the inter-shell
via-to-via web is met — so a graph-resolution error can never ship a clearance-violating trace or via, and
a partial result is still clean. Rip-up-and-reroute is the single-shell router's verbatim over the combined
graph (committed traces AND vias are one rippable unit per net). **The single-shell router is untouched and
bit-identical** — the cross-shell path is a NEW file and a NEW `Route(MidStack)` overload; the only shared
change is extracting `RouteClearanceClears` out of `RouteCandidateClears` (behaviour-preserving). Refused by
name: a ONE-shell stack (nothing to hop to — pointed at the single-shell `Route(stack.Outer)`) and a &gt; 2
shell stack (needs partial-span vias, filed). **Verified to the flat router's bar** (`MidCrossShellRouteTests`):
a cross-shell 2-pin routes with EXACTLY ONE via, both segments clean and the net connected, the via's feet
on the outer / inner walls; a same-shell net with a clear path routes with NO via (the penalty keeps it on
one shell); an OBSTACLE HOP (a net whose straight outer path is blocked by a full ring of other-net copper)
routes through the inner shell with TWO vias, and the MUTATION that proves the cross-shell capability is
what routed it is that the SAME fixture on a single shell is unroutable by name; several cross-shell nets
route clean and connected; a pin boxed in on BOTH shells is unroutable by name with the rest routed and
clean; the one-shell / &gt; 2 shell refusals; and two runs deterministic vertex for vertex. **Filed**:
TOPOLOGICAL / SHOVE routing (v1 detours around obstacles but does not push them), OPTIMAL via minimisation
(v1 uses a fixed via penalty), partial-span vias for a &gt; 2 shell stack, and length matching.

### Not in stages 1–9

Thermal VIAS as discrete high-conductivity paths (v1's effective conductivity smears the copper, so
it smears the vias too), a TRANSIENT board warm-up (the `SolveTransient` path exists, so it is a
bounded follow-on with its own erfc-style oracle; the effective slab already carries a volume-weighted
heat capacity), detailed die/package thermal models (v1 spreads a component's power uniformly over its
footprint, not through a junction-to-case network), airflow/CFD cooling, snap-fit/screw-boss detailing,
and tolerance stack-up are later stages over this one graph; each reads the
netlist↔copper identity stage 2 establishes and the DRC stage 4 provides. MID/LDS 3D surface routing
has landed as stage 9 (see above), its surface AUTO-router, its MULTI-SHELL form (`MidStack`, an inner
moulded copper layer stitched by through-shell vias) and its CROSS-SHELL auto-router (choosing which shell
a net rides and placing the vias) with it; its own filed follow-ons are topological / shove routing on the
surface, optimal via minimisation, partial-span vias for a &gt; 2 shell stack, length matching and a
conformal surface mask/pour. The richer interchange
(KiCad `.kicad_pcb`, STEP AP214 board assemblies) follows IDF. The drawn schematic SHEET has
landed (see above) as a VIEW of the graph; a good auto-placer and an obstacle-avoiding wire
router are the open follow-ons there, plus hierarchical sheets, buses, off-page connectors and
back-annotation. Cross-layer via/microvia stitching between board layers (so a net's pads on
different layers are geometrically connected) is the next embedded-side stage.

## 6e. CAM — manufacturing toolpaths (`EngrCAD.Cam`)

The CNC/CAM campaign (todo.md carries the full staged plan: FDM slicing → 2.5D CNC milling →
3-axis surfacing → HSM adaptive clearing → non-planar slicing, with toolpath/material-removal
animation cross-cutting). `EngrCAD.Cam` is a kernel-tier leaf over Core + Modeling, the
`EngrCAD.Ecad` pattern: no viewer dependency, `InternalsVisibleTo` its tests, packed like every
`src/*` project.

**Stage 1 — FDM slicing — landed, and it is deliberately a THIN layer over machinery that
already existed.** The campaign's premise was that the hard parts shipped without ever being
called CAM, and stage 1 is the measurement of that claim: the slicer's own code is layer
bookkeeping, an even-odd scanline and a text writer — everything geometric is a call into
landed machinery.

- **Layers**: exact sections at each layer's MID-height (the standard slicer convention, so a
  plane never lands flush on the part's own top/bottom face; the top plane is additionally
  clamped below the part's top so an exactly-divisible height cannot go flush either, and a
  flush INTERNAL horizontal face — which sectioning rightly refuses, an in-plane face making
  the section an area — is retried once at a deterministic +5%-of-a-layer nudge). The shape is
  lowered ONCE and sectioned N times through the same `PlanarSection` routes `Shape.Section`
  takes (the `Part.TryGetSolid` lesson — a hundred layers must not mean a hundred lowerings),
  which also means ANY representation slices: B-Rep exactly, mesh/SDF through the display mesh.
- **Walls**: successive inward `Region2dOffset`s — wall k's centreline at `bead·(k + ½)`, holes
  getting their own loops. Emitted innermost-first and NOT re-ordered by the travel linker,
  which is a decision the first test run forced: the greedy linker happily prints the outer
  wall first when its seam is nearer, and wall order is a print-QUALITY rule (the outer wall
  lands on settled neighbours), not a travel optimisation — so walls keep their emission order
  and only the infill is linked.
- **Infill**: a rectilinear scan alternating ±45° per layer at spacing `bead/density`, clipped
  to the region inside the innermost wall (inset `bead·(walls + ½)`, so the infill bead just
  meets the wall bead) by an EXACT even-odd crossing count with the half-open vertex rule (the
  `SheetHatch` lesson — a scan line through a vertex is counted by exactly one incident edge),
  anchored to the GLOBAL grid so the pattern's phase is a function of the stated spacing and
  never of where the part sits (the `SpaceFillingInfill` phase rule). Runs are linked by the
  shared `RunLinker`.
- **G-code**: the Marlin-flavour writer STATES its modes (G21/G90/M82 — a reader that cannot
  see a mode cannot check it) and the extrusion bookkeeping is an IDENTITY, not a calibration:
  every E is cumulative filament with `ΔE = segment length × BeadArea / FilamentArea`, the
  bead modelled as the stadium cross-section `h·(w − h) + π·h²/4`. The twin-decoder
  `GcodeReader` re-derives BOTH sides from the file alone (deposition length from coordinates,
  filament from E deltas), so the tests assert the identity on DECODED values — the house
  twin-decoder style, because a structural look at a G-code file proves nothing about its
  coordinates. The decoder refuses BY NAME the modes it must not guess about: `G20` (inches —
  the unit trap), `G91`/`M83` (relative modes whose absolute misreading is confidently wrong),
  `G2`/`G3` (arcs join with the CNC stages). Retraction is a stationary negative-E move paired
  with an equal unretract so the decoder can MATCH the pairs; temperatures are
  write-only-when-stated (0 writes nothing — never a zero that would cool a live hotend).

**Verification followed the campaign's own bar**: the layer grid as exact arithmetic; wall
perimeters against closed forms (an inward offset of a rectangle keeps sharp corners whatever
the join style, so wall 0's perimeter is exactly `2(a − w) + 2(b − w)` — a closed form, not an
approximation); the wall's clearance from the section boundary as an exact point-by-point claim
(the no-gouge analogue, `bead/2` to nine decimals); infill alternation asserted as
perpendicularity of the raw directions; solid-infill coverage as a MEASURED ratio with its
deviations ATTRIBUTED (the stadium bead's ~10.7% corner deficit against a rectangular slab plus
the scan's half-spacing edge margins — which is why the ratio sits below 1, never above);
determinism byte-for-byte through the writer; and every refusal by name. Docs
`examples/cam-slicing.md`; the campaign's remaining stages stay in todo.md.

**The print DIRECTION is a parameter, not a re-model** — `Slice(shape, profile,
printDirection)` rotates the part by the MINIMAL rotation taking the chosen axis to bed +Z and
slices in bed coordinates. +Z is the identity fast path (no transform node at all, so the
default slice is byte-identical to passing null — asserted through the writer); the antiparallel
case has no unique minimal axis, so it turns π about the codebase's one
`ArbitraryPerpendicular` convention, deterministic rather than a rounding accident; a zero
direction refuses by name; `SlicedPart.PrintDirection` records the choice so a consumer can pose
the result back into the part's own frame.

**The print ANIMATES with no re-meshing, and landing that added the animation system's FOURTH
track kind.** For planar slicing, the state of a print at any instant IS the material below a
plane — and a clip plane is SHADER state, so the material-addition animation honours the
"an animation must not touch geometry" rule by construction rather than by discipline.
`SectionTrack` sits beside pose/camera/deformation with the same at-most-one rule (several
plane sets on one clip state have no defined composition — a multi-plane cut is ONE track
returning the whole set); `SectionTracks.Sweep(normal, from, to, steps)` sweeps one plane with
optional STEP quantization (ceiling, so any t > 0 shows completed steps — set steps to the
slice's layer count and the reveal finishes whole layers, the way a printer does), and
`SectionTracks.Reveal(bounds, growDirection, steps)` is the print-progress law (t = 0 hides the
body entirely, t = 1 shows it whole, the offsets read off the bounds' corners with a 1% pad so
clip-shader rounding cannot leak a sliver at either end). The wiring touched every consumer of
`Animation.At` and nothing else: `AnimationSample` gained `Sections` (null = whatever sections
the render call or window carries stand — the same null-means-unchanged convention as the other
three), `OffscreenRenderer.RenderSequence` gained a per-frame-sections overload (the incumbent
3-tuple form delegates with null sections, so an untracked sequence is bit-identical; per-frame
sections ride the one-context batch exactly as the deformation scalar does, because both are
uniforms — they change what a frame LOOKS like without changing what is in it), the
`RenderToImage(scene, animation, t, …)` still gives a section track's planes precedence over
the call's own (the camera-precedence rule applied to the clip), and window playback drives
`ViewportControl.SectionPlanes`/`SectionEnabled` from the sample with clamp semantics — a
finished reveal stays revealed exactly as a finished explode stays exploded. One compile-time
tax recorded: the new overload made a bare `[]` argument ambiguous, which is what an explicitly
typed empty list in the one affected test is about.

**Stage 2 — 2.5D CNC milling — landed** (`MillTool`/`CncMill`/`CncGcodeWriter`; docs
`examples/cam-milling.md`): pocket, profile and drill, again a thin layer over landed machinery.
**Pocket clearing IS the inward-offset ring ladder** — `Region2dOffset` rings one `Stepover·D`
apart from the boundary pass inward until the region is exhausted, an island's grown boundary
(the offset's hole loops) ridden like any other loop, executed innermost-first per `StepDown`
level (the tool climbs outward, finishing at the wall) with the last level clamped to the exact
stated depth (arithmetic, not accumulation); `Stepover ≤ 0.5` PROVABLY covers the whole
reachable area (each ring clears ± a radius about its centreline and consecutive centrelines are
stepover·D apart). **Profiling is one outline offset** by the tool radius — ROUND joins, a
deliberate physical choice: that is the path a tool centre actually rolls around an outside
corner keeping contact, where a miter would lift it off the part — with holding TABS on the
FINAL pass only, evenly spaced by arc length (a stated convention, not rounding luck), each a
VERTICAL rise at the tab's own edge; the first cut of the closing stretch finishes at depth
before the rise, because a diagonal ramp would leave it part-cut (a bug caught in design, not on
a machine). **Drilling ships EXPANDED peck moves** — plain G0/G1, feed-down/rapid-up — so the
one twin decoder reads a drill cycle with nothing new (canned G81/G83 cycles are filed).
**`CncGcodeWriter` classifies a move by its SHAPE** — an XY move cuts at the feed rate, a
straight-down move plunges at the plunge rate, a straight-up move retracts as a rapid — which is
what lets ONE `MillPass` vocabulary (a 3D polyline) carry pockets, tabbed profiles and pecked
drills with no per-move annotations. Landing it forced one decoder addition with a general
lesson: `GcodeMove` gained a `Rapid` flag, because FEED STATE PERSISTS across G0 and G1 alike,
so "moves at the cut feed" silently included the rapids between loops and the round-trip length
identity failed until the flag separated them — the modal-state trap, the same family as the
G20/G91 refusals. **The verification oracle is the morphological OPENING**: a radius-r tool can
reach exactly `grow_r(shrink_r(region))`, so the union of the passes' stroked footprints (the
machined-stock simulation, `Stroke` + `UnionAll`) must equal it — asserted within 1% — and a
rectangular pocket's unreachable corner residue is CLOSED FORM, `(4 − π)·r²`, asserted directly;
the no-gouge claim is exact and point-by-point (every pass point ≥ r from the region boundary,
including an island's), depth levels are arithmetic (`5 @ 2 → −2, −4, −5`), the tabbed pass
lifts exactly `tabs` times to exactly `−depth + tabHeight`, the decoded cut length equals the
operations' own within formatting precision, and the program is byte-deterministic. Filed with
the campaign at the time — and since landed with their own records below: climb/conventional
selection, helical/ramp entry, canned cycles, the ⚠ feeds/speeds catalogue, rest machining,
and writer-side G2/G3 arc fitting; still open: native arcs carried end to end from the exact
curved-profile tier, and the material-removal animation over recorded stock states.

**FDM supports — columns under the measured overhang field** (`PrinterProfile.SupportOverhangAngle`,
0 = off — the write-only-when-stated path, so a profile stating nothing slices byte-identically).
The detector is the `Manufacturability` rule applied to the ORIENTED shape's own mesh — the
threshold converted to a sine ONCE and compared on the DOT PRODUCT (`−n·Z > sin θ`), never on a
derived angle, because `asin` round-trips `1/√2` an ulp high and a wall built at exactly 45°
would read as an overhang (the recorded lesson, inherited rather than re-learned) — and the
tessellation reuses the slice's ONE lowering rather than lowering again. Three decisions carry
it. **(a) The per-layer region is the projection of what is still ABOVE the layer, not a
per-facet bounding box**: a facet partly below the layer plane contributes its Sutherland–Hodgman
clipped upper part, so a slanted overhang's supports track its own height — per-facet MinZ
bounding would stop every column at its facet's lowest point and leave the high side hanging,
which on a coarsely-tessellated planar ramp is most of the overhang; the wedge test asserts the
clip DIRECTLY (`support x ≤ 10 − layerTop` on the 45° slant, the slant's own equation). The
active facet set shrinks monotonically as layers ascend (sorted by MaxZ, dropped from the front)
and the union is recomputed only when the set changed or a facet is being clipped, so a flat
underside — the common case — reuses its cached union at every layer below it. **(b) A facet
resting on the bed excludes itself with NO special case** — nothing of it is above any layer's
top, so no layer ever finds material to support — which is what makes "a plain box with supports
stated writes byte-identical G-code" a theorem rather than a filtered edge case (the part's own
bottom face is not an overhang; it is the print). **(c) The XY gap is a subtraction, not a
distance test**: the part's section grown by `SupportGap` (`Region2dOffset`) is subtracted from
the support union per layer, so a support pattern point on the region boundary stands exactly
the gap from the wall — asserted point-by-point with the grown region's inscribed-arc tolerance
(1e-3) allowed for, the no-gouge assertion shape. The pattern is sparse ONE-direction lines
(`SupportSpacing`, the breakaway convention — no alternation, so the stack shears apart), linked
by the shared `RunLinker` and printed before the walls. The footprint's degeneracy guard is
RELATIVE to the loop's own extent (an absolute epsilon on an area is the recorded trap). Filed
rather than shipped, each named in the docs: a support Z-gap (one layer of air under the
overhang for cleaner breakaway — v1 supports run to the underside exactly, which the table test
pins as `lastSupport.Z == undersideZ`), interface layers, and supports-on-model awareness
(v1 columns run bed-to-overhang, printing around any part material in between).

**Solid top/bottom shells landed** (`TopSolidLayers`/`BottomSolidLayers` on the profile,
0 = off byte-identically — the write-only-when-stated path, pinned by a same-G-code
assertion; docs `cam-slicing.md`), closing the biggest visible gap between the stage-1 slicer
and a real print: every layer used to be sparse interior all the way to the skin. The rule is
the neighbour-window one: a spot of a layer's infill core is SOLID exactly where the
intersection of the next N layers' sections above (or M below) does not cover it — a spot
within N layers of air — with the intersection folded through `Region2dBoolean` and
subtracted from the core, skins filled at the bead spacing and the sparse pattern keeping the
remainder, both linked as one travel group. A window reaching past the stack meets air, so
the part's own top and bottom layers come out wholly solid with NO special case, and zero
sparse density still lays the skins (a hollow part keeps its lids). Landing it moved the
sectioning to an upfront pass over all layers (the neighbour lookup needs every section
before any layer's paths are built) — a pure reordering, byte-identical with shells off. The
fixture with teeth is the STEP: a plateau exposed on one half and carrying a tower on the
other, where the solid/sparse split must land exactly AT the tower's wall — a slicer that
shells whole layers or none passes a plain box and fails there, which is what makes the plain
box the wrong fixture. Filed beside it in the PrusaSlicer-parity entry: monotonic top-surface
fill, ironing, bridges, and the skin-to-sparse anchor margin.

**Per-feature speeds and the print-time estimator landed together** (the parity family that
unlocks the cooling model; docs `cam-slicing.md`). The speeds are optional per-role values
resolved by ONE rule (`PrinterProfile.SpeedFor` — a stated `FirstLayerSpeed` wins on layer 0
whatever the role, a solid skin falls back through the infill family, everything unstated
resolves to `PrintSpeed`), and the writer reads that rule per path — so a plain profile's
G-code is byte-identical, pinned by the STRONGER structural claim that a profile stating
speeds differs from the baseline ONLY in its F words (strip `F\d+` from both outputs and
compare). **The estimator reads the DECODED program, deliberately**: the estimate is of what
the file says, exactly as the printer will read it, so a wrong feed or a lost move shows in
the time the way it would on the machine — the twin-decoder doctrine applied to a derived
quantity. The answer is an honest BRACKET rather than a number: the lower bound runs every
move at its own feed (a machine with infinite acceleration), the upper accelerates every move
from rest and back by the closed-form trapezoid — `d/v + v/a` when the move reaches full
speed (`d ≥ v²/a`), `2·√(d/a)` when it stays triangular, an E-only retract running the
extruder axis at `|ΔE|` — asserted as direct arithmetic, with the infinite-acceleration
limit collapsing the bracket onto the lower bound and the bracket monotone in the
acceleration. Junction-deviation cornering, per-axis limits and jerk are filed as the
refinement that NARROWS the bracket; they cannot move its ends, which is what makes the
bracket the honest v1 rather than a placeholder.

**The FDM FINISH wave landed the practical slicer feature set** (docs `cam-slicing.md`; the
research-grade trio — Arachne variable-width perimeters, lightning infill, tree supports —
stays filed as such). The design decisions worth keeping: **every infill pattern holds the
stated DENSITY by scaling its spacing to its direction count** (grid two directions at twice
the spacing, triangles three at three times), so a density means one thing across the family,
and GYROID is the TPMS level set sectioned at each layer's own z — a private level-FUNCTION
field (deliberately not `Sdf.Gyroid`, the thickened lattice SOLID) through
`SdfContours.OnPlane` at level 0, chained by the surfacing code's own exact-equality chainer,
clipped to the core at vertex granularity. **A spiral vase's z is ramped by the WRITER along
the wall's own arc length** (the slicer stays 2D — the model unchanged), the layer-start Z
move skipped above the base because the previous turn ended exactly one layer below; its
contradictions (a second wall, infill, top skins, supports) refuse by name at validation, and
a multi-island layer refuses before any path is built. **Cooling reads the same speeds the
estimator does** (one slowdown factor per layer, floored), and the volumetric cap applies
LAST because the melt limit is the machine's, not the profile's. **The raft is a PREPENDED
layer set with the part lifted** — geometry stands still, only Z shifts — with the skirt/brim
moved to the raft's own first layer by making adhesion a function of the first EMITTED layer
rather than part-layer zero. **The support Z-gap moves the clip plane, not the facets**
(`ClipAbove(loop, layerTop + gap)` with the drop condition shifted to match — gap 0 is
bit-identical), interface layers densify AND turn perpendicular near the contact, and
blocker/enforcer SHAPES are the code-first paint-on support (a blocker sectioned per layer
like the part; an enforcer forcing support under any downward facet inside its volume — the
mutation test: a 45-degree chamfer a 50-degree threshold ignores gains supports exactly where
the enforcer covers it). **A bridge is skin the layer DIRECTLY below leaves in air** — never
the first layer, because the bed is not air, which is the reasoning error the naive
bottom-window rule makes — filled along the region's own long axis. **Ironing forced
`SlicePath.Flow`**, and the extrusion identity GENERALISES rather than breaks: filament =
sum of length x flow x ratio, asserted through the decoder. **`RetractionExtraRestart` is the
one knob filed WITH a reason instead of built**: unmatched extra filament breaks the matched
retract-pair contract the twin decoder verifies, and the identity is worth more than the
knob. Compensations (elephant foot / XY / hole) apply to the STORED sections so walls, skins
and supports read one geometry, hole compensation re-winding each hole loop CCW before
growing it (the signed-area check, since a stored hole loop's winding is the region's own
business).

**The integration wave closed the slicer's outward seams**: custom G-code snippets pass
through with `{layer}`/`{z}` substituted and NOTHING validated at write time — deliberately,
because the twin decoder reads the finished file, so a snippet smuggling a relative-mode
`G91` or an inch-mode `G20` refuses THERE by name, which is a stronger guard than any
write-side allowlist (the decoder cannot be talked past). Fuzzy skin is the pattern-phase
rule applied to NOISE — the displacement is a stateless hash of (layer, point index), so two
slices are byte-identical and there is no RNG state to drift; it touches the outermost wall
only, never layer 0 (adhesion wants a flat first layer), pinned bit-for-bit on both
exemptions. `FilamentByRole` made `FilamentUsed`/`ExtrudedVolume` FLOW-AWARE (Σ length·flow —
backward-identical since flow is 1 everywhere ironing is off), and the per-role split sums to
the total exactly. `FdmPlating.Plate` is the plating story in one call: `Packing` arranges,
each part rests on the bed plane, and the returned UNION slices whole — disjoint parts
section into disjoint islands, so every per-island feature (walls, brims, skins, supports)
works with nothing new, and the out-of-room refusal is the packer's own, naming the part.

**Variable layer height closed the last practical parity box**, and the honest part is what
it did to the extrusion identity: each layer's E arithmetic now reads its OWN stadium
cross-section (`PrinterProfile.BeadAreaFor`), the flow-aware totals go per-layer, and the
test asserts BOTH directions — the slicer's height-aware filament total matches the decoder,
AND the naive single-ratio identity measurably FAILS on a mixed-height print (2%+), because
an identity that survives the feature it should reflect is a tautology. The adaptive schedule
is the stair-step cusp criterion (`h ≤ cusp/|n_z|`, two-pass band refinement, bed-resting
facets excluding themselves — the supports rule again), with the cusp height a REQUIRED
engineering input (the minimum-member-size rule: it IS the stated surface quality). A table
is validated as PRINTABLE per layer (≤ the bead) and as COVERING the part, both refused by
name with the deficit stated.

**The stage-2 completion pack** (climb/conventional, canned cycles, the feeds catalogue)
closed three filed boxes with one derivation, one reconstruction and one transcription.
Climb-vs-conventional is DERIVED, not transcribed — an M3 right-hand cutter's tooth at the
contact point moves WITH the feed exactly when the material is on the LEFT of travel (the
ω×offset cross product), so climb = material-left, which resolves per loop as "CCW iff the
material is inside it": an outside profile keeps CCW, a pocket ring goes CW, and an island
pocket orients its outer-derived and island-derived rings OPPOSITELY — applied by measured
shoelace sign (never the offset machinery's emission order) with the reversal about the
loop's own start point, so linking and cut length are untouched and the test asserts the
conventional multiset of signed areas is the climb one negated (to round-off — the reversal
reorders the shoelace summation, the non-associativity lesson in a one-line costume). Canned
G81/G83 cycles are a WRITER RE-SPELLING with the parameters RECONSTRUCTED from the pass's
own moves and verified against the peck arithmetic — a ladder that skipped bites falls back
to expanded emission (sound in the accept direction; the first draft's last-bite override
accepted exactly such a ladder and the fallback test caught it) — while the DECODER expands
cycles under the real Fanuc semantics (bites from R, not from the stock top: the canned
bites sit R above the expanded twin's, conservative, with sites and final depths identical),
modal bare-X/Y re-execution included, refusing a cycle missing Z/R/Q by name since a guessed
drill depth is confidently wrong geometry. `CncToolLibrary.Suggest` spells the two chart
identities once (`rpm = 1000·Vc/(π·D)`, `feed = rpm·flutes·chipload`) over the ⚠
`MillMaterials` transcription (asserted in the chart's own units, coverage held by
reflection), and the spindle cap preserves the CHIP LOAD rather than the feed — holding the
feed at a capped rpm would thicken every chip past what the flute clears.

**Sequential printing is a clearance theorem plus string discipline, and the filed
prediction dissolved again**: the backlog reserved a swept-cylinder SDF query for the
gantry/nozzle check, and the honest model is simpler — a printer's sequential capability IS
two numbers (the extruder clearance radius and the gantry height, ⚠ per machine), so the
check is a pairwise XY bounds gap (an UNDER-estimate of the true footprint gap: refuses
some legal plates, never accepts an illegal one — the sound direction) plus the height
rule: ascending print order makes the gantry always pass over shorter completed work, and
at most ONE part may exceed the gantry height, printed last (a second has nowhere legal to
go and is refused naming both). `FdmPlating.Arrange` is `Plate` minus the union — the
per-part identity a print order is a statement about, extracted rather than re-derived so
`Plate` is exactly its union. The combined G-code is the per-part programs with middle
headers/tails stripped (the writer gained an internal sectioned overload whose both-true
form is `Write` byte for byte), each handover a hop above everything completed + the XY
move to the next part's own start BEFORE descending — descend-first would put the nozzle
at first-layer height over the completed neighbour, the crash the mode exists to avoid —
plus `G92 E0`, which the twin decoder already understands: the combined program's filament
total equals the sum of the parts' own (asserted at 1e-6 relative) and the layer-Z
sequence drops exactly once per handover.

**Stage 3 — 3-axis surfacing — landed** (`CncSurfacing.Raster`/`Waterline`/`ScallopHeight`/
`StepoverForScallop`; docs `examples/cam-surfacing.md`), and it is the stage where the implicit
engine pays DIRECTLY rather than as a bridge: **the cutter-location surface of a ball-nose tool
IS the field's r-offset** — a ball of radius r touches the part exactly when its centre is at
distance r from the surface — so both strategies read `Shape.ToImplicit()` instead of
approximating an offset mesh, and the no-gouge claim is the field's own inequality
(`sdf(centre) ≥ r`, asserted point-by-point on a dome-on-plate union for both strategies).
**Raster is a SPHERE TRACE and the Lipschitz bound is the gouge-freedom proof**: descending the
vertical ray by `sdf − r` per step can never cross the r-offset (the `SurfaceCull` argument run
downward), so the trace is exact at convergence and CONSERVATIVE at its two failure modes — a
stall (the classic sphere-tracing graze along a vertical wall's offset) and the iteration cap
both leave the centre HIGH, stock left rather than a gouge, and a ray that never meets the
offset clamps to the part's own bottom. A flat top is machined EXACTLY (a box field is exact,
every interior tip on the face to the trace tolerance) and the dome apex is touched at its own
height because the rows and samples are GRID-ANCHORED (the slicer's phase rule — (0, 0) is
always a sample). **Waterline is the r-isolevel read where it lives**: the CL contour at a
centre plane is exactly the field's r-contour there, so `SdfContours.OnPlane` (the section
overlay's own marching squares) extracts it, the segments chain into loops by that contract's
exact endpoint equality, and each point is polished onto the isolevel by an IN-PLANE Newton
step — in-plane because a full-gradient correction would move the point off the waterline's z,
and skipped where the in-plane gradient is weak (a near-horizontal crossing has no in-plane
direction that changes the field, and the honest accuracy there is the marching-squares
crossing error, stated rather than averaged into a blended tolerance). On a vertical cylinder
every waterline point lands at `R + r` to 1e-6 with exactly one closed loop per level; the
grid spans the part plus the radius plus two cells, or a loop exits the boundary and comes back
open (emitted open, honestly — the tests would read it as a failure). **The scallop is a chord
identity, not a rule of thumb**: `h = r − √(r² − (s/2)²)`, `StepoverForScallop` its exact
algebraic inverse (round-trips to 1e-12), and the classic `s²/8r` is MEASURED as its
small-stepover expansion (within 1% at s = r/3) rather than shipped as the formula. Where the
field is a correct-sign LOWER BOUND (a CSG difference near its tool's fictitious faces) the
r-isolevel lies FARTHER out than the true offset — stock left, never a gouge, the conservative
direction inherited from the field contract. The stage-2 `CncGcodeWriter` carries surfacing
passes UNCHANGED — a move's meaning is its shape, so an XYZ-combined raster move cuts at the
feed rate with nothing new — which is the payoff of classifying moves rather than annotating
them. Filed by name at the time — and since landed with their own records: flat/bull-nose
cutter-location surfaces, a raster direction other than X, no-retract row linking, rest
machining, and holder/shank collision; the last of which — adaptive
stepover — has since landed too (the record below).

**The material-removal record and the toolpath animation landed as the PAIR the animation
system's own rule splits them into** (`CncStock.Simulate` in Cam; `PathTracks.Follow` in
Viewer.Core; docs `examples/cam-milling.md`). The rule — a pose track's answer is matrices,
never a re-meshed part — cuts this feature cleanly in two: the TOOL along its path is
matrices-only and rides the animation system as an ordinary pose track (`FollowPathTrack`:
arc-length parameterization so the tool crosses corners at constant speed, the explode-path
rule; endpoints exact; every bystander instance bit-identical; a wrong instance path fails at
CONSTRUCTION naming what exists — the MechanismTrack graft rule), while the CHANGING STOCK has
no matrices-only form at all, so it is recorded DATA — `CncStockState` values at N cut-length
fractions, stills and exports rather than a live clip, the transient-thermal precedent
(a per-step colour animation was likewise scoped separately rather than squeezed through the
deformation uniform). **The 2.5D swept volume is CLOSED FORM and that is what makes the record
exact**: a constant-z run occupies its stroked footprint (`Region2dOffset.Stroke`, the same
region the stage-2 opening oracle measures) from its level up through the stock, a vertical
descent bores an inscribed-32-gon disc — so a drilled state's volume is an exact polyhedral
prism, asserted to 1e-9 relative — and a pass moving in XY and Z at once (a surfacing raster
row) is refused BY NAME, its swept volume being no prism. **Two build findings.** A
single-point drill pass removes material through the PASS-ENTRY rule, not a special case:
every pass is entered by a plunge from above (the G-code writer's own convention), so a first
point below the stock top bores its disc — which for a stroked pass is contained in the run's
own round start cap and for a peck-0 drill IS the hole. And the subtraction had to be cut into
z BANDS (one level to the next, each band's cross-section the union of every level at or below
it) through the MESH imprint boolean: the first spelling — one prism per level running to the
top — hands the boolean the ENTIRE side wall twice wherever successive levels repeat a
footprint (which is every pocket), and the B-Rep route the Shape compiler prefers for
B-Rep-able operands refuses the chorded stroke profiles outright ("arrangement tracing did not
close") while taking minutes on the ones it accepts; banding leaves only the horizontal
stacked-plates coincidence the imprint boolean's coplanar tier is built for, and the suite runs
in seconds.

**Flat and bull-nose cutters landed on the raster, and the filed prediction was overturned
by an argument worth keeping**: the backlog spelled the feature as "the rounded-cone distance
the SDF vocabulary already spells", and the field route does not survive the disc. A
flat-bottomed tool's CL condition is min over its bottom disc of the field ≥ r, and any
CERTIFIED evaluation of a minimum to precision ε through a 1-Lipschitz oracle needs a
cover at radius ε in the worst case — Ω((a/ε)²) evaluations — and the worst case is not a
hostile fixture but a horizontally FLAT field, i.e. every plateau a flat cutter exists to
finish (a B&B certificate collapses exactly where the field stops sloping). The ball-nose is
special precisely because its disc is a point, which is WHY stage 3's field identity worked.
So flat/bull ride the TESSELLATION as the textbook drop-cutter (`MillCutter` +
`DropCutter`): one bottom-profile function f(ρ) all three kinds share (0 on the disc, the
torus rise on the corner, +∞ past the flank), contact the max over three modes — a VERTEX
exact (v.z − f(ρ)), an EDGE maximized by a bracketed 1D scan (a torus–line tangency is a
quartic, the sharp-corner fillet lesson, so the scan is the honest spelling), a FACE closed
form (slope match at ρ* = a + r·s/√(1+s²), taken only when the contact lands inside the
triangle) — over a 2D bucket grid, sharing the SDF route's serpentine loop verbatim
(`SerpentineRaster`, one grid rule for both routes). The oracles are equalities where the
geometry allows: the FLAT SPOT over a dome apex is z == S exactly (the apex vertex under the
disc reads f = 0), a plate reads its top exactly out to one disc radius past its edge (the
edge mode), the APT rim form √((S+r)² − (d−a)²) − r holds one-sided against the inscribed
mesh, and a ball pushed through the MESH route agrees with the exact field route to the
chord error — two constructions checking each other, the comparison band honestly
slope-amplified near the silhouette where dz/dd diverges. A stated ball cutter takes the
exact route BYTE-identically; waterline refuses flat/bull by name (their waterline is the
contour of the mesh dilated by the disc — a 2D arrangement, filed).

**Model-fed drilling and the raster angle closed two more residuals with one structural
move each.** `CncDrilling.FromShape/FromPart` is the one-declaration rule at the CAM
boundary: a `Shape.Drill`/`ThreadedHole` call already states diameter, depth and positions
— it is what `HoleTable` letters for the drawing — so the drill program reads the SAME rows
rather than transcribing coordinates beside the model, and `HoleTableRow` gained the
numeric drilling data (`DrillDiameter`/`Depth`/`Plane`) the callout text could not carry: a
counterbore/countersink contributes its THROUGH bore (the larger feature is a milling
operation, not a drill), a threaded hole its tap-drill pilot via `StandardHoles.Tapped`,
and the M6-pilot-Ø5 beside a Ø5.5 clearance bore is the discriminating fixture (two calls,
two diameters, grouped per tool with the counts conserved against the hole table's own).
Depth travels VERBATIM because the conventions already agree — the model's depth is to the
SHOULDER and so is a drill cycle's, so a real drill's tip reaches deeper exactly as
`WithTipAngle` draws it; a tilted placement plane refuses naming the row's LETTER (a
3-axis program runs straight down; which face goes up is the fixture's decision), as does
a second plane height (v1 is one setup). The raster ANGLE rides the one `SerpentineRaster`
rule both cutter routes share: the grid anchors in the ROTATED frame (the phase rule — a
pattern is a function of the stated spacing and angle, never of where the part sits) and a
quarter turn is EXACT (a sign swap, never a `cos`, the glTF-root lesson), pinned by the
test that a 90° raster is the 0° raster's grid TRANSPOSED bit for bit — an assertion only
the sign-swap spelling can pass, since `cos(π/2)` is 6.1e-17 and not 0.

**Rest machining is the opening identity used twice.** `PocketRest` computes the rest
region as `region − opening(region, R₁)` and pockets each residue piece over
`intersect(grow(piece, 2·r₂), region)` — and both constants carry proofs rather than
margins. The grow is 2·r₂ by a two-line sufficiency: any residue point p the finish tool
can legally reach admits a disc B(c, r₂) with |c − p| ≤ r₂ and c at least r₂ off the
region boundary; every point of that disc is within 2·r₂ of p and inside the region, so
the disc lies in the milled region and its centre in the r₂-inset — exactly where the
ring ladder walks (the first draft grew by r₂ and measurably under-covered, 8.56 mm²
uncovered against the 1.93 closed form). And the opening is grown by ε before the
difference because it touches the wall TANGENTIALLY at every residue cusp — the 2D
arrangement's recorded hostile case — which makes the contact transversal at the cost of
an ε-band the 2·r₂ grow wins back. Residues that cannot hold a disc of a stated minimum
thickness anywhere (default r₂/4, 0 keeps all) are skipped as flattening noise: a chorded
arc's junction corners leave sagitta-scale crumbs the opening genuinely cannot reach, and
the first honest-empty fixture — a rounded rectangle whose corner radius exceeds the rough
tool's — duly emitted micro-passes for them until the filter said what a feature IS. The
oracle extends the module's own: the COMBINED rough+rest footprint equals the finish
tool's opening within 1%, the uncovered remainder is exactly (4−π)r₂², and the no-gouge
claim holds point-by-point against the ORIGINAL boundary even though the tool centre
legitimately stands in cleared space (asserted from both sides: at least one centre IS
outside the residue, or the feature did nothing).

**Helical ramp entry replaced the plunge, and the diagnostic mattered more than the
helix.** `Pocket(rampAngleDegrees:)` enters each depth level on a helix descending from the
previous — already cleared — level about the level's own first point (radius under the tool
radius so no core post is left, capped by the MEASURED point-to-boundary room with a plunge
fallback when too tight, pitch 2π·r·tan(angle) so the stated angle IS the descent slope
along the arc, one flat closing turn so the ramp floor is cleared), and the level's rings
then run as ONE pass linked AT DEPTH — a link cut through one stepover of web — wherever
the exact segment-to-segment distance to the boundary clears the tool radius, with a
blocked link (a concave pocket's gap) flushing the pass and re-entering the incumbent way.
The oracle reads the decoder: every stationary-XY descending move ends at a level TOP
(cleared air) where the plunge program's end at level BOTTOMS, in material. **Building it
exposed a pre-existing ordering defect**: the ring ladder linked ALL of a level's loops in
one nearest-endpoint pass, which is pen-dependent — measured, level −4 of an ordinary
rectangle pocket started at its BOUNDARY ring, contradicting the method's own
innermost-first contract and the climb rule's "inward is already cleared" premise (and
incidentally stranding the helix at a point with exactly zero room). Loops now link WITHIN
each ring level, innermost first, in both emissions — the fix the doc comment's claim
always described. And one composition note worth recording: a helix's XY retraces one
polygon per turn, so its footprint contains EXACTLY-coincident repeated segments — the 2D
arrangement's hostile case — and a repeated segment adds no footprint, so any stroke-union
consumer (the coverage oracle, a future stock-sim composition) dedupes segments first.

**The flat/bull waterline landed as the silhouette-dilation contour, with the bull corner
a certified band ladder.** A flat cutter at tip z collides with exactly the material above
its own plane within R, so the collision region IS the part's XY silhouette above z grown
by R (the landed `MeshPlaneCut` keep-above + `PlanarSection.SilhouetteOfMesh` +
`Region2dOffset` machinery, nothing new), and its boundary is the cutter-location contour —
exact against the mesh. The bull-nose corner's reach grows with height above the tip
(f(ρ) inverted), and the honest discretisation direction was chosen the way the drop-cutter
work chose against the field: a SAMPLED stack under-covers between samples, which is the
gouge direction, so each band k clips the mesh above z + r·k/K and grows by the band's
OUTER reach a + √(r² − (r − r(k+1)/K)²) — every band over-covers its own slice, the contour
stands off AT LEAST the true CL distance, and K = 1 degenerates to the sharp envelope. The
45°-cone oracle separates all three answers on one fixture: the banded standoff addend
equals its own closed form max_k(reach_k − h_k) = 3.661 for Ø8 r1 at K = 4, bracketed
between the exact a + r(√2 − 1) = 3.414 (never gouged below) and the sharp envelope's 4.0
(measurably beaten), while a vertical wall reads exactly R for every cutter kind (the
corner never engages a wall except at its equator) and a flat cutter on the cone reads the
cone radius + R exactly, one-sided against the inscribed tessellation.

**No-retract row linking is one sentence of design**: the connector between a serpentine
row's end and the next row's start is sampled ON the cutter-location surface through the
SAME tipAt the rows themselves use, so it carries exactly the fidelity a within-row chord
does — gouge-free by the same construction, nothing new to prove — and merging the rows
into one pass replaces one plunge per row with one per operation. Opt-in (`linkRows`,
default off byte-identical), both cutter routes through the one serpentine rule, the row
samples themselves asserted IDENTICAL to the unlinked emission (linking adds only the
connectors).

**Laser cutting was the predicted near-free adjacent and measured as one**: the whole
feature is ONE outward offset — growing the region by kerf/2 moves its outer loops OUT into
the waste and its hole loops IN into the holes, which are exactly the two beam centrelines,
so the kerf compensation needs no per-loop case analysis and the freed part measures the
drawn dimensions by construction (the perimeter oracle is closed form: 2(w+h) + 2π·kerf/2
outside with round corners, 2(w+h) − 8·kerf/2 inside where an inward rectangle offset keeps
sharp corners). Holes cut FIRST (the release rule), the G-code is GRBL's M4 dynamic-power
flavour with NO Z word anywhere (a laser has no depth axis, and emitting one would make the
file mean something on the wrong machine), and the twin decoder reads the program unchanged
— M4/S are modes — so the cut length is verified through the decoded file at the writer's
own micron coordinate-quantization grade rather than against the plan's arithmetic.

**Stage 4 opened with trochoidal slotting** (`CncHsm.TrochoidalSlot`; docs
`examples/cam-milling.md` §HSM), and the finding that justifies the campaign's whole framing —
"the engagement angle computed from the evolving stock and BOUNDED by the stated maximum,
never inferred" — arrived in the first fixture: **the textbook straight-cut engagement
relation `a = r·(1 − cos φ)` is measurably WRONG for a trochoid**. Seeding the advance from it
at a 60° bound measured **90°** from the evolving stock, because a trochoid cuts against the
previous loop's CONVEX swept boundary and a convex opposing surface engages more circumference
than a straight wall at the same radial width (and the naive circle-against-circle bound
overshoots the other way at 143°, since it ignores what the current loop's own sweep already
removed). So the advance is SOLVED — bisection against a steady-state model built from the
same evolving-stock rule the tests measure with (six loops at the candidate advance, the last
loop's tool-circle arc not covered by the swept prefix) — and the verification re-measures the
REAL path independently (spiral hand-off and slot ends included), with a straight-line slot
cut as the ~180° control that proves the instrument reads burial at all. **The entry is an
Archimedean spiral-out at the same pitch, and its honesty is stated rather than hidden**: a
spiral-out's contact ARC is fundamentally wide (~180°, however small the pitch — the tool
orbits inside the hole it is opening) while SHALLOW, so its bounded quantity is the radial
step per turn (the chip load — why entry feed reduction exists), and the arc bound is a claim
about the trochoidal phase, measured from one loop after the spiral reaches full radius. Two
smaller findings: the loop is sampled by INDEX, never by accumulating theta (the total angle
is an exact multiple of the step for round fixtures, and an accumulated loop emits a final
segment a few ulps long — too short for the stroke's normalize, too long for exact-duplicate
compaction: the epsilon-guard-Ceiling lesson); and the trochoid × stock-record composition is
FILED rather than papered over — the swept union's boundary carries a near-tangent scallop
cusp per loop (circles of radius R + r whose centres sit one advance apart cross at a few
degrees), the mesh imprint boolean's hostile family, and a footprint-smoothing tolerance tried
against it measurably broke honest fixtures while fixing nothing. Swept footprint = the slot
stadium `L·W + π(W/2)²` within 2%, no-overcut point-by-point, depth levels arithmetic,
byte determinism. Filed: general adaptive (constant-engagement) pocketing over the evolving
stock region — of which this closed-form cycloid family is the honest first step — helical z
entry, and trochoidal linking of `Region2dThickness` necks.

**G2/G3 arc output landed as writer-side RECOVERY, and the guard is the finding**
(`CncGcodeWriter.Write(..., arcFitting: true)` + `GcodeReader`'s arc expansion; docs
`examples/cam-milling.md` §Arc output). The offset machinery places its corner vertices
INSCRIBED on the true tool-compensated arc (measured: a rounded plate's corner points sit on
one circle about the corner centre to 3.6e-14), so fitting the circumcircle of a run and
extending it while points stay on-circle at the weld tier RECOVERS the arc the chording
lost — a 40×24 r6 plate profiled outside by a Ø6 tool emits exactly four arcs at the
compensated radius 9. **The on-circle test alone shipped a gouge, and the failing case is
the recorded mirror-symmetric-fixture trap arriving LIVE in production**: IEEE negation is
exact, so two points and their y-mirrors are EXACTLY concyclic, and the symmetric part's
straight side flanked by its two corner tangency vertices read as four points genuinely on a
675 mm circle — on it to the bit, so no tolerance on the residual can see the defect,
because what is wrong is the chord BETWEEN the points: the fitted arc bulged 0.027 mm across
the 12 mm straight side, into the part. The repair is a **sagitta cap per accepted chord**
at the file's own 1e-3 coordinate quantum (`radius·(1 − cos(step/2)) ≤ 1e-3`): under it the
substitution is invisible at the resolution the file can state (a genuine inscribed corner
chord measures ~3e-4 here, the phantom 27×), and the cap is DERIVED from the writer's
three-decimal format rather than tuned. The test oracle is therefore the no-gouge form, not
a radius check — every decoded point of the fitted program within 2e-3 of the chorded
polyline — beside the closed-form length (2·(28+12) + 2π·9) and the byte-identical off
path. The decoder half expands I/J-form arcs into 5°-sampled sub-moves (so every downstream
identity — cut length, kinds, E conservation — reads the arc as the fine polyline it
machines as, with the extrusion distributed evenly, exact for a constant-flow arc) and
refuses BY NAME the ambiguous `R` form (two centres past 180°), a missing centre (a guessed
arc is confidently wrong geometry) and endpoints disagreeing about the radius (a mis-stated
centre cuts a spiral). One measurement kept honest: comparing lengths THROUGH the decoder is
a statement about the decoder's own 5° re-chording, not about the fit — the 5° expansion of
a true arc is SHORTER than the 1° source chords it replaced, so "fitted length exceeds
chorded length" is false through that instrument even when the fit is perfect, which is why
the length assertion is against the closed form.

**Holder collision landed as the flat drop-cutter's question asked at the holder's radius**
(`CncHolder.Check`/`ToolHolder`/`HolderReport`; docs `examples/cam-surfacing.md`): the
holder is a flat disc of the holder diameter whose bottom rides `StickoutLength` above the
tip — the CONSERVATIVE envelope of any real tapered holder — so a pass point collides
exactly when the surface under the disc reaches above `cl.z + stickout`, which is the FLAT
drop-cutter height at the holder's own radius. The check rides the same vertex/edge/face
contact arithmetic the flat cutter rides (`DropProbe`, the bucket grid extracted from
`DropCutter.Raster` as a pure move — the raster is byte-identical — because the bucket cell
is sized to the REACH and a holder's disc is wider than its cutter's, so each consumer
builds its own probe), which is what makes it impossible for the holder check and the flat
cutter to disagree about what a disc touches. **The deliverable is `MinimumStickout` =
max(required − cl.z) — the number that turns a failing setup into a passing one — and at
exactly that stickout the setup PASSES**, because zero clearance is resting contact rather
than a collision (the interference checker's own rule; a report whose own minimum the check
then refuses would be a useless number). Checked against the FINISHED part with the
boundary stated: in-process stock is more material, so the check is exact for finishing
passes — where holder collisions live — and a lower bound for roughing. **The fixture
finding is that the obstacle height is NOT the closed form**: the raster runs one grid step
past the part bounds, and there the ball's CL dips BELOW the top face wrapping the outer
edge — tip = √(r² − d²) − r exactly, −1 at a corner sampled √5 away in XY — so a boss
that a RIM point's disc can reach adds that dip, and the first fixture's honest minimum was
13, not the boss's 12. The fixture moved (a small boss centred in a large plate, out of
every rim disc's reach) rather than the tolerance, and the dip is recorded because it is
real setup arithmetic a machinist estimating "obstacle height plus a bit" misses the same
way. Verified: the minimum stickout exactly the boss height at the field-trace grade;
collisions LOCAL — no colliding point farther than the holder radius from the boss
footprint, which is what separates a disc query from a bounds test; a narrower holder
shrinking the collision band by exactly the radius difference while the minimum stays the
boss height (reach decides WHICH points collide, not how tall the obstacle is); a drill
operation needing depth-plus-rise; determinism; and refusals by name, including a holder no
wider than its cutter refused as VACUOUS — such a disc cannot collide before the cutter's
own flank engages, and the number to verify there is the flute length, which the check does
not model.

**Adaptive stepover landed by inverting which number is HELD** (`CncSurfacing.AdaptiveRaster`;
docs `examples/cam-surfacing.md`): a uniform raster holds the row spacing and lets the
scallop grow with the tilt; the adaptive raster holds the SCALLOP and lets the spacing
follow the surface — each next row is placed by bisection on the MEASURED worst 3D distance
between corresponding CL points of the row pair, pushed through the same chord identity
`ScallopHeight` states (`h = r − √(r² − (d/2)²)`), so no new formula exists to disagree
with the uniform one. **On a tilted plane the chord between CL points IS the surface
distance, so the spacing is EXACTLY cos θ times the flat spacing** — held at 45° by test
against a boolean-free ramp fixture (an extruded right triangle rotated so the hypotenuse's
slope runs across the rows) to 1e-5 with the flat plate taking the full flat spacing to
1e-12 — and on curved surfaces the chord is first-order (under on convex, over on concave),
stated rather than hidden. Three decisions carry it. **The governing radius is the CORNER
radius** (a ball's is its own): the cusp between passes is cut by the corner torus, so a
bull-nose adapts on its corner radius — a flat plate under a 6 mm bull with a 1 mm corner
spaces at StepoverForScallop(1, h), asserted — and a FLAT cutter is refused by name, since
it leaves facets, not scallops, and the chord identity has nothing to govern. **Rows anchor
to the PART, deliberately outside the phase rule**: the rule makes a pattern a function of
its stated spacing, and a variable spacing has no stated number to be a function of — the
pattern is a function of the surface, which is the feature. **At a cliff the spacing FLOORS
at 1/32 of the flat spacing and moves on**: a near-vertical wall's CL-point distance is
dominated by the drop, not the row spacing, so no spacing can meet the target there and
stalling would be a refusal of every part with a steep wall — the wall's finish is governed
by the flank, which no stepover rule can change (asserted: the march completes past a
12-tall wall, the floor engages only across it, and the row count stays far under the
all-floored bound). **One FP finding worth keeping**: at exactly the flat spacing the cusp
equals the target MATHEMATICALLY (the identity closes: r − √(r² − h(2r−h)) = h), so an
exact acceptance comparison hands a flat plate's fast path to the rounding of one square
root — measured, the ball route passed and the bull route bisected to 3.6e-10 under the
flat spacing on the same geometry — hence acceptance carries a 1e-9 RELATIVE grace, the
bisection's own tolerance grade, admitting nothing coarser than the search already accepts
(the `PolygonFan` tie-guard family: a predicate whose two sides are mathematically equal at
the boundary needs a stated tie rule, or the last bits decide).

**Horizontal-plate convection landed as the McAdams transcription beside the vertical
channels** (`NaturalConvection.PlateFacing`/`PlateCharacteristicLength`/`PlateRayleigh`/
`PlateNusselt`/`PlateFilmCoefficient`; docs `examples/fea-thermal.md`): heated-facing-up
`Nu = 0.54·Ra^(1/4)` (10⁴…10⁷) and `0.15·Ra^(1/3)` (10⁷…10¹¹), heated-facing-down
`0.27·Ra^(1/4)` (10⁵…10¹⁰), over the `A/P` characteristic length (Lloyd &amp; Moran) with
β at the film temperature — the `OptimumSpacing` convention, so one rule. **A Rayleigh
number outside a correlation's own validity range is REFUSED by name rather than
extrapolated**: a correlation is a fit, and outside its data it is a guess wearing four
significant figures — the honest-range rule the vertical family never needed because the
composite blends its two limits. Two identities carry the verification past transcription:
**facing-up is exactly TWICE facing-down in the shared laminar range to the bit** (0.54 =
2 × 0.27, and multiplication by an exact power of two commutes with rounding, so the two
literals' nearest doubles keep the ratio exactly); and **the turbulent ⅓ power makes the
film coefficient SIZE-independent** — Ra carries L³, so `h = 0.15·k·(gβΔT/να)^(1/3)` with
no L in it, asserted as a 1 m and a 2 m plate reading ONE h to 1e-12 relative — the
identity a transcription error in either the exponent or the constant structurally cannot
pass, where a spot value would only catch the constant. The facing enum is named by the
BUOYANCY case (`HeatedFacingUp`/`HeatedFacingDown`) with the cold-plate equivalence stated
in its doc (a cold face looking down IS the up case), and the fin-array SIZING stays
vertical-only by name — horizontal fin CHANNELS are a different correlation family, not a
parameter on this one.

**Lead-in/out arcs landed on the profile, and the design is one derived normal**
(`CncMill.Profile(..., leadRadius:)`; docs `examples/cam-milling.md`): each loop is entered
and left on a quarter arc TANGENT to the path at its seam, on the side away from the
material, and the reason no per-loop bookkeeping exists is that `Orient`'s winding contract
already made the material side a TRAVEL-relative fact — climb cuts with material left of
travel (the M3 cross-product derivation), so away is the RIGHT normal for every loop, outer
or hole, whichever way the polygon happens to wind. What the feature is FOR is the plunge:
the writer always plunges at a pass's first point, so prepending the lead moves the plunge
to the arc's start, off the wall, with no writer change at all — a plunge ON the profile
dwells and marks it, which is the defect the arc exists to remove. The construction is
exact and asserted as its own identity (the arc start is `P0 + n̂R − d̂R` to 1e-9, the
approach tangent to the cut direction at the chord sampling's own grade); a lead that
cannot fit — a small hole whose far wall lies inside the arc's reach — is refused by name
with the measured shortfall against the same `DistanceToBoundary` the no-gouge tests read;
leads compose with holding tabs and apply at every depth level; zero is byte-identical.
One pairing subtlety worth the comment it carries: the travel linker PERMUTES the loop
order, so the lead is matched to its loop by REFERENCE — the linker reorders, it never
rebuilds, which is what makes `IndexOf` sound there.

**The heatsink design-study loop landed as the docs-example composition the layering makes
it** (`fea-thermal.md` §Closing the loop): `EngrCAD.Modeling` cannot reference
`EngrCAD.Fea`, so a `Feature` cannot call `HeatsinkSizing` — and rather than bending the
dependency graph, the loop lives at the APPLICATION layer, where a snippet-defined
`FinnedSink : Feature` carries `[Param]` height and spacing, and the study's objective and
constraint read the convection correlations directly. **What makes it work is a recorded
design fact doing new duty: a `Shape` graph is LAZY, so regenerating a part costs nothing
until something measures it** — the study runs ~380 evaluations in milliseconds because
mass and resistance are closed forms of the parameters, and the geometry is measured ONCE,
at the winner, where the generated solid's `MassGrams()` agrees with the study's own closed
form to 0.000% (the loop-closure assertion: the solid the study reasoned about IS the solid
it generated). The fence self-verifies the narrative too: the default design STARTS
infeasible (~4 K/W against the 12 W @ 35 K ask of 2.92), the search descends on violation
alone into feasibility — the study machinery's own from-infeasible contract — and ends
exactly ON the resistance constraint (margin 0.00%, named binding). The fin COUNT is
derived (`floor` of the width over the pitch), so the objective is deliberately
DISCONTINUOUS in the spacing — the topology-changes case derivative-free search exists for,
here in its mildest form.

**The infill's exact-footprint option landed as the named alternative the estimator rule
requires** (`InfillPath.ExactFootprint`/`ExactCoveredArea`/`ExactCoveredFraction`; docs
`examples/infill.md`): the polygonal `CoveredFraction` measures through inscribed round
joins — a one-sided under-estimate, the safe direction for a coverage claim — and the exact
option runs the same measurement through `CurvedRegion2dOffset.Stroke`, whose round joins
and caps are exact sectors and half-discs, so the footprint IS the path's Minkowski sum
with the bead disc. The oracles are equalities where the polygonal twin can only approach
from below: a single straight run's exact area is the stadium `L·w + π(w/2)²` to nine
decimals and an isolated point's is the full πr² disc, with the real-fill assertion the
ORDERING (exact strictly above polygonal, both in [0, 1]). Two decisions carried over
rather than re-made: round joins and caps are the only style offered (they are the toolpath
truth, and the exact-Minkowski claim is theirs alone), and the DENOMINATOR stays the
flattened `RegionArea` — the flattened region is what the path was clipped against, so the
ratio is the covered fraction of the region the fill was actually computed on, where an
exact sketch area would divide an exact numerator by a different region than the one that
decided the runs. The stale institutional record fixed alongside: CLAUDE.md still called
the `Stroke` clockwise-corner defect "filed rather than fixed", when the fix (offer each
corner side in BOTH orders — swap as well as negate) had landed in the tamper-mesh
campaign's own merge with its six-path deficit test; the record now says so.

**Time-varying thermal boundary conditions landed on the seam the transient's own comment
reserved for them** (`ThermalModel.HeatFlux/HeatLoad/Convection/Temperature` law overloads
taking a `Func<double, double>`; docs `examples/fea-thermal.md` §Time-varying): the
stepping had deliberately kept the previous state whole rather than collapsing the
prescribed columns — its comment said that made "time-varying prescribed values a change
of one line rather than a rewrite" — and the prediction held. A law moves only the LOAD: a
condition's spatial pattern is assembled ONCE at add time at unit law value (by the same
facet quadrature as its constant twin, so the two cannot disagree about geometry) and
scaled by the law at each instant; the load enters at the scheme's own theta point
(θ·f(t_next) + (1−θ)·f(t_now)); prescribed laws recompute the prescribed-column products
per step and each stored state carries its own instant's values; the FILM COEFFICIENT
stays constant BY STATEMENT — h enters the factored matrix, the ambient only the supply —
and a steady solve refuses a law-carrying model by name. **The oracle is DISCRETE
exactness, constructed from the solver's own pieces**: for a prescribed ramp R·t the
discrete particular solution is a·t + b with a the uniform vector R (the discrete steady
answer to a constant Dirichlet value is the constant, any element order) and K·b = −M·a —
so b IS the steady solver's answer for a uniform generation of −ρc·R held at zero — and
any theta scheme integrates a linear particular solution exactly: seeded with b, every
step lands on b + R·t at round-off for backward Euler AND Crank–Nicolson. Beside it: a
lumped body under a sinusoidal ambient at ωτ = 1 reproduces the first-order closed form
(amplitude A/√2, 45° lag) within 2%, and a square-pulse heat load keeps the run's own
first law at round-off. **The first build got the ENERGY BALANCE wrong and the failure was
worth the record**: `EnergyBalance` read the applied heat from the model's CONSTANT nodal
loads, so a law's power never entered and a pulsed run reported a residual of exactly 0.5
— the fix needed law KINDS, because an applied law (flux, load) belongs to the applied
heat while a convective-ambient law belongs to the SUPPLY the convective loss is measured
against, the same split the constant conditions already live by; one law evaluation per
step now serves the load and the balance both.

**Lumped (HRZ) capacity landed as the monotonicity option the transient's own record
measured the need for** (`ThermalTransientOptions.Lumping`, the modal solver's
`MassLumping` vocabulary — the capacity IS the mass matrix's integral under another name,
so a second enum would be two spellings of one rule): the recorded finding was that at
steps short against the element diffusion time BOTH schemes undershoot a quench "because
that is the consistent capacity matrix, not the scheme", and the option is its remedy —
with a lumped diagonal, backward Euler is an M-matrix step and the discrete maximum
principle holds. **The fixture had to show the disease before the cure means anything**:
the consistent run genuinely leaves the physical bounds, and the measured DIRECTION is the
finding worth keeping — the artifact pushes the node NEXT to the quenched face ABOVE the
initial temperature (the min stays pinned at the surface value, so a min-only assertion
reads a healthy run; the violation lives on the MAX side). The lumped run leaves nothing
outside [surface, initial] to 1e-9. HRZ scales the strictly positive consistent diagonal
to preserve each element's capacity exactly — asserted through the run's own first law
against the SAME lumped matrix — and coincides with row-sum on 4-node elements to
round-off, NOT to the bit (a row summed and a diagonal scaled are different arithmetic,
measured 1.4e-13 apart on a magnitude of 86, which is why the assertion is relative);
row-sum on 10-node elements refuses by name (−V/20 at every corner, a negative heat
capacity). The arithmetic is the modal lumping transplanted verbatim with the same
exact-zero division guard.

**Surface radiation landed as the outer iteration the conduction remarks reserved for it**
(`ThermalRadiation.Solve`/`RadiationSurface`; docs `examples/fea-thermal.md` §Surface
radiation): grey-body `q = σε(T⁴ − Ts⁴)` is nonlinear in the unknown while everything
else in the thermal stack is one factorization, so each pass linearizes per FACET about
the previous answer's facet mean — `h_rad = σε(T̄² + Ts²)(T̄ + Ts)` with ambient Ts, which
is EXACT at its own linearization point, so a converged fixed point satisfies the true
Stefan–Boltzmann balance rather than an approximation of it. **The mechanism is a per-facet
film OVERLAY on `ThermalModel`** (internal, set around each inner solve and cleared in a
finally): `FilmCoefficientOf`/`FilmSupplyOf` add it in, so the surface-matrix assembly, the
load supply, the energy balance and the driven check all pick it up with zero threading —
a radiating facet counts as DRIVEN for the same reason a convective one does, so a model
held by nothing but its own glow is solvable, and the user's model is never mutated
(asserted: after a radiating solve, a plain steady solve of the same model still refuses
as undriven). **The plain Picard map was measured OSCILLATING** — a 1.7e-4 relative limit
cycle on the equilibrium fixture, stalled not diverging — so the linearization point is
under-relaxed (`Relaxation = 0.5` default, validated in (0, 1]), which converges cleanly
in ~30 passes to 1e-10. σ is stated in MODEL units (5.670374419e-11 mW/(mm²·K⁴) — the SI
constant through the film coefficient's own ×1e-3, the `ModelUnits` discipline), and
temperatures must be ABSOLUTE: the fourth power is a statement about absolute temperature,
a celsius model is silently wrong physics no solver can detect, and the non-positive
surroundings refusal says so loudly. Verified: a generating cube's equilibrium against the
lumped Stefan–Boltzmann closed form solved INDEPENDENTLY by bisection on the balance
(2e-3, the radiative-Biot grade); the small-signal limit degenerating to a convective film
at 4σεTs³ within the quadratic correction; determinism to the bit; refusals by name.

**The thermal transient streams** (`ThermalTransientOptions.OnState` +
`RetainStates`): a long run at `StoreEvery = 1` held O(steps × nodes) doubles, and the
filed answer was the right one — a callback per stored state, invoked with EXACTLY the
states a retained run stores (same times, fields bit-identical, StoreEvery honoured, the
initial and final states included), with `RetainStates = false` capping the returned list
at the two ends so the callback is the record and the run's memory is O(nodes). The
assertion is the bit-for-bit correspondence against a retained twin, which is what stops a
streaming path from quietly seeing different states than the list holds.

**The structural transient streams too** (`TransientSolveOptions.OnState` +
`RetainStates`, the thermal twin's pattern on the solver whose states are HEAVIEST — each
carries a full `StructuralResults`): the callback sees exactly the states a retained run
stores, bit for bit, on the constant-step and adaptive paths both, and the run's summary
numbers (peaks, worst equilibrium, energies) are tracked from every streamed state rather
than from the retained list, so they are identical either way — asserted, since a summary
quietly computed over a two-entry list would be the silent regression this feature
invites.

**Temperature-dependent conductivity landed as the radiation template's second consumer,
with the structural difference stated up front** (`ThermalNonlinear.Solve`; docs
`examples/fea-thermal.md` §Temperature-dependent): a radiating pass moves only the LOAD,
while a k(T) pass changes the MATRIX, so every iteration re-assembles and re-factors —
each pass evaluating the law per ELEMENT at the element's node-mean temperature through a
per-element conductivity OVERLAY on `ThermalModel` (the film overlay's pattern one level
in), where **NaN means "keep the model's own law"** — the sentinel that lets a temperature
law on one region coexist with a DIRECTIONAL `ConductivityLaw` on another, because the
first draft flattened every un-lawed element's tensor to its meaningless isotropic scalar
and the hole was caught in design rather than by a fixture. **The oracle is the KIRCHHOFF
TRANSFORM**, sharing no line with the linearization: for k(T) = k0(1 + βT) the variable
θ = ∫k dT is linear in x, so the slab's flux is (k0/L)[(T1 − T2) + β(T1² − T2²)/2]
exactly and θ(T_node)/θ(hot) must equal x/L at EVERY node (asserted to 5e-3, the
per-element-constant grade; the flux to 2e-3). **The flux caveat is stated on the result
rather than discovered by a caller**: `ThermalResults`' flux accessors read the MODEL's
constant laws — the overlay is cleared when the solve returns (asserted: a plain solve
afterwards answers identically) — and the first fixture measured exactly that failure,
the accessor-mean reading the constant-k 100 against the Kirchhoff 150 over the correct
temperature field; so the converged per-element k rides on the result
(`ElementConductivity`) and the nonlinear flux is the accessor's value rescaled by it. A
constant law converges in ONE pass bit-identical to the plain solve; refusals by name
(unknown region, non-positive k(T), the directional-law conflict, relaxation range).

**Cell-associated fields landed as an ASSOCIATION on the field, not a parallel type**
(`FieldAssociation.Vertex|Cell` on `MeshField`, `CellScalar`; docs `examples/fields.md`
§Cell-associated): the association is part of the field's identity — every derived
operation preserves it, which is what stops a `Magnitude()` of a cell field quietly
becoming a vertex field of the wrong length — and each consumer routes by it:
`VtuWriter` writes `PointData` or `CellData` (counts validated against the right total,
one shared block writer so the two cannot format differently), and the display path
places a cell value through **`RenderMesh.SourceFaces`, `SourceVertices`' sibling** —
every duplicate of a face's corners takes that face's value, so a cell field renders
FLAT per face with no shader change at all (the colour is already a per-vertex
attribute). Two honesty boundaries carried in the types: a SMOOTH render mesh shares
vertices between faces, so "which face's value does this vertex take" has no answer —
`CreateSmooth` carries an EMPTY face map and a cell display on one refuses with that
reason, rather than an arbitrary pick; and a CELL-associated deformation refuses by name
(a displacement moves vertices — a per-face displacement has no vertex to move). The
real consumer landed with it: a structural `.vtu` now carries the per-element von Mises
as cell data beside the recovered nodal field — the value the assembly actually
integrated, before any nodal recovery, and the array a ParaView threshold filter wants.

**NaN paints a distinct NO-VALUE grey, never the map's bottom stop** (`ColorMaps.NoValueColor`
\+ the NaN branch in `Sample(map, t)`; `FieldLegend.HasNoValue` and the NO VALUE swatch;
docs `examples/fields.md` and the fatigue page's new steel life render): NaN is "no
value" — an infinite fatigue life, a part with no data in a merged VTU — and the clamp
that used to catch it (`!(t > 0)` → first stop) painted an immortal node the colour of
the SHORTEST-lived one, the worst direction for the confusion to run, which is why the
fatigue docs page had to plot aluminium (every node finite) and document the dodge.
Three decisions carry it. **One rule, placed at the bottom of the funnel**: the NaN
branch lives in `ColorMaps.Sample(map, t)` itself, so the range overload
(`Normalize(NaN)` is NaN) and the log-scale colour path (a non-positive value has no
log position and arrives as NaN t) both inherit it with no second spelling — and an
exact zero still takes the bottom stop, because "the range minimum" and "no value" are
different statements. **The grey is chosen against the maps, and the reason is stated**:
mid grey (0.5) is unlike anything viridis produces (dark purple to yellow, nothing
desaturated) and a full lightness step from the diverging map's own neutral MIDPOINT
(0.865), so "no data" cannot be mistaken for "the crossing". **The legend swatch is
data-driven and appended as an ordinary BAND**: `FieldLegend.Build` scans the displayed
field once (`HasNoValue` — NaN always, non-positive under `LogScale`, i.e. the
association is with the DISPLAY not the field) and appends one grey swatch below the
bar with a NO VALUE label exactly when one exists — as an extra entry in the band
arrays both front ends already draw generically (`BandCount` × `VerticesPerBand`), so
the browser and the window inherit it with zero front-end change, and a finite field's
legend arrays are bit-identical to what they always were. The fatigue page now shows
the case it used to sidestep: the SAME bracket in SAE 1045 renders mostly grey
(immortal below the endurance limit) with the finite-life band coloured — the honest
picture of what an endurance limit means.

**Points and wireframe read the field too** (the line and point programs gained the
`aFieldColor` attribute + `uFieldColor` strength under the `Lit = 0` /
constant-when-absent rule; `PartUpload.WireColors`; docs `examples/fields.md` §Every
view style): a field-coloured part used to fall back to its part colour the moment the
style left Shaded, because the line and point programs were flat-colour. Three facts
made the fix small and each is worth keeping. **Points came almost free**, because the
points view draws the MESH buffer — the colour buffer is already in the VAO at slot 3,
so the whole change is declaring the attribute and setting one uniform per draw.
**The wireframe needed a parallel buffer, and its correctness rule is that the walk is
shared**: `WireframeEdges.ExtractIndexed` is the SAME `mesh.Edges` enumeration as
`Extract`, so segment i and index pair i describe the same edge by construction, and
each endpoint takes its source vertex's colour from the same
`FieldRendering.SourceColors` call the fills are built from — a wireframe reading of a
result structurally cannot disagree with the shaded one (log scale included, since the
colours come from the one shared mapping). **The neutral state is what keeps every
incumbent consumer byte-identical**: the line program serves the grid, axes, feature
edges, annotations, isolines, legend and cube, and none of them says anything about
the new uniform — a linked program's uniforms initialize to 0 and
`mix(uColor, v, 0.0)` is `uColor` exactly, so the only draws that change are the ones
that explicitly turn the strength up (asserted the docs-PNG way: no committed render
moved). Two boundaries stated rather than smoothed: a CELL-associated field keeps the
part colour in wireframe (a mesh edge borders two faces, so an endpoint has no one
cell colour — the smooth-mesh-has-no-face-map reasoning one primitive down), and a
SELECTED or hovered part keeps the highlight, because with no fill behind them the
line colour is the only channel selection has.

**The Gosper island's TRUE inradius landed, and the measurement overturned the filed
hypothesis** (`SpaceFillingCurve.IslandPlacement` + the internal `HexCellDistance`): the
island had been placed by a conservative bound — nearest unvisited site less the
lattice's covering radius — and the backlog attributed Gosper's 2–2.6×-finer-than-asked
spacing to it, with "a true inradius of the island's cell union would recover part of
that". The exact form is the distance from the centroid to the nearest unvisited site's
hexagonal Voronoi cell (point-to-regular-hexagon, six half-plane tests then six edge
segments, no epsilon; soundness is the 1-Lipschitz argument in the `SurfaceCull` shape —
a point closer than the inradius is in no unvisited cell, and the lattice tiles the
plane). Measured at orders 3–6 it buys **0.04–0.9%** — the conservative bound was nearly
tight, because at island sizes the nearest unvisited cell's closest point lies almost on
the centroid-to-site line. **The fineness is the RADIX**: each order shrinks the cell by
exactly 1/√7 ≈ 0.378 (measured 0.377 between consecutive orders), so the worst ask lands
2.6× finer structurally, against a square family's radix-2 worst case of 2× — a
quantization no placement can touch. The exact tier is kept (strictly better, sound,
self-contained, pinned by the existing coverage certificate plus closed-form
`HexCellDistance` tests) and the docs now attribute the fineness to the radix rather
than to the placement.

**Stacked legends: one bar per distinct visible display** (`FieldLegend.Build` over a
display LIST; `ViewportControl.ActiveFieldDisplays`; docs `examples/fields.md`): the
viewer used to show the FIRST visible part's display only, so a second part on a
genuinely different scale rendered with no bar at all — and the honest options the
backlog named were stacked legends or a scene-level shared range. A shared range was
rejected because it changes what the colours MEAN (re-ranging every part to one span is
a modelling decision the viewer must not make silently — the `SymmetricAboutZero`
never-apply-silently rule); stacking keeps each display's own statement. **The
mechanism is the NO-VALUE swatch's trick one level up**: everything is appended into
ONE `FieldLegendGeometry` — more bands, more frame segments, more labels — so all three
front ends draw a stack with ZERO draw-path change, and the plumbing reduces to each
selection site collecting every distinct resolvable display (record equality, draw
order — two parts SHARING one result object share one bar) instead of stopping at the
first. As many bars as fit vertically are kept, first-come; a single display reproduces
the incumbent centred layout bit for bit (the delegating overload plus the pinned
centring expression), which is what keeps every committed field render byte-identical.
The legend caches on display CONTENTS (per-entry record equality), the annotation
overlay's value-equality rule, in the window layer and the browser alike.

**Tangency to a cubic bézier landed — the one carrier the tangency vocabulary refused**
(`TangentLineBezierConstraint`; `ConstrainedSketch.Tangent(line, curve)` now takes a
`CubicSeg`; docs `examples/sketching.md`): the refusal's own reason spelled the
implementation — no closed-form support function means the FOOT parameter joins the
system as a solver variable, and the constraint is `B(t)` on the line plus `B′(t)`
parallel to it, two rows over one new unknown, removing exactly the one DOF a tangency
means (asserted off the solver's own rank, the `PointOnBezier` instrument). Written in
LIVE space exactly as `PointOnBezierConstraint` is — the control points ride the chord
similarity, `B(t) = s + (e − s)·β(t)` for the fixed complex β read off the drawn
offsets — so the analytic Jacobian is complex-block one-liners: the foot column is
`B′ × û` and `B″ × û` (row 1's t-derivative IS row 2's value), the line's endpoints take
the shared `cross(k, d)/|d|` derivative form the ellipse tangency already uses, and the
carrier's joints enter through the conjugate blocks. **The branch selector is the drawn
configuration, computed rather than scanned**: the carrier's tangents parallel to the
drawn line are the real roots of a plain QUADRATIC in t (B′ is quadratic and the cross
with a fixed direction is scalar), and the root whose point lies nearest the drawn line
is the tangency the drawing means — with a dense-scan fallback for a line no carrier
tangent parallels, since the solve may still rotate the line into reach. The verifying
test measures the SOLVED geometry (the line touches to 1e-7 and does not CROSS — the
signed distance keeps one sign around the touch, which is what separates a tangency
from a secant), and the probe had to be REFINED to say so: a 1/400 parameter grid reads
a true tangency as ~2.5e-7 purely from sampling a parabola off its vertex, so the
two-level refinement is what makes the tolerance a statement about the solve rather
than about the probe.

**Sketch constraint serialization landed as REPLAY, and that one choice settled every
sub-question the backlog filed** (`ConstrainedSketch.SaveConstraints`/`LoadConstraints`;
docs `examples/sketching.md` §Saving a constrained sketch): the filed design asked for
(a) a descriptor grammar for the entity refs, (b) a record per public constraint method
and (c) the assembly-enumeration coverage test, and all three follow from one rule —
**a saved constraint file is the CALL SEQUENCE, replayed through the public methods
against the same drawn sketch**. Each ref accessor stamps a canonical parseable
`Descriptor` (`point(3)`, `holeLine(0,0)`, `centerOf(holeArc(1,0))` — the GeometryRefs
one-string rule, with the prose `Description` kept for humans), each public method
appends one token record AFTER its `Add` succeeds (so a refusal leaves no record — a
pinned test), and the loader parses refs back through the SAME accessors and invokes
the SAME methods, which is why the loaded system cannot drift from the built one: every
branch selector (a tangency side, a foot seed, a distance side) re-derives from the
same drawing by the same code. The verifying pair is a byte fixed point
(save → load → save) AND a bit-identical replayed SOLVE — every solved segment
endpoint compared as bits, which an equivalent-but-reordered system fails. Overload
identity rides in the tokens, not in extra names: `Distance`'s point-point and
point-line forms share one record method and the second token's own kind says which,
with the one exception named (`TangentAtEnd`, since three `Tangent` overloads share
two-entity shapes). The coverage test enumerates the vocabulary FROM the assembly
(every public `ConstrainedSketch`-returning method must map into
`SupportedRecordMethods`), the `EverySketchSegmentKind_HasAJsonForm` treatment. The
vocabulary is all data — no lambda anywhere — so nothing loads as a warning; an
unknown record method refuses BY NAME (a newer vocabulary's file, not something to
skip). What deliberately did NOT land: wiring into `Feature.SaveInputs`, because no
feature carries a `ConstrainedSketch` input today — the seam is ready for the first
one that does.

**Gear backlash and the inspection dimensions landed as arithmetic held to the sketch**
(`GearSpec.Backlash`/`SpanOverTeeth`/`MeasurementOverPins`; docs `examples/gears.md`
§Backlash): the allowance thins THIS gear's teeth by j at the pitch circle — each flank
rotates j/(2·r_pitch) toward the tooth centre, EXACT because an involute rotated about
its own centre is the same involute at another phase — behind an exact-zero branch, so
a spec stating nothing generates bit-identical geometry (all 126 incumbent gear tests
unmoved), and `ToothThicknessAtPitch` carries the subtraction so the generator reads
ONE source of truth. The span identity is written as `(k−1)·p_b + cos α·(s + m·z·inv α)`
rather than the textbook expansion, which makes both properties one substitution each
(reduces to the textbook form at j = 0; drops exactly j·cos α with the allowance, a
pitch-circle thinning being a base-circle thinning times cos α) — and the sketch check
REBUILDS W from the MEASURED pitch thickness, which is what holds the drawn flank to
the caliper dimension. The over-pins measurement is verified against an INDEPENDENT
oracle sharing no arithmetic with it: a seated pin touches both flanks, i.e. the
region's own signed distance at the pin centre equals the pin radius, bisected along
the space centreline. **One trap paid for**: the involute function has a second branch
past π/2, and a pin large enough to push inv α_M onto it made the Newton inversion
return a confidently wrong measurement instead of a refusal — the tips guard is
therefore checked in INV-SPACE before the inversion (α_M ≤ acos(r_b/(r_tip + r_pin)),
one closed form), the bisection-needs-a-monotone-bracket lesson in Newton clothing.

**The keyed bore landed as one arc and three lines** (`StandardKeys`/`KeywaySpec` +
`Gears.KeyedBore` + the `keyway:` optional on `SpurGear`/`HelicalGear`; docs
`examples/gears.md` §A keyed bore): the hub's half of a DIN 6885 parallel-key seat is
a notch in the bore, and placing the notch corners exactly ON the bore circle (at
x = ±b/2, y = √(r² − b²/4)) is what keeps the profile lines-and-an-arc — exact in all
three representations, with a CLOSED-FORM hole area (πr² + b·(r + t2) − b·y_c/2 −
r²·asin(b/(2r))) the sketch's own exact `Area()` is held to at 1e-9 and the keyed
solid's volume to at mass-properties grade (~1e-7 relative, since the two bores chord
their arcs independently — the grade stated in the test rather than hidden in a loose
decimal count). The table follows the `StandardHoles` transcription convention (⚠
verify-against-datasheet; the SHAFT half — t1, the key height — is deliberately not
carried, since a hub feature should not restate dimensions it does not cut), the
`keyway:` optional defaults to null for a bit-identical plain bore, and the refusals
fire by name (a keyway with no bore, into the root circle, wider than its bore, an
off-table shaft). Wiring it surfaced the positional-caller tax of appending an
optional parameter: three internal call sites passed `fitTolerance` positionally into
the new slot and were moved to named arguments — the compiler caught all three, which
is the argument for optionals over overload pairs here.

**Web lightening joined the keyed bore as a blank sketch feature** (`LighteningSpec` +
the `lightening:` optional on `SpurGear`/`HelicalGear`): N circular holes evenly on a
bolt circle in the gear's web, defaulting to the web's own MIDDLE (midway between the
bore's reach — the keyway's top when one is cut — and the root circle), each removing
exactly π·d²/4 of blank area (the volume identity held at mass-properties grade beside
the keyed bore's). The three refusals are closed-form and each names its numbers: a
hole reaching the bore's clear radius, one reaching the root circle, and neighbours
overlapping (the chord 2·R·sin(π/n) against d — the planetary neighbour-clearance test
one feature over). The remaining set-screw BOSS is filed with its reason: a boss needs
a 3D hub proud of the web, i.e. a revolved blank cross-section rather than one
extrude — a gear-blank redesign, not a hole.

**Transient playback landed as the fifth animation slot, and the contract extension is
stated with its cost model** (`FieldSequenceTrack`; `Animation.FieldTrack`;
`ViewportControl.SetFieldSelection`; docs `examples/fields.md` §Transient playback):
the animation contract was "matrices, a camera or a scalar — never a re-meshed part",
and a field-sequence track extends it with a result SELECTION, whose cost is one
colour-buffer re-upload per step (measured 0.042/0.68 ms per frame at 12k/195k render
vertices — `FieldPlaybackBenchmark`, the measurement that DECIDED this design before it
was built: the full publish path is 40–50× more and n-buffers-uploaded-once costs 137 MB
at 60 heavy steps); instance count and order, the meshes and the pick BVH are all
untouched. Three rules carry it. **Hold-last-step, deliberately**: steps are (result
name, REAL seconds) and t maps linearly over the run's span to the latest step at or
before the instant — the stored states ARE the answers at their own instants, so
holding is honest where tweening colours would invent a state the solver never
produced. **The application rule lives on the TRACK** (`TryDisplayFor` — participation
is carrying a display AND every step as a result, the `PoseByPath`
a-track-saying-nothing-leaves-you-alone lesson; the clip's ONE range is the display's
explicit range, else the union of the steps' own, read off the raw nullable
`FieldDisplay.Range` since the resolved form cannot distinguish explicit from derived),
so the window, the stills and the legend ask one rule and cannot disagree. **The window
retains only what the re-upload needs**: the source-index lookups and the live colour
VBO per field-coloured part, captured at upload — no render mesh is held and none is
rebuilt (`FieldRendering.Colors` gained the retained-lookup overload the path wants).
The verification bar is the deformation track's: a still of the animation at a step is
BYTE-IDENTICAL to a static render of the same scene with the step's field and the run
range stated explicitly — both roads reach one configuration. **The batched
export rides it too** (`RenderSequence`'s optional per-frame `fieldSteps`, parallel to
`frames` and refused by name on a count mismatch; `AnimationExport` builds the list off
the samples it already evaluates, so an APNG of a transient run plays its steps): the
`PassCache` retains the window's `_fieldAnimation` data one context over — the live
colour VBO and the source-index lookups per field-coloured part — and a warm-cache
frame whose selection MOVED re-uploads just the colour floats (the attribute pointer
references the buffer OBJECT, so no VAO is touched), with `AppliedFieldStep` making a
run of frames holding one step re-upload nothing, the hold-last common case. Verified
at the batch's own bar: a three-frame sequence stepping A → B → A is byte-identical to
one fresh `Render` per frame with the same selection — the THIRD frame is the assertion
with teeth, a warm cache returning to a step it has already shown, which only the
re-upload path can make match. **And the browser closed the ladder**: `engrcad-gl.js`
gained `updateFieldColors` (a `gl.bufferData` into the uploaded mesh's field buffer —
the attribute pointer references the buffer OBJECT, so the VAO is untouched),
`EngrCadViewport` retains the source-index lookups on its per-part key record (uploads
survive tab switches by part reference, so the lookups ride the same lifetime) and
applies the sample's step at the same point poses are applied, with the repeat-selection
no-op and hold-last semantics the window states. Verified through the REAL WebGL path,
which no value test can reach because the re-upload ends in JavaScript: the `?report`
self-check gained a report-only third tab carrying a two-step fixture and drives the
A→B→A oracle at the pixel level — stepping forward must change pixels
(`fieldStepPixels > 0`) and stepping back must restore the first capture EXACTLY
(`fieldStepReturn == 0`), which a re-upload leaking into any other buffer or a stale
colour array fails from one side or the other. Scope: window + stills + batched
export/APNG + web — the playback ladder is complete. The filed frequency/load-step
"slider" neighbour landed as the properties panel's RESULT DROPDOWN
(`DocumentEdits.SetFieldDisplay` + the pure `ParamEditors.ResultChoices`, the material
editor's shape exactly): results are NAMED states, so the honest control is a choice —
switching keeps the rest of the display (a user stepping load cases has not changed
their mind about the exaggeration), "(none)" clears it, a display naming a removed
result stays LISTED (the current-value rule) and legal (resolution reports it), and the
edit rides the undo stack's byte-identity oracle like every other document edit.

**The time AXIS became document data** (`ResultSequence` + `Part.AddResultSequence` +
`FieldSequenceTrack.For`; docs `examples/fields.md`): the states were never the gap —
each stored transient state is an ordinary named result — but the ORDER and the
INSTANTS lived only in the `FieldSequenceTrack` an application hand-built, so a saved
document kept every state and lost the axis. `AddResultSequence(name, (field,
seconds)…)` publishes each step under a derived name (`"T @ 0.5s"` — the seconds in
"R" form, so the name is a deterministic function of the instant and survives the file
bit-for-bit) and records the `ResultSequence`, validated to the track's own rules
BEFORE any mutation (all-or-nothing, so a refused run leaves the part untouched);
re-publishing under one name REPLACES and removes the steps the new run no longer
uses — `AddResult`'s replace-by-name rule extended to a whole run, because a re-solve
with different instants must not leave stale twins behind. The record persists
write-only-when-stated inside the results gate (a sequence without its states is
dangling, so `IncludeResults = false` drops both; a sequence-free document is
byte-identical and save→load→save is a byte fixed point), and
`FieldSequenceTrack.For(part, name)` is the ONE rule from the saved axis to the
playback — asserted equal to the hand-built track step for step, with a missing
sequence refusing by name and listing what the part does carry. Fea stays a leaf: the
bridge is one app-layer line (`part.AddResultSequence("Temperature",
transient.States.Select((s, i) => (s.Fields()[0], transient.Times[i])).ToList())`),
which is the layering the thermal results' own remarks predicted — "a time slider over
a document's results is a matter of choosing a state rather than of a second API" —
completed by making WHICH states and WHEN a recorded fact rather than a convention.

**A displaced part's feature edges and wireframe landed as ONE line-program attribute,
and retiring the no-edges rule kept both halves of its own reasoning**
(`LineVertex`'s `aDeformOffset` + `uDeformScale`; `PartUpload.FeatureEdgeDeformation`/
`WireDeformation`; `MeshProjectionTarget.TryInterpolate`; docs `examples/fields.md`):
the rule was "a part carrying a displacement draws NO feature-edge overlay at any
factor", for two stated reasons — a static overlay over a displaced shape is a WRONG
outline rather than a coarse one, and the draw list must not depend on t or a clip could
not reuse one upload. Displacing the edges by their own attribute satisfies both AT
ONCE: the overlay is drawn at every factor and lands on the displaced rims at every
factor, and it is one uniform per frame exactly as the fills are — so the retirement is
the rule's own logic completed rather than reversed. Three pieces carry it. **A
feature-edge sample is an exact B-Rep curve point, not a mesh vertex**, so its
displacement is INTERPOLATED: `MeshProjectionTarget.TryInterpolate` reports the nearest
triangle's corners (in the construction mesh's own vertex numbering — triangulation
fans existing vertices and invents none) plus clamped barycentric weights, and the
offset is the weighted sum of the corners' own vectors — exact for any affine
displacement wherever the sample lies in a facet's plane (a box's edges, asserted
value-for-value against the field's closed form), and within the fills' own facet
interpolation otherwise, so the outline sits on the displaced surface to the same order
the shading does. **A wireframe endpoint IS a source vertex**, so its offset is
`VectorAt` through the same `ExtractIndexed` pairs the wire colours ride — no
interpolation, and it closes a gap that PREDATES the attribute path (a deformed part in
Wireframe had always drawn its undeformed edges while its fills moved). **The
constant-when-absent rule does the byte-identity work from both sides**: a line VAO
without the buffer reads offset (0,0,0), so `aPos + s·0` is `aPos` for any finite scale
and every incumbent line consumer — grid, axes, annotations, legend, isolines, cube —
is untouched with no uniform resets anywhere; and a DISPLACED wireframe at factor 0 is
byte-identical to its scale-0 twin's render (asserted), the same rule met from the other
side. All three front ends set the one per-draw scale (`DrawFeatureEdges` and the wire
case in the window, both line loops plus the translucent-edge pass offscreen, and
`Deformed(edge.Uniforms, …)` in the browser frame — asserted as VALUES there, the
DeformFrameTests way). The regression with teeth: a deformed part's ShadedWithEdges
render used to be byte-identical to its Shaded render, and now differs — with the
overlay's own pixel SET moving between factor 0 and factor 1, so the test sees the
outline FOLLOW the shape rather than merely exist. What deliberately did NOT change:
picking stays built once at the part's own scale (the spatial-index-cannot-be-a-uniform
reason stands), and the factor-0 frame of an animation still differs from a scale-0
still — by the ghost alone now, which is the difference that was always real. **The
hover affordance refused where that index was stale** (`FieldRendering.HoverIsStale`,
asked by the window's and the browser's one hover funnel each — SINCE RETIRED by the
deformed-ray correction, which makes the pick exact at every factor; see the later
record): a displaced part at any
effective factor other than exactly 1 was drawn where its pick BVH is not, so
highlighting it would have been an ambient claim answered from stale geometry — and the rule
is deliberately playback-INDEPENDENT, since a still scrubbed to factor 0.5 answers from
the same index the filed "while playback is running" framing would have exempted.
Clicking still selects (a deliberate act, staleness documented); exact comparisons
throughout, the factor 1 and a scale of 0 being assigned values.

**Reel/Short export landed as the composition it was filed as, and both real findings
came from the checks** (`ReelFormat`/`ReelFraming` in Viewer.Core, `ReelExport` in
Viewer; docs `examples/animation.md` §Reels and Shorts): the presets carry the frame
size/rate, the platform's duration CAP and its SAFE AREA (nominal transcriptions,
⚠ verify-against-datasheet — both portrait platforms overlay roughly the bottom 15%
and the right rail), and the three things a preset genuinely adds are framing INTO the
safe area, the cap as a refusal NAMING the platform (never a silent trim), and the
aliasing check as a measurement. **The framing is closed-form per round**: at a fixed
orbit orientation each corner's NDC coordinate is `a/(D + w)` with a and w fixed, so
the minimal filling distance is a max over per-corner solutions, and the asymmetric
safe area is honoured by shifting the TARGET in view space (the orbit camera's one
lever — it has no principal-point offset), iterated because the shift is first-order in
depth-spread-over-distance. **Finding one: a blanket distance floor eats the fill** —
a diagonal-length floor parked the landscape preset's camera past its own constraints
and the safe area filled only 0.70; the floor must be the exact front-of-eye
requirement (the nearest corner's depth, unchanged by the lateral shift), which is the
cries-wolf rule for a guard in a solver: a guard wider than its own condition quietly
becomes the binding constraint. **Finding two: a frame-to-frame matrix delta FOLDS at
π, so the Nyquist refusal it was to feed was unreachable** — a 4.2 rad step and a
−2.1 rad step are the SAME rotation matrix, found by the test that expected the
refusal to fire and watched the measure read 2.09 for a two-turn clip. The measure
samples at HALF steps and sums the two principal angles, which reads the true advance
up to 2π per frame; an exact 2π per frame is genuinely invisible to ANY sampling
measure, the honest boundary. The measure is body-level and says so — tooth-level
detail aliases at the PITCH, not the turn (the gear-clip lesson), and only the caller
knows a tooth count — so `MaxRotationPerFrameRadians` rides the result with
`SlowdownFactorFor` as the caption number, and the hard refusal sits at π where even
the direction of rotation is gone. The safe-area overlay is CPU-drawn on the finished
poster pixels (no shader, no three-front-end plumbing, and a poster without it is
exactly the export's frame); MP4 stays the documented ffmpeg recipe carried ON the
result (`libx264`/`yuv420p`), the dependency-free H.264 assessment staying filed. The
docs figure renders portrait through DocsGen's new `renderSize` snippet variable — the
one generator change, following the declared-variable convention `camera` set.

**B-Rep-exact interference volumes landed as a routed grade with the estimator NAMED,
and the test fixture found a mesh-boolean defect the feature then had to route around**
(`InterferenceVolumeSource` on `InterferenceRange`; `MotionInterference.IntersectionVolume`):
`CheckInterference`'s opt-in volumes now take `BrepBoolean.Intersection` of the POSED
solids whenever both parts lower (`TryGetSolid`, the shared cached lowering), both
placements are proper rigid motions (`BrepSolid.Transformed`'s own precondition — the
guard is the kernel's, not a restatement), and the boolean accepts the configuration;
every refusal falls back to the exact mesh boolean of the display tessellations, and
the SOURCE rides the result because two estimators answering one question at two grades
must both be nameable. The B-Rep answer is exact where the mesh one carries chord
error: the closed-form fixture (a spinner whose crossing's middle frame is EXACTLY π,
73 frames over [0, 2π] putting a frame value there, overlap the box [7,9]×[−1,1]×[−1,1])
measures 8 to 1e-9 through a genuinely rotated pose. **Two fixture findings.** The
coaxial equal-radius refusal cannot exercise the fallback: its two tessellations
COINCIDE rather than cross, so the transversal narrow phase rightly reports no clash at
all — a refusing configuration and a detectable clash are different properties. And the
OTHER named refusal, the tangent bicylinder, turned out to be hostile to the MESH
boolean too: `MeshBoolean.Intersection` of the Ø4 degenerate Steinmetz pair returns
10.56 against the analytic 42.67 for the B-Rep-route tessellation — and the follow-up
diagnosis overturned the mechanism TWICE (see the todo entry): not dropped patches
(inclusion–exclusion and the partition identity hold to round-off, every result
closed — the classification is perfectly consistent) but the IMPRINT's seam topology
where the surfaces GRAZE, and ALIGNMENT-dependent at that — the same geometry through
`MeshPrimitives.Cylinder` and its exact quarter-turn copy measures 42.26, correct
within chord grade. Both alignments are pinned by test so a fix announces itself. So the fallback fixture is
a NON-RIGID placement instead — a z-scaled post, which `Transformed` refuses while the
mesh boolean of boxes stays exact — and the test asserts the same 8 through the mesh
grade's name, the deliberate act of proving the two roads meet where both are exact.

**Variable outline offset landed with the filed construction corrected by its own
derivation** (`Region2dOffset.Offset(region, distances)`; per-vertex distances, linear
in arc length): the backlog filed "trapezoid slabs + interpolated-radius joins", and
the trapezoid is measurably the WRONG slab — the exact swept region of a linearly
varying disc along a segment is bounded by the external TANGENT line of the two end
circles (feet at n̂·cos φ − d̂·sin φ with sin φ = Δr/L, satisfying the tangency
condition m̂·(L·d̂ + Δr·m̂) = 0 exactly), and the trapezoid through the offset
endpoints under-covers near the smaller end by the tangency wedge — asserted by a
witness point BETWEEN the secant and the tangent, derived from the quadratic rather
than eyeballed. Each vertex takes a ROUND join of its own radius between the adjacent
tangent-foot directions — `AddCornerJoin` already arcs between whatever unit normals it
is handed, which is what made the reuse exact — and all-equal distances DELEGATE to the
constant path outright, bit-identical by construction. **The oracle is exact
membership**: p is in the dilation iff the region holds it or some edge's swept disc
reaches it, and minimising |p − e(t)|² − r(t)² is QUADRATIC in t (both terms are), so
the predicate is closed form — thousands of grid probes assert the built region against
it wherever the margin clears the join arcs' chord band, the only approximation in the
build (the tangent slabs are exact lines). Refusals by name: holes (v1 — compose),
non-positive distances (outward only; variable EROSION needs the complement frame's
distances defined, a real open question), and Δr ≥ L (the larger end's disc swallows
the whole edge sweep — the offset outruns the outline, and no tangent exists). The
curved tier's variable twin stays open with its reason stated: a variable offset of an
ARC is a spiral, which the lines-and-arcs vocabulary must FIT rather than carry.

**CFF `seac` accent composition landed as the bounded add it was filed as, with the one
semantic decision documented rather than inherited** (`CffOutlines` — charset parsing
off DICT op 15, the transcribed Standard Encoding, and the 4-argument endchar):
resolving a seac component is CODE → SID (Standard Encoding, the 256-entry table
transcribed under the ⚠ verify-against-datasheet convention) → GID (the charset:
formats 0/1/2 parsed, absent-or-0 the ISOAdobe identity where SID == GID, the
predefined Expert charsets refusing at seac by name since no text font uses them), and
the composed glyph is the base's contours plus the accent's translated by (adx, ady)
VERBATIM — the decision being that Type 2 carries no sidebearing operands, so the
Type 1 `asb` correction has nothing to correct, stated in place rather than silently
assumed. A component that is itself seac refuses by name — the spec forbids it, and the
refusal is what bounds the recursion at one level rather than a depth counter. The
verification is the synthetic-font convention at its strictest: composition asserted
coordinate-for-coordinate against the component glyphs' own outlines, and the charset
tests choose codes the TABLE routes to DIFFERENT glyphs than the identity would
(format 0 reverses the identity, format 1 shifts it), which is what proves the charset
was read rather than assumed — a parser bug that fell back to SID == GID would pass
every identity-charset test and fail these by drawing the wrong letter.

**MTEXT multi-line notes landed with the one-`Compute()` invariant KEPT rather than
traded** (`SheetNoteBlock` on `SheetText`; `DxfMText` both ways): the filed entry
judged the feature worth doing "only alongside a decision about where line breaking
lives", because collapsing a note into one object would make the SVG writer restack
lines and break the rule that every writer consumes ONE `Compute()`. The resolution is
that the grouping does not need to BE the geometry: the stacked single-line
`SheetText`s remain what every writer draws (SVG and PDF untouched byte for byte), and
a multi-line note's lines additionally share a `SheetNoteBlock` by REFERENCE —
insertion point, DXF attachment (5 middle-centre for a dimension's centred note, 1/3
top-left/right for a leader note, since the two stack about different datums), and the
full text with its own breaks. Only the DXF writer reads it: a run sharing a block
collapses to ONE MTEXT (`\P` separators, group 71), a single-line text stays a TEXT
entity byte-identically, and the reader joins code-3/1 chunks in order and restores
the breaks — so a DXF consumer finally sees one note instead of N unrelated strings,
which is what the entity exists for. The build's finding: `SheetNote` stacks its own
lines SEPARATELY from the dimensions' shared `CenteredText`, so the first cut grouped
only dimension notes — the leader-note test caught the miss, and both sites attach
blocks now, each with the attachment its own stacking datum implies.

**Multi-line text on a path landed as the analytic-only tier the entry sketched, with
the refusal narrowed rather than removed** (`TextOutlines.SketchesOnPath`/`Shape.TextOnPath`
accept `'\n'` on `Line2d` and `Arc2d` paths): line k rides the path offset k
line-heights DOWN — down being minus the glyphs' up, the path's left normal — and only
two curve kinds offset EXACTLY (a parallel line; a concentric arc keeping the angular
span), so every other path keeps the refusal NAMING those two kinds and the reason (a
general open curve's offset has no exact form and can self-intersect — the caller
builds it deliberately). The orientation rules compose rather than being restated: a
counter-clockwise arc's left normal points at its centre (the recorded dial
convention), so its lines stack OUTWARD, a clockwise arc's inward — with an inner line
reaching the arc's own centre refused by name. Two oracles carry it: the straight
horizontal path with two lines reproduces the ordinary two-line layout to nine decimals
(THE reduction — the offset line is exactly where ordinary layout puts its second
baseline), and on a circle the line-1 ink centres sit EXACTLY one line-height farther
out than line-0's — measured as the DIFFERENCE of the two rings, because the glyph's
constant baseline-to-ink offset lies along the radial direction and cancels there,
where asserting the raw radius books that offset as error (the fixture finding: 48.5
measured against a naive 52 expected, the 3.5 being the 'I' glyph's own ink centre
above its baseline).

**Loft sections carry holes now, and the filed assessment — "topology work in
`BuildLoftedSolid`, no new surface math" — was exactly right** (`SolidFactory.Loft`
gained a `holesPerSection` overload; `Shape.Loft` sketch sections, `Shape.LoftAlong`
and the pure taper's lowering all feed it): hole j of every section lofts into its own
inner skin and the caps become faces with hole loops. Three decisions carry it. **A
hole family is an ordinary loop family**, so the per-family machinery
(vertices/section edges/strips/rails, or the closed band) is ONE local function run for
the outer sections and each hole family — a family may be a segment chain while
another is a single closed curve (the drilled-taper archetype: square outer, circular
bore), because single-closed-ness is a per-family property. **A hole is aligned by the
SAME least-twist rules the outer gets and then `Reversed()`** — the extrude factories'
convention, so the winding carries the wall orientation and the cap loop builder needs
no per-family cases: the same sense rule (reversed order/`false` on the bottom,
forward/`true` on the top) applied to a reversed family IS the opposed hole loop.
**Every hole strip shares the OUTER's global v parameterization** rather than its own
chord lengths — the sections are common stations, and a per-family v would let a
hole's rail disagree with its cap about where a section sits. Correspondence is by
index (hole j of section k is hole j of section k+1, the `WithHole` declaration order
at the sketch level), hole-count mismatches and per-family incompatibilities refuse by
name, and an empty or null hole list reproduces the unholed loft — asserted as
bit-identical tessellation positions, which the shared-machinery refactor makes a
theorem rather than a hope. The oracles are exact identities: a square-holed square
frustum measures `Frustum(16,4,4) − Frustum(1,0.25,4)` to nine decimals (every wall of
both families is a planar trapezoid), a circular bore subtracts exactly the inscribed
n-gonal cone frustum, two holes read genus 2 off the tessellation's Euler
characteristic, and the taper flip turned `TwistExtrudeTests`' Impossible-naming test
into a Native one whose mesh route (the section sweep) agrees with the B-Rep route to
six decimals on the same closed form. The consequence stated in the API: a pure taper
of a holed sketch is B-Rep-Native, so a washer tapers with its bore about the same
scaling centre instead of refusing toward a boolean.

**Integer-ratio loft section counts split automatically now** (the loft follow-ups'
first compatibility route; `SolidFactory.MakeLoftFamilyCompatible`): where a family's
segment counts differ by an integer ratio, the coarser member's segments split into
equal-parameter `CurveSegment` pieces before alignment — no geometry moves, and the
correspondence stays natural (a square lofting to an octagon splits each side once,
its corners AND midpoints pairing with the octagon's corners). It is per FAMILY, so
holes inherit it, and a non-integer ratio still refuses by name at both the kernel and
the `Shape.Loft` construction check, since it has no canonical correspondence. Two
details carry the exactness: breakpoints are computed ONCE per segment so consecutive
pieces share their joint parameter bit-for-bit (the canonical-crossing rule), and the
extreme breaks take the segment's own domain values verbatim so the chain's corners
are untouched. The pieces compose with everything downstream because `CurveSegment`
already speaks the vocabulary — `Underlying` forwards, so the sampling unification
still recognises a split straight piece and re-expresses it as a degree-1 NURBS from
the PIECE's own endpoints where its partner strip is curved (which is what makes the
old refusal fixture, Rectangle to RoundedRectangle at ratio 2, loft and weld). The
oracle with teeth is the identity case: a 4-segment square lofting to its half-scale
copy described as 8 points pairs corners with corners and midpoints with midpoints, so
every strip is half of the unsplit frustum's planar wall and Frustum(4, 1, 3) stays a
nine-decimal identity of the tessellation — a split placed wrongly by even one index
breaks it. The NURBS degree-elevation route (A5.9) stays filed as the remaining half.

**Deformed-ray picking landed, and it retires the stale-hover refusal by completing its
own reasoning** (the displaced-edges pattern — the refusal existed because the pick
index did not describe the drawn shape, and now the ANSWER does): a displaced part's
pick BVH is still built exactly once, at the part's own `DeformScale` (the
spatial-index-cannot-be-a-uniform reason stands untouched), and `ScenePick.Nearest`
now takes the frame's `deformFactor` and corrects in two phases. The BROAD phase
queries the once-built index through the new `Bvh.Query(ray, inflate, hits)` with every
box expanded by `MaxDisplacement·|BuiltScale·(factor−1)|` — conservative BY
CONSTRUCTION, since every vertex moves at most that far from its indexed position and
expansion can only ADD candidates, the cross-plane-hole-validation sound-in-the-accept
direction. The NARROW phase then tests the EXACTLY-displaced triangles: `PickMesh`
carries the raw per-render-vertex displacement vectors (unscaled, so the displayed
vertex is the indexed one plus `BuiltScale·(factor−1)` times it), and Möller–Trumbore
runs on those — so the hit is exact at every factor, not approximated, and the world
point lands on the drawn surface. A delta of exactly zero — an undisplaced part at any
factor, or any part at factor 1 — takes the incumbent arithmetic bit for bit (the
exact-zero family). The test with teeth is the broad-phase discriminator: a part whose
displacement carries it 20 units clear of its indexed boxes is FOUND at factor 3 by a
ray that misses every indexed box (only the inflation can see it) and honestly MISSED
at factor 1 by the same ray. Both hover funnels simply pass the factor now;
`FieldRendering.HoverIsStale` is deleted rather than kept answering a question that no
longer arises. What deliberately did NOT change: the index is never rebuilt (the cost
the design exists to avoid), and the ghost/legend/uniform machinery is untouched.

**The property-nonlinear transient landed as a COMPOSITION, and the composition is the
design** (`ThermalNonlinear.SolveTransient` — c(T), and k(T) per step, closing the
thermal follow-ups' capacity item): a temperature-dependent property makes every step's
matrices functions of the state, so the honest structure is a sequence of ONE-STEP
constant-property transients — per step, evaluate the laws per element at the step's
START temperatures, set the internal overlays (a new `OverlayCapacity` carrying rho·c
beside the conductivity overlay, NaN = keep the model's own material), and run
`ThermalSolver.SolveTransient` for one step seeded from the previous end state through
`InitialField`. Nothing of the stepping machinery is restated — the theta schemes,
lumping, prescribed snapping and the per-step first-law identity all apply verbatim,
each sub-run being internally a constant-property step — and the cost is stated rather
than absorbed: `Factorizations` equals the step count BY CONSTRUCTION, the
one-factorization amortisation being exactly what a property nonlinearity necessarily
gives up. Property evaluation is EXPLICIT in the step (start-of-step temperatures),
first order in the property and so matched to backward Euler's own order; the one
refusal is time-varying load/prescribed laws, because a sub-run's clock restarts at
zero and the laws would be sampled at the wrong instants (re-basing them per step needs
a condition-rebuilding seam — filed with that name). The oracle is an IDENTITY rather
than a convergence claim: an insulated cube under uniform generation keeps a spatially
uniform field (the generation load vector and the capacity matrix's row action share
the partition-of-unity weights, so the uniform increment solves the step exactly), and
the FE run therefore matches the three-line scalar recurrence T ← T + dt·g/(ρc(T)) to
round-off at ANY step size — beside it, first-order convergence onto the enthalpy
closed form ρ(c0·T + c0·γ·T²/2) = g·t supplies the physics check, and laws returning
exactly the material's own constants reproduce the plain transient BIT FOR BIT (the
wrapper multiplies `Density` by the returned c — the same product the material caches —
and the overlay branch feeds `ThermalElement.Capacity` the same double, so the
degeneration is arithmetic, not tolerance).

**The loft's circular-vs-NURBS closed-section refusal resolved by CONVERSION, and the
filed degree-elevation route is retired as answering a design the loft never took**:
the backlog held "single-NURBS sections want degree elevation + knot merging (A5.9)"
— but `LoftedSurface` samples every section curve by its own NORMALIZED parameter, so
sections never needed a shared basis at all; the only real incompatibility was
tessellation DENSITY (a `Circle3d` samples angularly at `segmentsPerCircle`, a NURBS
at `curveSamples`, so the rim and the skin grid could not weld), which is exactly what
the old refusal's remedy prescribed to the caller — "rebuild the circle as a NURBS
section" — and `UnifyLoftSectionSampling` now does automatically: a mixed family's
circle-backed members are re-expressed as their EXACT rational NURBS full circles
(`NurbsCurve.Arc` over the full turn — the guard's 1e-12 slack admits 2π — with the
last control point ASSIGNED the first, since the trig round-off between `cos(0)` and
`cos(2π)` puts the arc's end a few ulps from its start and a closed curve's end must
be its start exactly). A rigid `TransformedCurve` over a circle converts through its
transform's image; a non-rigid one (an ellipse image) and any other carrier refuse
naming the type. Two constructions check each other in the tests: the fixture builds
its own NURBS circle independently through the public Arc factory, and a circle lofted
to that spelling of ITSELF is a closed cylinder within chord error of πr²h, while a
circle-to-circle loft (no mix) keeps the incumbent angular path bit for bit. With
this, the loft compatibility story is complete: equal counts loft directly,
integer-ratio counts split, circles convert against NURBS partners, and what remains
refused (non-integer ratios, mixed chain/closed families) is refused because no
canonical correspondence exists, not because machinery is missing.

**Time laws compose through the property-nonlinear transient now, and the seam is one
field where the filed shape guessed a rebuild**: the follow-up was filed as "wrap
`t => law(t0 + t)` at a condition-rebuilding seam the model does not yet expose" —
but the laws are only ever INVOKED at four sites (the transient's setup and per-step
evaluations, `AssembleLoad`'s instant form, `PrescribedVector`'s), so the seam is an
internal `ThermalModel.LawTimeOffset` added at exactly those invocations: a sub-run's
restarted clock is re-based to the run's own instants by setting the offset before
each one-step sub-run and clearing it in the finally, the overlay convention verbatim
and no condition is rebuilt. Offset zero is the incumbent arithmetic bit for bit
(adding 0.0 to a non-negative instant changes no bits), and the oracle is the
degeneration WITH laws: a constant-property nonlinear run carrying a ramped flux AND a
ramped prescribed temperature reproduces the plain lawed transient bit for bit at
every stored state — asserted at a DYADIC step, which is the claim's honest boundary:
the re-based instant is (n−1)·dt + dt against the plain run's n·dt, two spellings that
agree exactly only where dt's products are exact and differ by their own ulp at a
general step (measured: dt = 0.4 differs at ~1e-11 relative after the solve, dt = 0.5
to the bit). A WRONG offset shifts the ramp by whole steps and fails at any dt, which
is what the assertion exists to catch. **Landing it caught a real defect of the
restating-the-rule family**: `InitialState`'s prescribed-wins-at-t=0 snap read the
model's stored CONSTANTS, and a lawed node's constant is its law at GLOBAL zero — so a
re-based sub-run's initial snap silently rewrote every lawed prescribed node to the
run-start value (measured: the composed prescribed-law run diverged from state 2 by a
tenth of a kelvin per step, while the flux law composed bit-for-bit — the isolation
that named the path). The fix asks the ONE rule instead: the snap reads
`PrescribedVector(model, 0.0)`, which is law-aware and offset-aware, and a law-free
model reads the same constants through it bit for bit.

**A non-runnable live example now says so on the page** (the docs-site live-examples
plugin): the manifest always carried each refusal's reason — the compiler's own words —
and the site printed nothing, so the boundary was visible only to whoever opened the
JSON. A figure whose example cannot run in the browser now gets a one-line muted
figcaption from the same manifest ("This example runs on the full kernel only — it
uses MillPass, which the browser build does not ship."), the short clause extracted
from the compiler message's first quoted name with the full text riding on the hover
title, and a figure with no manifest entry at all stays a plain screenshot. One build
lesson worth keeping: **Astro's content-layer cache serves a page whose MARKDOWN did
not change without re-running the rehype pipeline**, so a plugin edit alone looks like
a no-op on every existing page — the plugin change verified in isolation while the
built page stayed stale, and deleting the `.astro` store (or building with the cache
cleared) is what makes a pipeline change actually re-render; the tell was the new CSS
appearing (the layout re-renders) while no figure changed.

**Live-example cache-busting landed with the filed fix overturned by the manifest's own
design rule**: the filed remedy — a content hash per example in the committed manifest
and filename — would churn ~150 manifest lines on every kernel commit, which is
precisely why byte counts were excluded from the manifest in the first place (it is
deliberately a deterministic function of the SOURCE, not of the binaries). The demo
instead stamps the fetch URL itself: `examples/<id>.dll?v=<stamp>`, where the stamp is
a fold of the shipped EngrCAD assemblies' MVIDs (one anchor type per assembly in the
live-example reference set), content-stable under deterministic compilation — so the
reader's HTTP cache is busted exactly when a deploy changes the geometry an example
would build, and never otherwise, with zero committed churn. Verified through a real
headless-browser fetch against a clean publish: the server log shows
`GET /examples/annotations.dll?v=aa4632277efad8041f87cf22994d7b4a`.

**The sphere's meridian density defect is fixed, the torus's half is deferred by a
measurement, and the STEP finding is the piece worth keeping**: `MakeSphere`'s
generators are now `CurveSegment`s over the meridian `Circle3d` — the whole-solid
fillet's corner-arc rule, since the angular density rule reads `Underlying` and a
rational NURBS arc hides it — so a sphere's facet count quadruples per density
doubling and the sphere-piercing corpus member's worst agreement CONVERGES with
density (0.67 → 0.74 → 0.94) where the old NURBS-parameter placement read a
non-monotone 0.88 → 0.70 → 0.92, its coarsest row flattered by where the rational
arc's samples clustered. **A surface GENERATOR has no edge vertices to carry its
trim** — `StepWriter.Simplify`'s flatten-the-segment rule is right for edge curves and
wrong for generators, so a `CurveSegment` generator now exports as a `TRIMMED_CURVE`
with PARAMETER trims and the reader reconstructs the segment verbatim (a new
`TRIMMED_CURVE` case; parameter trims only, refusing cartesian by name). NESTED
segments compose affinely down to the first non-segment base before emission, or the
trim is written against a basis `Simplify` flattens away — measured as a drilled
plate's bore wall arriving at 10/11 of its own span, exactly the drill overshoot the
inner segment carried. The TORUS conversion was built, measured, REVERTED — and then
LANDED once the real blocker fell: the 192/96 explosion was never the
band-with-holes tier (not reached on that fixture) but `RowedPeriodicBand`'s
up-front u-monotonicity gate, which the bore-scalloped chain always trips, dropping
the face to the merge walk whose fans refinement exploded. With the gate relaxed
(the chain-adjacent `StripBetween` threads the scallop; `SweepCycle` splits each
piece at its own u extremes) the torus generators are `CurveSegment`s over one minor
`Circle3d` like the sphere's, the meridian takes the angular density, and the
torus-cut-with-a-bore member's worst 192/96 agreement went 0.0198 → 0.9601. The
first diagnosis naming the wrong tier is the lesson: when a pipeline stage is
suspected, run the pipeline without it — the refusal reproduced with the rows
disabled, which is what pointed one tier over.

**ISO 286 fits and tolerance stackups** (`Iso286`/`ToleranceStackup` in Modeling;
docs `examples/fits.md`): the fit tables are a TRANSCRIPTION under the
verify-against-datasheet flag, stored in the standard's own micrometres and converted
to model millimetres at the API — the `Materials` lesson, that a constant must be
asserted in the form a human checks. Three decisions carry it. **Scope is the
hole-basis d–p band, refused by name beyond it**: letters a–c and r–z split their
fundamental deviations at sub-range boundaries the main table does not have (c changes
at 40 within the 30–50 range), so transcribing them halfway would be exactly the
plausible-wrong-row failure the flag exists to prevent — they are filed with the
reason, H11/c11 and H7/s6 named as the preferred fits waiting on them. **A fit's KIND
is derived from the clearance extremes, never looked up** — `MinClearance ≥ 0` is
clearance, `MaxClearance < 0` interference, else transition — so the classification
cannot disagree with the numbers it classifies (H7/p6 at Ø40 is interference by the
−1 µm maximum clearance, not by the letter p). And **the stackup CHAIN is the
caller's design statement**: the filed item said "a walk along the existing mate
graph", and the finding is that mates constrain POSES and carry no toleranced
dimensions — a chain walked off the mate graph would be a guess about which
dimensions matter and in which direction, so `ToleranceStackup` takes the explicit
chain (worst-case from each contribution's own signed band ends; RSS re-centring each
band on its MID so the statistical mean shifts exactly when a tolerance is
asymmetric, the textbook treatment stated rather than implied) and the model-attached
dimension scheme that would make derivation honest is filed as the follow-up.

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
- **"It takes a concrete type, so there is nothing to substitute" is a claim about the
  TYPE, and the thing to check is what an instance of it actually needs.** The viewer's
  remote-control screenshot path carried that verdict for a long time —
  `ViewportRemoteViewer` takes a `ViewportControl`, therefore only a real window could
  exercise it — and it was wrong twice over. A `ViewportControl` constructs perfectly well
  with no Avalonia application, no window and no GL context; and an instance that will
  never be rendered is not a poor imitation of the real thing but *exactly* the fixture the
  screenshot deadline exists for, since "no frame ever arrives" is precisely its state. The
  arm-on-UI-thread / wait-off-it split, the deadline, an unwritable path surfacing its own
  failure rather than a timeout, and the armed capture being claimed exactly once are all
  headless as a result. **Before writing an interface to make something testable, construct
  the concrete type and see what it does** — an extracted interface is a design change, and
  it is the more expensive answer when the cheap one works.
- **What genuinely needs the real thing should then be named narrowly, and driven by the
  product's own vocabulary rather than by synthetic input.** What was left after the above
  is a GL render pass and a live dispatcher, so the windowed test is a child process on
  `--view --rpc 0` driven through the remote-control endpoint — which answers with values
  instead of pixels, needs no window handles, and exercises the bridge on the way. This
  repo's recorded `SendInput` harness (synthetic clicks reaching Avalonia's pointer stack)
  is the right tool for the *pointer bindings*, and the wrong one for everything a verb can
  express. Such a test is opt-in (`ENGRCAD_WINDOWED_TESTS=1`) because opening a desktop
  window on every run is a real cost; the point is that "once per release, by hand" becomes
  one command.
- **A live-window test measures things an in-process one cannot, and that is its whole
  value — expect it to find something.** It did: `list_parts` answers `[]` until the first
  frame renders, because instances handed to `SetInstances` are queued and swapped in *by*
  the render pass while the RPC port is announced from `OnViewportReady`. No stub can show
  that, and no unit test was ever going to.

## 8b. The documentation site

The site is **Astro Starlight** (`docs/site/`) for everything a human reads, with **DocFX
reduced to the generated .NET API reference** published as a static subtree at `/api/`,
and the live WebAssembly demo at `/live/`. CI merges the three trees into one `_site`.

- **The markdown did not move, and that is what makes the migration checkable.**
  `tools/EngrCAD.DocsGen` compiles and executes every tagged C# snippet in `docs/**/*.md`
  and writes the screenshots to `docs/examples/images/`; its docs root is a command-line
  argument, so the content *could* have moved into `src/content/docs/` where Starlight
  expects it. Keeping it in `docs/` leaves the generator's contract, the fence syntax and
  every committed image path untouched, which is what turns **"all 134 committed PNGs are
  byte-identical"** into a real statement about the migration rather than a coincidence of
  it — the same oracle the shared-render-model refactors were held to. The cost is one
  custom `glob` loader with an explicit `base`, and one consequence recorded below.
- **A page that was a FILE became a DIRECTORY, and every relative link is wrong by one
  level — silently.** DocFX served `examples/fields.html`, so `](documents.md)` resolved;
  Starlight serves `examples/fields/`, so the same href resolves one level too deep. This
  is the whole risk of the migration and none of it raises an error: a browser 404s, a
  build does not. Two mechanisms answer it and they are deliberately different in kind.
  **`docs/site/src/rewrite-doc-links.mjs`** (a rehype plugin) resolves each `*.md` href
  against the filesystem, rewrites it to the served route, and **throws when the target
  file is not there** — so the markdown stays navigable in the repository and on GitHub,
  and a renamed page fails the build naming both ends. **`docs/site/check-links.mjs`**
  then validates the BUILT site: every `href` and `src` in the emitted HTML resolved the
  way a browser resolves it, fragments checked against the ids actually emitted.
- **`starlight-links-validator` was tried first and cannot work here** — it derives each
  page's id as `path.relative(srcDir + '/content/docs', filePath)`, one hard-coded
  assumption, so with content in `docs/` every id came out `../../../<page>/` and all 134
  internal links were reported invalid at once. Checking the OUTPUT turned out to be the
  better instrument regardless: it covers `<img>`, `<iframe>` and raw-HTML hrefs rather
  than only markdown link syntax (the screenshots ARE most of this site's references), and
  it is the artifact a reader gets rather than a model of it. **Writing it also caught a
  bug in itself that the plugin's design would have hidden**: taking `dirname` of a page
  URL ending in a slash drops a segment, so `../` links looked one level shallower than a
  browser sees them — which made the two genuinely broken links in `web.md` pass. Both
  guards are pinned by being *shown to fire* (delete a sidebar entry, rename a link
  target), the rule the Surface Nets fixtures already follow.
- **The sidebar is the one source of navigation order**, so `docs/toc.yml` and
  `docs/examples/toc.yml` are deleted rather than parsed — two orderings free to drift is
  the discrepancy this repo keeps removing. A page left out of the sidebar still builds
  and is still reachable by URL, so the checker asserts every built page appears in the
  rendered nav.
- **Image handling is `passthroughImageService()`, and it is a correctness setting rather
  than a performance one.** Astro optimizes relative markdown images through sharp by
  default, which re-encodes a PNG — and several of these are **APNG animations**, whose
  frames would be silently dropped. Passthrough still resolves and fingerprints each
  asset; it just does not touch the bytes (verified: the served `animate-explode.png` has
  the same SHA-256 as the committed one).
- **The base path comes from the deployment, not from the source.** Astro bakes asset and
  page URLs at build time, so unlike DocFX's fully relative output it must be told where
  it will be served; CI reads `base_path` from `actions/configure-pages`, which is
  `/EngrCAD` for a project site and empty for a custom domain. The repository name is
  therefore never written down. The one link that stays relative is the WebAssembly demo's
  (`../../live/` from `/examples/web/`), because that target is merged in after the site
  is built and its own artifact is path-portable — keeping the reference relative preserves
  that property end to end.
- **The new toolchain dependency is stated rather than absorbed**: Node ≥ 22.12 (CI pins
  24) now sits beside the .NET SDK, pinned exactly in `docs/site/package.json` with a
  committed `package-lock.json`, and `docs/writing-examples.md` tells a contributor how to
  build the site locally.

## 8c. Live documentation examples

Every example page carries a committed screenshot; each one whose snippet can run in a
browser now also carries a **Run it in your browser** button that swaps the picture for the
geometry kernel building *that* model in the reader's tab. The pieces are
`tools/EngrCAD.DocsGen/LiveExamples.cs` (compile and emit), `src/EngrCAD.Web/LiveExample.cs`
(load and run), `docs/site/src/live-examples.mjs` (the poster) and
`samples/EngrCAD.WebDemo`'s `?example=<id>` (the host).

- **The screenshot stays, and the viewer starts on a CLICK.** Three arguments, and the
  first is the one that settles it: the committed PNGs are this repository's regression
  oracle for anything that changes what a reader sees — an ambient-occlusion change moved 7
  of 106, a 2D-stroke corner fix moved exactly the 2 stroke-derived figures, and a
  whole-render refactor was validated by all 108 being byte-identical. A live-only page
  throws that away. Second, it is a payload decision with numbers behind it: the app is
  **2.8 MB brotli** and even without AOT the kernel runs ~19× slower than native, so
  charging every reader of every page for a picture would be a bad trade, while a reader
  who asked to interact has agreed to wait — and the runtime is then cached for every other
  example they open. Third, the site keeps building with no GPU and the page degrades to
  exactly the screenshot it always was with no JavaScript.
- **The kernel RUNS; a baked mesh was the alternative and was declined.** `GltfWriter`
  already exists, so shipping glTF per example would have been easy and would have made the
  page a model viewer rather than a kernel demonstration — the interesting claim is that the
  same code produces the same geometry in a browser, and only running it makes that claim.
  What made running it affordable is that **the docs build already compiles every snippet**:
  it compiles each one a second time and emits what it compiled (mean **6.0 KB**, max 12.0
  KB, 710 KB for all 118), so the browser needs no Roslyn — which would be several megabytes
  — and fetches one small file.
- **The reference set IS the rule, so the C# compiler decides what is live.** The browser
  compilation sees exactly the transitive closure `EngrCAD.Web` ships (Core, Mesh, Implicit,
  BRep, Interop, Modeling, Viewer.Core) and no globals. A snippet reaching for `EngrCAD.Fea`,
  for the desktop viewer's `EngrCad.RenderToImage` or `ConstructionPreviewRequest`, or for
  the docs-only `Scratch` directory simply does not compile, and the refusal carries the
  compiler's own words into the manifest. That is a maintained list nobody maintains: it
  cannot go stale against a payload change, because it *is* the payload. Measured: **118 of
  132** rendered examples run, the other 14 recorded with their reasons.
- **The one thing a reference set cannot catch is an empty filesystem**, since the browser's
  is an in-memory FS holding only the app's own assets. That is a short named list
  (`System.IO.File`/`Directory`/`FileInfo`/`DirectoryInfo`/`FileStream`, `System.Environment`)
  resolved through the **semantic model** rather than by scanning text — `heightmaps.md`
  names `Heightmap.ReadPng` in a comment while computing its grid entirely procedurally, and
  a substring scan refuses that page wrongly. Two `text.md` figures load a system font and
  are the only examples it removes.
- **A globals-less script cannot see `object`'s statics** — a real finding, because it looks
  like a capability refusal and is a scope one. Roslyn puts a script's globals type's
  members in scope, inherited ones included, so a submission compiled with no globals fails
  on a bare `ReferenceEquals(a, b)`, which is legal in every ordinary C# class and is used by
  `chamfer-fillet.md`'s drafted block. Passing `object` as the globals type restores exactly
  that scope and nothing else: no assembly reference is added and `Scratch` still does not
  exist, so the snippet that needs it is still refused for the right reason. That took the
  live count from 117 to 118.
- **The submission ABI is what neither side's compiler checks, so the round trip is the
  test.** A snippet is a C# *script*: Roslyn compiles one into a type with a static
  `<Factory>(object[])` returning `Task<object>`, where slot 0 of the array is the globals
  and the factory parks the submission INSTANCE in slot 1 — and every top-level variable is a
  **field on that instance**, which is exactly how `ScriptState.Variables` finds `scene`. The
  loader reads the same fields without Roslyn, finding the factory by SHAPE rather than by
  the `Submission#0` type name. `tests/EngrCAD.DocsGen.Tests` emits, loads, runs and compares
  the *geometry* (a plate with a through bore against its own closed form), which is what a
  "a scene came back" assertion would not.
- **The trimmer is why it works, and it works by default — which is why it is now said out
  loud.** A dynamically loaded example calls the kernel by reflection and the trimmer cannot
  see it. Blazor WebAssembly trims in `partial` mode, leaving assemblies not marked
  `IsTrimmable` alone, and none of ours is; the demo therefore lists the kernel assemblies as
  `TrimmerRootAssembly`, which measured **costs nothing** (identical published size). Without
  that, a default change would break every example that uses an API the demo itself does not
  — which is most of them, and it would break at run time in the reader's browser.
- **The manifest is the committed artifact and the assemblies are not.** `live-examples.json`
  sits beside the markdown so a plain `npm run build` produces the same site CI does, and it
  is deterministic on purpose — no timings, no byte counts (the informational-field rule the
  document format already follows), so it is not dirty after every run. The assemblies go to
  the demo's `wwwroot/examples/`, gitignored, where the publish picks them up as ordinary
  static assets; CI's assemble step then asserts every live id in the manifest has its file,
  because losing them is otherwise silent — the pages build, the buttons appear and every one
  of them 404s.
- **The poster's iframe URL is relative AND built in the browser**, from a `data-src`
  attribute. Relative keeps the app path-portable (root, `/EngrCAD/`, local preview);
  building it at click time keeps it out of the markup, so `check-links.mjs` — which resolves
  every emitted `href` and `src` against the emitted files — does not fail on `live/`, a
  directory CI merges in afterwards. What the checker *does* verify is the one thing a data
  attribute can still get wrong: the number of `../` steps, asserted to resolve to
  `<base>live/` from a page at any depth, and shown to fire.

## 9. Further capabilities

- **A matcap is PROCEDURAL here, and the reason is the parity rule, not the look.**
  A matcap shades by sampling a lit-sphere image at the view-space normal; the classic
  implementation is a texture, and this render stack has no texture machinery at all —
  no sampler uniforms, no colour image decode — and the one-shader-set rule means any
  texture would have to reach three front ends (window GL, offscreen EGL, WebGL2
  through `engrcad-gl.js`). An **analytic** matcap needs none of that: Gaussian lobes
  over the view-space normal, evaluated in `ViewerShaders.MeshFragment` behind an
  `uMatcap` int selector, with the lobe constants — which ARE the material — living in
  the one file all three front ends compile. Three riders carry the safety argument:
  `ShadingStyle.Lit = 0` because a linked program's uniforms initialize to 0, so a
  front end that says nothing gets the incumbent look (the committed docs PNGs are the
  oracle that the default moved nothing); the selector is deliberately NOT a
  `ViewStyle` member (the style says what is drawn — points, lines, fills — shading
  says how a fill is lit, and the two compose); and there is no per-part override,
  because a scene lit two ways reads as a rendering bug. The view-space normal costs
  no new plumbing: `uView` is a program-wide uniform every front end already sets, so
  the fragment shader just declares it too. Interactions stay orthogonal by
  construction — AO multiplies the matcap sample exactly as it multiplies
  ambient+diffuse, the section cut-face flat material returns before the lighting
  model, and the selection blend is folded into the surface colour before either
  lighting path reads it.
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
    displacement tracks is future work, not half-supported — and the shape is settled
    even though it is not built: a `DisplacementTrack` returns a per-instance DELTA
    (matched by occurrence path) that `Animation.At` post-multiplies onto whatever the
    pose track produced, N of them allowed because deltas compose where absolute pose
    lists do not. `ExplodeTrack` already IS one (it computes `Occurrence.ExplodeDisplacement`
    and adds it to a frame), so it converts cleanly. The hard one is `MechanismTrack`,
    and it is a PRODUCT question rather than a derivation: its delta is meaningful only
    against the assembled pose it was swept from, so an explode composed on top of a
    running mechanism displaces parts along axes the mechanism has already rotated —
    either exactly right (the exploded view of a posed mechanism) or exactly wrong (the
    offsets were designed in the assembled configuration). Do not build it until a
    concrete clip needs it and can settle that; the honest interim is `ExplodeTrack.Stagger`,
    which sequences within one track and is what most assembly animations actually want.
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
- **Extract the VALUE, not the lifecycle - which is why `PartUploads` exists and
  `ViewerModel` still does not.** All three front ends built the same five things per part
  before touching GL (`RenderMesh.CreateFlat`, the `FieldRendering.TryBuild` result, the
  occlusion array, `Part.GetFeatureEdges`, `WireframeEdges.Extract`, plus a `PickMesh`),
  and each is a pure function of the part. That extracts cleanly as `PartUpload` +
  `PartUploads.Build`. What does NOT extract is *scheduling*: the window streams uploads
  per part through `TabMeshLoader` on two threads, the offscreen pass is one-shot and
  synchronous, and the browser interleaves awaited JS uploads on one thread. A shared
  "ViewerModel" would have to abstract exactly the part that must not look the same, so it
  stays declined. **Two things `Build` deliberately does not do, and each is a real
  per-front-end policy rather than an accident.** (a) It does not decide WHICH pieces to
  build - the one-shot offscreen pass skips what its resolved mode cannot use because it
  has no dropdown to change its mind, while the window and the browser build everything so
  a style dropdown never re-uploads; the caller states its policy in a `PartUploadRequest`.
  (b) It does not own the cache: all three key on `Part` reference, but the browser
  releases on tab switch, the window on GL deinit and the offscreen pass with its context.
  Occlusion arrives as a *delegate* for the same reason - the window asks a never-bake
  cache read (so an upload cannot stall the render thread) while the offscreen pass bakes
  inline to stay deterministic, and those are different questions, not one flag. What DOES
  belong in the shared code is every rule about the CONTENT, and the payoff is one of
  them: **a part carrying a displacement draws no feature-edge overlay at any factor** had
  been written out three times, once per pass. Verified the only way a pure render
  refactor can be: all 108 committed docs PNGs byte-identical. (The rule itself has since
  been retired — the edges now carry their own displacement attribute — and retiring it
  was ONE edit for exactly the reason this extraction exists.)
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
- **A CLEAR IS NOT A DRAW, and `glClear` of the depth buffer is masked by `glDepthMask`.**
  The interop applies each draw's state as it goes and never resets it, so whatever the
  LAST draw of a frame set is still set when the NEXT frame starts - and if that draw
  disabled depth writes, the next frame's `gl.clear(DEPTH_BUFFER_BIT)` clears nothing. The
  stale depth buffer already holds the model at exactly those depths, so every fragment
  fails `LESS` against itself and **the model vanishes**, leaving only the draws that
  disable the depth test. Measured on the demo: the silhouette went **32 374 -> 786 lit
  pixels** the moment the annotation overlay went on with the view cube off. Three passes
  emit `DepthWrite = false` (the annotation overlay, translucent fills, the undeformed
  ghost) and three do not (the isolines, the legend, the cube), which is why this was
  invisible for a release: the cube is on by default and drawn LAST, so it re-enabled the
  mask and hid the defect everywhere except the one configuration that turns it off - the
  `?report` self-check. The fix is one line before the clear and one rule for both clears
  (the per-draw `clearDepth` the view cube uses inherits the same trap), and the guard is
  a source-reading test beside the property contract, because this seam has no compiler
  behind it either. **The desktop is structurally immune for a reason worth copying**:
  every site there that turns the mask off turns it back on before returning
  (`AnnotationLayer`, `ViewportControl`, `OffscreenRenderer` all pair theirs), where a
  per-draw state applier has no "before returning".
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
- **A remesher's longest edge stalls because of the FLIP stage, and the fix is a monotone
  guard rather than a bigger algorithm.** The distribution converges fast (95% of edges
  in band within 14 passes) while the maximum sits near 2 L forever, and the standing
  guess — that a collapse leaves a fresh long edge for the next pass to find — is wrong.
  The measurement that settles it is a subtraction: switch flips off and nothing else, and
  the same run ends at *exactly* the 1.33 L split threshold with nothing out of band,
  because the sweep already splits everything too long; switch the smoothing and projection
  stages off instead and the maximum is unmoved at 2.07 L. The flip predicate is pure
  valence arithmetic that never looks at a length, so on an elongated quad it swaps the
  short diagonal for the long one and manufactures exactly the edge the split stage exists
  to remove. `RemeshOptions.PreventLongEdgeFlips` refuses that flip.
  **Three things about it are worth keeping.** (a) **The obvious form of the guard is
  wrong**: refusing *every* flip that would leave an out-of-band edge strands the sliver
  whose only remedy was a flip from 2.5 L to 1.5 L, measured as a worst triangle angle of
  **0.02°** on a remeshed box against 31.7° for the monotone form that also permits a flip
  strictly shortening the edge it replaces. The monotone form buys a statable invariant —
  a flip can no longer raise the longest edge — where the strict form buys a rule that
  merely sounds stronger. (b) **The other half of the plan measured actively harmful and
  was dropped.** Reordering the sweep to try the split before the flip on an already-long
  edge looks obviously right (an out-of-band edge has a definite remedy, so why let a
  heuristic pre-empt it?) and is not: a flip *is* a remedy for a long edge, since the other
  diagonal of an elongated quad is the short one, and splitting instead pins the bad
  configuration and adds a vertex to it — measured, the in-band share fell 92.4% → 85.6%
  and the worst angle 0.89° → 0.17°, more slowly. The incumbent collapse → flip → split
  order was already right. (c) It is **opt-in** despite improving in-band share, maximum,
  minimum and run time together on a cylinder, a box and a sphere, because a remesh is
  wired into `Shape.Remeshed` and changing the default moves committed output; the one
  measure that is genuinely mixed is the cylinder's worst triangle angle (0.58° against
  0.89°), since a refused flip is a valence left irregular.
- **A restriction's correctness bar is the STATE it leaves, not the work it skips — and
  the test for it is vacuous until a counter proves it fired.** Face-aligned reprojection
  accumulated over every face even under queue scheduling, which was sound but made it the
  one stage that did not compose with the scheduler. Restricting it to the faces incident
  to the active set is legal because a face contributes only to its own vertices, and it is
  *bit-identical* rather than merely equivalent for two separate reasons that both had to
  be arranged: completeness (a candidate's incident faces are all visited, and the
  non-candidate vertices that get accumulated on the way are never read and are re-zeroed
  before they are) and **order**, since floating-point addition is not associative. The
  order requirement is what chose the implementation: keeping the ascending face scan and
  skipping only the expensive projection query gives the right order for free, where
  gathering the incident faces into a list needs an explicit sort to restore it — that
  version was built, measured no faster, and dropped. The lesson with the longest reach is
  the testing one: the bit-identity test **passed with the restriction deliberately
  broken**, because the fixture chosen never let a single face be skipped (42 996 of 42 996
  at 60 passes), so the two runs were the same walk. A proxy for "the active set shrank" —
  comparing queue output against sweep output — is not evidence either, since those differ
  for unrelated reasons. An internal counter asserted strictly smaller *before* the
  positions are compared is what gives the test teeth, and it then catches both a dropped
  face and a changed visit order, the latter differing only in the last bits.
- **Sweep scheduling keeps the whole-mesh walk, and the recorded reason for that was
  wrong — the right one is a measurement.** It was filed as "with every vertex active the
  restriction could only add a membership test per face", which conflates a *candidate* with
  a vertex the pass WRITES: under sweep every vertex is a candidate, but a PINNED one is
  never written (the accumulation's read loop skips it), so a face with three pinned corners
  contributes to nothing and skipping it is legal. Built, and bit-identical on all eight
  fixtures tried — so what is declined is the payoff, not the soundness. The share it can
  save is bounded structurally rather than by a fixture: **a pinned set is a
  ONE-DIMENSIONAL subcomplex of the surface** (boundary loops and crease chains), and a face
  needs ALL THREE corners inside it, so the skippable faces are those a curve fully contains
  — a vanishing fraction of a two-dimensional mesh. Measured over fixtures chosen to
  maximise pinning it ran **0.23% to 9.17%**, the ceiling being a cylinder whose n-gon caps
  put a fully pinned rim around a one-triangle-deep fan; no timing could separate that from
  noise, and the control arm proves the instrument rather than the result — an arm skipping
  **0.00%** of faces measured 0.795×, so the harness's own band is wider than the entire
  effect. **The filed scoping was backwards in the instructive direction**: it offered the
  saving to "a small `FixedVertices` set", and a small pinned set is precisely the case that
  skips nothing — the saving needs a large one, and even a maximal one reaches single-digit
  percent. The sweep test now runs on a cylinder rather than the queue fixture's featureless
  sphere, which pins nothing at all (measured `ConstrainedCount` 0 against 98), so the claim
  "sweep walks every face" is finally one a wrong implementation could fail.
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
  **The rails then taught the complementary rule**: sampling a rail DENSELY is as wrong
  as sampling it sparsely, because a ruled band's rail is an exact straight segment and
  25 near-collinear samples of it in a neighbouring PLANAR face's loop force the ear
  clipper into sliver ears (measured on a variable partial run: 18 of 23 facets
  degenerate, non-manifold at 128/96 — the slot's caps had only escaped because their
  arcs interleaved the runs). The resolution is not a rail rule but a SURFACE property,
  `LoftedSurface.IsAffineInV`: a degree-1 two-section loft is P = (1 − v)C₀ + vC₁, so a
  v-chord lies exactly on the surface, the natural grid collapses v to the two section
  rows, and the rails sample as the 2-point segments they are — the helical band's
  infinite-v-step argument, stated by the surface that satisfies it, with the grid and
  the edge sampling reading ONE condition. Landing it surfaced a second recorded trap
  in new clothes: the loft ALIGNER's golden-section seam shift stalls at √ε ≈ 1e-8 (the
  STEP reader's distance-minimization lesson), which put a 1.09e-8 phantom twist into
  every closed-section loft — invisible while 24 interior v rows averaged it away,
  4.5e-9 of volume once they collapsed. A Newton polish on dJ/ds = 0 with exact curve
  derivatives (a root solve) lands the true optimum, and two aligned circles now take
  the wrapper-free exact-zero path.
  **Variable laws reach partial RUNS through the same machinery** (`OpenRun` law
  overload): the law anchors at the run's corners INCLUDING its end vertices, and a
  setback termination is exact at any value because the band's end cross-section is a
  planar quarter arc of whatever radius the law returns there. The run-specific escape
  is named in the sharp-corner refusal: a run may simply STOP before a corner whose two
  edges would carry different radii.
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
- **A baked tracer curve now carries its two exact carriers, and the fixed sampling
  floor became a refinable one — where the consuming tier can take it.**
  `PolylineCurve3d.Carriers` rides from `SurfaceIntersection`'s tracer through
  end-snapping, simplification, rigid transforms and the archive, and
  `BRepTessellator.SampleEdge` refines each chord onto the exact intersection
  (`SurfaceCorner.TrySolvePoint` — the corner machinery's minimum-norm Newton, reused
  rather than restated) until it subtends one natural angular step. Refinement INSERTS
  only, at weld-tier acceptance, so the baked vertices pass through bit-for-bit and
  every guard errs toward keeping the chord: a coarse density, a carrier without an
  implicit form, or a non-converged solve all reproduce the pre-carrier output exactly.
  The band-crossing bore went 0.9988/0.9460/0.3229 → 0.9988/0.9999/1.0000 worst
  facet-vs-surface agreement at 32/96/192. The scope boundary is a MEASUREMENT, moved once on a
  corrected diagnosis: an OPEN branch refines in EVERY loop now (the outer-loop
  clause blamed `TriangulateBandWithHoles` for a refusal that was really
  `RowedPeriodicBand`'s u-monotonicity gate tripping on a bore-scalloped chain —
  relaxed, the chain-adjacent threading absorbs it and the torus-cut-with-a-bore
  member went 0.0198 → 0.9601 at 192/96); a CLOSED branch keeps its baked density,
  measured to buy nothing when refined (74 → 287 samples, same refusal), with the
  per-slab row construction that would carry it filed in todo.md after its first
  build measurably folded and was reverted.
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
  - **Never section-clipped**, and unlike the view-cube widget annotations **do**
    render in the headless pass — they are documentation content, which is exactly
    what offscreen renders are for (the docs example page exercises it).
  - **Occlusion-awareness is a whole-pass choice (`AnnotationDepth`), and the
    mechanism is two depth FUNCTIONS rather than either shape the backlog proposed.**
    `AlwaysOnTop` (the default, and 0 for the `ShadingStyle.Lit = 0` reason) draws the
    overlay with the depth test off; `Occluded` dims what has material in front of it.
    Neither a depth pre-pass nor a second line batch is needed, because *the scene is
    already in the depth buffer by the time the overlay draws*: one buffer is drawn at
    `LEQUAL` in the normal colour and again at `GREATER` in the hidden one, and the two
    comparisons partition the fragments with no overlap (LEQUAL takes equality, GREATER
    does not). That is what keeps three front ends honest — there is no CPU
    classification for them to disagree about, only a draw list.
    - **The dimension's VALUE is exempt, and the measurement is what settled it.**
      Depth-treating the whole overlay turned "40" and "⌀5.5" on the docs plate into
      smudges — the two figures a reader is there for — while the lines it dimmed read
      exactly as intended. A dimension is a POINTER and a VALUE: which side of the
      material the pointer runs on is real information, whereas the text's 3D position
      is a placement, so occluding it destroys information instead of conveying it. So
      `Build` takes an optional second list for glyphs and datum boxes and the value's
      range draws depth-off at full strength (one upload, two ranges — the field
      legend's trick). Passing null is the incumbent single-list build in the incumbent
      order, which is what leaves `Pick` and every always-on-top render bit-identical.
    - **The depth bias moves each point along its OWN EYE RAY.** It exists because the
      interesting annotations are coplanar with the face they document (a radial
      leader lies in the plane of the face whose bore it measures), so without one they
      are classified by which of two rasterizations rounded further — the bias makes
      *coplanar means visible* a decision. The trap is that translating along the view
      direction, the obvious form, slides a perspective point off its ray: measured
      **134 changed pixels** in a render whose overlay had nothing in front of it,
      purely from an anti-aliased 1-pixel line's coverage redistributing. Scaling about
      the eye leaves the screen position exact, so the mode becomes a colour change and
      nothing else (**663 darker, 0 lighter**; a free-space annotation is byte-identical
      in both modes) — and the scale factor is one constant for the whole overlay,
      since a perspective pixel's world size is itself proportional to depth.
    - **Dimmed rather than dashed, for a reason that is not about taste.** A
      screen-space stipple keyed on `gl_FragCoord` is constant along some screen
      direction, so a line parallel to it draws solid or vanishes entirely; there is no
      orientation-free fragment form, and a real dash needs an along-the-line vertex
      attribute reaching all three front ends. And the dim is DARKER, which is forced:
      a hidden fragment is always drawn over the occluder, and every part colour is a
      lit mid-tone brighter than the background, so darkening is the one direction that
      gains contrast in every case the mode can produce.
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
    An annotation you can see must be clickable even when model geometry sits in
    front of its anchors — which stays true under `AnnotationDepth.Occluded`
    precisely because that mode dims rather than hides, so the rule needed no
    revision when occlusion landed. The pick
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
  screenshot rides `CaptureScreenshotAsync`'s capture-on-next-frame path). That layering
  is what makes the stack testable without a window: transport and vocabulary are locked
  headlessly over real sockets with a stub viewer, and only the thin
  `ViewportRemoteViewer` wiring needs a live window. **Off by default, opt-in**
  (`WithRemoteControl(port, token)` / `--rpc [port] [--rpc-token t]`), because the
  endpoint moves cameras and writes files: loopback-only plus an optional
  per-request token is the honest local posture, not security theater.
- **A completion must fire where the RESULT becomes true, and a broadcast is not a
  synchronisation primitive.** `viewer_screenshot` used to answer with a *path*, because
  `SaveScreenshot` only arms a capture the render pass performs on its next frame — so
  the RPC thread had nothing to wait on and the path was a claim about the future.
  Returning pixels needs a per-request completion, and where it fires is the whole
  question. **Not from the render pass**, which is what the backlog entry proposed and is
  too EARLY: `glReadPixels` runs there, but the encode and the write are deliberately
  off-thread, so a task completed there hands back a path to a file that does not exist
  yet. **Not from the `Status` callback** either, but for a stronger reason than "it may
  be posted late": `Status` is a BROADCAST carrying prose for successes and failures
  alike, so a listener cannot tell its own capture from the toolbar button's or from a
  second concurrent request's — matching a string is not synchronisation. It fires from
  the capture's own write, immediately after `File.WriteAllBytes`. Two riders follow.
  **Arming is UI-thread work and waiting deliberately is not** — blocking the dispatcher
  is exactly how the frame would fail to arrive — so the deadline lives in
  `ViewportRemoteViewer` (10 s, under `ViewerRpcClient`'s 15 s so a caller sees the
  viewer's own message rather than a bare socket timeout), not in `ViewportControl`,
  which cannot know how long its caller is willing to wait. And **splitting the write out
  of the GL call is what made the ORDER testable**: `WriteCapture` takes pixels and no
  context, so a headless test resumes on the completion and asserts the file is already
  there — the entry had written this leg off as untestable, and it was only untestable
  while the two were tangled.
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
