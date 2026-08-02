# EngrCAD.Fea

Simulation: **tetrahedral meshing**, **linear-static structural analysis**, **heat
conduction** (steady and transient, with thermal-expansion coupling back into the structural
solve), **modal analysis** (natural frequencies and mode shapes, with or without stress
stiffening), **linear buckling** (critical load factors from a prior static solve's stress
field), **frequency response** (steady-state harmonic sweeps by modal superposition, with
Rayleigh or per-mode damping) and **fatigue post-processing** (S-N life and Goodman/Gerber
safety factors over two static load cases). Takes a closed manifold surface mesh (which every EngrCAD representation reaches —
`Shape.ToMesh()`, `BRepTessellator`, Surface Nets, an imported STL), fills it with tetrahedra,
and solves on them.

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
| `TetSmoothing` / `TetSmoothOptions` / `TetSmoothReport` | Sliver removal: interior-vertex optimization-based smoothing, boundary and anisotropy frozen |
| `TetGeometry` | Per-element measures: circumsphere, dihedrals, aspect, edge lengths |
| `QuadraticTetMesh` | The 10-node (quadratic) layer, a pure function of the linear mesh |
| `DelaunayTetrahedralization` | Internal: incremental Bowyer–Watson over exact predicates |
| `SurfacePatch` / `SurfacePatches` | Internal: coplanar same-tag triangle groups, the unit recovery works in |
| `AnalysisMesh` | The analysis view of a tet mesh: nodes, elements, tagged facets, linear or quadratic |
| `AnalysisBody` | One body of a multi-material model: a closed surface **and what it is made of** — the list `TetMesher.Mesh` and `StructuralModel.For` / `ThermalModel.For` BOTH read, so a region id is never restated |
| `Material` / `Materials` | **Lives in `EngrCAD.Core`**, not here — see below |
| `ElasticLaw` | The constitutive law: isotropic (from a `Material`), transversely isotropic, orthotropic or fully anisotropic, with a material FRAME applied once at construction — the directional half `Material` deliberately cannot carry |
| `StructuralModel` / `Facets` / `Dof` | The model: materials per region, supports and loads over facet selectors |
| `StructuralSolver` / `StructuralSolveOptions` / `FeaSolveReport` | Assembly, restraint checking, the solve, and what it did |
| `StructuralResults` / `NodalAveraging` / `StressRecovery` | Displacements, strain, stress, von Mises, publishing and `.vtu` |
| `ErrorEstimate` | The Zienkiewicz-Zhu error estimate: per element and overall, in the energy norm — the answer to "is this mesh good enough", which a solve otherwise never gives |
| `ThermalModel` | Conduction: held temperatures, flux, generation and convection over the SAME facet selectors |
| `ThermalSolver` / `ThermalSolveOptions` / `ThermalSolveReport` | Steady and theta-scheme transient solves, and the energy balance |
| `ThermalTransientOptions` / `ThermalTimeScheme` / `ThermalTransientReport` | Step, count, scheme, initial condition; one factorization per run |
| `ThermalResults` / `ThermalTransientResults` | Temperature, heat flux, per-state publishing and `.vtu` |
| `ModalSolver` / `ModalSolveOptions` / `MassLumping` / `ModalSolveReport` | Mass assembly, the shift, the eigensolve, and what it cost |
| `ModalResults` / `VibrationMode` / `RigidBodyMode` | Frequencies, mode shapes, participation factors, publishing and `.vtu` |
| `BucklingSolver` / `BucklingSolveOptions` / `BucklingSolveReport` | Geometric stiffness, the K-metric eigensolve, and what it cost |
| `BucklingResults` / `BucklingMode` | Critical load factors, buckled shapes, publishing and `.vtu` |
| `RayleighDamping` / `ModalDamping` | `C = alpha·M + beta·K` fitted to two frequencies, or a flat / tabulated per-mode ratio — no damping matrix is ever assembled |
| `HarmonicSolver` / `HarmonicSolveOptions` / `HarmonicSweep` | Steady-state response by modal superposition, with the mode-acceleration truncation correction |
| `HarmonicResponse` | Complex response over the sweep: modal coordinates, transfer functions, amplitude fields, CSV |
| `TimeIntegration` | The Newmark / HHT scheme as ONE value — beta, gamma and alpha together, with both stability conditions enforced at construction |
| `TransientSolver` / `TransientSolveOptions` / `TransientSolveReport` | Direct time integration of `M·a + C·v + K·u = f(t)` at a constant step, and the energy balance |
| `TransientResults` / `TransientState` | Every stored instant: a full `StructuralResults` plus velocity, acceleration and energies |
| `SnCurve` / `FatigueMaterials` | The Basquin S-N line (endurance limit DERIVED from its own knee) and a transcribed, verify-against-datasheet catalogue |
| `FatigueAnalysis` / `FatigueOptions` / `MeanStressCorrection` | Two load cases → per-node alternating/mean via signed von Mises, Goodman/Gerber corrected |
| `FatigueResults` | Per-node safety factor and log10 life, published and sampled like every other result |
| `TetElement` / `TetQuadrature` | Internal: shape functions, element stiffness, consistent MASS, GEOMETRIC stiffness, consistent loads, quadrature |
| `FeaAssembly` | Internal: the DOF index map, whole-model stiffness, MASS and geometric-stiffness assembly, reduction and weighted matrix sums — shared so the modal, buckling and transient solvers cannot build different `K` or `M` |
| `ThermalElement` / `TriangleQuadrature` | Internal: conductivity, capacity, convection surface matrix, expansion load |
| `LanczosEigen` | Internal: shift-and-invert Lanczos with deflation, locking and restarts — the inner-product METRIC is a parameter separate from the right-hand matrix, which is what lets one implementation serve vibration (metric M) and buckling (metric K) |
| `RigidBodyModes` / `SmallSymmetricEigen` | Internal: the surviving-rigid-motion computation the static solver REFUSES on and the modal solver REPORTS, plus the small dense eigensolver both need |
| `FeaGuards` | Internal: the element-Jacobian guard and the material-density guard EVERY solver asks, rather than each restating |
| `SurfaceSampler` | Internal: the display-mesh correspondence every results type publishes through |

## `Material` is not ours — it lives in `EngrCAD.Core`

Every solver here takes a `Material`, but the type is in `EngrCAD.Core` alongside
`ModelUnits`, because the document model needs the *same* one: `Part.Material` feeds mass
properties, the bill of materials and the default display colour, and Core is the only
assembly both projects see. Two types would have meant two densities, which is exactly the
1000× discrepancy the consolidation removed — the catalogue said tonne/mm³ while the
document model's mass properties documented kg/mm³, and neither figure is wrong on its own,
so nothing could catch a caller mixing them.

Two consequences for this project:

- **The analysis properties are OPTIONAL** (zero means "not stated"), so a material with
  only a name and a density is constructible. That is a legal *document* material and an
  illegal *analysis* one.
- **The refusals therefore live here, at the point of use, and name the property.**
  `StructuralModel`'s constructor and `SetMaterial` refuse a material with no Young's
  modulus (Lame's parameters would both be zero, so the stiffness would be identically zero
  rather than merely wrong, and the solve would report rigid-body modes for a model that has
  none); `ThermalSolver` refuses a missing conductivity or heat capacity; `ModalSolver`
  refuses a zero density. This is the same doctrine the model already followed for selectors
  that match nothing — refuse where the mistake was made, naming what is missing.

**Units are `EngrCAD.Core.ModelUnits`' mm / N / MPa / tonne / s throughout.** That type is
the single statement of the convention for the whole repository — the quantity-by-quantity
table, the tonne/mm³ density and the reasoning behind it live in its doc comment and in
design.md §2, and this project cross-references them rather than keeping a second copy that
could drift. Every verification number below is stated in that system.

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

## What kind of surface it wants

Boundary recovery is happy with **CAD tessellations**: B-Rep output, primitives, Surface Nets
fields, anything with structured triangle rows. Every fixture in the test suite recovers in
**zero rounds** — the input triangles are already faces of the Delaunay tetrahedralization.

**A remeshed surface is fine too, provided its triangulation is Delaunay-clean.** This section
used to say recovery "is not yet happy with irregular remeshed surfaces" because near-uniform
spacing has no structure. Measured, that was wrong in two directions (win-x64, Release):

| input | worst angle | worst radius-edge | patches / triangles | rounds |
| --- | ---: | ---: | ---: | ---: |
| remeshed sphere, `PreventLongEdgeFlips`, t = 2 | 36.3° | 0.84 | 832 / 832 | **0** |
| remeshed sphere, `PreventLongEdgeFlips`, t = 3 | 37.8° | 0.82 | 310 / 310 | **0** |
| remeshed sphere, `PreventLongEdgeFlips`, t = 4 | 23.1° | 1.27 | 138 / 138 | **0** |
| remeshed sphere, plain, t = 2 | 14.6° | 1.99 | — | *refused* |
| remeshed sphere, plain, t = 4 | 27.9° | 1.07 | — | *refused* |
| remeshed box, plain, t = 2 | 0.145° | 198 | 7 / 1638 | **0** |
| remeshed cylinder, every setting tried | 0.013–7.7° | 3.7–2124 | — | *refused* |
| structured cylinder (no remesh) | 3.74° | 7.66 | 50 / 188 | **0** |

**A remesh is not the obstacle.** A remeshed sphere meshes in zero rounds at three target edge
lengths with *one patch per triangle* — precisely the configuration the old explanation
blamed. **Triangle quality is not the criterion either**: a box at a 0.145° worst angle and a
radius-edge ratio of 198 meshes, while a sphere at 27.9° and 1.07 is refused, and the
structured cylinder that recovers in zero rounds has worse triangles (3.74°, 7.66) than the
remeshed sphere that does not.

What decides it is whether the surface triangulation is **already the boundary of the Delaunay
tetrahedralization of its own vertices**:

- Where the surface is **flat**, a patch lets the triangulation choose its own diagonals, so
  there is nothing to recover. A box is six or seven patches however badly it is triangulated,
  which is why the 0.145° row passes.
- Where it is **curved**, every triangle is its own patch and must appear *verbatim* as a
  face — the requirement the patch abstraction exists to avoid, arriving through the back door
  because curvature leaves it nothing to group. Refinement cannot manufacture that.

The practical rule is one flag on the remesher. Its flip stage is valence-driven with **no
length term**, so it can replace a Delaunay diagonal with a longer one, and a surface triangle
that is not locally Delaunay cannot be a face of the tetrahedralization.
`RemeshOptions.PreventLongEdgeFlips` stops that, and it is the difference between every
"refused" and every "0" in the sphere rows above.

**Recovery does not merely fail to clear the rest — it does not converge, in two distinct
ways**, and both are now detected instead of being spent on:

- A plainly remeshed **sphere** runs 6, 10, 11, 7, 5 offending faces and then sits at 5 for
  every subsequent round — 5 at round 12 and still 5 at round 40, while 44 Steiner points a
  round are spent. It is not a stall in the strict sense: the five faces differ each round,
  each smaller than the last, until by round 40 their three vertices agree to **1e-11** on a
  radius-10 sphere. Refinement is chasing its own tail into degeneracy.
- A remeshed **cylinder** diverges outright: 162, 405, 869, 1810, 3570, roughly doubling per
  round, with every offending face from round 1 onward touching a Steiner point the previous
  round created — refinement manufacturing the defect it is refining for.

So recovery stops as soon as the offending count has failed to improve on its best for five
rounds (`NonConvergenceRounds`, the monotone-decrease rule the trimmed-face refiner already
uses) and says that more rounds and a larger budget will not help. **The refusal also measures
the input** — worst minimum angle, worst radius-edge ratio, and what fraction of triangles have
no coplanar neighbour — and no longer advises "remesh the surface", which was backwards for the
input that most often reaches it: `MeshPrimitives.Cylinder`'s n-gon caps triangulate as a
one-corner fan at 3.74°, and remeshing that produces the 0.013°–7.7° rows above. Conforming to
a 0.013° sliver is expensive by *theory*, not by implementation.

What remains genuinely unsolved is the curved non-Delaunay case: a conforming Delaunay
tetrahedralization of an arbitrary PLC needs protecting-ball segment and subfacet encroachment
(Shewchuk's CDT construction, or Murphy–Mount–Gable), which would give a termination proof
instead of a budget. That is the backlog item; red subdivision is not a weak version of it but
a different thing that provably cannot reach it.

## Several materials in one model

`TetMesher.Mesh(bodies, …)` tags each tetrahedron with the index of the body it filled, and
`StructuralModel.SetMaterial(region, material)` assigns a material to a region id — a
correspondence that is right only as long as two separately written lists stay in the same
order. **`AnalysisBody` makes it a fact instead of a convention**: one list of
`(surface, material, name)` drives both the mesh and the model.

```csharp
var bodies = parts.Select(p => new AnalysisBody(p.GetMesh(), p.Material, p.Name)).ToList();
var tets   = TetMesher.Mesh(bodies, options);
var model  = StructuralModel.For(tets, bodies);      // region i takes bodies[i].Material
var heat   = ThermalModel.For(tets, bodies);         // ...and so does the thermal twin
```

That expression is also the **seam with the document model**, and it is a list rather than a
call on `Part` for a layering reason: this project depends on `EngrCAD.Core` and
`EngrCAD.Mesh` and nothing depends on it, so it cannot see a `Part` — what the two layers do
share is `Material` (which is *why* it lives in Core) and `HalfEdgeMesh`, and a body is those
two together. A null material is legal on an `AnalysisBody`, because meshing needs none and it
may have come straight from an unstated `Part.Material`; the model refuses it, by name, along
with a mesh region no body declares and a declared body that contributed no elements.

**Bodies must be DISJOINT — two mating along a face are refused by name.** That is the same
v1 boundary the section above describes, seen from the other side, and it deserves its
reasoning stated rather than an epsilon: welding the shared vertices *would* make the input
tetrahedralizable, and the result would look right and be wrong. `OffendingFaces` treats
every inside-to-inside face as interior, so an inter-body face is never recovered onto the
input plane, and a tetrahedron straddling the interface takes ONE region for its whole
volume — the material boundary would be a jagged surface of the mesher's choosing rather
than the plane the design drew. A conforming multi-material mesh needs the inter-body face
treated as a constrained boundary, plus a decision about whether a facet selector may name it
(it would be visited from both sides, so a pressure applied there would double-count). That
is a feature, and it is filed as one; until it lands, a bonded bi-material part is meshed as
one surface with one material, and `AnalysisBody` serves genuinely separate bodies in one
analysis.

## Directional materials: `ElasticLaw`

`Material` is isotropic and cannot say otherwise — it lives in `EngrCAD.Core` because a
bill of materials and a viewer need it too, and those consumers have no use for a
21-constant elasticity tensor. **`ElasticLaw` sits beside it rather than replacing it**:
the law supplies the elasticity and, optionally, a directional thermal expansion, while
density, name and the thermal transport properties still come from the `Material` — so a
modal solve of a composite part integrates the same density a BOM weighs it with.

That split is not a compromise, it is where the boundary genuinely falls. A material
**frame** — which way the fibres run, which way the plate was rolled, which way the layers
were printed — is a property of how the stuff was laid into *this part*, not of the stuff.
It is per-region analysis data, which is exactly what this project is.

```csharp
var lamina = ElasticLaw.TransverselyIsotropic(fibreFrame, 135_000, 9_000, 0.30, 0.45, 4_800);
var carrier = new Material("carbon UD", 135_000, 0.30, 1.6e-9);  // density, name
var model = new StructuralModel(tets, carrier);
model.SetElasticity(0, lamina);                                  // stiffness, and its frame
```

Three factories, in increasing generality: `TransverselyIsotropic` (five constants — a
unidirectional lamina, a drawn bar, a printed layer stack, with the transverse shear
modulus *derived* because in an isotropic plane it is not free), `Orthotropic` (nine — a
woven laminate, a rolled plate, wood), and `Anisotropic` (the full 6x6, for a homogenised
microstructure or anything else). All three take a `Frame3d`.

**Four decisions carry it.**

**(a) The frame is applied ONCE, at construction.** D is stored rotated into global
coordinates, so the element loop never sees a frame and an anisotropic element costs
exactly one `B'DB` more than an isotropic one — the same "invert the basis once" call
`LoftedSurface` makes about its cardinal basis. Laws are derived and cached per REGION, so
the 6x6 inversion and the rotation are paid once per model rather than once per element.

**(b) An isotropic model is untouched, bit for bit.** `TetElement.Stiffness` branches on
`ElasticLaw.IsIsotropic`, which only `FromMaterial` sets, so a model that states no law
assembles through exactly the index-form arithmetic it always did — asserted as a bitwise
comparison, which is what makes this feature safe to add under a verification suite whose
headline numbers are quoted to twelve digits. The general path is *separately* asserted to
agree with the index form to round-off on an isotropic law, so the two cannot drift in
MEANING while staying deliberately separate in ARITHMETIC. The thermal-load path makes the
identical split for the identical reason (and measures 0.0 difference on the fixture, which
was better than the argument required).

**(c) The minor Poisson's ratios are DERIVED, not inputs.** `nu_ji / E_j = nu_ij / E_i` is
the symmetry of the compliance matrix and the only relation there is; letting a caller
supply both is how a datasheet transcription comes to contradict itself, and the
contradiction would make the matrix non-symmetric with nothing to catch it.

**(d) Positive definiteness is checked by a Cholesky, because the Cholesky IS the
statement.** The classical orthotropic restrictions — `|nu_ij| < sqrt(E_i / E_j)` plus a
determinant condition coupling all three — are precisely "the compliance matrix is positive
definite", and testing them one at a time re-derives that in a form easy to get subtly
wrong. Note that the pairwise bounds are *not* sufficient: the fixture that pins this has
three equal moduli and `nu = 0.7`, where every pairwise bound holds (`sqrt(1) = 1`) and the
determinant is still negative. Without the check, a plausible transcription gives a
material that releases energy under some strain and the symptom is a factorization failure
deep in the solver rather than a message about the material. The same routine checks a
supplied 6x6, alongside a symmetry check that names the offending pair.

**Thermal expansion is stated on the LAW, and the branch needs BOTH halves of its
condition.** An expansion stated on the law wins over the `Material`'s scalar whether or not
the law is elastically isotropic — an elastically isotropic, thermally directional material
is unusual but real, and a stated coefficient with no effect is the worst failure mode. And
the scalar route is legal only for an *isotropic* law, whose Lamé parameters are the
material's: a directional region stating no expansion of its own is **refused by name**
rather than inheriting one, because the scalar load would be built from parameters that are
not that region's stiffness. (Both halves are pinned by test; the first version branched on
`IsIsotropic` alone and would have silently ignored the first case, the second on
`StatesOwnThermalExpansion` alone and would have quietly mis-built the second.)
`WithThermalExpansion(frame, a1, a2, a3)` rotates the expansion TENSOR with the same frame,
which produces genuine **shear** components off-axis (a heated off-axis lamina shears); that
is why the free strain is carried as a six-component Voigt vector rather than three numbers,
and why the stress recovery subtracts all six.

The law reaches modal, buckling, harmonic and transient solves for free, because every one
of them assembles its stiffness through `FeaAssembly`.

**Not covered, and stated rather than implied**: `Materials` carries no composite entries
(the catalogue is isotropic engineering metals and plastics, and a lamina's constants are a
layup decision rather than a stock number); a *laminate* — a stack of plies at different
angles — is several regions or a homogenised law the caller supplies, not something this
computes; and failure criteria for directional materials (Tsai-Wu, Hashin, maximum strain)
are a post-processing vocabulary that does not exist here, so `MaxVonMises` on a composite
part is a number with no engineering meaning.

## Stress recovery, and the error estimate that comes with it

A displacement-based element's stress is one order lower than its displacement and jumps
across faces, and **its accuracy is not uniform inside the element**: at the Gauss points of
the element's own rule it is *superconvergent* — as accurate as the displacement rather than
one order worse — while at the nodes, where a colour map most wants it, it is at its worst.
`StressRecovery.Direct` (the default) reads the nodes and averages;
`StressRecovery.Superconvergent` samples the good points and interpolates to the bad ones.

```csharp
results.Recovery = StressRecovery.Superconvergent;
var peak = results.MaxVonMises;                  // one order more accurate
Console.WriteLine(results.ErrorEstimate);        // "estimated error 4.31% (...)"
```

Zienkiewicz–Zhu patch recovery, with four decisions.

**(a) Vertex patches, evaluated at every node they touch.** A patch is the elements meeting
at one corner node; a polynomial of the displacement's own degree is fitted to its samples
by least squares and evaluated at every node of every element in the patch, each node
averaging the patches that reached it. That is the textbook rule, and it means a mid-side
node takes its value from the adjacent vertex patches rather than from a smaller,
worse-conditioned patch of its own.

**(b) Patches are assembled at INTERIOR corners only, and that was measured rather than
assumed.** A one-sided boundary patch is poorly conditioned, and including them **caps the
convergence rate**: the quadratic sequence read 2.77 then 2.47 — drifting down, away from
the theoretical 3 — and excluding them put it on 3.08/2.91. The signature is worth
recognising: a boundary layer of elements is an O(h) volume fraction, so a boundary error
one order worse shows up as a half-order loss in a global L2 norm, and a rate heading for
2.5 rather than 3 is what that looks like.

**(c) The boundary is then filled from the NEAREST patch, because skipping it costs the
whole point.** Excluding boundary patches leaves the nodes near a box's edges and corners
with no value at all — 228 of ~2 000 on the fixture — and those are exactly where a peak
stress usually sits. A breadth-first walk over the share-an-element graph gives each of them
the polynomial of the nearest patch, extrapolated a short way outside its own elements,
which is sound for the same reason the fit is: the polynomial approximates the stress over a
neighbourhood, not only over the sample hull.

**(d) The fit is in LOCAL coordinates.** Sample positions are shifted to the patch node and
scaled by the patch radius, so the normal equations are O(1) and their conditioning is a
property of the patch's SHAPE rather than of where the part sits in space. It also makes the
answer free — the recovered value at the patch node is exactly the fitted constant term.

**The error estimate is the more valuable half.** `StructuralResults.ErrorEstimate` is the
energy-norm distance between the element stress field and its recovery: per element (the map
an adaptive scheme refines against) and globally (the answer to "is this mesh good enough",
which a solve otherwise never gives). It is always computed from the *recovered* field
whatever `Recovery` is set to, because that is what makes it an estimator rather than a
smoothness measure.

**And it reports UNKNOWN rather than zero when it cannot estimate.** With no interior corner
node there is no patch, the recovered field *is* the finite-element field, and the arithmetic
gives the distance from something to itself: measured on a 24-element box, **9.9e-15 against
a true error of 0.313** — a number that calls the mesh perfect on exactly the mesh too coarse
to assess. `RelativeError` is `NaN` there, following the same "not small, unknown" spelling
`HarmonicResponse.TruncationError` uses.

`Direct` stays the default. Every verification figure this project quotes was measured
through it, so promoting the better answer would silently move all of them; and recovery is
not better *everywhere* — a recovered field is smooth by construction, so at a genuine
discontinuity (a material interface, a re-entrant corner) it smooths harder than averaging.

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

### Removing the slivers: `TetSmoothing`

`TetSmoothing.Smooth(mesh)` is the post-pass that acts on what the second measure sees. It
moves **interior** vertices to raise the worst dihedral angle, leaving the topology and the
boundary exactly as they were.

```csharp
var tets     = TetMesher.Mesh(surface, new TetMeshOptions { RefineQuality = true, MaxElementSize = 2 });
var smoothed = TetSmoothing.Smooth(tets, null, out var report);
Console.WriteLine(report);   // min dihedral 0.00 -> 17.00 deg, slivers 399 -> 0, drift 7.8e-15
```

Measured on a 20³ box (win-x64, i9-9900K, Release):

| target size | elements | ms | min dihedral | mean min-dihedral | slivers < 10° | volume drift |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 3.0 | 15 834 | 3 947 | 0.00° → **13.86°** | 42.4° → 39.5° | 190 → **0** | 1.3e-14 |
| 2.0 | 40 593 | 4 924 | 0.00° → **17.00°** | 44.4° → 41.2° | 399 → **0** | 7.8e-15 |
| 1.5 | 103 103 | 12 865 | 0.01° → **10.07°** | 43.6° → 39.9° | 1 149 → **0** | 2.1e-14 |

**Every sliver goes, at every size — on this fixture, and the residual is INPUT-DEPENDENT.**
A 20³ box whose faces are triangulated by the B-Rep tessellator instead
(`Shape.Box(20, 20, 20).ToMesh()`, which is what the docs page measures) starts from the
identical 190 slivers and ends with **2** rather than 0. A pattern search is a heuristic local
optimizer, so a small difference in the input changes which candidate wins a near-tie and with
it the whole path. Two things are worth naming as *not* the cause, because both were guessed
first and both are wrong: it is not translation (the same primitive anchored at a corner and
centred on the origin both reach 0) and not the build (Release and Debug agree bit for bit).
Read the table as "essentially all" rather than as a guarantee — the pass promises a
*deterministic* answer for a given input on a given build, not a sliver-free one, and the tests
assert a strict decrease rather than zero for exactly that reason.

Two further counterweights sit beside it. The **mean**
minimum dihedral falls by 2–4°, because the objective is the worst incident angle and lifting
it moves a vertex slightly away from what its other elements would have preferred — a real
trade, and the right one when the worst element is what conditions the matrix. And it is
**expensive**: on the 40 593-element box it costs 4.9 s against 504 ms to build the mesh, which
is why it is a post-pass a caller asks for rather than something the mesher does.

**Why smoothing rather than exudation.** The two standard answers are sliver exudation (a
weighted-Delaunay perturbation, which changes the topology) and optimization-based smoothing
(which moves points only). Only the second keeps every guarantee the mesher already makes
without re-deriving any of them: the boundary is untouched so the surface-fidelity contract and
the volume identity hold **by construction** (the drift column is pure round-off — mathematically
the elements go on tiling the same region), the connectivity is untouched so nothing has to be
re-classified or re-recovered, and every candidate position is accepted only if it leaves all
incident elements strictly positively oriented **by the exact predicate**, so `TetMesh`'s
orientation invariant is preserved rather than re-checked and hoped for. Exudation is the
stronger technique and is still the filed next step; it is also the one that can invalidate all
of that at once.

**Three rules make it safe**, and the second is the one to know about:

1. **Boundary vertices never move** — which is what makes the volume identity exact.
2. **Vertices touching a deliberately anisotropic element never move.** A smoother that
   "repairs" a boundary layer into isotropy would destroy the resolution the layer exists to
   provide, so every vertex of an element stretched past
   `TetQualityOptions.AnisotropyThreshold` is frozen and the report says how many. This
   inherits the partition's honest limit: a layer element and an accidental sliver are
   *affinely equivalent*, so freezing by measured stretch necessarily freezes accidental
   stretched slivers too. Intent is not recoverable from geometry — the number is reported
   instead of the problem being wished away.
3. **A move must strictly improve, by a floor** — a monotone-increase rule, so the sweep
   terminates and cannot oscillate.

Deterministic throughout: ascending vertex order, a fixed direction table, a fixed halving
stride schedule, no RNG and no parallelism, so two runs give bit-identical positions (asserted).
The structural patch test still reproduces a constant-strain state to **3.9e-15** relative
through a smoothed mesh, which is the check that nothing inverted or tore.

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

## Anisotropic boundary layers

A boundary layer is a graded stack of very flat elements marched **inward** from a named wall,
with the isotropic pipeline filling whatever is left. It is what resolves a steep gradient
normal to a surface — a viscous wall in CFD, a thermal skin, a contact face — without paying
for isotropic elements of that thickness everywhere.

```csharp
var tets = TetMesher.Mesh(surface, new TetMeshOptions
{
    FacetTags = tags,                       // B-Rep face ids, say
    RefineQuality = true,
    MaxElementSize = 2.0,
    BoundaryLayer = new BoundaryLayerSpec
    {
        Wall = Facets.Tag(1),               // the SAME selector a no-slip condition uses
        FirstLayerThickness = 0.15,
        LayerCount = 4,
        GrowthRatio = 1.3,
    },
}, out var report);

var layer = report.BoundaryLayer!.Value;    // element count, measured thicknesses, clearance
```

**Walls are named the way boundary conditions are named.** `BoundaryLayerSpec.Wall` is a
`Facets` predicate over the input surface's triangles, so `Facets.Tag(id)` picks the same face
for the layer that it picks for a condition later, and the geometric selectors
(`OnPlane`/`FacingAlong`/`InBox`) work here too.

The stack's elements are emitted **first**, so `[0, BoundaryLayerReport.ElementCount)` names
them in the finished mesh.

### How it works, and why it is only one new stage

The nodes are marched first; what is left over is then bounded by an ordinary closed triangle
mesh — the offset wall plus the non-wall faces trimmed back to the stack's rim — which
`TetMesher` already knows how to fill. So there is **no new volume algorithm**: this stage
produces columns and a surface, and every guarantee the existing pipeline gives (a conforming
boundary, the volume identity, exact orientation, determinism, refusals that name what failed)
is inherited rather than restated.

**Nodes march along a per-node average, not the facet normal.** The direction at a wall vertex
is the angle-weighted average of its incident wall facets' normals (the same pseudonormal
convention `MeshSdf` uses). Marching each triangle along its own normal would move a shared
vertex to several different places and tear the layer open along every edge where two facets
disagree — which on a curved wall is every edge.

**A rim node slides along the flat face it stops on**, and it does so by construction rather
than by tolerance: its direction is projected out of every non-wall plane it touches, so
`p + s·d` stays in that plane at every `s` and the stack's side wall is genuinely part of the
part's surface. How far the projection had to turn the direction is then *checked*
(`MinimumConstraintCosine`, 60° by default). A rim landing on a CURVED neighbour — more than
two distinct plane normals at one vertex — is refused by name.

**Prisms are split into three tetrahedra each, and the diagonal rule is combinatorial.**
`TetMesh` stores tetrahedra, so the prisms have to be split, and each quadrilateral side face
needs a diagonal that BOTH prisms sharing it agree on — otherwise the mesh is non-conforming
and every solver integrates over a gap it cannot see. The rule is Dompierre's: a quad's
diagonal contains whichever of its two base vertices has the smaller index in the input
surface. It is symmetric in the two, so neighbours agree without communicating.

> The geometric rule this repo uses elsewhere (`PolygonFan`'s shorter 3D diagonal) would be
> **wrong here**, and not marginally. A layer quad on a flat wall is an exact rectangle, whose
> two diagonals are mathematically equal, so the choice would fall to round-off on essentially
> every element of the stack — the same trap that made 408 of a UV sphere's 960 quads flip on
> an ulp.

**The stage runs in two passes, and the reason is the mesher's own design.** Boundary recovery
works per planar *patch* precisely because a Delaunay triangulation picks its own diagonal
across a coplanar quad — so handing the fill an offset wall and assuming it comes back
triangulated the same way is wrong on the most ordinary geometry there is, and wrong silently.
Hence: march the columns, hand over the surface, then read the interface triangulation **back**
off the finished fill and build the prisms on that. The fill chooses; the stack conforms.

### The interface is frozen — and that is the one thing to plan around

Once the stack has elements against the offset wall, the fill must not insert a vertex into
it. Sizing-driven boundary refinement and quality refinement's encroachment splitting both
honour that (recovery is deliberately left free, so a genuine failure is caught by the layer's
own interface check, which can say what went wrong).

The consequence is the standing rule of boundary-layer meshing, and it is worth stating
plainly: **the surface mesh sets the layer's in-plane element size.** Ruppert's encroachment
rule blocks interior refinement inside the interface triangles' diametral balls, so on a plain
two-triangles-per-face box those balls are half the box and *nothing* refines. Refine the WALL
surface to the size you want before growing the layer.
`TetMeshDiagnostics.RefinementBlockedByFrozenBoundary` reports how many refinement points were
declined for this reason, so the limitation is a number rather than a surprise.

### Refusing loudly

Producing inverted elements where a layer does not fit is far worse than refusing, so four
nets sit in front of that, in the order they fire, each naming what to change:

| net | catches | message |
| --- | --- | --- |
| per-facet fold | a wall turning faster than the stack is tall — a convex corner or fillet smaller than the stack, or a facet shorter than it | names the facet, its tag, its centroid and the layer |
| trimmed-face inversion | two flat walls swapping places across a thin part | names the face and its signed area before and after |
| self-intersection (`MeshIntersection.WithinItself`) | the offset surface genuinely crossing itself | names where and by how much |
| consumed volume | a stack that swallows its body | names the body and what is left |

**Nothing reachable from a real body currently exercises the self-intersection net**, and the
reason is structural rather than lucky: two flat walls closing on each other never *cross* —
they swap places, and two parallel sheets that have swapped places are still parallel — so the
face between them turns inside out first; and where a wall is curved enough to make the offsets
genuinely cross, its facets fold before they get there. It stays as the backstop for a shape
with neither property, and `BoundaryLayerTests` locks which net catches which family so the
finding cannot rot.

### Verified

On a 20³ box with the whole surface walled (0.5 mm first layer, ratio 1.2, 3 layers), a
Ø10×20 duct with a graded wall and an isotropic core, an L-prism with a genuine concave edge,
and boxes over six decades of scale:

- **volume identity** — sum of element volumes against the input surface's, relative:
  **0 to 3.8e-14** across every fixture, well inside the mesher's own 1e-9 bar
- **layer thicknesses and growth reproduced exactly** — measured first layer
  `0.15000000000000036` against a requested `0.15`, worst measured ratio
  `1.3000000000000074` against a requested `1.3`
- **the stack's outer skin has exactly the wall's area**, to 1e-9 relative
- **every face is used once or twice** (the direct statement of conformity), and the faces used
  once are exactly the reported boundary facets
- **determinism** — two runs bit-identical, positions and element order
- **the existing structural and thermal verification fixtures still hold through a layered
  mesh** (`BoundaryLayerSolverTests`, linear / quadratic):

  | fixture | measured on a layered mesh |
  | --- | --- |
  | structural displacement patch test | displacement **9.8e-16 / 9.6e-15** relative, strain 3.4e-14 / 1.4e-13 |
  | thermal patch test | temperature **2.4e-15 / 9.9e-15** relative, flux 9.1e-14 / 3.0e-13, energy balance 1.2e-14 / 2.6e-14 |
  | 1D slab, exact linear profile | temperature **1.1e-14 / 4.0e-14** relative on a 100 K drop, flux 1.4e-13 / 3.0e-13 |
  | equilibrium (pressure + gravity vs reactions) | **1.2e-12** relative |
  | cantilever vs Euler–Bernoulli | **−1.27% → −0.32% → −0.16%** at 420 / 1 688 / 3 796 quadratic elements — monotone from the stiff side |

The patch tests are the ones that earn their keep, because a wrongly split prism is invisible
to everything else: the solve converges and returns a plausible answer. A linear field can only
be reproduced *exactly* if the elements genuinely tile the body, so a patch test on a layered
mesh is a direct measurement of the diagonal rule.

### Not in v1

Uniform thickness only (no per-facet law); the march measures its thickness **along its own
direction**, so at a convex corner the perpendicular stand-off is `cos` of half the corner
angle rather than the requested value (the report's `MinMarchClearance` is that number); a rim
may only stop on FLAT faces; the wall triangulation is not refined for you; and CFD needs a
flow solver, which this is not — what it gives CFD is the mesh, not the physics.

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
- **The exact `InSphere` stage is where this project's allocation goes, and cospherical input
  is the normal case rather than the hostile one.** `Predicates3d.InSphere` escalates to
  `BigInteger` when its filter cannot settle the sign, which costs **9 229 ns and 5 698 bytes**
  against **73 ns and 0.1 bytes** for a filtered call (win-x64, Release). A tessellated sphere's
  vertices are *all* exactly cospherical, so it escalates 3.48 times per element and the exact
  stage accounts for an estimated **58% of the mesher's total allocation** on a Ø20 sphere
  (275 of 479 MB); a refined box escalates 0.30 times per element, for 34%.
  `TetMeshDiagnostics.InSphereEscalations` reports it per run, and
  `TetMesherBenchmark.InSphereExactStage_CostAndHowOftenItIsPaid` is the measurement. Fixing it
  is a Core change (a pooled or fixed-width big integer, or Shewchuk's `insphereexact`
  expansion form) and is filed in todo.md.

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
copies nothing but index arrays. Three API-hygiene rules it and `StructuralResults` share:
derived summaries (`Regions`, `FacetTags`, `Bounds`, `Volume`, the lazy stress caches) are
computed once and published lock-free by CompareExchange — legal because each is a pure
function of immutable data, so a racing first read computes an identical value and the loser
is discarded; every `IReadOnlyList` property is a read-only VIEW, never the internal array
castable back to `T[]` (the array behind `Nodes` is what every solver assembles from, and the
one behind `Displacement` feeds every derived stress pass); and a mesh whose index arithmetic
would overflow `int` (~215 M quadratic elements, ~716 M nodes) is refused at `Of` by name
rather than failing as a bare `OverflowException` from a negative array length. What is still
NOT safe is mutating `Averaging`/`Recovery` while another thread reads — the setters clear
the caches, and that is stated on the type.

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

**What would move that factorization wall is now a measurement rather than a guess.**
`SparseCholesky.Analyze` reads the deciding numbers off the symbolic pass in 5–520 ms,
where the factorization it describes costs 0.1–134 s
(`FeaBenchmark.WhatWouldMoveTheFactorizationWall`):

| | free DOF | ordering | factor nnz | updates | longest column | parallel ceiling |
| --- | ---: | --- | ---: | ---: | ---: | ---: |
| linear | 14 688 | Natural | 22 701 719 | 1.88e10 | 1 732 | 1.0x |
| linear | 14 688 | Amd | 8 769 703 | 4.32e9 | 1 702 | 1.6x |
| linear | 46 800 | Amd | 57 616 104 | 6.42e10 | 3 537 | 1.6x |
| quadratic | 14 688 | Natural | 96 705 622 | 4.20e11 | 12 956 | 1.0x |
| quadratic | 14 688 | Amd | 6 498 728 | 2.54e9 | 1 400 | 1.9x |

The **parallel ceiling** is total work over the elimination tree's heaviest root-to-leaf
path — the most a perfectly scheduled parallel factorization could ever win, with unlimited
processors and free synchronisation. At **1.0–1.9x** it rules out a parallel scheduler for
this algorithm, and for a structural reason: in 3D the top separator's columns are a
constant fraction of all the work and form a *chain*. The same table says where that work
sits — a few nearly-dense columns of 1 400–3 748 entries — which is what a supernodal
BLAS-3 kernel exists for. So blocking is the lever and scheduling is not.

Useful by-product: the **update count predicts factor time at ~1.0–1.3 ns per update**
across a 15x size range and both element orders (idle machine; the constant is
machine-specific), so what a factorization will cost is answerable before paying for it.

**The direct solver is still the default, and the reason is exactness, not speed.** This
project's verification claims — the patch test reproduced to round-off, strain errors at
1e-13, the two element orders agreeing on strain energy to twelve digits — cannot be made
about an iterative solve stopped at a relative residual. A default of CG would make every
headline accuracy claim a statement about an opt-in path. It also reports its fill, which
is the diagnostic that says a mesh is bad.

One honesty note on that decision: **no automatic size-based switch is offered**,
deliberately, because a crossover measured on one operator measures that operator — baking
a threshold taken from one cantilever into the library default would be the very mistake
the row above documents.

## Several load cases: `SolveAll`

The other classic argument for a direct solver — factor once, substitute many right-hand
sides — used to be one this library could not honour, because `Solve` factored and
discarded. `StructuralSolver.SolveAll` is the entry point that makes it real:

```csharp
var mesh  = AnalysisMesh.Quadratic(tets);          // ONE mesh object
var cases = loads.Select(f => { var m = new StructuralModel(mesh, steel);
                                m.Fix(Facets.Tag(1));
                                m.Force(Facets.Tag(2), f);
                                return m; }).ToList();

var results = StructuralSolver.SolveAll(cases);    // one assembly, one factorization
```

Measured (Release, win-x64, alternating in one sitting, best of three), against the same
cases solved one at a time:

| | free DOF | cases | separate | shared | speedup | factor | per extra RHS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| linear | 6 552 | 4 | 2 159 ms | 591 ms | **3.66x** | 524 ms | 6.69 ms |
| linear | 14 688 | 4 | 18 310 ms | 4 839 ms | **3.78x** | 4 706 ms | 27.12 ms |
| quadratic | 2 160 | 4 | 223 ms | 64 ms | 3.50x | 34 ms | 0.73 ms |
| quadratic | 6 552 | 4 | 1 660 ms | 456 ms | 3.64x | 317 ms | 5.13 ms |
| quadratic | 6 552 | 8 | 3 391 ms | 489 ms | **6.94x** | 330 ms | 10.83 ms |

An extra right-hand side costs 0.7–27 ms against 34–4 706 ms to factor, so the speedup
tracks the case count until the substitutions themselves start to matter — 3.5–3.8x of a
possible 4, 6.9x of a possible 8. **It also changes the direct-vs-iterative comparison
rather than merely improving a number**: CG reuses nothing but the matrix, so N cases cost
N whole CG runs, and the ratio in the table above divides by N. `FeaSolveReport.Advisory`
now says so, and stops firing once the amortisation has already won.

**What the cases must share is checked, not assumed**, and the list is what they are allowed
to differ in — loads, and the *values* of prescribed displacements. The stiffness matrix is
a function of the mesh, the materials and which degrees of freedom the supports removed, so
all three are compared: the mesh by reference (a reused factorization is a statement about
one node numbering), the restraint mask per node exactly, the material per element by value.
A prescribed *value* may differ freely because it moves to the right-hand side as
`f -= K_fc·u_c`, which is per case by construction. The refusal names the case and what
differs, because the alternative failure is silent: substituting one case's right-hand side
through another's factorization returns a field that converges, passes its own residual
check, and answers a question nobody asked.

`Solve` is now literally `SolveAll([model])[0]` — one implementation, so the two cannot
drift, and the single-case answer is bit-for-bit what it was.

Every case's report carries the **same** `AssembleMs` and `FactorMs`, because there was one
of each; only `SolveMs` is that case's own, and `LoadCases` says how many shared the cost.
Reporting a per-case share of a shared cost would be a made-up number and reporting zero
would hide what the run spent.

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

## Cancelling a solve

Every solve entry point takes Core's optional trailing `ProgressCancel`:

```csharp
using var source = new CancellationTokenSource();
var watch = new ProgressCancel(source.Token, fraction => Console.WriteLine($"{fraction:P0}"));

var results = StructuralSolver.Solve(model, null, watch);
```

`StructuralSolver.Solve`, `ThermalSolver.Solve` / `SolveTransient`, `ModalSolver.Solve` and
`BucklingSolver.Solve` all take it, and cancellation surfaces as
`OperationCanceledException` with nothing partial returned.

**What makes the parameter honest is that `SparseCholesky.Factorize` honours it.** The
advisory above names a slow factorization once it has finished, which helps the second run
and not the first — and the first run is where someone waits a minute and a half wondering
whether it has hung. Adding the parameter here *before* the factorization could be
interrupted would have advertised a cancellation that cannot cancel 99% of the work, which
is worse than none at all; `SparseSymmetricCG.Solve` takes one for the same reason from the
other side, so the promise holds whichever method runs.

**The fraction reported is the factorization's own**, and that is a measurement rather than
a shortcut: on any model slow enough to want a progress bar the factorization *is* the
solve (79.0 s of 80 s at 46 800 unknowns, against 0.32 s to assemble and 0.25 s to
substitute), so inventing per-phase weights would put a made-up number in front of a
measured one. Assembly, the reaction/energy pass and the element guards poll for
cancellation at element checkpoints and report no fraction; an iterative solve reports none
at all, because an iteration count is not progress.

**The one solve whose fraction is not the factorization's is the transient**, and the
reason is the same argument reversed: it factors once and then spends the run in
back-substitutions of uniform cost, so its step number is a genuinely exact measure of how
far along it is. It reports one fraction per step.

## Where assembly's time goes — and what it is worth parallelising

The element loop looks embarrassingly parallel, so it is worth knowing what is actually in
it before anyone parallelises it. Measured with the phases separated
(`FeaBenchmark.WhereAssemblyTimeGoes`, Release, win-x64, shares taken within one sitting):

| | elements | free DOF | element stiffness | whole assembly | its share | reactions |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| linear | 10 368 | 6 825 | 4 ms | 22 ms | 17% | 5 ms |
| linear | 24 576 | 15 147 | 9 ms | 149 ms | 6% | 15 ms |
| quadratic | 1 296 | 6 825 | 10 ms | 56 ms | 18% | 12 ms |
| quadratic | 3 072 | 15 147 | 18 ms | 130 ms | 14% | 18 ms |

**Computing the element stiffnesses is 6–18% of assembly.** The rest is the scatter into
the matrix builder, which is a shared write whose ORDER decides the last bits of every
summed entry — so it is the part that cannot be parallelised without giving up bit-identical
output. Two things follow, and both were reached by measuring rather than by reasoning about
the loop's shape:

- **The parallel element loop is declined, with the number.** It can address at most the
  6–18%, and assembly is itself ~4% of a quadratic direct solve and ~13% of a large
  iterative one, so a perfect parallelisation of the parallelisable half is worth under 2%
  of a solve. The shape that *would* work if that share ever grows: per-block
  `SparseMatrixBuilder`s merged **in block order**, which reproduces the serial add sequence
  exactly and so keeps the packed values bit-identical — the merge is the cost, and it is a
  copy of every entry.
- **Caching element stiffnesses for the reaction pass is declined too.** The todo's
  suggestion was that the reaction/energy pass recomputes every element stiffness a second
  time, which it does — but the whole pass is 5–18 ms against hundreds of milliseconds to
  assemble and seconds to factor, and the recomputation is at most 60–90% of that. Holding
  every element stiffness would cost 95 MB on the largest linear case to save single-digit
  milliseconds. `FeaSolveReport.ReactionMs` now reports the phase, so the decision has a
  number attached instead of hiding inside "total".

What the measurement *did* find is one layer down and is fixed: `SparseMatrixBuilder`'s
per-row insertion sort was O(k²) at k = 612 raw entries per row, which a 10-node
tetrahedral mesh reaches routinely. Replacing it with a stable key sort took quadratic
packing from **250 ms to 31 ms** (7.25x on the sort itself, measured against the old code
transcribed verbatim and alternating in one sitting) and made it linear in the entry count.
See the Core README. The general lesson is in CLAUDE.md: *an asymptotic guard justified by
the caller you had is not a guard, and a new consumer is exactly the event that invalidates
it.*

Whole-pipeline cost (Release, **idle machine**, and note the assemble column predates the
packing fix above, so it is now an upper bound), which says where the time actually goes:

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

**Directional materials**, exact rather than converging — a uniform stress state in a
prismatic bar is a constant strain, hence a linear displacement field, which is *in* the
linear-tetrahedron space, so the solve reproduces the closed form at any density and the
constitutive law is the only thing under test:

| lamina angle | apparent E_x | reference |
| ---: | ---: | --- |
| 0° | 135 000.00 MPa | E1 exactly |
| 30° | 20 267.42 MPa | classical off-axis modulus, 1e-12 relative |
| 45° | 12 406.66 MPa | same |
| 90° | 9 000.00 MPa | E2 exactly |

with the whole strain tensor at **4.4e-17 against a 2.0e-3 scale** and the uniform stress
state at 1.4e-12 of 25 MPa. **The oracle is built by 3x3 TENSOR rotation** — rotate the
stress into the material frame, apply the compliance there from the engineering constants,
rotate the strain tensor back — which shares nothing with the production path's Voigt
stress transformation but the physics; agreement between two derivations is evidence, where
a test re-running the same rotation would agree with a broken one just as happily. The
classical formula `1/Ex = c⁴/E1 + s⁴/E2 + (1/G12 - 2ν12/E1)s²c²` is then a *third* reading
of the same number. **Shear-extension coupling** is asserted separately (−7.9e-4 at 30°,
exactly zero on axis): it is the one behaviour no isotropic law can produce, and the one a
transposed rotation would lose while leaving every symmetric measure intact.

Directional thermal expansion: a free bar strains by exactly `alpha_i·dT` (1.1e-17 of
1.7e-3) with residual stress at 1.2e-13 of the 15.12 MPa a restrained bar would carry, and a
fully restrained bar carries σ = (−12.72, −27.95, −27.95) MPa — three genuinely different
numbers, so a law quietly collapsing to one scalar fails even at the right magnitude — to
3.6e-15.

**Stress recovery** — the claim is a RATE, not a value, so it is measured as an L2 norm of
the nodal stress field against the exact one, integrated INSIDE the elements over a
refinement sequence on the same manufactured solution:

| | direct | recovered | direct rate | recovered rate |
| --- | ---: | ---: | ---: | ---: |
| linear, 192 el | 1.540e-1 | 3.957e-2 | — | — |
| linear, 1 536 el | 6.151e-2 | 7.875e-3 | 1.324 | 2.329 |
| linear, 12 288 el | 2.302e-2 | **1.599e-3** | 1.418 | **2.300** |
| quadratic, 192 el | 1.068e-2 | 1.589e-3 | — | — |
| quadratic, 648 el | 4.745e-3 | 5.197e-4 | 2.000 | 2.756 |
| quadratic, 1 536 el | 2.669e-3 | **2.348e-4** | 2.000 | **2.761** |

against theory 1 and 2 (linear) and 2 and 3 (quadratic) — so **14.4x and 11.4x more accurate
at the finest mesh**, at a rate clearly above the direct one in both. The quadratic recovered
rate settling near 2.76 rather than 3.00 is reported as measured rather than rounded up: full
p+1 recovery on TETRAHEDRA is weaker than on hexahedra, and the boundary fill's extrapolation
is one cap on it.

The **effectivity index** (estimated over true error, in the energy norm) is the measurement
that says the estimator estimates the error rather than merely shrinking:

| | 192 el | 1 536 el | 12 288 el |
| --- | ---: | ---: | ---: |
| linear | 0.9544 | 0.9848 | **0.9955** |

| | 192 el | 648 el | 1 536 el |
| --- | ---: | ---: | ---: |
| quadratic | 1.0187 | 1.0150 | **1.0128** |

monotone toward 1 from below and above respectively — and both halves are asserted, since a
constant offset would satisfy a band alone. A **linear stress field is recovered exactly**
(1.1e-12 of 3.2e2 linear, 1.9e-11 of 1.7e3 quadratic) with the estimated error 0.00%, which
is the consistency check every recovery scheme must pass and the identity that keeps the
estimator from being a smoothness measure.

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
  semi-infinite erfc to 0.184 K on an 80 K step (2.3e-3); time order **1.05** and **2.00**;
  the whole-run first law at 1.3e-14 … 8.5e-13.
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
- No contact, plasticity or large deformation. Each is a different mathematical problem
  rather than a bigger version of this one.

---

# Modal analysis (natural frequencies)

The generalized symmetric eigenproblem `K·phi = lambda·M·phi` over the same `AnalysisMesh`,
the same materials and the same supports a `StructuralModel` already carries. Docs page:
[`docs/examples/fea-modal.md`](../../docs/examples/fea-modal.md).

```csharp
var model = new StructuralModel(AnalysisMesh.Of(tets), Materials.Steel);
model.Fix(Facets.OnPlane(new Vector3d(-40, 0, 0), Vector3d.UnitX));

var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 6 });
Console.WriteLine(results.ToText());                      // frequencies + effective masses

foreach (var field in results.SampleOnto(surface))        // one vector field per mode
    part.AddResult(field);
part.FieldDisplay = new FieldDisplay
{
    Field = ModalResults.FieldNames.Shape(1),
    Deform = ModalResults.FieldNames.Shape(1),
    DeformScale = 8,
};
```

`omega = sqrt(lambda)` and `f = omega/2pi` come out in rad/s and Hz with no conversion,
because the mm/N/tonne system is consistent in seconds (one tonne is one N·s²/mm).

## The mass matrix IS the thermal capacity matrix

Both are `integral(constant · N_i · N_j dV)`, so there is **one implementation**
(`TetElement.ConsistentMass`) with `rho` where the capacity has `rho·c`; the structural
assembly then replicates each scalar entry onto the 3x3 identity block, because an isotropic
inertia couples no two axes. `ThermalElement.Capacity` delegates to it. That matters because
the quadrature rule is the thing to get wrong: `TetQuadrature.ForMass` is **two degrees above**
`TetQuadrature.For`, and two copies would be two chances to under-integrate.

**Under-integrating is silent.** An n-point rule produces a matrix of rank n, so the
stiffness's rule gives a 4-node mass of rank 1 and a 10-node mass of rank 4 — singular either
way — while `sum_ij N_i N_j = (sum_i N_i)² = 1` exactly, so the total is still exactly
`rho·V`. "Does the mass matrix add up to the mass" passes it.

The check with teeth is the **rotational inertia**: a rigid rotation is linear in position, so
both element orders represent it exactly and `u' M u` must equal the tetrahedron's own
`omega' I omega`. Against `MeshMassProperties`' closed-form polyhedral moments — independent
arithmetic in another project — the production rule agrees to **2.2e-16 … 9.4e-15 relative**,
while the one-point rule reports **−2.4e-27 against a true 1.4e-10**: no rotational inertia at
all, because every entry being equal collapses `u' M u` to the square of the mean nodal value
and the mean of a rotation about the centroid is zero.

### Lumping

`MassLumping.Consistent` (default), `Hrz`, and `RowSum` for **linear elements only**.

**Row-sum lumping is refused by name for 10-node elements**: their row sums are `−V/20` at
every CORNER node — a negative mass, a node that accelerates towards a force pushing it away
— the same integral that already makes a quadratic element's consistent gravity load negative
at the corners. HRZ scales the consistent matrix's own strictly positive diagonal
`rho·integral(N_i² dV)` to preserve the element mass, and for a 4-node tetrahedron it produces
exactly the row sums (`rho·V/4`), which is asserted rather than assumed.

The reason to offer lumping is that the two schemes **bracket** the truth, not that either is
better. On a 16-element axial bar whose exact first frequency is 25 860.97 Hz: consistent
25 895.53 Hz (**+0.134%**), lumped 25 812.76 Hz (**−0.186%**).

## The eigensolver, and why the direct factorization finally pays

Shift-and-invert Lanczos on `A^-1 M` with `A = K − sigma·M`, in the M inner product. The
operator's eigenvalues are `1/(lambda − sigma)`, so the frequencies nearest the shift are its
LARGEST — and extreme eigenvalues are what Lanczos converges to first.

**`FeaSolveMethod.Direct` records, honestly, that "factor once, solve many right-hand sides"
does NOT apply to the static solver, which factors and discards. Here it does.** One
factorization of `K − sigma·M` serves one back-substitution per Lanczos step;
`ModalSolveReport.Iterations` says how many, and on this project's fixtures it is 18–23 solves
off a single factorization for three to eight modes. No iterative linear solver is offered on
this path for exactly that reason — it would have to converge afresh for every one of those
steps.

Three implementation rules, each of which cost something:

- **Full reorthogonalization (two passes), and then locking and restarting.** Reorthogonalizing
  stops round-off manufacturing spurious duplicate eigenvalues; but it also makes a GENUINE
  multiplicity invisible, since a single-vector Krylov space contains one vector per
  eigenspace and a square shaft's two identical bending modes are a real, common multiplicity.
  Converged modes therefore join the deflation set and a fresh start vector is orthogonalized
  against them, so the next run's extreme eigenvalue is the second copy. The solver also
  targets **one more mode than asked for** and returns the lowest `wanted`, which is what
  gives a missed copy a run to appear in before the extra one is discarded. That machinery
  recovers a double (now MEASURED on a synthetic exact double, not merely reasoned) and
  provably stops one copy short of a triple; `ModalSolveOptions.BlockSize` is the answer past
  it — b vectors advanced per step carry a multiplicity up to b by construction, with the
  extra targeting scaled to b because an UNCONVERGED copy's Ritz value hides high in the
  spectrum, past the next distinct eigenvalues, where the contiguous prefix cannot shield it
  (measured: a block of four on a synthetic quadruple stopped at `{1, 1, 1, 2}` believing
  itself done, four iterations short of its own budget, until the target learned to carry b
  extras).
- **A CONTIGUOUS converged prefix, never "whatever has converged".** Ritz values converge from
  the extreme end inwards but not in lock step, so a run can have the second eigenvalue
  converged while the first is still moving. Accepting out of order returns eigenvalues 2 and
  3 as "the two lowest" — measured on a 19 440-DOF cantilever asked for ONE mode, it returned
  **4 997.9 Hz** for a first bending mode of **834.9 Hz**, a plausible number that happened to
  be the second bending pair.
- **Convergence is MEASURED.** The textbook `beta_m·|y_m|` bound describes the residual of the
  shifted, inverted operator — one transformation away from what a caller cares about. This
  computes `K phi − lambda M phi` and reports it (`ModalSolveReport.WorstResidual`, and per
  mode).

## Rigid-body modes are separated, not refused

`StructuralSolver` refuses an unrestrained model; `ModalSolver` keeps its zero-frequency modes,
because a modal analysis of a free-free body is perfectly well posed. **The same machinery
answers both** (`RigidBodyModes.Surviving`, extracted from the static solver rather than
restated), so a refusal there and a mode listing here cannot describe the same physics
differently.

They are deflated out of the Krylov space so they can never be reported as the first six
structural modes, `VibrationMode.Number` starts at 1 on the lowest mode that stores strain
energy, and `RigidBodyMode.Eigenvalue` is the **measured** Rayleigh quotient of the exact rigid
field — zero in exact arithmetic, and in practice a conditioning measurement of that model.

Because `K` is singular when rigid modes exist, the factorization takes a small negative shift
(`ModalSolveReport.Shift`, reported; escalated and re-reported if a shift will not factor). A
fully restrained model needs none and the report says **exactly zero** — that factorization is
literally the static solver's.

## Mode shapes have no amplitude and no sign

- `VibrationMode.Shape` is **mass-normalised** (`phi' M phi = 1`): the scale every modal
  identity is stated in, and a magnitude of one over the square root of a mass — not a
  displacement.
- The **published** field is rescaled to a peak nodal magnitude of exactly **1 model length
  unit**, labelled `"mode shape"` rather than `"mm"`, so `DeformScale = 8` means "the
  most-displaced node moves 8 mm".
- The **sign is pinned** (largest-magnitude component positive) so two solves agree bit for
  bit. A convention for reproducibility, not physics.

Field names are `ModalResults.FieldNames.Shape(n)` = `"Mode 1"`, `"Mode 2"`, … The frequency is
deliberately not in the name: a field name is a document handle a `FieldDisplay` stores and a
saved document round-trips, and a name carrying a computed number would stop resolving the
moment a parameter changed.

## Verification

Full tables are on the [docs page](../../docs/examples/fea-modal.md). The headline numbers,
all on structured Kuhn meshes:

- **Axial bar** (Poisson's ratio zero and transverse DOFs removed, so the 3D and 1D problems
  are identical and there is NO modelling gap): free-free `n/(2L)·sqrt(E/rho)` matched to
  **+0.021% / +0.081% / +0.170%** at 40 linear elements, every one above the exact value as a
  Rayleigh quotient over a subspace must be; fixed-fixed +0.025% / +0.095% / +0.194%.
- **Convergence order** on that bar: **2.04, 2.01, 2.00** (linear) and **4.13, 4.25, 4.12**
  (quadratic), against theory 2 and 4 — an eigenvalue converges at `O(h^2p)`.
- **Cantilever** 100×10×10, quadratic, against Euler-Bernoulli's `beta·L` = 1.875/4.694/7.855:
  **−0.07% / −4.34% / −9.98%**, the gap growing with the mode number because a solid has shear
  deformation and rotary inertia that beam theory does not. Refinement is monotone from above
  (852.10 → 838.30 → 834.92 → 833.78 Hz), and the degenerate pairs split by 0.043% / 0.076% /
  0.132% — a direct measurement of the discretization, since Kuhn's subdivision picks its
  diagonals by index order and no reflection preserves that.
- **Simply-supported** 100×12×8, `beta·L = n·pi`: within **+0.09% / +0.12% / +0.37% / +0.62%**
  of Timoshenko while diverging from Euler-Bernoulli by up to 8% — the clearest available
  statement that the divergence belongs to the theory.
- **Free-free**: six rigid modes at eigenvalues −1.1e-3 … 1.6e-3 against a first elastic
  eigenvalue of 6.84e8, i.e. **2.41e-12 of it at worst** and under 2.3e-3 Hz; the seventh mode
  (`beta·L = 4.730041`) at 4 162.1 Hz against Euler-Bernoulli's 4 253.3 (−2.14%).
- **Orthogonality**, with the products assembled independently of the solver:
  `phi_i' M phi_j − delta_ij` at **7.1e-15 / 7.6e-15** and
  `(phi_i' K phi_j − lambda_i·delta_ij)/lambda_i` at **5.8e-13 / 1.6e-11** for linear /
  quadratic.
- **Effective mass**: a uniform cantilever's classical first-mode participation is 0.6132;
  measured **61.09%**.

### A modelling trap the simply-supported fixture cost

The axial rigid translation has to be removed somehow, and **pinning `u_x` at a single node —
exactly what a static 3-2-1 restraint does — is wrong in dynamics.** In statics a single-node
restraint is a local disturbance Saint-Venant confines to its neighbourhood; in dynamics it
creates a genuine mode in which the whole body translates axially while a few elements around
the pinned node deform, at a frequency set by the MESH rather than by the beam. Measured: a
spurious mode at **5 540 Hz**, sitting between the second and third bending modes and carrying
96% of the axial effective mass. Holding `u_x = 0` along the beam's own centroidal line removes
the axial family instead and adds no bending stiffness at all, because pure bending has
`u_x = −z·w'(x)` measured from the neutral axis and that is identically zero on it.

## Stress stiffening (a preloaded modal solve)

`ModalSolveOptions.Prestress` takes a completed static solve and adds its geometric stiffness,
so the eigenproblem becomes `(K + s·Kg) phi = lambda M phi` — the guitar string, the spinning
blade, the preloaded bolted joint. `PrestressScale` multiplies the stress field without
re-solving, because a linear solve's stress is homogeneous of degree one in its loads; a
frequency-versus-load curve is therefore ONE static solve and N eigen-solves.

```csharp
var statics = StructuralSolver.Solve(loadedModel);          // the preload case
var results = ModalSolver.Solve(model, new ModalSolveOptions
{
    ModeCount = 3,
    Prestress = statics,
    PrestressScale = 0.5,                                   // half the reference load
});
```

The prestress must have been solved on the **same `AnalysisMesh` instance** — the two
assemblies share a node numbering, not a shape, and a check on counts or positions would let
a differently numbered mesh of the same body through. The supports may differ. A scale of
exactly zero skips the combination entirely, so an unprestressed answer is **bit-identical**
whichever way it was asked for.

Past the critical load there is no vibration problem left: `K + s·Kg` is singular at
`s = lambda_cr` by definition, and the factorization refuses by name rather than reporting the
square root of round-off.

## Limitations

- **These are UNDAMPED natural frequencies**, which is what a modal analysis means; damping
  enters per mode where it is used (see `ModalDamping`), never as a matrix here.
- **No transient dynamics.** Frequency response is covered by `HarmonicSolver`; direct
  time integration needs a different stepping loop.
- **Multiplicity three and above wants `ModalSolveOptions.BlockSize`.** Locking and restarting
  recovers the second member of a degenerate pair and provably stops one copy short of a
  triple (pinned on a synthetic exact triple: `{f1, f1, f2}` for a truth of `{f1, f1, f1}`,
  every returned mode a genuine eigenpair); a block of size b carries a multiplicity up to b
  by construction. The default stays 1 — the incumbent path byte for byte — because real
  meshes split their theoretical multiplicities.
- **A near-critical prestress raises the residual floor**, because `K + s·Kg` is nearly singular
  by construction: measured on the pinned-pinned column, 2.99e-10 unloaded and 3.09e-9 at 90% of
  the critical load. The mode is right either way — its frequency lands on the closed-form law
  to nine digits — so the answer is to raise `ModalSolveOptions.Tolerance`, which the refusal
  now says explicitly because it reports the candidate it stalled on.
- **Sliver elements** are the same binding constraint every solver here has, refused by name by
  the same shared guard.

---

# Buckling (linear eigenvalue stability)

`(K + lambda·Kg) phi = 0` — the multiple of a reference load case at which a structure loses
stability, from the geometric stiffness that load case's own stress field produces. Docs page:
[`docs/examples/fea-buckling.md`](../../docs/examples/fea-buckling.md).

```csharp
var model = new StructuralModel(AnalysisMesh.Quadratic(tets), Materials.Steel);
model.Fix(Facets.Tag(baseFace));
model.Force(Facets.Tag(topFace), new Vector3d(-1000, 0, 0));    // the reference load

var statics  = StructuralSolver.Solve(model);                   // the prestress
var buckling = BucklingSolver.Solve(statics, new BucklingSolveOptions { ModeCount = 2 });

double criticalLoad = buckling.CriticalLoadFactor * 1000;       // scale YOUR load by it
```

**The load factor multiplies the whole load case, not one number in it.** A linear solve's
stress field is homogeneous of degree one in every load it was given — forces, pressures,
gravity, a thermal field, an enforced displacement — so the eigenvalue is a factor on the case
as a whole. Nothing here is called "the critical load", because a strut pushed from both ends
has an applied resultant of exactly zero and a scalar critical load would have to guess which
half of the load case was meant.

## The geometric stiffness has the mass matrix's shape

`Kg_ab = integral(grad N_a · sigma · grad N_b dV)` is a SCALAR per node pair replicated onto
the 3×3 identity block — exactly `TetElement.ConsistentMass`'s structure, and for a reason
rather than by coincidence: an isotropic inertia couples no two axes, and one stress tensor
contracts each of the three displacement components identically. So the assembly loop is the
mass matrix's with a different integral in it.

Three things follow, and the tests pin all three:

- **Every row sums to exactly zero** (measured 4.6e-17 / 3.9e-16 relative for linear /
  quadratic), because the shape functions are a partition of unity. Physically: a rigid
  translation of a prestressed body does no work against the prestress.
- **The rule is `TetQuadrature.ForGeometric`** — degree `3(p-1)`, so one point for a 4-node
  element and the degree-3 rule for a 10-node one, sitting between `For`'s `2(p-1)` and
  `ForMass`'s `2p`. That rule's negative centroid weight is a defect for a matrix that must be
  positive definite and is **harmless here**, since a geometric stiffness is indefinite by
  nature; exactness is all that is required, and it is checked against the 15-point degree-5
  rule (agreement 8.8e-15 relative).
- **The prestress comes from `StructuralResults`' own recovery seam**, not from a constitutive
  law restated here — so thermal-strain subtraction is included and thermal buckling is right
  rather than nearly right.

## The indefinite right-hand side changes the shift STRATEGY, not the shift

This is the decision the whole solver turns on.

A modal solve puts a small **negative** shift under the spectrum so `K − sigma·M = K + |sigma|·M`
is positive definite and factorable. That works only because `M` is positive semi-definite:
adding a positive multiple of it can only help. **`Kg` is indefinite** — tension stiffens,
compression softens, and one bending prestress does both at once — so `K − sigma·Kg` is a
definite matrix plus a *signed* multiple of an indefinite one, and no sign of sigma makes it
reliably factorable. The modal escalation loop is worse than inapplicable: multiplying a
failing shift by 100 pushes `K − sigma·Kg` **further** from definiteness, so a solver reusing
it would spend its retries getting steadily more wrong before refusing.

**The free parameter is not the shift; it is the metric.** Lanczos needs its inner product to
be positive definite, and `A^-1 B` is self-adjoint in the A inner product *and* in the B inner
product whenever A and B are symmetric — `<A^-1 Bx, y>_A = x'By = <x, A^-1 By>_A`, and likewise
in B. A modal solve runs in M's because M is the definite matrix there. Here the definite
matrix is **K**, so the iteration runs in the K inner product with the operator `K^-1(-Kg)`,
and the matrix that gets factorized is **K itself** — literally the static solver's
factorization, on every model, with nothing to choose.

**Once the metric is K the shift has no work left to do.** Shift-and-invert exists to turn
*interior* eigenvalues into extreme ones, and the reciprocal substitution has already done it:
`K^-1(-Kg)` has eigenvalues `theta = 1/lambda`, so the smallest critical factor — the only one
an engineer wants — is the operator's LARGEST eigenvalue, which is what Lanczos converges to
first with no transformation at all. `sigma = 0` is the answer rather than a fallback, and
`BucklingSolveReport.Shift` exists to say so.

ARPACK reaches the same place from the other side: its "buckling mode" uses
`OP = (A − sigma·M)^-1 A` with metric `B = A`, and **requires `sigma != 0`** because at zero
that operator is the identity. Inverting the other matrix removes the requirement along with
the choice.

`LanczosEigen` therefore took exactly one generalization — the metric became a parameter
separate from the right-hand matrix — and passing the same matrix for both takes the modal
path's arithmetic operation for operation, which is what keeps its output identical to the bit.

**Only positive load factors are reported.** A negative theta is a factor at which the
*reversed* load case buckles, which is a real answer to a different question, so the
descending-theta walk stops at the first non-positive value rather than skipping it — the same
contiguous-prefix rule that makes "the lowest k" mean it. A load case with no positive factor
at all is refused by name.

**And the indefiniteness caveat is about the general case, not about a column.** Under a
uniform uniaxial compression the element integral collapses to
`u'Kg u = -s·integral(|du/dx|²)`, non-positive for every displacement field — so `-Kg` is
positive semi-definite, the pencil is definite, and every factor is positive. That is why the
Euler cases converge as cleanly as they do; the machinery above is what makes a bending or
mixed prestress work too.

## The residual has a floor, and it is worse here than in a modal solve

`BucklingSolveOptions.Tolerance` defaults to **1e-7**, two decades looser than the modal
solver's, and that is a measurement rather than a hedge. The residual
`|K phi − lambda Kg phi| / (|K phi| + |lambda||Kg phi|)` is a *total* cancellation of two
products, so its floor is about `eps·kappa(K)` relative to a smooth mode's own energy. A modal
solve escapes that because its Krylov vectors come out of `K^-1 M`, which **smooths** — the
high-frequency content `K` amplifies is suppressed before `K` ever sees it. This solver's
operator is `K^-1 Kg`, and a geometric stiffness is derivative-like, so the two halves roughly
cancel in frequency content and the Lanczos vectors keep the part that sets the floor.

Measured on the pinned-pinned column at slenderness 69: every mesh up to 9 310 DOF reaches
1e-10, and a 23 166-DOF one stalls at **1.76e-9** — so a 1e-9 default would refuse an ordinary
refinement of an ordinary model. Nothing is given up: an eigenvalue is accurate to roughly the
SQUARE of the residual over the spectral gap, and the same 23 166-DOF column accepted at 1e-5
returns 15 437.12 N against the 9 310-DOF mesh's 15 437.99 N.

**Whether a K^-1-norm residual removes the floor was measured, and the answer is a lesson**
(`FeaBenchmark.WhetherAKInverseNormResidualEscapesTheBucklingFloor`): it escapes the
cancellation — 8.2e-11 on the same vector whose standard residual stalls at 1.9e-9, roughly
mesh-independent where the standard floor grows with `kappa(K)` — because
`K^-1·r = -lambda·(T phi - theta phi)` is exactly the shift-invert operator's residual, which
full reorthogonalization drives to round-off. That is also why it cannot be the acceptance
test: it saturates at its own ~1e-10 floor within a dozen Lanczos steps, while the standard
measure still reads 1.2e-7, so past that point it cannot distinguish a 1e-7-grade vector from
a 1e-9-grade one — a measure that has stopped measuring. The eigenvalue drift between the
earliest and the most converged acceptance is 1e-15…5e-13 relative on all three refinements,
so a tighter default was never going to buy accuracy; the default stands, and the reported
residual keeps its backward-error meaning.

The refusal when even that is unreachable **names which of the two causes it hit** —
`LanczosEigen` now reports the best candidate it saw but did not accept, so "no positive factor
exists" and "a factor was there and the tolerance was in the way" are different messages. They
want opposite responses from the user, and an empty list cannot tell them apart.

## Verification

Full tables are on the [docs page](../../docs/examples/fea-buckling.md). A 120 × 6 × 6 steel
column (slenderness `L/r` = 69.3) with Poisson's ratio exactly zero, 24 × 2 × 2 quadratic
elements, against `P = pi²EI/(K·L)²`:

| ends | K | Euler | Engesser | measured | vs Euler | vs Engesser |
|---|---|---|---|---|---|---|
| pinned–pinned | 1.0000 | 15 544.6 N | 15 468.3 N | 15 440.3 N | −0.671% | **−0.181%** |
| fixed–free | 2.0000 | 3 886.2 N | 3 881.4 N | 3 879.6 N | −0.169% | **−0.046%** |
| fixed–pinned | 0.6992 | 31 800.4 N | 31 482.6 N | 31 338.4 N | −1.453% | **−0.458%** |
| fixed–fixed | 0.5000 | 62 178.5 N | 60 974.9 N | 60 550.5 N | −2.618% | **−0.696%** |

Euler's derivation has no shear deformation and a three-dimensional solid has it, so the
measured load converges BELOW the Euler value by Engesser's ratio
`1/(1 + P_E/(kAG))` — the buckling twin of the Timoshenko correction the modal beam fixtures
quote, and the reason the fixed–fixed row (whose Euler load is four times larger, so whose
shear correction is four times bigger) is the furthest from Euler and no further from the
truth.

- **Refinement is monotone from ABOVE** — 16 122.23 / 15 553.67 / 15 449.65 / 15 438.52 N at
  4/8/16/32 elements along the length. That is a theorem rather than a property of the fixture:
  the discrete factor is a Rayleigh quotient minimised over the element subspace, and it holds
  STRICTLY here because `nu = 0` makes the prestress field exactly `-P/A` (measured 5.45e-13
  relative deviation from uniform), so `Kg` is exact and not merely accurate.
- **The buckled shape is the half sine**, worst deviation **2.4e-6** over 49 centroidal nodes —
  which separates "an eigenvalue near the Euler load" from "the Euler buckling mode".
- **The factor is exactly inverse in the reference load**: doubling the load halves the factor
  so their product is unchanged to **0.00e0** relative, which is the cheapest possible check
  that `Kg` is linear in the prestress.
- **The square section's modes come in degenerate PAIRS** (the column can bow in Y or Z),
  splitting by 0.0046% – 0.0714% — so the locking-and-restarting machinery the modal solver
  needed is exercised on a second physics.
- **Stress stiffening and buckling agree to nine digits.** For a pinned-pinned beam,
  `omega²(P)/omega²(0) = 1 + P/P_cr` exactly in Euler-Bernoulli theory, and measured on the
  discrete 3D system with `P_cr` taken from the buckling solve it holds to **7.4e-10 relative**
  from `P = -P_cr` (tension) through `P = +0.9 P_cr`:

  | P/P_cr | −1.0 | −0.5 | 0 | +0.25 | +0.5 | +0.9 |
  |---|---|---|---|---|---|---|
  | f (Hz) | 1 377.77 | 1 193.19 | 974.23 | 843.71 | 688.89 | 308.08 |
  | ω²/ω²(0) | 2.000000 | 1.500000 | 1.000000 | 0.750000 | 0.500000 | 0.100000 |
  | relative error | −7.4e-10 | −4.1e-10 | 0 | 1.9e-10 | 4.3e-10 | 6.5e-10 |

  It is that tight because the column's buckling shape and its first vibration shape are the
  same half sine, so the ratio of two Rayleigh quotients over one vector is the ratio of their
  numerators. One table checks the geometric stiffness, the load factor and the stress-stiffened
  modal path against each other.

### Linear tetrahedra are unusable for buckling, and the gap is measured

4-node tets are known to be too stiff in bending, and a buckling load is a RATIO of a bending
stiffness to a geometric softening — so the over-stiffness enters undiluted instead of being
averaged against anything. Measured on the same pinned-pinned column, against a true 15 468 N:

| mesh | linear | quadratic |
|---|---|---|
| 8×1×1 | 171 602 N (**+1 009%**) | 15 554 N (+0.55%) |
| 16×2×2 | 55 108 N (+256%) | 15 450 N (−0.12%) |
| 32×3×3 | 26 137 N (+69.0%) | 15 438 N (−0.20%) |
| 48×4×4 | 20 402 N (+31.9%) | — |

Where the static cantilever's tip deflection is 14% low at 12 288 linear elements, the same
elements put a column's critical load an **order of magnitude** high on a coarse mesh and are
still 32% high at 3 550 DOF, where the quadratic answer converged to 0.2% with 414. Use
`AnalysisMesh.Quadratic` for any stability analysis.

## Limitations

- **Linear buckling only.** The load factor is the eigenvalue of a problem linearised about
  ONE static state; it says nothing about post-buckling behaviour, imperfection sensitivity, or
  a structure whose prestress redistributes as it deforms. A shell that is imperfection-
  sensitive can buckle at a fraction of this number, which is a property of the theory and not
  of the implementation.
- **The prestress is the reference solve's, unscaled by the eigenvalue.** That is what "linear"
  means here: `Kg(lambda·sigma) = lambda·Kg(sigma)` is assumed, which is exact for a linear
  static solve and is precisely the assumption the `LoadFactorIsInverselyProportionalToThe
  ReferenceLoad` test pins.
- **Positive factors only** (see above); a load case that buckles only when reversed is refused
  rather than reported with a negative number nobody asked for.
- **Multiplicity three and above** takes the modal solver's answer —
  `BucklingSolveOptions.BlockSize`, the same block iteration through the same `LanczosEigen`,
  since axisymmetric parts buckle in degenerate families exactly as they vibrate in them.

---

# Damping, and frequency response

## Rayleigh damping — and precisely what it does not cover

`C = alpha·M + beta·K`, whose whole point is that the UNDAMPED modes still diagonalise it:
`phi' C phi = diag(alpha + beta·omega_n²)`, so the equations separate into one scalar
oscillator per mode and the damping is a per-mode RATIO
`zeta_n = alpha/(2·omega_n) + beta·omega_n/2`.

```csharp
var damping = RayleighDamping.FromRatios(50, 0.02, 500, 0.02);   // 2% at both ends
double zeta  = damping.RatioAtFrequency(180);                    // 1.05% in between
```

The ratio curve is a **U** — the mass term falls as `1/omega` and the stiffness term rises
linearly — with minimum `sqrt(alpha·beta)` at `omega = sqrt(alpha/beta)`. Two fitted points
pin it everywhere, and **everything outside the fitted range is damped MORE than either fitted
value**, often much more: fitting 3% at 20 Hz and 1% at 800 Hz gives 11.5% at 5 Hz and 6.2% at
5 kHz. `HarmonicResponse.DampingRatios` reports the ratio used for every mode for exactly that
reason. A fit with no solution — a ratio falling faster than `1/omega` between the two points —
comes out with a negative coefficient, i.e. damping that ADDS energy, and is refused by name
rather than returned.

**What is not covered, stated rather than implied away.** Proportional damping is the special
case in which one damping matrix happens to be diagonalised by the undamped real modes.
Physical damping usually is not: a discrete dashpot, two materials with different loss factors
in one model, viscoelasticity, joint friction and structural (hysteretic) damping all leave
`phi' C phi` with off-diagonal terms. When that happens the damped system's modes are no longer
the undamped ones — the eigenproblem becomes the **quadratic**
`(lambda²M + lambda·C + K) phi = 0`, whose eigenvalues and eigenvectors are complex and whose
standard solution linearises it into a `2n`-dimensional state-space problem in a non-symmetric
matrix pair. **That is a different solver, not a bigger version of this one**, and nothing here
attempts it. `ModalDamping` also takes a flat ratio (`Uniform`) or a measured table
(`PerMode`), both of which are proportional by construction.

**No damping matrix is ever assembled.** Every consumer wants `zeta_n`, and forming `C` in
order to project it back down to the same numbers would be arithmetic with nothing to show for
it. That is a design statement, not an omission.

## Frequency response by modal superposition

Each mode is a scalar oscillator, so the steady-state response to a harmonic load is
`q_n(W) = F_n / (w_n² - W² + 2i·zeta_n·w_n·W)` with `F_n = phi_n' f`, and the whole sweep is
one dot product per mode plus a complex division per (mode, frequency) pair. Nothing is
assembled and nothing is factorized.

```csharp
var modes    = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 6 });
var statics  = StructuralSolver.Solve(model);              // for the truncation correction
var response = HarmonicSolver.Solve(modes, new HarmonicSolveOptions
{
    Frequencies      = HarmonicSweep.Logarithmic(10, 5000, 400),
    Damping          = ModalDamping.Rayleigh(50, 0.02, 2000, 0.02),
    StaticCorrection = statics,
});
File.WriteAllText("sweep.csv", response.ToCsv(tipNode, axis: 2));
```

**The load comes from the modal model's own applied forces**, since every load type reduces to
consistent nodal forces when it is applied — so one model carries supports, loads and the modes
computed from it, and there is no second place for a load to be specified and forgotten. A
thermal load is refused by name, because it enters a static solve as an element integral rather
than a nodal force and accepting it would silently drop it. A free-free model is refused too: a
rigid mode's `F_n/(0 - W²)` grows without bound as the frequency falls, which is a true
statement about a body that simply accelerates away and a useless one to plot.

**Damping is required rather than defaulted.** A default would be this project inventing a
material property, and the one honest default — none — makes every resonance infinite. Say
`ModalDamping.None` and it is allowed; an undamped mode driven at exactly its own frequency
then returns a non-finite modal coordinate, left alone rather than clamped to a large number
nobody chose.

**Truncation is a correction, not a caveat.** `StaticCorrection` switches on the
mode-acceleration form
`u(W) = u_static + sum_n phi_n F_n [1/(w_n² - W² + 2i·zeta·w_n·W) - 1/w_n²]`, whose bracket
vanishes at `W = 0` — so the response is EXACTLY the static answer there however few modes were
kept, and the missing modes' static flexibility is carried at every other frequency.
`TruncationError` reports what the plain sum would have missed, and is **NaN without a static
solve**, because then it is not small, it is unknown.

**What a DIRECT solve buys, and when it is actually needed** (filed, not built): factorizing
`(K - W²M + i·W·C)` at every frequency costs a complex factorization per point — hundreds of
times this — and is the only option in three cases, none of which this can express. Damping
that is not proportional (the modes stop diagonalising C, and the eigenproblem becomes
quadratic); material properties that vary WITH frequency (a viscoelastic modulus — the modal
basis itself would change per point); and a load whose spatial distribution changes with
frequency.

## Verification

- **The resonant amplification is `1/(2·zeta)`**: measured **25.006 against 25.000** (0.02%) on
  a tip-driven cantilever at 2% damping. The reference is that MODE's static contribution, not
  the structure's whole static deflection — the other five modes supply 3.01% of this
  cantilever's static tip, and dividing by the whole thing reads as a 3% solver error that is
  entirely a modelling mistake in the measurement.
- **The half-power bandwidth is `2·zeta`** — the standard way a damping ratio is measured from
  a response, run in reverse: **2.0005% / 4.0034% / 10.0541%** against 2% / 4% / 10%, i.e.
  0.02% / 0.08% / 0.54% relative.
- **The phase lags the load by a quarter turn at resonance**: 90.073°, read AT the mode's own
  frequency rather than at the sweep's peak sample — those are two steps apart at this
  resolution and detuning by 0.04% of the frequency rotates the phase 1.15° at 2% damping, so
  probing the peak would measure the sweep instead of the response.
- **The static correction is exact**: a ONE-mode response at zero frequency reproduces the full
  static tip deflection to **1.8e-16 relative**, where the uncorrected one-mode sum is 3.01%
  short. Truncation falls monotonically with modes kept — 3.079% / 3.079% / 0.532% / 0.191% at
  1 / 2 / 4 / 8 modes.
- **A Rayleigh fit reproduces both ratios to 1e-15**, its minimum matches `sqrt(alpha·beta)` at
  `sqrt(alpha/beta)` against a search over the curve, and the ratios reach the response
  unchanged.
- **An unexcited mode contributes nothing however close its frequency**: driving the
  rectangular cantilever along Z leaves the Y-bending family at **8.6e-4 and 2.6e-3** of the
  largest modal force. Not exactly zero, and the reason is worth knowing — Kuhn's subdivision
  picks its diagonals by index order and no reflection preserves that, the same asymmetry the
  modal beam tests measure as a degenerate pair's splitting.

## Limitations

- **Proportional damping only** (above). Non-proportional damping is a quadratic eigenproblem
  and a different solver.
- **Harmonic (steady-state) response only** on this path. Direct time integration is a separate
  solver - see [Transient dynamics](#transient-dynamics-direct-time-integration) below.
- **Nodal-force excitation only.** Base acceleration would ride the participation factors the
  modal results already carry; a load whose spatial shape changes with frequency needs the
  direct solve.
- **No residual-vector basis augmentation.** The mode-acceleration correction handles the
  static part of what the truncated modes miss, which is most of it; a residual VECTOR (the
  static response orthogonalised against the kept modes, added to the basis) would handle the
  rest and is filed.

# Transient dynamics (direct time integration)

`TransientSolver.Solve` steps `M·a + C·v + K·u = f(t)` forward by the **Newmark / HHT-alpha**
family, over the same `AnalysisMesh`, the same materials and the same supports and `Facets`
selectors everything else here takes. Docs: [`fea-transient.md`](../../docs/examples/fea-transient.md).

**Different in kind from modal superposition, not a slower version of it.** `HarmonicSolver`
answers "what does this do under a steady sine at each of these frequencies" by projecting onto
a handful of modes; this answers "what does it do next" for an arbitrary load history, needs no
modes at all, and is the route a nonlinear solve would eventually wrap. The price is that it
computes every instant whether or not anything is happening.

## One factorization for the run - the third place that is genuinely true

The effective stiffness `a0·M + (1+alpha)·a1·C + (1+alpha)·K` depends on the step, the scheme
and the model and on nothing else, so a CONSTANT step lets it be factored once and every step
after that is a back-substitution. That is the argument `ThermalSolver.SolveTransient` and
`StructuralSolver.SolveAll` already make, and this is its third instance.

The **second** factorization is the one that is easy to skip and must not be:

```
a(0) = M^-1 . (f(0) - C.v(0) - K.u(0))
```

is a solve against **M**, not against K. A body released from a displaced position, or one
whose load is already on at `t = 0`, is accelerating at that instant; starting from `a(0) = 0`
puts a spurious half-step into the answer whose symptom - a startup wobble that decays - looks
exactly like physics. It is skipped only when that right-hand side is EXACTLY zero, and
`TransientSolveReport.Factorizations` reports which happened.

## The damping matrix is never assembled - and the backlog entry said it would be

todo.md filed this solver as "the ONE consumer that genuinely wants `C` as a matrix rather than
as per-mode ratios, so it is where `RayleighDamping` would first be assembled". **That is
wrong, and the correction is worth keeping.** Proportional damping means every appearance of C
is one of two things:

- a PRODUCT, `C.x = alpha.(M.x) + beta.(K.x)` - two matrix-vector products this solver already
  performs, and cheaper than one product against an assembled C would be, because the mass
  matrix's blocks are scalar multiples of the identity and M therefore has far fewer stored
  entries than K;
- a scalar MULTIPLE folded into the effective stiffness, which collects as `(...)M + (...)K`
  through `FeaAssembly.Combine`'s two-coefficient form.

So no damping matrix exists anywhere in this project, and `ModalDamping`'s claim that none does
stands unqualified. A NON-proportional model would genuinely need one - and would also need a
vocabulary for stating one, which does not exist here either.

## An unrestrained body is ACCEPTED, and that is a decision

`StructuralSolver` refuses one by name, because `K` alone is singular for a free body. The
effective stiffness carries `a0.M` with `a0 = 1/(beta.dt^2) > 0`, and a consistent mass matrix
is positive definite, so the stepping matrix is positive definite whatever the supports do. A
free body under a transient load flies away, which is the answer - the same exemption the
thermal transient makes for an insulated body. Pinned by a test so that nobody copies the
static guard across.

## The scheme is ONE value

`TimeIntegration` carries beta, gamma and alpha together, and every member is a named factory
that computes its own coefficients - an enum beside two loose doubles would let a caller state
a name and a pair that contradict it. Both stability conditions are enforced at construction
rather than documented:

| refused | why |
| --- | --- |
| `gamma < 1/2` | NEGATIVE numerical damping: the amplitude grows at every step size |
| `2.beta < gamma` | conditionally stable, and the safe step needs the largest eigenvalue of `K.phi = lambda.M.phi`, which nothing here computes |
| `alpha` outside `[-1/3, 0]` | below it HHT drops to first order; positive alpha amplifies |
| `TimeIntegration.CentralDifference` | explicit integration pays only with a DIAGONAL mass matrix, and row-sum lumping is itself refused for 10-node elements (`-V/20` at every corner) - so an explicit step here would still solve a linear system |

`HilberHughesTaylor(0)` IS `AverageAcceleration`: beta = 1/4 and gamma = 1/2 fall out of HHT's
own formulas, and the two produce **bit-identical** output (asserted over 4824 values of a
damped, loaded, 200-step run), which is what every `alpha != 0` exact-zero branch in the stepper
exists to guarantee.

## The energy balance is an IDENTITY

For `AverageAcceleration` the trapezoidal update relations give
`u(n+1) - u(n) = (dt/2)(v(n) + v(n+1))` and `v(n+1) - v(n) = (dt/2)(a(n) + a(n+1))`, from which

```
E(n+1) - E(n) = (dt/4) . (v(n)+v(n+1))' . [M(a(n)+a(n+1)) + K(u(n)+u(n+1))]
```

and putting the equation of motion at both ends into the bracket turns it into exactly the work
of the load plus the viscous dissipation. Nothing is approximated, so
`TransientSolveReport.EnergyBalanceResidual` is a defect detector rather than a tolerance -
**4.4e-13** relative over 3000 steps undamped, **1.4e-13** with work 1.44 and dissipation 6.28
both large. For a dissipative scheme the same number becomes the MEASUREMENT: it is the energy
the algorithm removed.

## Verification

The fixture is a model whose reduced system is exactly `1 x 1` - every degree of freedom
restrained but one - so there is **no discretization error at all**. That is the idiom the
modal work recorded as wrong in dynamics, and the distinction matters: that warning is about
MODELLING, where a single-node restraint creates a spurious mode whose frequency belongs to the
mesh. Here nothing is being modelled; the reduced system is made scalar deliberately so that
`m.q'' + c.q' + k.q = f(t)` IS the finite element problem. Its stiffness comes from a static
solve and its frequency from a modal solve, so the references are never the transient's own
arithmetic.

| case | closed form | measured |
| --- | --- | --- |
| step load, dynamic amplification | exactly 2 | **2.0000** |
| damped step, zeta = 5% | 1.8544679 | **1.8544825** |
| free vibration, 10 periods at 100 steps/period | 2.067% | **2.014%** |
| initial velocity (impulse) | 2.067% | **2.066%** |
| damped step response, worst error | 0.06051% | **0.06058%** |
| damped free vibration, worst error | 0.1513% | **0.1513%** |
| harmonic drive, closed form / `HarmonicSolver` | - | **0.027%** / **0.027%** |
| time order: Newmark / HHT / gamma = 0.6 / real mesh | 2 / 2 / **1** / 2 | **2.000 / 2.000 / 0.9925 / 2.000** |
| single-mode seed: leak out of mode 1 / mode 2 | 0 | **8.5e-11** / **7.9e-11** |
| measured vs predicted algorithmic frequency ratio | - | **eleven digits** |
| free body: `sum(M.a)` against the applied force | exact | **1.2e-11** |
| spectral radius at rho = 0.95/0.9/0.8/0.6/0.5 | `(1+alpha)/(1-alpha)` | within **1e-4** relative |

The phase-error predictions are derived rather than quoted: Newmark's `(1/4, 1/2)` amplification
eigenvalues are `(1 - W^2/4 +/- iW)/(1 + W^2/4)` with `W = omega.dt`, whose modulus is exactly 1
(hence the preserved amplitude) and whose argument is `W - W^3/12`, so the algorithmic frequency
is `omega.(1 - W^2/12)` and the displacement error after N periods is `amplitude.2.pi.N.W^2/12`.
For a damped run that product peaks at `t = 1/(zeta.omega)` with value
`amplitude.W^2/(12.zeta.e)`.

### Two fixture traps, both found by an impossible measurement

**(a) A convergence error read at a MAXIMUM of the cosine reports double the order.** The exact
solution is stationary there, so a phase lag `d` enters as `u0.d^2/2` rather than `u0.d`, and an
`O(dt^2)` scheme measured **3.9997** against a theory of 2 (HHT measured 3.18, a mixture with
its own amplitude term). Moving the probe to a QUARTER period fixes the phase reading and
breaks the other half: there the amplitude multiplies zero, so the FIRST-order control - the
row whose whole job is to prove the study can tell 1 from 2 - measured **1.344**. The error has
to be a norm over the run, sampled on a grid common to every refinement level. Note that the
trap is invisible to exactly the test that exists to detect a broken study.

**(b) HHT's amplification matrix is DEFECTIVE in the high-frequency limit.** A two-point energy
ratio measured the spectral radius **4.138% high at every radius** and converged to that offset
rather than drifting, which ruled out "not asymptotic yet" and looked like a wrong
`alpha`-to-`rho` formula. Eliminating the acceleration leaves a 2x2 block with trace
`2 - 4/(1-alpha)` and determinant `((1+alpha)/(1-alpha))^2`, whose discriminant `trace^2 - 4.det`
is **identically zero for every alpha**: a double real eigenvalue at `-rho_inf` with one
eigenvector, so the state decays as `n.rho^n` and the energy as `n^2.rho^(2n)`. Dividing the
`n^2` out is the whole correction and it is exact - `(30/20)^(2/20) = 1.041380`, the measured
offset to six digits. The formula was right; the estimator was not.

## Limitations

- **Linear only.** The stiffness is evaluated once about the undeformed configuration and never
  updated, so contact, plasticity, large deformation and follower loads are outside it. Each
  makes the problem a nonlinear solve WRAPPING this one, with a residual iteration inside every
  step - a different solver rather than an option.
- **Constant step.** Adaptive stepping would refactor at every change, which is the entire cost
  of the method.
- **One spatial load pattern** scaled by one scalar history. A superposition of patterns with
  independent histories needs a LIST proven to share one stiffness matrix, which is exactly
  `StructuralSolver.SolveAll`'s contract, and is filed.
- **No base excitation.** A prescribed displacement is held CONSTANT for the run and is not
  scaled by the load factor; a support whose motion is a history of its own is a different
  feature.
- **Proportional damping only**, as everywhere here.
- **Consistent mass only.** `MassLumping` exists for the modal solver, where consistent and
  lumped bracket the truth; a diagonal M buys nothing on this path because the effective
  stiffness carries K and has to be factored whatever M looks like.

# Fatigue (S-N life and safety factors)

Stress-life post-processing over two static load cases — **arithmetic over results that
already exist, not a solver**. Docs page: `docs/examples/fea-fatigue.md`.

```csharp
var cases = StructuralSolver.SolveAll([maxCase, minCase]);      // ONE factorization
var fatigue = FatigueAnalysis.Evaluate(cases[0], cases[1], FatigueMaterials.Steel1045);
Console.WriteLine(fatigue);          // min safety factor, where, how many finite lives
foreach (var field in fatigue.SampleOnto(surface))
    part.AddResult(field);           // safety factor + log10 life + the decomposition
```

## The loading model, and the scalar it stands on

The two cases are the **extremes of one proportional load history**, which is what makes a
scalar equivalent stress per node capture the cycle; `SolveAll` returns exactly such a pair
from one assembly and one factorization. The scalar is the **signed von Mises** — the
magnitude carrying the sign of the hydrostatic trace — so equal-and-opposite cases read
R = −1 rather than a pulsating magnitude. The convention is a decision with its reasons
recorded on `FatigueAnalysis.SignedVonMises`: the first-principal alternative needs an
eigensolve and JUMPS when two nearly equal principals of opposite sign swap magnitude,
the trace is linear in the stress so its sign boundary is a fixed plane, and in the
uniaxial state the S-N data was measured in the two agree exactly. Its blind spot is
structural rather than a defect: a reversed **pure shear** cycle is invisible to ANY
scalar signed equivalent (negating a pure shear tensor is a rotation of it), which is
precisely the case critical-plane methods exist for — see the refusals below.

Results on different meshes, or answering with different `Recovery`/`Averaging`
settings, are refused by name: differencing a `Direct` field against a `Superconvergent`
one would book the recovery gap as alternating stress.

## The catalogue: `SnCurve` / `FatigueMaterials`

The Basquin line `sigma_a = sigma'_f·(2N)^b` plus the ultimate strength the mean-stress
corrections anchor on. Six rows transcribed from the SAE J1099 / Dowling compilations,
**flagged verify-against-datasheet** exactly as `StandardHoles`' Trisert table is —
polished-specimen constants, no Marin surface/size/reliability factors, and the authority
is the datasheet for your material condition. Two decisions:

- **The endurance limit is DERIVED, never stored beside the line** — a steel's limit is
  the line at its own 10⁶-cycle knee, so the two cannot drift (the fine-pitch tap-drill
  rule), and beyond the knee the curve is flat at that value.
- **The aluminium rows carry no endurance limit because the material has none** — a real
  metallurgical distinction the API keeps: below its limit a steel node lives forever,
  while a safety factor for aluminium needs a stated `FatigueOptions.DesignLife`,
  refused by name without one.

Constants are stored in the form a human can check — MPa, which is both the datasheet
unit and the model unit, so unlike the density lesson there is no conversion for a
transcription test to hide behind.

## Mean-stress corrections, life, and the spellings

`MeanStressCorrection.Goodman` (default, the conservative line), `Gerber` (the parabola),
`None`. Three structural behaviours, pinned by test: at zero mean both are the identity
EXACTLY; at a mean of exactly S_ut the allowable alternating stress is EXACTLY zero; a
compressive mean takes **no benefit by default** (crediting compression requires
confidence it survives at the crack-starting surface for the whole service life, so every
standard tool defaults to none). A mean at or beyond S_ut is STATIC failure and lands on
the life floor.

The published fields each carry a deliberate spelling:

- **Safety factor** — the radial (load-multiplier) factor: scale the whole history by it
  and the node sits exactly ON the line, which is also how it is verified (re-solve with
  the loads scaled by the measured factor; the minimum reads 1.0 to 1e-9). NaN where
  there is no fatigue mechanism at all (zero amplitude, no tensile mean) — the no-value
  spelling, never an infinity that would poison a legend's range.
- **Life as log10(cycles)** — the log because lives spread over decades and the colour
  pipeline's range mapping is linear (a native log display is filed in todo.md).
  **Infinite life is NaN** (the VTU no-value convention `FieldRange.Of` already skips);
  a sub-cycle life — including static failure — floors at one cycle (log10 = 0), since
  ranging skips only NaN and a −∞ would poison the minimum.
- **Alternating and mean stress** (MPa) — the decomposition itself, inspectable beside
  its consumers.

## Verification

On the nu = 0 bar whose uniaxial state is exact in the element space (the
`AnalysisBody` trick), oracles built independently of the path under test:

| check | measured |
| --- | --- |
| sigma_a/sigma_m vs an oracle from the DISPLACEMENT solution | 40 / 60 MPa, ≤ 1e-10 relative at every node |
| equal-and-opposite loads read R = −1 | mean ≤ 1e-13 of the amplitude (measures exactly 0 — negation commutes with IEEE rounding) |
| the line at one reversal is sigma'_f; the knee is on the line; flat beyond it | **bit-exact** |
| SAE 1045 endurance vs the hand-worked 948·(2·10⁶)^(−0.092) | 249.53 MPa; steel endurance/UTS ratios 0.40/0.40/0.34 inside the asserted 0.30–0.55 band |
| life/stress inverse round-trip below the knee | ≤ 1e-12 relative |
| zero-mean identity (both corrections); allowable at S_ut | bit-exact; exactly zero |
| loads scaled by the measured safety factor | the critical node lands ON the line, min factor 1.0 to 1e-9 (Goodman AND Gerber) |
| R = −1 at 400 MPa, full pipeline vs the hand-computed life | 5.92e3 cycles, log10 within 1e-6 of the Basquin inversion |

## Refused by name, not approximated

- **Welds** — hot-spot / nominal-stress category methods (IIW, Eurocode) are their own
  discipline; a weld's life is governed by its detail class, not the parent metal's line.
- **Multiaxial criteria beyond von Mises equivalence** — non-proportional paths need
  critical-plane methods (Findley, Fatemi–Socie): a different computation over a
  different input, with the reversed-pure-shear case above as the structural tell.
- **Variable amplitude / rainflow** — needs a time history two static cases cannot
  carry; `TransientResults`' stored states are the natural input, filed in todo.md.
