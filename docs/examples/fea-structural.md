# Structural analysis (linear static)

`EngrCAD.Fea` solves **small-strain linear elasticity** on the tetrahedral meshes
[the mesher](fea-meshing.md) produces: 4-node or 10-node elements, boundary conditions
named through the same facet tags the mesher carries, a sparse symmetric solve on
`EngrCAD.Core.Solvers`, and results published as [fields](fields.md) that the viewer's
colour map and deformed-shape overlay pick up with no extra wiring.

The whole pipeline is five statements: mesh the surface, wrap it in a model, say what is
held and what pushes, solve, publish.

```csharp render:fea-structural-bracket
var bracket = Shape.Box(60, 40, 10)
    .Subtract(Shape.Cylinder(6, 40).Translate(0, 0, -20));
var part = new Part("bracket", bracket);

// Meshing the PART's display mesh means the results land back on it exactly:
// every display vertex is an analysis boundary node, matched by value.
var surface = part.GetMesh();
var tets = TetMesher.Mesh(surface, new TetMeshOptions
{
    RefineQuality = true,
    MaxElementSize = 14,     // deliberately coarse - this page is a picture, not a study
});

var model = new StructuralModel(AnalysisMesh.Quadratic(tets), Materials.Aluminium6061);
model.Fix(Facets.OnPlane(new Vector3d(-30, 0, 0), Vector3d.UnitX));
model.Force(Facets.OnPlane(new Vector3d(30, 0, 0), Vector3d.UnitX), new Vector3d(0, 0, -1200));

var results = StructuralSolver.Solve(model);

foreach (var field in results.SampleOnto(surface))
    part.AddResult(field);
part.FieldDisplay = new FieldDisplay
{
    Field = StructuralResults.FieldNames.VonMises,
    Deform = StructuralResults.FieldNames.Displacement,
    DeformScale = 60,
};

var scene = new Scene();
scene.Add(part);
```

![A bracket coloured by von Mises stress, drawn deformed](images/fea-structural-bracket.png)

The bracket is fixed on its left face and pulled down by 1200 N on its right. The shape
drawn is the **deformed** one at 60x exaggeration, ghosted over the original, and the
colours are von Mises stress in MPa. The maximum is on the bore wall — measured, the peak
node sits at radius 6.000 on a radius-6 hole — which is the classic stress concentration
rather than the built-in edge an eye goes to first.

> [!NOTE]
> That mesh is coarse on purpose, so this page builds in about four seconds. Its
> displacement is converged to under a percent but its **peak stress is not** — a
> concentration needs a much finer mesh at the feature. The verification numbers below are
> from the test suite, not from this picture.

## Units

Nothing in this kernel carries a unit, so a material is only meaningful against a length
unit you choose. `Materials` is stated in the **mm / N / MPa / tonne** system, which is
what the rest of EngrCAD assumes:

| Quantity | Unit | Example |
| --- | --- | --- |
| Length, displacement | mm | the model's own coordinates |
| Force | N | `Force(..., new Vector3d(0, 0, -1200))` |
| Stress, Young's modulus | MPa = N/mm² | steel E = 210 000 |
| Density | tonne/mm³ | steel 7850 kg/m³ = 7.85e-9 |
| Acceleration | mm/s² | `Materials.GravityMillimetres` = 9806.65 |

SI (m / N / Pa / kg) works identically. What does not work is mixing the two, and no check
can catch that — which is why the system is documented rather than enforced.

## Saying what is held and what pushes

A boundary condition names a **facet selector**. Tags are the durable handle: pass B-Rep
face ids through `TetMeshOptions.FacetTags` and a condition names a face rather than a
coordinate, the way the rest of EngrCAD's [selection vocabulary](selection.md) works. The
geometric selectors are for meshes that carry no tags — an imported STL, or a quick script.

| Selector | Picks |
| --- | --- |
| `Facets.Tag(id)` / `Facets.Tags(...)` | Facets carrying those tags |
| `Facets.OnPlane(point, normal)` | Facets lying in a plane (centroid on it **and** normal parallel to it) |
| `Facets.FacingAlong(direction, angle)` | "The top surface", "the loaded flank" |
| `Facets.InBox(aabb)` | Facets whose centroid is inside a box |
| `Facets.All`, `Facets.And`, `Facets.Or` | Everything, and the combinators |

| Condition | Meaning |
| --- | --- |
| `Fix(selector, dofs)` | A support. `Dof.All` clamps; `Dof.Z` alone is a roller |
| `FixNode(node, dofs)` | One node — how a statically determinate 3-2-1 restraint is built |
| `Prescribe(selector, displacement, dofs)` | An enforced deflection; the reaction is what a support at that displacement carries |
| `Pressure(selector, p)` | Uniform pressure. **Positive pushes INTO the body** |
| `Traction(selector, vector)` | Uniform force per unit area |
| `Force(selector, total)` | A total force spread over the selection — the resultant is exact |
| `Gravity(acceleration)` | Body load from the elements' own material densities |
| `BodyForce(p => vector)` | A general body force per unit volume |
| `NodalForce(node, force)` | A point load (whose local stress does not converge — see below) |

A selector that matches **nothing** is refused where it was written, naming the tags that
do exist. A quietly ineffective support surfaces much later as a singular system, and the
message there cannot point at the typo.

## Reading the answer

```csharp run:fea-structural-report
var plate = Shape.Box(50, 30, 8);
var part = new Part("plate", plate);
var surface = part.GetMesh();
var tets = TetMesher.Mesh(surface, new TetMeshOptions { RefineQuality = true, MaxElementSize = 12 });

var model = new StructuralModel(AnalysisMesh.Of(tets), Materials.Steel);
model.Fix(Facets.OnPlane(new Vector3d(-25, 0, 0), Vector3d.UnitX));
model.Force(Facets.OnPlane(new Vector3d(25, 0, 0), Vector3d.UnitX), new Vector3d(0, 0, -800));
model.Gravity(Materials.GravityMillimetres);

var results = StructuralSolver.Solve(model);
Console.WriteLine(results.Report.ToText());
Console.WriteLine($"peak von Mises {results.MaxVonMises:F2} MPa at node {results.MaxVonMisesNode}");
Console.WriteLine($"largest displacement {results.MaxDisplacement:F4} mm");

// Global force equilibrium must hold to round-off for ANY correct model - it is the
// cheapest end-to-end check there is, and nothing about it is visible in a stress plot.
if (results.Report.EquilibriumResidual > 1e-9)
    throw new Exception($"equilibrium residual {results.Report.EquilibriumResidual:E3}");

// ParaView, for anything the viewer does not show.
results.WriteVtu(Path.Combine(Path.GetTempPath(), "plate.vtu"));
```

`FeaSolveReport` is a value, not a log line: sizes, factor fill, timings per phase, the
solve residual, strain energy, the applied and reaction resultants, and the equilibrium
check. `StructuralSolveOptions.EstimateCondition` adds a power-iteration estimate of the
stiffness matrix's condition number — the number that tells a badly shaped mesh from a
badly posed model.

Beyond the report, `StructuralResults` gives nodal `Displacement`, averaged `NodalStress`
and `NodalVonMises`, `PrincipalStress`, per-element `ElementStress`/`ElementStrain`, and
`Reactions`. **Element values are kept public on purpose**: the nodal values are a
volume-weighted average of the elements meeting at a node, which is what a colour map
wants and what converges — and it also smooths a genuine discontinuity at a material
interface or a re-entrant corner. The jump between neighbouring elements is the standard
error indicator, and averaging is the standard way to hide a mesh that is too coarse.

## Elements

| | Linear (4-node) | Quadratic (10-node) |
| --- | --- | --- |
| Built by | `AnalysisMesh.Of(tets)` | `AnalysisMesh.Quadratic(tets)` |
| Strain within an element | constant | linear |
| Integration | 1 point (exact) | 4 points, degree 2 (exact for straight-sided elements) |
| Nodes | the mesh's vertices | vertices + one per edge, about 7-8x as many |
| Displacement convergence | O(h²) | O(h³) |
| Energy convergence | O(h) | O(h²) |

Quadratic elements are worth their cost for anything involving bending or a stress
concentration, and the tables below say by how much. `QuadraticTetMesh` is a pure function
of the linear mesh — mid-edge nodes at exact midpoints, so elements stay straight-sided,
which is what makes the 4-point rule exact.

## Solving

The default is sparse Cholesky with an **AMD fill-reducing ordering**, and that ordering is
not a micro-optimisation. On a 6 552-DOF 10-node cantilever it cut the factor from 19.4
million entries to 1.7 million and the factor time from 64.8 s to 0.34 s — **11.3x less
fill and 191x faster** — because fill is what decides whether a quadratic solve is
practical at all.

`FeaSolveMethod.ConjugateGradient` is the alternative, and the honest measurement is that
**it often wins here**: on 3D elasticity over tetrahedra, Jacobi-preconditioned CG beat
the AMD-ordered direct solve in three of four benchmark cases at a few thousand unknowns
(37 ms against 65, 153 against 524, 334 against 342), and this library's unblocked
Cholesky takes 79 s to factor a 46 800-DOF system. That is the opposite of the crossover
measured on a Laplacian, which is the point: such a comparison measures the *operator's
conditioning*, not the two algorithms. The direct solver keeps the default because it is
exact, deterministic, reports its fill, and amortises across load cases at a few
milliseconds per extra right-hand side — reach for CG for one large solve.

Supports are **eliminated, not penalised**: constrained degrees of freedom are removed from
the system rather than given a large diagonal, so the reduced matrix is genuinely positive
definite and its conditioning is the model's own.

## Refusals

An unrestrained body is refused **before** the factorization, per connected body, with the
surviving motion described rather than counted:

```
The model is not restrained: 3 rigid-body modes survive the supports
(rotation about the axis through (10, 7.5, 5) along (1, 0, 0); ...).
1 node is restrained in all. A linear static solve of an unrestrained body has no
unique answer; add supports, or restrain the six rigid-body degrees of freedom
statically (a 3-2-1 scheme) if the loads are self-equilibrated.
```

The check is per **connected body**, because a fully fixed part beside a floating one is
singular in a way no whole-model rigid mode describes. Letting the factorization discover
it instead gives "nonpositive pivot at column 4713", which tells nobody anything.

Also refused by name: a selector matching no facets; a model with every degree of freedom
restrained; an element whose Jacobian is non-positive in double precision (which a sliver
can be while the exact predicate still calls it valid — see the limitations below); and a
Poisson's ratio at or past 0.5, where the material is incompressible and a
displacement-based element cannot represent it.

## Verification

A solver is worth exactly what its verification is worth, so here is all of it. Every
number is from `tests/EngrCAD.Fea.Tests`, on structured meshes so that a measured
convergence order means something.

**Patch tests** — a constant-strain state must be reproduced *exactly*, to round-off, by
both element types. This is the standard correctness gate and it catches essentially every
assembly, indexing and Jacobian error outright.

| Test | Linear | Quadratic |
| --- | --- | --- |
| Displacement patch: relative displacement error | 2.4e-15 | 1.8e-14 |
| Displacement patch: relative strain error | 9.1e-14 | 1.5e-13 |
| Traction patch: stress error against 25 MPa | 3.9e-12 | 1.1e-11 |
| Traction patch: relative displacement error | 4.0e-14 | 2.5e-13 |

**Convergence order** (manufactured solution, cubic displacement field, Dirichlet
everywhere — no singularity for the rate to be limited by):

| | L2 order measured | theory | energy order measured | theory |
| --- | ---: | ---: | ---: | ---: |
| Linear | **2.00** | 2 | **1.00** | 1 |
| Quadratic | **3.03** | 3 | **2.02** | 2 |

**Cantilever tip deflection**, 100 x 10 x 10 mm, 1000 N tip load, steel:

| | h | elements | tip (mm) | error |
| --- | ---: | ---: | ---: | ---: |
| linear | 5.00 | 192 | 0.55157 | -71.0% |
| linear | 2.50 | 1 536 | 1.15880 | -39.2% |
| linear | 1.25 | 12 288 | 1.63297 | -14.3% |
| quadratic | 10.00 | 24 | 1.81094 | -4.9% |
| quadratic | 5.00 | 192 | 1.87899 | -1.4% |
| quadratic | 2.50 | 1 536 | **1.89778** | **-0.4%** |

Euler-Bernoulli `PL³/3EI` gives **1.90476 mm**; Timoshenko, adding the shear term
`PL/(kGA)` with k = 5/6, gives **1.91962 mm**. The extrapolated finite-element limit is
**1.90494 mm** — **+0.01% from Euler-Bernoulli and -0.76% from Timoshenko**.

That near-perfect agreement with the *simpler* beam model is a coincidence of two effects
cancelling, and saying so is the point: a 3D solve includes shear deformation, which
softens it towards Timoshenko, while the built-in end here is a genuine three-dimensional
clamp — every displacement zero over that whole face — which suppresses the Poisson
contraction and warping beam theory allows and stiffens it back. Note also that linear
tetrahedra are **very** stiff in bending: 14% low with twelve thousand elements, against
0.4% for quadratic elements with an eighth as many.

**Stress concentration**, a 120 x 40 x 2 mm plate with a Ø10 central hole (d/W = 0.25) in
plane strain:

| | elements | K_tn measured | vs Howland |
| --- | ---: | ---: | ---: |
| linear | 768 | 1.8352 | -24.6% |
| linear | 3 072 | 2.1941 | -9.8% |
| linear | 12 288 | 2.3566 | -3.1% |
| quadratic | 768 | 2.3206 | -4.6% |
| quadratic | 3 072 | **2.4216** | **-0.4%** |

Kirsch's classical answer for an *infinite* plate is K_t = 3. **The finite-width
correction matters and has to be stated**: Howland's exact strip solution in Peterson's
polynomial fit (Chart 4.1), against the **net** section stress, is
`K_tn = 2 + 0.284L - 0.600L² + 1.32L³` with `L = 1 - d/W`, which at d/W = 0.25 gives
**2.4324** — equivalently 3.2432 against the gross section, nearly 8% above the textbook 3.
The far-field stress is recovered to 0.03% on the finest quadratic mesh and 0.37% on the
coarsest linear one, which is the check that makes every K_t above mean anything.

**Rigid-body and equilibrium** properties, all satisfied to round-off:

- Every element's stiffness annihilates all six rigid-body modes (both orders).
- A rigid motion prescribed on the boundary produces a rigid motion inside, with strain at
  1e-14 of the field's own scale and energy at 1e-15 of a comparable straining motion's.
- A self-equilibrated load gives **identical strains** (4.6e-13 relative) and identical
  strain energy under two different statically determinate 3-2-1 restraints, with both
  reactions at 1e-13 of the applied load.
- The whole answer is **frame-indifferent**: strain energy and peak von Mises agree to
  1e-12 when the model, its loads and its supports are rigidly placed somewhere else.
- Gravity reacts the body's exact weight; a uniform pressure over a closed surface has no
  resultant; a total force distributed over a face set sums back to itself exactly.
- The two element orders agree on strain energy to twelve digits wherever both reproduce
  the field exactly.

## Limitations

- **Sliver elements are the real constraint, and they belong to the mesher.** The
  Delaunay mesher's own README names sliver removal as its top quality gap, and a
  structural solve is where it bites: measured on a 100 x 10 x 10 beam at a 5.0 size
  target, 32% of the elements are slivers below 10°, two have a non-positive
  double-precision volume, and the factorization fails outright. The solver refuses those
  by name rather than absorbing them. Compact bodies mesh well; long thin ones do not yet.
- **Element-associated results have nowhere to go.** `MeshField` is vertex-only in v1, so
  a per-element stress cannot be exported or displayed directly; `ElementStress` is
  available in code.
- Stress at a quadratic element's nodes is evaluated directly rather than extrapolated
  from the integration points. Superconvergent recovery is the standard refinement and is
  filed.
- No contact, no plasticity, no large deformation, no modal analysis. Every one of those
  is a different mathematical problem rather than a bigger version of this one; modal
  analysis is the nearest, needing a mass matrix and an eigen-solver on the same assembly.
