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
  `Helix3d` is the exact circular helix about a `Frame3d`'s Z axis
  (P(t) = O + X·r·cos t + Y·r·sin t + Z·p·t/2π, t = turning angle over [0, 2π·turns]):
  analytic derivative overrides (constant speed √(r² + (p/2π)²)), closed-form arc
  length turns·√((2πr)² + p²), lead angle, negative pitch descends; the origin+axis
  constructor delegates to `Frame3d.FromNormal` (the shared perpendicular convention).
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
- **`SolidFactory`** — `MakeBox`, `MakeCylinder`, `MakeSphere`, `MakeTorus`,
  `MakeCone(r1, r2, height[, baseCenter, axis])` (frustum; the side is an exact
  `RevolvedSurface` of the slanted line generator, reusing the revolved-band machinery —
  pole-fan tessellation, analytic plane⊥revolve circles, SURFACE_OF_REVOLUTION STEP
  export — rather than a dedicated cone surface type; rim circles are phase-aligned with
  u = 0; a zero radius makes that end an apex pole with no rim edge or cap), and the
  modeling operations:
  - `Extrude(profile, direction, holes?)` — shear allowed; holes make genus-n solids.
  - `Revolve(profile, axisOrigin, axisDir, angle?, holes?)` — full turn (torus topology,
    no caps) or partial (planar caps; closed profiles give pipe elbows; holes allowed).
  - `Sweep(profile, path, holes?)` — rotation-minimizing frames along an open path.

- **`SurfaceIntersection`** — `Intersect(a, b, region)`: exact analytic curves for the
  common quadric pairs (lines, circles, exact ellipses) and a general marching tracer
  (periodic-aware, multi-branch, closed-loop detection) returning `PolylineCurve3d` for
  everything else. See design.md §5 for the algorithm. Full-turn revolved surfaces whose
  sampled generator lies on a sphere centered on the axis (MakeSphere hemispheres) are
  recognized as **sphere carriers**: any plane cut returns the exact analytic circle
  (plane ⊥ axis keeps the phase-aligned path) — the tracer's region-clipped open
  polylines stop short of a bounded generator's rings and could never refine against
  face boundaries.

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
  (`SplitBandByWrapCurve`: constant-v cuts → two exactly re-surfaced sub-bands;
  NON-planar wrapping cuts — the cylinder∩cylinder curves where a cross-drill pierces
  a bore — → `SplitBandByNonPlanarWrapCurve`: both sub-bands KEEP the original surface,
  since no parameter line exists to trim at, and rely on trimmed-face tessellation;
  loops go to the side of the cut their v-range lies on, overlapping ranges — tangent
  configurations — throw). `TopologyEditor`
  supplies `SplitEdge` (patches every using loop) and `SealSeams` (boolean output
  sealing). **All coedge/curve sampling goes through
  `FaceGeometry.ExactSampleParameters`**: marching-tracer polylines are exact only at
  their VERTICES — a mid-chord sample sits a sagitta (~1e-4) off the carrier surface,
  far past the 1e-6 inverse-evaluation tolerance, and uniform sampling once made
  `PullCurveRuns` silently produce zero runs so cross-drill splits never happened —
  hence polyline-backed curves (raw or reparameterized via `CurveSegment`, whose
  `BaseStart`/`BaseEnd` expose the mapping) sample at vertex parameters and everything
  else uniformly. Closed curves interior to a face honor **mandatory seam breaks**
  (`SplitByInteriorClosedCurve`: hole and disk loops built from matching `CurveSegment`
  arcs, so a boolean's other side — which cuts the same circle at its own boundary
  crossings — pairs edge-for-edge in seam sealing). Wrap-splitting refuses faces with
  non-wrapping loops (a contractible fragment can share the band's carrier surface; a
  wrapping curve with no crossings lies outside it), and a fragment with ≥ 2 loops
  additionally parity-tests the cut against its own loops (several wrapping cuts can
  hit one band — a tool crossing a bore pierces its wall twice — and every sub-band
  shares the full carrier, so every cut pulls back onto every fragment; single-loop
  pole-bounded bands skip the check because the upward-ray convention cannot see a rim
  below the point). Arrangement tracing is
  **band-aware**: traced loops that wrap the period are band boundaries (traversal
  along +u = material above, mirrored on reversed faces), paired bottom-to-top by v
  into band sub-faces — pulled signed area is meaningless for them (the hemisphere
  band between a bitten equator and an untouched cap ring is the canonical case).

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
  profile (root solves, never distance minimization, which stalls near √ε). The
  rim-circle off-axis rejection floor scales with the coordinate magnitude (foreign
  files carry rounding noise proportional to their coordinates — an absolute 1e-6
  floor silently rejected slightly-off-axis rims on large geometry, leaving generators
  untrimmed), and near-miss rejections emit a diagnostic instead of failing silently.
  Units: millimetres assumed; other declared length units produce a diagnostic, not
  scaling.

Tessellation to meshes lives in `EngrCAD.Interop` (`BRepTessellator` +
`TrimmedFaceTessellator` for faces whose loops don't cover the surface's grid domain).

## Not yet implemented

Coplanar/tangent boolean cases, general fillet chains with corner patches,
NURBS surface export.
