# EngrCAD — TODO / idea backlog

Open work only — completed items are removed as they land (the record lives in git
history and CLAUDE.md's status). Many items come from a survey of **geometry3Sharp**
(`C:\Users\chris\projects\git\geometry3Sharp`, Ryan Schmidt / gradientspace —
triangle-mesh + implicit library; no half-edge, no BSP, no B-Rep, so it complements
rather than duplicates our engines) and name the g3 classes worth studying before
implementing. Ordered roughly by value-for-effort within each section.

## Core (EngrCAD.Core)

- [ ] **Infill footprint and coverage residuals** (the neck measure, the tiled rectangular
  footprint and the run linker ✅ landed — `Region2dThickness` + `SpaceFillingCurve.OverTiled`
  + `TiledHilbertLattice` in Core, `RunLinker` in Modeling; see design.md §2 and §6b). What is
  left is all about the FOOTPRINT measurement rather than the fill:
  - [ ] **`InfillPath.Footprint` is slow and the filed remedy is measured MOOT** (2026-08-16,
    i9-9900K: 183 / 251 / 1602 ms for 32 / 160 / 640-point fills of a 60×40 plate). The
    proposal was "stroke per RUN, then merge the runs by bounds" — but `Footprint` ALREADY
    strokes per run (it has since `ExactFootprint` landed), and splitting the cost shows the
    cross-run `UnionAll` is only **22%** (388 of 1602 ms at 640 points) while the per-run
    STROKES are 78% — and `Region2dOffset.Stroke` already unions its slabs through
    `Region2dBoolean.UnionAll`'s balanced fold, so there is no cheap association win left
    either (scaling measured ~E^1.25 between 149 and 629 segments, not the filed E²). What
    remains is research-grade: a fundamentally different footprint algorithm (one sweep over
    ALL slabs at once, or an arrangement of the path's offset curves) rather than a better
    fold — and the bit-pin constraint stands, since `Region2dGoldenTests` and
    `infill-hilbert.png` pin the incumbent output bits.

## B-Rep / sketching (EngrCAD.BRep)

## Deformation / analysis follow-ups

The foundation ✅ landed (`EngrCAD.Core.Solvers`: `PackedSparseMatrix` /
`SparseSymmetricCG` / `SparseCholesky`; mesh engine: `LaplacianMeshSmoother`,
`LaplacianMeshDeformer`, `MeshLocalParam`, `MeshIsoCurves`, `DijkstraGraphDistance`,
`MeshIcp`). Since then the Shape API grew **`Shape.Smoothed(step, passes)`** (Laplacian
fairing as a graph node — mesh-Native, implicit-Bridged via `MeshSdf`, B-Rep-Impossible,
the `Remeshed` precedent; docs `remeshing.md` §Fairing) and the exp map grew its decal
consumer **`SurfaceDecoration.Wrap`** (a flat polyline / space-filling curve / `Sketch`
outline laid onto a doubly-curved surface, with the distortion MEASURED on the laid curve;
design.md §2). Residuals:

- [ ] **A supernodal/left-looking numeric factorization** is the next lever, not a
  better ordering. AMD takes 3D 40³ (64k unknowns) from 125 s to 26 s, which is a real
  4.8× and still unusable — the fill is 20.6M entries and the up-looking scalar loop
  touches them one at a time. BLAS-3 dense blocks over the supernodes are the standard
  answer and the only thing that closes that gap. (Core.Solvers work — product-sized, and
  the AMD-vs-natural default is settled: FEA assembly consumes AMD, the mesh deformers
  stay natural to keep their bit-pinned outputs.)
- [ ] **Surface ENGRAVING — cutting a groove INTO the solid, not laying a curve ON it.**
  `SurfaceDecoration.Wrap` lands a `Sketch`/glyph outline on the surface (the exp-map
  wrapping the pipeline was built to enable); turning that laid `SurfaceCurve` into a
  removed groove (or a raised emboss) is a separate, larger operation — it needs the curve
  stroked to a bead and offset/booleaned against the body, or an SDF engraving field, and
  the distortion the wrapping reports feeds directly into the bead width. File a consumer
  before building: today the honest answer is that the wrapping produces the runs and the
  caller strokes/booleans them.

## Mechanisms (kinematics)

- [ ] **Gear follow-ups** (involute spur/helical landed as `Gears.Spur/SpurGear/
  HelicalGear` — the fit tier was adequate: 16 arcs/flank at module·1e-4, so no new 2D
  curve type; conjugate action is measured from CONTACT via the sketch's exact signed
  distance, because `Coupling.Gear` in the mechanism solver ENFORCES the ratio it
  would be asserting. Since then the **rack** (`Gears.Rack`/`RackBar` — the
  straight-line limit, hence exact, hence no fit deviation to report) and the
  **worm and crossed-helical wheel** (`Gears.Worm`/`WormPair`/`WormWheel` — the worm
  is a thread and rides `MakeThreadedRod`) have landed too):
  - **Trochoid root for low tooth counts** — `Gears.Spur` refuses below
    z_min = 2(h_a* − x)/sin²α by name; drawing the actual generated trochoid would
    admit z ≥ ~12 if it can be VERIFIED (the conjugate-contact instrument exists and
    measurably sees a 5e-2 flank error as 5.6e-4 rad of transmission wobble).
  - **Set screw boss** (the keyway ✅ and web lightening ✅ halves landed —
    `StandardKeys`/`KeywaySpec`/`Gears.KeyedBore` and `LighteningSpec`, both exact
    sketch features with closed-form areas). A boss needs a 3D HUB — a cylinder proud
    of the web, i.e. a revolved blank cross-section rather than one extrude — so it is
    a gear-blank redesign, not a hole.
  - **The apex relief groove on a herringbone** (`HerringboneGears`) — the one part
    of the double-helical form that did NOT land, and the entry carries its
    measurement so it cannot rot into a guess. A hobbed herringbone cannot have a
    continuous apex, so real ones carry a relief groove; a groove is material
    genuinely REMOVED, so it wants a boolean rather than the mid-plane weld — and
    subtracting an axial band from a gear fails in BOTH engines: the exact mesh
    boolean's imprint ("flip recovery of the intersection segment … did not
    converge") at every relief diameter, gap width and mesh density tried, and the
    B-Rep boolean as an unclosed solid with 1522 unpaired edges for the SAME band
    against an ordinary SPUR gear, which is what shows this is gear geometry rather
    than the herringbone's weld (both pinned by
    `HerringboneGearTests.SubtractingAnAxialBandFromAHerringbone_StillFails`). Two
    ways forward and they are different sizes: fix the boolean (the mesh imprint's
    flip recovery is the nearer of the two), or build the groove as a MIXED-SECTION
    RING STACK — helical toothed run, an annular transition face (gear outline with
    the relief circle as a hole), a plain relief band, then the mirror — which needs
    a level/loop bookkeeping layer beside `TwistedExtrusion` and is a construction
    rather than a parameter.
  - **A lazy herringbone node** — `HerringboneGears.Herringbone` meshes EAGERLY at a
    stated quality and wraps the result in `Shape.From`, because a `Shape` node would
    need a `ShapeCompiler` case. Nothing is lost in the B-Rep direction (a twisted
    extrude is Impossible there anyway); what is lost is re-meshing at a scene's own
    quality, so a herringbone in a scene rendered finer keeps the quality it was
    built at.
  - **The full gear taxonomy** (requested 2026-08-02), each with its honest scope:
    - **Straight bevel residuals** (`BevelGears.cs` landed: `BevelPair` +
      `Straight`/`StraightGear`, spiral and hypoid refused by name; see the Modeling
      README for the projection measurements). What is left:
      - **`BevelPair.PhaseFor(member)`** — the pair's TOOTH phasing is not solved, so a
        caller placing two members must phase them by hand (the docs example asserts the
        condition its own counts satisfy: contact at the pinion's 90° azimuth needs
        z₁ % 4 == 0 and z₂ % 4 == 2). **`GearMeshing` is now the pattern rather than
        `PlanetarySet`** — the tooth-index coordinate `u(θ) = z(θ − φ − τ)/2π` and the
        rolling invariant it makes are stated once there, and the contact instrument to
        verify a phase with already exists (`GearMeshingTests`). It was deliberately NOT
        landed with the parallel-axis rules, because a bevel's mesh happens on the shared
        cone ELEMENT rather than on a line of centres, so the invariant has to be derived
        for spherical rolling and then pulled back through Tredgold's projection —
        genuinely a fourth derivation, not a fourth call of the third.
      - **Conical end faces** — the loft's sections must be planar, so the ends are
        planes rather than the back and front cones, which makes the heel section
        deeper than the real tooth (×2.4 at δ = 65°) and is what caps the cone angle
        near 68° with the ISO 53 fillet. Trimming with cones needs a loft∩revolve
        boolean whose curves the tracer must find at tooth scale — measure before
        promising, since that is the aspect ratio the thread work found it fails at.
      - **A full-radius root** where two fillets no longer fit the gap, which is what a
        real deep-tooth gear does and would lift the cone-angle cap without changing
        the flank.
    - **Worm and worm wheel** — the worm IS a thread: `MakeThreadedRod`'s helical
      sweep with a trapezoid (ZA) profile is the exact worm body, one axis-touching
      revolve family this kernel already speaks. The WHEEL is the honest problem: a
      true throated wheel is the envelope of the worm's motion (no closed form —
      that is gashing-and-hobbing kinematics), so v1 is a helical gear at the worm's
      lead angle (the crossed-helical approximation, stated, with its point-contact
      caveat named) and the throated envelope is filed as assessed-not-promised.
    - **Cycloidal residuals** (`CycloidalGears.cs`/`CycloidalDrives.cs` landed — the
      tooth form, the conjugate pair, the BS 978-2 table, the drive disc). What is
      left: (a) **lantern (pin) pinions**, the classic clock partner of a cycloidal
      wheel — geometrically the drive disc's pin ring one size down, so the two files
      already hold both halves and only the pairing arithmetic is missing (a lantern
      pinion's mating wheel face is generated by the pin circle itself, which is why
      the describing circle is not free there); (b) **internal cycloidal gears**;
      (c) **contact ratio** as a reported number — a cycloidal pair's arc of action is
      the two generating-circle arcs clipped by the addenda, closed form, and a short
      addendum can put it under 1, which today the factory does not say; (d) drive
      **output roller holes**, a **running clearance** (the profile offset a further
      stated amount, which the offset machinery already does), the **eccentric shaft**,
      and a phased **two-disc stack** at 180° for balance; (e) a cycloidal-drive
      `Coupling` so a `MotionStudy` can drive the disc — the pose relation is already
      a closed form on `CycloidalDiscSpec` (`DiscRotation`/`DiscCentre`), so this is
      wiring rather than kinematics. **It is also the one gear ANIMATION that would
      not alias**: the docs' planetary clip has to run 30° of carrier and not loop,
      because a seamless 120° loop puts a planet at 1.08 tooth pitches per frame at
      24 frames (see `docs/examples/gears.md`), whereas a cycloidal drive turns its
      disc only −36° — exactly one lobe, so ONE input turn is a seamless loop — over
      a whole input revolution, i.e. 0.04 lobes per frame with nothing to alias.
      Without the coupling there is no `MotionStudy` to hand `MechanismTrack`, and a
      hand-rolled `PoseTrack` in a docs snippet would be the wrong place to put
      kinematics; (f) `Curve2dChains.Fit` is the SAME recursive
      biarc fitter as `Gears.FitFlank` (exact points and tangents, split at the worst
      interior sample, deviation measured afterwards) — the involute file predates it
      and should delegate, one algorithm in one place.
    - **Planetary residuals** (`PlanetaryGears.cs` landed: `PlanetarySet` with the three
      conditions, the boolean-free internal ring, solved phases verified from contact,
      and Willis emergent over composed `Coupling.Gear`s). What is left:
      - **Internal-mesh interference** — tip, involute and trimming interference are
        genuinely different conditions from an external pair's and none is checked; the
        undercut refusal that fires today is `Gears.Spur`'s applied to the CUTTER, which
        is conservative for a ring and is not the same question.
      - **An internal root fillet** — the ring's tooth roots are sharp (its tips carry
        the cutter's root fillet instead), which is a stress raiser rather than a
        geometric fault.
      - **Unequally spaced planets**, which have their own weaker assembly condition,
        and **profile-shifted sets** (a shifted sun/planet pair changes the operating
        centre distance, so `z_ring = z_sun + 2·z_planet` stops being the coaxiality
        statement and the derived ring count would silently be wrong).
      - **A compound/Ravigneaux vocabulary** — stepped planets and shared carriers are
        more couplings over the same joints, so the mechanism side likely needs nothing
        new; it is the placement and the assembly conditions that generalize.
    - **A meshing PHASE for a crossed-helical pair** — `CrossedHelicalPair` places
      the two members at the right centre distance and shaft angle with their pitch
      cylinders tangent, but not at the angular phase that would put a tooth of one
      in the gap of the other. **The parallel-axis half of this is DONE**
      (`GearMeshing`: external, internal and rack, verified from contact); what is
      left is genuinely different, because on SKEW axes the members do not share a
      transverse plane, so the tooth-index coordinates are taken in each member's own
      plane and the relation between them carries the shaft angle. The rolling
      invariant is the thing to derive; the tooth-index machinery and the contact
      instrument then serve unchanged. Note the honest scope: a crossed pair touches
      at a POINT, so "in mesh" means the two helicoids are tangent there rather than
      a tooth filling a space, and the phase is only defined at the contact point.
    - **Non-circular/elliptical gears** — refuse by name for now: conjugacy for a
      stated centre-distance function is an integral condition, a different problem
      from fitting a known curve; file only if a consumer appears.

Mechanisms v1 landed (`Joints.cs`/`Mechanism.cs`/`Couplings.cs`/`HigherPairs.cs`/
`MateSolverRates.cs`/`MotionInterference.cs`; docs `examples/mechanisms.md`): joints as
a vocabulary over mates with DOF asserted against the solver's rank, drivers +
continuation sweeps, named dead centres, analytic velocities/accelerations,
gears/belts/cams, joint limits, interference over the sweep, swept volumes as Shape
nodes, and Grübler/Kutzbach as a cross-check. **Since then**: multiple simultaneous
drivers (`SolveAt`/`Sweep`/`RatesAt` take lists; the multi form IS the implementation
and the single-driver overload is sugar over it; a sweep is a straight line through
driver space so the continuation logic is unchanged; the same coordinate driven twice
is refused by name), `Coupling.RackAndPinion` (a cam pair with a straight law, so it
reads the unwrapped angle and a rack driven through three turns keeps advancing), the
dwell-rise-dwell `CamLaw` catalogue with `Segments`, adaptive `SweptVolume(path,
maxTravel)` (rigidly interpolated placements bounded by exact bounding-box-corner
travel; 97%+ of the analytic disk from a 9-frame full-turn sweep), and **involute gear
geometry** (`Gears.cs`: `GearSpec` + `Gears.Spur/SpurGear/HelicalGear`, biarc-fitted
flanks with the deviation reported, undercut/pointed/fillet refusals by name,
conjugate action verified from contact — see the gear follow-ups item above), plus
**herringbone and crossed helical** (`HerringboneGears.cs`/`CrossedHelicalGears.cs`
/`HelicalGearGeometry.cs`: the double-helical apex as a mirror WELD verified by the
bit-exact vertex-set identity and by helix angles read off real transverse sections;
the crossed pair's Σ = β₁ + β₂ over signed helix angles with the tooth traces checked
coincident at construction; the every-coefficient-scales-by-cos-β rule for a gear
ordered in normal terms; and the helical pair's conjugate test as the
transverse-section argument measured against a derived bound).
Remaining follow-ups:

- [ ] **The exact MESH boolean's tangent-bicylinder defect is the IMPRINT, and it is
  ALIGNMENT-dependent — the diagnosis overturned its own first guess twice** (pinned
  from BOTH sides: `TangentBicylinderBooleanTests` in Mesh.Tests holds the healthy
  alignment at the Steinmetz volume, `TangentBicylinderDefectTests` in Modeling.Tests
  pins the defective one at its measured value so a fix announces itself).
  `MeshBoolean.Intersection` of two equal-radius perpendicular crossed cylinders
  through one axis point (Ø4, degenerate Steinmetz, analytic 16r³/3 = 42.67) returns
  **10.56 — a quarter** — for the B-REP-ROUTE tessellation (`Shape.Cylinder(2,8)`,
  one rotated a quarter turn), while `MeshPrimitives.Cylinder` and its own exact
  quarter-turn copy measure **42.26** — correct within chord grade. The first guess
  ("whole lobes dropped by winding classification") is WRONG: inclusion–exclusion AND
  the A = (A∩B) ∪ (A−B) partition hold to round-off on BOTH fixtures, every result is
  closed — the classification is perfectly consistent, which is exactly what makes the
  defect invisible to every downstream identity a consumer could check. The mechanism
  is the IMPRINT's seam topology where the two surfaces GRAZE: whether the facet
  crossings near the two tangent points join the intersection ellipses correctly is
  decided by the tessellations' phase alignment (the recorded alignment-not-tolerance
  family), and the wrong joining yields a self-consistent boolean of a WRONG partition.
  A fix needs the imprint hardened at grazing contact (seam-chain topology near
  near-parallel facet crossings), verified over an alignment FAMILY sweep, never one
  phase; until then a consumer measuring volumes near a tangency should treat the mesh
  answer as suspect and the B-Rep boolean's refusal-by-name as the honest tier.

- [ ] **Flexible sub-assemblies in mechanisms** — inherited from the mates layer: a
  deep occurrence whose owning sub-assembly is placed more than once is refused (one
  shared frame). A mechanism inside a twice-placed sub-assembly needs per-placement
  frame overlays first. See the assessment under "Assemblies follow-ups".

## Simulation

- [ ] **Topology optimisation follow-ups** (SIMP landed 2026-08-04 — `TopologyOptimizer`,
  design.md §3k, docs `examples/fea-topology.md`; passive regions, several load cases and the
  largest-connected-component release filter landed 2026-08-09; penalty continuation and
  symbolic-factorization reuse landed 2026-08-09). Each of these is a NAMED absence in the
  shipped feature rather than a defect, and each was weighed and deferred:
  - **MMA (Method of Moving Asymptotes)**, the general answer OC is not. Needed the moment
    there are two constraints or a different objective; a substantial dependency-free build
    (a per-variable convex subproblem with asymptotes updated from the iteration history,
    plus a dual solve). The API already says OC by name so nothing has to be unsaid.
  - **Local stress constraints**, which need p-norm or Kreisselmeier–Steinhauser aggregation.
    Filed as a separate FEATURE rather than a flag because the aggregation parameter CHANGES
    THE ANSWER — it interpolates between a mean and a maximum — so it needs its own
    verification against a case whose peak stress is known. It also needs the SIMP stress
    question settled: the stiffness carries a penalised modulus, so a void element's stress
    is not physical and the standard answer (a separate stress interpolation, `rho^q` with
    `q < p`) is a second modelling decision.
  - **Design-dependent loads (self-weight).** Refused by name today. It needs the adjoint
    term the self-adjoint shortcut drops, plus a decision about the load interpolation (a
    linear `rho` mass with a `rho^p` stiffness makes low densities artificially efficient,
    which is the classic self-weight parasitic-mass failure).
  - **Cost — the remaining lever.** One factorization per iteration: measured 288 elements in
    0.43 s, 1 152 in 2.5 s and 10 800 in about 50 s at 60 iterations. Reusing the symbolic
    factorization across the loop landed (`SparseCholesky.AnalyzePattern` → `SparseCholeskySymbolic`,
    bit-identical to a fresh `Factorize`, a bounded per-factorization saving — 1.13× at 1 152
    elements, 1.02× at 10 800, since the numeric pass is the floor). The remaining lever is a
    preconditioned CG warm-started from the previous iterate's displacement (the design changes
    little per step, so the seed is good) — a different mechanism that attacks the numeric cost,
    not the symbolic one.

FEA as a first-class citizen of the hybrid kernel: the CAD model (any representation)
feeds the mesher, results feed back into the viewer as fields on the mesh. The mesh
engine's half-edge structure and the implicit engine's SDFs are both real assets here
(SDF-guided sizing fields, inside/outside tests via winding numbers).

**Tet meshing landed** (`EngrCAD.Fea`: `TetMesher`, `TetMesh`, `TetQuality`,
`QuadraticTetMesh`, on Core's new exact `Predicates3d`) — conforming Delaunay with
verified boundary recovery, radius-edge + sizing-field refinement, region ids from
multi-body input, per-facet source-triangle tags, 10-node elements. Residuals below.

- [ ] **Conforming Delaunay for a CURVED non-Delaunay surface triangulation** (what is left of
  the old "boundary recovery on remeshed surfaces" top gap, whose filed diagnosis was measured
  and found wrong in two directions — see design.md §3b and the Fea README table).
  **What was wrong**: a remeshed surface is not the obstacle (a remeshed sphere meshes in
  **zero** recovery rounds at three target edge lengths once the remesh is Delaunay-clean,
  *with one patch per triangle* — the configuration the old entry blamed), and triangle quality
  is not the criterion either (a remeshed box at a **0.145°** worst angle and radius-edge
  **198** meshes; a remeshed sphere at **27.9°** and **1.07** is refused; the structured
  cylinder that recovers in zero rounds has *worse* triangles than the remeshed sphere that
  does not). **What is actually left**: where a surface is flat a patch absorbs any diagonal and
  nothing has to be recovered, but where it is curved every triangle is its own patch and must
  appear verbatim as a Delaunay face, and refinement cannot manufacture that. The fix is the
  textbook one this v1 deliberately skipped — protecting-ball *segment and subfacet
  encroachment* (Shewchuk's CDT construction, or Murphy–Mount–Gable) — which carries a
  termination proof where the budget carries none. Red subdivision is not a weak version of it;
  it is a different thing that provably cannot reach it, which is why the practical answer
  today is `RemeshOptions.PreventLongEdgeFlips` (measured to turn every refused sphere row into
  a zero-round one) and the refusal now says so.
  Already landed off this item: non-convergence detection (five rounds without improving on the
  best offending count, the trimmed-face monotone-decrease rule) and a refusal that measures
  the input's worst triangle and its curved fraction instead of advising a remesh.
- [ ] **The remesher makes a cylinder primitive worse, and nothing catches it.** Measured
  across six settings, `Remesher.Remesh` of `MeshPrimitives.Cylinder(10, 20, 48)` lands at a
  worst angle between **0.013° and 7.7°** with a radius-edge ratio between **3.7 and 2124** —
  `PreventLongEdgeFlips` included, so it is not the flip stage. The seed is that the primitive's
  n-gon caps triangulate as a **one-corner fan** (worst angle 3.74° before any remeshing) and
  the rim is pinned, so the remesher has little freedom on the cap and degrades it. Two
  separable pieces, and the MEASURE half has since landed — `RemeshResult.Quality` is a
  `TriangleQualityReport` rated against the remesher's own pinned population, so the
  degradation is visible to any caller that looks. What remains is the SEED: a better
  cap triangulation for `MeshPrimitives.Cylinder` (interior Steiner points — a bare
  centre fan only trades the ear's 3.7° for the apex's 360/n), which is a change to the
  most widely used primitive in the repository (committed goldens, docs PNGs and tet
  fixtures all sit on the fan), so it wants its own verification pass, not a rider.
  `EngrCAD.Mesh`, not Fea; it surfaced because the tet mesher is the first consumer
  that cannot tolerate it.
- [x] ~~**Sliver removal (the second named gap in tet meshing).**~~ ✅ **done** —
  `TetSmoothing.Smooth` is the optimization-based half (interior-vertex smoothing against a
  worst-incident-dihedral objective, boundary and deliberate anisotropy frozen, exact
  orientation predicate on every candidate). Measured on a 20³ box: **every sliver removed** at
  three sizes (190 → 0, 399 → 0, 1 149 → 0), worst dihedral 0.00° → 10–17°, volume drift
  7.8e-15 … 2.1e-14 — with the residual **coordinate-sensitive** (the same box translated to
  the origin leaves 2 of 190), so the guarantee is determinism rather than sliver-freeness.
  Residuals filed below.
- [ ] **Sliver exudation, and the topological half of `Stellar`.** `TetSmoothing` moves points
  only, which is what lets it keep the boundary, the volume identity, the connectivity and the
  orientation invariant by construction. The stronger techniques change TOPOLOGY — Cheng et
  al.'s weighted-Delaunay perturbation, and Klingner–Shewchuk's edge/face removal and 2-3/3-2
  flips — and would need the boundary contract, the classification and the region ids
  re-established afterwards rather than inherited. Worth it if a fixture ever appears that
  smoothing cannot clear; nothing in the suite currently is (every fixture reaches zero
  slivers), which is the honest reason not to build it yet.
- [ ] **`TetSmoothing` costs ~10x the meshing it follows** — 4.9 s against 504 ms on the
  40 593-element box, 12.9 s at 103 103. The profile is not measured yet, but the shape is
  obvious: 10 search directions × 8 stride halvings × every incident element's six dihedrals,
  per vertex per pass. Cheap levers in likely order: stop a vertex's search as soon as a pass
  finds no improving direction at the FIRST stride (most vertices after pass 1), cache each
  element's dihedrals and invalidate only the incident ones, and hoist the `TargetDihedralDegrees`
  gate to skip whole passes once the mesh is clean. A parallel pass is NOT free here — vertices
  share elements, so a block decomposition would change the visit order and with it the answer,
  and bit-identical output is asserted.
- [ ] **`TetSmoothing` lowers the MEAN minimum dihedral by 2–4° while raising the worst.** The
  objective is the worst incident angle, so lifting it moves a vertex away from what its other
  elements would have preferred (measured 42.4° → 39.5°, 44.4° → 41.2°, 43.6° → 39.9°). That is
  the right trade for conditioning and it is reported rather than buried, but a combined
  objective — maximize the worst, break ties on the mean — would plausibly keep both. Needs a
  measurement before it is worth the extra knob.
- [ ] **Tet meshing performance.** Measured 31k–80k tets/s (win-x64, Release), which is
  usable but well off TetGen. The profile is Delaunay build + per-pass classification;
  the obvious lever is replacing the winding-number classification inside the
  refinement loop with a flood fill over element adjacency once the boundary is known
  (winding numbers only for the initial seed). Also: `SurfacePatches`/`ClaimFaces`
  rebuild per round rather than incrementally, and `BuildEncroachmentIndex` rebuilds a
  BVH per refinement pass.
- [ ] **Boundary-layer residuals** (the layer itself landed — `BoundaryLayerSpec`,
  graded stack marched inward from a `Facets`-selected wall, prisms split by
  Dompierre's index rule, interface read back off the fill; see the Fea README and
  design.md §3b). What it does NOT give:
  - **Uniform thickness only.** No per-facet or per-tag thickness law, and no
    `cos`-correction at a convex corner: the march measures its thickness ALONG its own
    direction, so a box corner's perpendicular stand-off is `cos` of half the corner
    angle (0.577) rather than the requested value. `MinMarchClearance` reports it. A
    per-node correction is the obvious next step and is a local change.
  - **The wall triangulation is not refined for you**, and because the interface is
    frozen this is what sets the in-plane element size — measured, a plain
    two-triangles-per-face box gets no core refinement at all and reports 72 declined
    points. Auto-refining a PLANAR wall patch (and its columns) before marching is safe
    and would remove the sharpest edge of the limitation; a curved wall's midpoint is
    not on the surface, so that half needs the surface, not just the mesh.
  - **A rim may only stop on FLAT faces.** More than two distinct non-wall plane normals
    at a rim vertex is refused by name. Sliding a rim along a CURVED neighbour needs a
    projection onto that surface, which the mesher has no handle on today.
  - **Concave corners stretch rather than fan.** A single marched node at a reentrant
    corner produces a sharp wedge in the offset surface; a peanut (two unioned spheres)
    and a dumbbell prism both fail recovery on the resulting slivers. Multiple normals
    per node (the standard fix) is a real feature, not a tweak.
  - **The self-intersection net is unreachable from real bodies** — see design.md §3b for
    why that is structural rather than lucky. If a shape ever reaches it, that is the
    fixture the backstop has been waiting for.
- [ ] **Tet meshing breadth**: hex-dominant or voxel/SDF-based meshing (cut cells from
  `Sdf.Sampled` grids) as an alternative route; *curved* (iso-parametric) quadratic
  elements, whose
  mid-edge nodes would sit on the true surface rather than at edge midpoints — note
  that this needs a decision about what a shared node means where two boundary patches
  meet at an angle, which is exactly why the current layer is deliberately
  straight-sided; and coincident interfaces between bodies (v1 meshes disjoint bodies
  only, and refuses overlapping ones by name).
**Structural (linear static) landed** (`Material`/`Materials`, `AnalysisMesh`,
`StructuralModel`/`Facets`/`Dof`, `StructuralSolver`/`FeaSolveReport`,
`StructuralResults`, `TetElement`/`TetQuadrature`; docs `examples/fea-structural.md`) —
4- and 10-node tets, AMD-ordered sparse Cholesky or CG, facet-selector boundary
conditions, volume-weighted nodal stress, `MeshField` publishing with a display-mesh
sampling step, `.vtu` export. Verified: patch tests exact to 1e-13, manufactured-solution
orders 2.00/1.00 (linear) and 3.03/2.02 (quadratic), cantilever within 0.01% of
Euler-Bernoulli, Kirsch/Howland within 0.44%. Residuals below.

**Modal analysis landed** (`ModalSolver`/`ModalSolveOptions`/`MassLumping`/`ModalSolveReport`,
`ModalResults`/`VibrationMode`/`RigidBodyMode`, `LanczosEigen`, `RigidBodyModes` shared with
the static solver, `TetElement.ConsistentMass` shared with `ThermalElement.Capacity`; docs
`examples/fea-modal.md`) — shift-and-invert Lanczos with deflation, locking and restarts over
ONE factorization; consistent and HRZ mass (row-sum refused by name for 10-node elements);
rigid-body modes separated rather than refused. Verified: axial bar within 0.021% with
convergence orders 2.00/4.12 against theory 2/4, cantilever −0.07% from Euler-Bernoulli with
the shear gap growing by mode, simply-supported within 0.62% of Timoshenko, free-free rigid
modes at 2.4e-12 of the first elastic eigenvalue, orthogonality at 7.1e-15/5.8e-13, effective
mass 61.09% against the classical 0.6132. Residuals below.

**Buckling, stress stiffening, damping and frequency response landed**
(`BucklingSolver`/`BucklingResults`, `TetElement.GeometricStiffness`/`TetQuadrature.ForGeometric`,
`FeaAssembly` shared with the modal solver, `ModalSolveOptions.Prestress`, `RayleighDamping`/
`ModalDamping`, `HarmonicSolver`/`HarmonicResponse`/`HarmonicSweep`; docs
`examples/fea-buckling.md`) — the filed note that the shift logic needed revisiting was right,
and the answer turned out to be that the shift is not the free parameter at all: `A^-1 B` is
self-adjoint in BOTH the A and the B inner product, so the iteration runs in the K inner
product with operator `K^-1(-Kg)`, factorizes K itself, and needs NO shift because the
substitution `theta = 1/lambda` has already made the wanted eigenvalue extreme. `LanczosEigen`
took exactly one generalization for it (the metric became a parameter separate from the
right-hand matrix) and the modal path is unchanged to the bit. Verified: all four Euler end
conditions within 0.05–0.70% of the shear-corrected load, refinement monotone from above,
`omega²(P)/omega²(0) = 1 + P/P_cr` to 7.4e-10, resonant amplification 25.006 against 25.000,
half-power bandwidth within 0.54%, the static correction exact to 1.8e-16. Residuals below.

- [ ] **FEA: non-proportional damping — the quadratic eigenproblem.** A discrete dashpot, two
  materials with different loss factors in one model, viscoelasticity or hysteretic damping all
  leave `phi' C phi` with off-diagonal terms, at which point the damped modes are no longer the
  undamped ones and `(lambda²M + lambda·C + K)phi = 0` needs a `2n` state-space linearization
  in a NON-SYMMETRIC matrix pair — so neither `SparseCholesky` nor `LanczosEigen` applies and
  the modes come out complex. Scoped separately on purpose; `RayleighDamping`'s docs say
  precisely what is and is not covered. Note the steady-state RESPONSE under such damping no
  longer waits on this: `DirectHarmonicSolver` factors the full complex system per frequency
  with the model's own damping assembled — what remains open here is the damped NATURAL MODES.
- [ ] **FEA: residual-VECTOR basis augmentation.** `HarmonicSolveOptions.StaticCorrection`
  handles the static part of what truncated modes miss (mode acceleration), which is most of it
  — 3.079% → 1.8e-16 at zero frequency on the cantilever. The remainder wants the static
  response orthogonalised against the kept modes and added to the basis as a pseudo-mode, which
  also improves the response at non-zero frequencies rather than only at DC.
- [ ] **FEA: adaptive block shrink on Lanczos QR rank deficiency.** Block Lanczos landed
  (`ModalSolveOptions.BlockSize`/`BucklingSolveOptions.BlockSize`; design.md §3e carries the
  three measured findings) and treats a rank-deficient residual block as a BREAKDOWN — return
  what converged, restart — because restarting is slower and never wrong. The standard
  refinement is to drop the collapsed column and continue with a narrower block, which saves
  the restart's re-convergence; deliberately not built until a fixture wants it, since no
  case in the suite reaches the breakdown path other than by exhausting a small space.
- [ ] **FEA: nonlinear transient (contact, plasticity, large deformation).** Each makes the
  problem a nonlinear solve WRAPPING the linear stepper, with a Newton residual iteration inside
  every step — the stiffness is re-evaluated about the current configuration rather than once
  about the undeformed one. `TransientSolver`'s loop is the inner half of it and its energy
  identity is the natural convergence diagnostic (an energy-balance residual that stops falling
  is a Newton that has stalled). Filed as a different solver, not an option.
- [ ] **FEA: nonlinear buckling (post-buckling and imperfection sensitivity).** The linear
  eigenvalue factor is computed about ONE static state and assumes the prestress scales with
  the load without redistributing. A shell or a thin-walled section can buckle at a fraction of
  that number, and finding out needs an arc-length (Riks) continuation over a geometrically
  nonlinear solve — a different solver, and the honest scope statement is already in
  `BucklingSolver`'s limitations rather than implied away.
- [ ] **FEA: laminate theory and directional failure criteria.** `ElasticLaw` landed
  (orthotropic / transversely isotropic / fully anisotropic, with a material frame; see
  design.md §3h), and it deliberately stops at the constitutive law. Two things a composite
  user reaches for next, neither of which the law can supply. **(a) A LAMINATE is a stack of
  plies at different angles**, and today that is either several mesh regions (which needs a
  ply-thick element through the stack — expensive, and the mesher would have to be told to
  put layers there) or a homogenised law the CALLER computes with classical lamination
  theory. Doing it here means an `A`/`B`/`D` matrix vocabulary and a decision about whether a
  solid element can represent bending-extension coupling at all, which it cannot without
  through-thickness resolution — so the honest first step is a `LaminateStack` that produces
  a homogenised `ElasticLaw` and SAYS what it dropped. **(b) `MaxVonMises` on a composite
  part is a number with no engineering meaning** — a directional material fails by Tsai-Wu,
  Hashin or maximum strain against per-direction allowables, all of which want the stress
  resolved in the MATERIAL frame, which `ElasticLaw` knows and `StructuralResults` does not
  ask it for. That is a post-processing vocabulary (`ElasticLaw.ToMaterialFrame(stress)` plus
  a `FailureCriterion` with its allowables) rather than a solver change, and it is the more
  valuable of the two.
- [ ] **FEA: ADAPTIVE refinement, now that there is something to refine against.**
  `StructuralResults.ErrorEstimate` gives a per-element energy-norm error (design.md §3i),
  which is exactly the map an adaptive loop consumes — and the loop itself is the thing
  nobody has built: solve, estimate, choose a target size field from the element errors
  (the standard rule is `h_new = h_old · (target/e_local)^(1/p)` for an equidistributed
  error), re-mesh, repeat until the global figure is under a stated tolerance. Two things
  make it more than a for-loop here. **(a) `TetMesher` re-meshes from the SURFACE**, so
  every pass throws away the volume mesh and its boundary recovery rather than refining in
  place; a `SizingField` from the previous pass's errors is the cheap route and it needs the
  errors mapped from the old elements onto a spatial field (an `Sdf`-shaped
  `Func<Vector3d,double>` over a BVH of the old mesh, which is a small piece of work). **(b)
  The stopping rule wants a decision**: a global 5% target is the textbook one, but a model
  with a genuine singularity — a re-entrant corner, a point load — never reaches it and the
  honest loop caps the passes and REPORTS the figure it stalled at rather than refining
  forever. That is the same shape as the boundary-recovery non-convergence detection.
- [ ] **FEA: a bi-material colour plot still shows the blended interface value, because
  `MeshField` has one value per vertex.** `StructuralResults.Fields()`/`SampleOnto` publish
  the node-indexed `NodalStress` and `ThermalResults`' publish `NodalFlux`, so the honest
  per-material values (`NodalStressIn`, `NodalFluxIn`) stop at the API boundary and never
  reach a viewer or a `.vtu` — one gap with two spellings. Two shapes
  are plausible and the choice is a decision rather than a coding job. (a) **One field per
  region**, NaN outside it — which composes with the recorded rules (`FieldRange` skips NaN,
  and a NaN now paints the distinct `ColorMaps.NoValueColor` grey, so the out-of-region
  surface reads as "no data" rather than as a value) and needs a `FieldDisplay` that can
  show several fields at once. (b) **VTK's own
  answer**: write the interface as a cell-data array, or duplicate interface nodes per region
  so each material has its own surface — exact, and it changes what "node i" means to every
  consumer downstream. Note the whole thing is unreachable until conforming interfaces land
  (`AnalysisMesh.InterfaceNodeCount` is zero for every mesh the public API can build), so this
  is a follow-up to that item rather than to this one.
- [ ] **FEA: the factorization is the wall, and it is Core's to move.** In Release the
  cost is overwhelmingly `SparseCholesky`: at 46 800 DOF a linear solve measures 323 ms to
  assemble, **79 009 ms to factor**, 249 ms to substitute and 45 ms to recover stress. The
  up-looking algorithm is unblocked (no supernodes, no BLAS-3 inner kernel), which is fine
  for the mesh Laplacians it was written for and is the binding constraint on FEA scale.
  Measured against it with the two interleaved in one sitting, Jacobi-preconditioned CG
  wins at EVERY size with linear elements and past ~15 000 unknowns with quadratic ones —
  2.0x / 3.9x / 15.3x / **48.6x** at 2 160 / 6 552 / 14 688 / 46 800 DOF — the OPPOSITE of
  the Laplacian crossover recorded in CLAUDE.md, as that note itself predicts.
  <br>**ASSESSED, not built — and the assessment says WHICH option, which was not obvious.**
  `SparseCholesky.Analyze` (new, and cheap: 5–520 ms where the factorization is 0.1–134 s)
  reads the three deciding numbers straight off the symbolic pass. On this project's
  cantilever (`FeaBenchmark.WhatWouldMoveTheFactorizationWall`):

  | | free DOF | ordering | factor nnz | updates | longest column | parallel ceiling |
  | --- | ---: | --- | ---: | ---: | ---: | ---: |
  | linear | 14 688 | Natural | 22 701 719 | 1.88e10 | 1 732 | 1.0x |
  | linear | 14 688 | Amd | 8 769 703 | 4.32e9 | 1 702 | 1.6x |
  | linear | 46 800 | Amd | 57 616 104 | 6.42e10 | 3 537 | 1.6x |
  | quadratic | 14 688 | Natural | 96 705 622 | 4.20e11 | 12 956 | 1.0x |
  | quadratic | 14 688 | Amd | 6 498 728 | 2.54e9 | 1 400 | 1.9x |

  - **Tree parallelism is NOT the lever, and the number kills it outright**: the ceiling is
    **1.0x natural and 1.6–1.9x AMD**, with unlimited processors and free synchronisation.
    The reason is structural rather than incidental — in 3D, the top separator's columns are
    a constant fraction of all the work and they form a CHAIN, so a schedule that respects
    the elimination tree has almost nothing to overlap. Do not write a parallel scheduler
    for the up-looking algorithm; it cannot pay.
  - **Blocking IS the lever, and the same table says why**: the longest column is
    1 400–3 748 entries under AMD, so the work that the tree cannot parallelise sits in a
    few nearly-dense columns — which is exactly what a supernodal/multifrontal kernel turns
    into dense BLAS-3, where the parallelism and the vectorisation both live. That is also
    the answer to "why is the tree ceiling so low": the root separator is the work.
  - **A better preconditioner remains unmeasured** and is the other honest option (IC(0),
    or algebraic multigrid, which is what production FEA uses). Note the bar it has to
    clear is not the factorization but CG-with-Jacobi, which already wins by 48.6x at the
    top of the table — the direct path's value there is exactness, not speed.
  - **The by-product is worth more than it looks**: `UpdateCount` predicts factor time at
    **~1.0–1.3 ns per update** across a 15x size range and both element orders (idle
    machine; the constant is machine-specific and spreads on a loaded one). So a caller can
    be told what a factorization will cost BEFORE paying for it, which is the other half of
    what `FeaSolveReport.Advisory` could not do.
  <br>Note for whoever takes this: **benchmark in Release**. The same runs in Debug look
  assembly-dominated (1 822 ms assemble against 657 ms factor on the docs bracket) and
  would send the work into the wrong phase entirely. And **an idle machine**: the same
  Release binary measured the 46 800-DOF factor at 79.0 s idle and 134.4 s with other work
  on the box, so absolute times are only comparable within one sitting.
- [ ] **FEA: should `Direct` still be the default, and what would settle it.** It is the
  default today for EXACTNESS, not speed — the verification claims (patch test to
  round-off, strain at 1e-13, the two orders agreeing on strain energy to twelve digits)
  are not statements one can make about an iterative solve stopped at a relative residual,
  so a CG default would make every headline accuracy claim a statement about an opt-in
  path. That reasoning is written into the enum doc.
  - ✅ **A multi-load-case entry point** — `StructuralSolver.SolveAll` LANDED. The classic
    second argument for a direct solver is now true here rather than filed: N cases pay for
    one factorization and N substitutions, measured **3.5–3.8x for four cases and 6.94x for
    eight** against solving them one at a time, with an extra right-hand side costing
    0.7–27 ms against 34–4 706 ms to factor. It also divides the direct-vs-CG ratio by N,
    since CG reuses nothing, and `FeaSolveReport.Advisory` now says so and falls silent once
    the amortisation has won.
  - [ ] **Evidence that a size-based automatic pick is sound.** Still deliberately not done:
    a crossover measured on one cantilever measures that cantilever's conditioning, and
    baking its threshold into the library default would be exactly the mistake the row above
    documents. Fixtures of genuinely different conditioning (a thin shell-like plate, a
    near-incompressible nu, a graded mesh) would be needed before a threshold means
    anything.
  - [ ] **A reusable factorization as a VALUE**, if a caller ever needs load cases it cannot
    enumerate up front (an optimisation loop, an influence-coefficient sweep, a
    load-stepping scheme). `SolveAll` takes the list eagerly, which is right for the case it
    serves and wrong for a generator; the shape would be a `StructuralOperator` carrying the
    reduced matrix, the DOF map and the factorization, with `Solve(loadCase)` on it. Filed
    rather than built because nothing in the repo generates load cases lazily yet, and an
    object that hands out a live factorization has a lifetime question (it is large, and it
    silently goes stale if the model is edited) that a list does not.
  <br>Mitigated on both sides now: `FeaSolveReport.Advisory` names the slow factorization
  after the fact, and every solve entry point takes a `ProgressCancel` that reaches
  `SparseCholesky.Factorize`'s per-column loop, so the first run can be watched and
  aborted rather than only read about afterwards.
- [ ] **FEA: an optional `ILogger` on the solve — RULED PERMITTED, and the `ProgressCancel`
  prerequisite has now LANDED, so this is a live decision rather than a deferred one.**
  Recorded here so it is not escalated a third time. The rule as it now stands does not say
  "Viewer and Mcp only" — the app-layer work already relaxed it to **"extends inward on a
  weighed per-project basis"**, and `EngrCAD.Interop` and `EngrCAD.BRep` both carry the
  reference (event IDs 80/90). Weighing Fea by that same rule: it is a long-running
  operation with nothing above it to report on its behalf, which is exactly the condition
  the Interop/BRep grants were made for, so it qualifies. Core, Mesh and Implicit stay
  dependency-free: each still has a consumer seam above it, which is the actual test the
  rule applies.
  <br>**What the `ProgressCancel` work leaves for a logger to do, assessed rather than
  assumed.** Cancellation and a live fraction now cover the case that prompted the item (a
  caller who wants to know it has not hung, and to abort). Two genuinely different needs
  survive, and both are narration rather than control: (a) *which phase* a solve is in —
  the fraction says how far the factorization has got but not that assembly finished in
  0.3 s and the factorization is the thing running, which is what a user reads to decide
  whether the mesh is the problem; and (b) an *unattended* run (a CI regression sweep, a
  batch of load cases) where nobody is holding a progress callback and the record has to
  survive the process. Neither is served by a return value, since both are about what
  happened *during* rather than *at the end*. A logger would take event IDs in a new decade
  (100s), Information for phase boundaries with the sizes and the milliseconds, Debug for
  per-element or per-step detail. Still worth pausing on: `FeaSolveReport` already carries
  every number such a log line would print, so the honest scope is timestamps and ordering,
  not new information.
  <br>Original framing, kept because it states the question well:
  The other candidate pre-solve channel, and the precedent is real: Interop and BRep took
  the `Microsoft.Extensions.Logging.Abstractions` reference for exactly this ("the long
  operations accept an optional trailing `ILogger`", event IDs 80/90). What makes it
  Chris's call rather than an implementer's is that CLAUDE.md records the current line as a
  deliberate REVERSAL of the earlier `IEngrCadLog` shim, and records the leaf kernels
  (Core, Mesh, Implicit) as staying dependency-free on the reasoning that everything the
  backlog named was reachable at the Interop/BRep seams. `EngrCAD.Fea` did not exist when
  that was written and has no such seam above it — it is a leaf that is also a long
  operation, which is the case the rule never had to consider.
- [ ] **FEA: assembly parallelisation — MEASURED AND DECLINED, with the numbers, so nobody
  redoes it.** The item read "it is embarrassingly parallel per element with a per-row merge
  at the end", and the loop's shape does look like that. Measuring the phases separately
  (`FeaBenchmark.WhereAssemblyTimeGoes`) says otherwise: **computing the element stiffnesses
  is 6–18% of assembly** and everything else is the scatter into the builder, which is a
  shared write whose ORDER decides the last bits of every summed entry and therefore cannot
  be parallelised without giving up bit-identical output. Assembly is itself ~4% of a
  quadratic direct solve and ~13% of a large iterative one, so a perfect parallelisation of
  the parallelisable half is worth under 2% of a solve.
  <br>The design that WOULD be correct if that share ever grows, recorded so the thinking is
  not lost: per-block `SparseMatrixBuilder`s merged **in block order**, which reproduces the
  serial add sequence exactly (block order = element order, and order is preserved within a
  block), so the packed values stay bit-identical; per-block right-hand-side contributions
  have to be recorded as ordered (index, value) lists and replayed rather than summed into
  dense partial vectors, or the summation order moves. The merge is the cost and it is a
  copy of every entry.
  <br>The item's second half — "the reaction/energy pass recomputes every element stiffness
  a second time" — is true and also not worth fixing: the whole pass measures 5–18 ms
  against hundreds of ms to assemble and seconds to factor, and holding every element
  stiffness would cost 95 MB on the largest linear case to save single-digit milliseconds.
  `FeaSolveReport.ReactionMs` now reports the phase so the decision has a number rather than
  hiding inside "total".
  <br>✅ **What the measurement actually found is fixed**: `SparseMatrixBuilder`'s per-row
  insertion sort was O(k²) at k = 612 raw entries per row, which a 10-node tetrahedral mesh
  reaches routinely and a vertex ring never does. A stable key sort took quadratic packing
  **250 ms → 31 ms** (7.25x on the sort, interleaved against the old code transcribed
  verbatim) and made it linear in the entry count.
  <br>What is left open here is the OTHER half of the scatter: appending ~1.4 M tuples into
  15 000 per-row `List<(int, double)>` measures ~78 ns each, which is allocation churn
  rather than work. A flat append-only `(row, col, value)` array with a stable counting sort
  by row at pack time would remove the per-row lists entirely, is bit-identical by the same
  argument, and is cheaper in memory — but it is a rewrite of the most widely shared class
  in `Core.Solvers` to buy a few percent of a solve, so it wants a consumer that is actually
  assembly-bound first.
- [ ] **FEA: contact, plasticity, large deformation.** Each is a different mathematical
  problem rather than a bigger version of this one (a nonlinear solve wrapping the linear
  one). Modal has landed, so the assembly is now shared by three physics and a fourth
  consumer would be the fifth reason not to fork it.
- [ ] **FEA thermal follow-ups** (v1 ✅ landed — `ThermalModel`/`ThermalSolver`/
  `ThermalResults` + `StructuralModel.ThermalLoad`, docs `examples/fea-thermal.md`):
  - [ ] **Two-way coupling** (deformation feeding back into conduction) is a staggered or
    monolithic solver, not an extension of the one-way path. Filed for completeness; the
    one-way direction covers thermal stress, which is what is usually wanted.
- [ ] **Results/fields follow-ups** (v1 ✅ landed — `MeshField`/`FieldRange` +
  `VtuWriter` in EngrCAD.Mesh, `Part.Results`/`FieldDisplay`/`TryResolveFieldDisplay` in
  Modeling, `ColorMaps`/`FieldRendering`/`FieldLegend` in Viewer.Core, drawn in all
  three front ends with `--export .vtu` and `docs/examples/fields.md`):
  - (A deformed part's missing feature edges, and picking during an animation, moved to
    their own item below now that the deformation rides a uniform.)

- [ ] **Transient playback residual rungs** (the core ✅ landed — `FieldSequenceTrack`,
  window playback, stills, the batched export, AND web parity: the browser applies a
  step through `updateFieldColors`, a colours-only `gl.bufferData` into the mesh's
  field buffer, driven from the same `TryDisplayFor` rule and verified by the `?report`
  A→B→A pixel check — `fieldStepPixels > 0`, `fieldStepReturn == 0`):
  - (The frequency/load-step selector ✅ landed as the properties panel's **Result
    dropdown** — `DocumentEdits.SetFieldDisplay` + `ParamEditors.ResultChoices`, one
    undoable edit keeping the rest of the display; a CONTINUOUS slider stays declined,
    results being named states. Result persistence already rides the document
    envelope.)
- [ ] **A dependency-free MP4 route is FLAGGED, not promised** (the Reel/Short presets
  ✅ landed — `ReelFormat`/`ReelFraming`/`ReelExport.RenderReel`/`RenderReelPoster`, safe-area
  framing as a geometric assertion, duration caps as refusals naming the platform, the
  half-step aliasing measurement, and the ffmpeg recipe on the result; design.md §6b has
  the record): linking ffmpeg is ruled out by the dependency-free ethos and a hand-rolled
  H.264 encoder is a product-sized campaign — the smaller intermediate worth COSTING
  first is Motion-JPEG-in-MP4 or a minimal MPEG-4-container muxer, recorded so nobody
  starts H.264 from scratch.

## OpenSCAD feature parity (open items)

What remains from mapping OpenSCAD's feature set against EngrCAD (the covered ground —
primitives, 3D booleans, transforms, linear/rotate extrude + RMF sweep, STEP/STL/OBJ/PNG
export — is recorded in CLAUDE.md):

- [ ] **Text follow-ups** (`Shape.Text` ✅ landed — dependency-free TrueType reader,
  glyphs → exact sketch segments, containment-based counter detection, layout with
  `kern` kerning; **CFF/OpenType-PostScript outlines ✅ landed** — `CffOutlines`, Type 2
  charstrings → cubic `BezierTo`, CID-keyed via FDArray/FDSelect, every `.otf` opens;
  **GPOS kerning ✅ landed** — `GposKerning`, PairPos 1+2 incl. Extension lookups, with
  the spec's GPOS-over-legacy-`kern` precedence; **text on a curve ✅ landed** —
  `Shape.TextOnPath`/`TextOutlines.SketchesOnPath` over a `GlyphPose`, glyphs placed
  rigidly by mapping control points only (exact, because a Bézier is an affine
  combination of them), arc-length spacing, mid-advance anchoring, left-normal "up",
  closed paths wrapping and multi-line refused by name; **vertical alignment ✅ landed** —
  `TextStyle.VerticalAlign`, measured from the font's ascender/descender rather than the
  ink; **`TextFeature` ✅ landed** — `TextFeature(text, font)` in `StandardFeatures.cs`,
  `[Param]` Size/Height/LetterSpacing/Engrave/Plane, emboss/engrave with the Drill
  overshoot, the text+font as CONSTRUCTOR inputs so a fresh instance re-runs and the
  regeneration cache covers the font without the snapshot naming it, opaque to persistence
  by name since a font has no data form): **variable fonts** (`fvar`/`gvar`, incl. `CFF2` —
  rejected loudly today; PRODUCT-SIZED — fvar/avar axis parsing, gvar per-glyph delta
  interpolation with IUP, and CFF2's separate blend charstring interpreter, each with its
  own synthetic-font fixture); **`seac` accent composition ✅
  landed** — charset formats 0/1/2 parsed off DICT op 15 (absent/0 = the ISOAdobe
  identity; the predefined Expert charsets refuse at seac by name), the 256-entry
  Standard Encoding table transcribed (⚠ verify-against-datasheet), and the 4-argument
  endchar composes base + accent shifted by (adx, ady) VERBATIM — Type 2 carries no
  sidebearing operands, so the Type 1 asb correction has nothing to correct, the
  decision documented in place. A nested seac component refuses by name (the spec
  forbids it, and the refusal is what bounds the recursion); unresolvable codes name
  the code. Verified coordinate-for-coordinate on synthetic fonts, with the charset
  tests choosing codes the TABLE routes to different glyphs than the identity would —
  which is what proves the table was read rather than assumed.
- [ ] **Heightmap follow-ups** (`surface()` ✅ landed — `Shape.Heightmap` +
  `Heightmap.Mesh/ReadDat/ReadPng`; **colour-PNG luminance ✅ landed** — truecolor RGB/RGBA
  → Rec. 709 relative luminance `0.2126 R + 0.7152 G + 0.0722 B`, a documented rule, alpha
  ignored, palette still refused by name; **chunk CRC verification ✅ landed** — CRC-32/
  ISO-HDLC checked on critical chunks, a corrupt IHDR/IDAT named): Adam7 interlace
  ✅ landed (seven per-pass filter streams, empty passes contributing no bytes,
  twin-encoder-verified bit-identical to the straight spelling across gray-8/16,
  truecolor and empty-pass sizes); nothing remains open here.
- [x] ~~mirror B-Rep completion, remaining nodes~~ ✅ **landed in full** — revolve/sweep/
  rim/drill earlier (axis negation `F∘R(d,θ)∘F = R(−F·d, θ)` for revolves, intrinsic RMF
  for sweeps, isometry-commuting surgery for rims/drills), and now `Draft` /
  `Shell(t, openings)` / `RoundEdges` / `Loft` plus the pure taper (which lowers AS a
  two-section loft, so leaving it refused would have been one operation disagreeing with
  itself). Those five needed no identity — each is defined by lengths and angles alone —
  and Draft's pull direction takes its linear image un-negated. The last refusal in the
  family is gone too: `SheetMetalBody` is Native under a mirror by being re-DECLARED
  rather than re-placed — an ordered, edge-quoted flange tree is rebuilt the other way
  round (`MirroredInPlane`) and placed on a proper frame, since P = P′·FlipX.
- [ ] **2D offset follow-ups** (`Region2dOffset`/`Sketch.Offset` ✅ landed — round/miter/
  chamfer joins, erosion as complement dilation; **open-path stroking ✅ landed** —
  `Region2dOffset.Stroke(path, width, cap, join)`, butt/round/square caps, both-side
  corner joins so reversals get round noses, closed circuits enclose holes; **exact
  curved offsets ✅ landed** — `CurvedRegion2dOffset` keeps arcs as arcs and makes round
  joins true sectors, which retires the inscribed-arc contract rather than honouring it):
  **variable offset along the
  outline ✅ landed** (`Region2dOffset.Offset(region, distances)` — per-vertex distances,
  linear in arc length, all-equal delegating to the constant path bit-identically) —
  **and the filed construction was WRONG in the instructive direction**: the exact slab
  of a linearly varying disc is bounded by the EXTERNAL TANGENT line of the two end
  circles (tilt sin φ = Δr/L), not the trapezoid through the offset endpoints, which
  under-covers near the smaller end by the tangency wedge (asserted by a witness point
  between the secant and the tangent). Verified by an EXACT membership oracle — the
  per-edge minimisation of |p − e(t)|² − r(t)² is QUADRATIC in t, so the predicate is
  closed form and thousands of grid probes assert the built region against it outside
  the join arcs' chord band. Refused by name: holes (compose outer and holes yourself,
  v1), non-positive distances (variable EROSION stays open here — the complement trick
  needs the frame's distances defined, a real design question), and an edge whose
  distance changes by more than its length (the larger disc swallows the sweep; no
  tangent exists). Also open: the CURVED tier's variable twin (`CurvedRegion2dOffset`
  — a variable offset of an ARC is a spiral, not an arc, so it needs a fit tier), and
  `Sketch.Offset` exposure once erosion composes.
- [ ] **Twist-extrude follow-ups** (`Shape.Extrude(sketch, height, twist, scale, slices)`
  ✅ landed — taper = B-Rep-Native ruled loft, twist = direct mesh section sweep with
  twist-matched profile subdivision + collinear-chord-zip caps, implicit via mesh SDF):
  an exact twisted B-Rep surface type would make twist Native (big kernel feature, low
  priority); tapered sketches with holes are B-Rep-Native now (loft sections carry
  holes).
- [ ] **Planar-view follow-ups** (`PlanarSection.OfMesh`/`OfSolid`/`SilhouetteOfMesh` +
  `Shape.Section`/`Shape.Silhouette` ✅ landed — both OpenSCAD `projection` modes):
  - [x] ~~**`Region2dBoolean` leaves ~1e-7-area pinholes at near-tangency.**~~ **CLOSED
    with the opposite finding, which is why the note survives the closure.** The
    boolean is correct and the MESH has the hole: 780 of 780 probe points inside the
    64x48 torus's 1.45e-5 side-on hole are covered by ZERO facets, tested with the
    exact `Orient2d` over every triangle. In the band |z| ∈ [r·cos(π/n_minor), r] — the
    minor polygon's scallop, 4.28e-3 deep at n = 48 — the discrete tube only reaches
    that height near its minor-polygon VERTICES, and the major discretization breaks
    that thin band into lenses that need not overlap; the hole measured 1.16e-3 deep, a
    quarter of the scallop. Nor is it systematic: holes at 64x48, none at 32x24, 96x72,
    128x96, 64x96 or 128x48, because whether two lenses overlap is an alignment
    question. The test now asserts the strong form (every hole is uncovered by every
    facet). Residual, if anyone wants the SMOOTH body's silhouette: that is the
    "B-Rep silhouettes" item below, not a boolean fix.
  - [ ] **B-Rep silhouettes** — true silhouette curves on curved surfaces. Today the
    outline is always mesh-derived, so its fidelity is the mesh's however exact the solid.
    Now has a second consumer with a sharper need: `HiddenLineRemoval` draws a smooth
    surface's outline from the display mesh's view-dependent silhouette and labels it
    `EdgeSource.Silhouette` precisely because it is the one part of a drawing that is not
    exact. Exact HLR is blocked on this.
  - [ ] **`OfSolid` on a flush plane** — a plane containing a face or an edge throws
    (that section is an area, not a curve). **The coplanar-boolean tier landing did NOT
    unblock this, and the counterexample is worth keeping**: the natural repair — flush
    faces contribute their regions, everything transversal sections as before, union —
    equals NO limit section. A fused step block (slab footprint A under a boss footprint
    B ⊂ A) sectioned at the step plane has flush faces covering only A∖B, the boss's
    walls merely TOUCH the plane, and the interior under B has no face there at all — so
    the construction returns A∖B where the limit from below is A and the limit from
    above is B. A flush section needs INTERIOR classification, and the honest mechanism
    is the FdmSlicer's own: two ordinary sections nudged ±δ (each exact), returned as
    the below/above pair or their stated union — a semantics decision (which limit does
    `projection(cut=true)` mean on a graze?) before an implementation.
- [ ] `roof()` — straight-skeleton roof over a polygon; low priority
- [ ] **Camera-adaptive tessellation on zoom** — `TessellationQuality` ✅ landed (max
  angle + max chord deviation, per-solid resolution driving mesh AND feature edges);
  the follow-on is re-resolving against the on-screen pixel size of a radius when the
  camera zooms, which needs re-tessellation plumbing in the viewer.
  **Assessed; the criterion is the easy half and the PLUMBING is the item.** Deriving
  the target is one line — a chord deviation of half a device pixel at the current
  camera, i.e. `deviation = 0.5 * worldPerPixel(distance, fov, viewportHeight)` fed to
  the existing `TessellationQuality.MaxChordDeviation` — and `Part.GetMesh(quality)`
  already re-tessellates for a different criterion. What is missing, and what makes this
  a real piece of work rather than a knob:
  (a) **A re-tessellation must not run on the render thread and must not run per frame.**
  It needs `TabMeshLoader`'s generation-token discipline (a zoom that supersedes an
  in-flight re-mesh must not land) plus hysteresis — re-mesh on a factor-of-two change in
  the criterion, not on every wheel notch, or a drag queues dozens of tessellations.
  (b) **Every derived cache keys off the mesh**: feature edges, the pick BVH and the
  ambient-occlusion bake are all per-mesh, so a re-tessellation invalidates three
  expensive things. The AO bake is 12.3 s on the demo scene, which alone rules out
  re-baking per zoom level — the honest v1 keeps the coarse bake and accepts that
  occlusion is one level behind, or caps adaptivity to parts under the AO opt-out
  threshold.
  (c) **`Part.TryGetSolid` is the saving grace**: the B-Rep lowering is cached and
  criterion-independent, so a re-tessellation is the tessellate half only — which is
  what makes this affordable at all (measured elsewhere: lowering dominates a Shape
  part's meshing).
  (d) **The oracle is awkward**: the docs-PNG byte comparison cannot see this (renders
  are one-shot at a fixed camera), so it needs its own test — mesh a large-radius part
  at two camera distances and assert the segment counts differ in the direction the
  criterion predicts, plus that a zoom back out does not *coarsen* below the quality
  floor mid-session (a part that visibly loses detail when you pull back reads as a bug
  even when it is the criterion working).
  Worth about a day and a half. Not blocked on anything; deliberately not started in a
  sweep, because a background re-mesh triggered by camera motion is exactly the kind of
  feature that is fine in every test and janky in the hand.
- [ ] **Debug-modifier follow-ups** (v1 ✅ landed — `Part.Ghost`/`Hidden`/`Isolated`
  + `DebugFilter` shared by window/offscreen/exports/MCP; `#` highlight deliberately
  stays the selection mechanism; **web viewport Hidden/Isolated ✅ landed** —
  `EngrCadViewport.ResolveInstances` applies `DebugFilter.Shown` exactly as the window /
  offscreen / `--export` / MCP do, and Ghost renders through `EffectiveDisplayMode`, so with
  no flags it is the identity; **tree-row graying ✅ landed** —
  `SceneTree.IsEffectivelyHidden` exposes the own-AND-ancestors chain per row, pure and
  tested, and the markup asks it rather than restating a visibility rule it could drift
  from; selection still golds, a hidden part being addressable). Nothing open here.
- [ ] `$t` animation — time-parameterized models; viewer re-tessellates per frame. This
  is the *expensive* cousin of the Animation section above and deliberately separate:
  that one moves poses and the camera only, which is why it can animate with matrices
  alone; this one changes geometry, so every frame pays a full lower + tessellate.
  **Design assessment recorded in design.md** ("$t — assessed and deliberately
  deferred"): shape is `Func<double, Scene>` + offline frame bake, the work is
  prefix/identity caching across frames, and it should be built only when a concrete
  model needs morphing geometry.
- [ ] **DXF/SVG follow-ups** (v1 ✅ landed — `DxfDocument` LINE/ARC/CIRCLE/LWPOLYLINE/TEXT
  with layers and an LTYPE table both ways, exact bulge arcs; `SvgDrawing`
  visible/hidden/section/thin line classes plus sheet-sized output and text over
  Section/Silhouette/Sketch/`DrawingSheet`; hidden-line classification is now COMPUTED
  from the model by `HiddenLineRemoval`; **DXF SPLINE entities ✅ landed** both ways —
  `DxfSpline` + `DxfCurveMode.Spline`, exact cubic round trip, reading narrowed to what
  converts exactly with rational and general B-splines reported by name; **`$INSUNITS`
  ✅ landed** both ways — declared on write, HONOURED (rescaled to mm) on read, `Unitless`
  never scaled). **Two remain, and BOTH turn out to be changes to what the drafting layer
  PRODUCES rather than to how a writer spells it** — which is why neither is the small job
  its one-line description implies:

  - [ ] **SVG hatch as a `<pattern>` fill** rather than one path per hatch line (smaller
    files for a big section). Same shape: `SheetHatch.Fill` returns clipped LINE SEGMENTS,
    and a `<pattern>` needs the cut REGION plus a tile, so the hatch layer's output type
    changes. The anchoring survives either way — `patternUnits="userSpaceOnUse"` is
    origin-anchored exactly as the exact even-odd scan already is — so what is at stake is
    the content model, not the geometry.
- [ ] **DXF SPLINE follow-up: general B-spline decomposition.** Reading converts degree 1
  and cubics ALREADY in Bézier form; a general (uniform, or unevenly-knotted) B-spline is
  reported rather than sampled, which is right but leaves real third-party files on the
  floor. The exact fix is knot insertion to full multiplicity (The NURBS Book A5.6 Bézier
  decomposition) and it belongs in `EngrCAD.BRep` beside `BSplineBasis` — which is already
  public — not in a file reader; the DXF side is then two lines. Rational splines stay
  refused for a different reason: a sketch's `CubicSeg` is polynomial, so exactness would
  need a rational segment type, which is a `Sketch` vocabulary change rather than an
  import one.

## OpenCASCADE (OCCT) feature parity (open items)

- [ ] **Direct editing follow-ups** (`DirectEdit.OffsetFaces`/`MoveFaces`/`DeleteFaces` +
  the `Shape` overloads landed — see design.md §5 and `examples/direct-editing.md`; each
  item below was refused BY NAME in v1 with its reason, so none is a silent gap):
  - [ ] **Delete-face by EXTENDING the neighbours.** v1 heals only a wound that bounds a
    complete interior loop of a planar face (a boss, a pad, a pocket); a wound that runs
    only part of the way round a loop — deleting a chamfer band, a fillet band, a draft
    face — needs the two neighbours extended until they meet in a NEW edge, which
    `SurfaceCorner.TrySolveCurve` can already solve for the analytic pairs. The work is
    not the curve: it is the topology rewiring (two rim loops collapse into one edge) plus
    a soundness gate, since the extension can have no answer at all (a box's four sides
    extended past its deleted top never meet) and the refusal must come BEFORE any coedge
    moves. Note the v1 gate is `IsPlanar` on the loop-dropping face and the general fix
    subsumes it.
  - [ ] **Move a CURVED face.** Refused today because `CarrierBody.ConcentricRim` rebuilds
    each rim as a circle concentric with the ORIGINAL — exactly right for an offset (which
    leaves the axis alone) and false for a translation, which moves it. The fix is to take
    the rim's new axis from the new CARRIER rather than from the fit, keeping the phase
    rule (frame taken verbatim, never re-derived from a solved point). Would also unlock
    ROTATING a face, which is `Draft` with an arbitrary neutral line.
  - [ ] **Replace a face's surface** (OCCT `BRepTools_ReShape`): swap a planar face for a
    cone or a cylinder and re-solve the corners. `CarrierBody.Rebuild(carriers, what)` is
    already exactly that seam — it takes one carrier per face and rebuilds everything —
    so this is an API and a validation question rather than a geometric one.
  - [ ] **Direct edits as FEATURES.** They are `Shape` graph nodes today, so they compose
    and `Explain` reports them, but there is no `Feature` wrapper and so no `[Param]`
    distance a design study or a configuration could drive. The selector is a
    `FaceSetRef`, which already serializes, so the blocker is only that a `Feature` needs
    writing.
- [ ] **PDF export follow-ups** (the writer landed: `PdfDrawing` +
  `SheetWriter.ToPdf`, byte-fixed-point, twin-decoder-verified — see design.md §6c;
  each item below was declined in v1 with its reason and would be additive):
  - [ ] **Embedded font.** The standard-14 Helvetica over WinAnsi refuses the drafting
    symbols beyond the diameter sign (depth U+21A7, cbore U+2334, csk U+2335) and all
    non-Latin text. The TrueType reader already parses `glyf`; a subset embedder
    (FontFile2 + a CIDFont or a symbolic TrueType with a cmap) is the honest fix and
    removes the ⌀→Ø substitution too. Note the fixed point: an embedded subset must be
    a deterministic function of the used glyph set.
  - [ ] **PDF layers via optional content groups.** SVG and DXF carry the sheet's
    layers; PDF needs /OCProperties + `/OC BDC ... EMC` marked content to give Acrobat
    toggleable layers. Cheap, but every OCG is another object — keep the xref writer's
    object numbering a function of content so the fixed point survives.
  - [ ] **Opt-in Flate compression** for very large sheets (the BCL has `ZLibStream`).
    Declined as default: a sheet's stream is tens of KB and uncompressed ASCII is what
    the docs fence and the committed assertions read directly. If added, note zlib
    output is deterministic for a fixed level/strategy, so the fixed point can hold.
  - [ ] **Loose-profile/sketch PDF export** (`PdfDrawing.Add(Sketch)`): PDF paths are
    lines + cubic Béziers, so circular/elliptical arcs need flattening or the standard
    kappa cubic approximation — either way NOT exact, which is why the overload was
    refused rather than shipped silently lossy. Offer it with a stated tolerance
    parameter, mirroring `DxfCurveMode`'s honesty about what survives.

What remains against the reference B-Rep kernel (covered: primitives,
extrude/revolve/sweep, booleans, rim fillets/chamfers, drilled holes, conics + offset
curves, curve interpolation, projection/extrema, surface intersection, STEP
export+import, volume/area, tessellation — see CLAUDE.md):

- [ ] **Loft follow-ups** (`SolidFactory.Loft` + `Shape.Loft`/`LoftAlong` landed; each
  gap below is rejected by name today — assessed during the Shape wiring, none started):
  - [ ] **Open (uncapped) skins** — structurally blocked: `BrepSolid.Validate` requires
    two-manifold edge use, so this needs a sheet-body concept first, not a loft change.
  - [ ] **Periodic lofts** closing back on the first section — `LoftedSurface` needs a
    periodic knot vector in v plus band topology in BOTH parameters.
  - [ ] **Guide curves / spine** constraining the skin between stations — does NOT fall
    out of the cardinal basis (a guide constrains the blend *between* interpolation
    stations, which the collocation solve never sees); needs a constrained surface fit.
- [ ] Boolean extras: fuzzy tolerance, modification history (*section* ✅ landed).
  **`BrepBoolean.Section(a, b)`** (OCCT `BRepAlgoAPI_Section`) ✅ landed: the same
  per-face-pair `SurfaceIntersection` loop, but each curve is clipped to the region
  inside BOTH trims (`ClipToBothTrims`, the symmetric twin of the boolean's asymmetric
  `ClipToFace`, over the same `ClipBreakpoints`/`InsideForClip`) and RETURNED rather than
  fed to the splitter — a curve-only result that consumes nothing. The honest-endpoint
  caveat is stated in the API rather than hidden: analytic pairs (plane∩cylinder circle,
  plane∩plane line) give EXACT endpoints, tracer pairs give sampling-resolution ones, so
  it is a display/query answer, not sealed topology. Oracle (`BrepSectionTests`): a
  drilled-through plate's section is TWO circles, each sampled point on the radius-5 circle
  to the weld tier (proving it is the analytic circle, not a chorded polyline) at z = 0 and
  10, total length the closed-form 2·2π·5, disjoint solids section to nothing, and the
  inputs are not consumed. **Filed follow-up**: a `Shape.Section3d(other)` (curve-only) at
  the Modeling layer, and coincident/coplanar faces (a shared AREA, needing the
  coplanar-fusion rim machinery). **Fuzzy tolerance** is NOT a parameter to add but a
  rewrite of every coincidence decision in the splitter (OCCT threads it through BOPAlgo
  wholesale); the existing near-tangency rejections are the honest substitute.
  **Modification history** (which output face came from which input) is cheap to RECORD in
  `BrepBoolean` (fragments know their host face) and belongs with the topological naming
  item below — record at the boolean, resolve at the Shape layer.
- [ ] **Fillet follow-ups** (sharp-corner miters, edge sets, chamfer angles and
  whole-solid `FilletAllEdges` ✅ landed) — all of these are refused loudly today, so
  they are additions, not bug fixes:
  - [ ] **General corner machinery follow-up** — general trihedral corner patches
    ✅ landed (`FilletAllEdges` now rounds tetrahedra and drafted blocks: a trimmed
    spherical-triangle patch whose two pole-tangent arcs are exact meridians, plus a
    dedicated `TriangulatePoleGrid` trimmed tier excluded from midpoint refinement).
    The shared corner machinery ✅ landed too (`SurfaceOffset`/`SurfaceCorner`/
    `ImplicitSurface`/`CarrierBody`), unblocking curved shelling, curved draft and
    variable-radius fillet BANDS. Remaining: the non-conic corner CURVE itself — two
    variable-radius bands meet in a quartic, so a sharp corner under a varying law is
    still refused. `SurfaceCorner.CornerPolicy.AllowTraced` returns one with its
    deviation reported, but nothing in the kernel opts in (design.md §5 records why);
    turning that into a *usable* corner needs the traced curve re-sampled at
    tessellation time against its two exact carriers, which is the same fix the baked
    tracer-polyline residual below wants.
  - [ ] **Partial-run follow-ups** — SETBACK terminations ✅ landed (`Filleting.
    FilletEdges/ChamferEdges` resolve contiguous partial runs; the termination is the
    planar band cross-section perpendicular to the terminal edge, the industry
    default; `Shape.FilletEdges/ChamferEdges` accept them). Remaining, refused by
    name: cliff and vertex-blend terminations (different surfaces), arc-terminal runs
    (the cylindrical neighbour needs periodic re-trimming), mid-EDGE stops (terminate
    at a parameter, not a vertex — needs an edge split first), and variable-setback
    laws on runs.
  - [ ] **Variable-radius fillet follow-ups** — variable-RADIUS fillets ✅ landed
    (`FilletRim`/`FilletEdges` law overloads + `Shape.Fillet(radiusAt, faces)`/
    `FilletEdges(radiusAt, edges)` + `VariableFilletRimFeature`, beside the
    variable-SETBACK chamfers): along a straight run the band is the ruled skin between
    its two end quarter arcs, whose intermediate sections are TRUE circles because equal
    weights make lerping points identical to lerping control points, and its top and
    bottom boundaries are `LoftRailCurve` rails on the band so the grid and the edge
    polylines sample the same points. **Variable laws on partial RUNS ✅ landed**
    (`Filleting.FilletEdges/ChamferEdges` law overloads now resolve runs through
    `RimSurgeon.OpenRun(topLaw, sideRatio)`; the terminations are exact at any law value
    — the band's end cross-section is a planar quarter arc of whatever radius the law
    gives at the stop vertex; kernel corpus member "variable fillet run", analytic
    volume (1 − π/4)·L·(r₀² + r₀r₁ + r₁²)/3 converging at ratio exactly 4.0). Remaining,
    refused by name: a varying radius across a SHARP corner (two variable bands are
    cones that do not circumscribe a common sphere, so they meet in a quartic — a
    constant law across such a corner still works, so the refusal is about the law, and
    on a RUN there is a third way out: stop the run before the corner), and a varying
    law along an ARC or on a full circular rim (a spiral).
  - [ ] **`Shape.FilletEdges(law, edges)`/`ChamferEdges(law, edges)` still resolve
    complete rims only** (Modeling): the scalar edge-set overloads pass `edgeSelector`
    through to `Filleting.FilletEdges(solid, edges, radius)` and so pick up partial
    runs, but the LAW overloads route via `Filleting.RimFacesFor`, which refuses a
    partial selection before the kernel's new law-run path is reached. The fix is the
    scalar overloads' shape: hand the edge selector to
    `Filleting.FilletEdges(solid, edges, law)` (which now resolves rims AND runs) —
    a few lines in `Shape.cs`/`RimShape`, out of the B-Rep agent's fence.
- [ ] **Traced-curve residuals after the band-crossing fix** (`SnapTracerEnds` ✅ landed —
  a traced polyline is extended onto the EXACT solution of E(t) = S(u, v) once, on the
  curve object both faces share, and `SplitByCurve`'s interior probe ✅ now takes an exact
  sample instead of a mid-chord midpoint; together they closed the whole-solid-fillet
  band case and, unexpectedly, cuts that break out through a face boundary part-way).
  What is left:
  - [ ] **A tool drilled ALONG a band's own axis** — its intersection with the band runs
    the band's whole LENGTH rather than crossing it. **The recorded symptom is stale**:
    the failure has MOVED from splitting to classification — it no longer throws `Open
    splitting curves must start and end outside the face` but
    `Could not find a probe point on a face fragment` from `BrepBoolean.ProbePoint`,
    which now names the fragment (an `ExtrudedSurface` band fragment, one loop, pulled
    uv u [3.14, 3.92] × v [0.25, 0.75] on the measured case — a lengthwise sliver strip
    whose 12×12 probe grid finds no contained sample). So the splitting half was fixed
    by the SnapTracerEnds era and the remaining defect is a probe/parity one on
    hair-thin lengthwise fragments (pinned by
    `WholeSolidFilletBooleanTests.ToolRunningAlongABandsAxis_StillRefusesLoudly`).
  - [ ] **Tracer polylines now refine at tessellation time — the HOLE-RIM and
    winding-loop half remains.** ✅ Landed: `PolylineCurve3d.Carriers` carries the two
    exact surfaces the tracer marched on (attached in `SurfaceIntersection.March`,
    preserved by `SnapTracerEnds`/`Simplified`/`GeometryTransform`, serialized as two
    optional trailing surface refs in `BrepArchive`), and `BRepTessellator.SampleEdge`
    refines each chord onto the exact intersection (`SurfaceCorner.TrySolvePoint`,
    minimum-norm Newton, weld-tier acceptance, inserts-only so baked vertices pass
    through bit-for-bit) until a chord subtends at most one natural angular step. The
    band-crossing bore went **0.9988/0.9460/0.3229 → 0.9988/0.9999/1.0000** worst
    facet-vs-surface agreement at 32/96/192 — the degradation with density is gone.
    **The outer-loop clause is ✅ gone — an OPEN traced branch refines whatever loop
    it bounds** — because the failure that forced it was misattributed: the recorded
    blame on `TriangulateBandWithHoles` was wrong (that tier is not even reached on the
    torus fixture); the refusal was `RowedPeriodicBand`'s own up-front u-monotonicity
    gate, which a bore crossing the band's edge always trips (the rim SCALLOPS the
    chain, turning back in u at its widest points), dropping exactly those faces to the
    merge walk whose fans refinement then exploded. The gate is relaxed — the
    chain-adjacent `StripBetween` already threads a scalloped chain (seam-split
    `RowedStrip`, with `SweepCycle` splitting each piece at its own u extremes so the
    rim's turn vertex becomes a split point, not a refusal) — and the
    torus-cut-with-a-bore member's worst 192/96 agreement went **0.0198 → 0.9601**
    (one alignment residual remains at 128/64 only, 0.0267, recorded in the test).
    What stays gated is the CLOSED branch — see the per-slab-rows item below.
  - [ ] **Per-slab interior rows in `ZipSlabs`, for the CLOSED-rim band-with-holes
    case.** The fixture that needs them: `Torus(12, 4) − Cylinder(1, 40).RotateX(π/2)
    .Translate((16, 0, 0))` — a radial bore notching the outer band, whose interior
    rim is a CLOSED tracer branch on a face with finite stepV (loops [192, 192, 74]);
    it refuses at 192/96 today ("curvature refinement did not converge") and is the
    reason `SampleEdge`'s gate keeps its `IsClosed: false` clause — refining that rim
    74 → 287 samples was MEASURED to buy nothing (same refusal, every density below
    already clean), so the gate boundary is evidence, not a deferral. A first build
    threaded `RowedStrip` per slab (lower chain + reversed upper as the cycle) and was
    REVERTED on two measurements: the free slabs' only anchors are the cut chords'
    four endpoints, so rows land on long off-level diagonals whose slivers FOLD
    (0/1 → 2/7 folds at 48/24 and 128/64 on the fixture, bit-identical to baseline
    with the rows disabled), and the hole-adjacent slabs' anchored paths cross the
    dense chain near their ends, failing the area guard by ~1e-4 relative — a REAL
    overlap, correctly rejected. The sound construction is `StripBetween`'s own: the
    cut chords (and seam chords) must be PRE-SPLIT at the natural levels with vertices
    shared VERBATIM by both neighbouring slabs — and all-or-nothing across the whole
    slab run, because one slab rowing against a split chord while its neighbour sweeps
    the unsplit one is a T-junction crack. Until then the tier keeps the plain sweep,
    which is exact for the ruled (stepV = ∞) cylinder bands that dominate it.
- [ ] **Draft follow-ups** (`Draft.Apply` landed with per-face angles in one call, wired
  as `Shape.Draft`; CURVED faces ✅ landed too — a face of revolution about the pull axis
  tapers by rotating its generator in its own half-plane, so a drafted cylinder is
  exactly a cone and a drafted torus band another torus band): curved faces on any OTHER
  axis (their drafted carrier is not a surface of any family this kernel builds); caps
  with holes; a non-planar neutral surface.
- [ ] **Shelling follow-ups** (`Shelling.Offset/Shell` landed with per-face wall
  thickness, wired as `Shape.Shell(t, openings)`; CURVED faces ✅ landed on the shared
  `CarrierBody` rebuild — a cylinder shells to a cup, a cone frustum to a conical cup, a
  sphere offsets to a sphere, a pipe elbow opened at both ends to a genus-1 tube):
  - [ ] **Carriers with no same-family offset** — swept and NURBS surfaces. A sweep's
    parallel surface is not a sweep, so this needs either a fitted offset (with the
    deviation reported, the `AllowTraced` shape of decision) or a new surface type.
  - [ ] **Non-circular curved edges** — the rim rebuild constructs a concentric circle
    and verifies it; a rim that is some other curve falls through to
    `SurfaceCorner.TrySolveCurve`'s exact tier and is refused when that has no analytic
    pair.
  - [ ] **A SEALED shell of a partial revolve** — moving the cap planes cuts the offset
    torus in a quartic rather than a circle, so the concentric hypothesis is genuinely
    false and the refusal is correct. Making it work needs the tier-(c) corner curve.
  - [ ] **Non-concurrent >3-valent vertices** — CONCURRENT ones ✅ landed: four or more
    faces at a vertex is over-determined in general, but a square pyramid's apex has four
    planes that meet in a point by symmetry and offsetting each keeps that true, so the
    case now goes through the least-squares corner solve and is CHECKED rather than
    refused wholesale. What is left is the genuinely non-concurrent corner, where the
    offset opens the vertex into a small FACE — corner-patch construction (the
    `FilletAllEdges` machinery), not a better solve.
  - [ ] **Adjacent openings** — their shared rim has zero width, so the two openings must
    MERGE into one rim loop: a topology pass, not new geometry. Attempted and deliberately
    NOT built during the curved-corner work, because the shape of the fix is not what the
    note above assumed: the two openings lie on different PLANES, so they cannot become one
    face. What is actually needed is for each opening's rim annulus to lose its zero-width
    stretch along the shared edge — the outer edge there IS the inner edge, since neither
    plane moved — which means re-tracing both rim loops rather than merging them. That is a
    loop-surgery pass of the same kind `FaceSplitter` does, and doing it half-way would
    leave a solid that validates and is wrong.
  - [ ] **Global self-intersection detection** — deliberately unchecked, as in OCCT and
    `OffsetCurve3d`.
  - [ ] **A REVERSED curved face (boolean output).** Curved OFFSET now accepts a reversed
    face (`CarrierBody.Recognize(solid, allowReversedFaces: true)`, `Lift` offsets it by
    `−distance`), but SHELL keeps the refusal because its cavity twin (`Flipped`) hard-codes
    `IsReversed = true` for the inner face — right for a forward parent, and it needs to be
    `!parent.IsReversed` for a reversed one. Making shell sense-aware is the same
    verification pass DRAFT's `Taper` (which reads the lean off the surface normal, not the
    outward one) also wants; neither is exercised by a `Shape`-level construction today.
  The `Shape` route exposes one thickness; per-face thickness and per-face draft angles
  stay kernel-level escape hatches (`Shape.From(...)`) until a selector-to-value
  vocabulary exists at the Shape level.
- [ ] Feature operations (`BRepFeat`): pocket, boss, rib, slot as first-class features
  with faces-to-remove semantics
- [ ] **Shape-healing follow-ups** (curved-edge RE-TRIMMING — FixGaps' parametric mode,
  opt-in `RetrimCurvedEdges` — and `RepairShells` — orientation flood + outward vote +
  shell repartition, on by default — ✅ landed on top of the original six passes):
  - [ ] **Curve re-FITTING for perpendicular gap residuals** — the re-trim removes the
    tangential part of a merge gap; what remains is the vertex's perpendicular distance
    to the curve, reported and never repaired. Closing it means deforming or re-fitting
    the curve (OCCT's geometric FixGaps mode inserts filler segments), a modelling
    operation this healing deliberately refuses; the report IS the per-edge tolerance
    story, chosen over per-entity tolerances every consumer would then have to honour.
  - [ ] **Surface-sampled orientation vote** — the global side of a component is voted
    from the fan volume of its sampled boundary loops, which is exact in sign for
    polyhedra and boolean-style trimmed faces but ~0 for components whose faces are all
    pole-bounded or closed bands (a two-face sphere, a two-band torus): those keep the
    authored side with a note only when the flood already flipped something. A
    surface-domain-sampled flux vote would extend coverage; needs care on trimmed faces
    whose grid covers more than the face.
  - [ ] **Per-face wire winding vs same-sense flag** (OCCT `ShapeFix_Face`) — the flood
    fixes RELATIVE face orientation and the vote the global side, but a single face
    whose wire is wound opposite to its own `IsReversed` flag is internally
    inconsistent in a way only `FaceGeometry.LoopSignedArea` can see.
- [ ] Local operations: split shape by shape, glue faces. Assessed (task #11): *split*
  is the boolean pipeline stopped after face splitting — imprint both solids against
  each other's intersection curves and return all fragments unclassified; the
  machinery exists (`FaceSplitter` + the boolean's per-pair loop), the work is an API
  that returns fragment→host provenance without sealing, which is the modification
  history item again. *Glue* (merge coincident faces of touching solids without a
  boolean) is `ShapeHealing.SewDuplicateEdges` generalized from edges to overlapping
  face REGIONS. The B-Rep boolean now HAS the planar half of that machinery
  (`CoplanarFaces` — same-plane recognition, area-overlap sampling, normal-agreement
  classification), so a planar *glue* is within reach; curved overlaps still are not.
- [ ] **Exact-surface mass-property quadrature** (OCCT `BRepGProp` + `GProp_Domain`) —
  mass properties ✅ landed by tessellate-then-sum with Richardson extrapolation (1.9e-7
  relative on a cylinder at default quality). Exact quadrature is worth doing only
  *after* trimmed parameter-space boundaries become exact, since the domain scan is the
  accuracy limit, not the quadrature. Would make analytic primitives exact rather than
  1e-7. **Premise re-verified (OCCT-parity pass): still holds** — trimmed boundaries
  remain chordal AT BOOLEAN TIME (tracer polylines), so an exact domain scan still has
  nothing exact to scan against; what changed is that traced curves now CARRY their
  exact carrier pair (`PolylineCurve3d.Carriers`), so a future exact quadrature could
  refine its own domain boundary against them the way the tessellator now does, rather
  than needing a new mechanism. The tessellate-then-Richardson route also improves for
  free wherever rims refine.
- [x] **`BrepSolid` one-call rigid transform** ✅ landed — `BrepSolid.Transformed(Matrix4d)`
  over the new `GeometryTransform` (per-type surface and curve mapping; the `Clone()` walk
  with geometry moved and provenance inherited). Exact for **proper rigid motions**, which is
  the whole tier: every parameterization here is built from lengths and angles, so an isometry
  lets edge trim domains, seam phases and revolve angles be carried VERBATIM (asserted
  bitwise) instead of re-derived. Curve objects are mapped once and reused so a shared carrier
  stays one object. Verified through the two consumers that can actually see a bad
  re-placement: a posed drilled plate re-tessellates closed at the same discrete volume AND
  survives a second boolean.
  **The filed assessment was wrong in one place and right in another.** It proposed
  "+uniform scale where the type allows" — but the type is not the only party that has to
  allow it: `PolylineCurve3d` is parameterized by cumulative chord length, so scaling its
  points scales its DOMAIN, while a `BrepEdge` stores its trim domain separately and a
  `CurveSegment` stores base parameters in the base's units. Scaling the curve alone
  desynchronizes them silently, and every tracer-produced edge is polyline-backed, so this is
  the common case. Uniform scale is therefore refused for a BOOKKEEPING reason rather than a
  geometric one, and the message says so. It was right that `StepWriter.Simplify` holds
  similar arithmetic — see the residual below.
  Residuals:
  - [ ] **Uniform scale**, per the above. The fix is not in `GeometryTransform` but in
    carrying the factor into every domain that refers to a polyline-backed curve (edge
    domains and `CurveSegment` base parameters). Worth doing when a consumer wants to scale
    an imported body; a design should scale through the `Shape` API, which bakes the factor
    into construction inputs.
  - [ ] **Reflection.** Refused because it is not merely a placement: it reverses orientation,
    so every loop needs re-winding to keep outward normals outward, and the handedness-carrying
    types each need their own rule — `HelicalSurface` (derived: the mirrored band is the same
    surface on frame (L·X, −L·Y, L·Z) with NEGATED pitch and the u parameter negated, so the
    u domain flips, which interacts with `IsFullHelicalBand` and `SampleEdge`'s angular rule)
    and `RevolvedSurface` (the recorded conjugation identity F·Rot(d,φ)·F = Rot(−F·d,φ), i.e.
    the negated transformed axis). `Shape.Mirror` already does all of this correctly one layer
    up by baking the reflection into construction inputs, so this is a convenience rather
    than a capability gap.
  - [ ] **`StepWriter.Simplify` still holds its own wrapper-folding** for Line/Circle/Ellipse/
    NURBS under a `TransformedCurve`. It was deliberately NOT merged into
    `GeometryTransform.Apply`: the two do different jobs (unwrap-and-simplify for export vs
    place), and — the load-bearing part — `Simplify` is handed whatever transform a wrapper
    carries, which may include a uniform scale, where its circle arm keeps `c.Radius` and
    passes scaled axes through. That is only correct for a rigid map, so it is a latent defect
    in `Simplify` rather than a rule to centralize as-is. Merging means first making the shared
    mapper similarity-correct (normalize the axes, scale the radius, branching only when the
    scale differs from 1 so the rigid path stays bit-identical) and then re-checking STEP
    output; worth doing, but as its own change with its own measurement.
- **Per-part material** ✅ landed (`Material`/`Materials`/`ModelUnits`/`PartColor` in
  `EngrCAD.Core`; `Part.Material`/`.Of()`, `Part.MassGrams`/`DisplayMassGrams`,
  `BomLine.Material`/`UnitMassGrams`, `DocumentEdits.SetMaterial`, document persistence,
  properties panel and MCP `describe_part`; docs `examples/materials.md`; design.md §2).
  **The unit is tonne/mm³** — one convention, stated once in `ModelUnits`, with kilograms
  and grams as accessors; the 1000× discrepancy between the FEA catalogue and
  `PartMassProperties`' old kg/mm³ remark is gone. **All four residuals have landed** —
  `HardwareComponent.Material`/`FastenerMaterials` (a BOM of bought-in parts weighs itself;
  the field carries the SUBSTANCE, not the ISO 898-1 property class, and the bearing
  deliberately states nothing because its v1 body omits balls and cage), `AnalysisBody`
  (one list drives `TetMesher.Mesh` and `StructuralModel.For`/`ThermalModel.For`, verified
  on an exactly-analytic series-stiffness bar at nu = 0), the properties-panel material
  dropdown over `ParamEditors.MaterialChoices` on the undo stack, and the fea docs +
  `BrepMassPropertiesTests` unit cross-references. See design.md §3c and §6b. One
  follow-up came out of it:
  - [ ] **Conforming multi-material interfaces in `TetMesher`.** v1 meshes DISJOINT bodies
    and now refuses two that MATE along a face, by name — the natural way to draw a
    bi-material part. Two things stand in the way and both are precisely located.
    (a) Their surfaces share vertices, so the input is not tetrahedralizable without a
    cross-body weld. (b) Welding alone would be *worse than the refusal*:
    `TetMesher.Builder.OffendingFaces` treats every inside-to-inside face as interior, so
    an inter-body face is never recovered onto the input plane and a tetrahedron
    straddling the interface takes ONE region for its whole volume — the material boundary
    becomes a jagged surface of the mesher's choosing rather than the plane that was
    drawn. The fix is to weld across bodies AND make the region-change face a recovery
    target (`label[n] == Inside && region[n] == region[t]` is the interior test, and both
    bodies' interface triangles are already in the same coplanar patch, so `TryOnSurface`
    would succeed). What needs deciding rather than coding: an interface facet is visited
    from BOTH sides, so it would appear twice in the boundary-facet list — a `Facets`
    selector naming it would double-count a pressure, and `TetFacet.SourceTriangle` would
    be ambiguous between two coincident input triangles. Until then, a bonded bi-material
    part is meshed as one surface with one material.
    <br>**The post-processing half is now ready for it**: stress recovery assembles its
    patches per material region and never fits across an interface, `AnalysisMesh` carries the
    `(node, region)` slot table, and `StructuralResults.NodalStressIn` gives the per-material
    value a shared node has two of (design.md §3i). So this item no longer has to carry that
    question too — what it still owns is the double-counting facet decision above.
- [ ] **Topological naming residuals** (v1 ✅ landed: `BrepFace.Provenance` +
  `Shape.Tag(name)` + `FaceSetRef.Tagged`/`Within`. Tags survive the whole boolean
  pipeline, `BrepSolid.Clone`, `Drill`, patterns and transforms; the failure is one-sided,
  so a lost tag means fewer faces and never a wrong one — see design.md §6b.
  **The five rebuild sites ✅ landed too**: `Draft` (planar `BuildPrism` + the curved
  carrier path), `CarrierBody.Rebuild`/`Shell`, `Shelling`'s polyhedral `Offset`/`Shell`,
  `Filleting`'s `FilletAllEdges` + rim surgery + `TrimNeighborBand`, and `ShapeHealing`'s
  `WorkFace`. Every one asks the existing `BrepFace.DescendsFrom` rather than restating a
  copy, and `FaceProvenanceTests` measures each by tagging ONE face and asserting where the
  tag landed. **The filed framing had one thing wrong and one thing missing**: the four
  named sites are really SIX derive points, because `Draft` and `Shelling` each have a
  planar path and a curved one and the curved halves share `CarrierBody` — so the shared
  rebuild is where two of the six live and neither `Draft.cs` nor `Shelling.cs` mentions
  provenance at all; and `Shelling`'s `Dictionary<BrepFace,int>` was never needed, since
  every one of these sites already iterates its parent array positionally and the index map
  would have been a second spelling of the loop counter. **EDGE provenance ✅ landed** as a
  DERIVED query — `BrepQueries.Provenance(edge)` / `DescendsFrom(edge, tag)` /
  `solid.EdgesTagged(tag)`, the UNION of the edge's two faces' tags (an edge is "of" a step
  whenever it touches a face of that step). The decision the note left open was settled as
  union by the motivating query: "the edges of the boss" wants the boss's BASE rim, which
  borders a boss face and a non-boss one, and an intersection would drop exactly it. No new
  store (walked on demand from `edge.Uses` → `Loop.Face` → `Provenance`), the same one-sided
  safety inherited (a step that tagged no face contributes no edge).) What remains:
  - [ ] **A tag cannot be attached to an existing `Part`'s geometry after the fact**, only
    written into the graph. A UI that lets a user click a face and name it would need a
    tag-by-selection form, which is a different (and much weaker) guarantee — the tag would
    then be pinned to whatever the query matched at that moment.
- [ ] STEP follow-up residuals (unit scaling, CONICAL/TOROIDAL_SURFACE synthesis, exact
  `TransformedCurve(NurbsCurve)` export, PARABOLA/HYPERBOLA/OFFSET_CURVE_3D mappings and
  `Parabola3d.ToNurbs()` all ✅ landed): import bisections run a fixed 100 iterations
  (exact but wasteful, import-time only); imported PARABOLA/HYPERBOLA consumed OUTSIDE
  an edge (an offset basis, a revolve generator) carry a ±1000 placeholder domain since
  only edge vertices fix the real interval; plane-angle CONVERSION_BASED_UNITs (degree
  files) are not read — sound today because the reader reads no angular quantities, but
  a future entity that does must check; the closed-generator two-rim trim
  disambiguation reads OUR outward-band sense convention, so a foreign face whose wire
  winding contradicts its own same-sense flag could still pick the wrong half (per-face
  winding validation is ShapeFix_Face territory, not started)
- [ ] Data exchange follow-ups (glTF, native BREP and IGES import all ✅ landed; the
  original task-#11 assessment is kept below with each verdict recorded against it).
  **IGES** ✅ **import landed** (`IgesReader` + internal `IgesParser`, docs
  `examples/import.md`), export **filed and refused** — the assessment's "do not write
  it" holds: a writer would be a second, lossier encoding of geometry STEP already
  carries better. Landed entities: 110, 100, 104, 126, 128, 102, 116, 108, 118, 120,
  122, 124, 142, 144. **Residuals, in rough value order**: (a) **186 MSBO + 502 vertex
  list / 504 edge list / 508 loop / 510 face / 514 shell** — the one path that yields a
  SEWN solid rather than a face soup, but it is a second parallel topology encoding
  inside the same format, about as large as the rest of the reader, and it buys
  correctness `ShapeHealing` already supplies; (b) 402 associativity / 308+408
  subfigure instances, i.e. the nearest thing IGES has to assembly structure — worth it
  only if a real file turns up needing it; (c) 106 copious data (the polyline family),
  cheap and common in 2D-ish files; (d) 116-point and loose-curve output is returned but
  nothing consumes it through the `Shape` API yet; (e) the 142 parameter-space curve is
  discarded, since the topology has no pcurve slot — if one is ever added (a real
  change, blast radius across `FaceSplitter`/`BrepArchive`/`StepWriter`/the
  tessellator), IGES is a consumer waiting for it; (f) entity 314 colour and 406
  properties are skipped silently rather than carried onto `Part.Color`.
  **glTF** ✅ **landed** (`GltfWriter` in EngrCAD.Mesh + `GltfScene` in Viewer.Core;
  `.glb`/`.gltf`, `--export` and MCP wiring, docs `examples/exports.md`) — and it went
  further than this assessment expected: glTF has real hierarchy, so it preserves the
  assembly tree with one mesh per distinct `Part` rather than flattening to
  `PartInstance`s. Residual glTF follow-ups: no texture/UV support (nothing produces
  UVs yet); `KHR_materials_*` extensions unused (a metalness/roughness pair per part is
  all `Part` carries); the flat render mesh triples the vertex count against a
  shared-vertex mesh, so a `KHR_draco_mesh_compression` or an indexed-with-smoothing-
  groups path is the size lever if files ever get big; and a deformed `FieldDisplay`
  exports undeformed by design (see the reasoning in `GltfScene`) — a glTF morph target
  is the honest way to carry it, since a target's weight is exactly the exaggeration
  factor the file currently has nowhere to record.
  **Native BREP serialization** ✅ **landed** (`BrepArchive` in EngrCAD.BRep, `.ecb`,
  `--export` wiring, docs `examples/exports.md`, decision recorded in design.md §5). The
  measured need was the second one this assessment named: every modelled thread carries a
  `HelicalSurface`, which STEP has no entity for, so a threaded part had no lossless file
  representation at all. Versioned from day one, unknown versions refused by name. Format
  residuals: no compression (a busy solid is a few hundred KB of text — fine today, and
  the honest lever if it ever is not is gzip around the same bytes rather than a binary
  encoding, since that keeps the diffability the format exists for); no scene/document
  content by design (that is the OCAF envelope item below, which should REFERENCE `.ecb`
  files rather than embed geometry); the reader does not tolerate forward references, so
  a hand-reordered file is refused rather than sorted; and the `Diagnostics` list only
  ever carries unknown-header warnings today — there is no partial-read mode, which is
  the right call for a native format and the wrong one for an import format.
- [ ] **HLR / drawing follow-ups** (v1 ✅ landed — `HiddenLineRemoval` classifying exact
  B-Rep feature edges against the display mesh, `DrawingSheet`/`DrawingView` with
  third/first-angle standard layouts and section views, `SheetAnnotation` dimensions,
  SVG/DXF sheet export; `docs/examples/drawings.md`):
  - [ ] **Exact HLR** (OCCT `HLRBRep`): project edges AND true silhouette curves and
    classify algebraically against every face, instead of ray-casting against the
    display mesh. Blocked on the B-Rep silhouette item in the OpenSCAD section — with
    exact silhouette curves the rest is the same splitting machinery the boolean
    already has. The seam is right today (a list of classified 2D polylines), so this
    is a swap behind `HiddenLineRemoval.Project`, not a rewrite of the sheet layer.
  - [ ] **Auto-dimensioning**: a first pass placing the obvious dimensions (overall
    extents per view, hole diameters and their bolt-circle or grid spacing) from the
    graph's own `DrillShape`/`LocationSet` nodes, the way `HoleTable.For(part)` already
    reads them. Explicit placement stays the contract; this is a starting point, not a
    replacement.
  - [ ] **BOM-linked balloons**: `SheetBalloon` draws a circled string today, and `Bom`
    already numbers distinct parts — connecting them means picking a leader anchor per
    occurrence from the projection (a visible point on that instance's line work) and
    emitting a parts-list table beside the title block.
  - [ ] **More sheet standards** (the shared `DrawingFrame` landed with the ISO 5457 zone
    grid + centring marks and the B-series/ANSI paper table; these three remain). The
    third/first-angle projection SYMBOL as geometry rather than the words the title block
    prints today. An **ISO 7200 field layout** — a full new `TitleBlockLayout` beside the
    engineering and schematic ones (its own cell arrangement; wants a datasheet to get the
    exact fields right, which is why it was filed rather than half-built), which is what the
    `TitleBlock.Project`/`Sheet` fields already carry data for. **Exact per-size ISO 5457
    zone COUNTS**: `FrameStandards` derives the column/row count from a nominal field size,
    where ISO 5457 fixes a specific count per sheet size in a small table — transcribe it
    (verify-against-datasheet), keeping the nominal-size path as the fallback for a custom
    sheet.
  - [ ] **Detail views** (a scaled-up circle of a region) and **broken views** (a long
    part with its middle removed). Both are clipping problems on top of the existing
    view, not new projections.
  - [ ] **Cut-plane indication**: a section view is drawn correctly but nothing marks
    WHERE it was cut on its parent view — the chain-dashed cutting line with its arrows
    and letters. Needs a view-to-view reference, which the sheet model does not have
    yet.
  - [ ] **Corner resolution**: within one bias step of a model vertex the local-surface
    read picks up the far side's faces, so a hidden run stops that far short of its
    corner (measured: three junctions on a box cost 0.037 of an analytic 57.155, i.e.
    one bias each). Absorbed by the minimum-run rule and far below drawing resolution,
    but an exact HLR would not have it at all.
- [ ] **Document framework residuals** (the OCAF assessment's verdict held: do NOT port
  OCAF's label-tree/attribute model; `Scene`/`Tab`/`Part` with `FeatureHistory` as the
  parametric core is the document, and one versioned envelope is the persistence. Landed:
  `Document`/`DocumentFile.cs` — scene structure + per-part feature history + assemblies
  and poses + mates + annotations + results, with snapshots for geometry that has no
  recipe, warnings-not-exceptions on load, and a byte-identical save→load→save fixed
  point; and `DocumentEdits.cs`/`UndoStack.cs` — reversible edits with grouping, the
  serializer as the undo test oracle, and refused edits leaving the document untouched.
  Note the one place the assessment was overruled by building it: undo stores EDITS, not
  document snapshots, because a `Scene` snapshot is not a cheap value — design.md §6b).
  Open:
  - [ ] **`Shape`-graph serialization** would let a code-built `Shape` part save as a
    recipe instead of a snapshot, and would make `BooleanFeature` and `ComponentFeature`
    round-trip through `FeatureHistory` as well. It is the single biggest remaining gap in
    what a document can carry, and it is a real project: the graph has ~40 node types,
    several carrying sketches, fonts, hole specs, catalogue objects and lambdas.
  - [ ] **External geometry references.** Snapshots are embedded on purpose (design.md
    §6b). The case that would justify a `{"kind": "external", "path": ...}` record is a
    scan mesh too large to inline; it needs a resolver hook, path resolution relative to
    the document, and a missing-file policy, so it waits for a real need.
  - [ ] **Selector-backed annotations do not round-trip.** `LinearDimension.BetweenFaces`
    and `RadialDimension.OnEdge` take `Func<BrepSolid, …>` lambdas, so they save as opaque
    markers. The fix is not more serialization machinery but the vocabulary that already
    exists: overloads taking `FaceRef`/`EdgeSetRef`, whose `Descriptor` is the serialized
    form. Small, and it would make dimensions as persistent as features already are.
  - [ ] **Mechanisms in the document envelope.** The mechanism layer itself now
    round-trips (`Mechanism.SaveMechanism`/`LoadMechanism` — joints with saved
    reference directions and unwrap state, couplings by factory args, cam laws per
    `Feature.SaveInputs`, save→load→save a byte fixed point), but a `Document` still
    has no mechanism list to carry one in: a `Mechanism` OWNS its `MateSet` where a
    `Document` owns loose `MateSet`s, so wiring it in means deciding whether a
    document-carried mechanism replaces one of the document's mate sets or sits
    beside them (and what `reload` does to it). The persistence layer is done; the
    open question is the document's ownership model.
  - [ ] **The viewer's undo wiring wants a manual pass.** The stack, the edits and the
    grouping are covered headlessly, and the two edit paths the window offers (the tree's
    suppress toggle, the properties panel's `[Param]` fields) are routed through it — but
    the Ctrl+Z/Ctrl+Y handler and the toolbar buttons themselves are only exercised by
    running the app, since synthetic input does not reach Avalonia's keyboard stack the way
    `SendInput` reaches its pointer stack. Same caveat the RPC window wiring carries.
  - [ ] **The rollback bar is not undoable.** It suppresses a run of features and keeps its
    own per-part bookkeeping of which ones IT suppressed, so folding it into the stack means
    an edit that captures that bookkeeping too — a `CompoundEdit` of `Suppress` edits plus
    the marker state. Worth doing when the bar next gets attention.
  - [ ] **Undo does not reach every mutation yet.** The `DocumentEdits` vocabulary covers
    what a UI performs today; the remaining gap is deliberate rather than forgotten —
    (add/remove a whole `Part` or `Tab` ✅ landed as
    `AddPart`/`RemovePart`/`AddTab`/`RemoveTab`, with the filed occurrence decision
    settled as a REFUSAL: a placed part refuses by name listing every occurrence path,
    the cascade being a UI's `Group` of occurrence removals) and `Part.Results`/`FieldDisplay`. None is hard; each
    is a decision about scope rather than about mechanism. (`MateSet.Solve` as one
    undoable step ✅ landed as `DocumentEdits.SolveMates` — and the filed "wants the
    solver to report which frames it wrote" dissolved: capturing every frame in the
    assembly TREE is the safe superset, an untouched frame restoring to itself
    exactly, so no solver report was needed.)

## build123d / CadQuery parity (open items)

- [ ] **Weldment follow-ups** (`Weldment`/`FrameProfile` ✅ landed — skeleton runs,
  exact bisector-plane miters via overlong-extrude + on-plane box tools, butt joints,
  `Part.CutLength` → `BomLine.CutLength` cut lists, prism-cut-identity verification,
  coped saddles refused with the tracer reason; design.md §6b). Open, in rough order
  of value:
  - [ ] **Multi-member joints** (three members at a corner): which pair miters and
    what closes the third is a real drafting convention, not a formula — SolidWorks
    asks per-joint trim order. Wants a per-joint override vocabulary, not a guess.
  - [ ] **Coped (saddle) tube joints** stay refused pending the tracer's thread-scale
    seeding limits (the recorded M8 measurement); if `SurfaceIntersection` ever gains
    an analytic or robust cylinder∩cylinder tier at these aspect ratios, the refusal
    on `FrameJointStyle.Cope` names exactly what to relax.
  - [ ] **Corner reliefs / end caps / gussets**, curved members, and profile
    placement offsets (locate the run on a section corner or face rather than the
    factory datum) — all future work, none blocked.

Both are **OCCT front ends**, so unlike the OpenSCAD and OCCT sections above this one is
almost entirely about **API design, not kernel capability** — their contribution is how a
model is *expressed*, and the underlying operations are ones we largely have. Read them
for ergonomics, and copy capability rather than syntax: CadQuery's stringly-typed
selectors (`">Z"`, `"|Z and >Y"`) are the part to learn from and *not* imitate, because
`BrepQueries` + LINQ gives the same power type-safely. Landed from this section:
`BrepSelection` (the ordering/grouping layer + GeometryRef spellings), `LocationSet`,
`ExtrudeUntil`/`CutUntil`, `Packing`, frames & weldments (`Weldment`, docs
`examples/frames.md`), and the builder-form prototype whose verdict (an
honest no) is recorded in design.md §6b with the comparison committed as
`BuilderPrototypeTests`.

- [ ] **Selection-layer follow-ups**: `BrepSelection.Area`'s curved-face quadrature is
  ordering-grade (~1–2%, midpoint grid gated by pulled trim loops) — an exact-surface
  or adaptive tier is possible if a consumer ever needs measurement-grade face areas
  (today that is `BrepMassProperties`' job); `GroupByCoplanar` could gain a
  direction-filtered variant; `SurfaceKind` has no Helical member (helical faces
  report `Swept`).
- [ ] **Location-set follow-ups**: locations are 2D-in-plane by design — a full 3D
  `Frame3d`-list variant (build123d's arbitrary `Locations`) would serve component
  placement on non-parallel faces; `Shape.Pattern` could take a per-location scale law
  the way `LoftAlong` takes laws.
- [ ] **ExtrudeUntil follow-ups**: the resolution is eager (the `Bounds`/`Resized`
  policy) — an `UntilFeature` wrapper would re-measure per regeneration; a conforming
  end face (extrude-until-curved-face, what real CAD does with a trimmed end cap) needs
  kernel work: split the prism's side walls against the target and cap with the
  target's own surface patch.
- [ ] **Packing follow-ups** — 90° rotation ✅ and true-outline nesting ✅ landed
  (`PackOptions.Rotation`/`Nesting`, both opt-in and bit-identical by default; the
  outline path grows each silhouette by half the gap through `Region2dOffset` and
  searches a conservative raster bottom-left-first; free rotation refused by name, with
  the reason, in design.md §6b). What remains:
  - **Multi-plate overflow** instead of the loud refusal — `Pack` returns one
    `PackLayout`, so this is an API shape question first (a `PackLayout` list, or a
    plate index on each placement) and only then a loop.
  - **The search order is the ceiling on outline nesting, not the geometry.**
    Bottom-left-first structurally prefers "beside" to "inside" whenever there is room
    beside, so a roomy plate reproduces row packing (measured: six L brackets on a
    140-wide plate came out 78.97 deep against the shelf packer's 77.00, purely
    quantization; the same six on an 86-wide plate go 132.0 → 108.9). A candidate score
    that reads the resulting layout DEPTH, or true no-fit-polygon candidate positions,
    would nest on a roomy plate too — the raster and the clearance oracle are already
    there, so this is a scoring change rather than new geometry.
  - **The raster cannot take a zero-slack fit** (four 40 × 10 bars spanning exactly 50
    on a 50 mm plate): a mask's width always rounds up to a whole cell, so no
    resolution reaches it. Pinned by test from both sides; an exact slide-left/slide-
    down refinement after the raster finds a cell would close it.
  - **Free rotation** proper — a no-fit polygon per part pair per angle, or a
    deterministic optimiser over the angle with a stated stopping rule (`DesignStudy`'s
    Hooke–Jeeves is the precedent for the stopping rule bounding the ANSWER).
## Viewer

- [ ] **3D-annotation (PMI) residuals** (angular dimensions incl. `BetweenFaces`
  included-angle measurement, chain/ordinate styles, multi-line stroke-font layout
  with callout continuation lines, `ToleranceSpec` text sugar, `HoleTable` +
  `HoleAnnotations.AutoAttach`, pickable annotations, and **occlusion-aware
  rendering** ✅ landed):
  - **Annotation editing from the viewport** (picking ✅ — selection reports the
    text; dragging a picked dimension's offset would be the next affordance).
  - True leader-less ordinate dimensioning (datum zero point + aligned coordinate
    text per hole, no dimension lines) — `LinearDimension.Ordinate` is the
    baseline/running style.
  - Annotation persistence (JSON alongside `FeatureHistory.SaveParameters`) and
    STEP AP242 PMI export (far future).
  - **Dashed hidden annotation line work** — residual of the occlusion pass, which
    dims instead. Filed with its measurement rather than as a preference: a
    screen-space stipple keyed on `gl_FragCoord` (the shape the backlog originally
    proposed) is CONSTANT along some screen direction, so a line parallel to it comes
    out solid or vanishes entirely — there is no orientation-free fragment form. A
    real dash needs an along-the-line coordinate, and the cheap place for it is the
    shared `AnnotationGeometry.Build`, which already rebuilds per camera and already
    measures everything in screen pixels: chop each hidden-side segment into dashes
    there and no shader, attribute or upload plumbing changes in any of the three
    front ends. It would apply to the LINE WORK list only (the text list is exempt for
    the reason the value/pointer split exists at all). Worth doing only if dimming
    proves too weak on light part colours; `HiddenColor` is currently chosen against
    the mid-tone palette and stated so.
  - **Occlusion-aware hover/pick** is deliberately NOT open: nothing is hidden, so
    depth-blind picking stays correct. It only becomes a question if a future mode
    DROPS hidden stretches rather than dimming them, and that mode should not exist —
    a dimension you cannot see is a dimension you cannot check.
- [ ] **Construction-tree residuals** (rollback marker + suppress-from-tree +
  `[Param]` properties-panel editing + preview-restore-by-path + **typed editors**
  ✅ landed — `ParamEditors.KindFor` in Viewer.Core decides checkbox / enum dropdown /
  bounded slider / text from metadata the registry already carried, every editor still
  writing through the one JSON seam, the slider committing on release because each
  application regenerates and is an undo step): the rollback marker is click-to-place
  rather than a literal drag (drag-and-drop in the tree panel would need Avalonia
  pointer capture plumbing for marginal gain). Residual: the browser properties panel is
  read-only, so `ParamEditors` has one consumer — the rule is shared the moment the
  second one wants it.
- [ ] **Ambient-occlusion bake cost — three levers examined, two declined, don't redo
  them.** The bake was 12.3 s on the demo scene and already saturates every core.
  (a) **An any-hit early-out does not exist here**: occlusion accumulates as `1 − t`, so
  it is a NEAREST-hit query and a boolean test is a different renderer (measured 0.055
  darker over the occluded vertices; pinned by
  `Occlusion_AttenuatesWithDISTANCE_NotJustHitOrMiss`). (b) **Nearer-child-first traversal
  landed** — exact, bit-identical, but worth only **1.19× on a gyroid lattice and 1.04×
  (nothing) on a smooth blob**, because an escaping ray never sets the pruning bound below
  1 and most rays escape on ordinary CAD parts. (c) **Fewer rays changes renders**: 16 → 8
  is 1.6–1.9× and moves 59 of the 87 docs PNGs. What is left, in rough order of promise:
  - [ ] **Bake at fewer sample points and interpolate** — the cost is linear in vertex
    GROUPS, and a flat render mesh has one group per position per smoothing group, which
    on a tessellated curve is far more resolution than a half-strength vertex signal
    needs. Merging near-coplanar groups within a distance tier would cut the ray count
    without touching the ray's own cost. Changes output; needs the PNG oracle.
  - [ ] **A cheaper hemisphere for the common case**: a first pass of 4 rays that returns
    exactly 1.0 (fully open) could skip the remaining 12 — but only if "4 rays escaped"
    implies the other 12 do, which it does not, so this needs a conservative bound
    (e.g. an unoccluded cone) rather than a sample count.
  - [ ] The **80k-triangle opt-out** is still a cliff: a part just over it renders flat
    while its neighbour just under it does not. A budget expressed in rays × expected
    per-ray cost would degrade instead of cutting off.
- [ ] **Matcap follow-ups** (the analytic matcap ✅ landed — `ShadingStyle` Lit/Clay/
  Metal behind the `uMatcap` selector in `ViewerShaders.MeshFragment`, driven by the
  toolbar Shading dropdown / `EngrCadOptions.Shading` / `RenderToImage(shading:)` /
  MCP `screenshot`'s `shading` / `EngrCadViewport.Shading`, default byte-identical
  by the docs-PNG oracle): texture-based CUSTOM matcaps stay declined until a color
  image reader lands for other reasons (a texture must reach three front ends);
  more built-in looks (pearl, toon-step) are one lobe set each in the one shader
  file if anyone asks; per-part override stays deliberately not offered (a scene
  lit two ways reads as a rendering bug).

## Blazor web viewer

Reimplement the viewer for the web: a Blazor front end rendering EngrCAD scenes in the
browser. Opens the door to sharing designs by URL, embedding live models in the docs
site, and eventually a hosted modeling experience. The kernel is pure .NET with no
UI dependencies, which makes this unusually feasible.

- [x] **Architecture decided and prototyped: Blazor WebAssembly, kernel in the
  browser.** `src/EngrCAD.Web` (Razor component library) + `samples/EngrCAD.WebDemo`.
  The three risks the decision hung on are now measured, not guessed:
  - **The kernel compiles to WASM unmodified and returns identical geometry** — a flange
    with a 6-hole bolt circle and a filleted rim gave 1 560 triangles, closed, volume
    41 573.0 in headless Edge, matching the desktop run. No WASM-specific code path,
    and no `ArrayPool`/`stackalloc`/`Vector<double>` trouble.
  - **Speed is a constant factor, not a wall**: desktop 88.7 ms total; WASM without AOT
    1 677.3 ms (18.9×); WASM with AOT 385.2 ms (4.3×). All three from clean publishes,
    interleaved into one measurement window.
  - **Payload**: 1.9 MB brotli, or 4.6 MB with AOT. All nine EngrCAD assemblies are
    1.14 MB uncompressed / 0.41 MB brotli, so our own code is about a fifth of the
    download and the runtime is the rest.
- [x] **Docs site hosts the live demo** — `docs/examples/web.md` embeds it in an iframe
  and `.github/workflows/docs.yml` publishes the app into `_site/live/`. The app is
  **path-portable**: `<base href="./" />` plus the already-relative asset references the
  build emits mean no `StaticWebAssetBasePath`, no post-publish rewrite and no repository
  name in the artifact. `?embed` strips the page furniture for the iframe. The page's
  headline geometry (1 560 triangles, closed, 41 573.0 mm³) is pinned by a `run:` snippet
  so the docs build fails if the kernel ever disagrees with what the page claims.
- [ ] **AOT for the docs deployment is deliberately OFF, and worth revisiting** — it is
  4.4× faster for 2.4× the download. Declined for now because AOT compilation adds
  minutes to every docs deploy and the embedded demo rebuilds only on slider release;
  revisit once the WebGL viewer lands and the page becomes something you orbit rather
  than something you rebuild. Still missing a **time-to-first-render** number —
  everything measured so far is compute-only, and the 4.6 MB download is exactly the part
  that number would price.
- [ ] **A published-artifact smoke test would be worth having.** An incremental publish
  can ship a runtime that disagrees with the assemblies: it builds clean, runs ~1.6×
  slow, then aborts with `MONO interpreter: NIY encountered in ...:.cctor ()`. A clean
  publish fixes it and CI is immune (fresh checkout), so the workflow only asserts
  `index.html` exists. A headless-browser check that the published app actually *computes*
  would close the gap — windows runners have Edge — but it can flake, and a flaky check
  blocking docs deploys is its own cost. The verification recipe is in
  `src/EngrCAD.Web/README.md`.
- [x] **Scene-to-frame layer** — `ViewportFrame.Build(...)` is a **pure function** from
  instances + camera to a `FrameDescription`, which is what lets draw order, clear
  colour, furniture ranges and per-instance matrices be asserted as values instead of
  compared by eye (`EngrCAD.Web.Tests`). The window and offscreen passes drifted
  precisely because pixels were the only way to compare them.
- [x] **The orbit-camera component** — `EngrCadViewport.razor`'s pointer/wheel handlers
  call `CameraMath.DragOrbit`/`DragPan`/`DragZoom`/`WheelZoom`, which were moved OUT of
  `ViewportControl` into `EngrCAD.Viewer.Core` (along with `CameraState`,
  `PitchLimit`, `KeyStep`) so both front ends share one implementation; the desktop now
  delegates, every constant preserved verbatim and locked by `OrbitCameraTests`.
  Verified drawing by canvas readback: 33 912 lit pixels, and 111 481 changed after an
  orbit. Headless WebGL2 works under `--disable-gpu`, which had been an open risk.
- [x] **Shared render model, step 1** — the UI-free half of `RenderCore.cs` is now
  `src/EngrCAD.Viewer.Core` (no Avalonia, no Silk.NET; a scratch blazorwasm app builds
  and publishes against it). `ViewStyle`, `SectionPlane`/`SectionAxis`/`SectionCombine`/
  `SectionClip`, `RenderModes`/`EffectiveMode`, `ViewerShaders`, `CameraMath` and
  `RenderGeometry`'s pure half are public there, namespace unchanged. Verified by the
  50 docs PNGs being byte-identical, which is the oracle that actually constrains a
  render refactor.
- [x] **Shared render model, step 2** — `TabMeshLoader` + `MeshFlavor`, `ViewCubeMath`/
  `ViewCubeAnimation` + a new `ViewCubeGeometry` (face table, fill/edge/label builders,
  palette, hover rule), `StrokeFont`, `AnnotationItem`/`AnnotationCamera`/
  `AnnotationGeometry` (+ the overlay colour) and `SectionContours`/
  `SectionContourGeometry` (+ the three isoline family colours) are all public in
  `EngrCAD.Viewer.Core` now, namespace unchanged; the GL halves (`ViewCube`,
  `AnnotationLayer`, `SectionContourRenderer`) stayed behind and consume them.
  (`HoverThrottle` and `WireframeEdges` had already moved with earlier rungs.) Oracle:
  full suite green and all 53 rendered docs PNGs byte-identical. Note `TabMeshLoader`
  is Avalonia-free but **thread-model-bound** — the browser keeps its own
  single-threaded loader by design (EngrCAD.Web README).
- [x] **The per-part upload description** — `PartUpload` + `PartUploads.Build(part,
  request)` + `PartUploadRequest` in `EngrCAD.Viewer.Core`: the render mesh, the
  `FieldRendering.TryBuild` result (and its error), the occlusion array, the feature-edge
  and wireframe segments and the pick BVH, built once for the window, the offscreen pass
  and the browser. The caller states which pieces it wants and where occlusion comes from
  (a delegate — never-bake cache read in the window, bake-inline offscreen, none in the
  browser); the cache and every GL call stay with each front end. The payoff was the
  content rules, above all **"a deformed part gets NO edge overlay at any factor"**, which
  had been written out three times. Oracle: all 108 committed docs PNGs byte-identical.
- [ ] **The full `ViewerModel` abstraction (Scene→render-instances shared by Avalonia,
  offscreen AND the web client)** — assessed twice and still deliberately NOT forced.
  Everything the three front ends genuinely share is now extracted (frame values, camera,
  modes, pick, widget geometry, and — as of the item above — the per-part upload value);
  what remains different is the *lifecycle*: the window streams uploads per part through
  `TabMeshLoader` (two threads), the offscreen pass is one-shot and synchronous, and the
  browser interleaves awaited JS uploads on one thread. A shared ViewerModel would have to
  abstract exactly that lifecycle, which is the part that must NOT look the same (the
  TabMeshLoader lesson). Nothing is blocked on it.
- [ ] **`EngrCAD.Viewer.Core` pulls the whole kernel**, because `RenderModes.Resolve` is
  written against `EngrCAD.Modeling.DisplayMode`. Right for kernel-in-the-browser; if a
  shaders-only consumer ever appears, the fix is a Viewer.Core-local display-mode enum —
  an API change, not a move.
- [ ] **Feature parity ladder** (build in this order): ~~orbit/pan/zoom camera + shaded
  mesh rendering~~ ✅ → ~~feature edges~~ ✅ → ~~display modes + the global view style~~ ✅
  → ~~tab strip + model tree + visibility~~ ✅ → ~~picking~~ ✅ → ~~section planes +
  their SDF isolines~~ ✅ → ~~view cube~~ ✅ → ~~annotations~~ ✅ → ~~properties panel +
  BOM~~ ✅ (the int-uniform prerequisite landed as the `IntUniform`/`Vec4ArrayUniform`
  typed markers in `engrcad-gl.js` — the JS dispatches on marker shape, C# decides
  which uniforms are which) → ~~the measure tool~~ ✅ → ~~exploded views + animation
  playback~~ ✅ (both are `ViewportFrame.PoseByPath`, a pure function matching by
  occurrence path; the transport is `AnimationPlayback` from Viewer.Core with a timer
  and three widgets here) → ~~multi-plane section planes + combine, through to picking~~
  ✅ → ~~debug-modifier parity (`DebugFilter.Shown` in `ResolveInstances`)~~ ✅.
  → ~~multi-plane isolines (`SectionClip.Siblings` per plane)~~ ✅ (per-plane
  contour builds with the plane carried on each `ViewportContours`, sibling-clipped
  under Union exactly as the desktop renderer; single-plane output bit-identical;
  `SetSectionPlanesAsync` is the direct-call path and the `?report` beacon carries
  the quarter-cut relationships `bodySectioned < bodyQuarter < bodyWhole` and
  `0 < goldQuarter < goldContours + goldContoursY`).
  **Remaining rungs**: construction-tree rows + rollback previews (needs
  `ConstructionPreviewCache`'s background-lowering story rethought for one thread);
  a multi-plane section **UI** (the plane list and combine are plumbed end to end now —
  what is missing is a browser toolbar affordance for building the set; the desktop
  toolbar has a plane-count cycler, so this is a real parity gap, not a shared one);
  and the `?report` self-check should grow pose/measure relationships (the pose seam
  is unit tested, but "the canvas changed when the slider moved" is the check this
  front end's culture asks for).
- [x] **Docs-site embedding, the general form — landed, and by an option this entry did
  not consider.** Every example screenshot whose snippet can run in a browser now carries a
  **Run it in your browser** button that swaps the picture for the kernel building that
  model in the reader's tab (`tools/EngrCAD.DocsGen/LiveExamples.cs`,
  `src/EngrCAD.Web/LiveExample.cs`, `docs/site/src/live-examples.mjs`, the demo's
  `?example=<id>`; design.md §8c).
  **The filed recommendation was (c), emit each scene as a document, and the reason it was
  chosen was wrong about (b).** (b) was rejected as "shipping Roslyn to WASM" — but the
  compile does not have to happen in the browser: the docs build already compiles every
  snippet, so it emits the compiled ASSEMBLY and the browser only loads it. That costs
  **6.0 KB mean per example** (max 12.0 KB, 710 KB for all 118), no compiler in the
  payload, and it keeps the thing (c) gives up: a page's interactive block IS its code
  fence, run, rather than a second representation of the same model. Two prices, both paid
  in full: the browser must reflect over Roslyn's script-submission layout (pinned by a
  round-trip test rather than trusted), and only the examples that compile against the
  browser's own assembly set are offered — which turns out to be the feature's best
  property, since the refusal is the compiler's and cannot go stale against a payload
  change. 118 of 132.
- [ ] **The 14 examples that do not run, each with what it would take.** They are named in
  `docs/examples/live-examples.json` with the refusing tool's own words; none is a defect.
  - **Seven FEA figures** need `EngrCAD.Fea` in the browser payload. Measure that first —
    it is not obviously small, and the snippets themselves are the heaviest in the docs
    (tet meshing plus a solve), so at ~19× they may be minutes rather than seconds. If it
    lands, the honest version states the wait before the click.
  - **Two `text.md` figures** load a system font off the build machine. The fix is to ship
    one font as an app asset and give the docs a `Fonts` global the browser build can also
    supply — which is really "the docs harness needs a globals type both sides can see",
    the same question `Scratch` raises.
  - **`import-drilled`** uses `Scratch`. It would work as-is on the browser's in-memory
    filesystem (it writes an STL and reads it straight back), so this one is purely the
    globals question above.
  - **`construction-preview`** needs `ConstructionPreviewRequest`, which lives in
    `EngrCAD.Viewer` rather than `Viewer.Core`. Moving it down is the same
    shared-render-model step the camera and the cube already took, and the browser has no
    construction-tree rows to preview from yet anyway (the parity rung above).
- [ ] **The browser's per-draw GL state is never reset, and only one of its consequences
  has been found.** The depth-clear defect (Web README, "The number that went stale") came
  from `engrcad-gl.js` applying each draw's state and leaving it set, so the NEXT frame
  inherits whatever the last draw wanted. `depthMask` was the one that erased the model,
  because it also gates `glClear`; the other per-draw settings — `blend`, `cull`,
  `polygonOffset`, `depthFunc`, the viewport rect — cannot affect a clear, so nothing else
  is currently wrong, and that is a property of what the frame builder happens to emit
  rather than an invariant. The cheap version is to reset the whole block at the top of
  `drawFrame` (one state-setting pass per frame, not per draw) so a frame's appearance can
  never depend on the previous frame's last draw; the honest question first is whether any
  of the remaining settings can reach anything but a draw, since a reset that fixes nothing
  is state nobody can test. Worth doing WITH a measurement either way (the beacon's fields
  are the instrument and were bit-identical across the depth fix, so a no-op is provable).
- [ ] **The Run button quotes no cost.** A reader clicking one has no idea whether it is a
  half-second or ten. DocsGen already times each snippet's desktop execution and could carry
  a projected browser figure, but the honest number is time-to-first-FRAME (meshing
  included, which the snippet's own execution does not cover) and that is not measured on
  the desktop side today. Measure it in the browser instead — the `?example=<id>&report`
  beacon already reports `total` — and bake a band rather than a number.
- [ ] **Out of scope until later**: editing/sketching in the browser, collaboration,
  server-side model storage. This is a *viewer* first.

## MCP server / remote control of the viewer

The **headless server ✅ landed** (`src/EngrCAD.Mcp`: `EngrCadMcp.Run` + `--mcp` over
stdio — list/describe/screenshot/export/reload, PNG returned as an MCP image block,
stdout guarded, geometry evaluated lazily), and so have **write tools**
(`set_param`/`suppress_feature`/`unsuppress_feature` over `Part.History` +
`Part.Regenerate`), **screenshot's full option surface** (up to 4 section planes +
combine, explicit camera, export sizes), **structured content** (output schemas +
`structuredContent` on every JSON tool), the **StandardViews deletion** (poses route
through `ViewCubeMath`/`CameraMath`; `NamedViews` is only the name table), the
**forced no-GL error path** (`ENGRCAD_NO_GL`), and the **live-viewer RPC, option (b)**
(`RemoteControl.cs` in EngrCAD.Viewer: loopback-only newline JSON-RPC behind
`WithRemoteControl`/`--rpc`, token optional; `--mcp --viewer <port>` bridges
set_view/fit/set_section/set_display_mode/set_view_style/select_part/get_selection/
measure/viewer_screenshot; every mutation marshals through `Dispatcher.UIThread`, GL
only via `SaveScreenshot`'s capture-on-next-frame), **document persistence**
(`save_document`/`load_document` over the `Document` envelope — a session's edits now
survive it, reopening parametric, with snapshot parts named rather than silently
flattened; a loaded document is an overlay `reload` still discards) and the
**`screenshot` `t` parameter** (posed through the shared `EngrCad.PoseAt`). Remaining:

- [ ] **Untested**: a real third-party MCP client (Claude Desktop/Code) connecting —
  the protocol was driven by hand and via the SDK's own client.
- [x] **The windowed RPC path is covered** — `ViewportRemoteViewerTests` (headless,
  against a REAL `ViewportControl`) plus `WindowedRpcTests` (a live window, opt-in via
  `ENGRCAD_WINDOWED_TESTS=1`). The filed reason for it being untestable was wrong; see
  CLAUDE.md's status paragraph for what a `ViewportControl` does without a window.
- [x] **A client that connects the instant the port is announced sees NO parts** —
  resolved as the filed entry's FIRST shape: `ping` carries **`ready`**
  (`ViewportControl.InstancesDisplayed` — true once the render pass has adopted the
  instance swap and nothing newer is queued), and a part-not-found refusal during the
  gap says "poll ping until ready" instead of reading as "this model has no parts".
  Holding the port announcement until the first frame was rejected for the entry's own
  reason (a window that never renders would never announce), and reporting the pending
  list for its own (it would desynchronize the paths from the indices `select_part`/
  `set_display_mode` address). The windowed test now polls `ready` and then reads the
  list ONCE, with no blind retry; the headless half (a control that never renders IS
  the gap held open) is pinned in `ViewportRemoteViewerTests`. Honest scope: under
  lazy tab meshing the list is a growing prefix, so `ready` means "what is drawn",
  not "the document is fully loaded".
- [x] **`viewer_screenshot` returns pixels** — `ViewportControl.CaptureScreenshotAsync`
  is `SaveScreenshot` with a `TaskCompletionSource`; `ViewportRemoteViewer` awaits it
  OFF the UI thread under a 10 s deadline (blocking the dispatcher is how the frame would
  fail to arrive), the RPC result carries `written: true`, and `ViewerTools.Screenshot`
  reads the file and returns an MCP image block exactly as headless `screenshot` does —
  legitimate because the endpoint is loopback-only.
  **One detail of the filed diagnosis was wrong and is worth keeping.** It said the
  completion must fire "from inside the render pass"; that is too EARLY. `glReadPixels`
  runs there, but the encode and the write are deliberately off-thread, so a task
  completed at that point hands back a path to a file that does not exist yet. It fires
  from the write, immediately after `File.WriteAllBytes`. The entry's *conclusion* — not
  the `Status` callback — was right, but for a stronger reason than the one given: `Status`
  is a BROADCAST carrying prose for successes and failures alike, so a listener cannot
  tell its own capture from the toolbar button's or from a second concurrent request's.
  Synchronising on a string is not synchronisation.
  It also claimed this "cannot be tested by the headless socket harness", which held only
  because the write was tangled with the GL call: splitting `WriteCapture` out (pixels in,
  no context) makes the ORDER assertable — resume on the completion, assert the file is
  already there — and the bridge's image block, the timeout refusal and the
  unreadable-file case are all covered over real sockets with a stub. **Its closing claim
  has since been disproved too** — it named two remaining legs and said the second could
  not be automated because `ViewportRemoteViewer` "takes a concrete `ViewportControl`, so
  there is nothing to substitute". Nothing needs substituting: a `ViewportControl`
  constructs with no Avalonia application, no window and no GL context, and one that will
  never be rendered IS the deadline's fixture. Only the FIRST leg (a real render pass
  reaching the claim, under a live dispatcher) needs a window.
- [ ] **Option (c) — viewer hosts MCP directly over HTTP+SSE** stays parked unless the
  bridge process proves annoying in practice.
- [x] **`viewer_screenshot` reaches an animation's instant** — through the
  `set_animation_time` verb the entry predicted (park the transport, pause, then capture),
  verified against a real window by `WindowedRpcTests`. Rationale in CLAUDE.md and the two
  READMEs.
- [ ] **A narrower `save_parameters` tool** (writing only
  `FeatureHistory.SaveParameters` for one part) is the smaller sibling of the
  `save_document`/`load_document` pair that landed. Worth adding only if a client turns
  up that wants to diff one part's numbers rather than reopen a model; the document pair
  covers the "hand the tuning back" case that motivated it.
  (Packaging is settled: `src/EngrCAD.Mcp` is its own package on
  `ModelContextProtocol.Core`, so viewer and kernel consumers inherit nothing.)

## App layer / infrastructure

- [ ] **Docs-site follow-ups** (the DocFX → Astro Starlight migration landed 2026-08-03;
  Starlight hosts the landing page, getting started, writing-examples, the 51 example
  pages and the embedded live WASM demo, DocFX is reduced to the .NET API reference at
  `/api/`). Four residuals, each a stated boundary rather than something discovered later:
  - **`/api/` still wears DocFX's own theme, navigation and search.** It is a
    self-contained subtree, so a reader crossing into it changes visual gear entirely.
    The two candidate fixes are very different sizes: retheming DocFX's `modern` template
    to match Starlight's tokens is cosmetic and reversible; generating the reference INTO
    Starlight (a loader over DocFX's `mref` YAML, or over the XML doc comments directly)
    is a real project and the only route to one search index and one sidebar. Nothing
    should be attempted here until someone has decided which of the two is wanted, because
    the cheap one forecloses nothing and the expensive one replaces it.
  - **Search is split in two** — Pagefind indexes the 54 guide pages, DocFX's own index
    the 770 API pages, and neither knows about the other. This is the same finding as the
    row above from the reader's side, filed separately because it is the symptom a user
    reports.
  - **The sitemap is only correct in CI.** `site` is passed from
    `actions/configure-pages`, so a local `npm run build` skips sitemap generation with a
    warning. Harmless (a local build is a preview), but it means the sitemap is one of the
    few things the local build does not exercise.
  - **No versioned docs, i18n, edit-this-page links or last-updated stamps.** Starlight
    offers all four; none is wired, because each wants a decision (which versions? which
    branch does "edit" target?) rather than a default.
- [ ] **Design-study follow-ups** (`DesignStudy.cs` landed — see design.md §6b). Four
  residuals, each a stated v1 boundary rather than a gap discovered later:
  - **A dense deterministic direction set (OrthoMADS).** The poll is
    `{±e_i} ∪ {±e_i ± e_j}` with the step RATIO adapted one axis at a time, which is
    enough to slide along the constraint boundaries measured so far (a two-variable beam
    reaches its analytic optimum where a shared step stopped at 21.92 of a possible 25),
    but the set is finite, so a boundary whose slope never lands between two reachable
    ones can still stop the search short. OrthoMADS is the textbook answer and its Halton
    direction generator is deterministic, so it costs no randomness — but it replaces the
    poll-size/mesh-size stopping rule, and `StudyResult.OptimumTolerance`'s per-axis claim
    would have to be restated in MADS' terms rather than merely re-tuned.
  - **Discrete (`int`) variables** — a pattern count, a tooth number. Refused by name
    today. The step may not halve below one and the convergence criterion means something
    different (the answer is exact once the step reaches 1), so it is a second stopping
    rule beside the continuous one, not a cast.
  - **Memoization of repeated design points.** A pattern search revisits points and
    `FeatureHistory`'s prefix cache does not help (it holds one entry per feature index,
    overwritten each regeneration). Deliberately absent so the trajectory is EXACTLY the
    list of evaluations performed, which is what makes the determinism comparison mean
    what it says — a memo has to be recorded as a distinct trajectory outcome or the
    property is lost.
  - **A shared expensive analysis between the objective and the constraints.** Today the
    contract is that the objective is measured FIRST at every point, so a caller can run
    one solve there and let the constraints read a captured local; a first-class
    "evaluate once, report several numbers" seam would be better but wants a design
    (per-evaluation scratch keyed on what?) rather than a bag.
- [ ] **Configuration follow-ups** (`Configurations.cs` landed: `Configuration` /
  `ConfigurationSet` / `ConfigurationResult` on `Part.Configurations`, values through the
  `SaveParameters` seam, `DocumentEdits.SetConfiguration`/`Add`/`Remove`,
  document persistence with the active name round-tripping and the load NOT re-applying,
  `Bom.ByConfiguration`; docs `examples/configurations.md`, design.md §6b). Four residuals:
  - **Per-configuration SUPPRESSION** — "the variant without the boss", and the most-wanted
    thing v1 does not do. It is deliberately absent rather than missed: suppression is not
    part of the `SaveParameters` vocabulary, so it would arrive as a second field beside the
    parameter object with its own capture, compare, round-trip and staleness rules, which is
    the drift the one-seam rule exists to prevent. The shape a v2 would take is known and
    small — suppression is ALREADY in the regeneration cache key and already round-trips
    through `SaveHistory`, so it is a `suppressed: ["boss"]` array on `Configuration` plus
    four call sites (`Capture`, `Matches`, `Activate`, `Validate`), not new machinery — but
    it needs the decision recorded about whether a partial set that omits the array means
    "leave suppression alone" (it must) and whether `ActiveIsModified` reads it.
  - **A configuration cannot span PARTS.** The entry's "one `FeatureHistory`" is honoured
    literally, so an assembly-level configuration ("the metric build") that drives several
    parts at once has no spelling. It is not a bigger version of this: a document-level set
    is keyed by (part, feature, param) rather than (feature, param), which means deciding
    what happens when a member part is removed or renamed, and whether a part's own active
    configuration is overridden or composed. SolidWorks has both and they mean different
    things; pick deliberately rather than generalizing the type.
  - **No host surface yet.** No MCP tool (`list_configurations`/`set_configuration` are the
    obvious pair and the seam is already `Part.Configurations`), and no viewer control — the
    natural place is a dropdown beside the properties panel's `[Param]` editors, writing
    through `DocumentEdits.SetConfiguration` so it is one Ctrl+Z and never a second way to
    apply a value (the material dropdown's precedent, including its `republish` question:
    a configuration DOES move geometry, so unlike a material it must republish).
  - **`DocumentEdit` has no channel for warnings**, so `SetConfiguration` drops the
    `LoadParameters` messages a stale configuration produces; the documented answer is to
    call `ConfigurationSet.Validate()` first or `Activate` directly. That is honest and a
    little thin — every other edit here either cannot warn or throws — and the general
    question (should a `DocumentEdit` be able to report non-fatal findings?) is worth
    settling once for the whole vocabulary rather than for this one edit.
- [ ] **Tamper-mesh follow-ups** (`TamperMesh.cs` + `TiledHilbertRoute.cs` landed: an
  anti-drill conductive serpentine over a rectangular wall, N interleaved nets, and a
  `DrillGuarantee` that is derived AND measured by certified branch and bound; docs
  `examples/tamper-mesh.md`, design.md §6b). Five residuals, roughly in the order they pay:
  - **A wall that is not a rectangle is refused outright**, because the route would break
    into runs and a broken net cannot be monitored for continuity. The common real cases are
    a connector cutout and a rounded corner, and both want the same thing: a Hamiltonian path
    over an ARBITRARY set of lattice cells rather than a rectangle. That is a real algorithm
    (a Hamiltonian path in a grid subgraph is NP-hard in general but polynomial for
    solid/simply-connected grid graphs — Umans–Lenhart), and it should return the honest
    refusal when the cell set has no path (a checkerboard-parity obstruction is the usual
    cause and is cheap to detect first). Until then `SpaceFillingInfill` is the answer where
    runs are acceptable.
  - **The terminals are wherever the snake ends**, which is on the footprint boundary in
    every case but only at the two ends of ONE edge for a single row of blocks or an even
    number of rows. A caller who wants both terminals at a STATED edge (where the connector
    is) has no way to ask. The block snake could run in columns instead of rows, or reverse,
    which covers the four edges — a small routing choice, not a new construction.
  - **`Guarantee` measures over the whole footprint, including its own corners**, which for
    two or more nets is always the weakest point — so the reported number is dominated by the
    boundary rather than by the pattern. Honest, and documented with the "make the footprint
    overhang what you protect" remedy, but an `interior` variant (the same branch and bound
    over an inset rectangle) would let a designer see both numbers instead of inferring one.
  - **`IsolationGap` is derived, not measured.** It is `min(pitch)/nets − width`, which is
    exactly right for the offset construction and is cross-checked by a test that measures
    the closest approach between two nets' centrelines — but if a future net layout stops
    being a uniform offset family, the derived number would silently stop being the measured
    one. The measurement exists in the test; moving it onto the type is the change.
  - **No electrical model at all**: no resistance, no trace-length matching between nets, no
    via/terminal pads, no impedance. Deliberate — this is a geometry kernel — but a caller
    sizing a monitor wants `Length × sheet resistance` at least, which is arithmetic over
    numbers the layout already reports.
- [ ] **Manufacturability follow-ups** (`Manufacturability.cs` landed: draft / overhangs /
  wall thickness, each a report plus a `MeshField` the existing `FieldDisplay` colours;
  docs `examples/manufacturability.md`). Four residuals, in the order they would pay:
  - **Undercut detection** — the real complement of the draft check, and stated as a
    non-goal in its API docs rather than implied away: a face can have ample local draft
    and still be shadowed by material above it, so no rigid pull frees it. That is a
    VISIBILITY question along ±pull (a ray per surface sample against the body, or a
    depth-buffer sweep) rather than a normal question, and it is what turns "these faces
    have too little draft" into "this part cannot be moulded in two halves". Verification:
    a re-entrant groove in an otherwise well-drafted block is invisible to `CheckDraft`
    today and must be reported by this; a plain drafted block must still report none.
  - **A medial-axis (inscribed-ball) thickness estimator beside the ray cast.** The
    shipped estimator measures ALONG THE SURFACE NORMAL and is exact against planar
    opposites, which is the right answer for plates, ribs and webs and the wrong one at a
    fillet or an inside corner, where the largest inscribed ball is smaller. The ball is
    a bracketed search on `Part.TryGetSdf()` — walk inward along −n̂ for the largest t
    with `|d(p − t·n̂)| = t`, thickness `2t` — and it has a nice conservative property
    (a CSG difference SDF is a correct-sign LOWER bound, so it under-reports). It also
    has two real costs to weigh first: a lowering that can fail where the mesh route
    never does, and a root condition that is a degenerate INTERVAL rather than a crossing
    on the exact case (a slab satisfies it for every t up to T/2), so it is a
    largest-t-with-f≈0 search and needs its own tolerance argument. Offer it as a named
    `ThicknessEstimator`, never as a silent upgrade — two estimators answering one
    question must both be nameable.
  - **A per-FACET field spelling.** `MeshField` is per-vertex, so a facet quantity is
    published as the worst incident reading and bleeds one ring into the neighbouring
    face; a large planar face with no interior vertices is interpolated from its corners,
    which makes a correct face look implicated (measured on the docs housing figure).
    Cell association is already a documented gap on `MeshField`; a draft/overhang plot is
    the first consumer with a real need for it.
  - **A build PLATE for the overhang check.** A face resting on the bed is currently
    reported like any other ceiling, because the check knows the build DIRECTION and not
    where the plate is. Adding a plate is one plane plus a "within one layer of it" test
    — but it changes the reported area, so it is opt-in and stated, not a default.
- [ ] **ISO 286 follow-ups** (v1 ✅ landed — `Iso286`/`FitLimits`/`IsoFit`/`FitKind` +
  `ToleranceStackup`, docs `examples/fits.md`: IT5–IT12 × 13 ranges and shaft letters
  d–p + hole H transcribed in the standard's own µm, the kind DERIVED from the
  clearance extremes, worst-case + RSS with asymmetric bands re-centred on their mids):
  - [ ] **Letters a–c and r–z, and shaft-basis holes.** The large-clearance and
    heavier-interference letters split their fundamental deviations at sub-range
    boundaries (c at 40, 65, 100…; r/s/t likewise), so their table is roughly double
    the d–p one and was deferred rather than half-transcribed — H11/c11 and H7/s6 are
    preferred fits, so this is the first row to add. Shaft-basis holes (F8 and
    friends) are the same rows applied with the opposite sign convention.
  - [ ] **A stackup derived from the model.** The v1 finding, recorded in the class
    doc: mates constrain POSES and carry no toleranced dimensions, so a chain walked
    off the mate graph would be a guess about intent — deriving one needs a dimension
    SCHEME attached to the model (the drawing layer's dimensions are the natural
    source), which is a design question about where tolerances live, not a walk.

- [ ] **Parametric features follow-ups** (`FeatureHistory` landed; typed geometry
  inputs landed — `GeometryRefs.cs`: `PlaneRef`/`FaceRef`/`FaceSetRef`/`EdgeSetRef`/
  `AxisRef` with cardinality in the type, descriptor-as-cache-key-as-serialized-form,
  per-`Apply` resolution, and `ValidateInputs` naming the failing property; feature
  registry + whole-history JSON landed — `FeatureRegistry` with instance-free
  `[Param]` metadata and honest `CanCreate`/`Reason`, `SaveHistory`/`LoadHistory`
  with exact sketch/hole-spec/component constructor-input serialization via
  `Feature.SaveInputs`, nullable `[Param]` values, and a coverage test enumerating the
  sketch segment types so the writer cannot fall behind the reader again)
  — property-panel UI editing of `[Param]`s driven by the registry's metadata (free-text
  through the JSON seam landed; typed editors are the polish pass), feature list in the
  viewer model tree with registry-backed INSERTION (`DocumentEdits.AddFeature` is the
  undoable half; the catalogue dialog is not built), and **a `Shape`-graph serialization,
  which is what remains of the "code inputs" item** — it would unlock `BooleanFeature`,
  and by extension `ComponentAssembly(name, shape)`, whose base body is a lambda over an
  arbitrary `Shape` and therefore the one opaque record a fastener-bearing host still
  carries. (`ComponentFeature` itself now round-trips, by KIND plus factory arguments —
  the designation was assessed and rejected as the key, being lossy in exactly the fields
  a reload would get wrong.) Persistent topological
  IDs are no longer open in the abstract: `Shape.Tag` + `BrepFace.Provenance` landed, and
  what remains is the per-algorithm inheritance filed under "Topological naming residuals".
- [ ] **An OPTIONAL-numeric parameter editor** (Viewer / Viewer.Core, small). Nullable
  `[Param]` values landed and the rule that follows them is that a parameter whose editor
  cannot express absence keeps a sentinel instead — because `ParamEditors.KindFor` offers a
  slider exactly when the range is finite at both ends, and a slider is a total function
  onto its range. `EdgeFlangeFeature.KFactor` is the case: it stays `double` with 0 meaning
  "inherit" purely because its editor cannot say "unset". A third `ParamEditorKind`
  (a clear/inherit affordance beside the slider — a checkbox, or a "—" button that writes
  `null` through the same JSON seam) would let it become `double?` like its two neighbours
  and would remove the only asymmetry in that feature's API. `ParamEditors.Position`
  already returns 0 for a null value, so the panel does not crash today; it simply reads
  the minimum, which is the misleading part.
- [ ] **Geometry-reference vocabulary follow-ups.** Landed: `PlaneRef.Offset(distance)`
  and `PlaneRef.Rotated(degrees, inPlaneAxis)` (resolve the base, then move — so a
  derived plane re-finds its base per regeneration; axes carried verbatim, rotation axis
  in the base's own coordinates, exact-zero returns the base itself),
  `FaceSetRef.LargestByArea`/`SmallestByArea` over `BrepSelection.Area`,
  `Touching(point)` (carrier projection THEN the face's trim test, so a point over a bore
  matches nothing), `AdjacentTo(set)`, `CylindricalBetween(min, max)`, the edge RANGES
  (`EdgeSetRef.CircularBetween(min, max)` and `LongerThan`/`ShorterThan`/`Between` over
  `BrepQueries.Length`, with an open-ended range taking its own `lengthAtLeast(n)` term
  rather than an infinite bound the shared number lexer cannot read), and the `Shape`
  overloads (`Fillet`/`Chamfer`/`ChamferAtAngle`/`FilletEdges`/`ChamferEdges` in constant
  and variable-law forms take `FaceSetRef`/`EdgeSetRef`). Remaining:
  - **`Shell` cannot take one without a source break** — its `openings` parameter is a
    *nullable* `Func`, so a reference-typed overload makes the existing `Shell(t, null)`
    ambiguous at every call site (seven sites in the repo, four of them writing
    `openings: null`, which a named argument does NOT disambiguate when both overloads
    name the parameter the same). Three routes, none free: rename the reference-typed
    entry (`ShellOpening(...)`); leave callers on `openings.AsSelector("openings")`, which
    is what the doc comment now says; or give `FaceSetRef` an implicit conversion to
    `Func<BrepSolid, IEnumerable<BrepFace>>` — which would need no new overload anywhere,
    would not disturb the existing `FaceSetRef` overloads (an identity conversion still
    wins overload resolution), and costs the input NAME in the failure message, which is
    the whole reason `AsSelector` takes one. Worth a decision the next time someone writes
    the awkward call. `Draft`'s per-face predicate has the same shape.
  - **An exact-LENGTH edge query is deliberately absent** and should stay absent unless the
    measure improves: `BrepQueries.Length` is exact for lines and circular arcs and a
    64-chord polyline otherwise, so a value comparison at the weld tier would be a
    correct-looking question with a wrong answer on any traced or NURBS edge. The range
    filters are honest about being filters; an exact query would not be.
  - **A `VertexRef`** — assessed, and it is not the trivial fifth member it looks like.
    The other four resolve to things the kernel already treats as objects (a face, an
    edge, a frame); a vertex's USES are a *point* (anchor a dimension, seed
    `Touching`, place a pattern) and the natural spellings — "the corner between these
    three faces", "the highest vertex of this face", "the ends of this edge" — are all
    derived rather than stored, so the type's real content is the query set, not the
    resolution. It also needs a cardinality decision the others did not: "the corner
    where these faces meet" is exactly-one while "this face's corners" is a set, so it
    wants BOTH a `VertexRef` and a `VertexSetRef` or an honest reason it does not.
    Worth doing when a consumer exists (a vertex-anchored dimension is the likeliest);
    inventing it before then would fix the query set by guesswork.
- [ ] **Assemblies follow-ups** (v2 landed: BOM, exploded views, mates — now ACROSS
  assembly levels with typed `FaceRef`/`AxisRef` references and
  `SaveMates`/`LoadMates` persistence — STEP assembly export + import, tree
  expand/collapse, retro-assigned palette colors) — true GPU instanced drawing (matrix
  buffer, one draw per part), per-instance color/display-mode overrides, an
  **explode-path renderer** (the dashed leader lines drafting standards draw between an
  exploded part and its seat — and `Occurrence.ExplodePath` now gives it a real path to
  draw rather than a straight line to assume), and **flexible sub-assemblies**: a deep
  mate target inside a multiply-placed sub-assembly is refused today because its
  internal frame is one shared object. Mechanisms (above) can now assume cross-level
  mates exist: a linkage whose members are sub-assemblies is jointable via occurrence
  paths.
  **Flexible sub-assemblies, assessed — this is the big one, and it is a DOCUMENT-MODEL
  change rather than a solver change.** The refusal is honest and structural:
  `Occurrence.SubAssembly` points at a shared `Assembly` object, and that assembly's own
  occurrences carry `Frame3d`s, so two placements of one sub-assembly necessarily agree
  about every internal pose. Onshape's answer is per-instance internal state, and the
  seam it has to attach to here is `Assembly.Flatten` — the ONE walk every consumer sees
  (viewers, exporters, BOM, mates, mechanisms, animation). Three candidate shapes, with
  what each costs:
  (a) **Deep-copy on flexibility** — mark an occurrence flexible and clone its
  sub-assembly. Simplest, and wrong: it breaks part IDENTITY (`Scene.AllParts` dedupes by
  reference, so a cloned subtree would mesh and upload twice) and the BOM would
  double-count.
  (b) **A per-occurrence frame OVERLAY** — `Occurrence.Overrides: Dictionary<string,
  Frame3d>` keyed by the relative occurrence path, consulted by `FlattenInto` as it
  descends. Preserves identity, is additive to the format, and is a few lines in the
  walk. The cost lands on everything that WRITES a frame: the mate solver's variables are
  occurrence frames, so solving inside a flexible instance must write to the overlay
  rather than to the shared occurrence, which means `MateSolver` needs to address "the
  frame of THIS placement of that occurrence" — a path, not an object. That is the real
  work, and it is the same change `MateRef` would need.
  (c) **Instance-level document objects** (a first-class `AssemblyInstance` with its own
  occurrence list) — the most general and the most disruptive; it changes what an
  assembly IS.
  Recommendation: **(b)**, and only when a real model needs it. Roughly two to three days
  with the mate-solver addressing change, and the test that matters is not "it moves" but
  that two placements of one sub-assembly can hold DIFFERENT internal poses while still
  sharing one `Part`, one mesh and one BOM line.
- [ ] **Standard component library — remaining fidelity** (breadth landed: ISO 7380
  button, ISO 10642 csk, ISO 4032 nuts, ISO 7089 washers, 60x deep groove bearings,
  the opt-in exact hex socket on `CapScrew`, and `PlaceThrough(..., anchorInto:)`
  anchoring into a placed insert or nut with engagement/thread/point checks).
  Remaining: a modeled thread on the shank via `Shape.ExternalThread`; knurled/flanged
  inserts; ISO 2338's crowned pin ends; **hex sockets for csk heads** — the head top is
  planar but the primitive rebuild puts cone and shank tangent along a shared rim, so
  it needs either tangent-union support in the exact boolean or planar ⊥-axis caps on
  full-turn revolves (the cap is a `RevolvedSurface` with a pole today, which is also
  why the socketed cap screw is rebuilt from cylinder primitives); washer/nut stack
  seating (a screw seated ON a placed washer rather than on the face); bearing shaft
  seats (`PrepareAnchor` cutting the shaft's seat when bearings join a stack).
- [ ] **Frame3d enabled next steps** (the `TopPlane` behaviour question is settled: both
  conventions are now expressible — `PlaneRef.TopPlane` keeps world (0,0,z) origins,
  `PlaneRef.OnTopFace` gives the face's own frame — so it is a per-feature choice rather
  than a global decision; `StepWriter` AXIS2-via-`Frame3d` landed — a `Placement(Frame3d)`
  overload mirrors `StepReader.Axis2`/`FromZX`, and the matrix path now REFUSES mirrored
  (improper) placements by name, which passed the orthonormality guard and would have
  silently re-posed un-mirrored on read-back) — arbitrary section planes from a frame;
  Part poses as frames (assemblies above).
- [ ] **Parametric model layer follow-ups** (`.csx` scripting landed —
  `tools/EngrCAD.Script` runs a script through `EngrCad.Run` with save-to-reload via
  the new `EngrCad.NotifySourceChanged()`, DocsGen's snippet contract and Roslyn seam,
  docs page + `samples/scripts/bracket.csx`; the reusable-component pattern is
  documented as plain C# methods returning Shape/Part) — remaining: a fluent C#
  builder over the retained document model; `#load` library conventions for shared
  `.csx` component files; a `dotnet tool` packaging of the script runner so
  `engrcad model.csx` works without the repo.
- [ ] **Bounded planar carriers clip; unbounded ones only clip to the query region.**
  Two `PlaneSurface` faces meeting at an angle produce a line spanning the whole
  region, so a boss's wall imprints its footprint edge ACROSS the host's entire top
  face instead of just the shared rim — topologically fine (the splitter keeps only
  the interior stretches and every fragment classifies correctly), but a flush union
  leaves the host's top face in more pieces than it needs. `SolidFactory.MakeBox`
  builds `PlaneSurface` faces; sketch extrusions already get the bounded-parallelogram
  treatment. Options: give `MakeBox` bounded carriers, or pass `Intersect` a per-pair
  region (the two faces' bounds, expanded) instead of the whole-model one — the latter
  touches every pair in the boolean pipeline, so it needs the corpus gate behind it.
- [ ] **Logging follow-ups** (`ILogger` adoption ✅; kernel extension ✅ landed —
  Interop and BRep take the abstractions reference, weighed per project: optional
  trailing `ILogger` on `BrepBoolean` ops (event 80, sub-steps threaded through),
  `BRepTessellator.Tessellate` (81), `MeshSdf` ctors incl. the winding build (82),
  `StepReader.Read/ReadFile` (90); Mesh/Core/Implicit deliberately stay
  dependency-free since everything named is reachable at those seams; results stay
  return values) — remaining: thread a logger from `Shape` lowering
  (`ShapeCompiler`/`Part.TryGetSolid`) down to these seams so a design program's
  logger sees its own booleans; consider `SurfaceNets.Polygonize` and `MeshRepair`
  timing at the same standard; a `--log-kernel` switch on `EngrCad.Run` wiring the
  host console logger into the kernel seams.
- [ ] nuget.org publish — pack VERIFIED solution-wide at 0.1.0 (12 packages, zero
  warnings; every src project has a Description and a packaged README;
  `RepositoryType` added). Remaining, all Chris's to confirm: the placeholder
  `RepositoryUrl`/`PackageProjectUrl` (`example.invalid` — a real remote exists at
  github.com/veggielane/EngrCAD) and the MIT license choice, then the actual push.
  GitHub Pages needs Settings → Pages → Source: GitHub Actions enabled once, then a
  push deploys the docs site.

## Future work (whole domains, not scheduled)

- [ ] **GPU acceleration — for modelling AND simulation, and the contract question comes
  before the kernel question.** The repo's strongest correctness tool is bit-identity
  (batch SDF == scalar to the bit, deterministic assembly order, PNG byte-comparison),
  and a GPU cannot honour it: fused multiply-adds, vendor transcendentals and
  nondeterministic reduction order all move the last bits, and `Vector.Cos` differing
  by 1 ulp was already enough to keep the trig kernels scalar. So every GPU path is
  **opt-in with a stated deviation bound, never a silent default** — the "a silently
  divergent fast path is worse than none" rule is the whole design constraint, and the
  first deliverable is the seam that makes an honest A/B possible (upload boundary =
  the existing SoA batch seam; results compared against the CPU path with the bound
  ASSERTED, not assumed).
  - **Dependency shape**: kernel projects stay free of rendering/GPU references — a new
    leaf (`EngrCAD.Gpu`) implements existing seams (`Sdf` batch evaluation, an
    `IProjectionTarget`, a CG matvec provider) the way the viewer already consumes
    kernels, injected by the caller rather than referenced by Core/Implicit/Fea. The
    compute stack should ride what is already shipped (Silk.NET / ANGLE ES 3.1 compute
    or a thin D3D/Vulkan binding), measured before chosen; consumer GPUs run fp64 at
    1/32–1/64 rate, so fp32-with-refinement vs native fp64 is a MEASUREMENT, not a
    preference.
  - **Modelling candidates, ranked by contract compatibility**: (1) the ambient-
    occlusion bake — embarrassingly parallel hemisphere rays, 12.3 s CPU on the demo
    scene, and its output is *shading*, where a bounded deviation is survivable (though
    the committed PNGs move, so it lands as an opt-in like `Scheduling`); (2) dense/
    NarrowBand `Sampled` grid bakes through the batch seam — but re-measure first:
    SurfaceNets is now ASSEMBLY-bound, not evaluation-bound (the cull lesson: an 8×
    saving in the dominant cost bought 2.5× because the dominant cost stopped being
    dominant); (3) expression-tree→compute-shader SDF compilation, which CLAUDE.md's
    implicit roadmap already names. Mesh booleans and the exact predicates are
    OFF-LIMITS — classification order is load-bearing there.
  - **Simulation candidates**: (1) the CG matvec + Jacobi apply — the memory-bound
    classic, and CG already wins on 3D elasticity at scale, so accelerating it widens
    the win where the direct solver's wall stands; (2) batched element-stiffness
    integration feeding the SAME deterministic scatter (the assembly-parallelisation
    decline showed the scatter is 82–94% of assembly cost, so measure whether the GPU
    half pays at all before building it); (3) the supernodal factorization's BLAS-3
    inner kernel — the analysis already on file says blocking is the lever and the tree
    ceiling is 1.6–1.9×, and a GPU GEMM is that lever's strongest form, but it only
    exists AFTER the supernodal CPU factorization does, so it is sequenced behind it.
  - **Verification bar**: every kernel ships with its CPU twin and an asserted
    deviation bound; FEA results additionally re-verified through the existing
    closed-form suite (the figures that held through the unit consolidation are the
    regression oracle — if a GPU path moves the cantilever or a modal frequency past
    its recorded tolerance, the path is wrong, whatever the speedup); determinism
    stated honestly per path (same-device reproducibility is achievable, cross-device
    is not, and the docs must say which is promised).
- [ ] **CNC / CAM campaign — 3D-printing (FDM) slicing, 2.5D CNC milling (the Kiri:Moto
  shape), 3-axis surfacing, and HSM adaptive clearing.** One campaign because all four
  stand on a substrate that already shipped without ever being called CAM: exact planar
  sections (`Shape.Section`/`SectionExact` — a bore's rim arrives as ONE arc, so an
  arc-capable G-code writer emits G2/G3 rather than chords), exact 2D offsetting
  (`Region2dOffset`/`CurvedRegion2dOffset` — successive inward offsets ARE pocket
  clearing and perimeter shells, round joins are exact sectors, and `Stroke` is already
  documented as "toolpath footprints"), the space-filling infill family
  (`SpaceFillingInfill`/`InfillPath`, whose docs call their consumer a toolpath, with
  coverage MEASURED through `Stroke` and thin necks through `Region2dThickness`),
  deterministic travel linking (`RunLinker` — the open-TSP heuristic all fill consumers
  already share), the exact 2D signed distance (`SketchRegion.SignedDistance`), overhang
  analysis (`Manufacturability` — the support-generation seed, thresholds compared on
  the dot product), swept volumes (`Shape.SweptOver`, implicit-Native — a material-removal
  simulation is a swept tool subtracted from stock, which the SDF engine does natively),
  and `HoleTable.For(part)` (the drilling cycles' input — the holes already know their
  specs and depths). What is missing everywhere is the same thin-but-decisive layer:
  tool/machine models, pass linking with lead-in/out, Z stepping, and G-code.
  - **Stage 1 — FDM slicing: the CORE HAS LANDED** (`EngrCAD.Cam`: `FdmSlicer` +
    `PrinterProfile` + `GcodeWriter`/`GcodeReader`; design.md §6e, docs
    `examples/cam-slicing.md`) — mid-layer exact sections over ONE lowering, wall
    shells by inward offset (innermost-first kept as a print-quality ORDER the travel
    linker is not allowed to change — the first test run caught the greedy linker
    printing the outer wall first), ±45° rectilinear infill by exact even-odd scanline
    anchored to the global grid, `RunLinker` travel, the stadium-bead extrusion
    identity asserted THROUGH the twin decoder, retraction as decoder-matchable pairs,
    write-only-when-stated temperatures, byte-identical determinism. **The print DIRECTION
    is selectable** (`Slice(shape, profile, printDirection)` — minimal rotation onto the
    bed, +Z the byte-identical fast path, antiparallel via the one ArbitraryPerpendicular
    convention, zero refused) **and the print ANIMATES** (`SectionTrack`, the animation
    system's fourth track kind: `SectionTracks.Reveal` sweeps a clip plane up the build
    direction quantized to the slice's own layer count — shader state, no re-meshing, wired
    through APNG export / stills / window playback; the `animate:cam-print` APNG is
    committed). **SUPPORTS LANDED** (`SupportOverhangAngle`/`SupportSpacing`/`SupportGap`,
    0 = off byte-identically; the `Manufacturability` dot-product rule on the oriented
    shape's own mesh, per-layer Sutherland–Hodgman clip so a slanted overhang's supports
    track its own height, the XY gap as a grown-section subtraction asserted point-by-point,
    bed-resting facets excluding themselves with no special case; design.md §6e). Brim/skirt
    LANDED earlier (write-only-when-stated first-layer rings, a bore ringed inside too).
    **Stage-1 residuals, open**: a raft (outward-offset base layers under everything, supports
    included), a support Z-GAP (one layer of air under the overhang for cleaner breakaway —
    v1 runs supports to the underside exactly, pinned by test) + interface layers +
    supports-on-model awareness (v1 columns run bed-to-overhang, printing around part
    material in between), seam placement smarter than the deterministic first-vertex anchor,
    richer infill patterns (the `SpaceFillingInfill` families — the plain serpentine is
    `blockOrder: 0`, gyroid infill is a plane-section of the TPMS family the implicit
    engine already carries), and the MASS identity strengthened to a solid-vs-extruded
    comparison per shape family (the coverage ratio is measured today with its
    deviations attributed; the per-layer area and extrusion-bookkeeping identities are
    landed).
  - **Stage 2 — 2.5D CNC milling: the CORE HAS LANDED** (`MillTool`/`CncMill`/
    `CncGcodeWriter`; design.md §6e, docs `examples/cam-milling.md`) — pocket = the
    inward-offset ring ladder (innermost-first per StepDown level, island hole loops
    ridden, stepover ≤ 0.5 provably covering), profile = one outline offset with
    arc-length-spaced holding TABS on the final pass (vertical rises at the tab edge,
    never a part-cut ramp), drill = EXPANDED peck moves the one twin decoder reads,
    moves classified by SHAPE (XY = cut, down = plunge, up = rapid), the morphological-
    OPENING coverage oracle with the (4−π)r² corner-residue closed form, exact
    point-by-point no-gouge, and the decoder's new `Rapid` flag (feed state persists
    across G0/G1 — the modal-state trap). **The completion pack landed** climb-vs-
    conventional (`MillDirection`, DERIVED — material-left-of-travel is climb for an
    M3 cutter — applied by measured shoelace sign, island rings orienting oppositely),
    canned G81/G83 cycles (writer opt-in with Z/R/Q reconstructed from the pass's own
    moves and irregular ladders falling back; decoder expands under Fanuc semantics
    with modal bare-X/Y, refusing a missing Z/R/Q by name), and the ⚠ feeds/speeds
    catalogue (`CncToolLibrary.Suggest` over `MillMaterials` — the cap preserves chip
    load, not feed). **Model-fed drilling LANDED**
    (`CncDrilling.FromShape/FromPart` over `HoleTable`'s rows, which gained the numeric
    drilling data — diameter/depth/plane; one op per distinct diameter, cbore = the
    through bore, threaded = the tap pilot, tilted planes refused by row letter).
    **Rest machining LANDED** (`PocketRest`
    — rest = region − opening(R₁), each residue pocketed over intersect(grow(piece,
    2·r₂), region) with the 2·r₂ a DERIVED sufficiency; tangential cusp contact made
    transversal by an ε-grow; sub-thickness residues skipped as flattening noise;
    combined-footprint-equals-finish-opening oracle + the (4−π) ladder closed form).
    **Helical ramp entry LANDED**
    (`Pocket(rampAngleDegrees:)` — a helix from the previous cleared level, radius
    under the tool's with measured room and a plunge fallback, the level's rings ONE
    pass linked at depth behind an exact segment-clearance test; plunges end only at
    level TOPS, asserted through the decoder; fixed en route: ring loops link per
    RING level, innermost first — the one-global-link order was pen-dependent and
    started a level at its boundary ring). NOTE for the stock-sim composition: a
    helix retraces one polygon per turn, so its footprint has EXACTLY-coincident
    repeated segments — dedupe before stroking (the ramp coverage test does).
    **Stage-2 residuals, open**: native arcs carried end to end from the exact
    curved-profile tier (`SectionExact`/`CurvedRegion2dOffset` — a `MillPass` whose
    segments ARE arcs; the WRITER/DECODER half landed — opt-in `arcFitting` on
    `CncGcodeWriter.Write` fits co-circular constant-z runs into I/J-form G2/G3
    with each chord's sagitta capped at the file's own 1e-3 coordinate quantum,
    the cap being what the on-circle test cannot supply: IEEE negation is exact,
    so a symmetric part's straight side flanked by its two tangency vertices puts
    four points EXACTLY on a 675 mm phantom circle bulging 0.027 mm across the
    side; `GcodeReader` expands I/J arcs at 5° and refuses R-form/missing-centre/
    radius-disagreement by name), and the material-removal animation over recorded
    stock states. Lead-in/out arcs LANDED (`Profile(leadRadius:)` — quarter arcs
    tangent at the seam on the away-from-material side, a travel-relative fact by
    Orient's winding contract so no per-loop kind flag exists; the plunge lands at
    the arc start, off the wall; a lead that cannot fit a small hole refuses by
    name with the shortfall; composes with tabs and depth levels, 0 byte-identical). The LASER/drag-knife mode LANDED
    (`CncLaser` — one outward kerf/2 offset gives outer AND hole beam paths with the
    compensation right, holes first per the release rule, GRBL M4 flavour with no Z
    word anywhere, decoder-verified at the micron quantization grade).
  - **Stage 3 — 3-axis surfacing: the CORE HAS LANDED** (`CncSurfacing.Raster`/
    `Waterline` + the `ScallopHeight`/`StepoverForScallop` chord identity; design.md
    §6e, docs `examples/cam-surfacing.md`) — the implicit-engine differentiator built
    as predicted: the ball-nose cutter-location surface IS the SDF's r-offset, raster
    a gouge-free-by-construction sphere trace (Lipschitz — a stall leaves the centre
    HIGH, stock never gouge), waterline the r-isolevel on the centre plane via
    `SdfContours.OnPlane` + an IN-PLANE Newton polish (exact on the steep walls the
    strategy exists for, crossing-error-honest on flats), the gouge inequality
    asserted point-by-point on both strategies, a vertical cylinder's waterline at
    `R + r` to 1e-6, the flat-top raster exact, the dome apex touched at its own
    height (grid anchoring), and `s²/8r` MEASURED as the chord identity's expansion
    rather than shipped as the formula. **Flat and bull-nose LANDED, and the filed
    framing was overturned in the instructive direction**: "the rounded-cone distance
    the SDF vocabulary already spells" fails the arithmetic — the CL condition is a MIN
    over the tool's disc, and certifying a min to eps via a 1-Lipschitz oracle is
    Omega((a/eps)^2) wherever the field is horizontally FLAT, the common case, so
    flat/bull ride the tessellation as the textbook drop-cutter (vertex exact, edge a
    bracketed 1D scan since torus-line tangency is a quartic, face closed form) with the
    flat-spot/APT oracles exact and the ball's mesh-vs-field cross-check; the ball is
    special because its disc is a point, and it keeps the exact field route
    byte-for-byte. The raster ANGLE landed (grid anchored in
    the rotated frame, quarter turns exact sign swaps, one `SerpentineRaster` rule for
    both cutter routes). The flat/bull WATERLINE landed as the
    silhouette-dilation contour (exact for flat; a banded conservative ladder for the
    bull corner — each band clips above z + r·k/K and grows the band's OUTER reach, so
    it over-covers: stock never gouge; the 45°-cone oracle brackets the banded 3.661
    between the exact 3.414 and the sharp 4.0). No-retract row linking LANDED
    (`linkRows:` — rows merge into one pass with connectors sampled ON the CL surface
    via the same tipAt, one plunge instead of one per row, both cutter routes through
    the one serpentine rule, default off byte-identical). **Stage-3 residuals: NONE open.** Adaptive stepover LANDED
    (`CncSurfacing.AdaptiveRaster` — the scallop height is the stated number and
    the row spacing follows the surface: each next row placed by bisection on the
    MEASURED worst 3D CL-point distance through the ScallopHeight chord identity,
    exactly cos θ times the flat spacing on a tilted plane (held at 45° by test),
    first-order on curved surfaces and said so; corner radius governs, flat cutter
    refused by name; rows part-anchored since a variable spacing has no stated
    number for the phase rule to hold; cliffs floor at flat/32 — the wall belongs
    to the flank; the acceptance carries a 1e-9 relative grace because
    cusp(flatSpacing) == h MATHEMATICALLY and an exact comparison hands a flat
    plate's fast path to FP rounding). Holder/shank collision LANDED
    (`CncHolder.Check` — the holder as a conservative flat disc riding stickout
    above the tip, so collision is the FLAT drop-cutter question at the holder's
    own radius through the shared `DropProbe`; the report carries every colliding
    point and `MinimumStickout` = max(required − cl.z), at which the setup passes
    since zero clearance is resting contact; checked against the FINISHED part
    with the roughing lower-bound stated; the fixture finding: the raster runs one
    grid step past the part bounds where the ball's CL dips below the top wrapping
    the outer edge — exactly √(r²−d²)−r, −1 at a corner — so a boss a rim disc can
    reach adds that dip and the closed form is not the obstacle height). Rest
    machining landed with stage 2's entry.
  - **Stage 4 — HSM adaptive clearing** (the research-grade centrepiece, flagged as
    such: Kiri:Moto does not attempt it; Fusion/HSMWorks is the reference).
    **Trochoidal SLOTTING has LANDED** (`CncHsm.TrochoidalSlot`; design.md §6e, docs
    `cam-milling.md` §HSM) with the campaign's own bar met the strong way: the
    engagement angle MEASURED from the evolving stock and bounded by the stated
    maximum, the straight-slot ~180° control proving the instrument — and the finding
    that the straight-cut relation `a = r·(1 − cos φ)` is measurably WRONG for a
    trochoid (60° ask read 90°: the previous loop's swept boundary is CONVEX), so the
    advance is SOLVED by bisection against a steady-state model of the same rule.
    Spiral-out entry at the same pitch, its wide-but-shallow contact arc stated
    honestly (the entry's bounded quantity is the radial step, the chip load).
    **Still open in stage 4**: general adaptive constant-engagement POCKETING over the
    evolving stock region (the 2D boolean is the inner loop, so its measured cost
    budget decides the representation, region vs raster, the Packing lesson;
    `Region2dThickness` finds the necks that force trochoidal linking), helical z
    entry, and the trochoid × stock-record composition — the swept union's scallop
    cusps are near-tangent crossings, the mesh imprint boolean's hostile family, and a
    footprint-smoothing tolerance tried against it broke honest fixtures while fixing
    nothing (a real robustness item for the imprint's near-tangency handling, not a
    CAM-side workaround).
  - **Stage 5 — NON-PLANAR slicing** (deliberately last: it needs stages 1 and 3 landed
    first): curved-layer FDM, where the top layers follow the part's own surface rather
    than a stack of planes — the exp-map machinery is the substrate
    (`MeshLocalParam`/`SurfaceDecoration` already lay flat curves on doubly-curved
    surfaces WITH THE DISTORTION REPORTED, and that honesty is exactly what a
    non-planar slicer must inherit: bead spacing on a curved layer is the decoration's
    `MinScale`/`MaxScale` question verbatim). The v1 shape is planar-body +
    non-planar TOP-SURFACE finishing (the practical form: the last N layers deform to
    the surface, everything below stays planar), with the two constraints stated as
    refusals rather than discovered on a printer — the nozzle CONE must clear the
    already-printed surface (a swept-clearance query the SDF engine answers) and the
    layer normal's tilt is bounded by the nozzle geometry. Non-planar 3/5-axis CNC
    finishing shares the machinery and is assessed here, not assumed.
  - **Cross-cutting, all stages**: ONE dependency-free G-code writer with per-dialect
    flavours (Marlin vs GRBL/LinuxCNC differ in words, not structure — a fifth
    hand-rolled text format, plainer than PDF was) plus its twin-decoder reader; a
    `Machine`/`Tool`/`PrinterProfile` vocabulary with every transcribed table ⚠
    flagged. **The toolpath/material-removal pair HAS LANDED** (`PathTracks.Follow` +
    `CncStock.Simulate`; design.md §6e): the TOOL along its path is a matrices-only
    pose track (arc-length parameterized, bystanders bit-identical), and the CHANGING
    STOCK is recorded DATA — 2.5D swept volumes are closed-form prisms subtracted as
    z bands through the mesh imprint boolean, the drill state an exact 32-gon-prism
    identity — never a live clip, because a changing-geometry animation has no
    matrices-only form (the transient-thermal precedent); the FDM material-ADDED clip
    landed earlier as the SectionTrack. **Still open here**: a static toolpath line
    PREVIEW in the viewport (the isoline overlay's machinery, coloured by feature —
    the PrusaSlicer-parity preview item), the 3-axis (surfacing) stock simulation
    (a raster row's swept volume is not a prism — `Simulate` refuses it by name),
    and determinism throughout — two slices of one scene must stay byte-identical,
    because a toolpath diff is how a CAM regression is caught.
- [ ] **PrusaSlicer feature parity — the FDM stage grown into a full slicing stack**
  (filed 2026-08-15 at Chris's direction: "all the features of PrusaSlicer". The landed
  stage-1 core — mid-layer exact sections, walls, ±45° rectilinear infill, brim/skirt,
  supports, print direction, retraction, temperatures, the extrusion identity — is the
  substrate, and each family below names the landed machinery it rides. PrusaSlicer is
  reference BEHAVIOUR, never source (it is GPL and this repo is not a derivative):
  transcribe the conventions ⚠-flagged and verify against closed forms, the house rule.)
  - **Perimeters**:
    - [ ] Arachne variable-width perimeters (the headline algorithm: walls generated from
      a medial-axis skeleton with per-segment width, so a thin region gets 1..N beads
      with neither a gap nor an overlap — `Region2dThickness` + the exact offset family
      are the substrate; research-grade, budget it like the autorouter).
    - [ ] Classic gap fill (the sliver between two walls too narrow for a bead: detect
      where the inward offset vanishes, fill with a centre-line stroke) and thin-wall
      detection (a wall thinner than one bead becomes a single centre-line pass).
    - External-perimeters-first LANDED; spiral vase LANDED (writer-side z ramp along
      the wall's arc length, contradictions + multi-island layers refused by name);
      seam placement LANDED as Rear/Aligned (the concave-corner cost model still open).
    - [ ] Extra perimeters on overhangs / ensure-vertical-shell-thickness.
    - Fuzzy skin LANDED (a stateless hash of (layer, point index) — byte-identical
      slices, layer 0 and inner shells exempt bit-for-bit).
  - **Top/bottom & surfaces**:
    - Solid top/bottom shells LANDED (`TopSolidLayers`/`BottomSolidLayers`, the
      neighbour-window intersection subtracted from the infill core, skins at the
      bead spacing, top/bottom-of-stack solid with no special case, 0 = off
      byte-identically; the step fixture pins the split at the overhanging wall;
      design.md §6e). Still open here: shells by THICKNESS rather than count, and
      the skin-to-sparse anchor margin.
    - Monotonic skins LANDED (scanline order, one direction, never linked) and ironing
      LANDED (top-exposed skin only, per-path Flow — the extrusion identity generalises
      to sum of length x flow). Bridges LANDED as immediate-below air detection filled
      along the region's long axis at BridgeSpeed. Still open: internal bridges over
      sparse infill, thick bridges, bridge fan.
  - **Infill patterns**: grid / triangles / concentric / gyroid (the TPMS level set
    sectioned per layer) / Hilbert (`SpaceFillingInfill`) LANDED, every member holding
    the stated density by direction-count spacing. Still open: cubic/adaptive-cubic/
    honeycomb members, [ ] LIGHTNING infill (research-grade),
    combine-infill-every-N-layers, infill/perimeter anchor runs,
    solid-infill-threshold area.
  - **Layers**: variable layer height LANDED — an explicit bottom-up table (validated:
    printable per layer, covering the part) plus `AdaptiveLayerHeights` from the
    stair-step cusp criterion `h <= cusp/|n_z|` (the cusp height a REQUIRED input);
    the extrusion arithmetic went per-layer (each E reads its own stadium), with the
    mixed-height print asserted to BREAK the naive single-ratio identity.
  - **Supports**: Z contact gap LANDED (the clip plane moved, gap 0 bit-identical),
    interface layers LANDED (densified + perpendicular near contact), RAFT LANDED
    (prepended layers, the part lifted, adhesion moved to the raft), ENFORCER/BLOCKER
    shapes LANDED (`FdmSupportModifiers` — the code-first paint-on support). Still
    open: on-model awareness, buildplate-only vs everywhere as a stated choice, snug
    vs grid style, [ ] organic/tree supports (research-grade).
  - **Speeds, extrusion, cooling** (the writer grows a per-FEATURE table):
    - Per-feature SPEEDS landed (Wall/Infill/SolidInfill/Support/FirstLayer through the
      one `SpeedFor` rule, unset byte-identical, stated speeds changing only the F
      words) and the PRINT-TIME ESTIMATOR landed as an honest [min, max] bracket over
      the DECODED program (closed-form trapezoids; junction cornering filed as the
      refinement that narrows the bracket without moving its ends). Still open:
      per-feature WIDTHS and accelerations, external-vs-internal perimeter speeds,
      the volumetric flow CAP (auto-speed).
    - [ ] Cooling: minimum-layer-time slowdown, per-feature fan, bridge fan, fan off
      for the first N layers.
    - [ ] Retraction extras: z-hop (+ types), wipe-on-retract, extra restart length,
      firmware retraction (G10/G11), per-extruder tables; linear/pressure-advance K.
    - [ ] Elephant-foot compensation (first-layer inward offset), XY size compensation,
      hole compensation (per-loop signed offsets — exact with the curved offset tier).
  - **Multi-material / multi-extruder**:
    - [ ] Per-region extruder assignment via MODIFIER SHAPES (the code-first paint: a
      `Shape` intersected with the part assigns settings/extruders per region — one
      declaration, geometry and slice both derive), per-object setting overrides.
    - [ ] Tool-change G-code, a WIPE TOWER (a printed purge block — its own small
      slicing problem), layer colour change (M600), soluble support interfaces.
  - **Plating & sequence**:
    - Multi-part plates LANDED (`FdmPlating.Plate` over `Packing` — one shape sliced
      whole, disjoint islands, the packer's own out-of-room refusal).
    - Sequential printing LANDED (`FdmSequential` — ascending-height order, pairwise
      bounds-gap clearance refusing conservatively, one over-gantry part max printed
      last, handover hops + XY-before-descend + G92 E0, decoder-conserved filament;
      the filed swept-cylinder SDF query was NOT needed — the clearance radius and
      gantry height ARE the printer's own two numbers, and a bounds gap under-estimates
      so the refusal direction is sound).
  - **G-code & output**:
    - Start/end/layer-change snippets LANDED ({layer}/{z} substitution; a smuggled
      G91/G20 refuses at the DECODER by name — stronger than a write-side allowlist)
      and filament-by-role LANDED (flow-aware, sums to the total exactly). Still open:
      tool-change snippets (multi-material), G-code FLAVOURS (Klipper/RRF differ in
      words not structure), arc FITTING out of the exact curved tier (shared with the
      CNC G2/G3 item), binary G-code (.bgcode), a post-processing hook.
  - **Preview/analysis** (viewer-side): [ ] toolpath preview coloured by feature/speed/
    flow (the `FieldDisplay` colour machinery over path line geometry), travel moves
    shown, per-layer scrub (the print-reveal SectionTrack landed; a path-index scrub is
    its within-layer analogue), estimated time per feature.
  - **Geometry ops PrusaSlicer bundles — already landed here, listed for the mapping**:
    cut with connectors (`MeshPlaneCut` + `Shape.Drill` dowels), text emboss
    (`Shape.Text`), simplify (`MeshDecimator`), repair (`MeshRepair`), hollow + drain
    (`Shape.Shell` + `Drill`), measure (the viewer's measure tool).
  - **SLA family** (PrusaSlicer is an SLA slicer too; a separate campaign if wanted):
    [ ] per-layer mask IMAGES rasterized from the field (the implicit engine's natural
    fit — a layer is one batch `Sdf.Evaluate` over a pixel grid), pin/tree SLA supports,
    hollowing with drainage holes, anti-aliased edges, exposure profiles.
  - **Explicitly out of scope** (product surface, not kernel): network upload
    (PrusaLink/OctoPrint/Connect), printer accounts, the config wizard's profile
    bundles, paint-ON-mesh UI (the code-first equivalent is modifier shapes above),
    third-party repair services.
- **Heatsink design tool LANDED** (`NaturalConvection`/`HeatsinkSizing` in Fea +
  the `fea-thermal.md` example building the solid): Bar-Cohen & Rohsenow composite with
  Nu = 1.31 DERIVED from the composite (a second stored copy could only drift), Elenbaas
  El = 54.3 and air constants ⚠ datasheet-form; fin efficiency held against an
  independent FD solve (8 digits) AND a real 3D conduction solve of the same fin
  (ratio 1.0001 — the discriminating row); S(16L) = 2·S(L) to twelve digits; impossible
  envelopes refuse naming asked vs achievable watts. Horizontal-PLATE correlations have
  since landed (the McAdams family on `NaturalConvection`: 0.54/0.27·Ra^(1/4) + the
  0.15·Ra^(1/3) turbulent branch, validity ranges refused by name; facing-up exactly
  2x facing-down bit-for-bit, and the turbulent film coefficient SIZE-independent —
  the 1/3 power cancels Ra's L^3 — asserted as two plates reading one h). The design-study
  loop over a generated `Feature` landed as the docs-example composition it honestly is
  (`fea-thermal.md` §Closing the loop — the layering note: Modeling cannot reference Fea,
  so the objective reads the correlations at the app layer; the lazy Shape graph makes
  ~380 evaluations cost milliseconds, the winner's solid measured ONCE against the study's
  own closed form to 0.000%, the study starting infeasible at ~4 K/W and ending exactly ON
  the 2.92 K/W constraint).
- **Thermal SIMP LANDED** (`TopologyOptimizer.MinimizeThermal` + the
  `fea-topology.md` volume-to-point example): the loop proved physics-blind as predicted —
  `FeaAssembly.Conductance` learned the per-element scale, the shared `RunOptimization`
  extraction left the structural path bit-identical (its whole suite the oracle), the
  p = 1/3 uniform closed forms met EXACTLY, FD sensitivity 9.2e-8, `f'T = Σρᵖ·E_e` to
  twelve digits, the dendrite at 25% of the uniform smear. Convection and nonzero
  prescribed temperatures refuse by name (design-dependent loads); a ZERO sink is the
  volume-to-point convention. The remaining half of the original entry (below) stays open.
- [ ] **Topology-optimised HEAT SINK — the RENDERED dendrite docs figure and a
  convection-backed design workflow (was: thermal SIMP plus the docs example).** The structural optimiser's loop is nearly physics-blind:
  thermal compliance `T'·q` over a density-scaled conductance is the same OC iteration
  with `FeaAssembly.Conductance` learning the optional per-element scale
  `FeaAssembly.Stiffness` already carries (null skips the multiply, the incumbent path
  bit-identical — the same seam, same argument), the sensitivity is the element THERMAL
  energy at the assembly's own quadrature (the "two constructions check each other"
  identity again: `Σ ρᵖ·E_e` vs `f'·T`), and the filter/volume machinery carries over
  verbatim. **The honest scope is conduction-dominated design** — the classic
  volume-to-point problem (a generating region drained to a cold edge through a budget
  of high-conductivity material, which optimises into the well-known dendrite) — because
  design-dependent CONVECTION on evolving boundaries is refused BY NAME for exactly the
  reason structural SIMP refuses self-weight: the load moves with the design and the
  problem stops being self-adjoint. Verification in the landed optimiser's own style:
  the uniform-field closed form at p = 1, the finite-difference sensitivity check
  through the PRODUCTION evaluator, volume constraint at round-off, monotone descent —
  and the docs example designs the heat sink end to end (optimise → `Release` →
  `Shape.From` → the CNC campaign's own toolpaths over it, which is why it sequences
  after CNC: the example should finish as a manufacturable part, not a picture).

Each of these is its own product-sized campaign rather than a backlog item, and each sits
here because the honest assessment says so — not because nobody got to it. They are kept
in this file, with their reasoning intact, so that a future decision to start one begins
from what was already understood rather than from scratch.

- [ ] **CFD — assess honestly before starting, because it is not "FEA with different
  physics".** Structural and thermal share a shape: symmetric positive-definite operators,
  one unknown field, `SparseCholesky`/CG, and a verification bar of analytic solutions.
  Incompressible flow breaks every one of those, and the backlog should say so before
  anyone budgets it as a third solver.
  - **✅ STAGE 1 (non-symmetric solvers) HAS LANDED in `EngrCAD.Core.Solvers`** — `Gmres`
    (restarted GMRES(m), right-preconditioned so the tracked residual is the ORIGINAL
    system's), `BiCgStab`, an `Ilu0` preconditioner behind the shared `IPreconditioner`
    seam (also on `CgOptions.Preconditioner` for symmetric PCG), all verified against a
    dense partial-pivoting reference on random non-symmetric, upwind and high-Péclet
    convection–diffusion, the GMRES-converges-in-≤n theorem, the ILU-is-exact-LU-with-no-fill
    identity, breakdown honesty (no silent NaN) and determinism. **No new matrix type was
    needed** — `PackedSparseMatrix` was already general — and **AMD does NOT apply to ILU(0)**
    (zero fill means no fill to reduce; see design.md §2 and the `Ilu0` class doc; RCM/
    multicolour ordering is for the ILU(p)/ILUT tier, filed there when that tier exists). So
    the substrate below "the matrix is not symmetric" now exists; stages 2+ (the flow physics)
    remain open.
  - **The matrix is not symmetric.** Advection makes it non-symmetric and, at any
    interesting Reynolds number, non-diagonally-dominant. `SparseSymmetricCG` and
    `SparseCholesky` do not apply; this needs **GMRES or BiCGSTAB with a real
    preconditioner** (ILU at minimum) — now BUILT (see the stage-1 note above). Remaining
    non-symmetric-solver work for later stages, if wanted: ILU(p)/ILUT with a fill-reducing
    reorder (RCM/multicolour), block/Schur preconditioners for the saddle system, and a
    flexible GMRES that admits an iterative inner preconditioner.
  - **It is a saddle-point problem.** Velocity and pressure are coupled and the pressure
    has no equation of its own. Either a segregated scheme (SIMPLE/PISO) or a monolithic
    solve with a block preconditioner; and equal-order velocity/pressure elements are
    **inf-sup unstable** — so either Taylor–Hood (P2 velocity / P1 pressure, which the
    existing 10-node/4-node tet pair gives almost for free) or PSPG stabilization. The
    Taylor–Hood route is the one that reuses what exists.
  - **Advection needs stabilization** (SUPG) once it dominates diffusion, or the solution
    oscillates rather than being merely inaccurate — a failure mode that looks like a bug
    forever.
  - **The mesh requirement is no longer the blocker, and it is worth being precise about
    what that changed.** `BoundaryLayerSpec` now marches a graded anisotropic stack inward
    from a `Facets`-selected wall (measured stretches to 72x, the wall named by the same
    selector a no-slip condition would use, the volume identity and both patch tests
    surviving it). So a CFD solver would no longer be running on isotropic tets. What it
    does NOT give is uniform y+ control on curved walls (no `cos` correction at corners,
    no per-facet thickness law), rims that slide along curved neighbours, or concave
    corners meshed with multiple normals per node — all filed above. Nothing here touches
    the flow physics: **what the mesher gives CFD is the mesh, not the solver**, and the
    staging below is unchanged.
  - **Turbulence is a modelling choice, not an algorithm.** Laminar-only is a defensible
    v1 and covers real engineering (internal flow at low Re, cooling channels); RANS
    (k-ω SST) is a second project with its own wall-function subtleties.
  - **Staging that respects the verification culture**: (1) non-symmetric solvers in Core,
    verified against dense references; (2) **Stokes flow** — linear, no advection, and
    exactly where inf-sup stability is provable and testable; (3) steady Navier–Stokes,
    laminar; (4) transient; (5) turbulence, or not.
  - **Verification bar, non-negotiable and higher than the other solvers'** because CFD
    fails plausibly: Poiseuille flow against the exact parabolic profile and its friction
    factor; **lid-driven cavity against Ghia et al.'s tabulated centreline velocities** at
    Re 100/400/1000; backward-facing step reattachment length against Armaly; and flow past
    a cylinder against the **Schäfer–Turek** benchmark's drag/lift coefficients. Report
    every number in the design record, as the structural and thermal solvers did.
  - **The honest summary**: this is larger than structural and thermal combined, its
    prerequisite (anisotropic meshing) is itself a substantial project, and a
    half-verified CFD solver is worse than none because its output is persuasive. Worth
    doing — but as its own campaign, staged as above, not as a fourth item in a sweep.

- [ ] **ECAD — code-defined schematics, PCB layout, and MID/LDS 3D routing, as a
  first-class campaign** (re-scoped 2026-08-09 at Chris's request: the earlier entry drew a
  line that kept the connectivity side out as "a second company", and that line is now
  deliberately crossed — this kernel builds the whole stack, schematic through routing
  through 3D placement, with DRC on both 2D and 3D boards). It is large, it is staged, and
  the argument for doing it HERE rather than reaching for KiCad is that a surprising amount
  of it is geometry this kernel already does — copper clearance IS a region offset, a
  keep-out IS a boolean, and a trace on a moulded surface IS the tamper-mesh's conductive
  serpentine, already built.
  - **✅ STAGE 1 — code-defined schematics + the connectivity data model — LANDED** (the new
    kernel-tier project `EngrCAD.Ecad`, Core + Modeling only, no viewer; docs
    `examples/ecad-schematics.md`, design.md §6d, README in the project). What exists for the
    later stages to build on: `Schematic` declares `Component`s from reusable
    `PartDefinition`s (ordered `Pin`s + `PinType`, a `Footprint`/`Pad` DATA placeholder the
    layout stage consumes, an OPTIONAL `Func<Shape>` body hook) and connects pins into `Net`s
    (`NetKind` Signal/Stub/NoConnect, no-connect first-class); the object graph IS the netlist
    and `Netlist`/`ToNetlist()` is a derived read-only view. `Schematic.Check` is the
    combinatorial DRC (the counting identity `TotalPins == PinsCoveredOnce` plus
    floating/empty-net checks, offenders NAMED, every guard shown to fire); `Save`/`Load` is
    the byte-fixed-point JSON seam with `PartDefinition`s interned by identity, a `PartLibrary`
    re-attaching the code-only body on load, and structural refusals BY NAME. So the model,
    the verification and the persistence exist — the **next stage** (board + components as an
    `Assembly`) reads THIS graph: a board is a `Sketch` outline + `Drill`, a component is a
    `HardwareComponent` (body + seating + host preparation), placement is `LocationSet`/
    `Pattern` with a pose per part, and the derivation must ride the one-declaration rule
    (footprint and 3D body derive from the same `PartDefinition`, never a second source). The
    sub-bullets below are the OPEN stages.
  - **✅ STAGE 2 — the board and its parts as an Assembly — LANDED** (`PcbBoard`/`PcbStackup`/
    `PcbLayout`/`PcbPlacement`/`PlacedPad`/`CopperLayer` + the `Pad` extension + IDF import;
    docs `examples/ecad-pcb.md`, design.md §6d, README). The whole derivation rides the
    one-declaration rule off the STAGE-1 schematic graph: a `PcbBoard` is a polygon outline +
    thickness + copper stackup + its own holes/keep-outs, `Plate()` is the exact B-Rep
    (`Shape.Extrude` + `Shape.Drill`) with the closed-form volume oracle `area·t − Σπr²·t`; a
    `PcbLayout` places components at `(x, y, rot, side)` and DERIVES the copper (pads projected
    per layer, pin↔pad identity), the drills (through-hole pads drill the plate, SMD drills
    nothing), and the 3D bodies (`ToAssembly` → board + one occurrence per placed body,
    flattening to `PartInstance`s the BOM/exporters consume). The bottom flip is a genuine
    reflection on the part transform (`Mirror(Mirror(x)) == x`, board +Z untouched — the
    FlipX-not-FlipZ doctrine), and `WorldOf(placement)` is bit-identical to the assembly's own
    `PartInstance.World`. `Check()` is the geometric lift of the pin-counting identity
    (`PlacedPinCount == PlacedPadCount`, every pin covered once; pads-off-board / pins-without-
    pads / missing-footprints / holes-in-keep-out named). `Save`/`Load` embeds the schematic
    and is a byte fixed point; `Pad` gained `Kind`/`DrillDiameter` write-only-when-stated so a
    stage-1 footprint saves byte-identically. **IDF 4.0 import landed too** (`IdfReader`/
    `PcbImport`/`IdfWriter`): board outline + holes + placements + keep-outs, unit-honoured, a
    round-trip byte fixed point for the geometry it carries, malformed structure refused by
    name. Residual follow-ups: IDF arc outlines / cutout loops / `.emp` component bodies (v1
    flattens/drops/ignores them with a diagnostic); keep-out DRC is a centre-in-polygon test,
    not yet the copper-clearance region query; KiCad `.kicad_pcb` and STEP AP214 board-assembly
    interchange stay open below (the drawn schematic SHEET has since landed).
  - **✅ STAGE 3 — placement constraints — LANDED** (`PcbConstraints.cs`/`ConstrainedLayout.cs`/
    `PcbConstraintSolver.cs`/`PcbConstraintFile.cs`; docs `examples/ecad-constraints.md`,
    design.md §6d, README). Components are placed by CONSTRAINT rather than typed coordinates:
    the variables are each free placement's rigid 2D pose `(x, y, θ)`, `layout.Constrain()`
    builds a `ConstrainedLayout`, and `Solve()` returns a NEW `PcbLayout` at the solved poses —
    the copper/drills/nets/bodies DERIVE from it, so `Solved.Check()` still passes and
    `Solved.PadsOfNet` returns the moved copper (the one-declaration identity survives). **The
    load-bearing decision was a FOCUSED solver, not reuse of the Modeling one, and it is about
    the VARIABLE MODEL**: the sketch/mate LM engines are internal/private and bound to their own
    variables (3D 6-DOF `Occurrence` frames; free 2D POINT coordinates), and a placement is
    neither — a rigid pose whose rotation moves the WHOLE footprint about its origin — so
    `PcbConstraintSolver` rebuilds the MateSolver doctrine at 2D (analytic Jacobian; every
    residual a LENGTH, angular ones scaled by the board diagonal and the rotation variable
    divided by it; rank/DOF from a diagonally pivoted Cholesky of JᵀJ at the 1e-6 relative
    floor — the sketch-constraint floor, not the mate 1e-8; drawn layout as seed AND branch
    selector; under-constrained reported, contradictions/stationary NAMED, a failed solve
    bit-identical). No Modeling solver was touched; a shared generic 2D-rigid core is FILED (no
    third consumer). Vocabulary: `Lock`/`Fix`, `Group`/`Cluster` (a rigid body — each member's
    fixed offset captured off the drawn layout), `Orient`/`FixRotation`, `Distance`/`Spacing`,
    `AlignX`/`AlignY`, `Parallel`/`Perpendicular`, `PointOnLine` (SIGNED offset), `AlignEdge`,
    `InsideRegion`/`InsideBoard`, `ClearOf`/`ClearOfRegion`/`ClearOfKeepOut`. Inequalities are
    ACTIVE-SET residuals (`min(g,0)`, pushing only when violated, adding no rank while inactive)
    over a bounding-circle footprint model; the solve is deterministic; persistence extends the
    stage-2 seam (no-constraints byte-identical to a stage-2 file, constrained a save→load→save
    fixed point). Verified: satisfied-set converges to the weld tier with the DOF reported,
    `AlignEdge` makes edges exactly parallel/collinear, `Spacing` met exactly, `ClearOfRegion`
    leaves pads disjoint at the full clearance, contradiction/stationary/over/under all
    exercised, scale-invariant to 1000×.
  - **The one genuinely new thing is a CONNECTIVITY DATA MODEL beside the geometry, and
    keeping the two coherent is the whole discipline.** A netlist is a graph — components,
    pins, nets — and it is NOT a signed distance field or a topology. The failure mode of
    every ECAD/MCAD bridge is the two models drifting (a net the copper does not connect, a
    part the schematic does not place), so the rule from day one is that ONE declaration
    produces both: the code that declares a component and its connections IS the source,
    and the netlist, the footprint placement and the 3D body are all derived from it — the
    same "the declaration is the model" doctrine `SheetMetalBody` and `FeatureHistory`
    already enforce. A DRC or a routing result that disagrees with the netlist is then a
    bug in one derivation, not an unresolvable difference between two hand-kept files.
  - **Code-defined schematics** ✅ LANDED (see the STAGE 1 note above; the model, the
    combinatorial verification and the byte-fixed-point persistence all exist). The **drawn
    schematic SHEET** ✅ LANDED too (`SchematicSheet`/`SchematicDrawing`) — a VIEW of the graph
    (placed symbols, orthogonal wires + junction dots, net labels, refdes/values, title block)
    to SVG/DXF/PDF, a deterministic function of the graph + placement whose `Verify()` proves it
    joins exactly the pins the netlist connects; it REPLACES `Netlist.ToText()` as the
    human-readable view. Open follow-ons there are named below.
  - **The board and its parts are geometry this kernel already builds.** A board is a plate
    with holes and a thickness (`Sketch` outline + `Drill` for mounting holes and vias,
    exact B-Rep today). A component is a `HardwareComponent` — a body + a seating convention
    + a host preparation — so a panel-mount connector that needs a wall cutout is precisely
    `ComponentAssembly.Place` cutting the host while recording the occurrence, already built
    and tested. Placing the footprints on the board is `LocationSet`/`Pattern` with a pose
    per part.
  - **✅ STAGE 4 — copper DRC — LANDED** (`DrcRuleSet`/`PcbCopperModel`/`CopperFeature`/
    `DrilledHole`/`PcbDrc.Check` → `DrcReport`; docs `examples/ecad-drc.md`, design.md §6d,
    README). The geometric design-rule check over a board's copper — clearance, shorts,
    annular ring, drill-to-copper, copper-to-edge, trace width and an acute-angle / acid-trap
    threshold — every rule a region query the exact 2D machinery answers with no tolerance,
    NAMING/LOCATING/MEASURING its offender against its limit. Clearance is the tamper-mesh
    construction (grow each net's copper by half the clearance, require different-net grown
    regions disjoint — an empty `CurvedRegion2dBoolean.Intersection` PROVES it) and a SHORT is
    the ungrown overlap of different nets, read against the NETLIST (same-net copper touching
    is the intended connection, never flagged — the one-declaration identity). `DrcRuleSet` is
    a standalone checking parameter (NOT baked into the layout file, so stage-2/3 files stay
    byte-identical); `PcbDrc.Violates(model, candidate)` is the incremental seam stage-5
    routing costs a candidate route with. Verified from both sides of every limit against the
    closed-form gap, scale-invariant, deterministic; the placed stage-2 fixture is DRC-clean
    with an unrouted ratsnest. See CLAUDE.md's ECAD status paragraph and design.md §6d for the
    findings. The OPEN stage below (interchange) is next; MID/LDS 3D surface routing landed as stage 9
    (see below). Enclosure fit landed —
    stage 7; thermal coupling landed — stage 8: `PcbThermal.Solve` couples per-component power
    into the landed FEA thermal solver over an effective copper-smeared slab conductivity,
    verified against the analytic conduction parabola (3.16e-12 relative), the series-resistance
    rise (3.6e-5, energy balance exact), the copper-spreading ratio (34.7×, exactly `k_copper/k_bare`),
    the no-BC refusal, isothermal zero-power and bit determinism — filed there: thermal vias as
    discrete paths, a transient warm-up, detailed die/package models and CFD airflow. See CLAUDE.md
    and design.md §6d stage 8).
  - **✅ STAGE 4b — multilayer stackups + embedded/enclosed components — LANDED**
    (`LayerStackup`/`StackLayer`/`Embedding`/`EmbeddedCavity`; the `PcbPlacement` extended with
    `Layer`/`Embedding`/`CavityClearance` + the new `Embed` method; `DrcRule.CavityClearance`;
    docs `examples/ecad-pcb.md`, design.md §6d Stage 4b, README). The copper-only `PcbStackup`
    generalizes to the full physical build-up (an ordered list of copper AND dielectric layers,
    each a thickness) with the copper-only / surface path BYTE-IDENTICAL (a board built the old
    way carries a null `LayerStackup`). `TotalThickness` = Σ layers, copper z DERIVED by one
    contact rule (outer coppers at the faces — top at total, bottom at exactly 0 via bottom-up
    accumulation — inner coppers at midplanes). `Embed(reference, layer, x, y, embedding,
    clearance, side)` seats a component on an inner layer inside a cavity milled into the plate:
    ENCLOSED (an internal void, buried) or OPEN (a well to a face), both EXACT box-tool booleans
    (rel ~1e-16, closed) with a closed-form removed volume, so `ExpectedPlateVolume` stays the
    oracle less each cavity. Containment against the outer prism, 3D overlap (z-interval AND
    OBB SAT — stacked dies on different layers allowed), an emergent 2·clearance minimum-pad
    spacing, every refusal at `Embed` BY NAME. Identity holds across layers (embedded pads on
    their inner seat layer); the DRC is N-layer aware for free (inner clearance/shorts checked)
    plus a new `CavityClearance` (other copper clearing a cavity wall on its seat layer, the
    part's own pads exempt). Persistence write-only-when-stated (a full `layerStackup` or the
    copper `stackup`, plus the placement's layer/embedding/clearance) — byte-identical stage-2..4
    files, a multilayer/embedded save→load→save fixed point, a missing-layer placement refused at
    load. Cross-layer via/microvia STITCHING (the "OPEN follow-up") is now ✅ LANDED — see the
    VIAS + CONNECTIVITY bullet below — so a net's pads on different layers are geometrically
    connected by a via and the per-pad-layer caveat is closed.
  - **✅ EXPLODED VIEW — the multilayer board sliced into per-layer slabs — LANDED**
    (`PcbLayout.ToExplodedAssembly(spacing?, name?)` + `LayerStackup.Extents`; `PcbExplode.cs`;
    docs `examples/ecad-pcb.md` animate fence + committed APNG, design.md §6d, README). Slices the
    plate into ONE slab per physical `StackLayer` (outline extruded over the layer's own z-range
    from `LayerStackup.Extents` — the SAME bottom-up accumulation the copper z's come off, exposed
    rather than recomputed — drilled by every through hole and milled by every overlapping cavity),
    assembled with the placed components (surface AND embedded), fanned along the STACKUP NORMAL.
    Sibling of `ToAssembly` (untouched); returns an ordinary `Assembly`, so the explode slider,
    `ExplodeTrack` and exporters drive it with no new code (offsets are Modeling-level
    `ExplodeOffset`/`ExplodePath`, no viewer dep). Layers fan up from the BOTTOM datum (stays put);
    `gap` is the clean empty gap whatever a layer's thickness (offset adds to the original
    contiguous position, so thicknesses cancel), STACK ORDER = explode order. Surface parts lift off
    their face pure-Z; embedded parts dogleg (straight out of the cavity, then spread — the lateral
    leg IS the dogleg, which is why an embedded offset is the one not pure-Z). Oracles: factor-0
    component poses bit-identical to `ToAssembly`; slabs DISJOINT and TILE `[0, TotalThickness]`
    exactly so their union IS the plate (`Σ slab volume == ExpectedPlateVolume`); pure-Z /
    stack-order / factor-independent-count / determinism all asserted; a copper-only board (null
    `LayerStackup`) and a negative spacing refused BY NAME. Offsets are a VIEW concern, NOT baked
    into the layout file (byte fixed point untouched, the `DrcRuleSet` rule).
  - **✅ VIAS + CROSS-LAYER CONNECTIVITY — the routing PREREQUISITE — LANDED** (`Via.cs`/
    `PcbVia.cs`/`PcbConnectivity.cs`; `DrcRule.ViaToVia` + `DrcRuleSet.MinViaToVia`; docs
    `examples/ecad-pcb.md`, design.md §6d, README). A `Via` is a net-carrying plated cross-layer
    connection at `(x, y)` spanning copper layers `[From, To]` with a drill and an annular pad; the
    **via TYPE is DERIVED from the span, not stored twice** (Through/Blind/Buried/Microvia, THROUGH
    first then MICROVIA — a single dielectric hop — taking precedence; `AddVia(..., require:)`
    validates an intent and refuses a mismatch by name, the "non-adjacent-for-microvia" refusal).
    Via copper feeds `PcbCopperModel` (an annular pad per touched layer of exact area π(pad²−drill²)/4
    plus one drill), so via clearance / drill-to-copper / annular-ring / copper-to-edge all ride the
    existing rules FREE; the one new rule is `ViaToVia` (the drill web, all pairs, net-independent).
    **`PcbConnectivity` is the heart and CLOSES the multilayer per-pad-layer caveat**: a per-net
    graph joining features that TOUCH on a layer (exact region intersection, no tolerance) OR are the
    ends of a plated barrel (a via or through-hole pad, same-source-across-layers), a net CONNECTED
    when all its component pads are in one component; `PcbDrc.Ratsnest` DELEGATES to it, so a via
    that touches each pad routes a cross-layer net. Vias are LAYOUT TRUTH (round-trip write-only-when-
    stated, no-via byte-identical); v1 does NOT cut the via drill into the 3D plate B-Rep (copper /
    connectivity / DRC only). The connectivity engine is the seam an autorouter reuses — the routing
    prerequisite is now MET.
  - **✅ GERBER (RS-274X) + EXCELLON FABRICATION EXPORT — LANDED** (`PcbGerberExport`/
    `GerberWriter`/`GerberReader`/`ExcellonWriter`/`ExcellonReader`; docs
    `examples/ecad-fabrication.md`, design.md §6d stage 6, README). One Gerber per copper layer +
    a board-outline Gerber + an Excellon drill program, from a routed `PcbLayout` (or a raw
    `PcbCopperModel` for pours): pads flash, traces draw with a round aperture (the swept stroke
    IS the copper model's trace region), via pads flash as solid discs, anything else region-fills.
    Verified by the campaign's twin-decoder oracle — parse the Gerber BACK and the recovered copper
    equals the model's per layer by area AND symmetric difference (the DRC's own
    `CurvedRegion2dBoolean`), on a hand-built AND an autorouted board; the Excellon hits recover
    exactly. THE FILED FRAMING WAS RIGHT EXCEPT FOR ONE THING: the naive "flash the pad + clear the
    via drill" opens a hole the model FILLS at a via-in-pad or a routed via (the copper is a UNION,
    so a drill is a hole only where nothing covers it) — so the writer lays all the solid copper
    down, then clears exactly the HOLES OF THE FINAL UNION, which stays correct for every via
    (the crossing fixture's SIG via lands under a pad and its partner under the trace ending on it,
    so a correct exporter emits ZERO clears there). Coordinate format derived from the board's own
    magnitudes (scale-invariant; each `%FS` field is a single digit, so a two-digit fractional
    count overflowed the field and decoded a 1e-3-scale board 10^5 too large — a format bug the
    area oracle caught as a 10-orders-of-magnitude miss). A Bézier copper boundary is refused by
    name; the reader refuses a truncated file / missing spec / aperture macro by name.
  - **Fabrication follow-ups (filed; SOLDER MASK + SILKSCREEN + SOLDER PASTE have LANDED — see
    CLAUDE.md's ECAD status, design.md §6d stage 6, `examples/ecad-fabrication.md`):** the full fab
    set is now copper + mask + silk + PASTE + outline + Excellon. The PASTE / STENCIL layer
    (`PcbPaste`/`PcbPasteSettings`/`PasteAperture`, `GerberWriter.PasteLayer`,
    `FabricationOutput.PasteLayers`) is the mask's SIBLING through the SAME `GerberWriter` and
    twin-decoder oracle, with two deliberate differences that ARE the design: it covers **SMD pads
    ONLY** (a through-hole pad — one carrying a drill — and a via get NO aperture, the SMD-only rule
    whose classic bug is pasting a THT pad; no via policy is consulted, unlike the mask), and its
    default expansion is slightly **NEGATIVE** (`-0.05 mm`, the aperture a hair smaller than the pad
    to control paste volume, so it ALLOWS the negative offset the mask refuses). **STEP / MULTI-LEVEL
    stencils LANDED** (`PasteStencil`/`PasteStep`/`PasteLevelSelector`; docs `examples/ecad-fabrication.md`,
    design.md §6d stage 6, README): a foil milled to different thicknesses in different zones (a thin foil
    for a fine-pitch part, a thick foil for a thermal pad), ONE paste Gerber per level, each pad on
    EXACTLY ONE level (a partition; zone / pad-set / opt-in `FinePitch` selectors, first-match-wins, a
    required default); the foil thickness is DELIBERATELY absent from the aperture geometry (a level's
    aperture is the pad grown by its own expansion through the same exact offset machinery), so the
    aperture-equals-pad-plus-expansion oracle is unchanged; passed to the export like a `DrcRuleSet` (not
    baked into the layout file), so a layout that declares none saves byte-identically and the flat output
    is EXACTLY as-is. Its own residuals, filed: PERSISTING a step-stencil declaration in the layout file
    (a serializable grammar for its zones/selectors is a separate, larger job than generating the
    stencils), and a per-fabricator FOIL-THICKNESS catalogue (standard 100/120/125/150 µm foils named).
    What is STILL filed:
    PASTE-VOLUME optimisation (aperture area/shape
    reduction rules per pad size — a stencil-house recipe, not one fixed expansion), WINDOW-PANING /
    aperture segmentation of large apertures (a big thermal pad's paste is broken into a grid so it
    does not slump). FINE MASK TENTING control beyond the tented/opened via policy (per-via
    tenting, a mask dam width); and a LOWERCASE silk font (v1's `SilkFont` covers uppercase + digits +
    punctuation, so a value's lowercase advances as a blank). GERBER X2 has LANDED opt-in — the
    `%TO.N,<net>*%` object attribute (a fab's net-compare datum) on each copper object, a
    `%TF.GenerationSoftware%` file attribute, and a copper layer's `%TF.FileFunction,Copper,L<n>,<side>%`
    role, via `Generate`/`Write(..., includeX2: true)`, off = byte-identical, the reader ignoring
    attributes so an X2 file round-trips its copper exactly. The per-Gerber `FileFunction` now reaches
    EVERY layer, not just copper — `Soldermask,<side>` / `Legend,<side>` / `SolderPaste,<side>` for the
    mask / silk / paste and `Profile,NP` for the non-plated outline, threaded through `MaskLayer` /
    `PasteLayer` / `Silkscreen` / `Outline` via a `NonCopperFileFunction` helper so the same
    `GerberBuilder` emits every role and the whole package is self-describing and matches the `.gbrjob`
    manifest (off = byte-identical on the non-copper files too, with a mask round-trip beside the copper
    one). Each COMPONENT PAD flash on a copper layer also carries the X2 `%TO.C,<refdes>*%` and
    `%TO.P,<refdes>,<pad>*%` ASSEMBLY attributes (the copper tied back to its component pin), the identity
    looked up by the feature SOURCE (`"R1.1"` = `PlacedPad.Name`, no string parsing) so a via / trace /
    pour carries none. Each copper APERTURE also declares its `%TA.AperFunction` role
    (`SMDPad,CuDef`/`ComponentPad` by pad kind, `ViaPad`, `Conductor`, `Profile`), which changed aperture
    DEDUP to key on (shape, function) — a via pad and a trace of the same diameter but different role split
    into two D-codes under X2, while OFF the function collapses so dedup stays by shape and the file is
    byte-identical (the discriminating test is a Ø0.3 via-vs-trace collision, one `%ADD` off / two on; a
    pour region-FILLS so has no aperture, no `%TA`). A mask WINDOW and a paste APERTURE over a component
    pad also carry the `%TO.C`/`%TO.P` assembly datum, looked up by the opening's own `Source`
    (`MaskOpening`/`PasteAperture` already carry it — an AOI/SPI datum), the writers staying layer-clean by
    taking the pad identity as a plain tuple. A silk refdes/value/courtyard stroke also carries the
    `%TO.C` of the component it marks (`SilkStroke.Source` IS the refdes, so no lookup; a generic Mark
    carries none), `.C` decoupled from `.P` in the writer so a silk stroke gets `.C` without a spurious
    `.P` — so the X2 OBJECT attributes are now complete across EVERY layer. STILL FILED are the X2 `%TA`
    aperture functions on the mask / paste (less standard for a non-copper aperture). The JOB FILE has
    LANDED — `Write(..., includeJobFile:
    true)` drops `<name>.gbrjob`, the JSON manifest a modern fab reads (board size/thickness, copper
    layer count, surface finish, and every Gerber file with its `FileFunction` — the roles gathered from
    the whole set), deterministic (no CreationDate/GUID salt, a byte fixed point) and opt-in (off =
    Gerbers byte-identical); the oracle is that every listed file was actually written. A
    Gerber IMPORT of a foreign board is a different project (this is EXPORT; the reader is the
    round-trip oracle scoped to what the writer emits). Full topological push-and-route INSIDE the maze
    search (the router shoving DURING A*, not just the standalone `ShoveRouter` primitive) is the
    remaining routing stage (LENGTH MATCHING, differential-pair ANALYSIS + skew matching, SHOVE
    insertion, COUPLED routing, and the DIFF-PAIR-AWARE DRC for TIGHT intra-pair gaps have LANDED —
    `LengthMatch.Tune`/`MatchGroup`, `DiffPair`/`DiffPairs.Check`/`MatchSkew`, `ShoveRouter.Insert`,
    `CoupledRouter.Route` generating a pair as the two parallel offsets of a centre-line, and
    `DrcRuleSet.MinDiffPairGap` + `PcbDrc.Check`/`Violates` taking an optional named-pair list so a
    pair's two nets are checked at the tighter intra-pair floor while each half still clears everything
    ELSE at the general clearance and a short within the pair is still a short — all DRC-gated, and with
    no pairs named the DRC is bit-identical; see CLAUDE.md's ECAD status, design.md §6d,
    `examples/ecad-routing.md`). A CONFORMAL
    mask/silk/paste on a doubly-curved MID wall is refused for the tamper-mesh distortion reason (the
    MID/surface side's territory).
  - **IPC-D-356A netlist follow-ups (filed; the netlist itself has LANDED — `PcbIpc356`, see
    CLAUDE.md's ECAD status, design.md §6d, `examples/ecad-fabrication.md`; wider net-name/refdes fields
    via a `379` continuation record, per-inner-layer blind/buried-via access spans via an `L<from>-<to>`
    token, and CONDUCTOR (op `378`) records — one per routed trace, opt-in via
    `Write(layout, includeConductors: true)`, carrying the net + copper layer + width + centre-line path
    for a more thorough net-compare, with conductors OFF byte-identical and `ParseFile`/`ParseConductors`
    reading them back exactly; and the netlist now RIDES IN the fabrication package —
    `PcbGerberExport.Write(layout, dir, includeNetlist: true)` drops `<name>.ipc` beside the Gerber set,
    opt-in so the Gerber / drill files stay byte-identical — have since LANDED too):** An MCP export tool
    surfacing the fab package (the library `Write` overload exists; wiring it into the MCP tool surface
    is plumbing).
  - **Pick-and-place follow-ups (filed; the centroid file + a CONFIGURABLE BOTTOM-FLIP AXIS have LANDED
    — `PcbPickAndPlace`, see CLAUDE.md's ECAD status, design.md §6d, `examples/ecad-fabrication.md`;
    `Compute`/`ToCsv`/`ToPos`/`Write` take a `BottomFlipAxis` (X = default = `360 − rot`, the prior
    emission; Y = `180 − rot`, the other machine convention), both a sign swap so a quarter turn stays
    exact):** MULTI-VALUE / VARIANT P&P (a do-not-populate mask, or a configuration's per-variant values
    — needs an assembly-variant concept the layout does not yet carry, and would ride `Configurations`
    when it reaches ECAD). SEPARATE top/bottom centroid FILES have LANDED — `PcbPickAndPlace.WriteBySide`
    drops one CSV + `.pos` pair per POPULATED side (`<name>-top-pos.csv`/`.pos` and the `-bottom-` pair),
    a PARTITION of the same `Compute` rows filtered by side (nothing re-projected), a single-sided board
    yielding exactly one pair; the oracle is that the union of the two side files' parsed rows is the
    combined file's pose for pose. EMBEDDED-part handling (v1 emits an
    embedded placement by its 2D pose like any other; a buried die is not surface-placed, so a real
    assembly line would filter it — a scope decision, not a defect).
  - **Copper-pour follow-ups (filed, the pour + POUR PRIORITY have LANDED — see CLAUDE.md's ECAD
    status, design.md §6d, `examples/ecad-pcb.md`; `CopperPour.Priority` fills higher-priority pours
    first and carves lower-priority different-net pours around them, so overlapping pours no longer
    short — ties break by declaration order, and single / non-overlapping pours are unaffected):**
    CUSTOM RELIEF geometry
    beyond the four-spoke default (a different spoke count is a parameter, but a non-radial relief —
    a solid-with-notches, a keep-out-shaped relief — is filed); a POUR that clears other-net copper
    at the acute-angle rule's own default 90° threshold robustly (thermal-relief spokes meet the
    plane at ~90° corners, borderline on the strict-`<` test — a splayed/filleted spoke, or a
    thermal-relief-aware DRC exemption, would let the default rule pass); and a CONFORMAL pour on a
    doubly-curved MID wall (refused for the tamper-mesh reason — `MeshLocalParam`'s 2–5% distortion
    would land in the clearance).
  - **TEARDROPS (drill-breakout relief at trace-to-round-pad / trace-to-via junctions) — LANDED**
    (`TeardropSettings`/`TeardropBuilder`; `layout.WithTeardrops()`; docs `examples/ecad-pcb.md`
    (Teardrops), design.md §6d, README): same-net tapered copper at each junction, DERIVED by
    `FromLayout` tagged with the TRACE's source (a connector, not a terminal — pad count unchanged),
    off = byte-identical, DRC-gated against OTHER-net copper (dropped if it would violate, so a clean
    board stays clean), round pads/vias only, persistence a fixed point. **The geometry finding that a
    first attempt got WRONG is recorded in design.md §6d**: the naïve straight chamfer from the pad's
    perpendicular diameter to the trace edges lies ENTIRELY INSIDE the pad∪trace for a trace ending at
    the pad centre and adds ZERO copper; the correct shape is the CONVEX HULL of the pad disc (sampled)
    and the two trace-edge points, which fills the concave corners OUTSIDE the pad — with the oracle
    that the teardropped layer's union AREA strictly EXCEEDS the plain one (a no-op teardrop fails it).
    Filed follow-ups: a true tangent-ARC (exact) teardrop instead of the sampled hull, curved (vs
    straight-flank) teardrops, and teardrops on a MID surface.
  - **MID / LDS 3D surface routing — LANDED (stage 9), now INTRINSIC (works on ANY surface)**
    (`MidSurface`/`SurfacePoint`/`LocalExpChart`/`MidBoard`/`SurfaceTrace`/`MidRouting`/`Mid3dDrc` in
    `EngrCAD.Ecad`; docs `examples/ecad-mid.md` incl. the `ecad-mid-wearable` self-verifying showcase
    render, design.md §6d stage 9, README, CLAUDE.md ECAD status): routing conductors and seating
    components on a MOULDED, doubly-curved surface — a torus, a bumpy blob, a whole closed shell, NOT
    one exp-map chart. A `MidSurface` wraps an arbitrary mesh and answers the routing intrinsically with
    LOCAL charts per query (`Locate` snaps a pad/seat to a `SurfacePoint`, `Chart` is a per-pair
    `LocalExpChart` with the forward `SurfacePoint`→(u,v) map the DRC needs), so a CLOSED surface no
    longer wraps (a global chart there read `MaxDistortion` 22.5; the intrinsic torus routes and
    verifies clean). The clearance is a GEODESIC surface distance, certified both ways (a 3D chord is
    never longer than a geodesic → a chord ≥ clearance PROVES CLEAR; a closer pair is measured in a
    tight local chart with the grow-and-intersect and the local band folded, refusing a degenerate band
    as "too curved to certify"). Traces are laid as GEODESICS (`MidRouting.Connect` = the Dijkstra edge
    path then a straightest-geodesic curve-shortening smoothing). The DEVELOPABLE bit-for-bit oracle is
    PRESERVED (`MidBoard.OnSurface`, unchanged): a cylinder's 3D DRC still equals its unrolled flat
    sheet's to the last bit, and the intrinsic route reaches the same answer to the discretisation grade
    (0.07474 vs arc 0.075); a sphere geodesic matches `R·θ` within [0.98,1.10]. A component seats at a
    world position or a (u,v), a raw `Shape` body too (the showcase's MCU/LEDs/connector/passives).
    Findings baked into CLAUDE.md/design.md: the exp map measures the geodesic ACCURATELY along the
    separation (radial coord = geodesic), so the intrinsic DRC is CONFIDENT at board scales and refuses
    only where the clearance is comparable to the curvature radius — the naive "curvature shrinks the
    clearance" is INVERTED (geodesic ≥ chord always). **The surface AUTO-router LANDED**
    (`SurfaceRouter`/`SurfaceRouteOptions`/`SurfaceRouteResult`, `MidRouting.Route` — the geodesic
    analogue of the flat `PcbRouter`): each unrouted net decomposed over an MST and routed as a DRC-aware
    A* maze search over the mesh VERTEX GRAPH (admissible 3D-straight-line heuristic), straightened, and
    committed only after the exact 3D DRC (`Mid3dDrc.RouteCandidateClears`) certifies it clean — the
    vertex graph accelerates, the exact DRC is the source of truth, a boxed net is UNROUTABLE by name and
    the partial board is always clean. The obstacle model over-blocks safely (the 3D chord is a lower
    bound on the geodesic; a HALF-longest-edge margin makes the raw edge path clean by construction), and
    rip-up-and-reroute is the flat router's negotiated congestion verbatim. Runs on an INTRINSIC board
    (`OnMesh`); a global-chart board is refused with a pointer to `OnMesh`. Verified to the flat router's
    bar: a 2-pin net on a cylinder and a sphere cap routes clean+connected, several nets route AROUND each
    other, a congested board is completed by RIP-UP, a walled-in pin is unroutable by name, a dense knot's
    partial is always clean, the cylinder's routed CONNECTIVITY matches the unrolled flat board's (both
    clean, not bit-identical), and two runs are deterministic vertex for vertex; the wearable showcase now
    AUTO-routes (its PNG re-rendered). **MULTI-SHELL MID landed** (`MidStack`/`SurfaceVia` in
    `MidShell.cs`): an outer `MidBoard` plus inner shells, each the outer mesh offset inward by a
    dielectric thickness along its ANGLE-WEIGHTED vertex normal (same topology, so a through-shell via ties
    an outer point to its corresponding inner point). Each shell is its own board with its own exp map, so
    the per-shell DRC / routing runs unchanged; the multi-shell DRC adds only via-to-via spacing + the
    cross-shell ratsnest (a via's clearance to other-net copper on both shells falls out, since a via pad
    IS copper on its shell), and `Connectivity` ties a net across shells via the barrel (a single-shell
    stack is bit-identical to `Mid3dDrc.Check`). The developable oracle is EXACT (a cylinder's inner shell
    concentric at r−t to 8.9e-16, rim included); an inward offset self-intersects only where the surface
    is CONVEX and t exceeds the local radius, caught by a fold test (developable inversions) + a
    signed-volume sign flip (a sphere turning inside out — the fold test can't see it, since a uniform
    inward offset is a uniform scale and a cross product is inversion-invariant). **CROSS-SHELL
    AUTO-ROUTING landed** (`CrossShellRouter`/`MidRouting.Route(stack, …)`) — the surface analogue of the
    flat router's layer-changing via: one A* over the union of both shells' vertex graphs plus VIA EDGES
    tying corresponding vertices `(k, v) ↔ (k±1, v)` chooses which shell each segment rides and drops a
    through-shell via at the transition (trivial to enumerate because the shells share topology); the exact
    multi-shell DRC certifies every commit (per-shell trace + per-shell via-pad clearance + via-to-via web),
    so a same-shell net gets NO via, a cross-shell 2-pin ONE, an obstacle hop TWO, and the single-shell
    router stays bit-identical (a new file + a new `Route(MidStack)` overload). **Filed follow-ons**:
    TOPOLOGICAL / SHOVE routing on the surface (v1 detours around obstacles but does not push them),
    OPTIMAL via minimisation (v1 uses a fixed via penalty), PARTIAL-SPAN vias for a > 2 shell stack (v1
    routes a two-shell stack with full-stack vias), LENGTH MATCHING, a curvature-reach check for an OPEN
    convex cap over-offset, and a conformal surface SOLDER MASK / POUR (refused for the distortion reason
    copper pours already refuse curved walls).
  - **ECAD thermal coupling — the genuinely novel MCAD answer, the next stage over the
    enclosure fit that just landed.** Enclosure fit is DONE (`Enclosure`/`EnclosureFit`/
    `PanelCutout`/`FitReport` in `PcbEnclosure.cs`; docs `examples/ecad-enclosure.md`, design.md §6d
    stage 7, README, CLAUDE.md ECAD status): board-fits (closed-form outline vs cavity walls),
    component-clash (the landed `MeshIntersection.Crosses`, transversal so a seated part is not a
    clash), connector↔panel-cutout alignment, closed-form lid clearance/headroom, and keep-out
    volumes (surface crossing OR winding containment). What stays open here is the HEAT: per-component
    power dissipation as a volumetric `Generation` load into the landed thermal solver, conducting
    through board and standoffs into an enclosure with convective faces. It is a SEPARATE stage
    because it wants its own analytic oracle rather than a geometric one — verify against a
    uniformly-dissipating board conducting to a fixed-temperature edge (a closed-form temperature
    rise) before any real design; drawings and BOM already exist. Filed beside it: airflow/CFD
    cooling, snap-fit/screw-boss detailing, tolerance stack-up, and an exact round-cutout corner-fit
    check (v1 checks a round cutout against its bounding box).
  - **Interchange, in value order** (import first, since without it there is nothing to fit):
    **IDF 4.0** (board outline, placements, keep-outs; plain text, spoken by nearly every
    ECAD tool, and it carries exactly the geometry subset) — has LANDED (`IdfReader`/`IdfWriter`);
    **KiCad `.kicad_pcb`** whole-board IMPORT (open, S-expression) has LANDED too
    (`KiCadPcbReader`/`KiCadPcb`, the board twin of the component reader: the pads' own `(net ...)`
    tags reconstruct the schematic, no additive board-type change needed, connectivity/DRC/Gerber
    verified — see design.md §6d, docs `examples/ecad-pcb.md`); **EXPORT of our board to
    `.kicad_pcb` has LANDED too** (`KiCadPcbWriter` — the reader IS the oracle: write→read→write
    is a byte fixed point, earned by numbering nets in the reader's own pad-encounter order;
    embedded/inner-seated placements and free mounting holes refused by name, un-carried state
    reported), with custom pad primitives, differential-pair/length-tuning metadata, and
    rule-area / keepout zones still filed; then → **STEP
    AP214 board assemblies** (the writer, reader and assemblies exist, so mostly a mapping)
    → IPC-2581 and ODB++ (richer, heavier; filed behind the first two). And for the
    connectivity side, a **KiCad schematic/netlist** import so a code-defined schematic can
    ingest an existing design (the LIBRARY half — a single component's `.kicad_sym` symbol +
    `.kicad_mod` footprint via `ComponentLibrary`, AND an **Eagle `.lbr`** library via
    `EagleLibraryReader` (XML over `System.Xml.Linq`, the deviceset's `<connect>` map unifying
    symbol pins and package pads by pad number) — has LANDED. **The 3D model became the trinity's
    THIRD first-class view** (`ComponentModel3D`/`ModelPlacement`, `PartDefinition.Model`; a body
    SOURCE — a FILE reference that travels as data and loads on demand, or a `Func<Shape>` code
    model — unified with an offset/rotate/scale placement in the footprint frame; the KiCad
    footprint `(model …)` becomes a `FromFile` reference). **Whole KiCad `.kicad_sch` schematic
    import has LANDED** (`KiCadSchReader`/`KiCadSchematic`, the schematic twin of `KiCadPcbReader`,
    reconstructing the netlist from the wire GEOMETRY with a union-find over the connection points —
    since a schematic never lists its netlist, it draws it — sharing the `.kicad_sym` reader's
    symbol-parsing core; see design.md §6d, docs `examples/ecad-library.md`). **Hierarchical /
    multi-sheet import has ALSO landed** (`KiCadSchReader.ReadProject(rootPath)` /
    `ReadProjectFrom(rootFile, sheetsByFile)` — the flat union-find generalised with an instance
    dimension, cross-sheet stitching by sheet-pin ↔ hierarchical-label name match, global/power
    spanning and local scoping, hierarchical refdes, recursion refused / missing subsheet reported).
    **Single-sheet BUS import has ALSO landed** (`bus` wires, `bus_entry` rips and bus-VECTOR labels
    `DATA[m..n]` → members DATA{m}..DATA{n}; the honest finding is that a ripped tap's net is its OWN
    local label and same-named labels are already one net by local-label equivalence, so on a flat
    sheet the bus's connecting role is subsumed and the bus model's job reduces to declaring the
    member namespace — so a bus-vector label is not mistaken for a signal net — and validating the
    taps; verified by the member partition + a relabel mutation, reversed-range parsing, and dangling
    / non-member / bad-range reports). **Anonymous bus GROUPS have LANDED** — a `{A B DATA[0..1]}` label
    expands to its whitespace-separated members (each a bare signal OR a bus vector expanded in turn),
    declaring the namespace exactly as a vector does so the tap validation and connectivity
    reconstruction are unchanged; verified by the member partition (bare signals + a vector token), a
    non-member tap reported by name, and a nested group refused by name. **Named bus ALIASES have LANDED
    too** — a `(bus_alias "PCI" (members A B DATA[0..1]))` builds an alias TABLE, and a bare label
    matching an alias is read as a bus declaring those members (each a bare signal OR a vector expanded);
    so the single-sheet bus import now handles VECTORS, anonymous GROUPS and named ALIASES. **Buses
    ACROSS sheets have LANDED too** — a BUS sheet pin (a sheet pin whose name is a bus, resolved through
    the parent's alias table falling back to the child's) is kept OUT of the signal machinery and matched
    with the child's hierarchical BUS label of the same name; the stitch is MEMBER-BY-MEMBER (for each
    member M, the parent's local net named M joins the child's — only local labels need it, global/power
    already span; a member unused on one side stitches nothing). Per-sheet tap validation reuses the flat
    `ValidateBuses` generalized with an `instance` parameter whose flat value 0 IS the flat `Intern(p)`
    (bit-identical). Verified by the cross-sheet member partition (DATA0 spans, DATA1 spans, the members
    never short) + the rename mutation (the child's hier bus label renamed off the port splits the
    members, both dangling directions reported by name). The bus import is COMPLETE, and the DRAWING side
    is too — `SchematicBus.Group(label, members, path, …)` draws a group / alias bundle (arbitrary label
    text, explicit member names) beside the vector form, riding the same thick-pen / entries /
    Verify-exempt machinery (the vector constructor unchanged and byte-identical).
    **Multi-unit symbols merge** now (a `PartDefinition` gains `Units` — one `Symbol` per unit, `Pins`
    the union — and `KiCadSchReader` merges the same-refdes `(unit N)` instances into ONE component,
    placing each unit's pins at its own location; single-unit / symbol-less definitions are
    byte-identical, persistence writes a `units` key). **Multi-unit schematic DRAWING has LANDED** —
    `SchematicPlacement` keys poses by (refdes, 1-based UNIT), so `Place(refdes, pose)` places unit 1 (the
    whole single-unit API unchanged and byte-identical) while a multi-unit part places EACH unit at its
    own sheet location; `SchematicSheet` draws one symbol per unit (labelled `U1A`/`U1B`/…, the value once
    under the first unit), resolves each pin to the unit whose symbol carries it, and the connectivity
    reconstruction is UNIT-AGNOSTIC (it reads the drawn wire geometry), so a net across two amp units of
    one package draws as two symbols wired together and `Verify()` reconstructs it as one net; a
    multi-unit part with a unit unplaced is refused BY NAME (`U1B`). **De Morgan / alternate unit BODIES
    are now CARRIED** — the `_1_2` (`unit_style` 2) sub-symbols are collected per unit in parallel and
    built into each unit's `Symbol.Alternate` (same pin numbers, a different drawing) rather than
    discarded, round-tripping through the schematic file write-only-when-stated (a symbol with no
    alternate saves byte-identically; one with an alternate is a save→load→save fixed point). **DRAWING
    the alternate body has LANDED too** — `SymbolPose` gained an `Alternate` toggle, and `SchematicSheet`
    draws `symbol.Alternate` (via one `EffectiveBody` helper) when a placement asks for it, with
    `AnchorOf`/`LeaveDirection` reading the pin anchor off the EFFECTIVE body (the alternate's pin anchors
    differ, so the wire follows the drawn body; a partial alternate lacking a pin falls back to the
    primary). Single-unit / primary-body drawing is byte-identical. So De Morgan is complete end to end
    (carried, round-tripped, and drawable). **Whole Eagle `.sch` import has LANDED**
    (`EagleSchematicReader` — the KiCad importer's structural OPPOSITE: an Eagle net DECLARES its
    `<pinref>` terminals, so the import is a resolution not a geometric reconstruction; parts resolve
    through the schematic's embedded libraries via the shared `ReadLibraryElement`, a pinref resolves
    by symbol-pin NAME first — `pin="VCC"` lands on pad 8, the discriminating case — an unloadable part
    (typically a supply symbol) is reported and skipped with its nets surviving on their own names,
    nets are global across sheets, and the `.lbr`/`.sch` readers signpost each other by name).
    **Whole Eagle `.brd` import has LANDED too** (`EagleBoardReader` — the board twin: a signal
    DECLARES its `<contactref>` terminals, so the synthesized schematic is the file's own intent
    and the strong oracle is the CHECK against it, `PcbConnectivity` confirming the imported
    traces/vias actually join the declared pads; outline chained from the layer-20 `<plain>` wires,
    elements resolving packages through the shared `ReadLibraryElement`, `MR` rotations landing on
    the bottom side, an absent via diameter taking Eagle's auto-restring rule, a signal
    `<polygon>` becoming a `CopperPour` (isolate/orphans/thermals/rank mapped, the pad-joining
    GND plane the oracle), and airwires /
    inner-layer wires reported and skipped by name — the covered copper subset is
    the two-layer board). **IPC-7351 footprint GENERATION landed** (`Ipc7351` + `StandardBodies` —
    chips ≥ 1608 metric, dual/quad gullwing, SOT-23, BGA with JEDEC lettering; fillet-goal tables
    ⚠ transcribed nominals; the zero-tolerance identity, monotonicity and the closed-gap refusal
    are the tests with teeth; the small-chip goal row, QFN/DFN, MELF, chip arrays and paste
    divisions stay filed by name).
    **The VRML (`.wrl`) reader landed** (`VrmlReader` in EngrCAD.Mesh — the VRML97 IndexedFaceSet
    subset through the Transform/DEF/USE/Switch/LOD scene graph, coordinates verbatim with the
    KiCad 2.54 unit convention applied at `ComponentModel3D`; `.wrl` now loads through
    `MeshReader`/`Shape.From` too). **3D-model residuals, filed by name** (each RECORDED as a
    reference but not loaded): **IGES (`.igs`/`.iges`) 3D-model loading** (an IGES import
    is a face soup needing `ShapeHealing`, the 3-step read→heal→`Shape.From`), and **FETCHING an
    Eagle managed-library 3D model from Fusion's cloud** — the `<packages3d>` BINDING itself has
    landed (the URN, its bound packages and the device's `<package3dinstance>` are read as data,
    and a `ComponentModel3D` is attached whenever a caller's resolver finds a LOCAL file), so what
    remains is only the network half: a reader that fetched would make an import
    non-deterministic and unavailable offline, and the URN is exactly the key a caller needs to
    resolve it themselves. **Gerber/Excellon are FABRICATION formats** — copper artwork
    for a photoplotter, not a solid model — named here so nobody reaches for them thinking
    "PCB format"; the AUTOROUTER's output, however, does export to them, since that is what
    a fab house consumes.
  - **Verification bar, in the house style and higher than usual because ECAD fails
    plausibly**: an IDF round trip that is a byte fixed point; a schematic save→load→save
    fixed point with the pin-to-net counting identity; a DRC with a known violation found
    and a near-miss passed, measured against the closed-form gap; a routed net asserted to
    connect AND pass DRC; a MID 3D DRC on a cylinder agreeing bit-for-bit with the unrolled
    2D DRC; a board-in-enclosure clash found and a near-miss not; and a thermal case with an
    analytic answer. Every number in the design record, as the structural and thermal
    solvers did.
  - **Honest sequencing, each step independently useful** (the test of whether a domain
    belongs here): code-defined schematic + netlist → board + components as an `Assembly`
    (IDF/KiCad import feeds it) → PCB positioning constraints over the landed solver → 2D
    copper DRC → grid autorouter with DRC costs → panel cutouts and enclosure fit → thermal
    coupling → MID/LDS surface routing and 3D DRC. Stop at any point and what exists earns
    its keep. The load-bearing early decision is the one-declaration-produces-both rule; get
    that wrong and every later stage inherits two drifting sources of truth.

- [ ] **Schematic sheet follow-ons — a good auto-placer and an obstacle-avoiding wire router.**
  The drawn schematic SHEET ✅ LANDED (`SchematicSheet`/`SchematicDrawing`; docs
  `examples/ecad-schematic-sheet.md`, design.md §6d, CLAUDE.md ECAD status): placed symbols,
  orthogonal (Manhattan) wires with junction dots, net LABELS for rails, refdes/values, a title
  block, to SVG/DXF/PDF, a deterministic VIEW of the graph whose `Verify()` proves it joins
  exactly the pins the netlist connects. What it deliberately left, each a genuinely separate
  problem rather than a gap: **(a) a real auto-placer** — v1 hand-places (`SchematicPlacement`)
  and `Grid` is a labelled stand-in; a *good* layout (a graph/force-directed placer honouring
  net proximity and readability) is its own project, so it is refused-by-placeholder rather than
  invented. **(b) an obstacle-avoiding wire router** — the v1 trunk/L router may cross a symbol
  or another net (a crossing is not a connection), and routing that lays wires in clean lanes
  clear of symbols is a separate router (still NOT the copper autorouter). **(c)** hierarchical
  sheets / off-page connectors (a schematic that spans pages — bus DRAWING has since landed,
  `SchematicBus`; bus GROUPS and buses ACROSS sheets stay filed), and **(d)**
  back-annotation (edits made on the drawn sheet flowing back into the graph — but the sheet is
  a VIEW, so this is a different editing model, weighed against keeping the graph the one
  source). Also filed: power/ground *symbols* (a ground triangle, a VCC bar) in place of the
  plain text label a rail gets today, and pin name/number labelling on symbols (v1 draws the
  symbol graphics and pin stubs, not per-pin name/number text).

## Not worth adopting (deliberate)

- Raytraced/PBR rendering, a GUI sketcher, and freeform surface-modeling studios —
  each is a different product on the same data model (the "second company" argument
  the ECAD assessment makes), and each is served by mature tools a mesh export away.
  The viewer stays an engineering viewport; sketches stay code; surfacing waits for a
  need the `Shape` vocabulary cannot meet.
- g3's mesh structure itself (index+edge-list) — our half-edge with explicit boundary
  half-edges is a deliberate different choice; adopt its *editability mechanisms*, not
  the structure.
- 2D-only NURBS — we already have 3D NURBS curves/surfaces.
- g3's subdivision gap — it has no Loop/Catmull-Clark; we already have Loop.
- Skeletal-*field* convolution blends (`SkeletalBlend3d`/`SkeletalRicciBlend3d`) —
  they operate on 0..1 skeletal fields, not signed distances, and would break the
  implicit engine's sign-exactness contract.
- **Multibody DYNAMICS in the mechanisms layer** — forces, masses, friction and contact
  dynamics. Mechanisms answer "where does it go", not "what does it take", so that is a
  Simulation-layer problem and a different solver; the kinematic layer deliberately stops
  at pose. Mass properties already exist (`MeshMassProperties`/`BrepMassProperties` return
  inertia tensors about the centre of mass), so dynamics has its inputs waiting whenever
  it comes.
- **WebP animation export** — the committed animations ship as APNG (and GIF), which are
  hand-rolled over the shared `PngWriter` internals. WebP would want a VP8/VP8L encoder,
  which is not something to hand-roll: it means a dependency (libwebp or a managed port),
  and the payload win over APNG does not justify one for the docs site. Revisit only if
  APNG size becomes the pressure point.
- **VRML mesh export** — the last format the build123d "exporter breadth" survey named,
  and superseded by what already landed: VRML97's capability is a textured mesh scene, and
  **glTF 2.0** (`GltfWriter`, hierarchy-preserving, per-part PBR materials, `COLOR_0`
  result colours) covers all of it and is the format the ecosystem moved to. A VRML writer
  would be a few dozen lines and buy nothing glTF does not, so it is declined rather than
  filed. (STL/OBJ/OFF/3MF/AMF/VTU/STEP/glTF are the shipped set.)
- **build123d string selectors** (`">Z"`, `"|Z and >Y"`) — type-unsafe, and C# LINQ over
  `BrepQueries`/`BrepSelection` gives the same `sort_by`/`group_by`/`filter_by` power
  type-safely (that is what `BrepSelection` IS). Also declined from the same survey:
  build123d/CadQuery's Python-style implicit "pending" state carried between builder calls
  (confirmed worse without context managers by the builder prototype — design.md §6b), and
  the `Workplane` stack's history/rollback semantics (`FeatureHistory` already covers
  regeneration properly and with typed parameters).
