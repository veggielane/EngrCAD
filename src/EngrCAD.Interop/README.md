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
- Ambiguous saddle cells resolve by the cell-center average; the plane is fully
  general (pass the section plane mapped through an inverse instance transform —
  affine maps take the sample rectangle to a parallelogram, which the
  parameterization represents exactly).
- Sample/value scratch comes from `ArrayPool`; levels that never cross return empty
  segment lists.
