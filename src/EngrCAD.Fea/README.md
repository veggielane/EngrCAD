# EngrCAD.Fea

Simulation: **tetrahedral meshing**, **linear-static structural analysis** and **heat
conduction** (steady and transient, with thermal-expansion coupling back into the structural
solve). Takes a closed manifold surface mesh (which every EngrCAD representation reaches —
`Shape.ToMesh()`, `BRepTessellator`, Surface Nets, an imported STL), fills it with
tetrahedra, and solves on them.

Kernel-clean: references only `EngrCAD.Core` and `EngrCAD.Mesh`, no UI and no rendering.
Results leave as `MeshField`s, which is what lets the viewer colour-map them without ever
referencing this project.

## Why a separate project

A `TetMesh` is a genuinely different structure from a `HalfEdgeMesh` — structure-of-arrays
vertices, four vertex indices per element, tagged boundary facets, no half-edge topology at
all — and the algorithms that build it (Delaunay tetrahedralization, boundary recovery,
refinement) share nothing with the surface engine's booleans, subdivision or decimation.
Folding it into `EngrCAD.Mesh` would put a volume representation inside the surface engine
to save one project reference. Simulation also has a lot of growing left to do (stiffness
assembly, thermal, results fields), and this is where it grows. The rationale is recorded in
`design.md` §3b.

## What's here

| Type | Role |
| --- | --- |
| `TetMesh` | The mesh: SoA vertices, tetrahedra, per-element region ids, tagged boundary facets |
| `TetMesher` | The pipeline: surface mesh (or several) → `TetMesh` |
| `TetMeshOptions` | Quality target, sizing field, budgets, facet tags |
| `TetMeshDiagnostics` | What the mesher did — Steiner counts, recovery rounds, volume residual |
| `TetQuality` / `TetQualityReport` | Dihedral angles, radius-edge, aspect, sliver counts, histograms |
| `TetGeometry` | Per-element measures: circumsphere, dihedrals, aspect, edge lengths |
| `QuadraticTetMesh` | The 10-node (quadratic) layer, a pure function of the linear mesh |
| `DelaunayTetrahedralization` | Internal: incremental Bowyer–Watson over exact predicates |
| `SurfacePatch` / `SurfacePatches` | Internal: coplanar same-tag triangle groups, the unit recovery works in |
| `AnalysisMesh` | The analysis view of a tet mesh: nodes, elements, tagged facets, linear or quadratic |
| `Material` / `Materials` | Isotropic elasticity (E, nu, density) + conductivity, specific heat, expansion, and a nominal catalogue |
| `StructuralModel` / `Facets` / `Dof` | The model: materials per region, supports and loads over facet selectors |
| `StructuralSolver` / `StructuralSolveOptions` / `FeaSolveReport` | Assembly, restraint checking, the solve, and what it did |
| `StructuralResults` / `NodalAveraging` | Displacements, strain, stress, von Mises, publishing and `.vtu` |
| `ThermalModel` | Conduction: held temperatures, flux, generation and convection over the SAME facet selectors |
| `ThermalSolver` / `ThermalSolveOptions` / `ThermalSolveReport` | Steady and theta-scheme transient solves, and the energy balance |
| `ThermalTransientOptions` / `ThermalTimeScheme` / `ThermalTransientReport` | Step, count, scheme, initial condition; one factorization per run |
| `ThermalResults` / `ThermalTransientResults` | Temperature, heat flux, per-state publishing and `.vtu` |
| `TetElement` / `TetQuadrature` | Internal: shape functions, element stiffness, consistent loads, quadrature |
| `ThermalElement` / `TriangleQuadrature` | Internal: conductivity, capacity, convection surface matrix, expansion load |
| `FeaGuards` | Internal: the element-Jacobian guard BOTH solvers ask, rather than each restating |
| `SurfaceSampler` | Internal: the display-mesh correspondence both results types publish through |

```csharp
var surface = Shape.Box(40, 30, 6)
    .Subtract(Shape.Cylinder(2.75, 20))
    .ToMesh();

var tets = TetMesher.Mesh(surface, new TetMeshOptions
{
    RefineQuality = true,
    RadiusEdgeRatio = 2.0,
    SizingField = p => 1.5 + 0.8 * Math.Max(0, boreField.Evaluate(p)),
}, out var report);

Console.WriteLine(report);                          // counts + volume residual
Console.WriteLine(TetQuality.Analyze(tets).ToText()); // dihedral + radius-edge histograms
var quadratic = QuadraticTetMesh.From(tets);          // 10-node elements for second-order FEA
```

## The pipeline

1. **Delaunay tetrahedralization** of the surface's vertices — incremental Bowyer–Watson,
   with every combinatorial decision made by `Predicates3d` (exact `Orient3d` for point
   location, exact `InSphere` for cavity membership). There is no epsilon in that file and
   no configuration that can make the topology wrong.
2. **Classification** — a tetrahedron is inside iff its centroid's winding number against the
   input surface exceeds ½ (`MeshWindingNumber`), which also names which body it fills.
3. **Boundary recovery** — the faces separating inside from outside *are* the mesh's skin;
   recovery is the loop that refines the surface until every one of them lies on the input
   surface, verified rather than assumed.
4. **Quality refinement** (optional) — circumcentre insertion bounded by a radius-edge target
   and/or a sizing field.

### Two decisions worth understanding

**Recovery works per planar PATCH, not per input triangle.** A Delaunay triangulation is free
to pick its own diagonal across a coplanar quad, and both diagonals are equally Delaunay when
the four corners are cocircular — which they are on every box. Demanding that the *input*
triangle appear as a face therefore cannot converge: every refinement of a cocircular
configuration is cocircular again. Measured, a unit cube exhausted a 500 000-point Steiner
budget. A patch (`SurfacePatches`: union-find over edge-adjacent, coplanar, equally-tagged
triangles) states the property that actually matters — the skin equals the input surface as a
point set — while leaving the triangulation free inside a flat region. A patch never straddles
two tags, so boundary conditions stay attributable.

**Classification comes BEFORE the boundary.** The obvious arrangement is the reverse: recover
the input triangles, then flood-fill between them. Beyond the diagonal problem above, an
exactly-coplanar quad makes the tetrahedralization contain a *flat* tetrahedron, whose four
faces are both diagonals at once — so "the faces lying in this patch" covers the patch twice
and an area-coverage test reads exactly 2.0000× and refines forever (measured: 40 of 72
patches on a 12×6 UV sphere). Deriving the boundary from a classification decided
independently has neither problem: a flat tetrahedron has no volume, is never kept, and its
two interior-facing faces fall out as the boundary with no tie to break.

## What kind of surface it wants — the v1 limitation

Boundary recovery is happy with **CAD tessellations**: B-Rep output, primitives, Surface Nets
fields, anything with structured triangle rows. Every fixture in the test suite recovers in
**zero rounds** — the input triangles are already faces of the Delaunay tetrahedralization.

It is **not** yet happy with **irregular remeshed surfaces**. An isotropic remesh
(`Remesher.Remesh`) produces near-uniform vertex spacing with no structure, and enough of its
triangles fail to be Delaunay faces that red subdivision does not clear them — measured, a
remeshed cylinder (at three parameter settings) and a remeshed sphere all exhaust the recovery
budget. The mesher **refuses by name** rather than returning a mesh whose boundary is quietly
not the input surface, and `RecoveryLimitationTests` pins that so the eventual fix is visible.

This is worth stating plainly because the intuitive advice is wrong: remeshing improves
element quality in principle, but v1 recovery wants exactly the structure a remesh removes.
Mesh the tessellation directly and use a sizing field to control element size. Lifting the
restriction is the top backlog item.

## Contracts

- **Orientation is an invariant, not a convention**: every tetrahedron satisfies
  `Predicates3d.SignedVolume6(a, b, c, d) > 0`, checked exactly at construction. `Volume` is
  therefore a sum of positive terms with no cancellation.
- **Surface fidelity**: boundary Steiner points are edge midpoints computed in double
  precision, so they lie on the input surface to round-off rather than exactly. The enclosed
  volume matches the input surface's to relative round-off — measured **1.8e-15 to 1.1e-13**
  across the benchmark cases (up to 234 335 elements), well inside the 1e-9 an FEA consumer
  needs. What refinement changes is the *faceting*: a boundary facet is a piece of an input
  triangle, and `TetFacet.SourceTriangle` names which one, so face tags survive refinement.
- **Determinism**: fixed Morton insertion order, deterministic walks, no RNG anywhere. Two
  runs on the same input produce bit-identical output including element order.
- **Refusals name what failed**: an open or inward-wound surface, an unrecoverable patch (with
  its area, coverage and input triangles), an exhausted Steiner budget (naming the phase and
  the option to raise), overlapping bodies. A half-refined mesh is a mesh whose quality report
  is a lie, so the mesher never truncates silently.

## Quality: two measures, because neither alone is honest

The **radius-edge ratio** is what Delaunay refinement can bound, and bounding it excludes
every badly shaped tetrahedron *except* the sliver — four nearly-coplanar vertices, whose
circumradius and shortest edge are both perfectly ordinary. A mesh can therefore have an
excellent radius-edge histogram and still be useless for FEA. The **minimum dihedral angle**
is what governs the stiffness matrix's conditioning and is the number that sees slivers.
`TetQualityReport` carries both, plus `SliverCount` for what the first measure cannot see.

`RadiusEdgeRatio` defaults to exactly **2.0** because that is the bound below which Delaunay
refinement is not guaranteed to terminate; smaller values are allowed, and the Steiner budget
is what catches them.

**Refinement is not a quality option on curved bodies — it is what makes the mesh usable at
all.** A tessellated sphere's vertices are *all exactly cospherical*, so a tetrahedralization
built from them alone has no interior vertices: every element spans the body and the result
is slivers by construction. Measured on a Ø20 UV sphere at 48×24 (win-x64, Release):

| | elements | mean min-dihedral | max radius-edge | slivers < 10° |
| --- | ---: | ---: | ---: | ---: |
| conforming only | 3 402 | 5.5° | 58.6 | 85.9% |
| `RefineQuality`, size 2.5 | 14 583 | 39.8° | 4.71 | 4.8% |

Throughput and quality with refinement, on a 20³ box (win-x64, i9-9900K, Release):

| target size | elements | ms | tets/s | mean min-dihedral | max radius-edge | slivers |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2.0 | 40 593 | 504 | 80 522 | 44.4° | 1.82 | 1.0% |
| 1.2 | 170 921 | 2 880 | 59 346 | 46.2° | 1.88 | 0.7% |
| 0.8 | 234 335 | 7 430 | 31 539 | 40.8° | 2.33 | 1.6% |

Boundary recovery costs **zero rounds** on every well-formed surface in the set, so the cost
is the Delaunay build plus classification. Run the numbers yourself with
`ENGRCAD_BENCH=1` and `--filter FullyQualifiedName~TetMesherBenchmark` in Release.

Dihedral angles are computed as `atan2(|n1 × n2|, n1 · n2)` on **raw** (unnormalized) face
normals — exact at any magnitude, no normalization and no epsilon anywhere. That is the same
measure `HoleFiller`'s minimum-weight triangulation uses, for the same reason: a normalized
form needs a degeneracy guard, and a degeneracy guard on a cross product is an *area*
threshold.

## Quadratic (10-node) elements

`QuadraticTetMesh.From(linear)` is a **pure function** of the linear mesh: nothing re-meshes,
moves a corner, or consults the original surface. Mid-edge nodes are exact midpoints keyed on
the canonical `(min, max)` corner pair, so two elements meeting on an edge get the *same*
node and the assembled system is continuous. Corner nodes keep their linear indices, so
corner-indexed data transfers with no mapping. Node order is the Abaqus C3D10 / VTK
`VTK_QUADRATIC_TETRA` convention. Elements are straight-sided (sub-parametric), which is what
every linear-elasticity formulation assumes — and makes `Volume` an exact identity against
the linear mesh.

## Numerical notes

- All topology rides on `EngrCAD.Core`'s `Predicates3d` (exact `Orient3d` / `InSphere`).
  Cospherical points — every structured CAD grid has them, all eight corners of a cube lie on
  one sphere — are a **tie**, not an error: the cavity test is strict, so such a point does
  not invalidate a tetrahedron, and determinism comes from the fixed insertion order instead.
- The enclosing simplex is deliberately huge (circumradius 2¹⁰ × the bounding radius, a power
  of two so the corners are reproducible bit-for-bit at any model scale). With inexact
  predicates that would be a conditioning disaster; with exact ones it costs only predicate
  escalations.
- Degeneracy guards are **relative** throughout (`1e-13 × extent³` for a tetrahedron's
  determinant, `1e-13 × extent²` for a triangle's area) — the scale-free tier. An absolute
  epsilon on a determinant is a *volume* threshold and fails cubically with model scale; that
  is the lesson `MeshDecimator` paid 91% of a volume for.
- `TetGeometry`'s numbers are **measurements**, not decisions: no combinatorial branch in the
  mesher reads one. That separation is what lets the quality report be approximate without
  ever making the mesh wrong.

# Structural analysis (linear static)

Small-strain isotropic linear elasticity on the meshes above, 3 displacement degrees of
freedom per node, on `EngrCAD.Core.Solvers`. Docs page: `docs/examples/fea-structural.md`.

```csharp
var surface = part.GetMesh();                                   // the display mesh IS the input
var tets = TetMesher.Mesh(surface, new TetMeshOptions { RefineQuality = true, MaxElementSize = 14 });

var model = new StructuralModel(AnalysisMesh.Quadratic(tets), Materials.Aluminium6061);
model.Fix(Facets.OnPlane(new Vector3d(-30, 0, 0), Vector3d.UnitX));
model.Force(Facets.OnPlane(new Vector3d(30, 0, 0), Vector3d.UnitX), new Vector3d(0, 0, -1200));

var results = StructuralSolver.Solve(model);
Console.WriteLine(results.Report.ToText());
foreach (var field in results.SampleOnto(surface))               // onto the DISPLAY mesh
    part.AddResult(field);
results.WriteVtu("bracket.vtu");                                 // or ParaView
```

## Elements

`AnalysisMesh` is the one type assembly, boundary conditions, load integration, stress
recovery and publishing are written against, so the linear/quadratic difference is two
integers rather than two implementations. It wraps a `TetMesh` or a `QuadraticTetMesh` and
copies nothing but index arrays.

- **Stiffness is assembled in index form, not as B'DB.** For an isotropic material the
  integrand collapses to `K_ij^ab = L·N_i,a·N_j,b + M·N_i,b·N_j,a + M·(gradN_i · gradN_j)·d_ab`,
  the same matrix at a fraction of the arithmetic and with the symmetry manifest. A test
  asserts it against an explicit `B' D B` written independently.
- **Quadrature is the cheapest exact rule**: one point for a linear element (whose B is
  constant), the four-point degree-2 rule for a quadratic one. That is exact *only* because
  a straight-sided 10-node tetrahedron has a constant Jacobian, which makes B linear and
  `B'DB` quadratic — and that claim is not taken on trust: `TetElementTests` asserts the
  stiffness is unchanged under an independent degree-3 rule, with a **negative control**
  that moves one mid-edge node off its midpoint and checks the two rules then disagree.
- `BodyForce` integrates with a **degree-5** rule instead, because a caller's field is not
  a polynomial of the element's making and under-integrating a load caps a convergence
  study at the quadrature's order rather than the element's.

Two consistent-load results are worth knowing before they look like bugs. A uniform
traction on a **6-node facet** puts exactly **zero** on the corners and A/3 on each
mid-edge node. A body load on a **10-node element** puts **−V/20** on each corner and V/5
on each mid-edge node. Both sum correctly (to A and to V), which is what makes
`Force(selector, total)` preserve a resultant exactly; both fall out of the quadrature
rather than being special-cased; both are pinned by tests.

## Boundary conditions

A condition names a **facet selector** (`Facets.Tag`/`Tags`/`OnPlane`/`FacingAlong`/
`InBox`/`All` + `And`/`Or`) and the tag is `TetFacet.SourceTriangle` — so supplying
`TetMeshOptions.FacetTags` makes a condition name a B-Rep face rather than a coordinate,
the same topological-naming story as the rest of EngrCAD. Supports are `Fix`/`FixNode`/
`Prescribe`/`PrescribeNode` with a per-axis `Dof` mask; loads are `Pressure` (positive
pushes *into* the body), `Traction`, `Force` (a total spread over the selection),
`Gravity`, `BodyForce` and `NodalForce`.

**A selector that matches nothing is refused at the call**, naming the tags that do exist.
A quietly ineffective support surfaces much later as a singular system, and the message
there cannot point at the typo.

## Solving

Supports are **eliminated, not penalised**: constrained degrees of freedom are removed
from the system rather than given a large diagonal, so the reduced matrix is genuinely
positive definite, its conditioning is the model's own, and a prescribed non-zero
displacement moves cleanly to the right-hand side as `f_free -= K_fc · u_c`. A penalty
stiffness would have to be chosen relative to the material, and choosing it wrong is
invisible in the answer.

**An unrestrained body is refused BEFORE the factorization, per connected body, with the
surviving motion described rather than counted.** The six rigid modes are built over each
component's own nodes, normalised, and restricted to the constrained degrees of freedom;
the null space of that restriction (from a Jacobi eigen-decomposition, floor 1e-12 on the
Gram's eigenvalues = a 1e-6 relative singular value, the sketch-constraint solver's rule)
IS the set of surviving motions, and each is unpacked back into a translation and a
located axis:

```
The model is not restrained: 3 rigid-body modes survive the supports
(rotation about the axis through (10, 7.5, 5) along (1, 0, 0); ...).
```

Per **body** because a fully fixed part beside a floating one is singular in a way no
whole-model rigid mode describes. Naming the modes needs the null space and not just the
rank: a first attempt reported "which candidate modes were not pivoted", which for a model
pinned at one node named three *translations* when the surviving motions were three
*rotations*. An axis is a line, so the quoted point is its closest approach to the body's
centroid — pin the centroid and you get the pinned node back, pin a corner and you get the
same lines through a different point on each.

Default is `SparseCholesky` with `SparseOrdering.Amd`.
`StructuralSolveOptions.EstimateCondition` adds a power/inverse-power estimate of the
condition number (measured to rise 4.42x when h halves, against the theoretical 4).

**AMD ordering is what decides whether a quadratic solve is practical at all** (win-x64,
i9-9900K, Release):

| | free DOF | natural nnz | natural ms | AMD nnz | AMD ms | fill | speed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| linear | 1 008 | 226 089 | 41 | 88 326 | 7 | 2.6x | 5.9x |
| linear | 3 960 | 2 411 921 | 742 | 995 878 | 168 | 2.4x | 4.4x |
| quadratic | 2 160 | 2 136 147 | 1 604 | 314 977 | 35 | 6.8x | 46x |
| quadratic | 6 552 | 19 383 964 | 64 834 | 1 723 544 | 340 | **11.3x** | **191x** |

**And the direct-vs-iterative verdict here is the opposite of the one Core measured on a
Laplacian** — which is exactly the caveat CLAUDE.md already records, that such a crossover
measures the operator's conditioning rather than the two algorithms. AMD-ordered Cholesky
against Jacobi-preconditioned CG at 1e-10, single right-hand side, the two **interleaved
in one sitting** (this machine returns absolute times several-fold apart across sittings —
only the within-sitting comparison means anything):

| | free DOF | direct | CG iterations | CG | winner |
| --- | ---: | ---: | ---: | ---: | :-- |
| linear | 2 160 | 247 ms | 308 | **122 ms** | CG 2.0x |
| linear | 6 552 | 1 791 ms | 471 | **461 ms** | CG 3.9x |
| linear | 14 688 | 10 754 ms | 634 | **705 ms** | CG 15.3x |
| linear | 46 800 | 108 459 ms | 956 | **2 232 ms** | **CG 48.6x** |
| quadratic | 2 160 | **45 ms** | 395 | 74 ms | direct 1.6x |
| quadratic | 6 552 | **308 ms** | 579 | 354 ms | direct 1.1x |
| quadratic | 14 688 | 2 688 ms | 770 | **1 094 ms** | CG 2.5x |

So CG wins everywhere with linear elements and past roughly 15 000 unknowns with
quadratic ones, and at the top of the table it is not close — this library's up-looking
Cholesky is unblocked, and 108 s is what a user experiences as "the solver hung".

**The direct solver is still the default, and the reason is exactness, not speed.** This
project's verification claims — the patch test reproduced to round-off, strain errors at
1e-13, the two element orders agreeing on strain energy to twelve digits — cannot be made
about an iterative solve stopped at a relative residual. A default of CG would make every
headline accuracy claim a statement about an opt-in path. It also reports its fill, which
is the diagnostic that says a mesh is bad.

Two honesty notes on that decision. The usual second argument for a direct solver — factor
once, solve many right-hand sides — **does not apply yet**: `Solve` factors and discards,
so a second load case pays for a second factorization; the multi-load-case entry point is
filed. And **no automatic size-based switch is offered**, deliberately, because a crossover
measured on one operator measures that operator: baking a threshold taken from one
cantilever into the library default would be the very mistake the row above documents.

Reach for `FeaSolveMethod.ConjugateGradient` for a large single solve — and the report
now says so itself. **`FeaSolveReport.Advisory`** (surfaced in `ToText()`) fires when a
direct factorization both took real time *and* dominated its own solve, stating what this
run spent where, citing the benchmark ratio at a named size and fixture, and naming the
trade:

```
note: the factorization took 104.8 s, 99% of this solve. On this project's cantilever
benchmark FeaSolveMethod.ConjugateGradient measured 48.6x faster than Direct at 46 800
free DOF (2.2 s against 108.5 s) and 15.3x at 14 688; this solve has 46 800. The trade is
an answer accurate to the iterative tolerance instead of exact, and no fill diagnostic.
```

Both conditions are about **what happened**, never about size alone: a system that factors
quickly stays silent however many unknowns it has. That is what keeps it from being a
disguised threshold — and it is the asymmetry that makes a heuristic acceptable here after
one was refused for the default. A wrong threshold in a default produces a worse answer; a
wrong threshold in an advisory produces a line of text nobody needed.

Whole-pipeline cost (Release), which says where the time actually goes:

| | elements | free DOF | assemble | factor | solve | stress | total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| linear | 3 072 | 2 160 | 10 ms | 32 ms | 0.8 ms | 6 ms | 53 ms |
| linear | 24 576 | 14 688 | 93 ms | 4 493 ms | 25 ms | 10 ms | 4.7 s |
| linear | 82 944 | 46 800 | 323 ms | 79 009 ms | 249 ms | 45 ms | 80 s |
| quadratic | 384 | 2 160 | 59 ms | 63 ms | 1.5 ms | 1 ms | 140 ms |
| quadratic | 3 072 | 14 688 | 364 ms | 3 299 ms | 19 ms | 6 ms | 3.8 s |

Note what this table is for: in **Debug** the same runs look assembly-dominated (1 822 ms
to assemble against 657 ms to factor on the docs bracket), which is the opposite
conclusion and would have sent the next optimization into the wrong phase. Benchmark in
Release, and re-measure where the time goes after every win.

## Results

`StructuralResults` carries nodal `Displacement` and `Reactions`, per-element
`ElementStress`/`ElementStrain`, averaged `NodalStress`/`NodalVonMises`, and
`PrincipalStress`. Nodal values are a **volume-weighted** average of the elements meeting
at a node (`NodalAveraging`), which is what a colour map wants and what converges — and it
also smooths a genuine discontinuity at a material interface or a re-entrant corner, which
is why the element values stay public: their jump is the standard error indicator, and
averaging it away is the standard way to hide a mesh that is too coarse. Quadratic stress
is evaluated **at** the nodes rather than extrapolated from the integration points;
superconvergent recovery is filed.

`SampleOnto(displayMesh)` closes the gap between a solver's vertex set and a display
mesh's. A display vertex whose position matches an analysis boundary node **bit for bit**
takes that node's value directly — which covers essentially every vertex in the normal
case, since the same mesh was fed to the mesher and its vertices survive verbatim
(measured: max sampling distance exactly 0.0) — and anything else falls back to the
closest point on the nearest boundary facet, interpolated with that facet's own shape
functions. The distance is reported, so a mismatched pairing exposes itself instead of
looking like an answer.

## Verification

A solver is worth what its verification is worth. All of these are in
`tests/EngrCAD.Fea.Tests`, on **structured** meshes (`StructuredTetMesh`, Kuhn's
subdivision) so a measured convergence order means something — see below for why the
Delaunay mesher is not the fixture for this.

**Element level.** Every element's stiffness annihilates all six rigid-body modes (a
rotation field is linear, so both element types represent it exactly and the residual is
pure round-off); the matrix is symmetric and positive semi-definite; the index form equals
an independently written `B'DB`.

**Patch tests** — a constant-strain state reproduced exactly, to round-off:

| | Linear | Quadratic |
| --- | ---: | ---: |
| Displacement patch, relative displacement error | 2.4e-15 | 1.8e-14 |
| Displacement patch, relative strain error | 9.1e-14 | 1.5e-13 |
| Traction patch, stress error against 25 MPa | 3.9e-12 | 1.1e-11 |
| Traction patch, relative displacement error | 4.0e-14 | 2.5e-13 |

The two orders also return **the same strain energy to twelve digits** (3.17847) wherever
both reproduce the field exactly.

**Convergence order**, by the method of manufactured solutions (cubic displacement field,
body force derived analytically, Dirichlet everywhere so there is no singularity to limit
the rate):

| | L2 measured | theory | energy measured | theory |
| --- | ---: | ---: | ---: | ---: |
| Linear | **2.00** | 2 | **1.00** | 1 |
| Quadratic | **3.03** | 3 | **2.02** | 2 |

**Cantilever**, 100 × 10 × 10 mm, 1000 N tip load, steel:

| | h | elements | tip (mm) | error vs the FE limit |
| --- | ---: | ---: | ---: | ---: |
| linear | 5.00 | 192 | 0.55157 | −71.0% |
| linear | 2.50 | 1 536 | 1.15880 | −39.2% |
| linear | 1.25 | 12 288 | 1.63297 | −14.3% |
| quadratic | 10.00 | 24 | 1.81094 | −4.9% |
| quadratic | 5.00 | 192 | 1.87899 | −1.4% |
| quadratic | 2.50 | 1 536 | **1.89778** | **−0.4%** |

Euler–Bernoulli `PL³/3EI` = **1.90476 mm**; Timoshenko (k = 5/6) = **1.91962 mm**; the
Richardson-extrapolated finite-element limit is **1.90494 mm**, i.e. **+0.01% from
Euler–Bernoulli and −0.76% from Timoshenko**. That near-perfect agreement with the
*simpler* model is two effects cancelling and is reported as such: the 3D solve includes
shear deformation (softening it towards Timoshenko) while the built-in end is a genuine
three-dimensional clamp (stiffening it back). Note also how stiff linear tetrahedra are in
bending — 14% low at twelve thousand elements against 0.4% for quadratic at an eighth as
many. **The order measured on this fixture is 1.86, not 3**, because the clamped edge
carries a stress singularity; that is why the order table above uses a manufactured
solution instead.

**Stress concentration**, 120 × 40 × 2 mm plate, Ø10 central hole (d/W = 0.25), plane
strain imposed exactly by fixing z at every node:

| | elements | K_tn | vs Howland | mesh spread |
| --- | ---: | ---: | ---: | ---: |
| linear | 768 | 1.8352 | −24.6% | 9.50% |
| linear | 3 072 | 2.1941 | −9.8% | 5.08% |
| linear | 12 288 | 2.3566 | −3.1% | 2.27% |
| quadratic | 768 | 2.3206 | −4.6% | 3.54% |
| quadratic | 3 072 | **2.4216** | **−0.4%** | 0.60% |

Kirsch's infinite-plate K_t = 3 is the wrong number for a real plate: **the finite-width
correction is Howland's exact strip solution in Peterson's polynomial fit** (Chart 4.1),
against the NET section, `K_tn = 2 + 0.284L − 0.600L² + 1.32L³` with `L = 1 − d/W`, giving
**2.4324** at d/W = 0.25 — equivalently 3.2432 on the gross section, nearly 8% above 3.
Far-field stress is recovered to 0.03% on the finest quadratic mesh and 0.37% on the
coarsest linear one, which is what makes every K_t above mean anything.

The "mesh spread" column is a finding worth keeping. Theory says all four peak nodes (two
angles by two thickness layers) read identically; the y-reflection is exact to the last
bit while the **z-reflection is not**, because Kuhn's subdivision picks its diagonals by
logical index order and no reflection preserves that. The spread is therefore a *direct
measurement* of the discretization error rather than an estimate of it, and the location
claim ("the peak is on the perpendicular diameter") is held to exactly that bar — a
self-calibrating threshold, since a claim about where a peak is cannot be sharper than the
discretization measuring it.

**Rigid body and equilibrium**, all to round-off: a rigid motion prescribed on the
boundary produces a rigid motion inside (strain at 1e-14 of the field's own scale, energy
at 1e-15 of a comparable straining motion's); a self-equilibrated load gives identical
strains (4.6e-13 relative) and identical strain energy under two different 3-2-1
restraints; the answer is **frame-indifferent** (strain energy and peak von Mises agree to
1e-12 when the whole model, its loads and its supports are rigidly placed elsewhere);
gravity reacts the exact weight; a uniform pressure over a closed surface has no
resultant; a total force distributed over a face set sums back to itself.

## What the verification cost, and why the fixtures are structured

**The Delaunay mesher's slivers are the binding constraint on a structural solve, and they
are a mesher problem, not a solver one.** Measured on a 100 × 10 × 10 beam at a 5.0 size
target: 31 214 elements of which **9 954 (32%) are slivers below 10°**, minimum dihedral
0.000°, minimum element volume 5e-17, and **two elements whose exact signed volume is
strictly positive while their double-precision volume is exactly 0.0**. A stiffness matrix
assembled from that is not numerically positive definite and the factorization fails. The
same mesher on a 20³ box gives 1% slivers and behaves perfectly, so the trigger is
elongation. Verification fixtures are therefore structured (Kuhn's subdivision of a grid),
which also gives exactly geometrically similar refinement sequences — the thing a measured
order needs.

The solver **refuses** a non-positive Jacobian by name rather than absorbing it, and the
test that pins it uses the four corner coordinates of a real offending tetrahedron,
verbatim, so the guard cannot rot while the mesher changes.

**The guard has to ask the assembly's own arithmetic, not restate it.** The first version
tested the corner triple product `(b−a)×(c−a)·(d−a)`, which is the same mathematical
quantity the isoparametric Jacobian is but different arithmetic — and it disagreed in the
last bits, so elements passed the guard and were then integrated as exactly zero. That
surfaced as a 10-node mid-edge node with *no stiffness at all*, on an element whose corner
volume read a healthy 1.2e-15. It is the "one shared rule only holds if every caller asks
it" lesson from the tessellator, in a solver.

One more measurement from the same work: a **two-material bar in series** elongates 0.769%
less than the 1D formula `σ(L/2)(1/E₁ + 1/E₂)` predicts, because the two halves want
different Poisson contractions and the interface constrains them — correct physics,
reported rather than tuned away.

Run the throughput and ordering tables yourself with `ENGRCAD_BENCH=1` and
`--filter FullyQualifiedName~FeaBenchmark` in Release.

---

# Thermal analysis (conduction)

Fourier conduction `q = -k·grad T` on the same meshes, steady or transient, with **one
temperature degree of freedom per node** instead of three displacements. Docs page:
[`docs/examples/fea-thermal.md`](../../docs/examples/fea-thermal.md).

`ThermalModel` → `ThermalSolver.Solve` / `SolveTransient` → `ThermalResults` /
`ThermalTransientResults`, mirroring the structural triple deliberately: the same
`AnalysisMesh`, the same `Facets` selectors, the same builder shape, the same
refuse-an-empty-selection-at-the-call rule, the same `MeshField` and `.vtu` publishing.

| Structural | Thermal |
| --- | --- |
| `Fix` / `Prescribe` | `Temperature(selector, T)` |
| `Pressure` | `HeatFlux(selector, q)` — positive flows IN |
| `Force` | `HeatLoad(selector, Q)` — exact resultant |
| `Gravity` / `BodyForce` | `Generation(rate)` / `Generation(p => rate)` |
| `NodalForce` | `NodalHeat(node, Q)` |
| — | `Convection(selector, h, Tinf)` |

## What is genuinely different, and it is only three things

**Convection contributes to the MATRIX as well as the load vector.** `q = h(T - Tinf)` has a
term proportional to the unknown, so it is the one condition not pre-reduced to nodal heat
when it is added — its two halves are meaningless apart, and keeping them as one stored
condition makes applying one without the other impossible. It is also what lets a model with
no held temperature be solvable, because the surface matrix is strictly positive on
constants.

**The capacity matrix needs a quadrature rule two degrees ABOVE the conductivity's**, and
getting that wrong is silent. A conductivity integrates `grad N · grad N`, degree `2(p-1)`,
the same as a stiffness; a capacity integrates `N·N`, degree `2p`. Use the conductivity's
one-point rule for a 4-node capacity and every entry comes out `rho·c·V/16` — a **rank-one,
singular** matrix whose **total is still exactly `rho·c·V`**, so the obvious sanity check
passes it. `ThermalElementTests` measures precisely that (spread exactly 0), which is what
makes the negative control worth having.

**The undriven refusal is simpler and sharper than the structural one.** Conduction's null
space on a connected body is *exactly* the constants — add any constant to T and every
gradient, hence every flux and every boundary condition, is unchanged — so the check is a
boolean per connected body (a prescribed temperature or a convective facet anywhere?) where
the structural check needs a Jacobi eigen-decomposition to find which of six rigid modes
partly survive. A pure heat flux does not count; it is Neumann and says nothing about the
level. A transient of an insulated body is *not* refused, because the capacity term is
positive definite on its own.

## Time integration

A theta scheme, `(C/dt + theta·K)·T_next = (C/dt - (1-theta)·K)·T_now + f`, at a **constant**
step — which is what lets **one factorization serve the whole run**. That is the "factor
once, solve many right-hand sides" case `FeaSolveMethod.Direct` records as not yet arising
structurally; here it does, so the exact default is also the fast one.

**Backward Euler is the default, for L-stability rather than accuracy.** Both schemes are
A-stable, but a conduction system's fastest mode is roughly `alpha/h²` and Crank–Nicolson's
amplification factor for it approaches **−1**: those modes alternate in sign instead of
decaying. Measured on a quenched 40 mm bar with 1 mm elements, where `h²/alpha = 0.1 s` and a
backward step in temperature is numerical by definition:

| `dt` | `lambda·dt` | Backward Euler | Crank–Nicolson |
| ---: | ---: | --- | --- |
| 2.0 s | 20 | **0 backward moves** | 190 moves, worst **105.9%** of the step (64.8% after step 5) |
| 1.0 s | 10 | 0 backward moves | 143 moves, worst 81.5% (42.6% after step 5) |
| 0.5 s | 5 | 0 backward moves | 242 moves, worst 56.3% (24.0% after step 5) |

**And the honest counterweight, because this is easy to over-claim**: at a *short* step both
schemes move backwards, and that is the **consistent capacity matrix**, not the time
integration. At `dt = 0.005 s` backward Euler undershoots by 5.8% and Crank–Nicolson by 7.1%.
"Backward Euler is monotone" is a statement about a *lumped* capacity.

**The capacity is consistent, and lumping is filed rather than half-built.** A lumped
capacity would buy monotonicity under backward Euler and the possibility of explicit
stepping, and would cost accuracy. But the obvious way to get one is unavailable: row-sum
lumping sets `C_ii = rho·c·integral(N_i dV)`, which for a 10-node tetrahedron is **−V/20 at
every corner** — a negative heat capacity, the same surprising integral that already governs
a quadratic element's gravity load. Any quadratic lumping has to be a scaled-diagonal scheme,
which is a different approximation with its own error.

**A boundary condition wins over the initial condition at t = 0.** "The surface is suddenly
held at Ts" means Ts for every t > 0, and the value at the single instant t = 0 is what the
erfc solution is derived under. The alternative — letting the initial value stand and
transitioning inside the first step — was built and measured: it charges the surface node's
whole heat-up to that step, and the consistent capacity drags its neighbours down with it, so
a quenched bar undershot its own initial temperature by **49% of the step** against 5.8%.

## Thermal stress

`StructuralModel.ThermalLoad(nodalTemperature | ThermalResults, reference)` applies
`eps0 = alpha·dT` as an initial strain. **Two halves, and forgetting the second is the
classic error**: the load is `integral(B'·D·eps0)`, which for an isotropic material collapses
to `E/(1-2·nu)·alpha·dT` times a shape-function gradient; and the stress recovery must then
use `sigma = D(eps - eps0)`. Without the subtraction a freely expanding bar reports
`E·alpha·dT` — 126 MPa for steel at 50 K — under no load at all. Both come from one stored
field, so applying the load is what arms the subtraction.

The load is **self-equilibrated by construction** (the shape functions are a partition of
unity, so their gradients sum to exactly zero), which is why a thermal load adds nothing to
the applied resultant and the equilibrium check survives a coupled solve. The two models must
share the same `AnalysisMesh` **instance**, checked rather than assumed, because a temperature
field crosses by node index.

## Verification

Full tables are on the [docs page](../../docs/examples/fea-thermal.md). The headline numbers:

- **Patch test** exact to 2.4e-16 (linear) and 7.3e-16 (quadratic), flux to 2.3e-15/8.0e-15.
- **1D slab** vs the linear profile 1.4e-15/3.8e-15; **with generation** nodally exact for
  both orders with a measured interior ratio of **4.00**; **convective boundary** vs the
  mixed-BC solution 9.0e-14/3.9e-13 and the convected heat to 2.6e-15.
- **Hollow cylinder** vs the logarithmic profile: 1.0e-3 (linear) and 5.8e-4 (quadratic)
  relative at nRadial 8.
- **Convergence order** (manufactured, quartic): **2.01 / 1.00** linear and **3.05 / 2.02**
  quadratic in L2 / energy, against theory 2/1 and 3/2.
- **Transient**: lumped capacitance to 3.0e-4 of the initial excess at Bi = 2.1e-3;
  semi-infinite erfc to 0.118 K on an 80 K step; time order **1.05** and **2.00**; the
  whole-run first law at 1.3e-14 … 8.5e-13.
- **Coupling**: free bar expands to 6.9e-15 with stress at 8.5e-15 of `E·alpha·dT`;
  constrained bar carries `−E·alpha·dT` to 2.0e-15 with the lateral stresses vanishing.

### Four traps the verification found, all in the tests rather than the solver

**A fixture can make a convergence test measure nothing.** Spacing the hollow cylinder's
rings *geometrically* puts the nodes at equal intervals of `ln r` **and** makes every
ring-to-ring conductance equal, so the exact nodal values satisfy the discrete equations
identically — 4.3e-14 on a 120 K drop at every refinement. The study duly reported convergence
orders of **−2.50 and −1.27**, which is what a ratio of two round-off figures looks like when
mistaken for a signal. Uniform spacing restores a genuine error.

**A cubic manufactured solution is nodally exact on a uniform mesh.** The finite-element
stencil's truncation error is proportional to a fourth derivative, which a cubic does not
have, so both element orders reproduce it at the nodes to round-off (2.2e-13 on a field of
scale 113) and report no order at all. The field is quartic now.

**One-dimensional nodal superconvergence hides in the slab-with-generation case.** Both
orders are exact at the nodes, for two different reasons — a parabola is *in* the quadratic
space, and the linear discrete equations reduce to a central difference a quadratic satisfies
exactly. The first version asserted a refinement ratio of 4 and measured 0.72. The genuine
O(h²) is inside the elements.

**A radial fixture's measured order is capped by its own polygonal domain**, and that is
measured rather than assumed: refining the angular direction as `n²` instead of `n` lifts the
quadratic order from 2.00 to 2.28 and its finest error from 5.8e-4 to 1.1e-4, while leaving
the linear sequence unchanged at 1.28. The two orders are limited by different things. It is
the cantilever's clamped-end lesson in a new shape, and it is why the convergence ORDER is
measured against a manufactured solution on a box.

### And three the solver was wrong about

**The energy balance must include the capacity term at prescribed nodes.** The residual
`r = C·dT/dt + K·T - f` is what the equations say a boundary injects; a first version summed
only `K·T - f`, right for a steady solve and wrong for a transient, and the whole-run first
law came out **4.4e-2** while every temperature in the field was correct.

**The transient right-hand side cannot assume the previous step's prescribed values.**
Collapsing `(B_fc - A_fc)·T_c` to `-K_fc·T_c` is valid only when the prescribed values never
change, which is false on the first step of a step change. Carrying the previous state whole
costs one extra matvec per step and makes time-varying boundary values a one-line change.

**A linear temperature field's stress-free displacement is QUADRATIC.** `u_x` needs a
`-(b/2)(y² + z²)` term to cancel a shear that `u_y = (a + b·x)y` otherwise introduces — so
holding whole symmetry planes over-constrains free expansion and the model carries stress from
the restraints alone (measured 67 MPa, a quarter of `E·alpha·dT`). A 3-2-1 restraint gives
1e-10.

## Limitations

- **Sliver elements** (above) — the mesher's top quality gap, and the reason long thin
  bodies do not yet solve. Compact ones do. The same shared guard (`FeaGuards`) refuses them
  for both physics, asking the assembly's own Jacobian in both cases.
- **Element-associated results have nowhere to go**: `MeshField` is vertex-only in v1, so
  a per-element stress or flux cannot be exported or displayed, only read in code.
- Quadratic stress is evaluated at the nodes rather than recovered from the
  superconvergent integration points.
- Materials are **isotropic** only, thermally as well as structurally: one `k`, not a
  conductivity tensor. Orthotropic and anisotropic laws need a full 6×6 constitutive matrix
  with a material frame; the assembly is already written against `Lambda`/`Mu` and would
  need the general form.
- **Material properties are constant**, so conduction stays linear and the solve is one
  factorization. Temperature-dependent `k` or `c`, and radiation
  (`sigma·epsilon·(T⁴ - Tsurr⁴)`), are both nonlinear in the unknown and need an outer
  iteration wrapping this solver.
- **Time-varying boundary conditions are not exposed**, though the stepping is written for
  them. The step is constant, deliberately.
- Coupling is **one-way**: temperature drives stress, and deformation does not feed back into
  conduction. Two-way coupling is a different (staggered or monolithic) solver.
- No contact, plasticity, large deformation, or modal analysis. Modal is the nearest —
  the same assembly plus a mass matrix and a generalized eigen-solver — and is filed
  rather than started, because a static solver that is verified is worth more than two
  that are not.
