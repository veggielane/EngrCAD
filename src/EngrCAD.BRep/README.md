# EngrCAD.BRep

The boundary-representation engine: parametric geometry, topology, and modeling
operations. Depends only on `EngrCAD.Core`.

## Contents

- **Curves** (`Curve3d`): `Line3d`, `Circle3d`, `NurbsCurve` (Cox–de Boor; rational
  quadratics represent conics exactly), plus `ReversedCurve` / `TransformedCurve`
  wrappers. `Underlying` unwraps wrappers so consumers (tessellation) can pick sampling
  rules. The default `TangentAt` uses second-order one-sided differences at domain ends —
  sweep frames are sensitive to start-tangent error.
- **Surfaces** (`Surface`): `PlaneSurface`, `CylinderSurface`, `SphereSurface`,
  tensor-product `NurbsSurface`, and the generated surfaces `ExtrudedSurface`,
  `RevolvedSurface` (partial or full angle), `SweptSurface` (rotation-minimizing frames
  via double reflection; exact at its frame samples).
- **Topology**: `BrepSolid → BrepShell → BrepFace → BrepLoop → BrepCoedge → BrepEdge →
  BrepVertex`. Faces are built so surface normals point outward and loops run CCW around
  them (first loop outer, rest holes). `Validate()` checks loop chaining and two-manifold
  edge use; `SatisfiesEulerFormula(genus)` checks V − E + F − (L − F) − 2(S − G) = 0.
- **`Profile`** — planar closed chain of curve segments (or one closed curve) used by the
  modeling operations; winding is auto-corrected per operation.
- **`SolidFactory`** — `MakeBox`, `MakeCylinder`, and the modeling operations:
  - `Extrude(profile, direction, holes?)` — shear allowed; holes make genus-n solids.
  - `Revolve(profile, axisOrigin, axisDir, angle?, holes?)` — full turn (torus topology,
    no caps) or partial (planar caps; closed profiles give pipe elbows; holes allowed).
  - `Sweep(profile, path, holes?)` — rotation-minimizing frames along an open path.

- **`SurfaceIntersection`** — `Intersect(a, b, region)`: exact analytic curves for the
  common quadric pairs (lines, circles, exact ellipses) and a general marching tracer
  (periodic-aware, multi-branch, closed-loop detection) returning `PolylineCurve3d` for
  everything else. See design.md §5 for the algorithm.

- **`FaceGeometry` / `FaceSplitter`** — trimming groundwork: inverse surface evaluation
  (`Surface.TryProjectPoint`), curve pullback into parameter space (periodic-aware),
  point-in-face classification, and splitting faces by closed interior curves (hole +
  disk sharing one manifold edge).

Tessellation to meshes lives in `EngrCAD.Interop` (`BRepTessellator`).

## Not yet implemented

Open-curve face splitting (boundary-crossing arrangements), automatic B-Rep booleans,
filleting.
