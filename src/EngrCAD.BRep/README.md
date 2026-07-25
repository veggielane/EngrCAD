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
  `NurbsCurve.InterpolatePoints(points, closed)` builds a cubic B-spline passing exactly
  through the points (`GeomAPI_PointsToBSpline`-style): chord-length parameterization;
  open curves use clamped knots + natural end conditions via a tridiagonal collocation
  solve (two points degrade to a degree-1 chord); closed curves use periodic knots with
  wrapped control points, so the seam is C2 by construction (cyclic system solved densely).
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
  **Inverse evaluation on swept surfaces is a ONE-dimensional solve.** The base
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
- **Topology**: `BrepSolid → BrepShell → BrepFace → BrepLoop → BrepCoedge → BrepEdge →
  BrepVertex`. Faces are built so surface normals point outward and loops run CCW around
  them (first loop outer, rest holes). `Validate()` checks loop chaining and two-manifold
  edge use; `SatisfiesEulerFormula(genus)` checks V − E + F − (L − F) − 2(S − G) = 0.
- **`Profile`** — planar closed chain of curve segments (or one closed curve) used by the
  modeling operations; winding is auto-corrected per operation.
  `Profile.FromRegion(region, frame?)` places a Core `Geometry2.Region2d`
  (polygon-with-holes, the output of the 2D sketch engine's booleans) on a `Frame3d` and
  returns the `(outer, holes)` pair `Extrude`/`Revolve`/`Sweep` take — the 2D front door
  into the solid factories; `FromLoop(points, frame)` does one loop. Regions are polygonal,
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
Helical bands tessellate as sheared grids whose columns are iso-axial rungs — the
first/last columns ARE the cap cuts — with every boundary point taken verbatim from
the shared edge polylines (band↔band and band↔cap welds exact by construction); helix
rails and spiral cuts sample proportionally to their turning angle. Boolean fragments
of helical bands cut by cap-plane spirals are still band-shaped (two rail pieces +
two spiral cuts) and take the same path.

## Not yet implemented

Coplanar/tangent boolean cases, general fillet chains with corner patches,
NURBS surface export. Loft gaps: sections must already be segment-compatible (no degree
elevation / knot merging), holes in sections, open (uncapped) skins, periodic lofts that
close back on the first section, guide curves / spine, and the "pipe shell with evolution
law" generalization (a section scaled and twisted along a spine — which is a loft whose
sections are generated rather than given, so it lands on `LoftedSurface` once a law
evaluator exists). `LoftedSurface` is not STEP-exportable (same bucket as swept surfaces).
Draft gaps: curved faces (the general face-offset-and-reintersect), caps with holes,
per-face angles in one call, and drafting about a non-planar neutral surface.
`HelicalSurface` faces cannot be exported to STEP (same bucket
as swept surfaces); helical faces trimmed into anything other than a rail/spiral band
(e.g. a helical band cut by a NON-perpendicular plane or another curved surface) have
no tessellation path, and helical∩cylinder / helical∩helical intersections fall to
the marching tracer.
