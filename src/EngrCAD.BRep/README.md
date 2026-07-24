# EngrCAD.BRep

The boundary-representation engine: parametric geometry, topology, and modeling
operations. Depends only on `EngrCAD.Core`.

## Contents

- **Curves** (`Curve3d`): `Line3d`, `Circle3d`, `Ellipse3d`, `Parabola3d`, `Hyperbola3d`,
  `NurbsCurve` (Cox–de Boor; rational quadratics represent conics exactly), plus
  `ReversedCurve` / `TransformedCurve` wrappers. `Underlying` unwraps wrappers so
  consumers (tessellation) can pick sampling rules — never trust it for POSITION.
  `Curve3d` exposes virtual `DerivativeAt` / `SecondDerivativeAt` (finite-difference
  defaults, documented approximate); every analytic curve and both wrappers override
  them exactly, and `NurbsCurve` uses algorithm A2.3 basis derivatives + the rational
  quotient rule (eq. 4.8). The default `TangentAt` uses second-order one-sided
  differences at domain ends — sweep frames are sensitive to start-tangent error — and
  applies only to curves without exact overrides (`PolylineCurve3d`).
  `Parabola3d` uses the focal parameterization P(t) = Apex + (t²/(4f))·X + t·Y (local
  y² = 4fx; closed-form arc length f·(s√(1+s²) + asinh s), s = t/(2f));
  `Hyperbola3d` is one branch P(t) = C + A·cosh t + B·sinh t (arc length by adaptive
  Simpson — no elementary closed form). Both require a finite domain at construction
  (the underlying loci are unbounded; OCCT trims equivalently).
  `OffsetCurve3d` is a planar offset as first-class geometry:
  O(t) = C(t) + d·(n̂ × T̂(t)), positive d to the left of travel seen from +n̂ (CCW
  circle with n̂ = axis: radius r − d, exactly concentric). Its exact derivative is
  O′ = (1 − d·κ)·C′ with κ = C″·(n̂ × C′)/|C′|³ — never finite-differenced; exactness
  inherits from the base curve's derivative overrides. The constructor validates
  planarity but NOT |d| against the minimum radius of curvature — cusps and
  self-intersection from too-large offsets are the caller's responsibility (as in
  OCCT's `Geom_OffsetCurve`). `Underlying` forwards to the base curve for sampling
  rules only.
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

- **`FaceGeometry` / `FaceSplitter`** — trimming groundwork: inverse surface evaluation
  (`Surface.TryProjectPoint`), curve pullback into parameter space (periodic-aware),
  point-in-face classification, and splitting faces by closed interior curves (hole +
  disk sharing one manifold edge).

- **STEP export/import** — `StepWriter.Write/WriteFile` (ISO 10303-21 AP214
  `MANIFOLD_SOLID_BREP`; analytic surfaces/curves, rational NURBS via the
  complex-instance form, wrapper-curve simplification; swept surfaces not exportable)
  and `StepReader.Read/ReadFile` (its inverse: a full Part 21 parser — strings with
  `''` escapes, `1.E-6`-style reals, enums, typed values, complex instances, forward
  references — plus entity mapping back to `BrepSolid`, returning solids + a
  diagnostics list; unknown entities are skipped with a report). Round-trips
  everything the writer emits: topology is shared by entity identity (one edge per
  `EDGE_CURVE`, one vertex per `VERTEX_POINT`), edge domains are reconstructed
  exactly from vertex positions (closed-form phases for circles/ellipses, Newton with
  exact derivatives for B-splines), and `SURFACE_OF_REVOLUTION` — which stores
  neither our swept angle nor generator trims — recovers the angle from rail arcs and
  re-trims generators from rim circles by bisection on the exact (radius, axial)
  profile (root solves, never distance minimization, which stalls near √ε). Units:
  millimetres assumed; other declared length units produce a diagnostic, not scaling.

Tessellation to meshes lives in `EngrCAD.Interop` (`BRepTessellator`).

## Not yet implemented

Open-curve face splitting (boundary-crossing arrangements), automatic B-Rep booleans,
filleting.
