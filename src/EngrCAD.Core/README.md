# EngrCAD.Core

Foundation library shared by every engine: double-precision math types and spatial
acceleration structures. Has no dependencies and must stay free of geometry-engine and UI
concerns.

## Contents

- **Math types** (`readonly struct`, zero-allocation): `Vector2d`, `Vector3d` (implicitly
  convertible from tuples: `(1, 2, 3)`), `Matrix4d` (row-major storage, column-vector
  convention: `p' = M·p`, so `A*B` applies `B` first), `Quaterniond` (Hamilton product
  matching matrix composition order; `Slerp` shortest-arc; `FromRotationMatrix` —
  Shepperd's branch-on-the-largest extraction, so near-half-turn rotations lose no
  precision, result normalized), `Aabb`, `Ray3d`, `Interval`.
- **`Frame3d`** — right-handed rigid coordinate frame (origin + orthonormal X/Y/Z,
  Z = X × Y). World↔local maps for points, vectors, and rays; `Then` composition and
  exact transpose `Inverse`; `ToMatrix()` (column-vector convention); `Renormalized()`
  for iterated-frame drift. Factories: `FromXY` (Gram–Schmidt), `FromZX` (the STEP
  AXIS2_PLACEMENT_3D convention), and `FromNormal`, whose deterministic X is
  **exactly `Vector3d.ArbitraryPerpendicular`** — the codebase-wide perpendicular
  convention; sites deriving frames for the same axis must agree bit-for-bit or welded
  tessellation cracks (locked by tests).
- **`Tolerance`** — the central floating-point comparison policy. Kernel code never
  compares doubles with `==`; geometric predicates take a `Tolerance` (linear + angular).
- **`Predicates2d`** — adaptive-exact 2D predicates, a faithful port of Shewchuk's
  public-domain `predicates.c`: `Orient2d(a, b, c)` and `InCircle(a, b, c, d)` (plus
  `*Sign` variants) return values whose SIGN is exactly correct for all finite double
  inputs — exactly-collinear triples and exactly-cocircular quadruples yield exactly
  `0.0`. A cheap floating-point filter with a forward error bound handles the common
  case; near degeneracy the determinant escalates through Shewchuk's adaptive stages to
  an exact multi-term floating-point expansion (all temporaries `stackalloc`, zero heap
  allocation). Relies on .NET's per-operation IEEE-754 rounding with no automatic FMA
  contraction; inputs beyond ~1e150 or with subnormal differences are outside the
  analysis, as in the original. These predicates are the robustness foundation for 2D
  arrangement/sketch work (locked by tests against `BigInteger` exact arithmetic,
  including the classic Kettner ulp-grid where the naive determinant fails).
- **`Predicates3d`** — the 3D companion: `Orient3d(a, b, c, d)` and
  `InSphere(a, b, c, d, e)` (plus `*Sign`, plus `SignedVolume6`/`InSphereOriented`
  convenience forms) with the same guarantee — the SIGN is exactly correct for all finite
  double inputs, so exactly-coplanar quadruples and exactly-cospherical quintuples yield
  exactly `0.0`. That is what lets a Delaunay tetrahedralization treat a structured CAD
  grid (all eight corners of a cube are cospherical) as a *tie* rather than as noise.
  **The two exact stages are built differently, deliberately**: `Orient3d` escalates to
  Shewchuk's `orient3dexact` — expansion arithmetic in `stackalloc` spans, longest
  intermediate 96 doubles — while `InSphere` escalates to an exact INTEGER evaluation of
  the same determinant over exactly-decomposed doubles, in sign-magnitude big integers on
  `stackalloc` `ulong` buffers (pooled when coordinates spread over hundreds of orders of
  magnitude, which `InSpherePooledEscalations` counts so the pooled fixture can assert it
  still fires) — so **neither predicate allocates**. The integer form is kept over a
  transcription of `insphereexact` because that expansion form needs ~6000-component
  intermediates and several hundred lines of hand-unrolled sign bookkeeping (a liability,
  not an asset, when the integer form is *visibly* the determinant). It used to run on
  `System.Numerics.BigInteger` — measured (win-x64, Release, minima over interleaved
  runs) at **9 125 ns and 5 698 bytes per escalated call**, against **515 ns and 0 bytes**
  now (~18×) — and the reason that mattered is that **cospherical input is the NORMAL
  case for a CAD tessellation**, not the hostile one: the exact stage was 58% of a sphere
  mesh's total allocation, and `TetMesher` on a Ø20 r10 48×24 sphere went **478.7 →
  25.8 MB** allocated (a 20³ box at h = 2: 191.6 → 37.7 MB), escalation counts identical.
  Buffer sizing is a proof, not a guess (per-tier bit bounds from the determinant's
  degree, documented at the implementation), the minimum exponent is taken over NONZERO
  coordinates only (a zero would widen every operand by ~1000 bits while scaling cannot
  change the sign), and the exact stage is locked against the test-side `ExactReference`
  BigInteger ground truth — an independent cofactor expansion, so agreement is evidence
  rather than tautology — over cospherical lattice-sphere families at three scales,
  ulp-perturbed cousins, subnormal/wide-exponent (pooled-path) fixtures and zero-heavy
  configurations, plus an asserted-zero-allocation test on both paths. The filter carries
  everything that is not within its own error bound of degenerate; `InSphereEscalations`
  counts the exact stage so a consumer can report its rate instead of guessing.
  Shewchuk's expansion macros are shared with `Predicates2d` via internal
  `ShewchukExpansions` — one copy, because two copies of a numerical routine drift.
  **Note the sign convention trap**: `Orient3d` follows Shewchuk (positive when `d` is
  *below* the plane `abc`), which is **minus** six times the signed tetrahedron volume —
  hence the separately named `SignedVolume6`.
- **`Geometry2.Arrangement2d`** — planar arrangement of 2D segments: `Insert` splits
  existing edges (and the inserted segment) at proper crossings, T-junctions, and
  collinear overlaps, with all incidence DECISIONS made by the exact predicates and all
  intersection POINTS rounded to doubles under a `VertexSnapTolerance` merge (the
  standard snap-tolerance model, as geometry3Sharp's `Arrangement2d`: decisions exact,
  coordinates rounded). `ExtractCells()` walks every directed edge with the
  tightest-turn (clockwise-next-from-reverse) rule — exact angular comparisons — and
  returns `ArrangementCell2d`s: CCW outer loop, CW hole loops (disconnected island
  components, assigned to the smallest strictly-larger containing cell **from a different
  connected component** — a loop reachable from the cell's own loop would have been traced
  as part of it, so same-component nesting is structurally impossible; that rule is what
  stops a lone convex cell from adopting its own reversed perimeter as a hole when the two
  shoelace sums differ by an ULP), net `Area`;
  the unbounded face and zero-area spur loops are dropped, dangling edges appear as
  doubled-back slits. This is the same loop-tracing dance `FaceSplitter.SplitByCurve`
  performs in UV parameter space (which additionally handles periodic wrap and stays
  independent for boolean-safety); the sketch engine's region booleans build on this class.
  `BuildCcwEdgeFans()` exposes the combinatorial embedding (per-vertex incident edge ids in
  exact CCW order) so cell tracing and boundary tracing walk ONE order, plus
  `OtherVertex`/`FindEdge` for graph navigation.
- **`Geometry2.Region2d`** — polygon-with-holes region (g3: `Polygon2d`/`GeneralPolygon2d`/
  `PlanarComplex`): one outer loop + N hole loops, loops closed implicitly (never repeat the
  first point). The canonical winding is CCW outer / CW holes — the constructor re-orients
  whatever it is given and validates that every loop encloses area, that holes lie inside the
  outer loop, and that no two loops properly cross; `Reversed()` returns the mirror winding
  for consumers with the opposite convention (`IsCounterClockwise` reports which form a
  region carries). `Area` is exact shoelace (holes subtracted, anchored at the first vertex).
  **`Contains` treats regions as CLOSED sets** — a point exactly on a loop, including exactly
  on a vertex, is inside — and is EXACT for all finite doubles: the half-open upward-crossing
  parity rule with every sidedness decision made by `Predicates2d.Orient2dSign`, so no
  crossing x is ever computed or rounded. **`Region2d.FromLoops(loops)` is the automatic
  hole detector**: it sorts an unordered bag of closed loops into regions by containment
  DEPTH — even depth = an outer boundary, odd depth = a hole of its deepest container, so an
  island inside a hole becomes a region of its own — the "nested loop hierarchy" a sketch
  front door needs. Regions are polygonal by construction; curved input is flattened to a
  chord tolerance before it reaches this type. `WithoutCollinearVertices(loop)` drops
  vertices lying EXACTLY between their neighbours (exact orientation + dominant-axis
  betweenness, so a 180-degree slit reversal is never eaten).
- **`Geometry2.Region2dValidation`** — the simplicity guard every region consumer assumes and
  none of them used to check: does any pair of segments PROPERLY cross, inside one loop or
  between two? A self-intersecting outer loop is not a region at all — its "interior" depends
  on which fill rule you happen to apply, so `Area`, `Contains` and every boolean silently
  disagree — and before this, loops were checked against every *other* loop and never against
  themselves. `TryFindSelfIntersection` / `TryFindCrossing` report a `LoopCrossing` (which
  segments of which loops, plus the crossing point *for the message only* — the DECISION is
  exact `Predicates2d.Orient2dSign`, the coordinate is not); `Require` throws naming the loop
  in the caller's own vocabulary. Two rules worth knowing: **touching is not crossing** (a
  shared vertex or a collinear run stays legal, matching the convention `Region2d` already
  documented for hole-versus-outer contact), and **simplicity is checked BEFORE the
  enclosed-area test**, because a bow-tie with equal lobes has a signed area of exactly zero
  and would otherwise be refused for the wrong reason — or, in `FromLoops`, silently DROPPED
  by the zero-area filter. `FromLoops` checks only self-crossings, since its bag is unsorted
  and two loops in it are not yet known to share a region; loops that end up in one region are
  cross-checked by the constructor. Candidate pairs come from a `Bvh` over the segment boxes
  above 24 segments and from an all-pairs scan below it, so validation does not turn every
  sketch into an O(n²) pass.
- **`Geometry2.Region2dBoolean`** — `Union` / `Intersection` / `Difference` of regions (or
  region *sets*, each read as the union of its members), returning a list of canonical
  regions; empty result = empty list. Both operands' loops go into one `Arrangement2d`
  (which splits crossings and dedupes coincident edges), `ExtractCells` carves the plane
  into interior-disjoint cells, each cell is classified once by membership in A and B, kept
  cells' directed loop edges are collected, and **an edge with kept cells on BOTH sides is
  interior and disappears** — what remains is the result boundary, re-traced with the
  arrangement's own CCW fan order so kept material always stays on the left (outer loops CCW,
  hole loops CW). `Region2d.FromLoops` then re-derives the nesting, so a hole CREATED by the
  operation (square minus a centred square) is detected exactly like any other, and a
  difference that splits one region in two just yields two loops. Interior sample points are
  **clearance-based and scale-free**: for each outer-loop edge take the midpoint m and the
  distance d to the nearest OTHER arrangement edge — the open disk of radius d around m meets
  no other edge, so `m + d/2` along the inward (left) normal is strictly interior — using the
  edge with the largest clearance. No triangulation, no shrink factor, no epsilon; and since
  the cell boundary contains every operand edge, the sample can never sit on A's or B's
  boundary, so `Contains`'s closed-set convention never has to decide a tie.
  **`UnionAll(regions)`** unions MANY regions as a **balanced tree** rather than a linear
  fold: one arrangement is O(E²) in its total input, and a linear accumulate arranges every
  operand against the whole running union, whereas halving recursively keeps most
  arrangements small and lets each merge discard its children's interior edges before
  climbing. Union is associative, so the answer is identical — only the cost changes. Feed
  spatially sorted input when you have it.
- **`Geometry2.Region2dOffset`** — polygon offsetting (`Offset(region|regions, delta, join,
  miterLimit, arcTolerance)`), i.e. OpenSCAD's `offset(r=…)` / `offset(delta=…, chamfer=…)`
  and the geometry behind shells, pockets, clearances and cutter compensation. **The
  algorithm is a union, not an edge chase**: an outward offset by d is exactly
  `R ∪ (⋃ edge slabs) ∪ (⋃ corner joins)` — a point outside R but within d of it is nearest
  either to an edge interior (so it lies in that edge's slab, the rectangle swept d along the
  outward normal) or to a vertex (so it lies in that vertex's corner primitive) — and every
  primitive is a small convex polygon handed to `Region2dBoolean.UnionAll`. That is why
  offsetting had to wait for the arrangement-based boolean, and it is why **self-intersection
  is a non-issue**: there is no loop to invert, so an inward offset that eats through a neck
  simply returns two regions, or none. `OffsetJoin.Round` builds inscribed polygonal arcs
  (vertices exactly on the true offset circle, sagitta ≤ `arcTolerance`, so results sit just
  inside the true Minkowski sum — the same one-sided contract as `Sketch.ToRegions`
  flattening); `Miter` extends the two offset edges to their intersection, falling back to
  `Chamfer` past `miterLimit` (default 2, Clipper's); `Chamfer` bevels straight across.
  Straight-edge geometry is EXACT under every style — a mitered square is the larger square
  with four corners, not eight, which needs the miter apex computed from `sum.LengthSquared`
  and never from `sum.Length` squared (√2² is 2.0000000000000004, enough to tilt the apex a
  few ULPs off both offset edge lines so the collinear T-junctions stop collapsing).
  **Inward offsets are outward offsets of the complement** — `B \ dilate(B \ R, d)` with B =
  R's bounds grown by 3d — so there is no second algorithm and no special case for necks,
  islands, or holes merging. Cost is the union's: measured ~30 ms for a 16-gon and ~260 ms
  for a 512-gon outward round offset.
  **`Stroke(path, width, cap, join)`** dilates an OPEN polyline into a constant-width
  region — toolpath footprints, slots from centre lines, SVG strokes — by the same
  union: one full-width slab per segment, corner joins offered on BOTH sides of every
  interior vertex (the inner side's wedge is already inside its slabs, so only the
  outer gap changes the union — and a 180° reversal legitimately fills both, which is
  the round nose on a doubled-back path), and `StrokeCap.Butt/Round/Square` ends.
  Self-crossing paths just work (a union covers the overlap once) and a closed
  circuit (first point repeated) encloses its hole. With round caps and joins a
  stroke is the path's Minkowski sum with a disk, short only of the inscribed-arc
  sagitta; straight-segment butt/square/miter strokes are exact.
- **`Geometry2.SpaceFillingCurve`** + **`Geometry2.Morton2d`** — finite-order Hilbert /
  Moore / Peano / Gosper / Z-order curves laid over a region (`Over(bounds|region, family,
  spacing)`), plus their integer lattices (`LatticeSites`, `AreNeighbours`, `ToPlane`).
  **The name overpromises and the API says so**: a true space-filling curve is the LIMIT of
  a sequence and has infinite length, so the ORDER is the parameter — a caller states a
  *spacing* and gets the order whose cell size is at or under it, with the ACHIEVED
  `Spacing` reported beside the `RequestedSpacing` (the `BiArcFit.MaxDeviation` convention).
  **Which quantity quantises is a decision, not arithmetic**: the order comes from one
  inequality, `side ≤ spacing·radix^n`, so the surplus goes either into a finer spacing (hold
  the footprint) or a larger footprint (hold the spacing) — same order either way, so
  neither is cheaper, and the footprint is held because a curve is laid *over* a region and a
  pattern's phase must be a function of what the caller stated.
  Everything under the placement is INTEGER, which is what makes the contract testable
  without a tolerance: sites counted in closed form (`SiteCount`: 4^n, 9^n, 7^n+1) and
  pairwise distinct, consecutive sites exactly one lattice step apart (Manhattan 1 on a
  square lattice; one of six Eisenstein unit vectors on Gosper's triangular one — a DIAGONAL
  is not a step, which is why the rule is family-aware), Moore's closure asserted rather than
  trusted, and `Length == SegmentCount × Spacing` exactly. Only coverage is a measurement,
  and its bound is DERIVED from the cell's own circumradius (measured exactly √2/2 · h for
  the square families; 0.5738 · h for Gosper against the triangular lattice's 1/√3).
  **Two things are measured rather than claimed.** The longest straight run SATURATES and is
  what separates the families — 3 cells for Hilbert and Moore, 5 for Peano, 2 for Gosper, at
  every order from 3 up — so "Hilbert is the isotropic one" is a number rather than a
  reputation. And **Z-order is not a curve**: exactly `2^(2n−1) − 1` of its `4^n − 1` steps
  are not lattice steps and the largest jumps the full grid width, so it is offered as the
  bijective ORDERING it is (`Morton2d` is the same interleave `PlanarSection`'s silhouette
  fold sorts by — one copy, so a grid cannot be ordered two ways) and refused by name where a
  path is required. **Gosper is the one family placed differently and it is stated**: its
  cells are hexagons, so it fills an island rather than a rectangle and is scaled by its own
  MEASURED inradius — the nearest unvisited site's distance from the centroid less the
  lattice's covering radius, computed from the walk — which makes its achieved spacing
  markedly finer than a square family's at the same order. See
  `EngrCAD.Modeling.SpaceFillingInfill` for the toolpath consumer and `docs/examples/infill.md`.
- **`Geometry2.SpaceFillingCurve.OverTiled`** + **`Geometry2.TiledHilbertLattice`** — the
  RECTANGULAR footprint. Holding the footprint to a region's bounding SQUARE has a stated
  cost, and on a long thin plate it is most of the curve: an 80 × 12 plate at spacing 3
  generates 1024 cells and keeps 128. Tiling `blocksX × blocksY` Hilbert blocks covers the
  rectangle itself and stays ONE continuous path, because an order-n block runs between two
  ADJACENT CORNERS of its square and the eight symmetries supply whichever (entry, exit) pair
  each block's neighbours need — a boustrophedon over the block grid then links them. The
  footprint is still what is held, which is what makes the cells anisotropic: `SpacingX` and
  `SpacingY` are each their axis's extent over its cell count, `Spacing` is the larger (never
  coarser than the request), and `Anisotropy` reports the ratio. **One block reproduces the
  square form site for site and bit for bit**, and every square-footprint construction reports
  `SpacingX == SpacingY == Spacing` bit-identically, so the tiled path is a generalisation
  rather than a second mode. Hilbert only: Peano's blocks end at the same two corners and
  would tile identically (a straightforward addition), Moore is a closed LOOP with no ends to
  link, and Gosper does not tile a rectangle. `blockOrder: 0` makes every block one cell and
  the route a plain serpentine — the tightest fit and the worst isotropy, a member of the
  family rather than a degenerate case.
- **`Geometry2.SpaceFillingCurve3d`** — the VOLUME member: a finite-order 3D Hilbert curve
  over a box, for a consumer that wants ONE connected route through an interior (a
  single-extrusion print path, a single-channel cooling passage). Every convention above
  carries over — the ORDER is the parameter, the ACHIEVED `Spacing` is reported, the footprint
  is held (the box's bounding CUBE) — and so does the verification bar: `8^n` sites counted in
  closed form and pairwise distinct, consecutive sites exactly one lattice step apart
  (Manhattan 1, so a face diagonal is not a step), `Length == SegmentCount × Spacing` exactly,
  and the two terminals MEASURED to be adjacent CORNERS of the cube rather than asserted from
  the literature (which is also what would let 3D blocks tile). The walk is **Skilling's
  transpose algorithm**, chosen for the reason the 2D file gives for Peano's digit rule: a
  closed form has no orientation table to get backwards, and the bijectivity test is what
  would catch one. **Hilbert only, deliberately** — Z-order's 3D member is not a curve,
  Peano's is radix 3 (27 cells per level, so three times the spacing quantisation for nothing
  this consumer wants), and Gosper's lattice has no 3D analogue; an enum of one member would
  only invite the other three to be filled in without a caller. A PARALLEL type rather than a
  mode of `SpaceFillingCurve`, the call `CurvedRegion2d` makes against `Region2d`: the two
  share their conventions and none of their data. Consumer: `EngrCAD.Modeling.SolidInfill`.
- **`Geometry2.Region2dThickness`** — how thick a planar region is, by an OPPOSING-EDGE ray
  cast: the 2D twin of the wall thickness `Manufacturability` measures on a solid, and the
  LOCAL measure a connectivity test cannot give (whether a piece has a NECK narrower than the
  pass about to be laid through it). What is reported is the **perpendicular distance to the
  LINE of the segment hit** (`t·|d̂·n̂_hit|`), not the raw ray length — exact wherever the
  opposing boundary is straight, which for a polygon is everywhere, and what makes a tapered
  slot read its true width where the raw ray over-reports by `1/cos`. The probe starts exactly
  ON the boundary with no stand-off, because the source segment is excluded by INDEX (exact),
  and a stand-off would bias every reading low by its own length — measured, it did. Holes
  contribute their segments too, so the WEB between a bore and a wall is measured like any
  other neck. `Minimum` is the number a fill's spacing is compared against, `ThinnestAt`
  locates it, and `Mean` rides beside them and never instead (a mean says nothing about a
  neck). Cost is O(samples × edges) over the OUTLINE, so the scan is linear and there is no
  index to keep in step; the medial-axis inscribed disc is a NAMED alternative rather than a
  silent upgrade, the same call the 3D twin makes.

### The CURVED tier — `CurvedEdge2d` / `CurvedRegion2d` / `CurvedArrangement2d`

Everything above is polygonal, so curved sketch input reaches it flattened at a chord
tolerance and **everything built from a region inherits that error** — offsets, sketch
booleans, sections, silhouettes, and the `Profile`s an extrusion is built from. The curved
tier carries **lines and circular arcs through the arrangement unflattened**.

- **`Geometry2.CurvedEdge2d`** — the boundary vocabulary: a straight segment or a circular
  arc over t ∈ [0, 1], with a SIGNED sweep so orientation is intrinsic (no flag, matching
  `BRep.Arc2d`). It carries its own exact closed forms: `SignedAreaTerm` (Green's theorem
  with the arc term ½[r²Δ + cx(y₁−y₀) − cy(x₁−x₀)], so a disc measures πr² rather than an
  inscribed polygon's area), tight `Bounds` including the cardinal extremes inside the
  sweep, `NearestPoint`/`DistanceTo`, and `RayCrossings` over the arc's y-monotone pieces.
  It lives here rather than beside `Curve2d` because `Curve2d.ToCurve3d` returns a
  `Curve3d` and Core cannot reference EngrCAD.BRep; `Curve2d.TryToCurvedEdge` /
  `Curve2d.FromCurvedEdge` bridge the two exactly.
- **`Geometry2.CurvedRegion2d`** — one outer chain plus hole chains, closed implicitly by
  CHAINING (edge i's end is edge i+1's start; a single full-circle edge is a legal loop).
  Exact `Area`, canonical winding, `FromLoops` containment nesting, `ToRegion(chordTolerance)`
  down to the polygonal type and `FromRegion` up from it.
- **`Geometry2.CurveIntersection2d`** — line/line, line/arc and arc/arc in closed form.
- **`Geometry2.CurvedArrangement2d`** + **`CurvedRegion2dBoolean`** + **`CurvedRegion2dOffset`**
  — the same three algorithms as the polygonal path, with arcs surviving.

**`CurvedRegion2dOffset.Stroke(path, width, cap, join)`** is the curved twin of the polygonal
`Stroke` above, and it is where the tier's exactness becomes an *equality* rather than a
bound. Same construction — one FULL-WIDTH slab per edge, corner joins offered on both sides
of every interior joint, end caps — with both new primitives closed-form:

- a straight edge's slab is still a rectangle; an **arc's is the ANNULAR SECTOR** between
  radii r ± w/2 over the arc's own angular span, which is exactly the set of points whose
  nearest path point is interior to that arc. Its area is `(sweep/2)((r+w/2)² − (r−w/2)²)`
  = **`sweep·r·w`** — the squares cancel, which is why every test here asserts an equality;
- when **w/2 ≥ r the band swallows the centre**, the inner rim is gone, and the slab becomes
  the pie SECTOR of radius r + w/2. Still exact: every point of that sector sits at radius
  between 0 and r + w/2, so its distance to the circle is at most max(r, w/2) = w/2;
- a round cap is an exact **half-disc** and a round join an exact sector, so with
  `StrokeCap.Round` + `OffsetJoin.Round` the stroke **IS** the path's Minkowski sum with a
  disc of radius w/2 — not "short of it by the inscribed-arc sagitta", the qualification the
  polygonal twin has to carry. Measured on a quarter arc (r 8, w 3): a polygonal stroke of
  the same arc flattened to 4/8/16/32 chords approaches the curved answer strictly from
  below and is still 1e-3 short at 32, because the deficit is the inscribed geometry rather
  than a tolerance.

**A chain that returns to its start is stroked as a CIRCUIT** — the closing joint gets its
joins and no caps are added — and this is the one place the two twins' contracts differ. The
reason is the input vocabulary, not a change of mind: the polygonal `Stroke` takes POINTS,
where closure can only be spelled by repeating the first one, while a chain of edges makes
closure structural, so it is read off the same weld tier the chain's own continuity is
checked at. It changes nothing under round joins + round caps (a full disc at the closing
vertex contains the join wedge, so the two readings agree as sets) and it is what stops a
butt-capped circuit carrying a notch or a mitered one losing its last corner — measured on a
10×10 square at width 2 with miter joins, the polygonal twin returns 79 against 80, short by
exactly the 1×1 outer corner its repeated start point cannot claim a join for. A gap in the
chain is refused BY NAME with both endpoints and the gap printed; zero-length edges are
dropped, which cannot break the chain because their two endpoints are the same point.

The strongest test is not an area formula at all: **stroking a simple closed loop by w is the
same set as growing the region it bounds by w/2 and taking away the region shrunk by w/2**,
and `Stroke` and `Offset` reach it through different primitives (two-sided full-width slabs
against one-sided ones, plus the complement trick for the shrink), so agreement is two
constructions checking each other.

**Why a parallel type, and not an extension of `Arrangement2d`.** The straight arrangement
is boolean-critical: `Region2dBoolean`, `Region2dOffset`, every planar section and
silhouette and every rendered docs image sit on it, and its output is pinned bit for bit.
Teaching it curves would change its vertex fan comparator (positions → tangents), its edge
identity (a vertex pair → a vertex pair *plus a carrier*) and its area rule, three changes
at once in the code with the widest regression surface. The curved type shares the exact
predicates and the algorithms' shape instead; **the straight path is untouched** (locked by
`Region2dGoldenTests`' committed bit fingerprints). Same call design.md §5 makes for
`FaceSplitter`: do not unify boolean-critical machinery.

**Why the tier stops at arcs, and why that is a complete stopping point rather than an
arbitrary one.** The cell walk orders edges at a node by their departure TANGENT with the
departure CURVATURE as the tie-break — from p(s) = v + s·d + ½s²κ·n̂, two edges leaving
along the same d separate at second order and the larger signed curvature sits further
counter-clockwise. For lines and circles, agreeing in *both* means sharing a carrier: a line
and a circle never osculate, and two circles that osculate are one circle. So the tie-break
is complete and the walk never guesses. A third shape breaks it — two Béziers can agree to
second order and separate only in the third derivative, so the rule would need a jet of
unbounded order. Béziers are therefore flattened at the entry points (`Sketch.ToCurvedRegions`
says so), and the comparator **refuses by name** if it is ever handed a second-order tie
between different carriers.

**One tolerance, and it is a LENGTH** — the arrangement's own vertex snap distance, with no
second epsilon anywhere in the tier. A line is tangent to a circle when the centre's
distance from it differs from the radius by less than that; two circles are tangent when
their centre distance differs from r₀ + r₁ (or |r₀ − r₁|) by less than it; a point is on an
edge when its distance to the edge is under it. Every one of those is the same resolution at
which the arrangement can tell two vertices apart, so nothing finer could be represented.

**Near-tangency SNAPS rather than refusing.** A discriminant inside the band is reported as
ONE touch point, not as two near-coincident crossings and not as a miss. Both alternatives
are unstable — a pair of crossings a nanometre apart is a degenerate sliver cell whose
classification is decided by rounding — while snapping is area-neutral to O(τ^1.5) and
always yields a valid arrangement, *because a tangential contact is representable here*: the
two edges leave the node with equal tangents and different curvature, which the fan can
rank.

**The interior sample gains one thing the straight proof did not need.** Classification
still takes the boundary edge midpoint with the greatest clearance and pushes half of it
along the inward normal, but the push is also capped by the edge's own CURVATURE RADIUS.
Without that cap a small circular hole inside a large cell sends the sample straight past
the circle's centre and out the far side; with it the pushed point sits at |r ∓ s| from the
centre with 0 < s < r, so it is off the carrier circle by exactly s and off every other edge
by more — the same proof the straight case gives with an infinite radius. Classification
itself is the epsilon-free `ParityInside`, not the closed-set `Contains`, whose on-boundary
band would answer "inside both" for a cell thinner than the weld tolerance.

**Offsets are exact.** A circular edge's slab is an ANNULAR SECTOR (the edge, two radial
segments, the offset arc), degenerating to a pie slice of radius r when an inward offset
reaches the centre — both exactly the set of points within d of that arc on its outward
side. A round join is a circular SECTOR, which **retires** the inscribed-arc contract rather
than honouring it: an exactly-offset arc is neither inside nor outside the true offset, it
IS the true offset. A full-turn edge is halved before a slab is raised, because a whole
circle's annular sector is an annulus — a region with a hole, not a simple loop. A
tangent-continuous joint (a line meeting an arc) raises no join primitive at all: its two
outward normals are equal, so the exact-zero cross test that already skipped straight-through
vertices skips it too, and a stadium offsets to a four-edge stadium.

**Numerical lesson: at a tangency the departure DIRECTION is round-off, so an exact
predicate on it is confidently wrong.** The fan comparator sorts by the exact
`Orient2dSign` of the two departure directions — and where two edges are tangent at the
node, those directions differ only by arithmetic noise. Measured: a disc tangent to a
plate's straight edge from outside gave the arc a departure of `(−1.22e-16, −1)`, whose x
sign is nothing but the error in `sin(π)`, which put it on the wrong side of the plate's
exactly vertical edge; the tightest-turn walk then closed **no face at all** and the union
came back EMPTY. Only the curvature carries information there, so a second pass
(`OrderTangentialRuns`) re-orders each cyclic run of tangentially tied departures by
curvature. **The tie band is derived, not chosen**: a vertex may sit up to the snap
tolerance from the true tangency point, and displacing a point by δ along a circle of
radius r rotates its radial (hence its tangent) by δ/r = δ·|κ| — so the band is
`snap·max(|κ₁|, |κ₂|)` plus a few-ulp arithmetic floor, which vanishes for two straight
edges and leaves genuinely distinct line directions decided exactly. Runs are walked
CYCLICALLY, because a tangency along the +x axis puts one departure at the very start of
the fan and its partner at the very end. This is the same shape of finding design.md §5
records for `FaceSplitter.DepartureAngle`: **Shewchuk exactness is exactness about the
coordinates you hand it, and a tangent computed at a tangency is not one of them.**

**Numerical lesson: a full-turn arc's END is its START, exactly.** Evaluating the end angle
instead lands ~2e-16·r away, because `sin(2π)` is not 0 in doubles — and that gap is not
cosmetic. A +x parity ray whose ordinate falls inside it counts the seam piece's two
endpoints on opposite sides, and a point measurably inside a disc reads as OUTSIDE (found by
a disc∩rectangle that returned empty). The companion rule is that an arc's first and last
y-monotone piece take their ordinate from the STORED endpoints, never from the angle, so a
chain's parity is consistent across every joint.

  boundary, so `Contains`'s closed-set convention never has to decide a tie. The clearance
  comes from a **`Bvh` over the arrangement's edges built once per boolean** (edges embedded
  at z = 0, so the branch-and-bound's box distance is exactly the 2D one), replacing a
  per-loop-edge linear scan that made classification O(E²) — **bit-identical**, because only
  the minimum DISTANCE is used and a minimum over doubles does not depend on visit order.
  Measured on a union of 120 overlapping 32-gons (7 776 arrangement edges, 1 969 cells):
  classification 367.7 ms → 8.8 ms, whole union 436.2 ms → 93.6 ms.
- **`Spatial.Bvh`** — static bounding volume hierarchy (median split on the longest
  centroid axis, flat node array, allocation-free stack traversal). The build sorts the
  item permutation per node through a **contiguous key array** (`Array.Sort(double[],
  int[])`) rather than an `IComparer<int>` over scattered centroids, and **forks sibling
  subtrees onto the thread pool** above 4096 items (capped at ~2^(log2 cores + 1) tasks so
  a nested caller cannot flood the pool); a canonical renumbering pass replays the
  sequential node numbering afterwards, so **the tree is bit-identical to the original
  builder's** — item permutation, node ranges and bounds — and independent of scheduling
  (locked by fingerprint tests in `BvhBuildOrderTests`, which every future builder rewrite
  must reproduce). Measured on 8 cores: 32 400 triangle boxes 22.6 ms → 4.6 ms (4.9×),
  130 000 random boxes 142.5 ms → 33.6 ms. Queries (all zero-allocation per query, results
  appended to caller-provided lists):
  - box overlap and ray candidate queries (`Query`);
  - `QueryAll(ray, List<BvhRayHit>)` — every item whose box the ray passes through,
    ordered by ascending box entry t (ties by item index). Collect-then-range-sort:
    measured ~1.3× faster than a strict best-first PriorityQueue traversal even with a
    reused heap (which a thread-safe API could not cache), and candidate lists are small;
  - `QueryOverlap(other, List<(int, int)>)` — tree-vs-tree broad phase: all item index
    pairs whose boxes intersect (same coordinate space; self-query yields self-pairs and
    both orderings). Candidate pairs only — exact primitive–primitive intersection (e.g.
    triangle–triangle segments for imprint booleans) belongs to the layer owning the items;
  - `Nearest` — branch-and-bound with a caller-supplied exact distance. Two forms: a
    `Func<int, double>` convenience (allocates the caller's closure) and the generic
    `Nearest<TMetric>(in point, ref metric, …) where TMetric : struct, IBvhDistance`
    (constrained call — no boxing, no closure; bit-identical traversal). Hot paths
    (`MeshSdf.Evaluate`) use the struct form.
  - **Tree-order exposure**: the median-split build sorts the item permutation in place,
    so every node owns a contiguous range of `ItemsInTreeOrder`; `Root`/`NodeView`
    (bounds, `IsLeaf`, `First`/`Count`/`Items`, `Left`/`Right`, dense `Index` for
    parallel per-node side data, `NodeCount`) let consumers permute bulk SoA data into
    tree order once and run range scans per node instead of forking their own hierarchy
    (what `MeshWindingNumber` had to do before this existed).
- **`Spatial.Octree`** — dynamic octree for incrementally changing content
  (insert/remove/query); prefer the BVH for static geometry.
- **`IndexPriorityQueue`** — array-backed binary min-heap keyed by non-negative integer
  ids with an O(1) id→slot index, so `Update` (decrease/increase-key), `Remove`, and
  `Contains` work directly — no lazy stale-duplicate entries. Grows on demand; SoA
  storage; modeled on geometry3Sharp's `IndexPriorityQueue`. Used by `MeshDecimator`.
- **`ProgressCancel`** — cooperative cancellation (from a `CancellationToken` or any
  `Func<bool>`, sticky once observed) + coarse progress reporting for long operations,
  taken as an optional trailing `ProgressCancel? progress = null` parameter (zero
  overhead when absent). Cancellation surfaces as `OperationCanceledException`; kernel
  algorithms never return partial geometry. Wired into `SurfaceNets.Polygonize`,
  `MeshDecimator.Decimate`, `BRepTessellator.Tessellate` and — because it is the slowest
  single operation in the library — `SparseCholesky.Factorize` and `SparseSymmetricCG.Solve`.
- **Integer grid types** (g3: Vector2i/Vector3i/Interval1i/AxisAlignedBox3i):
  `Vector2i`/`Vector3i` (tuple conversion, operators, `ComponentProduct` as a `long`
  for overflow-safe grid sample counts, `ToVector2d/3d`), `Interval1i` (**inclusive**
  [Start, End] index interval, allocation-free `foreach`), and `AxisAlignedBox3i`
  (**inclusive** Min/Max index box: `Counts`, `Count`, per-axis `Interval1i` ranges,
  contains/overlap/intersect/expand). Adopted where they name a concept — grid
  dimension bookkeeping in `SurfaceNets` and `GridSdf`'s `GridFrame.Samples` — while
  hot flat-index arithmetic deliberately stays scalar (bit-for-bit outputs locked by
  the polygonizer determinism test).
- **`ConvexHull2`** — 2D convex hull (Andrew's monotone chain, O(n log n)); returns CCW
  strictly-convex hull vertices or indices, degrading gracefully on coincident/collinear
  input. The planar counterpart of the mesh engine's 3D quickhull. **The turn test is
  `Predicates2d.Orient2dSign`, not a raw cross product**: a chain is a sequence of
  orientation decisions that must be mutually consistent, and a naive determinant on
  points spread over a wide exponent range (where coordinate differences stop being
  exact) reports contradictory turns and pops vertices that belong to the hull —
  measured on ~7% of near-collinear mixed-magnitude clouds, and locked by
  `ConvexHull2Tests.NearCollinear_MixedMagnitudeSlivers_*`. Hull output is now verified
  against BigInteger ground truth (`ExactReference`) rather than a tolerance: strictly
  convex, enclosing every input point, exactly.
- **`PolylineSimplify`** — Douglas–Peucker simplification in 2D and 3D (g3's
  `PolySimplification2` role): `Simplify` for open chains, `SimplifyLoop` for implicitly
  closed ones, `MaxDeviation` to report what a simplification actually cost. It is the
  INEXACT companion to `Region2d.WithoutCollinearVertices`, which drops only vertices that
  provably change nothing — use this on traced intersection curves (`PolylineCurve3d
  .Simplified`), imported profiles, and any polyline whose sample density says more about how
  it was produced than about its shape. Guarantees: every dropped vertex is within the
  tolerance of the retained chord that replaced it, measured to the SEGMENT (a spike that
  doubles back along its own line is 0 from that line's extension and would vanish otherwise);
  endpoints always kept; splitting always at the farthest vertex, so it is deterministic with
  no ordering effects; and the output is a SUBSEQUENCE — retained points are bit-for-bit the
  originals. Not guaranteed: topology. Two far-apart stretches of a wiggly loop can be pulled
  onto each other, so a simplified loop may self-intersect where the input did not — which is
  why nothing in the kernel simplifies implicitly and why `Region2dValidation` catches it for
  callers who feed the result to `Region2d`. **The tolerance is absolute and in model units on
  purpose**: it is a deviation the caller CHOOSES to accept, not a degeneracy guard, and only
  the latter are relative per the epsilon ladder. The closed case cuts the loop at its first
  vertex and the vertex farthest from it (the standard anchor pair, so a near-degenerate first
  chord cannot decide the whole simplification) and never returns fewer than 3 points. The 2D
  and 3D bodies share one recursion through a struct-constrained chord metric, the
  `Bvh.Nearest<TMetric>` idiom — no interface dispatch, no boxing, and no second copy of
  Douglas–Peucker to drift.
- **`Arrangement2d` edge broad phase** — insertion used to test the new segment against
  EVERY existing edge, so a k-segment build was quadratic in exact-predicate calls (the
  workload `Region2dBoolean` feeds it: hundreds of chords per flattened loop). Edges are
  now bucketed in a uniform hash grid — each edge in the single cell of its bounding-box
  midpoint, cells sized at 4x the mean edge extent, rebuilt whenever the edge count
  doubles; edges longer than a cell go in an always-scanned overflow list — and a query
  walks the cells its own box covers plus one ring. Measured on a 30x30 line grid
  (12 640 edges): **9.1% of the edge-tests the full scan performed**.
  <br>Three properties keep it exact and cheap: an event needs a point shared by both
  segments, hence overlapping boxes, so the grid only removes edges that *provably*
  cannot interact (every survivor is decided by exactly the same predicates as before);
  `SplitEdge` only ever SHRINKS an edge, so a stale entry refers to a box containing the
  real one — over-approximation, never a miss; and nothing below `MinIndexedEdges` (256)
  builds an index at all, so sketch-scale arrangements are untouched.
- **`Distance3d`** — closest-point queries against 3D primitives, in terms of plain points
  so every engine can call them. `ClosestPointOnTriangle(p, a, b, c)` is Ericson's
  Voronoi-region form (Real-Time Collision Detection §5.1.5): six barycentric sign tests
  locate the feature and the answer is exact for it, with **no tolerance anywhere** — the
  only comparisons against a constant are exact-zero division guards (the epsilon ladder's
  algorithmic tier), which is what keeps a collapsed or sliver triangle returning a point
  on itself rather than a NaN. The `out TriangleRegion` overload also reports *which*
  feature (vertex, edge, interior) won, because a signed distance field picking the
  angle-weighted pseudonormal of the closest feature cannot reconstruct that from the point
  alone; the plain overload delegates to it, so the two can never disagree.
  `DistanceSquaredToTriangle` is the form branch-and-bound nearest queries want. This lives
  here because it had been written twice — privately in EngrCAD.Mesh's
  `MeshProjectionTarget` and again in Interop's `MeshSdf` — and the two copies had already
  drifted (only one carried the degeneracy guards).
- **Min-bounding fits** (`Fitting2d`, `Fitting3d`; g3: ContMinBox2/ContMinCircle2/
  ContOrientedBox3/OrthogonalPlaneFit3):
  - `Fitting2d.MinAreaBox` → `OrientedBox2d` — minimum-area oriented box via the
    calipers theorem (a side is collinear with a hull edge); evaluates every
    hull-edge-aligned box over the hull, O(h²) in the (small) hull size.
  - `Fitting2d.MinCircle` → `BoundingCircle2d` — Welzl minimum enclosing circle
    (iterative move-to-boundary form, deterministic seeded shuffle, expected O(n)).
  - `Fitting3d.FitPlane` → **`Frame3d`** — orthogonal-distance best-fit plane: origin
    at the centroid, Z the normal (smallest covariance eigenvector), X the dominant
    in-plane spread (largest) — a natural deterministic in-plane basis. Throws when
    the points don't determine a plane.
  - `Fitting3d.FitBox` → `OrientedBox3d` (a `Frame3d` + half extents) — PCA oriented
    box, re-centered to the tightest box with the PCA axes. **A heuristic, not the
    minimum-volume box**, and not a small gap: PCA orients by how the points are
    DISTRIBUTED, so sampling density changes the axes even when the shape does not.
    Good fit, needs no hull, tolerates degenerate clouds; the exact method is
    `MinVolumeBox` below, which takes a caller-supplied hull.
  - `Fitting3d.MinVolumeBox(hullVertices, hullTriangles)` → `OrientedBox3d` — the
    **minimum-volume** oriented box. **The 2D calipers theorem does not lift to 3D**: the
    minimum-volume box need NOT have a face flush with a hull face (Freeman–Shapira, and a
    great many implementations, assume it does). The regular tetrahedron on alternate
    corners of [−1, 1]³ is bounded by that cube at volume 8 while every face-flush
    candidate measures 16 — locked by a test. What holds is **O'Rourke's**
    characterization: at least two ADJACENT box faces each contain a hull EDGE, which makes
    each pair of hull edge directions a one-parameter family (swept + golden-section
    refined; unordered, so only j > i is searched). Face-flush candidates are evaluated
    exactly on top of that — inside a face's plane the 2D calipers theorem *does* apply —
    and PCA + axis-aligned seed the search, so the result can never lose to `FitBox`.
    The **hull is the caller's to supply**, deliberately: Core owns no polyhedron type and
    the 3D quickhull lives in EngrCAD.Mesh because it speaks `HalfEdgeMesh`; passing the
    hull as plain data (`ConvexHull.Compute(points).Triangulated().ToIndexed()`, a B-Rep
    solid's planar faces, or one the caller already has) keeps the layering intact. Cost is
    O(E² · h) — measured 3.6 / 22 / 122 ms for 18 / 42 / 78-vertex hulls on 8 cores, with
    the edge loop parallelized deterministically (own slot per index, in-order reduction).
    **What the sweep costs against O'Rourke's algebraic critical angle is MEASURED, not left
    as a caveat**: re-running a 43-hull corpus at 8× and 32× the sweep density with 8× the
    refined brackets, the best a finer search ever finds is **5.905e-8 relative**
    (12.566414756 → 12.566414014 on a random tetrahedron). So a family's optimum CAN hide
    inside a 3.75° bracket — the sweep is not exact — and it hides by about 2e-8 of a linear
    dimension, on a box whose consumers pack plates and choose stock in millimetres. The
    algebraic solve would buy that and nothing else, inside an O(E²·h) inner loop; the sweep
    is the engineering answer and `MinVolumeBoxTests` is the evidence, failing loudly if some
    future hull family turns out to hide a real minimum.
  - All built on **`SymmetricEigen3`** (cyclic-Jacobi 3×3 symmetric eigen-decomposition,
    unconditionally convergent) — now public with **both orderings**
    (`SolveDescending` for fitting's dominant-first convention, `SolveAscending` for the
    principal-inertia convention), which is what let `EngrCAD.Mesh` delete the
    near-verbatim internal copy it carried just to re-sort.
  - **`SymmetricTensor3`** — a symmetric 3×3 tensor as its six independent entries
    (inertia tensors, second-moment matrices, covariance): outer product, matrix-vector
    multiply, trace complement tr(T)·Id − T, and the congruence M·T·Mᵀ. Moved here from
    `EngrCAD.Mesh` (its mass-property code still consumes it) because a symmetric 3×3
    type belongs in the dependency-free foundation, beside the `SymmetricEigen3` that
    diagonalizes it.
- **`Solvers`** (namespace `EngrCAD.Core.Solvers`) — a small sparse linear-algebra
  library: symmetric positive-definite (Cholesky / CG), symmetric indefinite (LDLᵀ) and
  now **general NON-symmetric** systems (GMRES / BiCGSTAB + an ILU(0) preconditioner). It
  is the numerical substrate for the mesh engine's Laplacian smoothing/deformation and
  FEA assembly, and the non-symmetric solvers are the first stage of the CFD campaign
  (advection makes a flow operator non-symmetric, so neither Cholesky nor CG applies).
  Deliberately dependency-free and mesh-agnostic (doubles + int indices only) so the
  sketch constraint solver, FEA and a future flow solver all sit on the same types.
  - **`PackedSparseMatrix`** — immutable CSR (row-start offsets + column indices +
    values, rows sorted by column), with an optional **symmetric-upper storage** form
    that keeps only the upper triangle of a square symmetric matrix and mirrors
    off-diagonals during `Multiply` (half the memory and bandwidth). Assembly goes
    through **`SparseMatrixBuilder`** (finite-element style: `Add(r, c, v)` accumulates
    duplicates; packing stable-sorts per row so assembly is deterministic for a
    deterministic add sequence; symmetric-upper packing *rejects* lower-triangle adds
    rather than mirroring them, since a mirror would double-count a convention-following
    assembly).
    <br>**The per-row sort is a stable O(k log k) key sort, and the reason is a
    measurement.** It was an insertion sort, justified in a comment by "assembly rows are
    short (a vertex's ring), so the quadratic worst case never bites" — true of the mesh
    Laplacians the class was written for, false of the 3D finite-element assembly that is
    now its heaviest consumer: a 10-node tetrahedral mesh puts **612 raw entries** in its
    worst row, because every element touching a node contributes 30 columns to each of that
    node's rows. Each entry is now keyed as `(column, add-index)` packed into one long, so
    the ordinary primitive sort puts duplicates in add order and stability stops being an
    algorithm choice. Measured with the old sort transcribed verbatim as the baseline and
    the two **alternating in one sitting** (`SparseMatrixBuilderBenchmark`, i9-9900K,
    win-x64, Release):

    | longest row | rows | entries | insertion | key sort | speedup |
    |---:|---:|---:|---:|---:|---:|
    | 6 (a vertex ring) | 40 000 | 160 120 | 1.4 ms | 1.1 ms | 1.31x |
    | 24 | 20 000 | 320 185 | 4.7 ms | 3.6 ms | 1.30x |
    | 90 (4-node tet row) | 12 000 | 719 506 | 28.1 ms | 14.9 ms | 1.89x |
    | 612 (10-node tet row) | 3 000 | 1 212 416 | 241.5 ms | 33.3 ms | **7.25x** |

    End to end a quadratic FEA assembly's packing went **250 ms → 31 ms** and stopped
    growing superlinearly with the entry count. Nothing is slower, including at the row
    length the class was written for. Output is bit-for-bit unchanged — both sorts are
    stable, so duplicates are summed in the same order, and a floating-point sum is a
    function of its order; a test packs the same rows both ways and compares the bits.
    (Packing a pair into a long for **sorting** is sound in a way that packing one for
    **hashing** is not — this codebase's recorded trap is that `long.GetHashCode` is
    `lo ^ hi`, which collapses structured keys into a handful of buckets; a comparison reads
    all 64 bits and is exactly lexicographic.) Also: `Multiply(other)` (Gustavson row-merge SpMM — the bi-Laplacian L²
    construction), `ToGeneral()`/`ToSymmetricUpper()` (the latter takes the stored upper
    triangle as truth without comparing the lower, because the two halves of a
    numerically symmetric product differ in their last bits — same terms, different
    summation order), `Diagonal`, row-span accessors.
  - **`SparseSymmetricCG`** — Jacobi-preconditioned conjugate gradients. Deterministic
    (fixed-order sequential reductions — a parallel dot product would change last bits
    run to run). Convergence is a **return value, not a log line**: `SparseSolveReport`
    carries converged/iterations/residual, and a non-SPD search direction breaks out
    honestly instead of dividing by nonpositive curvature. The preconditioner's
    nonpositive-diagonal guard is a sign test, deliberately not a `Tolerance` comparison.
  - **`SparseCholesky`** — up-looking sparse Cholesky (elimination tree + per-row
    ereach, Davis ch. 4): factor once, forward/back-substitute per right-hand side —
    the shape of every Laplacian mesh solve, where x/y/z share one operator. Nonpositive
    pivots throw naming the column (in the caller's indices, whatever the ordering).
    **Ordering is a parameter** (`SparseOrdering.Natural` | `Amd`) defaulting to
    `Natural`, because reordering changes the summation order and an AMD-ordered solve is
    therefore *not* bit-identical to the natural one — every existing consumer's committed
    numbers were measured natural.
    <br>**Cancellable, and the progress fraction is work rather than columns.** `Factorize`
    takes the optional trailing `ProgressCancel` and polls it once per eliminated column —
    the finest granularity available without restructuring the inner loops, and the one that
    matters, because this is where an FEA solve spends 99% of its time (79.0 s of an 80 s
    solve at 46 800 unknowns). The reported fraction is the numeric pass's **inner-loop
    update count**, which is exact rather than an estimate: the symbolic pass has already
    counted every column of L, column *j* is used by exactly as many rows as it has
    off-diagonal entries, and it carries one more entry each time — so the total is
    `sum_j c_j·(c_j + 1)/2`, known before the first multiply. Column number would be a
    misleading bar wherever the factor **fills**: a dense factor has done 12.5% of its work
    at the halfway column. (Where the factor is merely *banded* — a natural-ordered 2D grid
    Laplacian, the shape this class was written for — the two agree closely, 0.468 at
    halfway; the re-weighting costs nothing there and is what makes the number honest on the
    systems worth cancelling.) Reports are throttled to 200 steps, and the accumulator is
    guarded by the null check so a caller who passes no progress pays nothing in the
    innermost loop. `SparseSymmetricCG.Solve` takes one too and polls per iteration, but
    reports **no** fraction on purpose — an iteration count is not progress, since the
    residual falls at a rate nobody knows in advance and the iteration cap is a stall
    detector rather than a work estimate.
  - **`SparseCholesky.Analyze` → `SparseFactorAnalysis`** — what a factorization WILL cost,
    from the symbolic pass alone: the stored entries L will have, the numeric pass's exact
    inner-loop update count, the longest column, and the heaviest root-to-leaf path of the
    elimination tree. It runs the *same* symbolic code the factorization runs (shared, not
    restated), so the prediction and the run cannot disagree; measured, it costs 5–520 ms
    where the factorization it describes costs 0.1–134 s.
    <br>Two things it is for. **A cost estimate before the wait**: on this repo's FEA
    cantilever the update count predicts factor time at **~1.0–1.3 ns per update** across a
    15x size range and both element orders, so "this will take two minutes" is answerable in
    a third of a second. And **deciding what would move the wall**, which it settled in a
    direction that was not obvious: `ParallelCeiling` — total work over critical path, i.e.
    the most a perfectly scheduled parallel factorization could ever win — measures
    **1.0x natural and 1.6–1.9x AMD** on 3D elasticity, because the top separator's columns
    are a constant fraction of all the work and form a *chain*. Tree parallelism cannot pay;
    the same table's longest column (1 400–3 748 entries under AMD) says the work is in a
    few nearly-dense columns, which is what a supernodal BLAS-3 kernel exists for.
  - **`SparseLdlt`** — the symmetric-INDEFINITE factorization A = L·D·Lᵀ (L unit lower,
    D diagonal), real or complex symmetric, for exactly the systems `SparseCholesky`
    correctly refuses. The consumer it unblocks is the direct per-frequency harmonic solve
    `(K − ω²M + iωC)·u = f`, whose matrix is complex SYMMETRIC (not Hermitian) and whose
    equivalent real form is symmetric indefinite by construction (eigenvalues in ±pairs).
    <br>**The backlog named three candidates and the weighing is the deliverable.**
    COCG/QMR was rejected because the item asks for a DIRECT solve — a shifted system near
    resonance is precisely where a Krylov method's convergence goes unpredictable, and
    re-importing that is what the direct solve exists to remove. A real Bunch–Kaufman LDLᵀ
    on the 2n×2n real form was rejected on STRUCTURE: a magnitude-searched 2×2 pivot merges
    two columns' patterns, so the symbolic pass stops predicting the numeric structure and
    AMD's counts go stale — which is why production sparse indefinite solvers (MA57,
    PARDISO) are multifrontal machines with delayed pivots, a different order of project
    (filed, for real indefinite systems that genuinely need pivoting). **The complex
    spelling wins because for this family the "2×2 pivots" are fixed by structure, never
    searched**: a complex pivot r + is is invertible whenever (r, s) ≠ (0, 0) — the
    robustness a Bunch–Kaufman block buys on the real form's paired ±structure, where the
    real form's leading block is K − ω²M alone and an unpivoted real factorization of it
    breaks down near every resonance. Because the pivots are structurally 1×1 in the
    complex field, the elimination structure IS the Cholesky one on the union pattern of
    the two parts, so **the symbolic pass is `SparseCholesky`'s internals verbatim (shared,
    not copied), `SparseOrdering.Amd` applies unchanged, and `SparseCholesky.Analyze`
    predicts this factorization too**.
    <br>**When it provably exists**: write Z = R + iS; a singular leading minor Z_k forces
    a vector annihilated by BOTH R_k and S_k (from uᵀS_k·u + vᵀS_k·v = 0 with S PSD), so
    with any positive-definite damping present — Rayleigh damping is, whenever either
    coefficient is nonzero — no pivot can vanish at ANY frequency, resonances included;
    breakdown requires an entirely undamped subsystem exactly at one of its own resonances,
    where the physical steady state is unbounded too and the loud refusal is the right
    answer. The REAL overload is unpivoted with the caveat stated: it factors iff every
    leading minor is nonsingular — true of shifted K − ω²M away from a measure-zero set of
    ω, and of a saddle system `[[A, B],[Bᵀ, 0]]` with constraints ordered LAST (the Schur
    complement is negative definite) — refuses an exactly-zero pivot naming the caller's
    column (an exact-zero division guard, not a tolerance), and reports
    `SmallestPivotMagnitude`/`LargestPivotMagnitude` so near-breakdown growth is visible.
    The documented AMD hazard: AMD reads the pattern and not the values, so it can reorder
    a saddle system's structurally-zero diagonal ahead of its constraints and turn a
    factorable matrix into a refusal — natural stays the default; AMD is right for the
    shifted-Helmholtz family, whose diagonal is structurally full. Verified against an
    independent dense partial-pivoting solve (real and complex) at backward residuals
    < 1e-13, an analytic saddle solution, the hand-assembled steel bar's K − ω²M above its
    first resonance (Cholesky's refusal asserted on the same matrix), Rayleigh-damped and
    dashpot-damped (union-pattern) harmonic systems, and `[[0, 1],[1, 0]] + i·I` — the
    smallest matrix where the complex pivot succeeds and the real part alone breaks down.
    Deterministic (bit-identical repeat factorizations), cancellable with the same
    exact-update-count progress convention as `SparseCholesky`.
  - **`AmdOrdering`** — approximate minimum degree (Amestoy–Davis–Duff 1996): quotient
    graph with element absorption (including the aggressive form), approximate external
    degrees, mass elimination, hash-detected supervariables, and an assembly-tree
    postorder as the returned permutation. Deterministic, allocation-bounded (one arena
    with 20% elbow room and in-place compaction), no tolerance anywhere — it reads a
    pattern, not values.
  - **Ordering measured** (`SparseOrderingBenchmark`, `ENGRCAD_BENCH`-gated; i9-9900K,
    win-x64, Release, best of three after a wall-clock warm-up budget). "solve" is one
    substitution; "RHS" is how many right-hand sides an AMD factorization must serve
    before it beats running CG once per side:

    | grid | n | nat fill | nat factor | nat solve | amd fill | amd factor | amd solve | CG | CG iters | RHS |
    |---|---|---|---|---|---|---|---|---|---|---|
    | 2D 50² | 2 500 | 125 049 | 5.3 ms | 0.27 ms | 35 913 | 1.8 ms | 0.09 ms | 1.0 ms | 34 | 2 |
    | 2D 80² | 6 400 | 512 079 | 32.9 ms | 1.29 ms | 120 766 | 7.1 ms | 0.31 ms | 2.7 ms | 34 | 4 |
    | 2D 120² | 14 400 | 1 728 119 | 158.8 ms | 5.05 ms | 321 309 | 22.2 ms | 0.84 ms | 6.1 ms | 34 | 5 |
    | 2D 250² | 62 500 | 15 625 249 | 2 754.8 ms | 47.65 ms | 1 874 755 | 204.8 ms | 5.66 ms | 26.6 ms | 34 | 10 |
    | 3D 14³ | 2 744 | 504 713 | 70.1 ms | 1.21 ms | 151 653 | 19.2 ms | 0.36 ms | 1.5 ms | 39 | 18 |
    | 3D 19³ | 6 859 | 2 359 153 | 572.9 ms | 6.34 ms | 644 706 | 148.2 ms | 1.48 ms | 3.8 ms | 39 | 65 |
    | 3D 24³ | 13 824 | 7 657 943 | 2 943.1 ms | 21.09 ms | 1 874 559 | 682.0 ms | 5.06 ms | 7.8 ms | 39 | 250 |
    | 3D 40³ | 64 000 | 99 966 439 | 125 219.8 ms | 262.39 ms | 20 614 676 | 26 172.2 ms | 55.68 ms | 40.5 ms | 40 | never |
    | 2D 250² Dirichlet | 62 500 | 15 625 249 | 2 892.5 ms | 51.20 ms | 1 874 755 | 221.3 ms | 5.81 ms | 750.0 ms | 858 | 1 |
    | 3D 24³ Dirichlet | 13 824 | 7 657 943 | 3 170.8 ms | 21.63 ms | 1 874 559 | 858.7 ms | 5.44 ms | 22.2 ms | 107 | 52 |

    AMD is 4.6–13.4× on factor time and 3.5–8.3× on fill everywhere, and it never loses:
    ordering a 62 500-unknown pattern costs single-digit milliseconds against a
    factorization measured in hundreds. **The last two rows are the ones that matter for
    the direct-vs-iterative question.** The shifted operator (L + I) is strongly
    diagonally dominant, so CG converges in an *n-independent* ~35 iterations and the CG
    column flatters it; drop the shift and CG needs 858 iterations at 62 500 unknowns
    (750 ms) while AMD factor + one solve is 227 ms — the direct solve wins on the FIRST
    right-hand side, by 3.3×. In 3D the crossover is real but far out (52 RHS at 13 824
    unknowns), because 3D fill grows like n² however it is ordered.
  - **`Gmres` — restarted GMRES(m) for a general non-symmetric A·x = b**, the workhorse for
    the systems Cholesky and CG cannot touch. Arnoldi (modified Gram–Schmidt) + Givens QR of
    the Hessenberg minimises the true residual over a growing Krylov subspace; monotone and
    non-increasing within an un-restarted cycle. **Right-preconditioned** (the
    `IPreconditioner`, null = none): the Krylov subspace is built on `A·M⁻¹` so the residual
    the Givens rotations track is the residual of the ORIGINAL system, not a preconditioned
    one — the reported number is the one a caller can recompute, which is exactly the
    silent-CFD-failure ("converged on the wrong residual") this guards against, and it is in
    fact recomputed exactly as `‖b − A·x‖` at every restart. **Happy breakdown is
    convergence**: a (near-)zero new Arnoldi vector means the subspace is invariant and the
    iterate is already exact, detected and reported rather than divided by. The theorem is
    a test: un-restarted GMRES (m ≥ n) reaches the exact solution in **at most n** steps, so
    the residual is round-off — measured 12/35 iterations at 2.7e-13 on a 35×35 system.
    Deterministic (fixed-order MGS, sequential reductions — bit-identical iterate sequence);
    working storage (m + 1 basis vectors + the small Hessenberg) allocated once per solve.
  - **`BiCgStab` — the cheaper-per-iteration non-symmetric alternative** (van der Vorst): a
    short fixed recurrence, so constant storage, at the cost of GMRES's monotone residual (it
    can oscillate, and stalls where GMRES grinds through). Which wins is problem-dependent, so
    both are provided. The preconditioner is applied to the two search directions and the
    recurrence carries the true residual throughout; the final report recomputes `‖b − A·x‖`
    anyway (a recurrence residual drifts). **Breakdown is reported, never a silent NaN** — the
    two failure modes (ρ ≈ 0, the shadow residual going orthogonal; ω ≈ 0 / t ≈ 0, the
    stabiliser collapsing) are each caught BEFORE the division they would spoil, returning
    `Converged = false` with the last honest residual (the CG "report the non-SPD direction
    rather than divide by it" convention). Measured on a skew-dominant matrix: 81 relative
    residual, no NaN.
  - **`Ilu0` — incomplete LU with ZERO fill**, the "ILU at minimum" a Krylov method needs on a
    non-symmetric system: L and U share A's OWN pattern, the IKJ elimination drops every
    fill (`a[i,j] -= (a[i,k]/a[k,k])·a[k,j]` applied only where (i,j) is a stored entry), so
    the factorization is O(nnz·bandwidth) and M = L·U is an approximation whose error is
    exactly the dropped fill. Verified two ways: on a **no-fill matrix (tridiagonal) ILU(0) IS
    the exact LU** — an identity, compared entry-for-entry against a complete LU and used as a
    direct solve to round-off — and as a **preconditioner it strictly cuts iteration counts**,
    measured GMRES 40→10, BiCGSTAB 37→7 on a 256-unknown 2D convection–diffusion operator.
    <br>**There is no ordering parameter, deliberately, and it is a real decision rather than an
    omission.** AMD reduces the FILL of a complete factorization and ILU(0) has no fill to
    reduce — that is its definition — so a fill-reducing permutation would spend a symbolic
    pass to move round-off around for no saving AND break the "no fill ⇒ exact LU" identity. A
    permutation there changes only WHICH entries are dropped, i.e. preconditioner accuracy, a
    different question wanting a different ordering (RCM for bandwidth, multicolour for
    parallelism) that only earns its keep once fill is admitted (ILU(p > 0), ILUT) — filed for
    that tier. So AMD does NOT apply here; the factorization stays natural-ordered, hence
    deterministic and bit-reproducible.
    <br>**For a symmetric matrix with a symmetric pattern (every SPD system this repo
    assembles) ILU(0) is symmetric**, because the dropped fill is symmetric too, so
    `M = L·U = L·D·Lᵀ` — the incomplete-Cholesky factor under another name — which makes it a
    legitimate **conjugate-gradient** preconditioner (`CgOptions.Preconditioner`, additive and
    leaving the Jacobi path bit-identical when null). Measured on a 24×24 Dirichlet grid
    Laplacian: CG **87 → 32** iterations. Pivots are all-or-nothing like `SparseCholesky`: a
    missing or zero diagonal throws naming the row (an exact-zero test, since how small a
    legitimate pivot may be is the caller's conditioning; a positive diagonal shift is the
    standard fix).
  - **`IPreconditioner`** — the one-method seam (`z = M⁻¹·r`) the three Krylov solvers share;
    null = identity, so an unpreconditioned solve costs no apply rather than an apply that
    copies. `Ilu0` is the one implementation today; the interface exists so a future
    block/multigrid preconditioner drops in without touching the solvers.
- **`ParallelFor.Blocks(from, to, body, minBlockSize)`** — thin block-parallel-for over
  index ranges (g3's `gParallel.BlockStartEnd`): splits the range into a bounded number
  of large contiguous blocks so each worker touches a contiguous slice of the underlying
  SoA arrays. Supported pattern: every index writes only its own output slot, which
  makes results bit-for-bit deterministic regardless of scheduling. Used by the
  `SurfaceNets` sampling phase and the dense `GridSdf` bake.

- **`ModelUnits`** — the one place EngrCAD's unit system is written down:
  **mm / N / MPa / tonne / s**, so a density is tonne/mm³ (structural steel `7.85e-9`,
  not 7850 and not 7.85e-6) and a mass computed from it is in tonnes.
  `DensityFromKilogramsPerCubicMetre` / `DensityToKilogramsPerCubicMetre` and
  `MassToGrams` / `MassToKilograms` are the conversions at the edges, plus
  `Gravity` = (0, 0, −9806.65). **Why the choice went this way**: a density is either a
  number an *equation* consumes or one a *report* prints, and only the second can be
  converted afterwards — an FEA mass matrix must balance against a stiffness in MPa and a
  length in mm with nowhere to put a factor, while mass properties form exactly one
  product from the density. So the convention lives where it cannot be converted, and the
  readable units are accessors rather than a second convention.
- **`Material` / `Materials`** — a name, a mass density, an optional display
  `PartColor`, and OPTIONAL analysis properties (Young's modulus, Poisson's ratio,
  conductivity, specific heat, expansion) with the Lame parameters derived from them.
  It lives here because it is the one type both `EngrCAD.Modeling` (`Part.Material`, mass
  properties, the BOM) and `EngrCAD.Fea` (every solver) need, and Core is their only
  common ancestor. **Zero means "not stated"**, and the refusal lives at the point of
  use — `StructuralModel` refuses a material with no modulus by name, `ThermalSolver` one
  with no conductivity, `ModalSolver` one with no density — because a material with just a
  name and a density is a perfectly good *document* material and is what a bill of
  materials is mostly made of. `Materials` is the nominal, verify-against-datasheet
  catalogue (steel, stainless 304 and 316, two aluminiums, titanium, cast iron, brass,
  ABS, PLA, nylon); no entry carries a colour, deliberately, since appearance is a finish
  rather than a property of the stuff — so assigning one to a part moves no pixels.
  `EngrCAD.Modeling`'s `FastenerMaterials` extends it for the hardware catalogue and
  *delegates* wherever this one already states the alloy (its A2 and A4 rows ARE 304 and
  316, renamed), because two spellings of one density is exactly the discrepancy this
  consolidation removed.
- **`PartColor`** — RGB in [0, 1], UI-framework free. Here rather than in the document
  model only because `Material` carries one; the *policy* (the palette and the
  once-only assignment rule) stays in `EngrCAD.Modeling`, which is where the invariant is.

## Deliberately not adopted (from the geometry3Sharp survey)

- **`DVector<T>`** (chunked growable array) and **`MemoryPool<T>`** — g3 needed them on
  old runtimes to dodge LOH resize copies and GC pressure. On modern .NET,
  `List<T>` with a capacity hint, exact-size arrays, and `ArrayPool<T>.Shared` (already
  used in the bakes) cover every current call site; no measured need exists in this
  codebase. Revisit only with a profile in hand.

## Conventions

- Public API passes structs by `in`; note that **expression trees cannot call methods
  with `in` parameters** — LINQ-visible wrappers live in `EngrCAD.Query`.
- Exact equality (`==`, `Equals`) is bitwise; geometric equality goes through
  `AreEqual(other, tolerance)`.
