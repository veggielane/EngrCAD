# EngrCAD.Interop

Conversions between the three geometry representations. References `EngrCAD.Mesh`,
`EngrCAD.Implicit`, and `EngrCAD.BRep` — the only kernel project allowed to depend on all
engines.

## The conversion triangle

- **Implicit → Mesh**: `SurfaceNets.Polygonize(sdf, region?, resolution, progress?)` —
  manifold Surface Nets (dual contouring): one vertex per *connected component of inside
  corners* per cell (plain one-vertex-per-cell produces non-manifold edges on thin
  sheets and saddles), one quad per interior sign-changing grid edge, wound outward.
  Surfaces crossing the sampling region come out open there. Grid sampling runs in
  parallel over i-slabs via `ParallelFor.Blocks` (each block fills and evaluates a
  disjoint slice, so the mesh is bit-for-bit identical to a sequential run); the
  topology passes stay sequential so output ordering never depends on scheduling. The
  optional `ProgressCancel` reports coarse progress and cancels cooperatively
  (throws `OperationCanceledException`, partial results discarded).
- **B-Rep → Mesh**: `BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples)` —
  each edge is sampled once into a shared polyline; planar faces (any number of loops)
  ear-clip via `PolygonTriangulator`; cylinder bands and full-domain generated faces
  (extruded/revolved/swept) tessellate as parameter grids whose samples match the shared
  edge polylines exactly; everything is welded (with seam zipping to repair T-junctions
  from earcut's collinear filtering).
  - **Trimmed faces** (loops not covering the surface's grid domain — `FaceSplitter`
    fragments such as a bore wall cut through by a slot, and every mitered rim-fillet
    band) go through `TrimmedFaceTessellator`, which picks a path in this order:
    1. **Strip zip** — a single-loop region whose boundary is a *band*: two chains
       monotone in one surface parameter, joined at each end by a single **rung**. The
       chains are already paired by construction, so the correct triangulation is the
       same monotone merge walk the periodic-band path uses, minus the period closure.
       The chain direction is the parameter carrying the natural sampling, so the rungs
       lie across the ruled or coarser one (getting that backwards would fan a 2-sample
       rung against a 25-sample chain). Guarded by a uv positive-area test on every
       emitted triangle: a merge zip triangulates a monotone region only while neither
       chain overhangs the other, and an overhang shows up as a fold.
    2. **Band with holes** — two-ring bands carrying extra interior hole loops (a
       cross-drilled bore wall) are cut open along a seam placed in the largest u-gap
       left free by the holes, unrolled into a rectangle-with-holes, and ear-clipped;
       the two seam chords are exact one-period translates with identical 3D endpoints,
       so they weld to each other.
    3. **Periodic band** — loops winding the period (rings subdivided into arcs) zip
       chain-to-chain or fan to a pole.
    4. **Ear clip** — everything else, by an exact-coordinate clipper (shortest-diagonal
       ears, on-edge points block, holes bridged).

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
    inverted**, rendering as a dark folded lens at every mitered corner — now **280 with
    none**. The count also stopped being quadratic (refinement had been subdividing the
    long diagonals the clipper left behind), and the mesh volume moved from −1.5e-4 to
    −4.8e-5 of the analytic prism, because the strip's facets no longer sag where the
    monotone-decrease rule cut a refinement cascade.

    Other numerical lessons baked in: earcut's exact-collinear filtering would drop
    iso-parameter run vertices (uv-collinear is *not* 3D-collinear — an unzippable
    crack), jittering breeds zero-area folds that refine into non-manifold welds, and
    ~1e-9 inverse-evaluation jitter demands an epsilon blocking band plus midpoint→vertex
    snapping during refinement (the same band makes bridge visibility treat
    nearly-collinear contact as touching — exact-zero cross products miss it by an ulp).
    The strip's own epsilon — how flat a step must be to count as a rung — is the 1e-6
    inverse-evaluation tier expressed **relatively**, `1e-6 × the loop's extent in that
    parameter`: u and v carry no model units, so an absolute epsilon there would be
    meaningless. Marching-tracer polyline edges are sampled at their exact vertices
    (`PolylineCurve3d.VertexParameters` — chordal midpoints sit off the surface and would
    fail inverse evaluation).

    **A trimmed face that cannot be tessellated now refuses**, naming the surface type,
    where it sits, its loop shapes, the sample counts in force and the reason (failed
    pullback, unsupported winding, refinement that would not converge). It used to fall
    back to the surface's natural grid, which covers the whole parameter rectangle rather
    than the trimmed face — not merely coarse but the *wrong* geometry, welding into an
    open mesh with no complaint. The sample counts belong in the message because some
    failures only appear at high density: before the strip path, refinement gave up at
    about `curveSamples = 192` on a filleted box and the silent fallback handed back an
    open mesh.

    Remaining gaps: pole-bounded single-chain bands with holes and |winding| > 1 loops
    are refused (they used to fall back to the grid), a rung sampled at more than two
    points falls to the ear clipper rather than being fanned, and a hole straddling every
    possible seam (covering a full period in u) is unsupported.
- **B-Rep booleans**: `BrepBoolean.Union/Intersection/Difference` — the full pipeline
  (face-pair intersection, seam-aligned splitting, SDF-probe classification, reversed
  subtracted faces, topological seam sealing via `TopologyEditor.SealSeams`). See
  design.md §5. v1 handles transversal cases; inputs are consumed; output passes
  `Validate()` with correct genus and exact volumes.
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
  branch-and-bound nearest-triangle search over a BVH (Ericson closest-point-on-triangle);
  sign from the angle-weighted pseudonormal of the closest feature (Bærentzen–Aanæs),
  exact for watertight meshes even at edges and vertices. `Evaluate` is allocation-free
  in steady state — the nearest search goes through `Bvh.Nearest<TMetric>` with a struct
  distance metric, not a closure (0 B measured over 100 k calls; locked by
  `MeshSdfTests.Evaluate_SteadyState_DoesNotAllocate`). The result is a first-class
  `Sdf` node composable with the whole implicit engine.
  The sign source is opt-in via `new MeshSdf(mesh, MeshSignSource.WindingNumber)`, which
  drives the fast generalized winding number (`MeshWindingNumber` in EngrCAD.Mesh) instead
  of the pseudonormal — same partition on watertight meshes, but also accepts **open**
  (non-watertight) meshes, where the distance is still to the existing surface and the sign
  degrades gracefully near holes. The default (`MeshSignSource.Pseudonormal`) is unchanged
  and still requires a closed mesh.

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

**Known limitation**: in near-tangent views (a torus seen side-on, where every quad is
almost edge-on) the 2D boolean can still leave a **pinhole** of ~1e-7 of the outline area.
Areas are correct to 6 significant figures and order-independent; only the hole COUNT is
unreliable there, so filter holes by area if that matters. The residual cause is cell
classification in `Region2dBoolean` at near-tangency, not the silhouette.

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
