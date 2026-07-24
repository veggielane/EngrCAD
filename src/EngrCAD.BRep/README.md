# EngrCAD.BRep

The boundary-representation engine: parametric geometry, topology, and modeling
operations. Depends only on `EngrCAD.Core`.

## Contents

- **Curves** (`Curve3d`): `Line3d`, `Circle3d`, `NurbsCurve` (Cox–de Boor; rational
  quadratics represent conics exactly), plus `ReversedCurve` / `TransformedCurve`
  wrappers. `Underlying` unwraps wrappers so consumers (tessellation) can pick sampling
  rules. The default `TangentAt` uses second-order one-sided differences at domain ends —
  sweep frames are sensitive to start-tangent error — but `NurbsCurve` overrides it with
  the exact analytic derivative (algorithm A2.3 basis derivatives + the rational quotient
  rule, eq. 4.8; also exposed as `DerivativeAt` / `SecondDerivativeAt`).
  `NurbsCurve.InterpolatePoints(points, closed)` builds a cubic B-spline passing exactly
  through the points (`GeomAPI_PointsToBSpline`-style): chord-length parameterization;
  open curves use clamped knots + natural end conditions via a tridiagonal collocation
  solve (two points degrade to a degree-1 chord); closed curves use periodic knots with
  wrapped control points, so the seam is C2 by construction (cyclic system solved densely).
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

- **`FaceGeometry` / `FaceSplitter` / `TopologyEditor`** — trimming machinery: inverse
  surface evaluation (`Surface.TryProjectPoint`), curve pullback into parameter space
  (periodic-aware; `PullCurveRuns` tolerates curves that only partially lie on a bounded
  surface — off-surface stretches separate contiguous runs, and cut ends gain one
  extrapolated seed sample so crossings at a band's end rings are still found),
  point-in-face classification, splitting faces by closed interior curves (hole + disk
  sharing one manifold edge), open/crossing curves (full parameter-space arrangement:
  boundary edges split at crossings — refined by 3D curve–curve Gauss–Newton, exact from
  both solids' sides; crossing seeds slightly inclusive so cuts through split-created
  vertices are not missed — interior segments as shared two-use edges, sub-faces traced
  by tightest-turn walking: clockwise for CCW-wound faces, counter-clockwise for
  reversed ones, `IsReversed` preserved throughout), and period-wrapping curves
  (`SplitBandByWrapCurve`: band → two exactly re-surfaced sub-bands). `TopologyEditor`
  supplies `SplitEdge` (patches every using loop) and `SealSeams` (boolean output
  sealing).

Tessellation to meshes lives in `EngrCAD.Interop` (`BRepTessellator` +
`TrimmedFaceTessellator` for faces whose loops don't cover the surface's grid domain).

## Not yet implemented

Coplanar/tangent boolean cases, general fillet chains with corner patches, STEP import,
NURBS surface export.
