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
  `Parabola3d.ToNurbs()` is the segment over its domain as an EXACT quadratic Bézier
  (the parabola is the one conic that is polynomial: ends plus the tangent-intersection
  middle control point, unit weights, parameter affine over the domain).
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
  `SpiralArc3d` is a **conical spiral arc**: in the frame's cylindrical coordinates the
  angle IS the parameter and both the radius and the axial coordinate are linear in it,
  P(t) = O + X·r(t)·cos t + Y·r(t)·sin t + Z·z(t); exact derivatives. **This one shape is
  every curve a coaxial straight-generator surface of revolution cuts from a
  `HelicalSurface` band.** Substituting the band's r = r₀ + dr·v,
  z = z₀ + dz·v + rate·u into the carrier's r = a + b·z gives
  v·(dr − b·dz) = (a + b·z₀ − r₀) + b·rate·u, so v is linear in u and therefore so are
  the radius and the axial coordinate. The special cases are members of the one family,
  not separate types: a plane ⊥ the axis (the cap cuts of `MakeThreadedRod`) leaves
  `AxialRate` zero and the arc **planar**; a coaxial CONE (a thread's 45° end chamfer)
  is the general form; a zero `Slope` degenerates to a circular or helical arc. Always
  built on the band's own axis frame so its parameter IS the surface u (phase alignment).
  Two properties, and the difference between them earned its keep: `IsPlanar` is
  `AxialRate == 0` — lies in a plane perpendicular to the axis, which is what a consumer
  means and what the tessellator's full-band gate reads — while the arithmetic fast path
  that omits the axial term entirely needs the *stricter* "in the frame's own X/Y plane",
  because adding a zero VECTOR is not a no-op on a −0.0 coordinate and every threaded
  rod's cap loop must stay bit-identical.
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
  **`PolylineCurve3d.Carriers`** is the two exact surfaces a marching-traced intersection
  curve was marched on (attached by `SurfaceIntersection`'s tracer, preserved by
  `Simplified`, the boolean's end-snapping and `GeometryTransform`, and serialized by
  `BrepArchive` as two optional trailing surface refs): a traced curve's sample count is
  fixed at boolean time, and the carrier pair is what lets the tessellator refine its
  chords back onto the EXACT intersection at whatever density the caller asks for
  (`BRepTessellator.SampleEdge` → `SurfaceCorner.TrySolvePoint`). The failure direction
  is safe by construction — a derivation that cannot carry the pair drops it, and the
  curve samples at its baked vertices exactly as before.
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
  **`Curve2d.TryToCurvedEdge` / `Curve2d.FromCurvedEdge`** are the bridge to Core's EXACT
  curved 2D tier (`Geometry2.CurvedEdge2d` — the boundary vocabulary of `CurvedArrangement2d`,
  which lives in Core because Core cannot reference this project). `TryToCurvedEdge` is
  virtual and its default REFUSES rather than sampling — the same rule `ToCurve3d` states by
  being abstract — with only `Line2d` and `Arc2d` overriding, because lines and circles are
  exactly the tier that arrangement can decide soundly (its tangential tie-break is complete
  for them and would need an unbounded jet for a Bézier; see the Core README). The signed
  sweep crosses verbatim in both directions, so orientation is data on both sides.
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
    exporter fit curves with no analytic STEP form — traced polyline edges, RMF rails —
    instead of SAMPLING them into a degree-1 B-spline,
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
  **`IsAffineInV`** states the property a degree-1 TWO-section loft has and others do not:
  P(u, v) = (1 − v)·C₀(u) + v·C₁(u), so a v-chord at any fixed u lies EXACTLY on the
  surface and interior v samples buy nothing — the helical band's infinite-v-step rule,
  stated by the surface that satisfies it. The tessellator's natural grid collapses such
  a loft's v to the two section rows and samples its `LoftRailCurve` rails as the exact
  straight segments they are (one condition, both sides, so grid and edge polylines agree
  by construction). The rule earned its place by a measured defect: a variable fillet
  band's rail sampled at 25 near-collinear points forced the neighbouring planar face's
  ear clipping into sliver ears (18 of 23 facets degenerate at 48/24, non-manifold at
  128/96); with the collapse the rail is a 2-point segment and the slivers are zero.
  A degree-1 loft of MORE sections is only piecewise affine and deliberately does not
  qualify.
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
  **All three swept surfaces share ONE seed-selection rule** (`SeedSelection`): refine from
  every LOCAL MINIMUM of the sampled residual and from each minimum's two neighbours, never
  from the single best seed. `SweptSurface` learned this first — a sliver profile hides two
  branches inside one seed interval, so a single seed descends to whichever is nearer and
  returns the MIRRORED parameter — and the other two INHERITED the same weakness from the
  generic base rather than introducing it, so the rule now lives in one place instead of
  three. It is purely combinatorial: no epsilon decides which seeds are tried, so it cannot
  be right at one scale and wrong at another. Measured against a deliberately single-seeded
  reference on a 12-bump wavy vase generator (more features than 17 samples can resolve),
  round-trip acceptance over a 42×42 grid went **1092 → 1428** for both the extrusion and
  the revolve, and at 20 bumps **504 → 1008**; a circular generator extruded along a
  direction nearly in its own plane — whose projection perpendicular to that direction is
  the sliver — went from 1758 to a complete **1764/1764**. The fix also cleared a defect
  that had been diagnosed as something else entirely: `FilletAllEdges`' spherical corner
  patches reported 176 tessellation vertices "2.6e-3 off the surface", which was really the
  projection converging onto the mirrored branch of a great-circle generator that folds in
  (radius, axial). The vertices were on the surface all along, and "the vertex is off the
  surface" and "the projection converged somewhere else" produce identical evidence.
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
  `NurbsSurface` still uses the base grid, legitimately: no such reduction exists —
  and **the base grid itself is now multi-seeded**: after the GLOBAL-best seed (tried
  first and alone, so every call the single-seed implementation used to satisfy takes
  the identical iteration and returns bit-identical parameters), refinement runs from
  every LOCAL minimum of the 17×17 grid and its 8 neighbours, first success wins. A
  folded NURBS hairpin (branches 0.08 apart under a ~2-unit seed interval) measured
  **80/205** round-trip failures single-seeded and 0 multi-seeded. The fixture lesson:
  a MIRROR-SYMMETRIC hairpin is secretly benign — every wrong-branch seed has a
  same-branch mirror at the identical tangential offset minus the perpendicular
  penalty, so the argmin can never land on the wrong branch; hostility needs
  asymmetric branch parameterizations. The 1D overrides above now also DEFER to this
  base grid when their reduction fails (a heavily aliased generator where no 1D seed's
  basin contains the answer but a damped 2D step wanders in), so "the override is
  never worse than the base" holds by construction rather than by luck — locked by
  `SweptSeedSelectionTests.NeitherOverrideEverDoesWorseThanTheGenericGridSearch`.
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
  **`BrepSolid.Transformed(Matrix4d)`** is that same walk with the geometry moved through
  `GeometryTransform` — each surface answered in its OWN family (a moved cylinder is a
  `CylinderSurface`, a moved rim a `Circle3d`) rather than wrapped, so the tessellator keeps
  its sampling rules and STEP its analytic entities. **It is a POSE, not a re-derivation, and
  proper rigidity is exactly what buys that**: every parameterization here is built from
  lengths and angles, both of which an isometry preserves, so edge trim domains, seam phases
  and revolve angles are carried VERBATIM (asserted bitwise) rather than recomputed. Curves
  are exact per type with a `TransformedCurve` fallback — itself exact in position and
  parameterization, and its `Underlying` still reports the base type — so a curve never
  refuses; a surface has no such wrapper, so an unknown surface type refuses by name rather
  than approximating. Curve objects are mapped ONCE and reused, since a seam curve backs two
  edges and a carrier backs many, and splitting one carrier into several numerically-equal
  copies is how a solid stops welding without any count changing. Nothing internal needs
  this — the `Shape` compiler bakes transforms into construction inputs at lowering, which is
  exact for every operation — so it exists for geometry with no construction history to bake
  into: an imported STEP or IGES body being posed. **Three refusals, each with its own
  reason**: a shear or non-uniform scale changes the surface FAMILY (a sheared cylinder is an
  elliptic cylinder, a sheared sphere an ellipsoid); a **uniform scale** looks admissible and
  is refused for a bookkeeping reason rather than a geometric one — `PolylineCurve3d` is
  parameterized by cumulative chord length, so scaling its points scales its DOMAIN, while a
  `BrepEdge` stores its trim domain separately and a `CurveSegment` stores base parameters,
  and every tracer-produced edge is polyline-backed; and a **reflection** reverses
  orientation, so every loop would need re-winding and the handedness-carrying types
  (`HelicalSurface`, and a `RevolvedSurface`'s axis through F·Rot(d,φ)·F = Rot(−F·d,φ)) each
  need their own rule — `Shape.Mirror` already does all of that one layer up.
- **Face provenance** (`BrepFace.Provenance` / `DescendsFrom(parent)` / `AddProvenance(tag)`)
  — the persistent half of topological naming, beside the semantic `BrepQueries` selectors.
  A tag is stamped by the modelling layer (`Shape.Tag`) and then **inherited wherever a face
  is derived from another**. `DescendsFrom` is the ONE rule and every derive site asks it
  rather than restating a copy: `BrepSolid.Clone`, every `FaceSplitter` fragment and both
  `BrepBoolean` re-wound-tool sites, plus the five sites that rebuild a solid face by face
  from a **positional parent** — `Draft` (both the planar `BuildPrism` and the curved
  carrier path), `CarrierBody.Rebuild`/`Shell` (curved shelling and curved draft share it),
  `Shelling`'s polyhedral `Offset`/`Shell`, `Filleting`'s `FilletAllEdges` shrunk faces plus
  rim surgery's rewritten face and `TrimNeighborBand`, and `ShapeHealing`'s `WorkFace`
  rebuild.
  **Two properties carry it.** It is **set-valued**: a boolean can split one face into
  several, and shelling emits a wall AND its cavity twin from one parent, so a tag names a
  set and never "the" face. And the failure is **one-sided** — a site that invents surface
  simply does not tag, so a fillet band, a corner patch and a partial run's termination face
  descend from no single face and stay empty. A query therefore returns FEWER faces, never a
  face from somewhere else; an incomplete answer is a nuisance, a wrong one is a silent
  misresolve. `FaceProvenanceTests` measures each site by tagging exactly ONE face and
  asserting *where the tag landed* (located by `Bounds().Center` — `IsPlanar`'s origin is an
  arbitrary in-plane point and a circular loop's face-frame origin is its seam vertex, so
  both read the rim), since an off-by-one in a parent array leaves the count right and the
  meaning wrong.
- **`BrepQueries` / `BrepSelection`** — the LINQ selection vocabulary over topology.
  `BrepQueries` classifies and measures: `IsPlanar` (incl. extruded straight lines) /
  `IsCylindrical` (incl. extruded circles and axis-parallel revolved lines) / `IsLinear` /
  `IsCircular` / `Length` / `Bounds` / `IsConvex` / `NormalAt` / `Frame(face)`, adjacency
  (`face.Edges()`, `face.RimEdges()`, `solid.FacesOf(edge)`, `solid.ConvexEdges()`) and
  `PlanarFacesWithNormal`. `BrepSelection` is the **ordering/grouping layer** on top
  (build123d's `sort_by`/`group_by`/`filter_by`, deliberately as extension methods rather
  than string selectors): `SortAlong(direction)` / `Extreme` / `Highest` / `Lowest`
  (planar faces rank by their exact plane offset when the direction is parallel to their
  normal — a plane's stored origin is an arbitrary in-plane point, so only that component
  is well-defined — and by bounds centre otherwise; edges rank by curve midpoint),
  `GroupAlong(direction)` (rank clusters ascending), `GroupByCoplanar` (same outward
  normal AND same offset at the weld tier; non-planar faces stay singletons, nothing is
  dropped), `FilterBy(SurfaceKind)` / `Kind()` (semantic kinds — Planar / Cylindrical /
  Conical / Spherical / Toroidal / Revolved / Extruded / Swept / Nurbs — with revolved
  generators classified by SAMPLING, because the kernel spells circular generators three
  ways and a curve-type switch would classify identical geometry differently),
  `GroupByRadius` / `NthByRadius(n)` (distinct radii ascending, negative indexes from the
  largest, out-of-range failures name the radii that exist), and **`Area(face)`** —
  exact for planar faces (closed-form boundary-integral terms for straight edges,
  `Circle3d` edges and `CurveSegment`s over one; other curves chord-sampled) and
  midpoint quadrature over the pulled-back trim region for curved faces (~1–2% at the
  default resolution — ordering-grade for `LargestByArea`/`SortByArea`, not
  mass-property grade; that is `BrepMassProperties`' job). Everything is deterministic:
  stable sorts, first-wins ties, groups in ascending rank or first-appearance order.
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
  **`Profile.FromCurvedRegion(region, frame?)`** is the EXACT counterpart, taking Core's
  `Geometry2.CurvedRegion2d` — the output of the curved 2D boolean and offset, whose
  boundary still carries circles. The profiles it returns hold `Circle3d`s and trimmed arcs
  rather than chords, so an extruded sketch-boolean result is an analytic B-Rep with the
  closed-form volume instead of a prism whose error is a floor no tessellation density can
  lower; a region whose whole boundary is one full circle becomes a single-closed-curve
  profile, exactly as `Profile.Circle` would, so an extruded disc is a three-face cylinder.
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
    The continuous shift is FINISHED by Newton on dJ/ds = 0 with the curve's exact
    derivatives — derivative-free minimization of a smooth quadratic stalls at √ε ≈ 1e-8
    of the shift (the recorded distance-minimization trap: root solves, never distance
    minimization), and the measured cost was two coaxial circles, whose true optimum is
    EXACTLY zero, coming back with a 1.09e-8 phase that put a twist into every ruled quad;
    the polish lands the true optimum and an exactly-aligned pair then takes the
    wrapper-free path, restoring loft-vs-cone tessellation identity to nine decimals.
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
    phases). V = 2K, E = 3K, F = L = K + 2 ⇒
    Euler–Poincaré 0 at genus 0. Exact volume for ANY length:
    L·(2π/P)·∫₀^P ½R(s)² ds (the full angular sweep at each z washes out the phase).

    **`leftHand: true`** winds the same profile the other way. It is not a second
    construction: the axial rate becomes −P/2π and every formula here already carries the
    signed rate, so a rail's phase u runs DOWN as z runs up and three consequences follow
    mechanically — a rail's helix is anchored on the TOP cap (`Helix3d`'s domain always
    starts at its own frame's plane, so a descending rail must start there), the u = min
    and u = max edges of a band swap which cap they are, and both cap loops chain the
    other way round. Counts, Euler and the volume formula are unchanged. The result is
    the EXACT mirror of the right-hand rod: measured 0 residual on every band surface and
    9e-15 at the vertices (helix trigonometry).

    **A left-hand rod is not a right-hand rod on some other frame.** Every right-handed
    frame is a rotation of every other, so no choice of pose can flip handedness — it has
    to enter the arithmetic. (Two flipped axes is a rotation, which is the trap: `FlipY`
    alone is the reflection, `FlipY·FlipZ` is a half-turn about X.) The Shape compiler
    uses the mirror identity in reverse to lower `Mirror(thread)`: writing
    m = (m·FlipY)·FlipY leaves a proper similarity placing a rod of the opposite
    handedness, which is why a mirrored thread is Native rather than refused.

  - `MakeThreadEndChamferTool(majorRadius, chamferLength, endAxial, atMaxAxial[, frame])`
    — the solid a 45° lead-in chamfer REMOVES from such a rod: the region outside a
    coaxial cone that reaches `majorRadius` exactly `chamferLength` from the end face.
    Subtract it and the chamfer is exact, because a coaxial cone cuts every helical band
    in a conical `SpiralArc3d` (see `SurfaceIntersection`).
    **Every face of the tool but the cone clears the rod**, which is the whole reason it
    is a four-corner revolve rather than the cone alone: the generator is extended past
    both ends by a quarter of the chamfer, so the flat bounding it sits at a radius
    strictly outside the rod and the flat capping it sits axially outside the end face —
    the tool-overshoot rule `Drill` follows, which keeps every intersecting pair
    transversal. (That overshoot is belt and braces rather than load-bearing now that a
    coaxial annulus is itself an exact cut; a tool whose flat rim lies ON the crest
    cylinder is exactly the input that found that gap.) A chamfer at or past the thread
    depth puts the cone tangent to every root band along the end plane — coincident
    curved-surface boolean input — and is refused one layer up, in the Shape compiler:
    this factory builds the tool for whatever chamfer it is given.

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
  The planar path handles **planar-faced prisms** — two caps perpendicular to the pull
  direction, single-loop caps, four-sided planar sides — and rejects caps with holes,
  selecting a cap, and a taper large enough to fold the profile (checked by winding *and*
  per-edge direction against the original loop, since a signed area alone can stay positive
  while one edge has already reversed).
  **Curved faces taper too, and exactly.** A face of revolution about the pull direction
  drafts by rotating its GENERATOR in its own axial half-plane about the point where that
  generator crosses the neutral plane — the same "rotate about the neutral line" rule, one
  dimension down. A drafted cylinder is therefore *exactly* a cone (a `RevolvedSurface` of a
  straight generator, not a cone to some tolerance), a drafted cone another cone, and a
  drafted torus band another torus band, since a rigid rotation of a circular profile is
  still a circle. A cylinder has no generator of its own, so one is synthesized as a line in
  its own X half-plane — which is what makes the drafted cone keep the cylinder's PHASE, its
  rim circles still landing on the carrier's grid. Rims are then re-solved through the shared
  `CarrierBody` rebuild (see below). The rotation SENSE is chosen by measurement rather than
  derived: both candidates are built and the one whose normal leans further toward the pull
  direction wins — deriving it would mean re-proving the half-plane's handedness against the
  generator's traversal and the revolve's outward convention, three sign conventions that have
  each cost this kernel real geometry, for an answer one dot product settles. Refused by name:
  a curved face on any other axis (its drafted carrier is not a surface of any family this
  kernel builds).
  **Per-face angles in one call**: `Draft.Apply(solid, neutralOrigin, pullDirection,
  angleSelector)` takes a `Func<BrepFace, double?>` (null = leave the face) — every corner
  is solved once from its two final planes, which is exactly what chaining single-angle
  drafts computes too (the operation is closed-form and composable, locked by a
  positions-bit-equal-to-the-chain test), just without re-recognizing the prism per angle.
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
  **Per-face wall thickness**: `Shelling.Shell(solid, Func<BrepFace, double> thickness,
  openingSelector?)` asks the selector once per non-opening face (a thick base under thin
  walls) — nothing new geometrically, since each inner corner was already the intersection
  of its three offset planes and unequal offsets just move that intersection.
  **Curved faces work too, and are equally exact** (a separate code path, `CarrierBody`, so
  polyhedral output stays bit for bit what it was — the switch is a property of the INPUT, not
  of the request): `SurfaceOffset` keeps every carrier in its own family and `SurfaceCorner`
  re-solves every vertex as an exact root. A cylinder shells to a cup, a cone frustum to a
  conical cup, a sphere offsets to a sphere, and a quarter-turn pipe elbow opened at both ends
  to a genus-1 tube whose volume matches Pappus. Two rules that path had to learn:
  **a curved rim is CONSTRUCTED and then VERIFIED, never intersected and trusted** (a
  concentric circle about the original's own axis and phase, then every sample measured
  against both offset carriers — a rim the construction cannot reproduce is refused by name,
  which is exactly what catches a SEALED elbow, whose moved cap planes cut the offset torus in
  a quartic rather than a circle); and **a domain-driven carrier must be re-trimmed to its new
  loops** — an inward offset moves a cone's rims axially as well as radially, so the inner
  band's `RevolvedSurface` grid keeps sampling its old extent and the trimmed tessellator
  refuses the face outright. The generator parameter for that trim is solved geometrically
  against the loop points' own axial coordinate, never by projecting them onto the surface
  (the vCut lesson: a ~1e-7 projection error times the cone's slope shifts the ring radially
  past the weld tolerance).
  **Higher-valence vertices are solved rather than refused wholesale**: four or more faces
  at a vertex is over-determined and usually has no offset position at all, but "usually" is
  not "always" — a square pyramid's apex has four planes that meet in a point BY SYMMETRY,
  and offsetting each of them keeps that true. So the over-determined case goes through the
  least-squares corner solve and is then CHECKED, and only a vertex whose offset planes
  genuinely miss is refused (the message says a corner like that opens into a small face,
  which is corner-patch construction rather than a carried-over rebuild). Rejections are
  loud: curved faces with no exact offset (swept and NURBS surfaces), non-circular curved
  edges, non-concurrent higher-valence vertices, adjacent openings (zero-width rim),
  openings on a face with holes, multi-shell inputs, and an offset that locally folds the
  solid. **Not** checked: an offset large enough to make distant surfaces pass through
  each other with no local symptom — the same contract OCCT offers and `OffsetCurve3d`
  already documents for curves.
- **`SurfaceOffset` / `SurfaceCorner` / `CarrierBody`** — the curved-corner re-intersection
  machinery three operations were blocked on (curved shelling, curved draft, variable-radius
  fillets), built once rather than three times.
  - **`SurfaceOffset.TryOffset(surface, distance)`** lifts a carrier along its own normal and
    stays in the SAME family: a plane offsets to a plane, a cylinder to a cylinder, a sphere
    to a sphere, and a revolve to the revolve of its OFFSET GENERATOR — which for a straight
    or circular generator is again straight or circular, so a cone offsets to a cone and a
    torus to a torus. Frames, generator spans and sweep angles carry over verbatim, so the
    offset surface's u = 0 sits where the original's did (the phase-alignment rule: a rim
    circle lands on the carrier's grid only if the frame did not rotate). Internally
    `CircularArc` recognizes a circular curve by SAMPLING it and rebuilds it in the **same
    spelling** (`Circle3d` / `CurveSegment` / rational arc) — those agree on the point set and
    NOT on the parameter mapping, and a revolve samples its generator at even parameter, so a
    respelt arc silently moves every sample. Two traps it carries: a straight generator
    extruded along ANY direction is still a plane, so a *sheared* straight extrusion offsets
    exactly — but by the UNIT normal, since the raw cross product is short by the shear's sine
    — while a sheared CURVED generator has no same-family offset; and the fit must use the
    EDGE's domain, because a partial revolve's rail is a whole `Circle3d` used over a quarter
    turn and fitting the carrier reports a full-turn sweep.
  - **`SurfaceCorner.TrySolvePoint(carriers, seed)`** is the corner itself, and a corner POINT
    is never approximate whatever the carriers are — it is the root of a small square system,
    and Newton on exact carriers converges to machine precision. Three planes take the
    algebraic Cramer solve verbatim (so polyhedral output cannot move); anything with a
    closed-form implicit distance (`ImplicitSurface`: plane, cylinder, sphere, cone or
    circular revolve, straight or circular extrusion) Newtons onto the root. The step is the
    **minimum-norm** one — the corner moves only within the span of its carriers' normals —
    and that single rule reproduces every case that would otherwise be hand-written: a SEAM
    vertex has two incident faces because a closed rim starts and ends there, and a TANGENT
    junction has three faces of which two are the same carrier (an elbow's profile circle
    split into two arcs offsets to two halves of ONE circle; a sphere's two hemispheres to one
    sphere). In each, the normal span is exactly the direction the geometry is free in, so a
    cylinder's seam stays in its seam half-plane and an elbow's junction keeps its angle on
    the offset profile circle without either being written down. The residual is always
    reported; more than three independent carriers is least squares and refused when they
    genuinely miss.
  - **`SurfaceCorner.TrySolveCurve(a, b, start, end, policy)`** is where the exactness brand
    bites. Analytic pairs give a conic trimmed exactly to its corners; everything else is the
    marching tracer, whose chordal output would bake a fixed sampling floor into a primary
    feature's B-Rep. `CornerPolicy.ExactOnly` is the default and refuses by name; `AllowTraced`
    is opt-in and LABELS the result (`CornerCurve.Tier`) with its measured `Deviation`. Even
    then the curve is made to TERMINATE exactly — its ends are replaced by the solved corner
    points, so the chord error stays strictly interior and never reaches a vertex, the one
    place the weld tier is absolute.
  - **`CarrierBody`** is the rebuild: recognize a solid's topology once, hand it one carrier
    per face, and get back a valid solid with every vertex re-solved, every edge reconstructed
    and every domain-driven grid re-trimmed. `Shelling` supplies offset carriers and `Draft`
    tapered ones; the machinery between is identical.
- **`SurfaceIntersection`** — `Intersect(a, b, region)`: exact analytic curves for the
  common quadric pairs (lines, circles, exact ellipses), plane ⊥ helical-axis cuts
  (exact `SpiralArc3d` on the band's own frame — the SAME arithmetic
  `MakeThreadedRod`'s cap cuts use, so seams weld; a dz = 0 helicoid ramp cuts in an
  exact radial line), **any COAXIAL surface of revolution with a straight (radius, axial)
  profile against a helical band** — a 45° end-chamfer cone, a coaxial cylinder (whose
  cut is one complete iso-v helix, the runout-diameter case) — as the exact conical
  `SpiralArc3d` derived above, clipped to v ∈ [0, 1], to the carrier's own generator
  extent and to the band's u domain, with parallel profiles (dr = b·dz) reporting nothing
  since they never cross transversally. **A coaxial ANNULUS joins that family from the
  other end**: its generator is perpendicular to the axis, so no `r = a + b·z` form
  describes it at all (its b is infinite) and it is recognized separately
  (`TryCoaxialDisk`) and cut as the axis-perpendicular PLANE it is, clipped to its own
  radial extent — one implementation of that cut, shared with the `PlaneSurface` case.
  Recognizing it is not a nicety: a chamfer tool's flat is exactly this surface, and
  without the arm the pair fell to the marching tracer, whose polyline hugged the
  annulus's own v = 0 edge where its rim sat on the crest cylinder and ended strictly
  inside the band — which face splitting refuses by name, three faces away from the
  cause. **And a cone's cut on a CONSTANT-radius band is a circle, exactly**: a crest or
  root flat has dr = 0, so the cut's axial coordinate is the single z where a + b·z = r₀,
  computed in that closed form rather than through the general v-linear expressions,
  which reach it only up to rounding. `SpiralArc3d.IsPlanar` is an exact-zero test every
  downstream tier reads, so with the rounded form the SAME chamfer came out planar at one
  end of a rod and not at the other; **bounded planar carriers** (below), and a general marching tracer
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

  **Seeding is two passes, and the ORDER is the safety argument.** The first is the
  historical isotropic `SeedResolution`×`SeedResolution` grid in (u, v), emitted in its
  original order. The second runs only for surfaces whose two parameter directions differ
  in MODEL length by more than `SeedAnisotropy` (4), and re-grids them with the same sample
  budget redistributed to match the shape — `nu·nv ≈ resolution²` with `nu/nv = aspect` —
  so the spacing is roughly equal in millimetres rather than in parameter units. Because
  `March` traces seeds in order and skips any seed already covered by a traced branch,
  every branch the old grid found is still traced FIRST and from the SAME seed: its
  polyline is bit-identical, and the second pass can only add branches that used to be
  missed entirely (the whole suite is green and unchanged, which is what that claim means
  operationally). The case it exists for is a thread band: an M8 crest flat wound over
  thirteen turns is ~330 mm long and 0.16 mm tall, an aspect ratio near 2000, so the
  isotropic grid puts its columns ~13 mm apart along the band while spending 24 rows across
  a strip a sixth of a millimetre wide — narrower than a Ø6 cross-drill's whole window.
  Measured against that drill: the crest band returned **zero** branches and the flank band
  six; with the second pass, three and nineteen.

  **The extents are measured as SPEED × domain length, never as a chordal polyline** —
  `ParameterExtents` averages |∂P/∂u| over a 4×4 cross of cell CENTRES (never the domain's
  own nodes, since `Eval` clamps there and a central difference taken at a boundary is
  silently one-sided and halves the speed it reports). A chord between two samples of a
  coiled band measures across the coil rather than along it: an 8-sample polyline reported
  61 mm where the band is 327 mm long — a number with no relation to either. The speed form
  is exact for helices, circles and lines alike, because their speed is constant.

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
  - **A bounded planar carrier ∩ a quadric** (`TryPatchQuadric`) — the SAME exact curves
    the main switch produces for a real `PlaneSurface` (cylinder, sphere, revolve ⊥ its
    axis, sphere-carrier revolve), **clipped to the runs the patch actually carries**
    (`ClipConicToPatch`). This is the drilled-side-wall case: a bore's rim on an extruded
    wall used to come back as a fixed ~57-sample tracer polyline while the identical bore
    on a flat cap got a `Circle3d`, so the wall bore's volume error *floored* — measured
    −7.4e-4 / −5.3e-5 / +4.7e-5 / +6.5e-5 at 32/64/128/256 segments, wandering rather than
    converging — and is now 7.1e-14 / 6.8e-14 / 4.3e-14 / −5.3e-14, exact as an identity.
    **BOUNDED patches only**: an unbounded `PlaneSurface` still takes the main switch
    verbatim, so nothing in the boolean pipeline's regression surface changed route.

    **The clip is one harmonic per patch edge.** Both patch coordinates are affine in the
    point and a conic is affine in (cos θ, sin θ), so each is exactly
    `c + a·cos θ + b·sin θ` — and each of the four edges is one equation
    `R·cos(θ − φ) = level − c` whose two roots are `φ ± acos(…)`. Membership is then decided
    at the MIDPOINT of each interval between consecutive roots, because the four
    constraints are independent (two crossings of `s = 0` can bracket a stretch that leaves
    through `t = 1`), and a midpoint is strictly inside or strictly outside every one of
    them, so the test is exact and no epsilon enters. A conic wholly inside is returned as
    ITSELF, by reference, so every input the old containment test accepted produces
    bit-for-bit what it always did and a closed curve stays closed (the wrap-splitting and
    hole-splitting paths key on `IsClosed`); a run straddling the seam comes back as ONE
    `CurveSegment` running past the domain end, which a closed base makes meaningful.

    **Closed form is what makes the ends WELD.** A clipped end becomes a VERTEX shared with
    the neighbouring face — a bore breaking out of a wall's top edge ends exactly where the
    top face's own intersection curve starts — so it must not carry a sampling error. On a
    Ø6 bore whose rim runs off a wall's top edge, the arc's ends measure **exactly** the
    wall's top (`|z − 2.5| = 0`) and land within **0 and 4.4e-16** of the top plane's own
    `±√(r² − d²)`, i.e. 0–2 ulps, against a tracer that stops up to one march step short of
    the boundary and never reaches it at all. Every sample of the clipped arc stays inside
    the extrusion's own `[0, 1]²` — measured escape **0.000** — which is the geometric guard
    the `Promote` trap earns: "lies on the carrier" is not "IS the carrier", so the answer
    is checked by sampling rather than by a type test.

    **A conic TANGENT to a patch edge needs a rule, and the rule is derived rather than
    chosen.** Near a tangency the `acos` argument is within round-off of ±1, where `acos`
    has a square-root singularity, so the two roots come back ~1e-7 rad apart however exact
    the geometry is and the midpoint between them reads inside or outside by round-off.
    Neither reading is more accurate — but they are not equally safe: DROPPING a run of
    angular span δ removes a chord of `scale·δ` from the curve, an outright gap, while
    KEEPING it leaves the curve at most `scale·(1 − cos(δ/2))` outside the patch, second
    order in δ. So a dropped run is flipped whenever that excursion is inside the weld
    tolerance — for a real tangency, by six orders — and a touching conic comes back as the
    CLOSED conic it is. **Whether the round-off falls the safe way without the rule is
    ALIGNMENT, not tolerance**, so the test is a family: measured, **62 of 480** tangency
    configurations come back with a pinhole in them without it and **0 of 480** with it.
    (The separate 1e-9 rad `DuplicateRootAngle` is a structural cleanup for a conic crossing
    a patch CORNER, where two edges give the same root twice and the zero-span interval
    between them has no midpoint to decide.)

    Still deferred: **the axis-parallel LINE pair** (a bore drilled ALONG a wall rather than
    into it), which is not a conic and keeps its tracer route — and measurably needs no help,
    since `SnapTracerEnds` plus `SampleEdge`'s baked-carrier refinement already take that
    family to quadratic convergence.

  **Cylinder promotion needs the WHOLE circle** (`Promote` / `WrapsWholeCylinder`). An
  extrusion of a full circle along its axis IS a `CylinderSurface`, and promoting it is
  what gives drilled bores exact analytic rim circles. An extrusion of an ARC is not —
  it is a bounded patch on that cylinder — so the guard samples the ACTUAL generator and
  requires both that every sample sit on the candidate cylinder and that the accumulated
  swept angle be a full turn. **The angular half is load-bearing, not belt-and-braces**:
  a rounded rectangle's corner is a quarter arc extruded, every point of which lies on
  the full cylinder, so the previous start-point-only guard promoted it and the kernel
  then reported intersections around 270° of surface the face does not carry. Measured
  consequence: on a 60 × 40 × 10 plate with Ø12 corners, a THROUGH counterbore anywhere —
  dead centre, 27.8 mm from every corner, as well as the reported Ø8 hole 10 mm from a
  corner — failed with `Open splitting curves must start and end outside the face`,
  because the fabricated far side of a corner cylinder crossed the tool's band and the
  tracer's open curve ended strictly inside it. Tightening the face-bounds prefilter could
  not have separated that pair (the two AABBs touch at a corner, and the prefilter is
  deliberately conservative-over — see todo.md). With the guard in place the same drill is
  a plain hole, and the near-corner and dead-centre placements remove volumes agreeing to
  2.6e-10. A quarter-arc corner now also sections against a parallel plane as the exact
  translated ARC (via the generator-translate path) instead of a whole fabricated circle.

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
  point-in-face classification (**two spellings, deliberately**: `Contains` is the
  one-sided upward-v ray parity the splitter's arrangement and the boolean's fragment
  classification have always used, and it has a blind spot — an upward ray cannot see a rim
  BELOW the probe, so it calls every point of a pole-bounded face outside; `ContainsTwoSided`
  runs the same parity in BOTH v directions, which agree on a properly closed trim and
  disagree exactly when one side of the domain is a degenerate POINT, and resolves the
  disagreement to *inside*. That makes it both the pole-correct rule and the one that errs
  toward inside, which is what a keep-or-drop decision wants — `PlanarSection`'s cross-section
  test, where the finding originated (a sphere's section came back empty), and the boolean's
  carrier clip both ask it), splitting faces by closed interior curves (hole + disk
  sharing one manifold edge), open/crossing curves (full parameter-space arrangement:
  boundary edges split at crossings — refined by 3D curve–curve Gauss–Newton, exact from
  both solids' sides; crossing seeds slightly inclusive so cuts through split-created
  vertices are not missed — interior segments as shared two-use edges, sub-faces traced
  by tightest-turn walking: clockwise for CCW-wound faces, counter-clockwise for
  reversed ones, `IsReversed` preserved throughout), and period-wrapping curves
  (`SplitBandByWrapCurve`: constant-v cuts → two exactly re-surfaced sub-bands, on
  extruded, **cylindrical** and fully revolved bands alike;
  NON-planar wrapping cuts — the cylinder∩cylinder curves where a cross-drill pierces
  a bore — → `SplitBandByNonPlanarWrapCurve`: both sub-bands KEEP the original surface,
  since no parameter line exists to trim at, and rely on trimmed-face tessellation;
  loops go to the side of the cut their v-range lies on, overlapping ranges — tangent
  configurations — throw).

  A plain `CylinderSurface` band differs from the other two in exactly two places, both
  the same fact: **it tessellates from its RING LOOPS, not from a parameter grid**. So
  both sub-bands keep the whole cylinder — shortening the loops IS the edit, where the
  extruded and revolved cases must shorten their surfaces too because their grids ignore
  loops — and vCut is `(cutStart − origin)·axis` exactly, one dot product with no
  projection error to refine away. A cylinder is also unbounded in v, so the "the cut
  coincides with a boundary ring" test reads the rings; against an infinite domain it
  could never fire. Bores from `Shape.Drill` and `SolidFactory.Extrude` are extruded
  circles and take the domain-driven path, which is why this went unexercised for so
  long. The NON-planar case (a cross-drill piercing the wall, or a tilted plane cutting
  it in an ellipse) **now works on a plain cylinder too**. It used to refuse by name, on
  the reading that its fragments "would need trimmed cylindrical tessellation with
  wrapping loops, which does not exist" — but the splitting was never the problem and
  neither was the trimmed path, which pairs wrapping loops by pulled-back u perfectly
  well. The defect sat one stage later, in `BRepTessellator`'s ROUTING; see the Interop
  README's `IsRingPairedBand` note.
  `TopologyEditor`
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
  else uniformly. The rule and its predicate (`FaceGeometry.IsPolylineBacked`) are
  **public**, and two more call sites now go through them rather than restating the
  test — the tessellator's `SampleEdge`, whose local copy checked for a raw
  `PolylineCurve3d` only and so dropped every `CurveSegment`-wrapped tracer curve onto
  the uniform path, and `TraceFaces`' tightest-turn probes, which read a departure
  direction 2% along an edge's domain and therefore read it from a point a sagitta off
  the surface. Those probes now use the nearest exact vertex parameter, falling back to
  the FAR endpoint for a single-chord edge (an on-surface vertex, and a chord's exact
  direction). **A third site joined them**: `SplitByCurve`'s "is this stretch of the cut
  inside the face?" probe took the ARITHMETIC midpoint of the interior stretch, which on
  a tracer polyline is a mid-chord point — measured 5e-3 off the surface on a whole-solid
  fillet's quarter-arc band at the tracer's own sample density, five thousand times the
  inverse-evaluation tolerance. `TryProjectPoint` then failed, the stretch was discarded
  as "leaves the surface entirely", no seam edge was built, and the face came back
  UNSPLIT with no complaint — which is how a bore crossing a fillet band cracked the
  result along entire tangency edges. `InteriorProbeParameter` takes an exact interior
  sample instead; a stretch spanning no vertex has none and keeps the midpoint, and
  non-polyline curves keep it bit-for-bit (an analytic curve is exact everywhere, so
  there is nothing to fix and no reason to move an existing probe). Note the predicate is
  deliberately not a test on `Underlying`: a segment's
  underlying curve is its base's, which says nothing about the parameter mapping the
  rule turns on.

  **A `CurveSegment`'s WRAP is only meaningful on a CLOSED base** (`CurveSegmentWrapTests`).
  A segment straddling a circle's seam legitimately runs past its base's domain end, and
  there wrapping IS what the parameter means — but an OPEN base has nothing on the other
  side, and the clipping arithmetic that builds these segments routinely overshoots the
  end by an ULP. The unconditional wrap did not nudge such a sample, it TELEPORTED it to
  the base's START. Measured on a thread's end chamfer: a conical spiral trimmed to a face
  ended one ulp past its carrier arc's domain, so the edge's last sample came back 0.375 mm
  away at the other end of the arc, off the face entirely; the pulled-back loop then
  carried a stray vertex at the surface's v = 1, the trimmed band tier stopped recognizing
  a band, the ear clipper ran instead and folded 244 of 3562 facets — three stages
  downstream of one ulp, and reported as a non-manifold weld. An open base's parameter is
  now clamped to its own domain.

  Closed curves interior to a face honor **mandatory seam breaks**
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

  **`SplitByCurves(face, curves)` is where the routing decision lives, and there are two
  ways to split a face by several curves.** A **CASCADE** — each curve applied in turn to
  the fragments the previous ones produced — is what booleans have always done and remains
  bit-for-bit what they get: a curve that enters and leaves through the face's boundary
  closes its own arrangement, and a later curve meets an earlier one's segments as ordinary
  boundary edges of the fragment it is splitting, so curve–curve crossings come out for
  free. **ONE SIMULTANEOUS ARRANGEMENT** is required when a curve TERMINATES INSIDE the
  face, which the cascade structurally cannot do: the first curve applied has no partner
  segments to end on, so its arrangement has a dangling edge and cannot be traced. That is
  exactly the shape a face-pair intersection curve takes once it is clipped to the OTHER
  face's trim — it stops where the neighbouring face's boundary crosses this face's
  interior, and the curve continuing from there belongs to the neighbour. `SplitByCurve` is
  the one-curve call into the same entry point, so the incumbent path is reached through the
  new one rather than beside it.

  The simultaneous path's nodes come from four sources, all found **before a single coedge
  moves** (the rim-surgery all-or-nothing rule: `SplitEdge` patches neighbouring faces'
  loops, so a refusal halfway would leave them half-edited): a curve crossing the face
  boundary; two curves crossing each other (`CurveCrossings` + `RefineCurvePair`, the
  curve–curve twin of the boundary refinement, seeded in uv and solved in 3D for the same
  sagitta reason); two open curves whose **ENDPOINTS COINCIDE**; and mandatory seam breaks.
  The third is not a special case of the second and must not be reached for by loosening a
  transversality test — two clipped curves meeting end to end **touch**, and whether a
  crossing test fires there depends on the angle they happen to meet at. Nodes within the
  seam tier of one another are ONE node, so a corner where two clipped curves and the
  boundary all meet yields one vertex rather than three. A dangling end with nothing to meet
  is refused by name, reporting the point and how many curves were offered for the face —
  the usual cause being that the curve it should have met was never handed to this face.

  **The gate is "a curve stops strictly inside the face WHERE ANOTHER CURVE IS THERE TO MEET
  IT", and both halves were paid for.** *Strictly* means clear of the boundary by the seam
  tier, since an open cut may legitimately end exactly ON the boundary (a plane∩helical-band
  spiral arc ends on the band's rails). The partner clause is what separates a CLIPPED curve,
  whose terminus is a real corner the neighbouring face's curve continues from, from a
  **TRACER-TRUNCATED** one, whose terminus is an artefact of the marching step and has nothing
  to meet — and tracer truncation is common, so without the clause the new path engages on
  ordinary geometry. The arrangement cannot help a curve with no partner; it can only trade
  one refusal for another. One curve therefore never qualifies, which is what makes
  `SplitByCurve` exactly the incumbent function.

  **A closed CHAIN that wraps the period is not a hole** (`ChainWrapsPeriod`, asked by the
  boolean's interior-chain extraction before it commits to the chain path).
  `SplitByClosedCurveChain` makes a face-with-hole plus a disk, which is the right topology
  for a contractible chain and the wrong one for a wrapping cut: the two sides of a wrapping
  cut are two BANDS, each still reaching right round, and neither is a hole in the other. Such
  chains only started arriving once the boolean clipped its carrier curves — a bore breaking
  out through the rim of a flat cap gets its z-constant cut trimmed to the arc the cap shares,
  so the bore wall's cut is that arc joined end to end with the curve where the wall leaves
  the body, and together they still go right round. A wrapping chain goes to the arrangement,
  which already pairs wrapping loops bottom-to-top by v.

  Two numerical rules the chain path needs, both no-ops on an aperiodic surface:
  **each pulled run must be shifted into the running polyline's period before it is
  appended** (`PullCurve` unwraps a run only against ITSELF, starting from the raw projection
  of its own first sample, so on a periodic surface the concatenation jumps by ±period at
  every junction and both the signed area that decides the winding and the drift that decides
  whether the chain wraps become noise — measured on a Ø6 tool crossing a rounded box's fillet
  bands, whose 8-curve chain on the tool's cylindrical wall spans 227° straddling u = 0: the
  disk came back wound CW on an unreversed face, every one of its eight edges had the same
  sense as its partner, and `Validate` refused the result); and **wrapping boundaries at the
  SAME v must be ordered TOPS FIRST** in `TraceFaces`' band pairing, because they are the two
  sides of ONE cut and a cut closes the band below before it opens the band above — otherwise
  the order between them is whatever the trace happened to produce and half the time the walk
  sees two bottoms in a row and refuses. They are GROUPED by a scale-free v window rather than
  sorted through a tolerant comparator, which would not be a strict weak ordering.

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

  **"Wraps the period" is net u DRIFT, never u SPAN** — one rule,
  `FaceGeometry.LoopWrapsPeriod`, asked by all three sites that need it (this tracing,
  wrap-splitting's "every loop must span the band" precondition, and
  `BrepBoolean.ProbePoint`). A contractible loop may reach most of the way round the
  period and come back: a threaded rod's end-chamfer facet on its cone spans **272°** and
  closes with a net drift of 0.02 rad. A span test files that as a band boundary and every
  consumer then does something structurally wrong with it — the tracer hunts a partner
  boundary to pair it with, wrap-splitting lets a wrapping cut fabricate a phantom band out
  of it, and `ProbePoint` walks halfway toward the surface's own v domain edge and lands
  outside the fragment entirely, so the boolean classifies the facet away. A genuine wrap
  drifts a full period; a contractible loop returns to where it started. The span survives
  as the cheap first half of an AND (no loop can drift a period without spanning one).

- **`Filleting`** — rim chamfering and filleting as topology surgery on an existing solid
  (no booleans): the outer rim of a planar face is replaced by a bevel or blend band, the
  face shrinks, and the neighbours drop.
  - **Chamfer** — straight rim edges become planar strips that MITER at sharp corners
    (both strips contain the straight corner segment, so it is their shared edge by
    construction); a full circular rim becomes an exact cone band. Sizing is two setbacks
    (`ChamferRim`) or the "distance and angle" spelling `ChamferRimAtAngle(setback,
    degrees)` — setback measured IN the chamfered face, angle measured FROM it, 45° being
    the symmetric case.
  - **Variable-setback chamfer** — `ChamferRim(solid, face, setbackAt)` /
    `ChamferRimAtAngle(solid, face, setbackAt, degrees)` / `ChamferEdges(solid, edges,
    setbackAt)` take a LAW `Func<Vector3d, double>` evaluated at each rim corner, linear
    along each edge. Still exact everywhere, on two facts: a linearly varying
    perpendicular inset of a straight edge is still a straight LINE (merely tilted), so
    sharp corners keep exact line–line miters and the corner segment from mitered top
    point to dropped bottom point is a boundary ruling of both adjacent strips; and a
    constant top:side ratio (a constant angle) keeps each strip's four corner points
    coplanar (`s₀·d₁ = d₀·s₁`), so every strip stays an exact PLANE — the strip's frame
    just follows the tilted top boundary instead of the edge. Enforced restrictions,
    both because a circle offset by a varying amount is a spiral with no exact B-Rep
    form: an arc rim edge needs the law constant along the arc (its band remains the
    exact cone), and a full circular rim needs it constant everywhere. Sharp corners
    adjacent to an arc are refused under a law (the miter of a tilted line against a
    concentric arc is not the arc's endpoint). A neighbour whose surface is
    domain-driven is re-trimmed to the HIGHEST remaining rim corner, since a tilted rim
    dips below a constant trim. Variable-RADIUS fillets stay refused — the corner, not
    the band, is the blocker (see the future-work note below).
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
    A sharp corner at an ARC is refused as a documented POLICY (design.md §5): that blend
    pairs a torus with a cylinder, which is not a conic, and tracing an approximate corner
    would bake a non-refining sampling floor into a primary feature — so the refusal
    locates the corner and names the exact escapes (make the rim tangent-continuous,
    chamfer instead, or take the implicit route explicitly).
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
    construction (a box gives 6 + 12 + 8 = 26 faces, 48 edges, 24 vertices). Refused
    loudly: concave edges (an opening cannot round them) and vertices of valence ≠ 3.
    Corners where one incident face is perpendicular to the other two (every box, convex
    prism and sheared box) keep the exact LUNE — the spherical triangle reduces to the
    region between two meridians of that face's normal closed by an equatorial great
    circle, a FULL-DOMAIN surface of revolution on the natural grid. **General trihedral
    corners** (a tetrahedron's, a drafted block's) build a TRIMMED spherical-triangle
    patch on the same sphere: pick one face normal as the pole axis — the two arcs that
    end at its tangency are then exact MERIDIANS (a great circle through the pole lies in
    a plane containing the axis), landing on the u = 0 / u = sweep domain boundaries, the
    pole closes the top, and only the third (diagonal) arc genuinely trims the interior.
    The generator's interior-side extent is not a weld boundary, so it takes the
    diagonal's sampled elevation minimum with a fixed margin; the three weld boundaries
    are exact by construction. Tessellation goes through a dedicated trimmed tier
    (`TriangulatePoleGrid` in Interop): one column per diagonal boundary sample, interior
    rows invented at the natural density and shared by index between adjacent column
    zips, meridian columns verbatim from the shared edge polylines, one one-step fan
    ring at the pole — and the whole grid EXCLUDED from midpoint refinement, because the
    refiner's flat uv metric overstates u chords near a pole without bound (measured: it
    cascaded midpoints into the apex fan at 16/8 and half-step slivers into the last rows
    at 48/24, both rejected by the corpus floor; the tier's own cells are already at
    natural density and converge quadratically — Steiner on a regular tetrahedron to
    1e-4 with a 3.0–5.0 halving ratio). Volume converges onto Steiner's formula
    `V = V₀ + A₀·r + (r²/2)·Σ ℓₑθₑ + (4π/3)r³` (dimensions of the ERODED body — the
    last term because a convex polytope's exterior solid angles sum to one ball).
    <br/>All the corner arcs are `CurveSegment` over `Circle3d`, never rational NURBS arcs:
    the patch is a surface of revolution sampled at even ANGLES, so an arc parameterized
    any other way samples to different points and the patch stops welding to its band.
    <br/>These solids round-trip through STEP: the corner patches' meridian boundaries are
    circles THROUGH the axis (no rim rule applies), and the reader recovers the closed
    circular generator's trim from them in CLOSED FORM — each meridian is a rotated copy of
    the generator, its azimuth read from the two circles' plane normals (the ±normal branch
    resolved by requiring the azimuth to sit within the swept angle and the rotated centre
    to land on the generator's), its points rotated back into the generator's plane and
    their angles read in the generator circle's own frame; the edge bands' extruded
    surfaces recover the same way from their congruent TRANSLATED end arcs. Never a
    distance minimization (which stalls at √ε, past weld tolerance). Closed NON-circular
    generators still keep the honest non-manifold diagnostic. Known gap:
    `BrepBoolean` can cut a whole-solid fillet through its PLANES (a centre drill is
    exact — locked with a Steiner-minus-bore volume test) but not through an edge BAND.
    The earlier "re-surfaced sub-band loses the corner arcs" diagnosis was WRONG; the
    measured causes: the tool∩band tracer loop leaves the band's quarter domain and
    closes on the extended carrier; the on-band runs' pseudo-samples now reach the
    domain edge (extrapolation extends by however many local gaps it takes, capped —
    one gap was measured falling short, leaving zero seeds), so crossing SEEDS exist —
    but `RefineCrossing` demands a true 3D intersection to 1e-11 and a CHORDAL tracer
    polyline sits a sagitta (~1e-4) off the exact tangency edge mid-chord: the curves
    are skew, every seed is rejected, the band never splits, and the whole band
    mis-classifies while its planar neighbours split — the result cracks along entire
    tangency edges and the boolean refuses loudly (with the unpaired edges now itemized
    in the message). The fix needs exact edge-vs-tool-surface crossings (the tracer
    curve must carry its surface pair) plus crossing-snapped polyline segment ends on
    BOTH solids, so the seam still welds by construction. Mitered RIM fillets cut
    correctly.
  - **Selection** — by face (`FilletRim`/`ChamferRim`) or by EDGE (`FilletEdges`/
    `ChamferEdges`; `RimFacesFor` remains the complete-rims-only resolution). A complete
    planar face rim resolves to rim surgery; a **partial run** — a contiguous selection
    stopping part-way along a rim — now blends with **SETBACK terminations**: the band
    stops at the run's end vertex with a planar face perpendicular to the terminal edge,
    exact because the band's cross-section there is already planar (the fillet's quarter
    arc / the chamfer's segment — the industry-default stop; cliff and vertex-blend
    terminations stay refused, each being a different surface). The top face keeps the
    terminal vertex and gains one in-plane link segment to the inset boundary, the
    terminal side face gains the matching descent segment, and the termination face
    closes the triangle against the band's end cross-section — its outward normal points
    INTO the removed wedge (the intact material is beyond the run vertex), which is what
    fixes every link edge's two senses. Interior run corners miter/blend exactly as full
    rims; interior-neighbour bands re-trim, terminal neighbours keep their full height.
    The whole selection is grouped and validated BEFORE any surgery: runs must be
    contiguous, start and end on STRAIGHT edges (an arc terminal's periodic cylindrical
    neighbour cannot be re-trimmed yet), and stay clear of other selected rims and runs —
    three blended edges meeting at one vertex is the spherical-corner shape that belongs
    to `FilletAllEdges`, and is refused naming it. Removed volumes are closed form (the
    prism terminates flush at the end planes): one chamfered edge removes exactly
    `c²/2·L`, an L-run `c²/2·(L₁+L₂) − c³/3`.
    **Variable laws work on runs too** (`FilletEdges`/`ChamferEdges` law overloads now
    resolve rims AND runs; `OpenRun` takes the law with the full-rim machinery — per-corner
    values anchored at the run's corners INCLUDING its end vertices, `VariableTopCorner`
    miters, ruled-skin bands between the corner sections with `LoftRailCurve` rails,
    highest-corner neighbour trims). A termination is exact at ANY law value — the band's
    end cross-section is a planar quarter arc of whatever radius the law gives at the stop
    vertex — so a run under a linear law removes exactly
    `(1 − π/4)·L·(r₀² + r₀r₁ + r₁²)/3` (asserted through tessellation, converging at
    ratio exactly 4.0). The full-rim refusals carry over with one run-specific way out
    named in the message: a sharp corner between different radii may simply STOP the run
    before the corner, since the termination takes any value where the miter cannot.
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
  complex-instance form, wrapper-curve simplification — including EXACT
  `TransformedCurve(NurbsCurve)` export by transforming control points with weights
  and knots untouched, sound for any affine map because a rational curve is an affine
  combination of its control points at every parameter, which is what lets
  NURBS-profile extrusions round-trip at reconstruction accuracy instead of sampling
  their translated top edges to degree-1 polylines; swept surfaces not exportable).
  The full analytic conic family maps both ways: `Parabola3d` ↔ PARABOLA and
  `Hyperbola3d` ↔ HYPERBOLA (the ISO 10303-42 parameterizations are ours verbatim, so
  edge trims reconstruct in CLOSED FORM — one dot product / asinh per vertex — and the
  import replaces the construction-time placeholder domain with the exact
  vertex-derived interval), and `OffsetCurve3d` ↔ OFFSET_CURVE_3D (displacement
  d·(ref_direction × T̂) = our d·(n̂ × T̂), so distance and direction carry over
  unchanged; trims by Newton on the exact offset derivative)
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
  profile (root solves, never distance minimization, which stalls near √ε). A partial
  revolve of a SINGLE closed profile curve (an elbow with a one-curve tube section) has
  no junction rails at all, so nothing on its boundary carries the angle — it used to
  come back SILENTLY as a full turn with zero diagnostics, refused by the tessellator's
  full-domain gate three stages later; `TryAngleFromRotatedCopy` now reads the angle in
  closed form as the azimuthal rotation between corresponding samples of the generator
  and its rotated boundary copy (congruence checked in (radius, axial) profile
  coordinates, reversed correspondence tried), and the closed-generator diagnostic is
  exempted there because the face genuinely covers the whole generator. The
  rim-circle off-axis rejection floor scales with the coordinate magnitude (foreign
  files carry rounding noise proportional to their coordinates — an absolute 1e-6
  floor silently rejected slightly-off-axis rims on large geometry, leaving generators
  untrimmed), and near-miss rejections emit a diagnostic instead of failing silently.
  **Units are resolved and scaled on import**: the LENGTH_UNIT a
  `GLOBAL_UNIT_ASSIGNED_CONTEXT` references — never a bare scan of all length-unit
  entities, because an imperial file's `CONVERSION_BASED_UNIT('INCH', …)` chain
  necessarily *contains* a metric base-unit entity that is not the file's unit — is
  resolved to a millimetre factor (SI prefixes closed-form; conversion chains
  multiplied down, so inches scale by 25.4) and applied to every length read
  (coordinates, vector magnitudes, radii); the factor is reported as a diagnostic, a
  unit that cannot be resolved falls back to unscaled-with-a-diagnostic (millimetres
  assumed), and scale 1 leaves every coordinate bit-identical (exact-== semantic
  guard). **Foreign quadric surfaces synthesize onto the existing revolve machinery**:
  `CONICAL_SURFACE` becomes a `RevolvedSurface` of a slanted line generator (the
  `MakeCone` representation) built over the face's own boundary-vertex axial range —
  an apex-degenerate range extends to the cone's natural apex, snapped EXACTLY onto
  the axis so the pole machinery sees a pole and not a hair-thin rim — and
  `TOROIDAL_SURFACE` becomes a revolve of the minor circle in the frame's x–z plane,
  both trimmed by the ordinary rim/rail recovery. That recovery gained the missing
  disambiguation for a CLOSED generator bounded by two rims (a split torus's two
  bands see the SAME two junction circles, and min/max would hand both faces the same
  half): the rim coedge senses say which rim is the generator's start — the same
  outward-band convention the single-rim pole case reads — with the rim circle's own
  axis alignment folded in.

- **Native B-Rep archive** (`BrepArchive.Write/Read/WriteFile/ReadFile` →
  `BrepArchiveResult`; extension `.ecb`, wired into `--export`) — the **lossless**
  round-trip STEP cannot give. Every curve and surface type in this project round-trips
  as ITSELF, including the ones with no AP214 entity at all: `HelicalSurface` (every
  modelled thread), `LoftedSurface`, `SweptSurface`, `OffsetCurve3d`, `SpiralArc3d`,
  `PhaseShiftedCurve`, plus trimmed edge domains and `CurveSegment` mappings.
  **Text, and that is a decision about testing rather than about geometry** (design.md
  §5): a numbered entity table with `#n` references, one entity per line, `;` comments,
  dependencies always defined before use — so a committed corpus archive can be *diffed*
  the way the golden fingerprints and byte-compared docs PNGs already are, where a binary
  would need a decoder written before anyone would look at it. Exactness is not given up
  for it: `"R"` formatting is a bijection on finite doubles, and the tests assert the
  strong form — **save → load → save is byte-for-byte a fixed point** over a 14-solid
  corpus (box through threaded rod, whole-solid fillet and boolean output). **The entity
  table keys on REFERENCE identity, never on structural equality**, which is load-bearing
  and not an optimization: `BrepEdge.IsClosedEdge` *is*
  `ReferenceEquals(StartVertex, EndVertex)`, so two coincident vertices and one shared
  vertex are different solids, and deduplicating by position would silently change
  topology. The same rule gives sharing for free — an edge used by two faces is written
  once, a seam curve shared by two edges comes back as one object. Frames are rebuilt
  with `Frame3d.FromOrthonormal`, the only factory that stores its axes verbatim (the
  re-deriving ones would move them by ulps and the fixed point would break — the
  `AxisRef` lesson). An **unknown version is refused by name**, as are foreign units and
  dangling or forward references. Scope is deliberately GEOMETRY, not document: solids,
  not scenes, poses, features or materials.
- **IGES 5.3 import** (`IgesReader.Read/ReadFile` → `IgesReadResult`; the record layer is
  the internal `IgesParser`) — **import only, deliberately**: IGES is a legacy format
  whose remaining use is receiving files from old CAM and surfacing systems, and writing
  it would mean maintaining a second, lossier encoding of geometry STEP already carries
  better. Entities are the ones that map ONTO EXISTING kernel geometry rather than
  needing new surface types: 110 line, 100 circular arc, 104 conic arc, 126/128 rational
  B-spline curve and surface, 102 composite curve, 116 point, 108 plane, 118 ruled (a
  two-section `LoftedSurface`), 120 surface of revolution, 122 tabulated cylinder (an
  `ExtrudedSurface`), 124 transformation matrix, 142/144 trimmed parametric surface.
  **The result is a FACE SOUP and says so** (`IsFaceSoup`): IGES carries no shared
  topology, so every trimmed surface owns its boundary curves, two neighbouring faces
  reference two coincident-but-distinct curves, and the assembled shell's edges are used
  once rather than twice — `Validate()` refuses it until `ShapeHealing.Heal` has sewn it,
  which is exactly the case healing was built for. Four rules carried over from
  `StepReader`: units resolved from the Global section and scaled to millimetres (with
  the exact-`==` guard that a millimetre file is bit-identical, not "scaled by 1");
  unknown entity types skipped ONCE with a diagnostic naming the type and its first
  offender; a bad entity costing only itself; and everything reported as DATA
  (`Diagnostics`) rather than as log lines. Two things specific to a column-oriented
  format: **record structure is validated up front and refused by name** — column 73 is
  the section letter and 74-80 the sequence number on every card, so a file without that
  shape is rejected before a parameter is read (`StlReader`'s size-test-first rule) — and
  **Hollerith counts are honoured when splitting fields**, since an author field
  containing the parameter delimiter would otherwise shift every later Global parameter
  by one and silently change the file's units. The conic arc (104) is classified from its
  own coefficients rather than from its form number, which is routinely wrong; a
  contradiction is reported and the coefficients believed. The 142 boundary takes the
  MODEL-space curve, not the parameter-space one — this topology has no pcurve slot
  (trimming is 3D loops pulled back on demand), so the choice is stated rather than
  silently made.
- **Shape healing** (`ShapeHealing.Heal/Analyze` — OCCT `ShapeFix`): repairs imported
  face-soup topology; every repair is a return value (`ShapeHealingReport`), never a
  log line, and the input is never modified. Passes in order, each switchable: vertex
  merging (union-find, keeping an existing coordinate), small-edge collapse,
  degenerate-face removal (dimensionless area/perimeter² sliver rule), edge sewing,
  **curved-edge re-trimming** (opt-in, `RetrimCurvedEdges` — OCCT
  `ShapeFix_Wire::FixGaps`' parametric mode: each curved edge's domain moves to the
  foot-of-perpendicular parameters of its unified vertices via a LOCAL Gauss–Newton —
  the gap is sub-tolerance so no global seeding or period unwrapping is needed, and the
  break-before-negligible-step rule makes a converged end come back bit-identical so
  healing stays idempotent; what remains is the vertex's PERPENDICULAR distance to the
  curve, which only curve re-fitting could remove — a modelling operation, reported
  and never attempted; the per-edge tolerance story is exactly that report, since this
  topology deliberately carries no per-entity tolerances for downstream code to have
  to honour), straight-edge refitting (opt-in — the one pass that moves geometry),
  wire re-ordering/re-sensing, and **shell repair** (`RepairShells`, on by default —
  OCCT `ShapeFix_Shell`, the B-Rep counterpart of `MeshRepair`'s winding flood): a
  consistency flood over shared-edge senses (the manifold invariant is two uses with
  OPPOSITE sense, so agreement across an edge means one face is flipped; flipping
  re-winds every loop AND toggles `IsReversed`, so "loops CCW about the outward
  normal" keeps holding; contradictions mean non-orientable and are reported, not
  guessed), a **global outward vote** per CLOSED component from the fan volume of the
  sampled boundary loops against containment parity (a nested component is a void and
  must point INTO its cavity — sealed `Shelling.Shell` output is left exactly alone;
  pole-bounded/closed-band faces like spheres enclose ~no loop volume, so their vote
  is honestly ambiguous and the authored side is kept), and shell **repartition** so
  each connected component is one shell (splitting a foreign writer's flat face list,
  merging shells a component spans) — skipped bit-stably when the partition already
  matches.

  Writer-side placements go through `Frame3d`: a `Placement(Frame3d)` overload
  mirrors the reader's `Axis2` (`Frame3d.FromZX`) — STEP stores origin + Z + X and both
  sides derive Y right-handed, so frames round-trip by construction — and the matrix
  path refuses **mirrored (improper) instance transforms by name** (they pass every
  orthonormality test, but a left-handed triple would silently re-pose the part
  un-mirrored on read-back) while still emitting the matrix's own columns so the
  written text is ULP-stable. `Read`/`ReadFile` take an **optional trailing `ILogger`**
  (`Microsoft.Extensions.Logging.Abstractions` — abstractions only, this project's one
  package dependency; `KernelLog.cs`, stable event ID **90**) that reports counts and
  timing; the import's real findings stay in `StepReadResult.Diagnostics`, data the
  caller acts on — logging complements them, never replaces them.

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
**cliff and vertex-blend run terminations** (the setback termination is implemented;
each of the others is a different surface), **arc-terminal partial runs** (the
termination itself is exact, but the periodic cylindrical neighbour needs re-trimming),
**sharp corners at arc rim edges** (a documented policy — torus ∩ cylinder is not a
conic; see the Filleting section), and
**a varying radius across a SHARP corner** — variable-radius fillets themselves landed
(`FilletRim`/`FilletEdges` law overloads + `Shape.Fillet(radiusAt, faces)` +
`VariableFilletRimFeature`): along a straight run the band is the RULED skin between its
two end quarter arcs, and because those are equal-weight rational conics on one frame,
lerping their points equals lerping their homogeneous control points, so every
intermediate section is a TRUE circle of the interpolated radius, tangent to both
neighbours. What is refused is the corner: two variable-radius bands are cones that do
not circumscribe a common sphere, so they meet in a quartic with no conic to miter them
on. A CONSTANT law across a sharp corner makes both bands equal-radius cylinders again
and the exact bicylinder ellipse is back, so the refusal is about the law and not about
sharp corners; arcs take the law only where it is constant along the arc (a circle
offset by a varying amount is a spiral), exactly as variable-SETBACK chamfers do. The
bands' top and bottom boundaries are RAILS on the band (`LoftRailCurve`) rather than
free-standing lines, which is what makes the band's grid and the shared edge polylines
sample the same points.
Loft gaps: sections must already be segment-compatible (no degree
elevation / knot merging), holes in sections, open (uncapped) skins, periodic lofts that
close back on the first section, guide curves / spine, and the "pipe shell with evolution
law" generalization (a section scaled and twisted along a spine — which is a loft whose
sections are generated rather than given, so it lands on `LoftedSurface` once a law
evaluator exists). `LoftedSurface` is not STEP-exportable (same bucket as swept surfaces).
Draft gaps: curved faces on an axis other than the pull direction (a face of revolution
ABOUT the pull direction now drafts exactly), caps with holes, and drafting about a
non-planar neutral surface.
Shelling gaps (curved faces now shell exactly — see `SurfaceOffset`/`SurfaceCorner` above):
carriers with no same-family offset (swept and NURBS surfaces), non-circular curved edges,
higher-valence vertices whose offset planes are NOT concurrent (a concurrent one — a
pyramid apex — now works), adjacent openings, and global self-intersection detection.
`HelicalSurface` faces cannot be exported to STEP (same bucket
as swept surfaces); helical faces trimmed into anything other than a rail/spiral band
(e.g. a helical band cut by a NON-perpendicular plane or another curved surface) have
no tessellation path, and helical∩cylinder / helical∩helical intersections fall to
the marching tracer.
