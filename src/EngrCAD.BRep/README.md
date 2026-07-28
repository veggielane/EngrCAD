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
  `SpiralArc3d` is a planar Archimedean spiral arc, P(t) = O + X·r(t)·cos t +
  Y·r(t)·sin t with r(t) linear in the angle t (zero slope = circular arc, kept in the
  type so all helical cap cuts share one sampling rule); exact derivatives. It is
  exactly the curve a plane ⊥ axis cuts from a `HelicalSurface` band — with a linear
  generator, solving z(v) + pitch·u/2π = z_cap makes v (hence radius) linear in u —
  and is always built on the band's own axis frame so its parameter IS the surface u
  (phase alignment).
  `LoftRailCurve` is the curve a fixed section parameter traces across a `LoftedSurface`
  (the loft analogue of `SweptRailCurve`); it evaluates the surface itself rather than
  re-interpolating the junction points, so a rail edge and the face's u = 0 grid column are
  the same arithmetic. `PhaseShiftedCurve` moves a closed curve's seam,
  P(t) = base(wrap(t + shift)) — how lofting aligns successive closed sections so the skin
  does not twist; `Underlying` forwards (a shifted circle still samples as a circle).
  **Arc length** is `Curve3d.ArcLength(from, to)` (adaptive Simpson with Richardson
  extrapolation over the exact speed |C′|, tolerance RELATIVE to the chord so it is
  scale-free) and its inverse `ParameterAtLength` (safeguarded Newton on L(t) − s = 0 with a
  bisection bracket — a ROOT solve, never a minimization, which stalls at √ε), with
  `ArcLengthTable3d` caching a monotone table for resampling loops (`SampleByLength` spaces
  points equally ALONG the curve, which uniform parameters on a varying-speed curve are not).
  The virtual is overridden EXACTLY wherever a closed form exists — `Line3d` and `Helix3d`
  have constant speed, `Circle3d`'s parameter is the angle, `Parabola3d` has the antiderivative
  f·(s√(1+s²) + asinh s), a `PolylineCurve3d` IS parameterized by cumulative chord length so
  its arc length is its parameter, and `ReversedCurve`/`CurveSegment` forward to their base so
  a reversed or trimmed exact curve stays exact. `Helix3d.Length()`/`Parabola3d.Length()` now
  delegate to the virtual rather than carrying second copies of the same formula.
  Everything else integrates, and its accuracy is therefore the accuracy of `DerivativeAt` —
  which is exact on every analytic curve and on NURBS, so a rational arc's length matches
  r·sweep to 1e-8 despite an integrand that is far from constant in the NURBS parameter.
  One copy of adaptive Simpson (`AdaptiveQuadrature`) now serves `Curve2d`, `Curve3d` and the
  conics' closed-form gaps.
  `PolylineCurve3d.Simplified(tolerance)` drops the samples the marching tracer's step
  produced rather than the curve's shape (Core's `PolylineSimplify`, Douglas–Peucker):
  retained vertices are bit-for-bit the originals, but a polyline is CHORD-LENGTH
  parameterized, so the domain shortens and anything holding parameters into the curve — a
  `CurveSegment`, a pulled face loop, a boolean's mandatory break — must be rebuilt. That is
  why nothing in the pipeline simplifies implicitly.
  `NurbsCurve.InterpolatePoints(points, closed)` builds a cubic B-spline passing exactly
  through the points (`GeomAPI_PointsToBSpline`-style): chord-length parameterization;
  open curves use clamped knots + natural end conditions via a tridiagonal collocation
  solve (two points degrade to a degree-1 chord); closed curves use periodic knots with
  wrapped control points, so the seam is C2 by construction (cyclic system solved densely).
- **2D curves** (`Curve2d`): `Line2d`, `Arc2d`, `BezierCurve2d`, `NurbsCurve2d` — the
  sketch-plane siblings of the 3D family. Two deliberate divergences from `Curve3d`:
  `DerivativeAt`/`SecondDerivativeAt` are **abstract**, because every 2D curve here is
  analytic and there must be no finite-difference fallback for a new type to inherit by
  accident; and `Arc2d` carries a **signed** `SweepAngle`, so orientation is intrinsic to
  the data rather than a separate flag plus a reverse-and-hope repair (the g3 shape).
  `Arc2d.FromPointAndTangent(start, tangent, end)` is the biarc construction primitive and
  degenerates to a `Line2d` on a RELATIVE straightness test (sagitta against chord — a
  dimensionless sine, so it behaves the same at micron and kilometre scale).
  `NurbsCurve2d` shares the basis via **`BSplineBasis`** (now public: the A2.1/A2.2/A2.3
  algorithms depend only on knots and degree, so `NurbsCurve`, `NurbsCurve2d` and
  `NurbsSurface` all use the one copy), and `NurbsCurve2d.InterpolatePoints` DELEGATES to
  the 3D interpolation on z = 0 rather than forking the collocation solve — every
  operation on the z component is 0·m, 0 − 0 or 0/d, so the control points come back with
  z exactly zero. Arc length is `Curve2d.ArcLength` (adaptive Simpson with Richardson
  extrapolation, tolerance RELATIVE to the chord) and its inverse `ParameterAtLength`
  (safeguarded Newton on L(t) − s = 0 with a bisection bracket — a ROOT solve, never a
  minimization, which stalls at √ε); `ArcLengthTable2d` caches a monotone table for
  resampling loops. `Curve2d.DistanceTo`/`NearestPoint` are closed-form on `Line2d` and
  `Arc2d` and sample-plus-Newton elsewhere — and because every candidate is a real point
  ON the curve, the generic path can only ever OVER-estimate, which is the safe direction
  for a fitting error metric.
  **`Curve2d.ToCurve3d(plane)`** places a 2D curve on a `Frame3d` as an EXACT `Curve3d` — the
  bridge into the topology vocabulary, consumed by `Profile.FromCurves(curves, plane?)`. It is
  ABSTRACT for the same reason the derivatives are: every conversion is exact and there must
  be no sampled fallback for a new type to inherit by accident. Lines become `Line3d`,
  Béziers become the equivalent Bézier-knot `NurbsCurve` (a re-expression, same control
  points), `NurbsCurve2d` keeps its degree/knots/weights so a rational arc stays an exact arc,
  and arcs lift exactly as sketch arcs already did — a full turn to a `Circle3d` on the arc's
  own start radial (parameter following the SIGNED sweep), anything less to a `CurveSegment`
  over a circle on the frame's axes, so `Underlying` stays the `Circle3d` downstream
  classification depends on and a negative sweep arrives as a decreasing parameter range
  rather than a reversal wrapper. `Profile.FromCurves` hands the lifted chain to the ordinary
  `Profile` constructor, so closure, planarity and winding are validated in exactly one place;
  `Sketch.ToCurves`/`Sketch.FromCurves` close the loop on the Modeling side. See design.md §5
  for why the bridge is deliberately this small.
- **Biarc fitting** (`BiArcFit`, `BiArc2d`, `BiArcChain2d`/`BiArcChain3d`): two
  tangent-continuous arcs through a point+tangent pair, plus tolerance-driven chains
  through a polyline and a 3D wrapper that turns a PLANAR traced polyline into exact
  `Line3d` + rational-arc `NurbsCurve` pieces (STEP-exportable, far lighter than a
  polyline edge). Three things worth knowing:
  - **Adoption is opt-in and the deviation is always reported.** Nothing in the kernel
    fits biarcs implicitly — `SurfaceIntersection.Intersect` still returns
    `PolylineCurve3d`, and the boolean pipeline still consumes it. Two OPT-IN doors exist
    for consumers that want light analytic geometry rather than weldable topology:
    **`SurfaceIntersection.FitAnalytic(curves, tolerance)`** returns one `AnalyticFit` per
    input saying whether the curve was fitted, what the fit cost, and why it was refused
    (a genuine space curve comes back as `NotPlanar`, never silently flattened); an
    unfitted entry carries the original curve, so concatenating every entry's curves is
    always correct. **`StepWriter.Options.ArcFitTolerance`** (default null = off) makes the
    exporter fit curves with no analytic STEP form — traced polyline edges, RMF rails,
    `TransformedCurve(NurbsCurve)` — instead of SAMPLING them into a degree-1 B-spline,
    and `StepWriter.Write(solid, options)` returns a `Result` carrying the worst adopted
    deviation plus the fitted/sampled curve counts. The chain is emitted as ONE degree-2
    rational B-spline rather than a `COMPOSITE_CURVE`: consecutive rational quadratic
    Béziers over double interior knots ARE a degree-2 B-spline (exactly how
    `NurbsCurve.Arc` builds a multi-quadrant arc), and a straight piece is the same form
    with control points p₀, (p₀+p₁)/2, p₁ at unit weights — which reproduces the line
    exactly, parameterization included. That keeps the file inside the entity set
    `StepReader` already parses. **Why the boolean pipeline must not adopt these**: a
    traced polyline is exact only at its VERTICES and the whole splitting machinery is
    built on that (`FaceGeometry.ExactSampleParameters`); replacing an edge's carrier with
    a fitted arc moves every point on it by up to the fit tolerance, decades past the 1e-9
    weld tier.
    `BiArcChain*.MaxDeviation` is the largest distance from an INPUT SAMPLE to the fit
    (for 3D, √(in-plane² + out-of-plane²), so it includes the flattening). It measures the
    given points only and says nothing about the true curve between them; that is a
    property of the sampling, not of the fit. A non-planar polyline is REFUSED
    (`BiArcFitStatus.NotPlanar`) rather than silently flattened.
  - **The free parameter is computed in the stable form** `d = |v|² / (√disc + v·t)`
    rather than `(√disc − v·t)/denom`. Algebraically identical, but it stays accurate as
    `denom = 2 − 2·t₁·t₂` approaches zero AND reduces exactly to the equal-tangent case at
    denom = 0 — so the reference implementation's epsilon test on the squared quantity
    `|t₁+t₂|² ≈ 4` (which picks a branch at an arbitrary angular threshold of √ε)
    disappears entirely, along with the broken semicircle branch behind it.
  - **The endpoints are exact and the round-off goes to the joint.** The second arc is
    built backwards from the end point and reversed, so both data points and both end
    tangents are reproduced to ~1 ulp of the radius and all the construction error lands
    on the interior junction — where nothing has to weld. Chained fits therefore hand
    their shared data points over at round-off, not at the fit tolerance. Tangents at
    polyline samples come from the circle through each point and its neighbours (exact for
    circular and straight data, which marched intersection curves usually are), with a
    relative area test falling back to the chord.
- **Surfaces** (`Surface`): `PlaneSurface`, `CylinderSurface`, `SphereSurface`,
  tensor-product `NurbsSurface`, and the generated surfaces `ExtrudedSurface`,
  `RevolvedSurface` (partial or full angle), `SweptSurface` (rotation-minimizing frames
  via double reflection; exact at its frame samples), and `HelicalSurface` — the
  co-rotating sweep of a straight profile segment (one screw-thread facet):
  P(u, v) = O + X·r(v)·cos u + Y·r(v)·sin u + Z·(z(v) + pitch·u/2π) with (r(v), z(v))
  linear from `ProfileStart` to `ProfileEnd`, u a finite turning-angle interval spanning
  all turns (NOT periodic — the axial advance makes every u distinct, so inverse
  evaluation never wraps a seam), v ∈ [0, 1]; and `LoftedSurface` — the lateral skin of a
  loft, P(u, v) = Σ α_k(v)·C_k(u_k) with u_k the section curve's own parameter at the
  normalized u, both parameters over [0, 1]. The blend α is the **cardinal basis** of
  B-spline interpolation (A[k][j] = N_{j,p}(v_k) at the section parameters,
  α_k(v) = Σ_j N_{j,p}(v)·(A⁻¹)[j][k], degree p = min(3, sections − 1)): solving the
  interpolation ONCE at construction is what makes a loft a surface at all — a chord-length
  re-parameterization recomputed per u would give every strip its own v mapping and the
  shared rails would disagree. α_k(v_j) = δ_jk is applied as an **exact-equality special
  case**, so the tessellation grid's v = 0 / v = 1 rows reproduce the first and last section
  curves bit-for-bit (they are also the shared cap and neighbour edges). `NaturalUSegments`
  mirrors `BRepTessellator.SampleEdge`'s rules for the sections that ARE the face's u
  boundaries — the rule lives on the surface because only it knows what its sections are.
  Exact analytic `DerivativeU`/`DerivativeV`/`NormalAt`.
  Exact analytic normals and exact
  closed-form `TryProjectPoint` (the point's angle fixes u up to whole turns, the axial
  coordinate solves v linearly; in-range v preferred so steep generators can't alias
  onto the neighboring turn; dz = 0 helicoid ramps solve v from the radius).
  **Inverse evaluation on swept surfaces is a ONE-dimensional solve** — on ALL THREE of
  them, though the third gets there differently. The base
  `Surface.TryProjectPoint` scans a 17×17 (u, v) grid and Gauss–Newtons in 2D, but a
  translational or rotational sweep has one free parameter too many: for
  `ExtrudedSurface`, P = C(u) + v·direction, so v is whatever the direction component
  demands and only the perpendicular residual Q(C(u) − p) constrains u (Q removes the
  direction component); for `RevolvedSurface`, u is the point's azimuth in closed form
  once v matches the generator's (radius, axial) profile. Both override with a scan of
  the generator alone (the base class's own u resolution, ranked by the *exactly*
  optimal other parameter rather than a quantized one) plus 1D Gauss–Newton using the
  generator's exact `DerivativeAt` — no damping, no clamped 2D wandering. The grid the
  base class walks re-evaluates the SAME generator point once per column: 289 curve
  evaluations where 17 carry all the information. Inverse evaluation is the inner loop
  of every face pullback, so this is the hot leaf of the whole boolean pipeline —
  measured 8–10× on engraved-text and drilled-plate booleans, and it cut the Interop
  test suite from 4m16s to 44s. Both fall back to the base implementation only where
  the reduction genuinely does not apply (a collapsed extrusion direction, a point on a
  revolve's axis where the azimuth is undefined).
  `SweptSurface` gets there by a DIFFERENT structural fact, because its
  rotation-minimizing frame varies along the path so no parameter is available in closed
  form. What is true is that every surface point at path parameter v lies in the frame's
  own plane there (the profile's component along the start tangent is discarded by
  construction), so `f(v) = (p − Path(v))·Tangent(v) = 0` is a scalar equation in v
  ALONE — the foot-of-perpendicular condition — and once v is known the point's local
  (x, y) offset in that frame fixes u by matching the generator, again in 1D. Two
  decoupled solves, neither involving the other's unknown. f is not monotone on a curving
  path, so its roots are BRACKETED by a 16-sample scan and refined by safeguarded
  bisection (the bracket guarantees convergence, not the seed), the path's two ends are
  always candidates (a point beyond an end cap has no interior root), and every candidate
  is scored on the true 3D residual. The profile offsets do not depend on v at all, so the
  whole u seed table is built once — 17 curve evaluations against the base class's 289,
  which additionally recomputes a full `Frame(v)` per sample. **Measured 3.9× (curved
  profile segment) and 5.2× (full-circle tube profile) per projection**, against the
  unmodified base implementation reached through a wrapper forwarding the same
  `PointAt` — same geometry, same queries, only the algorithm differs.
  Correctness improved as well: the override accepted 400/400 round-trip queries on both
  surfaces where the base accepted 392 and 266. See the seed-resolution note under
  `SweptSurface.SolveGeneratorParameter` for why refinement starts from every local
  minimum *and its neighbours* — a sliver profile hides two branches inside one seed
  interval, and single-seed refinement silently returns the mirrored parameter.
  `NurbsSurface` still uses the base grid, legitimately: no such reduction exists.
- **Topology**: `BrepSolid → BrepShell → BrepFace → BrepLoop → BrepCoedge → BrepEdge →
  BrepVertex`. Faces are built so surface normals point outward and loops run CCW around
  them (first loop outer, rest holes). `Validate()` checks loop chaining and two-manifold
  edge use; `SatisfiesEulerFormula(genus)` checks V − E + F − (L − F) − 2(S − G) = 0.
  **`BrepSolid.Clone()`** deep-copies the topology graph and SHARES the geometry (curves
  and surfaces are immutable once constructed, so only topology needs copying). It exists
  because booleans CONSUME their inputs — `SplitEdge` patches every loop using an edge and
  `SealSeams` re-parents coedges and unifies vertices — so any caller handing one solid to
  two booleans must clone first. The damage is silent otherwise: face/edge/vertex counts
  survive, so the solid still looks intact, and the second boolean either throws deep
  inside face tracing or returns a closed, `Validate`-clean, WRONG result.
- **`Profile`** — planar closed chain of curve segments (or one closed curve) used by the
  modeling operations; winding is auto-corrected per operation.
  `Profile.FromRegion(region, frame?)` places a Core `Geometry2.Region2d`
  (polygon-with-holes, the output of the 2D sketch engine's booleans) on a `Frame3d` and
  returns the `(outer, holes)` pair `Extrude`/`Revolve`/`Sweep` take — the 2D front door
  into the solid factories; `FromLoop(points, frame)` does one loop and REFUSES a
  self-intersecting one (`Region2dValidation`): a profile is the boundary of a face, and a
  boundary that crosses itself has no interior to extrude or revolve, so the factories would
  build a self-overlapping shell that still passes `Validate()`. `FromRegion` does not
  re-check — `Region2d` validated its loops when it was constructed. Regions are polygonal,
  so these profiles are polygonal: a region derived from curved sketch input carries that
  flattening (see `Sketch.ToRegions`), whereas a sketch handed straight to a modeling
  operation keeps its exact arcs and NURBS.
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
  - `Loft(sections, style)` — skins a closed solid through a list of planar sections
    (OCCT `BRepOffsetAPI_ThruSections`): each corresponding pair of profile segments becomes
    one `LoftedSurface` strip, junctions become `LoftRailCurve` rails, the first and last
    sections are capped. `LoftStyle.Smooth` is one face per strip interpolating ALL sections
    (intermediate sections leave no edge); `LoftStyle.Ruled` is a band of faces per interval
    (every section is an edge loop). Two sections are the same solid either way.
    **Compatibility is by segment index and normalized parameter** — sections must already
    have the same segment count, and a mismatch is rejected with a message saying so rather
    than being papered over (no degree elevation / knot merging yet). The one automatic fix
    is representational: where one section's strip is straight and another's is curved, the
    straight one is re-expressed as an exact degree-1 NURBS so both take the tessellator's
    generic sampling rule and the grid welds. **Alignment** happens before skinning:
    sections wound against the loft direction are reversed, multi-segment sections are
    cyclically rotated to the least-twist segment pairing, and closed single-curve sections
    get a continuous seam shift — all three minimize the same **centroid-relative** sum of
    squared corner travel (leaving the sections' separation in that objective makes it a
    large constant plus a tiny quadratic well, which measurably cost eight digits: a seam
    shift resolved to only ~3e-9, i.e. 3e-8 of positional twist, past weld tolerance).
    The v parameterization is global to the loft (mean chord length), never per strip.
  - `MakeThreadedRod(pitchProfile, pitch, length[, frame])` — a helically threaded rod
    whose entire lateral boundary is ONE co-rotating sweep of a per-pitch profile
    (boolean-free by design: winding a ridge onto a core cylinder would be the
    unsupported coaxial-tangent boolean; here the root flats ARE part of the sweep).
    The profile is a list of (radius, axial) corners spanning < 1 pitch (strictly
    increasing axial, radii positive; the closing segment wraps to corner 0 + pitch);
    each of the K segments becomes one `HelicalSurface` band spanning ALL turns,
    adjacent bands share exact `Helix3d` rails on the rod's own frame rotated to each
    corner's phase (rails start on the z = 0 cap plane), and the flat caps are disks
    bounded by the closed chain of K `SpiralArc3d` cuts covering one full turn. Any
    positive length works (no whole-turn constraint — rails just end at different
    phases). Right-hand only (positive pitch). V = 2K, E = 3K, F = L = K + 2 ⇒
    Euler–Poincaré 0 at genus 0. Exact volume for ANY length:
    L·(2π/P)·∫₀^P ½R(s)² ds (the full angular sweep at each z washes out the phase).

- **`Draft`** — draft angles (OCCT `BRepOffsetAPI_DraftAngle`), the moulding/casting taper:
  `Draft.Apply(solid, neutralOrigin, pullDirection, angle, faceSelector?)` (or the
  `neutralFace` overload, which pulls *into* the solid so drafting about a box's bottom face
  narrows it going up). Each selected face's plane is **rotated about its neutral line** —
  where it meets the neutral plane — by exactly `angle` toward the pull direction, and every
  corner is then the exact algebraic intersection of three planes: the rotated normal is
  `n·cos θ + p̂⊥·sin θ` (p̂⊥ = the pull direction's component in the face plane), and the
  anchor slides along that same in-plane direction onto the neutral plane, so it lies on the
  neutral line the rotation fixes. Nothing is offset, projected or fitted — a drafted box is
  exactly a frustum, geometry ON the neutral plane provably does not move (it is the parting
  line), and drafting twice by θ/2 equals drafting once by θ. Faces the selector does not
  name keep their planes exactly; their corners still move, because the drafted neighbours
  they meet did. The rebuild uses `PlaneSurface` faces (not a ruled loft), so the result
  stays selectable by the same `BrepQueries` vocabulary and STEP-exportable.
  v1 handles **planar-faced prisms** — two caps perpendicular to the pull direction,
  single-loop caps, four-sided planar sides — and rejects everything else loudly: curved
  faces, caps with holes, selecting a cap, and a taper large enough to fold the profile
  (checked by winding *and* per-edge direction against the original loop, since a signed
  area alone can stay positive while one edge has already reversed).
- **`Shelling`** — offset solids and hollowing (OCCT `BRepOffsetAPI_MakeOffsetShape` /
  `MakeThickSolid`): `Shelling.Offset(solid, distance)` moves every face along its own
  normal (positive grows, negative shrinks) and `Shelling.Shell(solid, thickness,
  openingSelector?)` hollows to walls of that thickness. For a **polyhedral** solid this is
  exact with no approximation anywhere: an offset plane is a plane, and each offset vertex is
  the algebraic intersection of the three planes that met there. Topology is carried over
  verbatim, so hole loops and genus survive — offsetting a plate inward shrinks its outline
  and *grows* its bore, because a bore wall's outward normal points into the bore.
  Shelling adds the hollowing structure: the offset copy becomes an inward-facing inner
  boundary (flipped plane axes + loops walked backwards with flipped senses, so it is
  genuinely CCW about the flipped normal rather than an `IsReversed` flag), and each face
  named as an opening contributes a **rim** face — the removed face's own loops as its outer
  boundary (they supply the second use of every edge that face used to carry) plus the inner
  opening as a hole. With no opening the cavity is sealed and the result is a **two-shell**
  solid; with openings it is one shell, and two opposite openings give a genus-1 tube.
  Rejections are loud: curved faces (a cylinder offsets to a cylinder and a revolve to the
  revolve of an `OffsetCurve3d` generator, but their *corners* need surface–surface
  re-intersection, not a three-plane solve), vertices where more than three faces meet (the
  offset corner is over-determined and needs corner patches), adjacent openings (zero-width
  rim), openings on a face with holes, multi-shell inputs, and an offset that locally folds
  the solid. **Not** checked: an offset large enough to make distant surfaces pass through
  each other with no local symptom — the same contract OCCT offers and `OffsetCurve3d`
  already documents for curves.
- **`SurfaceIntersection`** — `Intersect(a, b, region)`: exact analytic curves for the
  common quadric pairs (lines, circles, exact ellipses), plane ⊥ helical-axis cuts
  (exact `SpiralArc3d` on the band's own frame — the SAME arithmetic
  `MakeThreadedRod`'s cap cuts use, so seams weld; a dz = 0 helicoid ramp cuts in an
  exact radial line), **bounded planar carriers** (below), and a general marching tracer
  (periodic-aware, multi-branch, closed-loop detection) returning `PolylineCurve3d` for
  everything else. See design.md §5 for the algorithm. Full-turn revolved surfaces whose
  sampled generator lies on a sphere centered on the axis (MakeSphere hemispheres) are
  recognized as **sphere carriers**: any plane cut returns the exact analytic circle
  (plane ⊥ axis keeps the phase-aligned path) — the tracer's region-clipped open
  polylines stop short of a bounded generator's rings and could never refine against
  face boundaries.

  **The tracer's numeric constants live in one place**: the private
  `SurfaceIntersection.TracerSettings` record struct (seed resolution and pairing radius,
  march step divisor, seed/branch/closure step multiples, Newton iteration counts, the
  1e-10 residual / 1e-9 seed-acceptance / 1e-8 corrector-acceptance ladder, Levenberg
  damping, the 1e-7 tangential-contact guard, domain slack, the 1e-14 pivot floor and the
  1e-7 central-difference step). They are a SET, not independent knobs — the march step is
  the unit every spatial test is measured in, the acceptance ladder must stay ordered
  (residual < seed acceptance < corrector acceptance < the 1e-6 pullback tolerance that
  consumes traced vertices), and the central-difference step bounds how tight the residual
  can usefully be. **Boolean-critical**: tune them together, with the whole suite and the
  DocsGen snippets as the regression net.

  **Bounded planar carriers.** A sketch extrusion's walls are `ExtrudedSurface`s over the
  profile's individual segments, so a pocket wall is `ExtrudedSurface(line, dir)` —
  geometrically a plane, but a BOUNDED one (a parallelogram). Two paths handle them, both
  clipped to the surfaces' real extents, never just to the query region:
  - **Plane ∩ extrusion whose generator lies in a plane PARALLEL to the cutting plane** —
    every generator point then meets the plane after the same travel along the direction,
    so the section is EXACTLY the generator translated by `direction · v`. Exact for any
    generator shape (straight pocket walls, slot arcs, cubic glyph outlines), bounded
    exactly to the generator's own extent, and — the reason it is checked first — built
    from the generator's own points, so adjacent profile segments hand over their shared
    corner **bit-for-bit** and a pocket outline closes into the chain
    `SplitByClosedCurveChain` consumes. A plane flush with either rim reports no section:
    that is the coplanar/tangent case booleans reject, and splitting there would only
    make zero-extent slivers.
  - **Two planar carriers meeting at an angle** — the exact analytic line, clipped to the
    query region AND to each bounded carrier's parallelogram (two slab clips in the
    patch's own (s, t) coordinates). Straightness is decided by SAMPLING the actual
    generator at the 1e-9 weld tier; `Underlying` is only a type hint.

  These replaced the marching tracer for these pairs, which was the root cause of
  "subtracting a straight-edged sketch extrusion silently produces an open mesh": the
  tracer breaks the step *after* its parameters leave the domain, so its polyline stopped
  up to one march step (≈ region extent / 150) short of each generator end. The four wall
  cuts never met at the pocket corners, the outline never closed, and the boolean left
  single-use edges — an open mesh with no error; on an extruded plate a through-cut
  silently removed nothing at all while still passing `Validate()`.

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
  far past the 1e-6 inverse-evaluation tolerance (now the named constant
  `FaceGeometry.InverseEvaluationTolerance`, used by all pullback call sites in BRep
  and Interop), and uniform sampling once made
  `PullCurveRuns` silently produce zero runs so cross-drill splits never happened —
  hence polyline-backed curves (raw or reparameterized via `CurveSegment`, whose
  `BaseStart`/`BaseEnd` expose the mapping) sample at vertex parameters and everything
  else uniformly. Closed curves interior to a face honor **mandatory seam breaks**
  (`SplitByInteriorClosedCurve`: hole and disk loops built from matching `CurveSegment`
  arcs, so a boolean's other side — which cuts the same circle at its own boundary
  crossings — pairs edge-for-edge in seam sealing). Open splitting curves may
  TERMINATE exactly on the face boundary when a crossing was detected there (a
  plane∩helical spiral arc ends on the band's rails; the endpoint's containment parity
  is rounding noise and is not tested). `SplitByClosedCurveChain(face, curves)` splits
  a face along a CLOSED CHAIN of open curves lying in its interior whose endpoints
  pair end-to-start (the spiral-arc chain a threaded tool's bands cut into a drilled
  plane): one vertex per junction, ONE edge per curve — so a boolean's other side,
  splitting each band by its own arc, pairs edge-for-edge in seam sealing — with the
  hole loop wound opposite the outer loop and the disk sharing the same edges.
  Wrap-splitting refuses faces with
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

- **`Filleting`** — rim chamfering and filleting as topology surgery on an existing solid
  (no booleans): the outer rim of a planar face is replaced by a bevel or blend band, the
  face shrinks, and the neighbours drop.
  - **Chamfer** — straight rim edges become planar strips that MITER at sharp corners
    (both strips contain the straight corner segment, so it is their shared edge by
    construction); a full circular rim becomes an exact cone band. Sizing is two setbacks
    (`ChamferRim`) or the "distance and angle" spelling `ChamferRimAtAngle(setback,
    degrees)` — setback measured IN the chamfered face, angle measured FROM it, 45° being
    the symmetric case.
  - **Fillet** — a full circular rim becomes an exact quarter-torus (`FilletEdge`); a
    tangent-continuous chain of lines and arcs becomes quarter-cylinder and
    quarter-torus-segment bands sharing circular junction arcs; and a **sharp corner
    between two straight rim edges MITERS on an exact ellipse**. Two equal-radius quarter
    cylinders whose axes intersect are a bicylinder, whose intersection is two ellipses;
    the branch through this corner has semi-axes `up·r` (vertical) and `bottom − centre`
    (horizontal, length `r/cos(Δ/2)` for a turn of Δ) — perpendicular by construction, so
    the exact conic is read straight off the two points the surgery already computed, with
    no trigonometry to round off. The circular junction arc is literally the
    `|bottom − centre| = r` specialization. Reflex corners work too (their bands reach
    PAST the edge's end to meet the miter, so the band surface is built to span it).
    A sharp corner at an ARC is refused: that blend pairs a torus with a cylinder and is
    not a conic.
    <br/>**Why a miter and not a ball.** A spherical patch is the classic corner where
    THREE blended edges meet. At a rim corner only two are blended — the two side faces
    keep their sharp shared edge — and a sphere of the fillet radius there is tangent to
    all three planes at single points, so at the tangency plane the cross-section would
    jump from a rounded corner to a sharp one. The miter is the only surface that closes
    the two-blended-edge configuration, and it is exactly what the union of the two edges'
    removed slivers produces: the cross-section at depth t is the face inset by
    δ(t) = r − √(r² − t²) with sharp corners, which makes the volume of a filleted prism
    analytic through the offset-polygon law
    `V = A₀·h − P₀·r²(1 − π/4) + (Σ tan(θᵢ/2))·r³(5/3 − π/2)` — signed turns, so reflex
    corners are covered by the same formula.
  - **Whole-solid filleting** (`FilletAllEdges`) is the OTHER classic corner: three blended
    edges meeting at a vertex, which is where the spherical patch belongs. It is built as
    the exact morphological opening (K ⊖ B_r) ⊕ B_r — for a convex polyhedron, erode every
    face plane inward by r, then dilate — so it needs no booleans and nothing to seal:
    every face keeps its own plane with a shrunk boundary, every edge becomes a cylindrical
    band about the ERODED edge line, and every vertex becomes a spherical patch on the
    ERODED vertex, bounded by great-circle arcs. Each curve is created once and handed to
    both of its faces, so senses follow mechanically and the result is manifold by
    construction (a box gives 6 + 12 + 8 = 26 faces, 48 edges, 24 vertices). Every face is
    FULL-DOMAIN, so it all tessellates on the natural grid and the volume converges
    quadratically onto Steiner's formula
    `V = V₀ + A₀·r + (r²/2)·Σ ℓₑθₑ + (4π/3)r³` — the last term because the eight octants
    are exactly one ball. Refused loudly: concave edges (an opening cannot round them),
    vertices of valence ≠ 3, and corners where no incident face is perpendicular to the
    other two. That last restriction is what keeps the patch an exact surface of
    revolution — the spherical triangle is then the lune between two meridians of that
    face's normal, closed by an equatorial great circle — and it holds for every box, every
    convex prism, and every sheared box. A general trihedral corner's spherical triangle
    has no exact revolved form, and there is no other tessellable surface type for it.
    <br/>All the corner arcs are `CurveSegment` over `Circle3d`, never rational NURBS arcs:
    the patch is a surface of revolution sampled at even ANGLES, so an arc parameterized
    any other way samples to different points and the patch stops welding to its band.
    <br/>Known gap: `StepWriter` exports these solids correctly (a STEP
    `SURFACE_OF_REVOLUTION` is unbounded by definition and the face boundary trims it), but
    `StepReader` cannot re-trim a CLOSED generator when the swept angle came from rails —
    the corner patches' meridian boundaries are circles through the axis, which no rim rule
    recognizes — so a re-imported rounded solid meshes non-manifold. The reader now emits a
    diagnostic saying exactly that instead of failing silently. Second known gap:
    `BrepBoolean` cannot yet cut a whole-solid fillet (a fragment's re-surfaced sub-band
    loses the corner arcs from its domain); the solid itself is sound — every loop point
    projects inside its own face's domain, which is a locked test — so this is a boolean
    limitation, not a construction one. Mitered RIM fillets do cut correctly.
  - **Selection** — by face (`FilletRim`/`ChamferRim`) or by EDGE (`FilletEdges`/
    `ChamferEdges`, and `RimFacesFor` which resolves a selection into the rim features
    that reproduce it). A complete planar face rim resolves; a partial run does not, and is
    refused before any surgery runs, because a band that stops partway along a rim has to
    terminate somewhere and every exact termination is a different surface. Filleting EVERY
    edge of a convex solid is refused for the same reason in reverse: its vertices need the
    spherical corner patch, which is a different construction from rim surgery.
  - Numerical rules the surgery depends on (all learned from real cracks): rim circles come
    from EDGE SAMPLES (`ActualCircle`), never from `Underlying` — a translated extrusion
    top's underlying circle sits at the base; every new rim edge is built in the top face's
    traversal direction, which fixes every sense mechanically; domain-driven neighbour
    surfaces (extruded/revolved) must be re-TRIMMED, since their tessellation grids ignore
    the loops; arc corner offsets are computed from the arc's own radial, because
    finite-difference tangents carry ~1e-9 of angular error — enough to rotate a band
    generator past the weld tolerance; and a band whose loop was mitered must still SPAN
    every loop point in its surface domain, for the same grid reason.
  - Tessellation note: mitered bands are genuinely trimmed faces (their loops cut across
    the parameter rectangle), so they take `TrimmedFaceTessellator`'s ear-clip + midpoint
    refinement rather than the natural grid. That is correct but costly — the refinement
    fills the band with an O(curveSamples²) triangulation where a strip of
    O(curveSamples) would do, and it leaves the mesh volume a few parts in 10⁵ under the
    exact one at default quality. A dedicated strip path for extruded trimmed bands in
    `BRepTessellator` is the follow-up.

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

### Named epsilon tiers

The epsilon ladder documented in `CLAUDE.md` has two named B-Rep constants, both on
`FaceGeometry`, both boolean-critical and both locked by
`FaceGeometryTests.EpsilonLadder_NamedTiers_HoldTheirDocumentedValues`:

- `FaceGeometry.SeamTolerance` (**1e-7, seam tier**) — the distance at which geometry
  constructed *independently* on the two sides of one shared curve still counts as
  coincident. Used by `TopologyEditor.SealSeams` (vertex unification + seam-edge
  merging), `Profile`'s chain-join check, and Interop's `BRepTessellator`
  full-domain boundary match. Geometry built EXACTLY on both sides (tessellation
  welds, mandatory seam breaks, clone dedupe) stays on the 1e-9 weld tier — do not
  promote those sites here.
- `FaceGeometry.InverseEvaluationTolerance` (**1e-6, inverse-evaluation tier**) — every
  `Surface.TryProjectPoint` pullback in BRep and Interop.

`FaceSplitter.CrossingParameterDedupe` (1e-8) is a *curve-parameter*-space window, not
model units: it merges crossings that name the same point (an endpoint hit reported by
two adjacent boundary edges; a mandatory boolean break landing on a crossing the
arrangement already found). The end-of-domain guards beside it scale by `Domain.Length`
instead, because they must stay meaningful on arbitrarily reparameterized curves.

The marching tracer's own constants are collected in `SurfaceIntersection.TracerSettings`
— see the surface-intersection section above.

Tessellation to meshes lives in `EngrCAD.Interop` (`BRepTessellator` +
`TrimmedFaceTessellator` for faces whose loops don't cover the surface's grid domain).
Helical bands tessellate as sheared grids whose columns are iso-axial rungs — the
first/last columns ARE the cap cuts — with every boundary point taken verbatim from
the shared edge polylines (band↔band and band↔cap welds exact by construction); helix
rails and spiral cuts sample proportionally to their turning angle. Boolean fragments
of helical bands cut by cap-plane spirals are still band-shaped (two rail pieces +
two spiral cuts) and take the same path.

## Not yet implemented

Coplanar/tangent boolean cases,
NURBS surface export. Filleting gaps, all refused loudly rather than approximated:
**spherical corner patches on non-perpendicular trihedral vertices** (`FilletAllEdges`
covers the perpendicular ones — boxes, convex prisms — which is where an exact surface of
revolution exists), **partial edge runs** (a band that stops mid-rim needs a
termination surface — cliff, setback or vertex blend — that this engine does not build),
**sharp corners at arc rim edges** (torus ∩ cylinder is not a conic), and
**variable-radius fillets**: the band itself would be exact — a linear radius law between
two equal-weight rational arcs is a degree-(2,1) NURBS whose v-sections are true circles,
and it stays G1 with both neighbours — but the corner where two such bands meet is the
intersection of two non-cylindrical surfaces, which is not a conic, so there is no exact
miter to weld them on. Variable-SETBACK chamfers do not have that problem (the corner
segment is a boundary ruling of both bilinear strips) and are the cheaper next step.
Loft gaps: sections must already be segment-compatible (no degree
elevation / knot merging), holes in sections, open (uncapped) skins, periodic lofts that
close back on the first section, guide curves / spine, and the "pipe shell with evolution
law" generalization (a section scaled and twisted along a spine — which is a loft whose
sections are generated rather than given, so it lands on `LoftedSurface` once a law
evaluator exists). `LoftedSurface` is not STEP-exportable (same bucket as swept surfaces).
Draft gaps: curved faces (the general face-offset-and-reintersect), caps with holes,
per-face angles in one call, and drafting about a non-planar neutral surface.
Shelling gaps: curved faces (cylinders/revolves offset exactly, but their corners need
surface–surface re-intersection — the same missing machinery as the non-perpendicular
corner patches above), higher-valence vertices, adjacent openings, variable per-face
thickness, and global self-intersection detection.
`HelicalSurface` faces cannot be exported to STEP (same bucket
as swept surfaces); helical faces trimmed into anything other than a rail/spiral band
(e.g. a helical band cut by a NON-perpendicular plane or another curved surface) have
no tessellation path, and helical∩cylinder / helical∩helical intersections fall to
the marching tracer.
