# EngrCAD.Modeling

The unified modeling API: build a **`Shape`** once with one vocabulary, then decide at
the end which representation it becomes:

```csharp
var body = Shape.Box(40, 30, 10) - Shape.Cylinder(4, 12).Translate(10, 8, 0);

BrepSolid exact   = body.ToBrep();       // precision modeling, STEP export
Sdf       field   = body.ToImplicit();   // blends, shells, lattices
HalfEdgeMesh mesh = body.ToMesh();       // rendering, FEA, 3D printing
scene.Add("body", body);                 // viewer picks the best route itself
```

`Shape` is an immutable operation graph (like the `Sdf` AST, but engine-neutral). Each
conversion *lowers* the graph: native operations where the target engine has them,
bridges through another representation where it doesn't, and a clear error where no
route exists. `shape.Explain(target)` reports the per-node plan without doing the work;
`CanConvertTo` is the boolean version; impossible conversions throw
`ShapeConversionException` carrying the same report.

Transforms are never applied to finished geometry when the target can do better: the
lowering accumulates the matrix and bakes it into construction inputs (profiles,
directions, axes), so a rotated-then-drilled B-Rep stays exact.

## Operation support by target

| Operation | → B-Rep | → Implicit (SDF) | → Mesh |
| --- | --- | --- | --- |
| `Box` | ✅ native (extrusion if sheared) | ✅ native · 🔶 bridged if sheared | ✅ native |
| `Sphere` | ✅ native (rigid + uniform scale) · ❌ sheared (ellipsoid) | ✅ native · 🔶 bridged if sheared | ✅ / 🔶 |
| `Cylinder` | ✅ native (any affine — circle becomes ellipse) | ✅ native · 🔶 bridged if sheared | ✅ native |
| `Torus` | ✅ native (rigid + uniform scale) · ❌ sheared | ✅ native · 🔶 bridged if sheared | ✅ / 🔶 |
| `Cone` (`r1`, `r2`; 0 = apex) | ✅ native (rigid + uniform scale) · ❌ sheared (elliptic cone) | ✅ native · 🔶 bridged if sheared | ✅ / 🔶 |
| `Wedge` (`topX`, `topOffsetX`) | ✅ native (any affine — it is an extrusion) | ✅ **native** (exact 2D SDF) · 🔶 bridged if sheared | ✅ native |
| `Extrude(Sketch)` | ✅ native | ✅ **native** (exact 2D SDF) | ✅ native |
| `Revolve(Sketch)` full turn | ✅ native (axis-touching OK: on-axis stretches become poles) | ✅ **native** (exact 2D SDF) | ✅ native |
| `Extrude` (profile, holes, shear) | ✅ native | 🔶 bridged (tessellation → mesh SDF) | ✅ native |
| `Extrude(Sketch, twist, scale)` (OpenSCAD `linear_extrude`) | taper only: ✅ native (ruled loft — straight sides sweep exact planes through the scaling centre; ❌ with holes) · twist: ❌ (no analytic twisted surface) | 🔶 bridged (section-sweep mesh → mesh SDF) | ✅ native (direct section sweep, `slices` rings) |
| `Revolve` (partial/full, holes) | ✅ native (rigid) · ❌ sheared | 🔶 bridged | ✅ / 🔶 |
| `Sweep` (RMF path, holes) | ✅ native (rigid) · ❌ sheared | 🔶 bridged | ✅ / 🔶 |
| `Loft` (sections) / `LoftAlong` (evolution law) | ✅ native (rigid + uniform scale; `SolidFactory.Loft`) · ❌ sheared (chord parameterization is metric) | 🔶 bridged (tessellation → mesh SDF) | ✅ native |
| `Union` / `Intersect` / `Subtract` | ✅ native (`BrepBoolean`) | ✅ native | ✅ (from B-Rep, else `MeshBoolean`) |
| `SmoothUnion` / `SmoothIntersect` / `SmoothSubtract` | ❌ no B-Rep form | ✅ native | 🔶 polygonized |
| `Offset` / `Shell(t)` (SDF skin) | ❌ no B-Rep form (`Shell(t)`'s message names the exact overload) | ✅ native | 🔶 polygonized |
| `Shell(t, openings)` (exact inward hollow) | ✅ native (rigid + uniform scale; polyhedral child — `Shelling.Shell`) | 🔶 bridged (tessellation → mesh SDF) | ✅ native |
| `Draft(angle, neutral, pull, faces?)` | ✅ native (rigid + uniform scale; planar-faced prisms — `Draft.Apply`) | 🔶 bridged | ✅ native |
| `RoundEdges(r)` (whole-solid rounding) | ✅ native (rigid + uniform scale; convex planar solids — `Filleting.FilletAllEdges`) | 🔶 bridged | ✅ native |
| `Lattice` (gyroid & co.) | ❌ no B-Rep form | ✅ native | 🔶 polygonized |
| `Chamfer` (planar-face rims) | ✅ native (miters; cone bands on circles) | 🔶 bridged | ✅ native |
| `Fillet` (G1 planar-face rims) | ✅ native (cylinder/torus bands) | 🔶 bridged | ✅ native |
| `PatternLinear` / `PatternCircular` | ✅ native (multi-shell when disjoint) | ✅ native | ✅ native |
| `Hull(...)` (convex hull) | ❌ mesh construction, no B-Rep import | 🔶 bridged (hull mesh → mesh SDF) | 🔶 quickhull over tessellated operand vertices (exact for polyhedral operands) |
| `Remeshed(...)` (isotropic remesh) | ❌ a remesh is defined on a triangulation, and no mesh→B-Rep import | 🔶 bridged (remeshed triangles → mesh SDF, so the field carries their chord error) | ✅ native (`Remesher` over the child's mesh lowering, projected back onto it) |
| `Text(...)` (TrueType outlines) | ✅ native (lines + quadratic Béziers → exact profiles) | ✅ **native** (exact 2D SDF per glyph) | ✅ native |
| `Translate` / `Rotate` / `Scale` (uniform) | ✅ baked into inputs | ✅ native SDF ops | ✅ |
| `Scale(x, y, z)` / `Resized(newSize, auto?)` (OpenSCAD `scale`/`resize`; resize measures `Shape.Bounds(quality)` eagerly and scales about the origin) | per the affine row below | 🔶 bridged unless factors equal | ✅ / 🔶 |
| `Mirror(point, normal)` | ✅ box/cylinder/extrude (any affine) + sphere/torus/cone (mirrored similarity) + revolve (axis negated: F·Rot(d,φ)·F = Rot(−F·d,φ), the LH-thread identity) + sweep (RMF transport is intrinsic — no fix needed) + rim/drill (isometry-commuting surgery/tools) | ✅ native (query point reflected — exact) | ✅ (winding flipped; exact reflection of the tessellation) |
| General affine (shear, non-uniform scale) | ✅ box/cylinder/extrude · ❌ others | 🔶 bridged | ✅ / 🔶 |
| `ExternalThread` (no chamfer, no clearance) | ✅ **native** (boolean-free helical sweep, rigid + uniform scale; not STEP-exportable) | ✅ native (exact-sign thread SDF) | ✅ native (B-Rep tessellation) |
| `ThreadedHole` (no clearance) | ✅ **native** (pilot + thread as ONE clipped-profile helical tool; spiral-arc chains split the drilled faces) | ✅ native | ✅ native (B-Rep tessellation) |
| `ExternalThread` (chamfers) / either with clearance | ❌ chamfer cones / distance-field profile offsets — reported per cause | ✅ native (exact-sign thread SDF) | 🔶 polygonized |
| `Heightmap(heights, cellSize)` (OpenSCAD `surface()`; grids, `.dat`, grayscale PNG via `Heightmap.ReadDat/ReadPng`) | ❌ mesh construction | ✅ exact mesh SDF | ✅ native (manifold-by-construction terrain solid) |
| `From(BrepSolid)` | ✅ (untransformed) · ❌ transformed | 🔶 bridged (mesh SDF) | ✅ tessellated |
| `From(HalfEdgeMesh)` | ❌ no mesh→B-Rep import | ✅ exact mesh SDF (closed meshes) | ✅ as-is |
| `From(Sdf)` | ❌ no SDF→B-Rep | ✅ native | 🔶 polygonized |

✅ native (exact for the target) · 🔶 bridged through another representation
(approximate but robust; `Explain` names the route) · ❌ impossible — the conversion
throws, with the offending node named.

Everything is convertible **to mesh**: what has no B-Rep form is polygonized from the
SDF path instead (Surface Nets), so `ToMesh`/`Scene.Add` never reject a shape.
`ToMesh` picks the highest-fidelity route per graph: whole-tree B-Rep tessellation
first (crisp edges, exact booleans), SDF polygonization when blends/offsets are
involved, per-node mesh booleans only for `From(mesh)` leaves.

## Dropping down to the engine APIs

`Shape` is a convenience layer, not a cage. When something needs an engine's full API,
exit with `ToBrep()`/`ToImplicit()`/`ToMesh()`, work directly, and re-enter with
`Shape.From(...)` — the wrapped result composes with everything else:

```csharp
// 1. Exit to B-Rep for an operation Shape doesn't surface (rim filleting):
var puck = (Shape.Cylinder(10, 4) - Shape.Cylinder(4, 6)).ToBrep();
var rim = puck.Edges.First(IsTopOuterRim);
var filleted = Filleting.FilletEdge(puck, rim, radius: 1);

// 2. Exit to the SDF AST for a custom field (any hand-written Sdf composes):
Sdf ripple = Sdf.Sphere(6).Offset(0.5 * Math.Sin(...));   // or your own Sdf subclass

// 3. Re-enter and keep modeling representation-agnostically:
var body = Shape.From(filleted)
    .SmoothUnion(Shape.From(ripple).Translate(0, 0, 6), 0.8)
    .Lattice(Sdf.Gyroid(2, 0.4));
scene.Add("hybrid", body);
```

The same works with hand-built `HalfEdgeMesh` geometry (scanned or generated meshes):
`Shape.From(mesh)` is an exact signed distance field to the mesh in implicit lowerings
and participates in mesh booleans directly. The support table above tells you which
exits are lossless for the graph you've built — `Explain(target)` tells you for a
specific shape.

**File import**: `Shape.From(path)` reads .stl/.obj/.off through
`MeshReader.ReadAndRepair` (weld, degenerate/duplicate removal, outward orientation,
T-junction zip; hole filling opt-in via `fillHolesAndCracks`) and wraps the repaired
mesh — the `out MeshRepairReport` overload reports what repair did. Docs:
`docs/examples/import.md`.

## Sketching

2D sketches — lines, circular arcs, cubic/quadratic béziers — drawn with a fluent
builder or primitives, then consumed by any representation:

```csharp
var plate = Sketch.Start(-2, -1)
    .LineTo(2, -1)
    .ArcTo(new(2, 1), radius: 1.4, clockwise: false)
    .LineTo(-2, 1)
    .BezierTo(new(-3.4, 0.6), new(-3.4, -0.6), new(-2, -1))
    .Close()
    .WithHole(Sketch.Circle(new(1, 0), 0.4));

var body = Shape.Extrude(plate, 0.5);           // B-Rep: exact NURBS profile
var vase = Shape.Revolve(vaseSketch);           // implicit: exact 2D signed distance
```

Primitives: `Rectangle`, `RoundedRectangle`, `Circle`, `Polygon`, `Slot`. Sketches are
pure 2D; `Shape.Extrude/Revolve/Sweep` place them with a `SketchPlane` (`XY`/`XZ`/`YZ`
presets, `At(origin, x, y)`, or **`On(face)`** — sketch directly on any planar face of
a lowered body: X/Y are the face surface's own directions, the normal is outward, the
origin the face's outer-loop vertex centroid, via `BrepQueries.Frame(face)`; revolve
defaults to `XZ` so the axis is world Z, sketch x = radius). `SketchPlane` is a veneer
over Core's `Frame3d` (`plane.Frame`, `new SketchPlane(frame)`). `Area()` is exact
(arc terms analytic, béziers by exact-degree Gauss quadrature).

The payoff: a sketch knows its **exact 2D signed distance** (`ToRegion()`, composable
with `Sdf.ExtrudedRegion`/`RevolvedRegion`), so sketch-based extrusions and full
revolutions are *Native* in the implicit lowering — no mesh bridge — while B-Rep gets
exact rational arcs/béziers and mesh gets crisp tessellation. Sketches touching the
revolve axis (vases, domes) work everywhere on full turns: on-axis stretches revolve
to nothing and are dropped, their endpoints becoming B-Rep poles (partial revolves
still need axis clearance).

**The sketch field is the inner loop of every implicit sketch solid, so `SketchRegion`
is structure-of-arrays with lane-wise kernels** — lines, full circles, partial arcs and
cubic béziers all have one — behind a bounding-box reject and a y-bucket index over the
ray-parity pieces. Every one of those is a *pure restructuring*: the double that comes
out is bit-for-bit what a plain loop over the segment classes returns, held by golden
bit-hashes taken from that loop plus batch-vs-scalar bit equality
(`SketchRegionKernelTests`). Measured on the batch entry: **2.23×** on a stadium
(arc-dominated) and **1.37×** on an all-bézier outline.

Two of those kernels needed an argument rather than a transcription:

- **Partial arcs decide in-sweep by a cross-product wedge test, not `Atan2`** — which has
  no bit-exact vector form. With `c₀ = f × o` and `c₁ = g × o` against the sweep's two
  boundary rays, in-sweep is `c₀ ≥ 0 && c₁ ≤ 0` up to a half turn and the same pair OR'd
  beyond it (past π the *complement* is the narrow wedge; at exactly π the two forms
  coincide). That decides the same predicate by different arithmetic, so the two agree
  only away from the boundary — hence a **certainty band**: `c₀ = |o|·sin(δ)` and
  `c₁ = |o|·sin(δ − span)`, so requiring both to exceed `1e-9·|o|` puts the point a
  nanoradian off either boundary ray, five orders outside anything `Atan2`, the
  subtraction and the reduction by the *double* `2*PI` can contribute. Any lane inside the
  band sends its block back to the scalar path, so the result is **bit-identical for every
  input** rather than a bounded deviation — and the inputs that most want that land in the
  band by construction, since a segment endpoint shared bit-for-bit with its neighbour
  sits exactly on a boundary ray.
- **The bézier kernel masks the *write*, not the iteration.** Its Newton stage's one piece
  of divergent control flow is a `break` on a vanishing derivative; a stopped lane keeps
  its value because a sticky per-lane flag gates the write to the refined parameter, not
  because iterating on would be harmless. It would not be: a vanishing `g′` makes the step
  infinite and the clamp would turn that into 0 or 1.

**Degeneracy guards scale with the sketch.** A sketch's units and scale are entirely the
caller's choice — a micron seal groove and a metre weldment go through the same
constructor — so its degeneracy tests are RELATIVE (`Sketch.RelativeDegeneracy`, 1e-12):
the enclosed area is compared against the sketch's extent², an arc chord against its
endpoints' coordinate magnitude, and `ArcThrough`'s circumcenter determinant (four times
a signed triangle area, hence quadratic) against that magnitude squared. Absolute floors
were wrong in both directions at once — they rejected a legitimate 1 µm × 0.5 µm pocket
as "encloses no area" while accepting a 1000 × 1e-10 sliver, and called a perfectly good
micron-scale 3-point arc collinear while building a radius-1e15 circle from three
metre-scale points that were collinear to round-off. Two things stay absolute on purpose:
sketch **closure** (1e-9, the weld tier — those endpoints become exactly shared vertices
downstream) and the arc **sweep** guards (1e-12 rad; angles are dimensionless).

### Sketch constraints (`SketchConstraints.cs` / `SketchConstraintSolver.cs`)

**The variational constraint layer**: draw roughly, constrain, solve exact —
`sketch.Constrain()` returns a `ConstrainedSketch` whose vocabulary is Onshape's
(CadQuery's `Sketch.constrain(...).solve()` was the API reference):
`Coincident`/`Horizontal`/`Vertical`/`Parallel`/`Perpendicular`/`Tangent`
(line–arc and arc–arc)/`EqualLength`/`EqualRadius`/`Concentric`/`Fix` plus dimensions
`Distance` (point–point, point–line — 0 is point-on-line)/`Angle`/`Radius`/`Diameter`.
`Solve()` returns a report carrying an ordinary solved `Sketch` — the geometry
pipeline downstream (regions, SDF, extrude/revolve/sweep, features) is unchanged.

- **Variable mapping**: joints shared between consecutive segments are ONE point
  variable (2 DOF); arcs carry center + radius tied to their joints by two internal
  endpoint-consistency rows (net +1 DOF, the bulge); a single-full-circle loop is
  center + radius only; bézier control points are not variables — they follow their
  chord's similarity on rebuild. Entities address the sketch's *normalized* segment
  order (the `ToCurves()` order), including hole loops (`HoleArc(0, 0)` is the washer
  bore).
- **MateSolver doctrine throughout**: Levenberg–Marquardt with an ANALYTIC Jacobian,
  every residual a length (angular rows scaled by the sketch's characteristic
  length), rank-revealing DOF report via diagonally pivoted Cholesky of JᵀJ (dense —
  sketches are tens of variables and dense is honest), refuse-loudly non-convergence
  (a failed solve produces NOTHING and names the constraints carrying the residual),
  and named stationary configurations (Perpendicular between lines drawn exactly
  parallel has no first-order step; the report says so instead of nudging).
- **The drawn configuration is the seed AND the branch selector**: tangency side,
  external-vs-internal arc tangency and arc sweep branch are all read off the
  drawing. Under-constrained is NORMAL — the LM step lies in the Jacobian's row
  space, so motions no constraint sees are never taken and unconstrained geometry
  keeps its drawn proportions; the remaining-DOF count is always reported.
  Over-constrained-but-consistent converges and reports the redundant row count;
  contradictions fail naming the rows that cannot drop.
- **Two numerical lessons paid for here**: (a) adjacent line–arc tangency must be the
  perpendicularity form `d̂·(c − J) = 0` at the shared joint, never
  center-to-carrier-distance = r — the distance form leaves the tangency foot only
  *second-order* constrained (sliding the joint δ along the line moves the residual
  δ²/2r), so a solve "converged" at 1e-9 carried √(2r·1e-9) ≈ 1e-4 of foot slop,
  measured as 3.6e-4 of area error on a fully constrained rounded rectangle, and the
  near-zero singular value corrupted the DOF rank. (b) The rank floor is 1e-6 on
  relative singular values (looser than MateSolver's 1e-8, applied squared): 1e-8²
  = 1e-16 sits *below the pivoted elimination's own round-off* at sketch sizes — a
  rank-9 Jacobian over 14 variables measurably reported rank 10, an arithmetic
  impossibility, because the eliminated Schur complement's ~2e-16-relative residue
  out-ranked the floor.
- Verified: every constraint solo, the classic sloppy rounded rectangle fully
  constrained to **0 DOF** with area exact to the analytic w·h − (4−π)r², an
  under-constrained slot keeping its drawn proportions, contradiction/stationary
  naming, and 1e-3/1/1e3 scale freedom (`SketchConstraintTests`).

Constraint *serialization* does not fall out of the feature-history `[Param]`
descriptor pattern (constraints reference entities, and features re-run fresh
instances anyway), so it is deliberately not in v1.

### 2D regions and sketch booleans

Sketches also lower to **polygonal `Region2d`s** (Core's `Geometry2` — outer loop +
holes, exact containment, exact area) which support union/intersection/difference:

```csharp
var plate  = Sketch.Rectangle(20, 10);
var pocket = Sketch.Rectangle(6, 4);

var region = plate.Subtract(pocket)[0];              // a hole is CREATED by the cut
var (outer, holes) = Profile.FromRegion(region, SketchPlane.XY.Frame);
var body = Shape.Extrude(outer, Vector3d.UnitZ * 6, holes);
```

- `sketch.ToRegions(chordTolerance)` → regions; `Sketch.ToRegions(sketches, tol)` takes
  SEVERAL sketches as one bag of loops and **detects the nesting itself** — draw the
  plate outline and its bolt holes as separate sketches and the holes fall out, no
  `WithHole` needed. (`Region2d.FromLoops` is the classifier: even containment depth =
  outer, odd = hole of its deepest container, so an island inside a hole is its own
  region.)
  A self-intersecting outline is now REFUSED here rather than producing garbage — a loop
  that crosses itself has no interior, so its area, its containment and every boolean below
  it would depend on an arbitrary fill rule (Core's `Region2dValidation`; the message names
  the loop and where it crosses).
- `Union` / `Intersect` / `Subtract` on sketches (and on `Region2d`) run Core's
  arrangement-based `Region2dBoolean`.
- **`sketch.ToCurves()` / `Sketch.FromCurves(curves)`** are the LOSSLESS door, in contrast to
  the regions above: the outer loop as an exact `Curve2d` chain (lines, arcs with their
  signed sweep, cubic Béziers) and back again, with nothing flattened. Use them to fit a
  biarc chain, measure an arc length, or hand a chain to `Profile.FromCurves` for an exact
  analytic profile that never touched a polygon. `FromCurves` refuses a general
  `NurbsCurve2d` by name (a sketch that quietly sampled one would make every downstream
  "exact" claim false) and elevates a quadratic Bézier to the equivalent cubic exactly.
  Hole loops travel as their own sketches — `WithHole` puts them back — which is the whole
  bridge; see design.md §5 for why it is deliberately this small.
- **`sketch.Offset(delta, join, miterLimit, chordTolerance)`** grows (positive) or shrinks
  (negative) the sketch by a constant distance — OpenSCAD's `offset()`, and the geometry
  behind clearance fits, wall shells, pocket stock and cutter compensation. `OffsetJoin`
  is `Round` (arcs), `Miter` (sharp, cut back past the miter limit) or `Chamfer` (bevel).
  Straight-edged input is EXACT under miter/chamfer. An inward offset may split the
  sketch into several regions or consume it entirely, so the result is always a list —
  no inverted loops, because Core's `Region2dOffset` offsets by UNIONING one primitive
  per edge and per corner rather than chasing edges.
- `Profile.FromRegion(region, frame)` (BRep) returns the `(outer, holes)` pair the
  solid factories take, so regions feed `Extrude` / `Revolve` / `Sweep`.
- **`shape.Section(plane, chordTolerance)`** goes the other way — a 3D body back to 2D
  regions in the plane's own coordinates (`projection(cut = true)`, the drawing-view
  section). Exact geometry when the shape lowers to B-Rep, otherwise from the display
  mesh; cavities become holes automatically. Move the plane off any flush face or
  in-plane edge: a section running along a face is an area, not a curve, and is refused.
- **`shape.Silhouette(plane, quality)`** is the OUTLINE the shape casts along the plane's
  normal (`projection(cut = false)`) — a through hole survives as a hole, a blind pocket
  does not. Always from the mesh (a silhouette is the union of the projected faces), so
  fidelity and cost both follow the mesh quality; see the Interop README for the numbers.
- **`Packing.Pack(parts, plateWidth, plateDepth, gap)`** (`Packing.cs`) — 2D bin packing
  of silhouette footprints onto a build plate (build123d's `pack`): a deterministic
  SHELF packer (deepest-first, then width, then input index — no randomness), gap
  honored between parts and to the plate edges, placements returned in input order with
  each part's measured footprint; `PackLayout.Apply`/`Packing.Arrange` return the
  translated shapes (XY only — how a part sits in z is the model's business). Footprints
  are `Shape.Silhouette` bounds, so an overhang wider than the base gets its room. A
  layout that does not fit refuses loudly naming the first part that ran out of plate;
  no rotation or concavity nesting in v1 (stated, not implied). Docs:
  `docs/examples/packing.md` (packed-plate render + one-STL export).

**Fidelity contract — read this before using regions for curved sketches.** Arcs and
béziers are FLATTENED to polylines within `chordTolerance` (default 1e-3 model units,
sagitta-sized for arcs, adaptive de Casteljau for béziers); lines are exact. Anything
built from a region inherits that approximation. A sketch handed straight to
`Shape.Extrude/Revolve/Sweep` is untouched and keeps its **exact** curves — exact NURBS
profiles for B-Rep and the exact 2D signed distance (`ToRegion()`, singular) for
implicit, which is what makes sketch extrusions implicit-*Native*. Exact curved 2D
booleans are a documented follow-up (todo.md); until then, prefer 3D booleans on
exact solids when curved boundaries must stay exact.

## Parametric features (FeatureScript, but plain C#)

A feature is a class with `[Param]` properties and an `Apply` body; a model is an
ordered `FeatureHistory` that regenerates on change:

```csharp
sealed class BoltCircle : Feature
{
    [Param(Min = 2, Max = 24)] public int Count { get; init; } = 6;
    [Param(Min = 1, Units = "mm")] public double Radius { get; init; } = 11;

    public override Shape Apply(FeatureContext c) =>
        c.Body!.Drill(StandardHoles.Counterbored(4), BoltPoints(Count, Radius), 30, c.TopPlane);
}

var history = new FeatureHistory();
history.Add(new BasePlate { Width = 48 });
history.Add(new BoltCircle());
history.Add(new FilletRimFeature { Radius = 2 });
scene.Add(history.ToPart("bracket"));
```

Regeneration replays with **prefix caching** (editing feature 5 re-runs only 5..n —
keep `Apply` a pure function of parameters + context), validates `[Param]` ranges
first, stops at the first failure keeping the last good body, supports suppression,
and reports per-feature statuses. `SaveParameters`/`LoadParameters` round-trip values
as JSON so a design is re-tunable without recompiling.
Standard features (`ExtrudeSketchFeature`, `HoleFeature`, `FilletRimFeature`, patterns,
`BooleanFeature`) cover simple histories; `Feature.FromFunc` handles one-offs.
`FeatureHistory.BodyAfter(i)` is the **rollback** accessor: the body as of feature `i`
(the cached prefix output), which the construction tree below previews.

### Geometry inputs (`GeometryRefs.cs`)

Between-feature geometry references are **semantic queries** over the regenerated
body, not persistent topological IDs — and they have a typed vocabulary, so "this
feature needs a plane" is something a feature can *say*:

| Type | Resolves to | Cardinality |
| --- | --- | --- |
| `PlaneRef` | `SketchPlane` | exactly one |
| `FaceRef` | `BrepFace` | exactly one |
| `FaceSetRef` | `IReadOnlyList<BrepFace>` | at least one (`.Optional()` → any) |
| `EdgeSetRef` | `IReadOnlyList<BrepEdge>` | at least one |
| `AxisRef` | `Ray3d` | exactly one |

```csharp
public sealed class Boss : Feature
{
    [Param(Min = 1)] public double Height { get; init; } = 6;

    [Param(Description = "Face the boss grows from")]
    public PlaneRef Plane { get; init; } = PlaneRef.TopPlane;

    public override Shape Apply(FeatureContext c) =>
        c.Body! | Shape.Extrude(profile, Height, Plane.Resolve(c, nameof(Plane)));
}
```

Queries nest and are named: `FaceSetRef.PlanarWithNormal(n)` / `Cylindrical(r?)` /
`All` / `RimFacesOf(edges)` / `OfKind(SurfaceKind)` / `NthByRadius(n)` /
`GroupAlong(set, direction, n)`, `FaceRef.One(set)` / `Extreme(set, direction)` /
`Top` / `Bottom` / `LargestByArea(set)` / `Largest`, `PlaneRef.TopPlane` / `OnTopFace`
/ `On(faceRef)` / `At(plane)`, `EdgeSetRef.RimOf(faces)` / `Convex` / `Circular(r?)` /
`NthByRadius(n)`, `AxisRef.OfCylindrical(face)` / `Of(origin, direction)`. The
ordering/grouping ones are the serializable spellings of `BrepSelection` in
EngrCAD.BRep (`SortAlong`/`Extreme`/`GroupAlong`/`GroupByCoplanar`/`FilterBy`/
`Area`/`NthByRadius` — the build123d `sort_by`/`group_by`/`filter_by` capability as
LINQ). `FaceSetRef.From/Where` and `EdgeSetRef.From` take a lambda
when no named query fits. A `SketchPlane` — and a `SketchPlane?` whose null means "the
top plane" — converts implicitly, so incumbent code is untouched.

Three properties make them more than sugar:

- **The `Descriptor` is the cache key AND the serialized form.** One canonical
  parseable string (`topPlane`, `extreme(planar([0,0,1]),[0,0,1])`) that `ToString`
  returns, so a `[Param]` reference contributes an honest term to the regeneration
  snapshot and round-trips through `SaveParameters`/`LoadParameters`. Lambda-backed
  references print `opaque(label)`, are `IsSerializable = false`, and load as a
  warning rather than a crash.
- **Resolution is per-`Apply` and never memoized on the reference** — it caches on the
  `FeatureContext`, which is fresh per feature, so validation and `Apply` share one
  query while an edited model always re-resolves. (`Mates` deliberately does the
  opposite and pins at construction: a mate is a numerical constraint, not a query.)
- **`Feature.ValidateInputs` resolves everything before `Apply`, all-or-nothing**, and
  names the property: `"Plane: expected exactly one cylindrical face, found 0."` A
  failure is a `Failed` status with the last good body intact, never an exception from
  inside the operation. Override it to add checks of your own.

`[DeferredInput]` marks an input handed to the `Shape` graph's own late-resolved
selectors (the rim features' `Faces`): validation skips it, because resolving early
would force a B-Rep lowering regeneration otherwise never pays for — the deferred
resolution still names the input when it fails.

## The construction tree: how a part was built

`part.ConstructionTree()` answers "how was this made?" as a row tree any viewer (or
script) can walk without knowing the graph's internal node types:

```csharp
var root = part.ConstructionTree();      // null for raw B-Rep/mesh/SDF parts
foreach (var row in root!.Flatten())
    Console.WriteLine($"{row.Path,-6} [{row.Kind}] {row.Label}");
// ""     [Operation] Drill(2 holes)
// "0"    [Operation] Extrude(sketch)
// "0/0"  [Sketch]    Sketch(4 curves, 1 holes)
```

Two sources feed it:

- a **`Shape`-backed part** gives the operation graph — one row per node, operands as
  children (booleans, hulls, drills, rims, patterns, transforms). Labels come from the
  node's own `Describe()`, the same text `Explain` prints, so they cannot drift.
  Sketch-driven extrude/revolve/sweep rows carry a **sketch child row** holding the
  `Sketch` and its placement matrix.
- a **`FeatureHistory`-backed part** (`history.ToPart(...)`, or `new Part(name,
  history)`) gives the ordered feature list instead: names, `Suppressed` state, and
  each feature's `[Param]` values as leaf rows. `Part.History` is the link.

A row identifies a graph node **by reference plus a positional `Path`** ("0/1/0").
Both halves matter: `Shape` is immutable and shared, so one sub-shape can appear at
several paths (a pattern operand), and the path is what distinguishes the rows while
the reference is what previews are keyed by. Trees are built once per `Part` and
cached, so node references are stable across UI rebuilds.

### Previews (what a row looks like)

`ConstructionPreview.Build(node)` turns a row into display **line geometry**:

- a **sketch** row: its curves flattened onto its `SketchPlane` in 3D (lines exact,
  arcs and Béziers chorded at display resolution — a preview, not a lowering).
- any other row: the feature edges of that sub-graph's geometry, by the same rule
  `Part.GetFeatureEdges` uses (exact B-Rep edges when the sub-shape has a B-Rep
  lowering, mesh dihedrals otherwise). Selecting an intermediate operation therefore
  shows the model **as of that step** — a rollback view.

Building a preview lowers geometry, so it must not run on a UI or render thread — the
same rule `Scene.PreMesh` follows. `ConstructionPreviewCache` is the memo: `TryGet`
takes the synchronous fast path, `Get` builds and caches (call it on a background
task). Entries key on the shape reference, so a sub-shape shared by several rows is
lowered **once**; a failed lowering is reported as `ConstructionPreview.Error` rather
than thrown, so one bad step is a status message, not a crash.

## Queries, chamfer & fillet

B-Rep topology is LINQ-queryable (`EngrCAD.BRep.BrepQueries`): classify faces
(`IsPlanar`, `IsCylindrical`) and edges (`IsLinear`, `IsCircular`, `Length`,
`IsConvex`), walk adjacency (`face.Edges()`, `solid.FacesOf(edge)`), or use sugar
like `solid.PlanarFacesWithNormal(Vector3d.UnitZ)`. Selectors drive the rim features:

```csharp
var plate = Shape.Extrude(Sketch.RoundedRectangle(36, 26, 5), 8)
    .Fillet(2.5, s => s.PlanarFacesWithNormal(Vector3d.UnitZ))      // smooth top rim
    .Chamfer(1.5, s => s.PlanarFacesWithNormal(-Vector3d.UnitZ));   // beveled base
```

Scope (enforced with clear errors): both operate on the closed outer rim of planar
faces. **Chamfer** takes straight edges (sharp corners miter exactly — planar strips)
and full circular rims (exact cone bands). **Fillet** needs tangent-continuous rims —
lines + arcs like rounded rectangles, slots, and circles (exact quarter-cylinder and
torus-segment bands sharing junction arcs); round sharp sketch corners first. Both are
B-Rep-native (implicit lowering bridges through the tessellation); selectors run on
the *lowered* solid, so upstream transforms are visible and feature sizes scale with
uniform scaling.

## Loft

`Shape.Loft(sections, style)` skins a closed solid through two or more planar
cross-sections (`SolidFactory.Loft` under the hood — OCCT's `ThruSections`). Sections
are sketches placed by `SketchPlane`s (the extrude/revolve/sweep vocabulary) or raw
B-Rep `Profile`s; they must have matching segment counts (they correspond by segment
index), winding and starting segment are auto-aligned to the least-twist match, and the
ends are capped:

```csharp
var transition = Shape.Loft(
[
    (Sketch.Rectangle(10, 4), SketchPlane.XY),
    (Sketch.Slot(8, 3), SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY)),
]);                                       // LoftStyle.Smooth; .Ruled for straight strips
```

`LoftStyle.Smooth` interpolates ALL sections with one skin (intermediate sections leave
no edge); `Ruled` runs straight strips between consecutive sections. **Support story**:
B-Rep-Native under rigid placement + uniform scale — the transform bakes into the
section curves and the skin interpolates them exactly. A sheared placement is
B-Rep-Impossible (the loft's chord-length parameterization and least-twist alignment
are metric, so they do not commute with a shear); implicit lowering bridges through the
tessellation. Sections with holes, open (uncapped) skins and periodic lofts are refused
by name — see todo.md for the follow-up assessments.

**`Shape.LoftAlong(section, spine, sectionCount, scale, twist, style)`** is the
evolution-law loft (OCCT's pipe shell with a law): one sketch carried along a spine in
the same rotation-minimizing frames `Sweep` uses, scaled by `scale(s)` and rotated
in-plane by `twist(s)` radians (s = 0 → 1 along the spine), the generated sections
feeding `Loft` unchanged. Without laws prefer `Sweep`, whose swept surface is exact
along the whole path — the law is what `LoftAlong` exists for.

## Draft, shell and whole-solid rounding

Three more OCCT-parity operations ride the same selector story as the rim features
(queries over the *lowered* solid, so upstream transforms are visible and lengths scale
with uniform scaling). All three are B-Rep-Native under rigid + uniform-scale
placements, bridge implicit/mesh through the exact B-Rep, and refuse what they cannot
do exactly BY NAME at lowering:

```csharp
var boss = Shape.Extrude(Sketch.Polygon(outline), 12)
    .Draft(3, neutralOrigin: (0, 0, 0), pullDirection: Vector3d.UnitZ);   // release taper

var tray = Shape.Box(60, 40, 20)
    .Shell(2.5, s => s.PlanarFacesWithNormal(Vector3d.UnitZ));            // open-top hollow

var block = Shape.Box(30, 20, 12).RoundEdges(2);                          // box → 26 faces
```

- **`Draft(angleDegrees, neutralOrigin, pullDirection, faces?)`** (`Draft.Apply`)
  rotates each selected side face's plane about its neutral line — exact, composable
  (chain calls for per-face angles), planar-faced prisms only in v1. Geometry on the
  neutral plane does not move: it is the parting line.
- **`Shell(thickness, openings)`** (`Shelling.Shell`) hollows INWARD keeping the outer
  surface exactly; opening faces are removed (tray), `openings: null` seals the cavity
  as a second shell. **This is deliberately a different call from `Shell(thickness)`**,
  the SDF onion `|d| − t/2` whose skin straddles the surface — two calls, two
  geometries, never one call with representation-dependent walls. Polyhedral children
  only in v1 (curved-face corners need surface–surface re-intersection; todo.md).
- **`RoundEdges(radius)`** (`Filleting.FilletAllEdges`) rounds every convex edge and
  corner in one boolean-free morphological opening — exact cylindrical bands and
  spherical corner patches. Convex planar solids with 3-valent corners in v1;
  concave edges and general trihedral corners are refused by name.

## Patterns

`shape.PatternLinear(count, step)` and
`shape.PatternCircular(count, axisOrigin, axisDir)` union transformed copies (balanced
tree, all representations). Disjoint results become valid multi-shell solids; a
Difference tool swallowed whole becomes a cavity shell. For hole arrays, keep passing
point lists to `Drill` — that stays the cheaper idiom.

**Location sets** (`Locations.cs`): `LocationSet` is "place this feature at these N
poses" as one immutable VALUE — `Grid(cx, cy, sx, sy)` (centred, x-fastest),
`Polar(count, radius, startAngle, rotate)` (CCW, seam not repeated; each location
carries its polar angle unless `rotate: false`), `PolarArc` (both ends included),
`Hex(cx, cy, pitch)` (closest packing, centred by extents), `Linear(count, step)`,
`At(points)`, composed by `Translate`/`Rotate`/`+`. One value feeds three consumers:
`Shape.Drill(spec, locations, depth, plane?)`, `Shape.Pattern(locations, plane?)`
(stamps the shape — modeled at the plane origin — at each location's point + rotation;
for an origin-modeled shape `Pattern(Polar(n, r))` equals
`Translate(r,0,0).PatternCircular(n, ...)` exactly, by conjugation), and
`ComponentAssembly.Place(component, locations, face?)`. Serializable like
`GeometryRef`s: `Descriptor` (`grid(3,2,10,8)`, `translate([5,0],hex(3,3,6))`) is the
cache-key term `ToString` returns, and `LocationSet.Parse` reconstructs it — locations
bit-for-bit, since parsing re-runs the same deterministic constructors.

## Extrude/cut until a face

`shape.ExtrudeUntil(sketch, plane, Until.Next|Last)` (boss) and `CutUntil` (pocket) —
build123d/CadQuery's `until=NEXT/LAST` (`ExtrudeUntil.cs`). Both extrude from the
sketch plane along −normal (the `Drill` convention). The stop distance is resolved by
probe rays from the profile's strict interior against the body's mesh
(`UntilResolver`, an internal seam the tests pin directly): `Next` = the first surface
met for a boss / the first EXIT for a cut (punch through the first wall, stop in the
void); `Last` = the far boundary. **The robustness is the point**: the stop must be
one plane perpendicular to the extrusion (hits clustering within 1e-6 × extent —
planar stops tessellate exactly, genuine curves spread far wider), and anything else
refuses loudly naming the candidates — hit clusters with ray counts for curved/slanted
stops, the miss count for overhanging profiles, the probe point for tangent grazes
(enter/exit alternation breaks). Ray–triangle tests use inclusive barycentric bounds
(±1e-9, dimensionless) because a crossing on a shared mesh edge must register on at
least one triangle — exclusive tests can drop it from both, and a ray that enters but
never exits poisons the resolution. Overshoots follow the Drill never-coplanar rules:
a `Next` boss reaches half the thinnest wall INTO the body (capped 2%), a `Next` cut
half the gap into the void, a `Last` cut 2% past the far face, and a cut tool also
clears the TOP by 2% when the plane sits on a body face (a submerged plane gets no top
clearance — its tool top is interior, and extending it would cut above the plane).
Only a `Last` boss ends exactly flush, documented as the coplanar-union case B-Rep may
refuse. Resolution is EAGER (the `Bounds`/`Resized` policy); the result is ordinary
extrude + boolean nodes, so `Explain` stays honest with no special case.

## Holes

`Shape.Drill` places one hole recipe at a list of 2D points on a plane, cutting along
−normal; every tool is a revolved sketch, so drilling is exact in all representations:

```csharp
var plateTop = SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);
var plate = Shape.Box(30, 20, 12)
    .Drill(HoleSpec.Counterbore(4.5, 8, 4.5), [new(0, 5), new(10, 5)], depth: 14, plateTop)
    .Drill(StandardHoles.Countersunk(3), [new(-10, 5)], depth: 14, plateTop)     // M3, ISO values
    .Drill(StandardHoles.Trisert(4), [new(5, -5)], StandardHoles.TrisertMinimumDepth(4), plateTop);
```

`HoleSpec.Simple/Counterbore/Countersink` take explicit dimensions;
**`StandardHoles`** (metric, mm) supplies ISO 273 clearance fits (close/normal/loose),
DIN 974-style counterbores for socket cap screws, 90° countersinks for ISO 10642
flat-heads, coarse tap pilot holes (`Tapped` — pilot only; `ThreadedHole` below models
the thread itself), and Tappex
Trisert® insert pilots (`Trisert`/`TrisertMinimumDepth` — ⚠ verify the insert table
against the current Tappex datasheet before production use).

**Drill points.** `spec.WithTipAngle(includedAngleDegrees)` gives a blind hole the
conical bottom a real twist drill leaves (`StandardHoles.TwistDrillPoint` = 118°,
`SplitDrillPoint` = 135°). It is exact everywhere — the tool stays ONE axis-touching
revolved sketch, the cone being the profile run from the bore radius down to the apex on
the axis, the same machinery a countersink already uses — and the tessellated result is
an n-gon prism plus an n-gon pyramid, matching the discrete truth as an identity at
every density.

- **Depth is measured to the SHOULDER**, the deepest full-diameter point, with the tip
  reaching `(diameter / 2) / tan(angle / 2)` further. That is the drawing convention
  (ASME Y14.5 / ISO 129 dimension a blind hole excluding its point), and it makes adding
  a tip strictly additive: the same `depth` removes the same cylinder either way, plus
  the cone. `spec.TipLength` reports the overhang, and `HoleCallout` appends
  " ×118° TIP" after the depth.
- **The default stays flat**, which is what a model usually wants for a through hole or a
  reamed/bored feature, and keeps every existing design's tools at exactly the reach they
  had. The standard angles are offered as named constants rather than as
  `StandardHoles` defaults for the same reason — a clearance or counterbored hole is
  normally a through hole, where the point never exists in the finished part.
- The tip is checked like the flat bottom is: an APEX landing exactly on a body face is
  rejected at lowering (a point tangency) just as a coplanar flat bottom is, and the
  cross-plane tool test reads the silhouette, so it sees a point that reaches into an
  opposing bore the shoulder alone would clear.

**Holes are validated against each other before any geometry is built**, because two
cutting tools that overlap or touch are degenerate boolean input and fail deep inside
tessellation rather than at the call that created them. Three layers:

- within one `Drill` call, centre distance against the summed surface diameters;
- across `Drill` calls **on the same placement plane**, the same 2D test (mixing
  clearance holes and counterbores in two calls is the normal way to build a plate);
- across `Drill` calls on **different planes** — opposing bores on the two faces of a
  plate, a side bore crossing a top bore — a 3D tool-vs-tool interference test. Each
  tool is covered by bounding cylinders about its axis, and a pair is cleared by either
  of two sufficient conditions: the distance between the axis SEGMENTS exceeding the
  summed radii, or a separating axis (a finite solid cylinder is convex, and its support
  extent along a unit **d** is exactly `|n·d|·halfLength + r·√(1 − (n·d)²)`). Both are
  needed and neither subsumes the other: the segment distance settles skew and
  offset-parallel layouts, while two COLLINEAR tools drilled towards each other have
  axis segments at zero radial distance however much web is left between them, so only
  the axial projection separates them.

  A cheap whole-tool bound clears the overwhelming majority of layouts in one test; only
  an ambiguous pair is refined slab by slab over the tool's silhouette breakpoints (the
  `HoleSpec.ToolSilhouette` that `ToolProfile` itself is built from, so a validated
  configuration and the geometry actually cut cannot disagree). The refinement is
  **sound in the accept direction at any subdivision** — every slab pair separating still
  proves the tools disjoint — and EXACT wherever a slab's radius is constant, which is
  all of a simple or counterbored tool; only a countersink's cone is over-approximated,
  and it is subdivided. The residual error is therefore a conservative refusal in a
  near-tangent band, which is exactly the configuration a boolean cannot survive anyway.

  Not covered: `ThreadedHole`'s thread void (its tap-drill pilot goes through `Drill` and
  is), and tools from separate `Shape` branches later unioned.

## Threads

Real modeled thread geometry (not cosmetic), built for 3D printing:

```csharp
var stud = Shape.ExternalThread(8, length: 16, clearance: 0.15);        // M8×1.25 stud
var boss = Shape.Cylinder(8, 6).Translate(0, 0, -3) | stud;             // welded onto a boss

var block = Shape.Box(30, 20, 12)
    .ThreadedHole(StandardThreads.Metric(6), [new(-8, 0), new(8, 0)], depth: 14,
                  SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY), clearance: 0.15);
```

**`StandardThreads.Metric(size)`** supplies the ISO 261/262 coarse series M2–M12 with
the ISO 68-1 basic profile (`ThreadSpec` documents the formulas: H = (√3/2)P, crest
flat P/8 at d, root flat P/4 at d1 = d − (5/4)H, depth 5H/8); custom `ThreadSpec`s are
allowed. **`StandardThreads.Fine(size)`** is the first-choice ISO 261 fine pitch of the
same sizes (M8×1, M10×1.25, M12×1.5), **`Metric(size, pitch)`** names a second- or
third-choice one (M10×0.75), and `Pitches(size)` lists what a size carries — coarse
first, then the fine series from coarsest down; an uncatalogued pitch is refused with
that list in the message. Fine tap drills are exactly `d − P` and no second column stores
them: unlike the coarse chart, whose `d − P` falls between stock drills and is rounded,
the fine pitches are round numbers to begin with. `StandardHoles.Tapped(ThreadSpec)` cuts
the pilot for any spec, fine or custom, from the spec's own tap drill. ⚠ Verify the pitch
and tap-drill columns against a current standard before production use.
`ExternalThread` is a threaded rod along +Z (45° lead-in chamfers to the minor
diameter on both ends by default); `ThreadedHole` cuts a tap-drill pilot (via `Drill`,
truncating the internal crests as tapping does) plus a modeled thread void per point.

**Clearance** is the printing fit allowance, applied normal to the flanks (the profile
offsets perpendicular to its own boundary): the external thread *shrinks*, the internal
void *grows*; default 0, typical FDM 0.1–0.25 mm, capped at half the thread depth.

**Left-hand threads**: `spec.LeftHanded()` (or `WithHandedness(bool)`) winds the same
profile the other way — every diameter is shared, so handedness is not a different
thread — and the designation becomes `M8×1.25-LH`, which `ThreadCallout` picks up
automatically. It is **Native in all three representations**, because a left-hand thread
is exactly the mirror image of its right-hand twin: the implicit field flips one sign in
the helical phase (bit-for-bit the mirrored right-hand field), and the B-Rep factory
takes a signed axial rate. **`Mirror` of a thread is therefore Native too, not
Impossible** — the compiler writes a mirrored placement as m = (m·FlipY)·FlipY, where
FlipY is the axis-containing reflection that IS the handedness flip, leaving a proper
similarity to place a rod of the opposite handedness; `Mirror(Mirror(x))` comes back
right-handed. The refusal that remains is a genuinely different one: a sheared or
non-uniformly scaled placement cannot re-place a helix at all.

One measured artifact worth knowing: a mirrored (or left-hand) rod tessellates to the
SAME vertices as the mirror of its right-hand twin but with a systematically ~3× larger
volume deficit at the same density, because a grid quad's diagonal is chosen by corner
ORDER (`HalfEdgeMesh.Triangulated` fans from corner 0) and mirroring a sheared band's
cells swaps which diagonal that picks. Both converge quadratically onto the same
analytic volume, so it is a discretization constant, not a drift.

Threads are **implicit-native** (`Sdf.Thread`: exact sign, documented approximate
distance). **External threads are also B-Rep-native** when the basic profile is
unmodified — zero clearance and `chamferEnds: false` — via
`SolidFactory.MakeThreadedRod`: the entire lateral boundary is ONE boolean-free
co-rotating sweep (one exact `HelicalSurface` band per profile facet sharing `Helix3d`
rails, flat caps bounded by spiral arcs; crest phase-aligned with the SDF so all three
representations are the *same* geometry; any length, no whole-turn constraint; not
STEP-exportable). Such threads mesh through exact B-Rep tessellation. With chamfers
(the default) or clearance, B-Rep stays **Impossible with a per-cause report** — 45°
chamfer cones cutting helical bands are future surface-intersection work, and
clearance offsets the profile as a distance field (reflex corners round into arcs, no
exact B-Rep counterpart) — and meshes come from Surface Nets, the printing route.
**`ThreadedHole` is B-Rep-native at zero clearance** via a subtlety worth knowing:
the B-Rep path does NOT drill the pilot separately (the pilot bore wall and the
thread tool's root band would be coaxial — tangent, unsupported boolean input);
instead each hole subtracts ONE combined tool — the internal thread form clipped at
the pilot radius, so the tap-drill volume is part of the same boolean-free helical
rod. The only face pairs the boolean sees are helical-band ∩ drilled-plane: exact
spiral arcs that chain into a closed loop the plane face splits along
(`FaceSplitter.SplitByClosedCurveChain`). Nonzero clearance keeps B-Rep Impossible
with the same distance-field report, so thread features stay honest about what they
can and cannot represent. One boundary: downstream B-Rep booleans may cut modeled
threads only with planes perpendicular to the thread axis (the exact spiral case) —
cuts along the threads fail loudly; use clearance or the implicit route for those.

## Text

Modeled text — OpenSCAD's `text()`, but exact. TrueType `glyf` outlines are straight
lines and **quadratic** Béziers, OpenType/CFF (`.otf`) outlines are lines and **cubic**
Béziers, and `SketchBuilder` already has `LineTo`/`QuadraticTo`/`BezierTo` — so glyph
contours of either flavour map onto sketch segments with **no flattening**. Text
therefore inherits the whole pipeline: exact NURBS profiles in B-Rep, the exact 2D
signed distance in implicit, crisp tessellation in mesh — Native in all three, no
bridge anywhere.

```csharp
var font = TrueTypeFont.Load(@"C:\Windows\Fonts\arial.ttf");

var badge = Shape.Text("ENGRCAD", font, size: 9, height: 1.5,
                       style: new TextStyle { Align = TextAlign.Center });

IReadOnlyList<Sketch> outlines = TextOutlines.Sketches("ENGRCAD", font, 9);  // 2D, for your own ops
```

- **Fonts** (`Text/TrueTypeFont.cs`) are read by a hand-rolled, dependency-free parser
  (kernel projects pack to NuGet and take no third-party dependencies — the same reason
  `PngWriter` and the EGL binding are hand-rolled). Tables: `head`, `maxp`, `cmap`
  (formats 4 and 12), `loca`, `glyf` (simple **and** composite glyphs, with the
  repeat/short-vector coordinate compression), `hhea`/`hmtx`, plus optional `kern`
  (format 0), `name` and `OS/2`. Hinting instructions are skipped — modeled text is
  resolution independent. **TrueType Collections (`.ttc`) and variable-font `CFF2`
  tables are rejected with a message naming the limitation**, never silently
  mis-modeled.
- **OpenType/CFF (`.otf`) fonts work too** (`Text/CffOutlines.cs`): `OTTO` containers
  store glyphs as PostScript Type 2 charstrings — cubic Béziers — parsed by the same
  hand-rolled approach (INDEX/DICT structures, local + global subroutines with the
  count-dependent bias, the whole curve-operator family including flex, and CID-keyed
  fonts via FDArray/FDSelect). Contours carry `GlyphContour.IsCubic` and become
  `BezierTo` segments, so `.otf` text is exactly as exact as `.ttf` text;
  `font.HasPostScriptOutlines` reports which flavour loaded. **The decoding trap worth
  knowing**: `hintmask`/`cntrmask` are followed by one data byte per eight declared
  stems, *including stems declared implicitly by arguments still on the stack* —
  miscounting reads mask bytes as operators and garbles everything after, which is why
  the synthetic-font tests pin decoded outlines to exact coordinates (CFF's cousin of
  TrueType's implied-midpoint subtlety). Legacy `seac` accent composition and the
  Type 2 arithmetic operators are rejected by name.
- **Size is the em size** (the typographic meaning of "12 point"); capitals are shorter.
  When a drawing specifies letter height, convert with `font.EmSizeForCapHeight(h)`.
- **The origin is the baseline** at the start of the first line — x along the writing
  direction, y up. Descenders reach below y = 0, further lines sit below the first, and
  `TextStyle.Align` decides whether x = 0 is a line's start, middle or end.
- **`TextStyle`** carries `LetterSpacing` (tracking, inserted between glyphs only),
  `LineSpacing` (baseline step, default 1.2), `Align` and `Kerning` — all spacing as a
  multiple of the em size, so one style is correct at every size. Kerning comes from the
  OpenType `GPOS` `kern` feature when the font has one (`Text/GposKerning.cs`: PairPos
  formats 1 and 2 — the class-pair matrix most fonts use — unwrapped through Extension
  lookups, both coverage and both class-definition formats, lookups accumulating), else
  from the legacy `kern` table; per the spec, a GPOS `kern` feature makes the legacy
  table invisible rather than merging with it. `font.HasKerning` reports whether either
  source exists.
- **Counters** (the holes in O, A, 8) are separate contours. TrueType's convention is
  clockwise outlines and counter-clockwise counters, but real fonts violate it often
  enough that orientation is not trusted: contours are nested by **containment** (a
  point-in-region majority vote using the sketch's own exact parity), even depth draws
  and odd depth becomes a hole attached to its immediate parent. An island inside a
  counter becomes its own top-level sketch.
- **Missing characters throw**, naming the character and the font — a part number
  engraved with a character silently dropped is worse than a failed build. Blank glyphs
  (space) draw nothing and still advance the pen.
- `TextOutlines` also measures: `AdvanceWidth` (exact typographic width, never touches
  an outline), `Bounds` (the actual ink) and `LineHeight`.

### Embossing and engraving

No new operation is needed — place the text on a face with `SketchPlane.On(face)` (or an
explicit `SketchPlane.At`) and boolean it. The engraving tool deliberately **overshoots**
the face (1.5 deep for a 1 mm pocket), the same rule `Shape.Drill` follows so booleans
never see coplanar faces:

```csharp
var plate = Shape.Box(70, 22, 4);                                   // top face at z = 2
var top = SketchPlane.On(plate.ToBrep().PlanarFacesWithNormal(Vector3d.UnitZ).First());
var pocket = SketchPlane.At((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);   // 1 mm down
var style = new TextStyle { Align = TextAlign.Center };

var embossed = plate | Shape.Text("ENGRCAD", font, 12, height: 1.2, top, style);
var engraved = plate - Shape.Text("ENGRCAD", font, 12, height: 1.5, pocket, style);

scene.Add(new Part("badge", engraved));
```

**Engraving is B-Rep-Native.** A whole word subtracted from a body lowers to a valid
single-shell solid whose volume is the plate minus the exact glyph section times the
pocket depth — measured exact to machine precision on straight-sided glyphs, and limited
only by chordal tessellation on curved ones. This is recent: glyph side walls are sketch
extrusions, and until `SurfaceIntersection` grew an exact **bounded planar carrier** path
(see the BRep README) they went through the marching tracer, which stopped short of each
wall's ends. Every engraving was then either an open mesh or — worse — a closed,
`Validate`-clean solid with the tool buried as an internal cavity and the wrong volume.

**What still needs the implicit route.** Two configurations, both loud rather than
silent now (`BrepBooleanException`, whose message names this workaround):

- **Lettering that runs off an edge of the body.** A glyph straddling a side face makes a
  cut chain that crosses the boundary part-way; `FaceSplitter` rejects it
  ("Open splitting curves must start and end outside the face"). Keep the text inside the
  face, or use the field.
- **Flush embossing is not fused.** Text placed exactly on a face (`SketchPlane.On(face)`)
  is a coplanar pair, so the union takes the disjoint fast path and the result is the body
  and the glyphs as *touching* shells — closed, valid and exactly the right volume, but
  not one shell. **Sink the lettering a fraction into the face** (place the sketch plane
  0.1 mm below and add that to the height) and the pair is transversal, so the boolean
  really fuses into one shell:

```csharp
var sunk = SketchPlane.At((0, 0, 1.9), Vector3d.UnitX, Vector3d.UnitY);   // face at z = 2
var fused = plate | Shape.Text("ENGRCAD", font, 12, height: 1.3, sunk, style);   // one shell
```

The implicit route stays the general fallback and is exact as a field:
`Shape.From(shape.ToImplicit()).ToMesh(quality)` (raise `SdfResolution` for crisp
lettering). For a purely visual plate, adding the body and the lettering as two `Part`s
skips the boolean entirely.

## The document model: Part, Assembly, Tab, Scene

Parts carry all their own information — name, geometry from **any** engine (`Shape`,
`BrepSolid`, `HalfEdgeMesh`, or `Sdf`), color, placement — and are grouped into a
scene's named tabs, which the viewer shows as a tab strip (per-tab cameras):

```csharp
var scene = new Scene(new MeshQuality { SegmentsPerCircle = 48 });

var housing = scene.AddTab("housing");
housing.Add(new Part("body", bodyShape, Palette.Steel));
housing.Add(new Part("lid", lidSolid));            // color auto-assigned from Palette

scene.Add(new Part("jig", jigMesh));               // shorthand: default "Model" tab
```

Colorless parts get palette colors from the tab: at `Add` time, and — for parts added
to an assembly AFTER the assembly joined the tab — retroactively on the next
`Tab.Instances()` flatten. **The color-stability rule**: a color, once assigned, never
changes (assignment is `??=` and the tab's palette cursor only advances), and
latecomers take the *next* entries in the tab's display order — so adding a part later
can never reshuffle an existing part's color, only consume a fresh one.

`Part.GetMesh(quality)` produces the display mesh on first use and caches it (Shapes
via their best route, B-Reps tessellated, SDFs polygonized, meshes as-is);
`Scene.PreMesh()` does this for every **distinct** part up front so viewers never
tessellate on the render thread. Part names are unique per tab. `Part` is
deliberately a leaf — tabs and assemblies are the containers.

**`PreMesh` primes parts in parallel** (`ParallelFor.Blocks` over the distinct-part
list). Parts are independent by construction: every cache a part fills — the lowered
solid, the display mesh, the feature edges, resolved annotations — lives on that part
behind that part's own lock, and lowering a `Shape` graph *builds* fresh geometry
rather than mutating the graph, so nothing about the output depends on scheduling.
Scene wall time therefore drops to the SLOWEST part rather than the sum: the demo
scene's 25 distinct parts measured **6.1 s sequential → 3.6 s** on 8 cores, where one
drilled plate alone is 2.3 s of that. Failures stay deterministic too — each part's
exception goes into its own slot and the first failure *in scene order* is rethrown
with its original stack, so a broken part still reports exactly what the sequential
pass reported instead of a scheduling-dependent `AggregateException`. (One caveat
worth knowing: `Shape.From(brepSolid)` used as a **boolean operand** hands the raw
solid straight to `BrepBoolean`, which consumes its inputs — so two parts sharing one
source solid *through a boolean* were already unsafe to lower twice, sequentially or
not. Wrapping a solid as a whole part, or as anything other than a boolean operand,
is fine.)

**Preparing on demand.** A host that does not want to mesh the whole document before
showing anything has three finer entry points, all idempotent and all safe off the
render thread (the viewer meshes a tab when it is first *viewed*, on a background
task, and shows a progress bar):

| API | What it does |
| --- | --- |
| `Part.HasMesh` | Non-blocking probe: is the display mesh already built? Takes no lock, so it never waits behind a mesh in flight on another thread — a UI can ask before deciding to show numbers. |
| `Part.Prepare(quality, progress)` | One part's worth of `PreMesh`: display mesh + feature edges + resolved annotations. |
| `Tab.PreMesh(quality, progress)` | The same for one tab's distinct parts, in order — the per-tab sibling of `Scene.PreMesh`. |

`Part.GetMesh(quality, progress)` threads a `ProgressCancel` in, and here the rule is
narrow on purpose: **only the SDF route observes it** (Surface Nets reports fractions
and polls for cancellation). A B-Rep lowering runs to completion because its result is
cached inside `TryGetSolid`, and abandoning one mid-flight would leave that cache
claiming a lowering it never produced. `Tab.PreMesh` therefore polls for cancellation
*between* parts: the part in flight finishes and its mesh stays cached (so returning to
it costs nothing), which is the useful half of cancelling anyway.

Two display flags travel with the part rather than with a viewer, so a design states
its own intent: `Part.DisplayMode` (Shaded / Wireframe / Translucent) and
`Part.ClippedBySection` (default true). Setting the latter false makes a viewer's
section planes leave the part whole — the convention every drafting standard shares,
that shafts, bolts, nuts, keys, pins and ribs are drawn **unsectioned** in a section
view, because cutting a solid fastener lengthwise shows nothing and only clutters the
section. It is the "cut the housing, keep the internals" switch for assemblies, and it
has no effect at all when no section is active.

**`Part.TryGetSolid()` lowers the part's exact B-Rep at most ONCE and caches it**
(null when the part has no exact form — an SDF or mesh part, a Shape with no B-Rep
route, or a lowering that failed). Everything that needs the solid takes it from
there: the display mesh (`GetMesh` tessellates it), the feature-edge overlay,
selector-based annotations, STEP export, and construction previews of the whole part.
Before this, each of those compiled the graph independently — on a five-hole drilled
plate that was three ~9 s lowerings, and `Scene.PreMesh` of a heavy Shape scene went
from **32.8 s to 10.1 s** once they shared one. `PreMesh` primes it off the render
thread like the mesh; a lowering that fails is remembered, so the failure surfaces
once (verbatim from `GetMesh`) instead of being retried per consumer.

**`Part.TryGetSdf(out sdf, out error)` is its implicit twin**, cached the same way: an
`Sdf` part hands back its own field, a `Shape` with an implicit route is lowered at
most once, and everything else returns false with a null error — "nothing to show" and
"it went wrong" stay distinguishable. A failed lowering becomes a cached *diagnostic*
rather than an exception per caller, because its consumer (the viewer's section-plane
isoline overlay) asks per rebuild and must degrade to a status message. This matters
for the same reason as the B-Rep cache: a bridged shape's implicit lowering can build
a `MeshSdf`. Deliberately NOT primed by `PreMesh` — only the section overlay needs it,
and paying for it on every scene load would tax every user of the viewer.

`Part.GetFeatureEdges(quality)` is the display **edge overlay**, cached the same
way (and primed by `PreMesh`): parts with an exact solid sample their ACTUAL B-Rep
edges at display resolution (`BrepFeatureEdges` in Interop, at least 96 segments per
circle regardless of mesh quality), so exact circles stay smooth at any tessellation;
everything else (SDF/mesh parts, failed lowerings) falls back to mesh-dihedral
extraction.

### Assemblies (v1: occurrences)

An `Assembly` is a named list of `Occurrence`s — a shared `Part` **or** a nested
`Assembly`, each with a rigid `Frame3d` pose relative to its parent. Poses compose
down the tree; tabs hold assemblies next to loose parts:

```csharp
var clamp = new Assembly("clamp");
clamp.Add(platePart);                                     // occurrence "plate", identity
foreach (var (x, y) in corners)
    clamp.Add(boltPart, Frame3d.FromXY((x, y, 0.8), Vector3d.UnitX, Vector3d.UnitY));
// one bolt Part, four occurrences: "bolt", "bolt.2", "bolt.3", "bolt.4"

var stack = new Assembly("stack");
stack.Add(clamp);                                         // sub-assembly at identity
stack.Add(clamp, Frame3d.FromXY((0, 0, 2.2), Vector3d.UnitY, -Vector3d.UnitX));

scene.AddTab("assembly").Add(stack);
```

Semantics worth knowing:

- **References, not copies.** The same `Part` (or `Assembly`) placed many times is
  shared: it is meshed **once** (`GetMesh` cache; `Scene.AllParts` enumerates each
  distinct part once, which is what `PreMesh` walks) and every instance renders with
  its own composed world matrix. `Occurrence.Frame` is mutable, so parametric design
  code can re-pose between live reloads.
- **Names.** Occurrence names are unique per assembly level: derived names
  auto-suffix (`bolt`, `bolt.2`, …), explicit duplicates throw, `/` is reserved for
  paths. Tab item names are unique across parts *and* assemblies.
- **Flattening is the seam.** `Tab.Instances()` (and `Assembly.Flatten()`) resolve
  the tree to `PartInstance`s — `(Part, World, Path)` with paths like
  `"stack/clamp.2/bolt"` and `World = occurrenceFrames…ToMatrix() * part.Transform`.
  Viewers and exporters consume that list and never walk assemblies themselves;
  loose tab parts flatten too (path = name, world = `Part.Transform`).
- **Cycles are rejected** at `Add` time; an assembly can appear in many parents
  (a DAG), just never inside itself.

### Bill of materials

`Bom` counts occurrences per distinct `Part` over the flattening — the same
`PartInstance` list viewers render — so nested sub-assemblies roll up for free:

```csharp
var bom = Bom.For(stack);            // also For(tab), For(scene), For(instances)
Console.WriteLine(bom.ToText());     // aligned table: QTY / ITEM / KIND / WHERE
File.WriteAllText("bom.csv", bom.ToCsv());
```

- A `BomLine` is `(Part, Quantity, Paths)`; `Item` is the catalogue designation for
  hardware and the part name otherwise, and `Hardware` carries the
  `HardwareComponent` itself, so `bom.Hardware` / `bom.Manufactured` split the list
  into bought-in and made. `Part.Hardware` is set by `HardwareComponent.ToPart()`,
  which caches one part per catalogue item — so N placements of one screw are one
  line with quantity N.
- Lines group by part **reference**, the document model's own notion of sameness. Two
  separately constructed parts that happen to share a name stay two lines (they are
  two parts); `bom.ByItem()` rolls those together for a purchasing view.
- `Bom.Structured(assembly)` is the indented BOM: one `BomNode` per item per level,
  with `Quantity` per parent and `TotalQuantity` multiplied down the tree, so a
  sub-assembly placed twice doubles everything inside it. The leaf totals agree with
  the flat list by construction — both count the same occurrences.
- The viewer's **BOM** toolbar button shows the current tab's table and drops a CSV in
  the temp directory, reporting the path in the status bar (the `Capture` convention).

### Exploded views

An explode is a scalar 0 → 1 composed into the flattening, so viewers and offscreen
renders get it for free and no second code path exists:

```csharp
stack.AutoExplode();                          // derive offsets from the geometry
var pulledApart = stack.Flatten(explode: 1);  // or tab.Instances(0.4), scene.Instances(t)
```

- `Occurrence.ExplodeOffset` is where an occurrence goes at factor 1, in the **parent
  assembly's** coordinates. `Flatten(factor)` adds `factor × offset` to the frame's
  origin before composing, so nested offsets compose: a sub-assembly moves as a unit
  and its own occurrences move within it. At factor 0, or with no offset, the frame is
  returned untouched — an un-exploded flatten is bit-for-bit what it always was.
- **The instance count and order are independent of the factor.** That is what lets the
  viewer animate it with `ViewportControl.SetInstancePoses` — matrices only, no GPU
  buffer touched — so N instances keep sharing one mesh, one buffer set and one pick BVH
  through the whole animation.
- `AutoExplode` derives offsets with three rules: the **largest non-catalogue occurrence
  is the datum and does not move**; **hardware backs out along its own axis** (a
  `HardwareComponent` body is modeled +Z out of the host, so the occurrence frame's Z
  *is* the fastener axis — better than any centroid guess, and free); everything else
  moves **radially away from the datum**, scaled by how far out it already sits, so the
  outermost item travels the full spread and a stack keeps its order. It needs the
  parts' bounds, so like `Scene.PreMesh` it belongs off the render thread.
  - The datum matters: taking the direction from the *assembly centroid* instead is
    wrong whenever the parts are spread out — the centroid sits in the empty middle and
    everything, base included, flies away from nothing.
- Viewer: an **Explode** toggle plus a factor slider (disabled for a tab with no
  assemblies). Headless: `EngrCad.RenderToImage(..., explode: 1)`,
  `--explode <factor>`, and `EngrCad.Configure().WithExplode(f)`.
- **Per-occurrence factors**: `Assembly.Flatten(Func<Occurrence, double>)` /
  `Tab.Instances(Func<Occurrence, double>)` / `Scene.Instances(Func<Occurrence,
  double>)` ask the delegate once per occurrence — the sequenced explode's substrate
  (fasteners back out before the cover lifts; `ExplodeTrack.Stagger` in
  `EngrCAD.Viewer.Core` drives it along an animation timeline). Same walk as the
  scalar overload, so instance count/order are unchanged and a factor of exactly 0
  leaves that occurrence's frame bit-for-bit untouched.

### Mates (constraints)

`Flatten` composes `Occurrence.Frame`s and those frames are mutable, so mating is
exactly "solve for the frames":

```csharp
var report = new MateSet(rig)
    .Ground(baseOccurrence)
    .Add(Mate.Concentric(
        MateGeometry.CylindricalFace(baseOccurrence, s => Bore(s)),
        MateGeometry.CylindricalFace(lidOccurrence,  s => Bore(s))))
    .Add(Mate.Planar(
        MateGeometry.PlanarFace(baseOccurrence, s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First()),
        MateGeometry.PlanarFace(lidOccurrence,  s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First())))
    .Solve();                     // writes Occurrence.Frame; throws if unsatisfiable
```

- **Mate kinds**: `Coincident` (points, 3), `Planar` (faces bear against each other with
  an optional gap, 3), `Concentric` (axes collinear, 4), `Distance` (1), `Parallel` (2),
  `Perpendicular` (1), `Angle` (1). `Ground(occurrence)` pins one in place;
  `MateGeometry.World(point, direction)` mates against space itself.
- **Geometry references** are `MateRef`s: explicit local coordinates
  (`MateGeometry.Point/Axis`), **semantic B-Rep selectors**
  (`MateGeometry.PlanarFace/CylindricalFace` with a lambda), or **typed
  `FaceRef`/`AxisRef` queries** (`MateGeometry.PlanarFace(occ, FaceRef.Top)`) — the
  same `GeometryRefs` vocabulary features declare. All of them resolve **once, when
  the mate is built**: a mate is a numerical constraint, so its geometry is pinned
  rather than re-queried inside the solver's inner loop (the deliberate opposite of
  feature inputs, which re-resolve per regeneration — eager-vs-lazy is the consumer's
  choice, not a different vocabulary).
- **Across assembly levels**: every builder has an `(Assembly, path, …)` overload —
  `MateGeometry.Point(rig, "carrier/bolt", p)` — where the occurrence path
  (`Assembly.ResolvePath`) names a unique *placement* even when the sub-assembly type
  is shared. The deep occurrence's own local frame becomes the solver unknown; its
  ancestors' frames compose as inputs, and an ancestor that is itself a mate target
  contributes its own chain-rule columns (a variable is a world-space rigid
  perturbation, so the chain rule is the one-level formulas with the rotation moment
  arm taken about each free link's composed world origin — still fully analytic).
  Sub-assemblies no mate reaches into stay rigid; `Ground("carrier/bolt")` pins a deep
  occurrence so a chain through it still rides its free ancestors. **One refusal keeps
  it honest**: a deep target whose owning sub-assembly is placed more than once is ONE
  shared frame — solving would move every placement — so the solve rejects it naming
  the placements (follow-up: flexible sub-assemblies with per-instance internal DOF).
- **Persistence** (`MateSet.SaveMates`/`LoadMates`, the `FeatureHistory` conventions):
  each end saves its occurrence path, its pinned coordinates, and — when built from a
  typed query — the `GeometryRef` descriptor (`cylindricalFace(one(cylindrical))`).
  Loading resolves paths against the assembly and re-resolves queries against the
  parts *eagerly* (construction time is load time), returns warnings instead of
  throwing (a failed query falls back to the pinned coordinates; a missing occurrence
  skips that mate by name), and saved JSON is a fixed point under save→load→save —
  which is why `MateRef` keeps an already-unit direction verbatim instead of
  re-normalizing it by an ulp. Lambda-backed selectors save an `opaque` marker and
  load from coordinates with a warning, exactly like opaque feature references.
- **How it solves**: Levenberg–Marquardt on the residuals with an **analytic** Jacobian
  (finite differences would cap accuracy near 1e-8, an order worse than the 1e-9 weld
  tier this aims at). Angular residuals are multiplied by the assembly's characteristic
  length so every residual is a length and one linear tolerance is meaningful; the
  rotation variables are divided by the same length so every Jacobian column is O(1).
- **It refuses loudly.** A solve that does not converge writes NOTHING — the frames are
  left exactly as the caller left them — and `MateSolveResult.Diagnostics` names the
  mates carrying the residual. The result also always reports how many degrees of
  freedom the mates actually pinned (rank of the Jacobian, from a diagonally pivoted
  Cholesky of JᵀJ), plus a per-movable-occurrence report
  (`MateSolveResult.OccurrenceFreedoms`: the rank of each occurrence's own 6×6 block —
  an upper bound on its pinning, honestly labeled, since a mate between two
  occurrences pins *relative* motion), so an under-constrained assembly says so;
  `MateSolverSettings.RequireFullyConstrained` turns that into a failure too. An
  under-constrained assembly is legitimate CAD — a hinge is *supposed* to keep a
  rotation — so it is reported, not refused, by default.
- **One honest limitation**: an `Angle` or `Perpendicular` mate whose two directions
  start exactly parallel is a **stationary** configuration — d/dθ cos θ is zero at
  θ = 0, so no first-order step exists. The solver detects it and says so rather than
  nudging at random and sometimes converging. Place the occurrence roughly where it
  belongs and solve again.

### STEP assembly export

`StepAssembly` writes the product-structure form from the same flattening:

```csharp
StepAssembly.WriteFile(tab, "gearbox.step");    // also (assembly, …), (scene, …), (instances, …)
```

One `PRODUCT` per distinct solid, one `NEXT_ASSEMBLY_USAGE_OCCURRENCE` per placement,
and each pose as a `CONTEXT_DEPENDENT_SHAPE_REPRESENTATION` over an
`ITEM_DEFINED_TRANSFORMATION` — so a bolt placed forty times is written once and
referenced forty times. Solids come from `Part.TryGetSolid()`, the cache the display
mesh and edge overlay already share, so exporting lowers nothing a second time.
`StepAssembly.Plan` returns what will be written **and** what was skipped (parts with
no exact B-Rep), so a caller can report the gaps instead of shipping a quiet hole; the
viewer's `--export part.step` uses it and now writes ONE assembly file instead of one
file per part. `StepReader` reads the structure back (`StepReadResult.Instances`,
`HasAssemblyStructure`), including nested sub-assemblies, with poses intact. STEP
placements are rigid, so a scaled or sheared instance transform is refused by name
rather than written as if it were rigid.

### Annotations (PMI)

Parts carry their own manufacturing information — **3D annotations** attached in
part-local space (`Annotations.cs`), rendered by the viewer with dimension lines and
billboarded text (and included in offscreen/docs renders). Modeling owns the data +
measurement only; no rendering here.

```csharp
var plate = new Part("plate", plateShape)
    .Annotate(LinearDimension.BetweenFaces(              // auto-measures 40
        s => s.PlanarFacesWithNormal(-Vector3d.UnitX).First(),
        s => s.PlanarFacesWithNormal(Vector3d.UnitX).First()))
    .Annotate(RadialDimension.OnEdge(                    // reads the actual bore: "⌀5.5"
        s => s.Faces.SelectMany(f => f.Edges()).First(e => e.IsCircular(out _, out _, out _)),
        diameter: true))
    .Annotate(new LeaderNote((0, 0, 4), "DEBURR"))
    .Annotate(new DatumLabel((-20, 0, 0), "A"));
```

- **Two kinds of geometry reference.** Plain part-local points (`LeaderNote`,
  `DatumLabel`, point-to-point `LinearDimension` — what the viewer's measure tool
  creates), and **semantic B-Rep selectors** (`Func<BrepSolid, BrepFace/BrepEdge>`,
  the `BrepQueries` vocabulary the rim features use). Selector dimensions
  **auto-measure**: `LinearDimension.BetweenFaces` measures the actual
  plane-to-plane distance of two parallel planar faces, `RadialDimension.OnEdge`
  reads the actual radius of an `IsCircular` edge. Selectors re-run per resolution,
  so dimensions track parameter edits and `FeatureHistory` regeneration — the same
  topological-naming story as features (no persisted indices to go stale).
- **Resolution.** `Part.ResolveAnnotations()` lowers the geometry to B-Rep once
  (cached) and returns render-ready `ResolvedAnnotation`s (kind, part-local anchors,
  placement offset, formatted text, measured value); viewers pose them by the
  instance transform, so assembly instances show their part's annotations in place.
  `TryResolveAnnotations` is the non-throwing viewer path (a bad selector after an
  edit becomes a status message, not a crash) and `Scene.PreMesh` pre-resolves off
  the render thread. `Label` overrides the formatted text; `Offset` places the
  dimension line/text (zero = renderer default).
- **Callouts.** `HoleCallout.From(spec, anchor, depth)` and
  `ThreadCallout.From(spec, anchor, depth)` generate standard-text `LeaderNote`s
  from `HoleSpec`/`ThreadSpec` ("⌀5.5 ↧14", "M6×1 ↧12") so drilled/tapped parts can
  label themselves from the same specs that cut them.

## Standard components ("smart" hardware)

Real hardware, where **a component is more than geometry: placing it modifies the host
model and assembles itself.** One call cuts what the part needs and adds the occurrence:

```csharp
var top = SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);   // Box(70, 44, 12) top

var build = new ComponentAssembly("plate", Shape.Box(70, 44, 12), Palette.Sage);
build.Place(StandardComponents.CapScrew(5, 16), [new(-24, 0), new(24, 0)], top);
build.Place(StandardComponents.TrisertInsert(5), [new(0, 0)], top);

scene.AddTab("hardware").Add(build.ToAssembly("bracket"));
// plate now has two Ø10 counterbores over Ø5.5 clearance holes and one Ø7.1 insert
// pilot; the assembly has the plate plus three posed component occurrences.
```

A `HardwareComponent` carries three things: its own parametric `Body` (a `Shape`), a
seating convention, and a **host preparation** — the cut the target body needs
(`Prepare`, and `PrepareAnchor` for the far body of a stack). The catalogue is
deliberately small and correct rather than broad:

| Component | Host preparation | Body (v1 fidelity) |
| --- | --- | --- |
| `StandardComponents.CapScrew(size, length, seating, fit)` — ISO 4762 SHCS | ISO 273 clearance hole, plus the DIN 974 counterbore when `ScrewSeating.Counterbored` (the default); as an anchor, the coarse tap-drill pilot plus two pitches of runout | head cylinder (dk, k = d) on a plain shank, one exact revolve — **no hex socket, no modeled thread** (use `Shape.ExternalThread`) |
| `StandardComponents.TrisertInsert(size)` — Tappex Trisert® | the catalogue pilot bore (`StandardHoles.Trisert`) at `TrisertMinimumDepth` | plain sleeve bored to the thread's minor diameter — no knurl, no flange |
| `StandardComponents.Dowel(diameter, length, inserted)` — ISO 2338 m6 | reamed hole at the **nominal** diameter, just past the inserted length (both bodies of a stack) | cylinder with 45° end chamfers rather than the standard's crowned ends |

⚠ ISO 4762 head diameters and the Trisert table are transcribed, not derived — both
carry a verify-against-the-datasheet warning in the source. Head height (k = d), the
thread profile and the clearance/counterbore/tap-drill sizes all come from formulas or
tables already in `StandardHoles`/`StandardThreads`.

**The local frame is one rule.** A component's `Body` is modeled with **+Z out of the
host** and the origin at its *seating datum* — the surface it bears on (a cap screw's
head underside, an insert's or a dowel's top face). `SeatDepth` says how far below the
host's face that datum sits, so a counterbored screw is the same geometry as a proud
one, just posed deeper; `SeatFrame(face, point)` turns a point on a face into the
occurrence pose. `InsertedLength` (how far the body reaches below the datum) is what
makes a stack computable.

**Preparation is a `Feature`, and that is the point.** `Place` appends a
`ComponentFeature` to a `FeatureHistory`, so placements regenerate, cache and suppress
like any other step:

- `build.Suppress(placement)` removes the component's bore from the host **and** its
  occurrence from the assembly — one switch, both halves.
- Leave the face out and the component seats on `FeatureContext.TopPlane`, re-resolved
  every regeneration: change an upstream thickness parameter and the fasteners move with
  the face they sit on, their holes re-cut through the new body.
- `ComponentAssembly.History` is public, so placements interleave with your own
  features; `new ComponentAssembly(name, history)` decorates an existing parametric
  model instead of a fixed shape.
- Depths resolve explicit-first: `ComponentSite.Depth(natural)` prefers the placement's
  `Depth` parameter, and `ComponentSite.ThroughDepth` answers "all the way through this
  body" (the host's extent below the face plus 5%, so a through tool never ends coplanar
  with the far face — which `Drill` rejects) without the component knowing the host's
  size.

`ToAssembly()` regenerates and returns an `Assembly` whose occurrence 0 is the prepared
host (also `build.Host`) followed by one occurrence per placed component. Components are
shared by reference — `HardwareComponent.ToPart()` hands back one `Part` however many
times it is placed, so N fasteners mesh once.

### The full fastener stack

`PlaceThrough` prepares **both** bodies from one call — clearance (and counterbore) in
the near body, the threaded engagement in the far one:

```csharp
var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
var mateFace  = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);

var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5));
var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10));

cover.PlaceThrough(StandardComponents.CapScrew(5, 16),
                   [new(-20, 0), new(20, 0)], coverFace, basePlate, mateFace);
// cover: Ø10 counterbore + Ø5.5 clearance through.  base: Ø4.2 tap-drill pilot,
// 13.1 deep — 11.5 of engagement plus two pitches of runout, computed not guessed.
```

The engagement is geometric: the grip is the distance from the component's seating datum
down to the anchor face, and what is left of `InsertedLength` engages the far body.
Placement points are **projected** onto the anchor face along the fastener axis, so its
2D axes need not match the seating face's; non-parallel faces, an anchor above the seat
and a component too short to reach are all rejected with the numbers in the message. The
screw is placed **once** (on the near body) — the far half carries `Assemble = false`.
To anchor into an insert rather than a tapped hole, place the insert on the far body
itself and use `Place` on the near one.

## Mechanisms (kinematics)

**A mechanism is the same mate system, driven** — the mate solver's DOF report is the
whole insight: a fully-constrained assembly is static, and a mechanism is a mate
system with DOF > 0 plus a driver consuming them. No second solver exists.

- **Joints** (`Joints.cs`): `Joint.Revolute(1)/Prismatic(1)/Cylindrical(2)/
  Spherical(3)/Planar(3)/Screw(1)/Fixed(0)` — each a NAMED combination of ordinary
  mates built from the same `MateRef`s (explicit coordinates, `BrepQueries`
  selectors, `FaceRef`/`AxisRef`, occurrence paths). Every joint's nominal DOF is
  **asserted against the solver's measured rank** (`VerifyDegreesOfFreedom`, run by
  `Mechanism.Add`) — a wrong definition fails immediately, by name. Axis joints carry
  joint coordinates: `Angle` (right-handed about A's axis, **unwrapped** through full
  turns by committed increments — the residual stays continuous within one solve
  because state moves only when a converged solve commits) and `Displacement`, both
  zero at construction; `Rebase()` re-zeroes after assembly. `WithLimits` puts hard
  stops on revolute (degrees) / prismatic (length) joints — a converged pose past a
  stop is rolled back completely and refused naming the joint.
- **The solver extension** (`Couplings.cs`): an internal `AuxiliaryConstraint`
  contributes residual rows plus ANALYTIC derivatives over evaluated mate-end world
  geometry, appended beside the mates so the rank/DOF machinery counts them like any
  rows. The screw pitch (z − z₀ = P·θ̂/2π) is one row; so are drivers and every
  higher pair. With no extras the solver is bit-identical to the plain mate solve.
- **Drivers and sweeps** (`Mechanism.cs`): a `MechanismDriver` pins one joint
  variable (angle drivers are the wrap-free pair [c − cos τ, s − sin τ]; drivers on
  variables the joint itself locks are refused); `SolveAt(t)` is the mate solve with
  it fixed; `Sweep(from, to, frames)` is the motion study — **continuation is
  load-bearing** (each step seeds from the previous converged pose, never the
  assembled one — the four-bar elbow-flip lesson), steps adapt by halving (safe
  because a failed solve writes nothing), and a sweep that cannot proceed reports the
  parameter and leaves the last good pose. `MotionFrame`s carry flattened instances
  only — poses, no geometry (the Animation input format).
- **Singularities are named** (`the same rank machinery`): a stall runs a
  zero-iteration rank probe with the threshold widened to 3% (a sweep stalls NEAR a
  dead centre, where the Jacobian is almost, not exactly, deficient), compared
  against the sweep-start probe — a dead centre names the driven joint and parameter
  and refuses to guess a branch; a merely-unreachable target says "outside reach",
  and the SAME pose driven from a different joint passes silently.
- **Rates** (`MateSolverRates.cs`): `RatesAt(driver, t, rate, accel)` solves
  J·q̇ = −∂C/∂t then J·q̈ = −r̈₀ with the second-order terms assembled
  **analytically** (composed rigid flow: centripetal terms per free chain link plus
  Coriolis cross terms between levels — never finite differences, the mate solver's
  own doctrine). Per-occurrence world velocity/acceleration/angular rates and
  per-joint coordinate rates; refused (naming the free DOF) when the driven system is
  not fully constrained. Verified against the slider-crank closed form, velocity AND
  acceleration.
- **Higher pairs** (`HigherPairs.cs`): `Coupling.Gear/Belt/Ratio` (Δθ₂ = ∓ratio·Δθ₁,
  one row, expressed on the coordinates' change since construction) and
  `Coupling.Cam(cam, follower, CamLaw)`. A `CamLaw` carries lift, slope AND curvature;
  `CamLaw.FromSketch` samples a radial profile's **exact** sketch signed distance
  (outermost crossing, bisected) into a C² periodic spline whose own calculus feeds
  the Jacobian — verified against the eccentric-circle cam's closed form.
- **Interference & swept volume** (`MotionInterference.cs`):
  `study.CheckInterference()` — instance-bounds broad phase, `MeshIntersection.Crosses`
  narrow phase (transversal only: resting contact is not a clash), ranges per pair,
  jointed pairs skipped by default (tessellated pins interpenetrate their bores),
  exact mesh-boolean volumes opt-in per confirmed range. `study.SweptVolume(path)` /
  `Shape.SweptOver(poses)` is a graph node: implicit-**Native** (child field lowered
  once, placed per pose, unioned), mesh via Surface Nets, B-Rep honestly Impossible.
- **Mobility** (`Mechanism.Mobility()`): Grübler/Kutzbach beside the measured rank,
  disagreement informative not an error — the planar four-bar in space predicts −2
  where the rank correctly measures 1 (Bennett/Sarrus are the textbook cases), and
  raw mates outside the joint vocabulary are flagged as invisible to the formula.

Docs: `docs/examples/mechanisms.md`. Deliberately out of scope: forces, masses,
friction, contact dynamics — mechanisms answer "where does it go".

## Debug modifiers & the validation report

Part-level debug flags (the OpenSCAD `%`/`*`/`!` analog): `Part.Ghost` (rendered
translucent via `Part.EffectiveDisplayMode` — the property every render path reads —
but excluded from geometry exports), `Part.Hidden` (neither rendered nor exported),
`Part.Isolated` (when any part in scope is isolated, only isolated parts
show/export). The rules live in ONE place, `DebugFilter`
(`IsShown`/`IsExported`/`Shown`/`Exported`), shared by the window, offscreen
renders, `--export`, and the MCP tools — with no flags set every filter is the
identity, so nothing changes until you ask. `SceneReport.Create(scene)` is the
`assert`/`echo` analog: per part — kind, face count, watertightness (open meshes
flagged with their boundary-loop count), volume (closed meshes only), surface area,
world bounds — plus notes for meshing failures (named, not thrown) and active debug
flags; `ToText()` is the aligned table the viewer's **Check** button shows, and
`AllClean` is the one-line assertion for scripts.

## 2D interchange (DXF & SVG)

`DxfDocument` reads and writes 2D profiles (LINE / ARC / CIRCLE / LWPOLYLINE with
layers): `Add(sketch, layer)` writes lines and arcs **exactly** (LWPOLYLINE bulge =
tan(sweep/4) is an exact arc encoding; full-circle loops become CIRCLE; cubic béziers
flatten at a stated chord tolerance — the one lossy mapping), and `ToSketches(out
diagnostics)` comes back: closed polylines and circles directly, loose LINE/ARC
entities chained end-to-end at the weld tier, anything unclosable *reported*, never
invented (the `MeshReadResult` convention). Loop nesting is deliberately the caller's
decision on import. `SvgDrawing` writes drawings from `Shape.Section`/`Silhouette`
regions and exact sketches (SVG `A`/`C` commands — nothing flattened), with
**line-class-driven styling** (`SvgLineClass.Visible`/`Hidden`/`Section` → solid /
dashed / dash-dot groups per layer, the build123d edge-classification lesson);
model space is y-up mm, flipped once at the root, 1 user unit = 1 mm. Docs:
`docs/examples/dxf-svg.md`.

## Quality

Bridges and mesh output honor `MeshQuality` (`SegmentsPerCircle`, `CurveSamples` for
tessellation, `SdfResolution` for polygonization); `Scene.Options` carries the same
knobs for everything shown through a scene. Hosts (e.g. the viewer's
`EngrCad.Configure()` builder) can supply a *default* quality without overriding a
scene that chose its own: `Scene.HasExplicitOptions` records whether options were
passed at construction, and `Scene.ResolveQuality(fallback)` /
`Scene.PreMesh(fallback)` implement the precedence **explicit scene options >
host fallback > `MeshQuality` defaults**.

**Adaptive tessellation** (`TessellationQuality`, opt-in via
`MeshQuality.Tessellation`): instead of a fixed count, state a criterion — max angle
per segment (OpenSCAD `$fa`) and/or max chord deviation (OCCT linear deflection),
clamped to `[MinSegments, MaxSegments]`. `SegmentsFor(radius)` is THE criterion;
`ResolveFor(solid)` scans the solid's curvature radii (circular/elliptic edges,
cylinder/sphere/revolved/extruded/swept surfaces) and resolves one count pair sized by
the largest radius (the chord criterion binds there: n ≈ π·√(r/2d)). The load-bearing
property: **`Part.GetMesh` and `Part.GetFeatureEdges` resolve through the same
criterion**, so the exact edge overlay can no longer detach from the faceted fill on
large rims (with fixed counts the overlay is deliberately finer at ≥ 96
segments/circle, which is where the detachment came from). Null `Tessellation` keeps
the fixed counts bit-for-bit; the SDF route's `SdfResolution` is deliberately
untouched (a volumetric grid is not a per-radius quantity).

## Future work (todo.md)

Sketch constraint solver (see todo.md), mesh→B-Rep import
(unlock blends → B-Rep), fillets on `Shape` with edge selectors, ellipsoid surfaces for
non-uniformly scaled spheres. For text: text on a curve, variable fonts (`fvar`/`gvar`/
`CFF2`), and B-Rep booleans for sketch-extrusion tools (which
would make engraving B-Rep-native). For standard components: more families (button and
countersunk heads, nuts, washers, bearings), higher body fidelity (hex sockets, modeled
threads on the shank, knurled inserts), and stacks that anchor into a placed component
(an insert) rather than the screw's own tapped pilot.
