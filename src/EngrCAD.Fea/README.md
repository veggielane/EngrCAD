# EngrCAD.Fea

Simulation foundation: **tetrahedral meshing**. Takes a closed manifold surface mesh (which
every EngrCAD representation reaches — `Shape.ToMesh()`, `BRepTessellator`, Surface Nets,
an imported STL) and fills it with tetrahedra suitable for finite-element analysis.

Kernel-clean: references only `EngrCAD.Core` and `EngrCAD.Mesh`, no UI and no rendering.

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
