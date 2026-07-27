# EngrCAD.Mesh

The discrete (mesh) geometry engine: a half-edge polygon mesh with construction,
traversal, metrics, algorithms, and GPU/export extraction. Depends only on
`EngrCAD.Core`.

## Contents

- **`HalfEdgeMesh`** — half-edge (DCEL) structure over struct-of-arrays storage.
  Boundary edges get explicit half-edges chained into boundary loops, so `Twin` always
  exists. `Build(positions, faces)` validates manifoldness (rejects non-manifold edges,
  inconsistent winding, bow-tie vertices); `Validate()` checks structural invariants.
  Metrics: surface area, signed volume (`Volume` requires closed topology,
  `SignedVolume` doesn't), Euler characteristic, boundary loops, bounds.
  Topology is immutable after `Build`; algorithms produce new meshes.
  `Transformed(matrix)` maps every position through an affine matrix in one call;
  negative-determinant maps (mirrors) reverse face winding so closed solids stay
  outward-oriented with positive volume.
- **Handles** (`Vertex`, `HalfEdge`, `Face`) — cheap struct wrappers designed for fluent
  LINQ traversal (`vertex.OutgoingHalfEdges()`, `face.AdjacentFaces()`, `face.Bounds`).
- **`EditableMesh`** — the mutable companion (index-based, like g3's `DMesh3`;
  `HalfEdgeMesh` stays immutable): `FromMesh` → guarded Euler operators → `ToMesh`
  (compacts live elements and re-validates through the manifold-checking `Build`).
  Same SoA half-edge layout with explicit boundary half-edges, plus alive flags and
  free lists chained through the dead slots (freed indices are recycled; live indices
  never move until compaction), and `Timestamp`/`ShapeTimestamp` counters so caches and
  spatial trees can invalidate. Operators return a `MeshOperationResult` and run their
  entire guard set **before** the first mutation — a refusal never touches the mesh:
  - `SplitEdge` (interior 2→4 triangles / boundary 1→2 + boundary-chain insert; exact
    lerp position at parameter *t* measured from the passed half-edge's origin),
  - `FlipEdge` (interior triangle pairs; refuses when the target edge exists, which
    also blocks all valence-3 endpoint flips),
  - `CollapseEdge` (destination merges into origin; link condition — shared neighbors
    must be exactly the opposite apexes — plus interior-edge both-endpoints-boundary
    bow-tie, duplicate-edge, isolated-tetrahedron, last-triangle, and wire-edge
    (`IsolatedEdge`, defensive — `Build` cannot produce a face-less edge) guards; the
    decimator keeps its own equivalent on its face-set scratch state),
  - `PokeFace` (any n-gon → fan around centroid or explicit point; the reported fan
    faces come back as an `ImmutableArray`, not a caller-writable `int[]`),
  - `MergeEdges` (welds two **boundary half-edges** head-to-tail — the crack-closing
    primitive; handles shared-endpoint seams and full slit closure, and automatically
    welds the doubled boundary edges the guards deliberately admit, g3-style),
  - `SetPositions(vertices, positions)` — many vertices as **one** operation (one change
    record, one timestamp bump, one DEBUG validation). A per-vertex `SetPosition` loop over a
    whole-mesh smoothing pass is O(n²) in DEBUG, which is what the remesher needs this for;
    `Valence` and `OutgoingHalfEdges(vertex, Span<int>)` are the allocation-free ring
    queries beside it (the iterator overload allocates an enumerator per call).
  Every vertex keeps the invariant that its outgoing pointer prefers the boundary
  half-edge, so `IsBoundaryVertex` stays O(1) through arbitrary edit sequences.
  `Validate()` checks the full structure (twin involution, ring closure/completeness,
  single boundary fan per vertex, free-list integrity, count agreement) and runs
  automatically after every mutation in DEBUG builds.
- **`MeshChange` / `MeshChangeSet`** — undo/redo change records (g3 `DMesh3Changes`
  pattern implemented as an exact slot journal): while a change set is active
  (`BeginChangeSet`/`EndChangeSet`), every operation emits a reversible record of its
  primitive slot writes (old → new, free lists and counters included). `Apply`/`Revert`
  verify each slot before writing — out-of-order replay throws instead of corrupting —
  and do → revert restores the storage **bit-identically**. Timestamps are
  cache-invalidation counters, not state, and are excluded from the round-trip.
- **`MeshPrimitives`** — box, UV sphere, cylinder, cone frustum (`Cone(r1, r2, h)`;
  zero radius = apex fan; true n-gon caps). Outward CCW winding.
- **`ConvexHull`** — 3D quickhull over point sets → closed triangle mesh. One
  extent-scaled epsilon for all visibility tests; points within it of a face plane are
  absorbed (coplanar regions come out as consistent triangulations of true hull
  vertices, not sliver fans); degenerate inputs (&lt; 4 points, coincident, collinear,
  coplanar) throw with the reason. Output goes through the manifold-validating `Build`
  as a safety net.
- **`MeshBoolean`** — union/difference/intersection, one algorithm: the **imprint
  boolean**. Cut both meshes along their exact
  intersection curve (`MeshMeshCut`),
  flood-fill each mesh's faces into **patches** bounded by that curve, classify each patch
  once by the other mesh's `MeshWindingNumber` at the centroid of its largest triangle,
  keep the halves the operation calls for (difference reverses the tool's kept patches),
  and weld the two halves by **exact coordinate equality** — the imprint guarantees
  bit-identical seam vertices, so there is no gap to bridge and no tolerance to pick. At
  every seam edge exactly one face survives on each side (adjacent faces lie on opposite
  sides of the other surface), which is why the result is closed and manifold.
  *Classification assumption*: after the imprint no face straddles the other surface, so a
  whole patch is inside or outside — probing per patch (rather than per face) is what
  keeps seam slivers, whose centroids sit arbitrarily close to the other surface, from
  deciding anything. All three operations take an optional `ProgressCancel`, threaded the
  whole way down: the imprint owns 85% of the reported span and polls per broad-phase pair
  and inside both meshes' cuts, and each mesh's classify-and-emit pass owns half of what is
  left. **Polling follows cost, not code structure** — coincidence classification and the
  patch flood fill are linear passes over the faces and get one checkpoint each at their
  boundary, while the per-patch winding-number probes (each a query over the whole *other*
  mesh) poll every iteration.

  *A BSP-tree clipper (csg.js) used to sit behind a `BooleanMethod` selector as the
  alternative, and is now deleted.* It was the first boolean this engine had, and it was
  the right thing to build before there was an intersection curve to imprint with; keeping
  it as an escape hatch stopped making sense once coincident surface was classified
  (below), because the comparison came out one-sided in every dimension. What it got wrong
  is still locked by tests here, since those are hostile inputs worth pinning: a bore whose
  flats stop 1e-9 short of the box's sides (BSP returned a shell with boundary edges — a
  hole in the solid), any model at ~1e-5 scale (BSP's absolute 1e-9 degeneracy test was
  applied to a cross product, i.e. an AREA, so every polygon was discarded and the result
  came back empty), and flush-mating parts (below). Its final measurement is the one that
  settled it: a 32k+32k sphere union took **74.9 s** and returned an **open** 347k-face
  shell, against **0.71 s** for a closed 50k-face mesh here. Two of its lessons outlived
  the code — every absolute epsilon it carried is the origin of this codebase's
  scale-free-guard rule, and *a recursion whose depth is a property of the input has no
  "big enough" stack* (its splitting plane was the first polygon's plane, so a convex body
  built an essentially degenerate chain of depth O(polygons) and two 32k-triangle spheres
  killed the process outright until every walk was rewritten with an explicit stack).

  *Performance*, measured in Release over eleven representative pairs (Debug is
  pessimistic: `EditableMesh` runs a full `Validate()` after every operator there). The
  imprint path is **1.6–2.2× faster than it was** after three fixes, all found by
  instrumenting phases rather than by inspection, and none where the reading eye would
  look:
  1. **`MeshImprinter` located interior seam points by scanning every fragment ever cut
     from the host face** — O(k²) in the points landing on ONE face, which is exactly the
     shape of a bore's rim landing on a single cap triangle. 96% of the imprint for a
     64-sided bore through a box. Replaced by a walking point location (step across
     whichever edge the target lies furthest outside of) with the old scan as the fallback,
     so it is an optimization and not a change of answer.
  2. **The boolean built two Barill multipole hierarchies to ask about eight points.**
     Classification asks ONE question per surface patch; building an O(n log n) hierarchy
     for that was 66–86% of the whole classification phase. `MeshWindingNumber.Direct`
     skips the hierarchy (exact O(n) sums), and the boolean picks by question count against
     a measured break-even of ~32 queries. Classification of two 32k-triangle spheres:
     65 ms → 6 ms.
  3. **`Triangulated()` rebuilt meshes that were already triangles**, and the exact boolean
     triangulates both operands on entry — so cascaded booleans revalidated everything at
     every stage. It now returns `this`, which is safe because `HalfEdgeMesh` is immutable.

  Left on the table (Core's, not the mesh engine's): the broad phase — two `Bvh.Build`s
  plus `QueryOverlap` — is now the single largest line on dense operands, ~22 ms of the
  58 ms sphere-sphere boolean.
- **`CoincidentSurface`** — the coplanar half of the exact boolean: what to do where the
  two solids **share** boundary instead of crossing it (stacked parts, a boss standing on
  a plate, a tool bottoming out exactly on the far face). The winding number is exactly ½
  on such a face and decides nothing, so those faces are classified by **normal
  agreement** instead — normals agreeing means both solids lie on the same side, normals
  opposing means they mate back to back — and the set algebra follows:

  | normals | union | intersection | difference (A − B) |
  |---|---|---|---|
  | agree | keep one copy | keep one copy | drop both |
  | oppose | drop both | drop both | keep A's copy |

  The surviving copy is always the FIRST mesh's: both meshes cover the region, so exactly
  one copy can ever remain, and A's needs no special case. Membership is decided at the
  face centroid against a BVH of the other mesh's coincident carriers, which is legal
  because the coincident region's **rim is imprinted by the ordinary transversal path** —
  a coplanar patch ends where the other solid's surface leaves the shared plane, i.e. at a
  transverse face, and that pair does produce a segment. Coincidence is also a patch
  boundary in the flood fill, so a ½ can never leak into an ordinary patch's probe.
  Two numerical lessons: this classifier reads only the **sign** of an area-weighted
  normal against a plane, because `Vector3d.TryNormalize(Tolerance.Default)` applies the
  ABSOLUTE 1e-9 to a cross product and so stops recognizing mating faces below ~1e-4 scale
  (measured: a non-manifold union at 1e-5 — the BSP defect, reintroduced); and the
  coplanar-overlap test's separating-axis margin is `epsilon × edge length`, since
  `Orient2d` returns twice an AREA and comparing it to a bare epsilon made triangles that
  merely touch along a shared edge — the commonest relation between neighbouring solids —
  report as overlapping.
- **`MeshMeshCut` / `MeshImprint`** — exact mesh–mesh intersection and imprint: the two
  meshes come back cut along their common curve, sharing it vertex-for-vertex. Broad
  phase is `Bvh.QueryOverlap` over per-triangle boxes; the narrow phase is the Möller
  interval test (corner distances to the other plane → each triangle's interval on the
  common line → the overlap of the two intervals). **Every intersection point is one mesh
  edge crossing one face plane of the other mesh**, evaluated from the edge's
  lower-indexed endpoint, so its value depends only on (edge, plane) — the two faces
  sharing that edge and the two meshes get bit-identical coordinates, and the seam welds
  by equality rather than by tolerance. All degeneracy tests are **relative to the
  operands' extent** (1e-13, the scale-free tier), never the absolute 1e-9 weld tier;
  that is what makes the cut work on 1e-5-scale models and on near-tangent pairs where
  the retired BSP path's absolute constants failed. The imprint itself is
  `EditableMesh`-only — `SplitEdge` for points on edges (updating both adjacent faces, so
  no T-junction can appear), `PokeFace` for points inside a face, and `FlipEdge`
  constraint recovery (Anglada) for the segments themselves — wrapped in one
  `MeshChangeSet`, so a refusal reverts the journal and leaves the mesh bit-identical
  instead of half-cut. Split vertices are written to the exact shared coordinate right
  after the split (the operator's lerp only picks a valid topological parameter). A
  crossing landing within the degeneracy guard of an existing vertex snaps to it on both
  sides, moving the other mesh's vertex if it has one there too — the only geometry the
  algorithm ever perturbs. Coplanar overlapping faces contribute no curve (two coplanar
  triangles meet in an area, not a segment) and are reported separately as
  `CoincidentFacesA`/`CoincidentFacesB` — whole carrier triangles, a complete superset of
  the shared surface that consumers clip by containment (see `CoincidentSurface`).
  Coplanar faces that only touch along an edge are not coincident at all.
  `MeshImprint` reports the shared `Points`/`Segments`, the chained `Polylines` (a closed
  loop repeats its first index), and `Length`.
- **`LoopSubdivision`** — triangle-mesh Loop subdivision with boundary rules.
- **`MeshDecimator`** — quadric error metric (Garland–Heckbert) edge collapse with link
  condition and normal-flip guards; boundaries are preserved exactly. Candidates live in
  Core's `IndexPriorityQueue` (one always-current entry per undirected edge, re-keyed in
  place on neighborhood changes — replaced the lazy stamped-duplicates queue at equal
  speed and equal-or-better quality). The topology layer is **`EditableMesh.CollapseEdge`**,
  which is what that operator exists for; it replaced a private indexed-face-set scratch
  state (a `HashSet` of faces per vertex plus its own link check) after a measured
  comparison recorded in `MeshDecimatorQualityTests`: **bit-identical output** on twelve
  fixture/budget pairs, **0.84×** the time (Release, best of 9 after a 1.5 s warm-up budget),
  and — the substantive win — correct at 1e-5 scale, where the old path lost **91%** of the
  volume because it normalized face normals against the absolute 1e-9 weld tolerance. That
  is an absolute epsilon on a cross product, i.e. an *area*, so below ~1e-4 scale every face
  read as degenerate and contributed no quadric at all; the guards are now `1e-13 × extent`,
  the scale-free tier. Optional `ProgressCancel` parameter reports progress and cancels
  cooperatively (`OperationCanceledException`). Two gotchas preserved in comments: never key
  edge maps by a packed `(min &lt;&lt; 32) | max` long — the default long hash is `lo ^ hi`,
  which collapses structured mesh-edge keys into a handful of hash buckets (measured 4×
  whole-algorithm slowdown), tuple keys hash properly; and **seed the priority queue in a
  second pass**, after every face has contributed its quadric — folding the seeding into the
  accumulation loop keys the whole initial queue off partial quadrics, which still produces a
  closed, manifold mesh at exactly the requested face count and is only visible as a 2.4×
  worse approximation error at light decimation.
- **`MeshPlaneCut`** — slices a mesh by a plane, keeping the side the normal points away
  from (material below the plane, as when slicing for printing). Builds a new mesh:
  kept faces copied, crossing faces Sutherland–Hodgman-clipped with exact line-plane
  crossing points shared per undirected edge (bitwise-identical on both sides, so
  `Build` welds without tolerance). Returns the on-plane cut loops (ordered, wound CCW
  from the normal side); `cap: true` ear-clips each loop closed, with a zip pass
  re-inserting exactly-collinear loop vertices earcut filters. Nothing-removed cuts
  return the input mesh; remove-everything cuts throw; nested (annular) loops can't be
  capped per-loop and throw `NotSupportedException` — use `cap: false` and fill yourself.
  Faces crossing the plane 3+ times (non-convex n-gons) are triangulated in their own
  plane via `PolygonTriangulator` before clipping — each piece is convex, so
  Sutherland–Hodgman never bridges separate kept regions (a vertex-0 fan only works for
  star-shaped polygons).
- **`MeshWindingNumber`** — generalized winding number (Jacobson et al. 2013) for robust
  inside/outside classification, including on **non-watertight** meshes (holes,
  self-intersections, duplicated patches) where normal/ray-parity tests fail.
  `WindingNumber` is the exact per-triangle signed-solid-angle sum (Van Oosterom–Strackee);
  `FastWindingNumber` is the Barill et al. 2018 fast approximation — triangles clustered
  in a median-split hierarchy, distant clusters evaluated by a second-order (dipole +
  quadrupole) multipole expansion of their winding field (β radius test, default 2),
  giving O(log n) queries with error far below the ½ decision threshold. `IsInside`
  thresholds at ½. Construction triangulates and indexes once; the mesh may be open.
  `FromTriangulated` skips the triangulating copy when the caller already paid for it;
  **`Direct` additionally skips the hierarchy**, making every query the exact O(n) sum.
  That is the right trade below ~32 queries — the hierarchy costs ~0.7 µs per triangle to
  build and an exact query ~20 ns per triangle — and it is a cost decision only:
  `FastWindingNumber` and `IsInside` fall through to the exact sum, so they stay correct.
- **`MeshMassProperties` / `MassProperties` / `MassPropertyIntegrator`** — volume, surface
  area, centre of mass and the inertia tensor of the solid a closed mesh bounds (OCCT's
  `GProp_GProps`), with a `density` parameter so mass and moments come out in real units.
  **Exact for any polyhedron**: each facet contributes the closed-form moments of the
  tetrahedron it spans with a reference point (∫λ_aλ_b dV = V·(1+δ_ab)/20 in barycentric
  coordinates), and those sum with no discretization error whatsoever — a box, a prism or a
  tetrahedron agrees with the closed form to round-off. Faces may be n-gons: area comes from
  Newell (right for concave faces, which a sum of fan-triangle areas is not) while the
  moments come from the fan (which telescopes exactly whatever the polygon's shape), so the
  results agree bit-for-bit with `SurfaceArea()` and `SignedVolume()`.
  - `MassProperties` carries the <b>volume-weighted</b> second moment about the centroid,
    S = ∫(r−c)(r−c)ᵀ dV, and derives the rest: `Inertia` = ρ·(tr(S)·Id − S), `InertiaAbout`
    (parallel axis), `Principal()` (ascending moments + a right-handed `Frame3d` at the
    centre of mass, by cyclic Jacobi), `WithDensity`, `Transformed` (similarity transforms
    only — shear and non-uniform scale change the surface area in a way the properties alone
    cannot express, so they are refused rather than fudged) and `Combine` (assemblies,
    including mixed densities, whose result reports the bulk density).
  - **Numerical lesson, worth the one subtraction it costs:** the divergence-theorem sum is
    over terms of size |r|³ that cancel down to the volume, so a body far from the origin
    loses digits catastrophically. Measured on a 10 mm cube posed at (1e6, 2e6, 3e6): the
    origin-referenced sum is **6.5e-7** relative, the bounding-box-centre-referenced sum
    **5.2e-12** — five orders of magnitude, and it worsens with (distance / size). Never
    integrate moments about the world origin. (A test pins this, and it had to be built on a
    *rotated* box: an axis-aligned one at a round offset has integer coordinates whose
    products cancel exactly and hide the effect entirely.)
- **`PolygonTriangulator`** — 2D triangulation with holes; a faithful port of mapbox
  earcut (minus z-order hashing).
- **`HoleFiller`** — hole filling for open meshes, construct-new (g3 `SimpleHoleFiller` /
  `PlanarHoleFiller` / `AutoHoleFill` dispatch). Boundary half-edges are wound opposite
  their interior twins, so fill faces that follow the boundary walk order supply exactly
  the free directed edges and `Build` welds them manifold. `FillSimple(mesh, loop)` —
  centroid-vertex triangle fan (single triangle for 3-loops); refuses large wildly
  non-planar loops (plane deviation above a fixed fraction of the loop extent — a
  scale-free shape guard, not a tolerance) where a fan would self-intersect.
  `FillPlanar(mesh, loop[s])` — best-fit plane (`Fitting3d.FitPlane` → `Frame3d`),
  project, ear-clip via `PolygonTriangulator`, map back; multiple loops sharing one plane
  become polygon-with-holes fills (after projection, CCW loops are outers and CW loops are
  holes — walk orientation encodes nesting intrinsically; each hole goes to its smallest
  containing outer), which handles the annular case `MeshPlaneCut` refuses to cap.
  Earcut-dropped exactly-collinear vertices are re-expanded by a ring-aware chord zip
  (hole bridges are never chords). `FillAll(mesh, options)` dispatches per hole and
  reports a `HoleFillOutcome` per boundary loop: planar where the loop fits a plane
  within `PlanarityTolerance` (absolute, default weld 1e-9 — exact cut/tessellation rims
  qualify, curved rims miss by their sagitta), grouped by common plane; else simple under
  `MaxSimpleFillVertices`; else the `Fallback` tier below, or `Skipped` with the reason.
  - **`FillMinimal(mesh, loop)`** — the **minimum-weight triangulation of the rim's own
    vertices** (Barequet–Sharir / Liepa dynamic program), g3's `MinimalHoleFill` tier. No new
    vertices, so the patch interpolates the rim exactly and cannot bulge; the weight is the
    pair (largest dihedral angle, total area) compared lexicographically, which is why
    removing two adjacent faces of a box and refilling restores the box's **exact** volume —
    the fill puts the corner back rather than cutting a flat chord across it. This is the
    tier for holes in faceted geometry. *Deliberate deviation from g3*, which seeds a fan and
    runs four iterative edge-flip passes its own comments call unstable ("strong ordering
    effects", "will frequently not converge", a hard pass cap to stop the oscillation, a
    forced interior-vertex removal stage with a debugger break in it): the dynamic program is
    the standard algorithm here — deterministic, globally optimal for the stated weight,
    O(n³) time and O(n²) memory in the rim length, and it cannot oscillate. Chords that
    already join two rim vertices elsewhere in the mesh are forbidden (using one would give
    that edge a third face), which can leave a rim with no admissible triangulation; that
    throws, and `FillAll` reports it. The dihedral measure is `atan2(|a×b|, a·b)` on the raw
    (unnormalized) normals — exact at any magnitude, no normalization and no epsilon.
  - **`FillSmoothed(mesh, loop, options)`** — a **relaxed membrane**: a coarse fan is remeshed
    to the hole's own mean rim-edge length with its rim pinned, and Laplacian smoothing pulls
    the interior into a smooth surface spanning it (g3's `SmoothedHoleFill`). The tier for
    holes in curved geometry, where a flat minimal patch reads as a dent. The patch is built,
    remeshed and relaxed **as a standalone mesh** and stitched back, so the surrounding
    surface is untouched (g3's `ConstrainToHoleInterior = true` mode; its default instead
    grows two rings into the original mesh, trading fidelity for blending). The stitch is
    exact, not tolerant: rim vertices are pinned **and** rim edges are barred from splitting
    (`RemeshOptions.SplitFixedEdges`), so the patch comes back with the rim it went in with,
    vertex for vertex, and the halves weld by index — an extra rim vertex would be a
    T-junction. `TargetEdgeLength` defaults to the hole's own mean rim edge, so the fill
    matches the surrounding tessellation at any model scale (g3's equivalent defaults to an
    absolute 2.5 world units, which is silently wrong for anything not in millimetres).
    Iterated Laplacian smoothing with a fixed boundary converges to the same membrane a
    linear solve would give, so no sparse solver is carried.
  - `HoleFillOptions.Fallback` selects which tier `FillAll` uses for the loops the planar and
    simple fills decline, and defaults to **`Minimal`**. It shipped as `None` on the
    reasoning that reporting a hole honestly beats inventing questionable geometry — which
    is right, and is exactly why `Minimal` is the better default: **it invents nothing**.
    Every vertex of a minimum-weight patch is already a vertex of the hole, so it
    interpolates the rim exactly, cannot bulge, and restores creases rather than chording
    across them. The honest report survives wherever the tier genuinely cannot decide (no
    admissible triangulation, or a rim past `MaxMinimalFillVertices`), so `FillAll`'s
    report-what-you-cannot-fill contract is unchanged in the cases it was written for; the
    difference is that `MeshRepair.AutoRepair` now closes a long non-planar hole instead of
    naming it. `Fallback = None` restores the old behaviour exactly, and `Smoothed` remains
    the opt-in that does add vertices. `MaxMinimalFillVertices` (default 256) caps the cubic
    dynamic program.
- **`MeshExtrude`** — construct-new extrusion ops (g3 `MeshExtrudeFaces` /
  `MeshExtrudeMesh`). `Faces(mesh, faceIndices, offsetVector | distance)` pulls a face
  patch off the mesh: patch vertices shared with the rest (or on the open mesh boundary)
  are duplicated at the offset position, interior patch vertices move in place, and each
  patch-boundary half-edge a→b gains the wall quad [a, b, b′, a′] — exactly the two
  directed edges freed by moving the patch (a `MeshFaceSelection` overload takes the
  selection vocabulary directly), so winding is correct by construction and
  closed meshes stay closed (multiple disjoint regions each get their own walls; input
  face indices survive, walls appended). The distance form offsets along area-weighted
  patch-only vertex normals. `Thicken(mesh, thickness)` turns a surface into a solid
  shell — the direct-mesh complement of SDF `Shell`: the input stays as the front skin,
  a reversed copy offsets <i>against</i> the vertex normals (material behind the
  surface), and each boundary loop is stitched with a quad band; open surfaces become
  closed slabs/shells, closed meshes become hollow two-shell solids.
- **`Remesher`** — isotropic remeshing to a uniform target edge length (g3's
  `Remesher`/`RemesherPro`, Botsch &amp; Kobbelt's split/collapse/flip/smooth loop) built on
  `EditableMesh`'s guarded Euler operators, which is exactly what those operators exist for.
  One pass is: a single sweep over the edges trying **collapse → flip → split** per edge
  (at most one succeeds, first wins), then a full double-buffered smoothing pass
  (`RemeshSmoothing.Uniform` or `Cotangent`), then a projection pass. Returns a
  `RemeshResult` with the mesh and the operation counts.
  - The sweep visits edges on a **modulo-prime stride**, not in index order: with sequential
    ids on a tessellated cylinder, every tiny edge collapses into its neighbour in turn and
    the whole mesh erodes away; jumping around breaks that symmetry. The stride is a fixed
    constant and the algorithm uses **no random number generator anywhere** — two runs give
    bit-identical positions. Its coprimality with the half-edge capacity is checked (g3
    leaves that hole open: a capacity that is a multiple of its prime would visit a sub-cycle
    and miss most of the mesh).
  - **Constraints are expressed on vertices, not edges**, which is a deliberate departure
    from g3 forced by our topology: an undirected edge is named by the smaller of a twin
    pair, and a collapse *merges* edge pairs — the surviving edge generally gets a different
    canonical index, and freed indices are recycled — so an edge-keyed constraint table goes
    stale (worse: silently aliases a different edge) after the first collapse. Vertex indices
    never do, because a collapse always removes the *unpinned* end. Everything g3 spells with
    edge flags follows: an edge with two pinned ends can be neither collapsed (both ends
    fixed) nor flipped (a flip destroys the edge), which is `NoCollapse | NoFlip`, while
    splitting stays legal and the midpoint inherits the pin — a constrained chain keeps its
    geometry and gains resolution. `SplitFixedEdges = false` adds `NoSplit` for callers who
    need the chain back vertex for vertex (the smoothed hole fill's stitch).
  - `PreserveBoundary` (default on) and `FeatureAngleDegrees` (default 30°) are **re-derived
    from the current geometry at the start of every pass**, so they need no bookkeeping at
    all: boundary-ness is intrinsic, and a crease's dihedral is unchanged by splitting it.
    Explicit `FixedVertices` are honoured too. Documented limitation, pinned by a test:
    feature detection reads the dihedral of the mesh it is given, and a *coarse tessellation
    of a smooth surface* has large dihedrals — `UvSphere(12, 8)` facets meet at ~30°, so the
    default pins much of it. Pass 0 (or a larger angle) when remeshing tessellated curvature.
  - Split/collapse thresholds are **1.33 L / 0.66 L**, not Botsch's 4/3 and 4/5: with the
    classic factors an edge just over 4/3·L splits into halves of ≈0.667·L, *below* the
    0.8·L collapse threshold, so every split immediately produces two collapse candidates.
  - Every threshold is relative to the target edge length and every degeneracy guard to its
    square (areas scale quadratically — the BSP lesson), so remeshing behaves identically at
    1e-5 scale. Guards read the **sign** of the dot of two unnormalized cross products, never
    `TryNormalize` against an absolute tolerance.
  - Measured convergence (Release, `UvSphere(1, 12, 8)` → target 0.25): 90% of the input's
    edges are outside the [0.66 L, 1.33 L] band; after 40 passes **0%** are, at 73 ms. Note
    that in DEBUG builds `EditableMesh` runs a full `Validate()` after every operator, so
    remeshing is O(n) per operation there and DEBUG timings are meaningless.
- **`IProjectionTarget` / `MeshProjectionTarget`** — the surface a remesh pulls vertices back
  onto. Smoothing shrinks a model (Laplacian flow is curvature flow — a sphere loses radius
  every pass), and projection is what undoes it, so the remesh changes the tessellation and
  leaves the shape. `MeshProjectionTarget` is a BVH over a **snapshot** of the target's
  triangles (the mesh being remeshed is mutating underneath) plus the exact closest point on
  the winning triangle (Core's `Distance3d.ClosestPointOnTriangle` — Ericson's
  Voronoi-region form: six barycentric sign tests, no tolerance anywhere); queries are
  allocation-free through `Bvh.Nearest<TMetric>`. The
  interface lives here so `EngrCAD.Mesh` needs no dependency on the implicit engine — the
  SDF-backed target (`p − d(p)·∇d(p)`, iterated) is `SdfProjectionTarget` in
  **EngrCAD.Interop**, which is where a type may see both engines. Its guarantee is
  one-sided rather than convergent: a 1-Lipschitz lower bound puts the surface at least
  `|d|` away in every direction, so the step can never cross it, but `|d|` need not
  decrease near a CSG difference's fictitious faces. See that project's README.
- **`MeshWelder`** — polygon-soup → mesh via spatial-hash vertex welding, with optional
  T-junction seam zipping: for every directed edge with no reverse partner, the crack
  vertices lying collinearly along it are inserted, so both sides of a seam end up with
  the identical subdivision and the surface closes. The zip's two thresholds are the
  epsilon ladder's **1e-7 seam tier expressed relatively** — a fraction of the soup's own
  extent, not an absolute constant. The scaling preserves the tier's meaning rather than
  breaking it: a seam vertex misses its coarse edge by the two sides' *independent*
  construction error, and every source of that error is proportional to the model (a
  chord's sagitta scales with the radius; a float32-quantized STL's cracks scale with the
  coordinate). At unit extent it is exactly the 1e-7 it replaced, and it is deliberately
  two decades looser than the 1e-9 weld tier, which stays absolute because geometry that
  must weld is constructed exactly rather than independently. The absolute form was wrong
  in both directions away from unit scale — a metre-scale model's genuine seam vertices
  sat further off their coarse edge than 1e-7 and the crack stayed open, while a
  1e-5-scale model's *distinct* vertices sat closer than it and were zipped onto edges
  they had no business on.
- **Selections** (`MeshFaceSelection` / `MeshVertexSelection` / `MeshEdgeSelection`) —
  read-only v1 of the selection/region model (g3 `MeshFaceSelection` et al.): immutable
  index sets over one mesh with `Grow`/`Contract` (one-ring steps; face grow is
  vertex-adjacency, face contract removes faces touching a border vertex — one that also
  belongs to an unselected face, mirroring g3's `ContractBorderByOneRingNeighbours`),
  conversions between the three kinds (`ToVertices`/`ToEdges`/`ToFaces(requireAll)`),
  boundary extraction (`BoundaryHalfEdges` plus `BoundaryLoops` chained by rotating
  around each destination vertex through selected faces — correct even at pinch
  vertices), and patch extraction (`ToMesh()`: remapped construct-new submesh; a
  selection touching itself only at a vertex fails `Build`'s bow-tie check with a
  "pinch" message; the `ToMesh(out vertexMap)` overload also reports where each extracted
  vertex came from). Edges are stored canonically as the lower half-edge index of each
  twin pair, matching `mesh.Edges`.
- **`MeshRegionOperator`** — extract-modify-reinsert (g3 `RegionOperator`): pull a face
  selection out as a mesh in its own right, edit it with anything that takes a mesh, put
  it back. `Extract(mesh, selection)` → `.Region` (+ `RegionToBaseVertex`, `SeamEdges`);
  `Reinsert(replacement)` returns a NEW session over the NEW mesh whose selection is the
  reinserted faces, so edits chain (g3's `CurrentBaseTriangles`, without the mutation).
  Two things worth knowing:
  - **Transactionality is free**, which is why this is NOT built on `MeshChangeSet` the
    way g3 builds it on an in-place editor: `HalfEdgeMesh` is immutable after `Build`, so
    a refused or failed reinsertion leaves the caller holding the original — there is no
    half-applied state for a journal to undo.
  - **The seam is the contract, and it may not be refined.** A replacement must reproduce
    the region's boundary as the same *directed* edges at bit-identical positions
    (direction proves the orientation matches; exactness because this engine welds shared
    geometry by equality, so a rim that drifted 1e-12 would weld into a crack instead of
    failing). Splitting a seam edge is refused too — the base face on the other side still
    holds the un-split edge, so the result would be a T-junction; refining across a seam
    means refining the neighbours, which is a different operation. Edits that satisfy the
    contract: anything confined to the interior, and `MeshDecimator` (its exact boundary
    preservation IS this contract). `LoopSubdivision` is not one — it splits *and* smooths
    the open boundary — and is refused with a message naming the offending edge.
- **`MeshConnectedComponents`** — edge-connected face components (g3
  `MeshConnectedComponents`): deterministic ascending-seed flood fill returning
  `MeshComponent`s (face selection + area + divergence signed volume + closed flag) with
  per-component extraction (`ToMesh`, always manifold — the source mesh forbids
  bow-ties, so components never share vertices) and `Separate(mesh)` splitting a
  multi-body mesh into its bodies.
- **`RenderMesh`** — flat (per-face) or smooth (per-vertex) triangle extraction for GPUs.
- **`ObjWriter`** — minimal Wavefront OBJ export for debugging.
- **Mesh import** — `StlReader` (binary + ASCII, autodetected: the exact
  84 + 50·n byte-size test runs *before* any `solid` prefix sniffing, because binary
  exporters routinely write "solid" into the 80-byte header; the prefix + a
  printable-text check only decide when the size doesn't match), `ObjReader`
  (v/f with all index forms incl. negative relative; vt/vn/materials/groups ignored and
  reported; polygonal faces triangulated in their Newell plane via
  `PolygonTriangulator` with a fan fallback when earcut filters collinear vertices —
  a dropped vertex a neighbor still references would open an unweldable T-junction),
  `OffReader` (OFF + variants; extra color/normal columns ignored with warnings), and
  the `MeshReader` extension-dispatch facade. All readers weld coincident vertices
  (spatial hash; representatives keep their exact file coordinates — welding never
  moves geometry; tolerance parameter defaults to the 1e-9 weld tier) and attempt the
  manifold `Build`. **Dirty files don't throw**: every reader returns a
  `MeshReadResult` carrying the welded indexed soup + `MeshReadDiagnostics`
  (non-manifold / inconsistently-wound / boundary edge counts, duplicate and
  degenerate faces, parser warnings, the `Build` failure message) with `Mesh` null,
  so the repair pipeline can take over; `RequireMesh()` throws with the diagnostics
  summary. Note binary STL is float32-quantized: coincident vertices are
  bit-identical (default weld is exact), and cracks below one float ulp cannot exist,
  so repair-time crack welding on STL needs tolerances at or above ~1e-7·|coordinate|.
- **`MeshRepair`** — repair pipeline v1 for dirty soups (construct-new; inputs never
  mutated): crack welding at `MeshRepairOptions.WeldTolerance` (default = the 1e-7
  seam tier — cracks are independently authored geometry on two sides of a seam) →
  degenerate-face removal (triangle minimum altitude / polygon Newell-area-vs-perimeter
  below the weld distance = numerically weldable noise) → duplicate-face removal
  (canonical vertex cycle, either winding; must precede orientation — duplicates
  overload edges past two uses and would block the flood) → orientation
  (per-component BFS flood crossing only clean two-use edges, then an outward vote:
  **signed volume for closed components** — the winding integral in closed form —
  and **generalized winding-number probes for open ones**, probing both sides of the
  largest faces and deciding by the sign wherever |w| ≥ 0.25; a single
  behind-the-normal probe is provably blind to inward-wound sheets, since the
  winding is ~0 on the exterior side under either orientation) → T-junction seam
  zip (`MeshWelder`'s pass) → manifold `Build`. `Clean(...)` overloads take a
  `MeshReadResult`, an existing `HalfEdgeMesh` (polygons preserved — also useful to
  right an inside-out but manifold import), or a raw soup, and return the repaired
  mesh + a `MeshRepairReport` (vertices merged, duplicates/degenerates removed,
  components, faces rewound, components flipped, T-junction insertions, closedness,
  notes). Known v1 limitation: nested cavity shells are oriented outward
  like everything else. `MeshReader.ReadAndRepair(path, options?, fillHolesAndCracks?)`
  is the one-call import path (read welds exactly at 1e-9; repair applies the crack weld).
  - **`MergeCoincidentEdges(EditableMesh, tolerance)`** — crack closing by welding
    boundary edge PAIRS with the editor's `MergeEdges`, the topological complement of
    vertex welding. Two boundary half-edges pair up when they run in **opposite**
    directions with matching endpoints (a same-direction pair would fold the surface, and
    is skipped); candidates come from a spatial hash on edge midpoints. Because every
    merge runs the operator's manifold guards, the tolerance can be loosened far past a
    safe vertex-weld distance — an unsafe merge is *refused*, leaving that crack open,
    never corrupting the mesh. Vertex positions never move.
  - **`AutoRepair(...)`** — the full dispatch: `Clean`'s soup passes, then (only if the
    result is still open) `MergeCoincidentEdges` for leftover cracks and
    `HoleFiller.FillAll` for what remains, which by then is a genuine hole rather than a
    crack. Reports `CracksMerged` / `HolesFilled` / `HolesSkipped` and notes each refused
    hole. Since the fill's default fallback tier is `Minimal`, this closes long non-planar
    rims a centroid fan would self-intersect on, adding no vertices; pass
    `HoleFill = new HoleFillOptions { Fallback = HoleFillFallback.None }` to have them
    reported instead. An already-closed `Clean` result returns immediately, so the common case costs
    nothing. Defects beyond even this (fins, self-intersections) still fail `Clean`'s
    final `Build`, loudly, with post-repair edge diagnostics.
