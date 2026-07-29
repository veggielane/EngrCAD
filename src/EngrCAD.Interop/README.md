# EngrCAD.Interop

Conversions between the three geometry representations. References `EngrCAD.Mesh`,
`EngrCAD.Implicit`, and `EngrCAD.BRep` — the only kernel project allowed to depend on all
engines. Also `Microsoft.Extensions.Logging.Abstractions` (abstractions ONLY — no
provider, no UI): the long operations — `BrepBoolean.Union/Intersection/Difference`,
`BRepTessellator.Tessellate`, the `MeshSdf` constructors — take an **optional trailing
`ILogger`** for timing/diagnosis (`KernelLog.cs`, stable event IDs **80** boolean /
**81** tessellation / **82** mesh-SDF build; a boolean passes its logger to its own
sub-steps, which log at Debug under its Information completion). Null is the default
and costs one branch; every operation's findings remain return values or exceptions —
logging complements them, never replaces them.

## The conversion triangle

- **Implicit → Mesh**: `SurfaceNets.Polygonize(sdf, region?, resolution, progress?)` —
  manifold Surface Nets (dual contouring): one vertex per *connected component of inside
  corners* per cell (plain one-vertex-per-cell produces non-manifold edges on thin
  sheets and saddles), one quad per interior sign-changing grid edge, wound outward.
  Surfaces crossing the sampling region come out open there. The
  optional `ProgressCancel` reports coarse progress and cancels cooperatively
  (throws `OperationCanceledException`, partial results discarded).
  - **The inside-corner rule alone is NOT manifold, and the gap is the ambiguous face.**
    A grid face whose inside corners are exactly a diagonal pair (Marching Cubes' ambiguous
    face) has all four of its edges crossing, so it carries two quad edges between the same
    two cells — and when BOTH cells join that pair into one component, the two quads land on
    the same DIRECTED edge and `HalfEdgeMesh.Build` refuses the mesh. That is the *only* way
    a duplicate arises: two such instances must come from one pair of cells, hence one shared
    face, and a face with just two crossings gives its two instances opposite directions.
    So each interior face is tested once, by the cell on its **+ side**, against the
    neighbour across it; where both join, that component is **split by the outside blob each
    crossing reaches**. The split always exists, because a cell can never join both an
    ambiguous face's inside pair AND its outside pair — a path between the inside pair must
    use an off-diagonal corner of the far face as *inside*, while a path between the outside
    pair needs both of them *outside*. Nothing else is refined, so every other cell stays
    bit-identical (every golden fingerprint and every rendered docs PNG is untouched by the
    fix). The per-cell map is therefore keyed by **cube edge**, not by inside corner: one
    corner's crossings can end up on two different vertices. Whether the configuration lands
    is an ALIGNMENT question, not a tolerance one — `Sphere(16).Lattice(Gyroid(12, 1.2))`
    fails at resolution 88 and at none of the other nine tried — so `SurfaceNetsManifoldTests`
    sweeps families and resolutions rather than pinning one fixture, and asserts each fixture
    still *carries* the configuration so it cannot quietly stop testing it. A one-cell-thick
    wall is what produces it, which is why a plain `Sphere(10).Shell(0.6)` at resolution 44
    reproduces it (240 such faces) just as a gyroid lattice does: the gap was in the rule,
    never in lattices. Cost is unmeasurable at res 192/256 (a second 8-corner flood fill is
    O(cells with a sign change), i.e. O(n²) against the O(n³) walk); allocation rises ~2 MB
    at res 256, the map going from 8 ints per mixed cell to 12.
  - **Sampling is deinterleaved and streamed.** The grid is never materialized as points:
    coordinates are generated from the indices straight into pooled x/y/z scratch and fed
    to `Sdf.Evaluate(x, y, z, distances)` — the SoA batch entry — so the round trip that
    built a `Vector3d[]` corner array only for the AST root to transpose it back apart is
    gone (24 bytes per corner, and one pass over the whole grid). Samples live in a
    **sliding window of whole x-slabs** sized to a 64 MB budget, with cell vertices and the
    three quad passes interleaved into the same walk, so peak memory scales with the
    grid's cross-section rather than its volume: a 1024³ grid needs 16 MB of samples where
    the dense array needed 8.6 GB. Below about resolution 200 the whole grid fits the
    budget and the window IS the grid — the small-model path is unchanged.
    Measured on the reference machine (win-arm64, Release, idle): res 96 **39.9 → 15.6 ms**
    and 40.9 → 19.9 MB; res 256 **735.5 → 258.8 ms** and 562 → 145 MB; res 384
    **1922.7 → 747.5 ms** and 1842 → 289 MB. See `SurfaceNetsBenchmark`.
  - **Output is bit-for-bit independent of both the batching and the window.** Slabs are
    sampled in parallel via `ParallelFor.Blocks` (every sample lands in its own slot), the
    topology passes stay sequential, and quads are emitted into per-axis buckets keyed by
    the loop variable that was outermost in the dense version's three emission passes, then
    concatenated — which reproduces the dense face ordering exactly while letting the
    passes run slab by slab. `SurfaceNetsSamplingTests` locks all of it against golden
    bit-hashes of the pre-streaming output, against a wrapper that forces every batch back
    through the scalar `Evaluate`, and across window sizes from "whole grid" to "two slabs".
  - **Only the blocks the surface can reach are visited** (`SurfaceCull`). The grid is
    tiled into 8³-cell blocks; a block whose centre reports `|d| > halfDiagonal + oneCell`
    is skipped entirely, at a cost of one evaluation per block (a 32-cell coarse level in
    front of it throws away the far field for 1/64th of that). **The completeness argument
    is the whole point**: an `Sdf` here is 1-Lipschitz, so every point `p` of such a block
    has `|d(p)| ≥ |d(c)| − |c − p| ≥ |d(c)| − R > 0` and the block cannot contain a sign
    change, hence no vertex and no quad. Nothing is seeded and nothing is flooded — *a
    seed-and-flood continuation silently drops components its seeds miss, and the only sound
    way to prove the seed set complete is a cull, which has already produced the visit set.*
    Cell and quad loops keep their exact `(j, k)` order and only skip runs of tiles, so the
    mesh is **bit-identical to the full walk**, ordering included
    (`TheCulledWalk_IsBitIdenticalToTheFullWalk`, including three separated components — one
    smaller than a block — and a hollow shell).
    Measured (reference machine, `SurfaceNetsBenchmark.CullSpeedupByFieldAndResolution`):
    the CSG field evaluates **28.4% / 15.6% / 12.0%** of the grid at res 96 / 192 / 256 for
    **1.60× / 2.18× / 2.46×**, and a `MeshSdf` field **2.99×** at res 96.
  - **Beyond that, polygonization is no longer sample-bound** — the honest half of the
    result. At res 256 a field costing one square root per sample still takes 132.8 ms
    against 129.3 ms for the real CSG field: evaluation is free and the cost is assembly.
    `HalfEdgeMesh.Build` alone was 39–48%, the rest per-cell component maps, quad lists
    and the sample window.
  - **Assembly has since been attacked, and is no longer where the time goes.** Quads are
    emitted into per-axis **flat index buffers** rather than one `int[4]` per face
    (289 726 heap allocations at res 384, for a mesh whose defining property is that it is
    grid-structured), and the whole buffer goes to `HalfEdgeMesh.Build`'s uniform-stride
    overload, whose twin resolution is now a counting sort over each edge's lower endpoint
    instead of a half-million-entry dictionary. Output is bit-identical, golden
    fingerprints included. Measured interleaved within one sitting
    (`SurfaceNetsBenchmark.AssemblyShareByResolution`, i9-9900K win-x64 Release):

    | res | vertices | assembly before → after | share before → after | total before → after |
    |---|---|---|---|---|
    | 96 | 17 930 | 6.5 → 2.0 ms | 42.1% → 18.2% | 12.9 → 10.7 ms |
    | 192 | 72 232 | 27.0 → 8.0 ms | 38.4% → 15.8% | 67.1 → 50.6 ms |
    | 256 | 129 268 | 47.5 → 13.4 ms | 40.8% → 14.8% | 116.2 → 90.2 ms |
    | 384 | 289 726 | 132.3 → 35.5 ms | 41.0% → 16.7% | 322.5 → 212.6 ms |

    Allocation at res 256 falls 145 → 103 MB. **The share, not the speedup, is the number
    that matters**: at 15–18% the builder has stopped being the place to look, and what
    remains at res 384 is ~35 ms of building against ~175 ms of grid walk (component maps,
    crossing interpolation, the three quad passes).
- **B-Rep → Mesh**: `BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples, progress?)` —
  each edge is sampled once into a shared polyline; planar faces (any number of loops)
  ear-clip via `PolygonTriangulator`; cylinder bands and full-domain generated faces
  (extruded/revolved/swept) tessellate as parameter grids whose samples match the shared
  edge polylines exactly; everything is welded (with seam zipping to repair T-junctions
  from earcut's collinear filtering).
  - **`IsRingPairedBand` — the ring path checks its own precondition.** A plain
    `CylinderSurface` has no parameter grid at all: `TessellateCylinderBand` emits one
    quad per sample index j joining `bottom[j]` to `top[j]`, which is the right band
    *exactly when the two loop polylines sample the same azimuths in the same order*.
    Two natural rings do — both are circles on the cylinder's own frame at identical
    parameters, so their radial parts agree to a few ulps. Two INDEPENDENTLY traced
    wrapping cuts do not, and the old gate (two loops, one closed coedge each) admitted
    exactly the case the pairing could not triangulate: a cross-drill piercing the wall
    left the tool's band bounded by two marching-tracer polylines with unrelated phases,
    and **18 of its 40 quads faced inward** (worst facet-vs-surface agreement −0.0000)
    before the weld reported a duplicated directed edge three stages downstream. The gate
    now compares the paired samples' radial vectors at the weld tier; anything that fails
    goes to `TrimmedFaceTessellator`, which pairs by pulled-back u. That is what makes
    `FaceSplitter`'s non-planar wrap cut work on a plain cylinder — the split and the
    trimmed path were both correct all along. Residual, pre-existing and measured: a
    traced rim's sample count is set by the tracer's ARC-LENGTH step, so a *small* band
    gets few samples per turn and the facets beside it fall under the corpus's
    density-scaled floor (a Ø10 drill's rim carries 66 samples per turn and reads
    0.858 / 0.9995 / 0.9998 at 32/96/192, a Ø3 one carries 40 and reads
    0.974 / 0.949 / 0.565) — fold-free throughout, and volumes still converge.
  - **`IsFullHelicalBand` — the same rule, one surface family along.** A helical face used
    to go unconditionally to `TessellateHelicalBand`'s SHEARED grid, which interpolates
    its interior columns linearly between two exactly projected rail corners and therefore
    assumes the face's two `SpiralArc3d` cuts ARE the ends of u. That holds for every band
    `MakeThreadedRod` builds and for nothing else, so a face trimmed by anything but its
    own cap planes now goes to `TrimmedFaceTessellator`. **The gate reads `IsPlanar`, not
    "is a `SpiralArc3d`"**: a coaxial cone cuts a helical band in a `SpiralArc3d` too — the
    conical spiral of a 45° end chamfer — so counting spiral edges would send a chamfered
    band down a grid whose columns interpolate across a boundary running diagonally.
    Nothing periodic is needed here: a helical band's u is NOT periodic (z advances every
    turn), so every loop has winding 0 and the non-wrapping tiers apply, with
    `NaturalSteps` giving u the circle density (the same rule `SampleEdge`'s
    `AngularSegments` gives the rails and cuts bounding the face, so rows and boundaries
    agree by construction) and v an INFINITE step, since the generator is straight and
    `PointAt` is affine in v — a v-chord lies exactly on the surface and never needs
    refining. Covered by `TrimmedHelicalFaceTests` on hand-built faces whose trimming
    curve comes from the production intersection, and end to end by `ChamferedThreadTests`
    now that a 45° lead-in chamfer reaches this path from the `Shape` API.

    **`SampleEdge`'s angular rule must read the turning angle through the curve's own
    parameter mapping, not off the edge's domain.** A `Helix3d` or `SpiralArc3d` parameter
    IS the angle, but a `CurveSegment` — what the face splitter hands back after every
    cut — reparameterizes to [0, 1] while `Underlying` still points at the spiral, so
    `domain.Length` there measures a segment FRACTION as if it were radians. Every such
    edge got the same count whatever it spanned (11 at `segmentsPerCircle` 64 — and 11 at
    256 as well: a density FLOOR, the shape of the baked-tracer-polyline finding), and on
    a chamfered thread it put two cuts of the SAME 0.785 rad span at 8 and 11 samples,
    which the sheared grid reports as *boundary polylines disagree in sample count*. That
    is the third occurrence of one rule: **`Underlying` is a TYPE hint and says nothing
    about the parameter mapping** — the same reason `FaceGeometry.ExactSampleParameters`
    exists on the polyline side.
  - **Trimmed faces** (loops not covering the surface's grid domain — `FaceSplitter`
    fragments such as a bore wall cut through by a slot, and every mitered rim-fillet
    band) go through `TrimmedFaceTessellator`, which picks a path in this order:
    1. **Strip, interior rows first** — a single-loop region whose boundary is a *band*:
       two chains monotone in one surface parameter. When the cross direction is curved
       (finite natural step), `RowedStrip` inserts the natural grid's own sample rows
       into the BASE triangulation before anything else: one constant-cross path per
       inside stretch of each natural level (crossings taken in key order alternate
       enter/leave, so a level threads *between* scallops), anchored at existing
       boundary vertices — never an invented boundary point — with interior vertices at
       the natural key values. Each path cuts its piece in two; the sub-bands, at most
       ~1.5 steps tall, go through the same monotone stack sweep, which between two full
       rows reproduces the untrimmed grid's quads exactly. A uv-area identity is the
       closing guard; any snag falls back to the rowless sweep below, so the rowed path
       can never be worse than what it replaces. Rows are tried in BOTH key orientations
       before any rowless fallback — a rowed triangulation in the less-preferred key
       beats a rowless one in the preferred key.
       The rowless path: the chain direction is the parameter carrying
       the natural sampling, so the cross edges lie across the ruled or coarser one
       (getting that backwards would fan a 2-sample end against a 25-sample chain). The
       loop is split at its extreme-key vertices and handed to the same **stack sweep**
       the slab path uses, which is correct on any monotone polygon; the older
       rung-counting split plus merge zip stays behind it as a fallback for loops the
       sweep declines (a chain running backwards in the key, or one side that is a single
       edge). Every band in the docs tessellates bit-identically either way — the sweep's
       value is the two shapes the rung split *cannot express*:
       - a **cross edge sampled at more than two points** (a curved end) is several
         consecutive vertices at one key. The sweep stacks them — exactly collinear is
         deliberately not a turn, so nothing pops between them — and fans them from the
         opposite chain's first vertex when the funnel closes, which is what keeps them
         out of the zero-area trap that fanning them among themselves would be. The
         tie-breaking is load-bearing: the extremes are taken as the LAST of the tied
         minimum run and the FIRST of the tied maximum run, so a whole tied run lands on
         one chain. Split between the two chains, the merge would interleave the sides at
         equal keys and ask the sweep to triangulate collinear points.
       - a **band whose chains meet at a point** (a cross edge of no steps) is just a
         monotone polygon with a single extreme vertex, where the sweep starts anyway.

       **"Collinear is not a turn" has to mean straight to within ROUND-OFF, not
       bit-exactly straight** — and the gap between those two readings was a real defect.
       The pop test is the sign of `(b−a)×(c−a)`, which on a constant-parameter boundary
       run is `|b−a|·|c−b|·sin(0)`, i.e. nothing but the pullback's own noise; an exact
       `> 0` therefore emits a facet chosen by arithmetic. In uv that facet is degenerate
       and harmless, and in MODEL space it is nothing of the sort, because **uv-collinear
       is not 3D-collinear** — three consecutive samples of a curved rim span a real facet
       whose normal is the rim's binormal, the standing trap this whole file exists to
       avoid, arriving through the tier that was supposed to be the cure. Measured on a
       threaded rod's 45° lead-in chamfer, whose cone face carries a 65-sample rim at
       constant v: the pop fired on ~1e-15 of jitter and fanned that rim flat into the end
       plane at facet-vs-surface agreement **−0.7071 = −cos 45°** exactly — the angle
       between the end plane and the cone, which is what makes the number a fingerprint
       rather than a symptom. `TurnsIntoInterior` now tests the dimensionless **sine** of
       the turn (`|cross| ≤ 1e-9·|b−a|·|c−b|` is not a turn). Dividing by the two edge
       lengths is what separates the populations rather than shrinking both, since the
       noise is absolute in uv while a genuine turn scales with the chord: ~4e-12 for a
       jitter turn against ~1.6e-2 for a real one at 64 segments/circle (~4e-3 at 256),
       ten orders apart, so the constant is not tuned. Radians are dimensionless, which is
       why this guard is deliberately absolute — the epsilon ladder's stated exception for
       angular guards — rather than relative to the region's extent; the comparison that
       matters is local to the triple. Declining to pop is always safe: the vertices stay
       on the stack and are fanned later from the opposite chain, which is both the
       correct band triangulation and already the path an exactly-collinear run took.
       **The evidence that it is exactly right is the triangle COUNT.** Scanning 5% steps
       of the thread depth on M6×1 / M8×1.25 / M10×1.5 / M12×1.75, both ends chamfered:
       10 of 76 depths folded — 0/4/3/3 per size, at unrelated fractions, an alignment
       phenomenon rather than a threshold — and after the guard **exactly those 10 rows
       change and the other 66 are byte-identical**, with every changed row keeping its
       facet count to the unit (16 526 → 16 526, …). The guard adds and removes no
       geometry; it only stops round-off from choosing the diagonal. Folds went N → 0 and
       worst agreement −0.7071 → 0.513…0.730, landing inside the 0.562…0.979 band the
       never-folding depths already occupied — one population, so no quality was traded
       for the fix. (That band is coarse because a sub-depth chamfer cone is an
       extreme-aspect strip — 0.034 mm tall around a 25 mm circumference at the shallowest
       step — which is a separate residual, filed, and NOT something the sweep can fix.)
       Pinned by `ChamferedThreadTests.SubDepthChamfersCarryNoFoldsAtAnyFraction` over all
       ten, which also asserts the fixture still CARRIES the configuration (each cone
       still presenting ≥ 32 rim vertices at one radius in an end plane), so it cannot
       quietly stop testing the trap.

       Neither shape is reachable from the `Shape` API yet — the constructions that would
       make one (a spherical band between two meridian cuts, a cone fragment through the
       apex) are refused earlier by the exact B-Rep boolean, and a sweep of eighteen
       further candidates found nothing else that reaches them — so both are covered by
       direct unit tests on hand-built faces in `TrimmedBandGapTests`.
    2. **Band with holes** — two-ring bands carrying extra interior hole loops (a
       cross-drilled bore wall) are cut open along a seam placed in the largest u-gap
       left free by the holes and unrolled into a rectangle-with-holes; the two seam
       chords are exact one-period translates with identical 3D endpoints, so they weld
       to each other. The unrolled region is then **slab-swept**: each hole is split at
       its extreme-u vertices into a lower and an upper u-monotone chain, cutting the
       band into a run of u-monotone slabs (free slab, below-hole slab, above-hole slab,
       free slab, …) that are triangulated by the textbook **stack sweep for monotone
       polygons**. The cut at a hole's leftmost vertex `L` is the two-segment chord
       `bottom[k] → L → top[j]` (k, j the last ring samples at or before `u(L)`), whose
       halves are shared *verbatim* by the slabs on both sides — watertight by index,
       never by tolerance — and no vertex is invented, because the ring polylines are
       shared edge geometry and inserting a sample into one would crack the neighbouring
       cap. A global uv-area identity (outer ring less the holes) is the closing guard,
       since the per-slab tests cannot see a gap or an overlap between slabs. Ear
       clipping remains the fallback for holes that do not decompose this way.
    3. **Periodic band, interior rows first** — loops winding the period (rings
       subdivided into arcs) get full-period rows at the natural v values strictly
       between the rings (`RowedPeriodicBand`: natural u columns plus a closure
       duplicate, one `SweepMonotone` per adjacent chain pair — row-to-row strips ARE
       the untrimmed grid's zigzag), and a single winding chain gets rows between chain
       and pole with only the last row fanned to the pole point (`RowedPoleFan`).
       Chain-adjacent strips may still span many steps where a ring chain scallops
       through hole rims, so they get their own partial rows via `RowedStrip` on the
       strip's unrolled cycle — with the strip's two seam chords pre-split at the
       natural levels, which is legal precisely because a seam chord is an unrolling
       artifact internal to the face (the right chord is the left's exact one-period
       translate; each split vertex's 3D point is computed once and copied to its twin;
       every sub-chord is marked as boundary so the pair still welds bit-for-bit).
       Pole-fan edges are refinement-exempt: the pole's u is arbitrary, so a fan edge's
       uv u-span is an artifact, not curvature — refining it bent a *flat* vase disk
       into 467 folds. The rowless sweep (and behind it the old merge walk) remains the
       fallback for chains the rows decline.
    4. **Ear clip** — everything else, by an exact-coordinate clipper (shortest-diagonal
       ears, on-edge points block, holes bridged).

    **Reversing a face's polygons must not move the fan diagonal.** A reversed face
    (boolean output, pointing opposite its surface normal) has its polygons re-wound, and
    the obvious `Reverse()` turns `[a, b, c, d]` into `[d, c, b, a]`. Both are the same
    cyclic polygon wound the other way, so for the winding the choice is free — but a
    quad is triangulated downstream by fanning from vertex 0, so the first splits along
    a–c and the second along b–d. On a skewed non-planar grid cell those are not equally
    good triangulations, and `Reverse()` silently picked the wrong one for every
    subtracted tool's face. Measured on an M8 B-Rep threaded hole, whose sheared helical
    grid has cells with a diagonal ratio up to 40:1: **5 544 of 30 912 facets faced
    inward, worst agreement −0.163**, against zero folds and 0.99976 for the identical
    geometry unsubtracted (a threaded rod). The reversal now rotates so vertex 0 stays
    put — `[a, d, c, b]` — and the hole matches the rod at 0.99897.
    (`PolygonFan` has since made the downstream split a function of the geometry rather
    than of vertex 0, so the two spellings would now triangulate alike; the rotation stays
    because a polygon whose corner 0 wanders is still a polygon whose *audit* wanders, and
    a rule that cannot be broken by a re-wind is better than one that merely is not.)

    Oversized interior edges are then midpoint-split to the natural grid density with
    new vertices on the exact surface. Boundary vertices are always the exact shared edge
    samples, so seams weld at 1e-9. Routing between grid and trimmed paths is a two-sided
    3D match of loop samples against the natural grid boundary — precisely the invariant
    grid welding needs.

    **Why the strip path exists, and why the ear clipper is the LAST resort.** Ear-clipping
    a band is not merely wasteful, it is visibly wrong. The clipper's shortest-diagonal
    rule eats the dense boundary chains first, and three consecutive samples of a smooth
    boundary curve span a sliver whose normal is `T × K` — the curve's **binormal**, not
    the surface's. Decomposed, `T × K = k_g·N + k_n·(T × N)`, so the sliver only agrees
    with the surface where the boundary's **geodesic** curvature `k_g` dominates. A miter
    ellipse meets the top of a fillet tangent to the flat face, where `k_g` passes through
    zero: there the sliver's normal is perpendicular to the surface's and its sign is pure
    rounding noise, so half the slivers face inward. Measured on
    `Shape.Box(30, 20, 6).FilletEdges(2, topRim)`: **13 088 triangles, 808 of them
    inverted** (worst facet-vs-surface normal agreement −0.22), rendering as a dark folded
    lens at every mitered corner — now **280 with none** (worst agreement 0.99994).

    The cost stopped being quadratic too, and that mattered more than it looked: the
    clipper left long interior diagonals that refinement then subdivided, and the
    monotone-decrease rule that keeps that cascade terminating cut it in unpredictable
    places, so the ear-clipped mesh **did not converge**. Measured on the same box:
    13 088 triangles at curveSamples 24, 147 744 at 96, 642 160 at 144, 904 928 at 176,
    621 392 at 192 (not even monotonic), and refusal at 256; the volumes wandered —
    3516.70, 3517.03, 3516.82, 3516.84, 3517.04 — against an analytic 3517.2274 they
    never approached. The strip is linear in the sample count (280 → 552 → 1096 → 2184)
    and converges quadratically from inside, so the mesh volume moved from −1.5e-4 to
    −4.8e-5 of the analytic prism and keeps improving.

    **The band-with-holes case is the same defect in its purest form, and it is not the
    shortest-diagonal rule's fault at all — it is forced.** Both ring chains of a band lie
    at a constant v (an extruded surface's rings are its v-domain ends, and the pullbacks
    are *bit-identical*), so consecutive ring samples are EXACTLY collinear in uv and the
    clipper refuses those corners as zero-area ears. The only clippable ears in the
    unrolled rectangle are its own four corners, so the clipper can only **fan**, and
    refinement then bisects the fan chords into slivers. Measured on the docs'
    oblique-section housing (`Box(44,44,30) − Cyl(r13) − Cyl(r5)·RotY(π/2)`) at
    segmentsPerCircle 128: the bore wall went from **12 164 triangles at a worst
    facet-vs-surface normal agreement of 0.0198** (an 88.9° sliver — no triangle was
    strictly inverted, which is why a fold *count* alone would have missed it) to **416
    triangles at 0.99981**, and the whole mesh from 13 480 triangles to 1 732. Volume
    excess over the analytic 40 699.916 at segmentsPerCircle 32/64/128/256 was
    61.19 / 18.60 / 13.40 / 11.25 — ratios 3.29, then **1.39, then 1.19**, stalling near 11
    — against 76.20 / 21.49 / 5.97 / 1.82 now, i.e. ratios 3.55, 3.60, 3.27. The
    independent implicit route (`ToImplicit()` + Surface Nets at resolution 256) lands at
    −3.79 of the same analytic value, so the two code paths bracket it.

    Two things the merge walk of path 1 could NOT have done here, worth keeping: a merge
    pairs the chains by u, so where one chain carries many samples between two of the
    other's — a drilled breakout curve against a coarse ring — it fans them from a single
    far vertex, and the moment that stretch turns back on itself (the breakout's right-hand
    end) consecutive fan triangles invert. The **stack sweep pops only at convex turns**, so
    it is correct on any monotone slab, and on the free slabs it reproduces exactly the
    natural grid's zigzag.

    **Interior rows are what retired "carried by refinement".** Measured before/after on
    `Sphere(10) − Cylinder(3, 40)` (a drilled sphere, whose wall is a two-ring periodic
    band spanning nearly pole to pole): **43 948 facets / 12 folds / worst −0.2022 →
    3 244 / 0 / 0.9994** at 32/24, an outright refusal at 128/96 → 49 902 clean facets,
    and volume error falling at ratios 4.35 / 5.08 per density doubling (napkin-ring
    analytic 3 636.2246) — it is now a corpus member. `Box(20,20,20) − Sphere(12)`, the
    corpus's hardest shape: **101 246 / 266 folds / −0.2426 → 4 608 / 0 folds / 0.7024**
    at 48/24, tessellating at 96/48 where it used to refuse (its residual — a narrow
    column at each hole rim's u-extreme, where the rim tangent goes vertical and no
    level path can anchor — keeps it just below the corpus floor; filed in todo.md).
    With rows in place the corpus measures **refinement idle on 16 of 19 members'
    trimmed faces** (identical output with it on or off); it stays for the residual
    columns it still genuinely fixes (Box − Sphere's base has 3 marginal folds that
    refinement REPAIRS), demoted from convergence mechanism to residue duty. Two rules
    fell out: the refinement step metric is **per-axis max-norm**, never the 2-norm — a
    grid cell's own diagonal spans one step in EACH axis, and a 2-norm bisects the very
    grid that defines the quality bar — and **pole-fan edges are refinement-exempt**,
    because the pole's u is arbitrary so a fan edge's uv u-span is an artifact
    (refining a *flat* vase disk's fan bent it into 467 folds at worst −1.0).
  - **Progress + cancellation** (`ProgressCancel? progress = null`, free when absent) is
    polled at **edge and face boundaries** — the coarse checkpoints, since one trimmed face
    is an indivisible ear-clipping job — and cancellation throws rather than returning a
    partial mesh. It is safe to cancel here precisely because the tessellation's own result
    is discarded wholesale; the rule the document model learned the hard way is that
    abandoning work whose result is **cached** (a `Shape`'s lowered `BrepSolid`) leaves the
    cache claiming a lowering it never produced, so **never pass a cancellable progress
    from inside a lowering**. Tessellating an already-cached solid is downstream of the
    lowering and may observe the token.
    Routing between grid and trimmed paths is a two-sided 3D match of loop samples
    against the natural grid boundary — precisely the invariant grid welding needs.
    Numerical lessons baked in: earcut's exact-collinear filtering would drop
    iso-parameter run vertices (uv-collinear is *not* 3D-collinear — an unzippable
    crack), jittering breeds zero-area folds that refine into non-manifold welds, and
    ~1e-9 inverse-evaluation jitter demands an epsilon blocking band plus midpoint→vertex
    snapping during refinement (the same band makes bridge visibility treat
    nearly-collinear contact as touching — exact-zero cross products miss it by an ulp).
    The strip's own epsilon — how flat a step must be to count as a rung — is the 1e-6
    inverse-evaluation tier expressed **relatively**, `1e-6 × the loop's extent in that
    parameter`: u and v carry no model units, so an absolute epsilon there would be
    meaningless. Marching-tracer polyline edges are sampled at their exact vertices —
    chordal midpoints sit off the surface and would fail inverse evaluation. `SampleEdge`
    asks `FaceGeometry.ExactSampleParameters` for those rather than reading
    `PolylineCurve3d.VertexParameters` itself: its own copy of the test recognized only a
    RAW polyline, so an edge whose curve is a `CurveSegment` wrapping one — what the face
    splitter hands back after a cut — fell through to the uniform path with every interior
    sample a sagitta off the surface.

    **A trimmed face that cannot be tessellated now refuses**, naming the surface type,
    where it sits, its loop shapes, the sample counts in force and the reason (failed
    pullback, unsupported winding, refinement that would not converge). It used to fall
    back to the surface's natural grid, which covers the whole parameter rectangle rather
    than the trimmed face — not merely coarse but the *wrong* geometry, welding into an
    open mesh with no complaint. The sample counts belong in the message because some
    failures only appear at high density: with the ear clipper, refinement on a filleted
    box's bands gave up at `curveSamples = 256` (measured; the backlog note guessed 192,
    where it still converged — after 900 k triangles) and the silent fallback handed back
    an open mesh.

    Remaining gaps: pole-bounded single-chain bands with holes and |winding| > 1 loops
    are refused (they used to fall back to the grid), and a hole straddling every
    possible seam (covering a full period in u) is unsupported. **Neither refusal is
    reachable from the `Shape` API** — a latitude cut does give a sphere a pole-bounded
    cap, but drilling it off-axis makes the boolean re-split so the bore's rim lands on
    the two-ring band below; cutting lower, or the same on a cone or a torus, fails
    earlier in the boolean; and |winding| > 1 needs a helical intersection curve, which is
    refused before tessellation. `TrimmedFaceRefusalTests` locks that verdict and drives
    both refusals directly on hand-built faces so the messages cannot rot.

    **Whole-corpus quality gate.** `TessellationCorpusQualityTests` audits 21 named
    constructions — drilled plates, cross-drills, spherical cavities, threaded rods and
    holes, lofts, shells, drafts, mitered and whole-solid fillets, chamfers, vases,
    partial revolves, sweeps, tori, cones, sketch pockets, Bézier engraving, wedges —
    facet by facet against the exact surface each one samples, at 16/8, 48/24 and 96/48.
    Its measurement rules live in `TessellationQuality` and are load-bearing: the
    reference normal is the mean of the surface normals at the triangle's three
    **vertices** (a centroid sits a sagitta inside a curved surface, so projecting it
    fails and the assertion silently checks nothing); the audit runs on **unwelded
    per-face polygons** via the internal `BRepTessellator.TessellateByFace`, because
    welding destroys the facet → face attribution; and polygons are **fanned from vertex
    0**, which is how the render mesh triangulates a grid quad — auditing a quad as a unit
    would have missed the reversed-face defect entirely, since its whole mechanism is the
    fan diagonal moving. The floor is one formula for every surface family,
    `cos(3 · 2π/n)` — three natural grid steps of surface normal, the allowance being for
    facets where two independently sampled boundaries meet. The worst case is the
    cross-drilled housing (0.6431 at 16 segments, 0.9925 at 48, 0.9995 at 96), because
    its breakout curves are tracer polylines baked in at boolean time and do not refine
    with `segmentsPerCircle`. Everything else measures above 0.999 at 48/24.
- **B-Rep booleans**: `BrepBoolean.Union/Intersection/Difference` — the full pipeline
  (face-pair intersection, seam-aligned splitting, SDF-probe classification, reversed
  subtracted faces, topological seam sealing via `TopologyEditor.SealSeams`). See
  design.md §5. Transversal and coplanar-PLANAR cases are handled; inputs are consumed;
  output passes `Validate()` with correct genus and exact volumes.
  - **Coincident (flush) planar surface** (`CoplanarFaces.cs`) — flush embossing, stacked
    plates, blocks butted together, a pocket floor flush with the host's own face. The
    model is the mesh boolean's, translated: the shared region's rim is imprinted by the
    ordinary TRANSVERSAL curves of the neighbouring faces, and the coincident fragments
    are then classified by **normal agreement** instead of by an inside/outside probe,
    which reads zero there and decides nothing (the B-Rep twin of the winding number
    being ½ on a shared surface). Agreeing normals mean both solids lie on the same side,
    so the surface bounds the union and the intersection and vanishes from the
    difference; opposing normals mean the solids mate back to back, so union and
    intersection bury it and only the difference keeps it. **Exactly one copy can ever
    survive and it is always the FIRST solid's** — the asymmetry is deliberate and is
    documented in design.md §5. Coincident CURVED surface (a shaft in a bore of its own
    diameter) is refused BY NAME before any splitting.
    - Three rules had to be added around the classification itself, and each was found by
      a case that failed without it. **A curve that never reaches a face's INTERIOR must
      not split it**: when two solids mate, each neighbour face's own boundary IS an
      intersection curve (a boss's wall meets its host's top plane exactly along the
      wall's bottom rim), and splitting a face along its own boundary is what the
      arrangement tracer cannot close. **A pair whose bounds meet in a single POINT is
      dropped**: butting a boss against a plate puts the two side walls corner to corner,
      and their carrier planes still cross in a full line that runs clean through the
      plate's wall — the line is real, the contact is not. **The disjoint fast path is
      disqualified by a shared plane**: two stacked plates of the same footprint meet
      only along their own boundary edges, so after the first rule every curve is gone and
      the operands look disjoint, which returned them as two touching shells — precisely
      the fusion failure this tier exists to fix. All three are gated on a shared plane
      existing, so a purely transversal boolean takes exactly the path it took before.
    - **The rim imprint has a second source, and it is needed.** The transversal path
      supplies the rim for an unbounded `PlaneSurface` neighbour, but a sketch extrusion's
      wall is a *bounded* patch and `TryPlaneExtrudedSection` deliberately reports NO
      section when the cutting plane is flush with the generator's rim. Embossed text is
      exactly that case, so a coplanar face also takes the partner face's OWN boundary
      curves as rim curves (skipped where an existing curve already covers them, since
      splitting twice along one curve breaks the tracer). Taking the partner's curves is
      also the best possible weld: the new edges ride the geometry the other solid already
      references, so `SealSeams` pairs them by construction rather than by tolerance.
    - Coplanar overlap is decided by **sampling the shared area**, not by probing
      centroids: two plates overlapping in a strip have neither centroid inside the other,
      and the first version of the test missed exactly the case that most needs it. A miss
      is safe in one direction only — it leaves the boolean on its pre-existing path,
      which fails loudly rather than producing wrong geometry.
  - **The result is verified before it is returned.** Every operation checks that the
    assembled solid is two-manifold (each edge used by exactly two coedges, every loop
    chaining end-to-start) and throws `BrepBooleanException` otherwise, naming the
    operation, counting the unpaired edges and locating one crack. An unclosed result is
    the project's worst failure mode: it tessellates into an open mesh with no complaint
    and exports an unprintable STL, and only surfaces if somebody thinks to call
    `Validate()`. `ShapeCompiler` catches the exception and appends the route that does
    work — `Shape.From(shape.ToImplicit()).ToMesh(quality)`. It deliberately does NOT
    fall back automatically: that would make `Explain(Representation.Brep)` a lie (it
    reported Native) and would quietly downgrade an exact model to a polygonized one.
    Note the limit of the check — it catches *unclosed* results, not *wrong but closed*
    ones (a tool buried as an internal cavity is perfectly manifold), so end-to-end tests
    must still assert analytic volumes.
  - **Traced curves are SNAPPED onto their exact boundary landing** before either solid
    splits (`SnapTracerEnds`). The marching tracer breaks its step only AFTER the
    corrector's parameters leave the domain, so a traced curve always stops up to one
    march step short of a bounded surface's edge; where that edge also bounds the face
    being split, the polyline crosses nothing, `FaceSplitter` finds ZERO crossings, and
    the face is whole-classified. That is what cracked a whole-solid fillet along entire
    tangency edges: measured on `FilletAllEdges(20×14×8, r2) − Ø6 cylinder`, the four band
    curves ended 5.5e-5 and 1.1e-2 from the two tangency lines and produced no crossings
    at all. The landing is SOLVED, not extrapolated — E(t) = S(u, v) is a well-posed 3×3
    Newton system on the exact boundary edge and the other solid's exact carrier, seeded
    from the polyline's own last vertex (which already lies on S, so only t moves) — and
    it happens ONCE, on the single curve object both faces share, so the two solids get a
    bit-identical endpoint. Snapping per face during splitting would instead give them
    endpoints a sagitta apart, opening a pinhole at every crossing.
    Together with the face splitter's exact interior probe (see the BRep README) this
    closed the band-crossing family: a Ø6 bore down through two fillet bands, and Ø4
    cross-drills along Y and X, are now `Validate`-clean and closed, satisfy
    |A| − |A−B| = |A∩B| to 0.02–0.06 % of the removed volume, and converge with
    tessellation density. It also closed a family nobody was aiming at — a bore swallowing
    a rounded rectangle's corner and breaking out through both adjacent walls now
    converges QUADRATICALLY onto its analytic volume (1.0e-4 / 2.7e-5 / 6.8e-6 / 1.7e-6
    relative at 32/64/128/256 segments). **What remains** is a tool drilled ALONG a band's
    own axis, whose intersection runs the band's whole length instead of crossing it, and
    the quality note that a baked tracer polyline keeps a fixed sample count while the
    grid around it refines, so facet-vs-surface agreement on those bands DEGRADES with
    density (0.999 at 32 → 0.90 at 192; no folds, and volumes still converge).
  - **Straight-edged sketch extrusions (pockets, slots, polygons, engraved lettering)
    are exact**, via `SurfaceIntersection`'s bounded planar carriers — see the BRep
    README. Before that they were the headline silent failure: the marching tracer
    stopped short of each wall's ends, the pocket outline never closed, and the boolean
    returned single-use edges (open mesh, no error) or — when it found no curves at all —
    buried the whole tool as an internal cavity, giving a closed `Validate`-clean solid
    with the wrong volume.
  - **Cut-through-hole differences work**: a tool passing through an existing bore
    (e.g. a slot narrower than the bore) splits the bore wall into trimmed fragments,
    which tessellate via `TrimmedFaceTessellator`. Kernel work that enabled it:
    tolerant curve pullback (`FaceSplitter.PullCurveRuns` — cut curves may leave a
    bounded band's surface; on-surface runs get extrapolated seed samples at their cut
    ends), 3D curve–curve Gauss–Newton crossing refinement (projected-uv iteration
    failed near domain-edge rings; both solids now converge to the same exact point),
    slightly-inclusive crossing seeds (a cut through a split-created vertex lands at
    tp = 0/1 up to rounding), reversed-face splitting (CCW↔CW-aware sub-face tracing,
    `IsReversed` preserved through all split paths), a mandatory break at every closed
    intersection curve's domain start on both sides (the wrap-splitting side anchors
    its seam vertex there), and `ProbePoint` preferring the largest triangle's centroid
    (sliver centroids sit within the classification SDF's sagitta of the other solid's
    curved surface).
  - **A face's whole curve list goes to `FaceSplitter.SplitByCurves` at once**, which owns
    the choice between the curve-at-a-time cascade (what `SplitAll` used to spell inline, and
    still what every curve crossing the face boundary at both ends gets, bit for bit) and one
    simultaneous arrangement. Only the second can place a curve that TERMINATES inside the
    face, which is what a face-pair curve becomes once it is clipped to the other face's trim.
  - **Intersection curves are CLIPPED to the pair's shared trim** (`ClipToFace`).
    `SurfaceIntersection` intersects CARRIERS, and a carrier is either unbounded (a plane) or
    bounded only by its own parameter rectangle (a helical band's domain is the bounding
    rectangle of a parallelogram-shaped face), so the curve it returns runs past both faces.
    Each face's splitter already discarded the stretches outside ITSELF; nothing discarded the
    stretches outside the OTHER face, so a face was split along geometry the pair does not
    share. Measured: a pocket's four wall lines cut a host face into **9** fragments where the
    tool's footprint asks for **2** (a plate with a flush pocket went from 18 faces to 11, a
    two-bore plate with a pocket from 20 to 13, with the display mesh's polygon count and
    volume unchanged), and a chamfer cone's cut ran past a threaded rod's cap and arrived at
    the cone face as a dangling edge no arrangement can trace.

    **Three rules carry it, each paid for by a measured failure.**
    (a) **The rule is ASYMMETRIC**: each face drops only the stretches that lie inside ITSELF
    and outside its partner. Handing both faces the intersection of the two trims — the
    obvious symmetric form — is wrong wherever the two faces share a boundary, because
    clipping to the partner then cuts the curve exactly ON this face's own boundary and turns
    a transversal crossing into a tangential touch (`Box(20,20,10) & Box(10,30,10)`, whose
    side walls meet along their full height: every vertical curve stopped on both walls' rims
    and tracing did not close). Keeping the stretches outside THIS face costs nothing — the
    splitter drops them by loop parity anyway — and restores the crossing.
    (b) **The breakpoints are shared**, computed once per face pair from BOTH faces' exact
    `CrossingParameters`, so wherever the pair genuinely shares a stretch the two sides cut it
    at identical parameters and the seam edges pair.
    (c) **The containment test errs toward KEEPING** (`InsideForClip`): a stretch wrongly kept
    only reproduces the un-clipped behaviour, one wrongly dropped loses a seam silently and
    the boolean returns two touching shells. Three ways: a probe that does not project onto
    the surface at all counts as inside; parity is `FaceGeometry.ContainsTwoSided`, so a
    POLE-BOUNDED face answers instead of calling every point on itself outside (without it a
    sphere-through-a-box union lost its whole seam curve and came back at Euler 4); and a
    stretch running ALONG the boundary counts as inside, because where two solids mate the
    shared rim IS a face boundary on one side.

    **A curve surviving whole is returned as ITSELF**, not as a full-domain segment — a closed
    curve must stay closed, since wrap-splitting and hole-splitting both key on `IsClosed` —
    so every boolean whose curves already lie inside both trims gets bit-for-bit what it got
    before. The closed-curve **seam anchor is conditional on both sides still seeing the closed
    curve** (`SeamBreaks`' `anchored` flag): a slot through a bore shares with the slot's floor
    only the arc inside the slot's width, so the bore wall is cut there and never wrap-splits
    while the floor still sees the full circle — the stale anchor left the +x arc as two edges
    against the wall's one, six unpaired edges in all.
  - **`ProbePoint` decides "this fragment is a band" by net u DRIFT, not u SPAN**
    (`FaceGeometry.LoopWrapsPeriod`, the one rule the face splitter's tracing and
    wrap-splitting also ask). The band path probes halfway toward the surface's own v
    domain edge, which is right for a ring-bounded band and catastrophic for a
    CONTRACTIBLE facet that merely reaches most of the way round — a threaded rod's
    end-chamfer facet spans 272° and closes — because the probe then lands outside the
    fragment and the boolean classifies it away. Pinned by `ProbePointWrapTests`, whose
    fixture asserts it still CARRIES the configuration (one loop, span past three quarters
    of the period, no wrap) so it cannot quietly stop testing it.
  - Drilling works into **cylinders** exactly as into boxes (the cap bounds a closed
    circular edge, so a different split/re-weld path runs): for well-posed inputs the
    result is `Validate`-clean with the right genus and exact volume in all three
    representations (`HoleTests.CylinderDrilling_*`). The transversal-only contract still
    bites on *degenerate input*, and identically on boxes: a through-hole whose `depth`
    equals the plate thickness leaves the tool's flat bottom **coplanar** with the far
    cap (pass a depth past the far face), and hole features that are **tangent or
    overlapping** on the drilled face (e.g. Ø10 counterbores at 10 mm pitch) pinch the
    shared face into a non-manifold result. A feature that breaks out through the curved
    wall is likewise unsupported. These surface as `ProbePoint`/tessellation errors, not
    as silently-wrong geometry.

(The `Scene`/`Part` document model lives in `EngrCAD.Modeling`, which layers on top of
this project's conversions.)
- **Mesh → Implicit**: `MeshSdf(mesh)` — signed distance to a closed manifold mesh:
  branch-and-bound nearest-triangle search over a BVH (Core's
  `Distance3d.ClosestPointOnTriangle`, whose `out TriangleRegion` exists for this caller —
  the sign needs to know which feature won, not just where it is);
  sign from the angle-weighted pseudonormal of the closest feature (Bærentzen–Aanæs),
  exact for watertight meshes even at edges and vertices. `Evaluate` is allocation-free
  in steady state — the nearest search goes through `Bvh.Nearest<TMetric>` with a struct
  distance metric, not a closure (0 B measured over 100 k calls; locked by
  `MeshSdfTests.Evaluate_SteadyState_DoesNotAllocate`). The result is a first-class
  `Sdf` node composable with the whole implicit engine.
  **Batches stay the scalar loop, deliberately.** `MeshSdf` does not override the batch
  seam, and `MeshSdfBatchTests` pins that (batch equals scalar bit for bit, including on
  the surface, where any seeded search breaks). The measurement behind the decision: a
  narrow-band bake of a mesh field spends **74–85% of its wall clock inside these queries**,
  so there is real headroom — but *seeding* the branch and bound with the previous coherent
  sample's answer, which is provably result-identical and looks free, measured only
  **1.12–1.20×** on the most coherent run available and a small net **loss** on scattered
  probes. The reason is worth remembering: **a nearest-first branch and bound is already its
  own seed** — descending the nearer child first reaches a tight bound in O(log n) node
  tests, so a seed can only save part of the first descent. (A standalone prototype claimed
  1.88×; its baseline went through a `Func` delegate per triangle while the seeded path
  called the kernel directly. The gap was the delegate. Never benchmark an optimization
  against a baseline you wrote differently.)
  **The packet query was the remaining lever. It was built and measured too, and it does not
  survive contact with the batch seam** (`MeshSdfPacketBenchmark`). One traversal per
  coherent group, with per-point pruning at the leaves so the shared work is the node tests,
  wins **1.45×** on a compact 2³ block of grid points — but a packet's shared bound is
  governed by the group's **diameter**, since a node is visited whenever it could beat the
  *worst* member's current best. The batch seam hands over a flat span that every bulk
  consumer generates **z-fastest**, so the real groups are rows: the same 8 points in a row
  measure **0.86×**, and 64 of them span 1.9 units on a 3-unit model, at which point the
  shared bound is the whole model and the packet is a brute-force scan (**0.30×**).
  `MeshSdf` cannot regroup a collinear run into blocks, and teaching the batch contract to
  carry "these points form a compact block" is a large API change to buy a win in a shape
  no caller produces. Tie-breaking never became the question. Two negatives ride along:
  seeding the packet from one exact query at the group's centre changes nothing
  (0.80–1.31×, and it makes the best case *worse*), and pruning on **squared** distances
  throughout — removing a `Math.Sqrt` from every box and triangle test — measures
  0.94–0.99×.
  The sign source is opt-in via `new MeshSdf(mesh, MeshSignSource.WindingNumber)`, which
  drives the fast generalized winding number (`MeshWindingNumber` in EngrCAD.Mesh) instead
  of the pseudonormal — same partition on watertight meshes, but also accepts **open**
  (non-watertight) meshes, where the distance is still to the existing surface and the sign
  degrades gracefully near holes. The default (`MeshSignSource.Pseudonormal`) is unchanged
  and still requires a closed mesh.
  **`MeshSdf` construction deliberately takes no `ProgressCancel`** — measured, then
  declined: on a 32 040-triangle mesh the pseudonormal constructor is 21.8 ms and the
  winding-number hierarchy 29.2 ms (8 cores). Cancellation in the viewer is granular to a
  whole part, which takes seconds, so checkpoints inside a 20 ms constructor buy nothing
  and would have to be threaded through call sites that sit *inside* cached lowerings —
  exactly where a token must not reach.

## Planar cross-sections (`PlanarSection`)

`projection(cut = true)`: the cross-section of a solid through a plane, as 2D
`Region2d`s in the plane's own coordinates. Nesting is re-derived by
`Region2d.FromLoops`, so a bore inside a plate becomes a hole without anyone declaring it.

- **`PlanarSection.OfMesh(mesh, plane)`** — `MeshPlaneCut`'s ordered boundary loops
  projected into the plane. Fidelity is the mesh's; a plane that misses the mesh returns
  an empty list rather than throwing.
- **`PlanarSection.OfSolid(solid, plane, chordTolerance)`** — the exact route:
  `SurfaceIntersection` per face, trimmed to the face, chained into loops. Fidelity is set
  by `chordTolerance` alone rather than by whatever tessellation the display uses, so a
  bore rim is as smooth as asked for; curved sections are INSCRIBED polygons (the same
  one-sided contract as `Sketch.ToRegions`), straight sections exact.

Three things make the B-Rep route close reliably:

1. **Edge crossings are the loop-assembly key.** A section curve leaves a face exactly
   where the plane crosses one of the face's EDGES, and that edge is shared with the
   neighbouring face — so the crossings are solved once per edge, by bisection on the
   edge's own exact curve, and both faces use the *same* point. Runs are then chained by
   node INDEX, not by welding two independently computed endpoints (which would be the
   1e-7 seam tier at best, with drift). The endpoints are the node POSITIONS, never the
   curve re-evaluated at the searched parameter — a ternary search leaves ~5e-11 residual,
   enough to stop a box's section corner being exactly a corner.
2. **Keep/drop probes sit at a piece's MIDPOINT**, never at an end (which is on the trim
   boundary, where containment is a tie) — the same rule `BrepBoolean` learned.
3. **Containment is decided by a TWO-sided v-ray parity.** Both directions agree for a
   properly closed trim (a vertical line crosses a closed loop an even number of times).
   They disagree exactly on a POLE-BOUNDED face, where one side of the domain is a point
   rather than a rim: a sphere's northern hemisphere has its only rim BELOW the cut, so
   `FaceGeometry.Contains`'s one-sided upward ray sees no crossing and calls the probe
   outside — which returned an empty section for every sphere. When the two disagree the
   probe is between the rim and the pole, hence inside.

Degenerate placements are refused with guidance rather than answered plausibly: a plane
**flush with a planar face** (the section there is an area, not a curve) and a plane
**containing a whole edge** (a sphere cut exactly at its equator — the section runs along
two faces' shared boundary, where every probe is a tie).

### Silhouettes (`PlanarSection.SilhouetteOfMesh`)

`projection(cut = false)`: the outline a body casts along the plane's normal. A through
hole survives as a hole; a blind pocket or an internal cavity does not. Every face's
projection is a region and the silhouette is their union — three things make that
affordable, and the ordering matters far more than the face count:

1. **Back faces are dropped first**, halving the input. EXACT for a closed mesh and only
   for a closed mesh: a ray along the normal leaves the solid through a front-facing face,
   so the front-facing projections already cover the whole outline. An open mesh keeps
   every face, because that argument does not hold.
2. **Faces are Morton-sorted by projected centroid**, so the fold merges neighbours first
   and intermediate boundaries stay simple. Merging face 1 with face 900 produces two
   disjoint regions and no cancellation at all.
3. **The fold is `Region2dBoolean.UnionAll`'s balanced tree.**

Measured on a torus tessellated at 64 segments (3072 front-facing faces): Morton-sorted
balanced tree **67 ms**, unsorted balanced tree **2.4 s** (36×), linear accumulate
**259 s** (3800×). A 128-segment sphere (12k front-facing faces) takes ~240 ms. Mesh
fidelity is the knob — the union is exact for whatever mesh it is given.

**Projected coordinates are quantized to 1e-12 of the outline's extent** before the union
(`PlanarSection.SilhouetteGrid`, the scale-free tier — never an absolute weld tolerance),
and this is load-bearing. Two mesh vertices on the same feature line — a torus's latitude
ring, a cylinder's rim — are only equal to within ULPS once projected, since each was
evaluated independently. Two edges that should be collinear then sit ~2e-16 apart: far too
small for the arrangement to see as a T-junction, far too large to ignore. The sliver cell
left between them is one ULP thick, so its interior sample rounds back onto its own
boundary, and the union's answer starts to depend on the merge order (measured: a
16-segment torus viewed side-on came out 60.42 unsorted and 59.33 Morton-sorted, the truth
being 60.42) — a 64-segment one threw "boundary tracing hit a dead end" outright. Snapping
to a grid ~4500 ULPs wide collapses those pairs to identical doubles, the arrangement
dedupes them as coincident edges, and no sliver is ever built. It is nine orders below the
chord tolerance a polygonal region carries anyway.

**The near-tangency "pinhole" is REAL GEOMETRY, not a boolean defect** — a finding that
overturned this file's own earlier diagnosis, and worth keeping because the wrong answer
was so plausible. A 64x48-tessellated torus seen side-on returns one hole of 1.45e-5 (about
2.4e-7 of the outline), and the standing explanation was cell misclassification in
`Region2dBoolean` at near-tangency. It is not: **780 of 780 probe points inside that hole
are covered by ZERO facets** of the mesh, tested with the exact `Orient2d` predicate over
every triangle, so the union the boolean returned is the correct union of what it was
given. What has the hole is the *tessellated solid's own shadow*. In the band
|z| in [r*cos(pi/n_minor), r] — the minor polygon's scallop, 4.28e-3 deep at n = 48 — the
discrete tube only reaches that height near its minor-polygon VERTICES, and the major
discretization breaks that thin band into lenses that need not overlap. The hole measured
1.16e-3 deep, a quarter of the scallop.

It is also not systematic: sweeping the density gives holes at 64x48 and none at 32x24,
96x72, 128x96, 64x96 or 128x48, because whether two neighbouring lenses overlap is an
alignment question. **A silhouette is the shadow of the mesh you give it, not of the
surface you meant** — the same rule the remesher's feature detection follows — so filter
holes by area if you want the smooth body's answer, and refine the tessellation if you want
the discrete one to converge onto it. `TorusSilhouette_AlongTheAxisIsAnAnnulus_AcrossTheAxisIsSolid`
now asserts the strong form: every hole it finds is uncovered by every facet.

## Mass properties (`BrepMassProperties`)

`BrepMassProperties.Compute(solid, density, options)` gives a `BrepSolid`'s volume, surface
area, centre of mass and inertia tensor (OCCT's `BRepGProp`), returning the same
`MassProperties` type `EngrCAD.Mesh` defines — because the route is **tessellate-then-sum**,
deliberately:

- The alternative, Gauss quadrature over each exact surface, needs the trimmed parameter
  domain scanned against the trimming curves (OCCT's `GProp_Domain`). This kernel's faces
  are trimmed by pulled-back polylines whose parameter-space boundary is itself approximate
  for marching-tracer edges, so quadrature over that domain would not be exact either — it
  would only hide its error behind a more impressive-looking integral. Tessellating keeps
  the error in one place, measurable, and buyable.
- **Planar-faced solids come out exact.** Triangulating a planar polygon covers it exactly,
  so the divergence-theorem sum is an identity, not a tolerance claim: a box, a prism, an
  extruded sketch or a drilled plate's flats agree with the closed form to round-off, and
  the answer does not change with the tessellation setting at all.
- **Curved faces converge as O(h²)**, always under-estimating (the tessellation is
  inscribed) — the ≈ 2π²/3n² chord deficit of an inscribed n-gon, measured at 1.6e-3
  relative for a cylinder at n = 64 and 4.0e-4 at n = 128. Because that is a clean O(h²)
  series, `Extrapolate` (**on by default**) integrates at n and 2n and Richardson-cancels
  it. Measured relative volume error at the default n = 64: cylinder 1.6e-3 → **1.9e-7**,
  sphere 2.2e-3 → **4.8e-7**, torus 2.0e-3 → **3.7e-7**, and a *drilled plate* — a boolean
  result whose bore wall is a trimmed face — 1.1e-4 → **1.4e-8**. It costs a second
  tessellation at double density, and it is a no-op on planar-faced solids (both densities
  tessellate identically, so (4P₂ − P₁)/3 returns P). Turn it off for geometry whose
  tessellation error is not smooth in h: a face that takes the trimmed path at one density
  and the grid fallback at the other jumps rather than converges, and extrapolation
  amplifies a jump by 4/3 instead of cancelling it.

So: **exact for planar-faced solids, ~1e-7 relative for curved ones out of the box.**

`ShapeHealing` (EngrCAD.BRep) has its geometric acceptance tests here
(`ShapeHealingIntegrationTests`), since confirming that a repaired face soup measures as the
same body — and tessellates closed — needs both projects.

## Planar iso-contours (`SdfContours`)

`SdfContours.OnPlane(sdf, origin, uSide, vSide, uSamples, vSamples, levels)` samples an
SDF on an arbitrary planar grid (the parallelogram `origin + u·uSide + v·vSide`, one
batch `Evaluate` call for the whole grid) and marching-squares each requested iso level
into line segments with 3D endpoints in the SDF's own space — the geometry behind the
viewer's section-plane isolines (d = 0 is the surface cross-section; ±k·spacing
visualizes the field). Properties the consumers rely on, locked by `SdfContoursTests`:

- **Deterministic and chainable**: crossings on a cell edge are interpolated from the
  same two samples with the same expression on both sides, so touching segments meet
  *bit-identically* — loops close under exact endpoint equality (a contour passing
  exactly through a sample node is shared by all four surrounding cells, multiplicity
  above two there).
- **Accuracy**: linear interpolation places crossings within O(h² · field curvature)
  of the true iso point for grid step h (a radius-r circle section errs by ~h²/8r).
- Ambiguous saddle cells resolve by the cell-center average — the average of the
  four corner *samples*, locked by a hyperbolic two-sphere section test (diagonal
  inside corners connect exactly when the corner average goes negative); the plane
  is fully general (pass the section plane mapped through an inverse instance
  transform — affine maps take the sample rectangle to a parallelogram, which the
  parameterization represents exactly).
- Sample/value scratch comes from `ArrayPool`; levels that never cross return empty
  segment lists.

## Remeshing against a field (`SdfProjectionTarget`)

`new SdfProjectionTarget(field, iterations = 2)` implements EngrCAD.Mesh's
`IProjectionTarget` over any `Sdf` — the quality-control pass for implicit output, and the
completion of a seam the mesh engine deliberately left open (the interface lives there so
the mesh kernel needs no dependency on the implicit engine; this is the consumer that
supplies it). One step is the Newton step `p' = p - d(p)·grad d / |grad d|`, and the
central-difference step is `5e-6 ×` the field's bounding-box diagonal — relative, never an
absolute constant, so it works at 1e-4 scale.

- **For an exact field, one step is not an approximation at all**: the gradient points
  straight away from the closest surface point and `|p - c| = |d(p)|`, so the step lands on
  `c`. Iterating exists for the fields that are *not* exact — CSG differences, smooth
  blends, a `MeshSdf` of a coarse mesh are all correct-sign **lower bounds** with
  `|grad d| <= 1`, so one step under-shoots and the residual shrinks by `(1 - |grad d|)` per
  step.
- **The guarantee is one-sidedness, and only that.** A 1-Lipschitz lower bound puts the
  surface at least `|d(p)|` away in *every* direction, so a step of exactly that length can
  never cross it however wrong the gradient direction is — which is why no damping or step
  limiting appears in the code. `|d|` is *not* guaranteed to decrease.
- **The counter-example is a plain CSG difference, and it is pinned by a test.** Inside the
  material a subtracted tool removed, the field measures the distance to the tool's own
  surface — a face that is not there — and the gradient jumps where the branch switches, so
  two branches trade the point back and forth. Measured on `Box(2,2,2) - Sphere(1.2)`: a
  probe 0.14 above the removed cap is exactly where it started after six steps, while the
  true distance to the real rim is 0.45. This is the same "correct-sign lower bound near a
  subtracted tool's fictitious faces" the modeling layer already warns about, seen from the
  other side.
- Which matters little for the job: a remeshed vertex starts **on** the surface and stays
  within a fraction of an edge length of it, where every one of these fields is locally the
  exact distance to a real face. Project points that are already near the surface; do not
  use this as a general closest-point query.
- It is an **oriented** target: `Project(point, out normal)` reports the unit gradient the
  last Newton step computed, so face-aligned (RZN-flow) reprojection gets its orientation for
  free. A point already exactly on the surface takes no step, so the gradient is read where it
  stands rather than reporting "unoriented" — which would send a face-aligned remesh down its
  fallback path for precisely its best-placed triangles.
- Measured on the pairing it was written for — `SurfaceNets.Polygonize(Sphere(1), 32)`
  remeshed at target edge 0.12 for 8 passes — the worst vertex-to-field distance drops by
  more than an order of magnitude while the volume stays within 2% of the exact sphere.
  Cost is 7 field evaluations per step, so wrap an expensive AST (a `MeshSdf` above all) in
  `Sdf.Sampled(...)` or `Sdf.NarrowBand(...)` first.

## B-Rep feature edges (`BrepFeatureEdges`)

`BrepFeatureEdges.Extract(solid, segmentsPerCircle = 96, curveSamples = 48,
sharpAngle = 30°)` produces display-overlay line segments from the solid's ACTUAL
B-Rep edges — the exact-geometry alternative to mesh-dihedral extraction
(`MeshFeatureEdges`): a rim circle sampled here stays a smooth circle at any mesh
tessellation, because segments come from the edge curves via the tessellator's own
`SampleEdge` rules (circles at `segmentsPerCircle`, helices angularly, tracer
polylines at their exact vertices, lines as 2 points). Sharpness is decided on the
exact surfaces: adjacent faces' outward normals (`BrepQueries.NormalAt`, reversal
applied) are compared at three interior probe points — smooth seams (a periodic
face's own seam edge, wrap-split sub-band junctions on one carrier, sphere
generator seams) are omitted, boundary/non-manifold edges and unprobeable edges
(tracer polylines are on-surface only at vertices) are kept: draw rather than
hide. Consumed by `Part.GetFeatureEdges` in EngrCAD.Modeling, which both viewer
render paths use.
