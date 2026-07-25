# EngrCAD.Interop

Conversions between the three geometry representations. References `EngrCAD.Mesh`,
`EngrCAD.Implicit`, and `EngrCAD.BRep` — the only kernel project allowed to depend on all
engines.

## The conversion triangle

- **Implicit → Mesh**: `SurfaceNets.Polygonize(sdf, region?, resolution, progress?)` —
  manifold Surface Nets (dual contouring): one vertex per *connected component of inside
  corners* per cell (plain one-vertex-per-cell produces non-manifold edges on thin
  sheets and saddles), one quad per interior sign-changing grid edge, wound outward.
  Surfaces crossing the sampling region come out open there. The
  optional `ProgressCancel` reports coarse progress and cancels cooperatively
  (throws `OperationCanceledException`, partial results discarded).
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
- **B-Rep → Mesh**: `BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples)` —
  each edge is sampled once into a shared polyline; planar faces (any number of loops)
  ear-clip via `PolygonTriangulator`; cylinder bands and full-domain generated faces
  (extruded/revolved/swept) tessellate as parameter grids whose samples match the shared
  edge polylines exactly; everything is welded (with seam zipping to repair T-junctions
  from earcut's collinear filtering).
  - **Trimmed faces** (loops not covering the surface's grid domain — `FaceSplitter`
    fragments such as a bore wall cut through by a slot) go through
    `TrimmedFaceTessellator`: loops pulled into (u, v), non-wrapping regions ear-clipped
    by an exact-coordinate clipper (shortest-diagonal ears, on-edge points block, holes
    bridged), band-like regions (loops winding the period — rings subdivided into arcs)
    strip-zipped chain-to-chain or fanned to a pole, then oversized interior edges
    midpoint-split to the natural grid density with new vertices on the exact surface.
    Boundary vertices are always the exact shared edge samples, so seams weld at 1e-9.
    Routing between grid and trimmed paths is a two-sided 3D match of loop samples
    against the natural grid boundary — precisely the invariant grid welding needs.
    Numerical lessons baked in: earcut's exact-collinear filtering would drop
    iso-parameter run vertices (uv-collinear is *not* 3D-collinear — an unzippable
    crack), jittering breeds zero-area folds that refine into non-manifold welds, and
    ~1e-9 inverse-evaluation jitter demands an epsilon blocking band plus midpoint→vertex
    snapping during refinement (the same band makes bridge visibility treat
    nearly-collinear contact as touching — exact-zero cross products miss it by an ulp).
    Two-ring bands with extra interior hole loops (a cross-drilled bore wall) are cut
    open along a seam placed in the largest u-gap left free by the holes, unrolled into
    a rectangle-with-holes, and ear-clipped; the two seam chords are exact one-period
    translates with identical 3D endpoints, so they weld to each other. Marching-tracer
    polyline edges are sampled at their exact vertices (`PolylineCurve3d.VertexParameters`
    — chordal midpoints sit off the surface and would fail inverse evaluation).
    Remaining gaps: pole-bounded single-chain bands with holes and |winding| > 1 loops
    fall back to the grid path, and a hole straddling every possible seam (covering a
    full period in u) is unsupported.
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
  against a baseline you wrote differently.) The untried lever is a *packet* query — one
  traversal per coherent block, collecting candidates for all its points at once — which
  attacks the node-test cost rather than the initial bound.
  The sign source is opt-in via `new MeshSdf(mesh, MeshSignSource.WindingNumber)`, which
  drives the fast generalized winding number (`MeshWindingNumber` in EngrCAD.Mesh) instead
  of the pseudonormal — same partition on watertight meshes, but also accepts **open**
  (non-watertight) meshes, where the distance is still to the existing surface and the sign
  degrades gracefully near holes. The default (`MeshSignSource.Pseudonormal`) is unchanged
  and still requires a closed mesh.

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
