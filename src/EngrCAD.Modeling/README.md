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
| `Extrude(Sketch)` | ✅ native | ✅ **native** (exact 2D SDF) | ✅ native |
| `Revolve(Sketch)` full turn | ✅ native (axis-touching OK: on-axis stretches become poles) | ✅ **native** (exact 2D SDF) | ✅ native |
| `Extrude` (profile, holes, shear) | ✅ native | 🔶 bridged (tessellation → mesh SDF) | ✅ native |
| `Revolve` (partial/full, holes) | ✅ native (rigid) · ❌ sheared | 🔶 bridged | ✅ / 🔶 |
| `Sweep` (RMF path, holes) | ✅ native (rigid) · ❌ sheared | 🔶 bridged | ✅ / 🔶 |
| `Union` / `Intersect` / `Subtract` | ✅ native (`BrepBoolean`) | ✅ native | ✅ (from B-Rep, else `MeshBoolean`) |
| `SmoothUnion` / `SmoothIntersect` / `SmoothSubtract` | ❌ no B-Rep form | ✅ native | 🔶 polygonized |
| `Offset` / `Shell` | ❌ no B-Rep form | ✅ native | 🔶 polygonized |
| `Lattice` (gyroid & co.) | ❌ no B-Rep form | ✅ native | 🔶 polygonized |
| `Chamfer` (planar-face rims) | ✅ native (miters; cone bands on circles) | 🔶 bridged | ✅ native |
| `Fillet` (G1 planar-face rims) | ✅ native (cylinder/torus bands) | 🔶 bridged | ✅ native |
| `PatternLinear` / `PatternCircular` | ✅ native (multi-shell when disjoint) | ✅ native | ✅ native |
| `Hull(...)` (convex hull) | ❌ mesh construction, no B-Rep import | 🔶 bridged (hull mesh → mesh SDF) | 🔶 quickhull over tessellated operand vertices (exact for polyhedral operands) |
| `Translate` / `Rotate` / `Scale` (uniform) | ✅ baked into inputs | ✅ native SDF ops | ✅ |
| `Mirror(point, normal)` | ✅ box/cylinder/extrude (any affine) + sphere/torus/cone (mirrored similarity) · ❌ revolve/sweep/rim/drill (no mirrored lowering yet) | ✅ native (query point reflected — exact) | ✅ (winding flipped; exact reflection of the tessellation) |
| General affine (shear, non-uniform scale) | ✅ box/cylinder/extrude · ❌ others | 🔶 bridged | ✅ / 🔶 |
| `ExternalThread` (no chamfer, no clearance) | ✅ **native** (boolean-free helical sweep, rigid + uniform scale; not STEP-exportable) | ✅ native (exact-sign thread SDF) | ✅ native (B-Rep tessellation) |
| `ThreadedHole` (no clearance) | ✅ **native** (pilot + thread as ONE clipped-profile helical tool; spiral-arc chains split the drilled faces) | ✅ native | ✅ native (B-Rep tessellation) |
| `ExternalThread` (chamfers) / either with clearance | ❌ chamfer cones / distance-field profile offsets — reported per cause | ✅ native (exact-sign thread SDF) | 🔶 polygonized |
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
still need axis clearance). A 2D constraint solver is future work (todo.md).

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
as JSON so a design is re-tunable without recompiling. Between-feature geometry
references are **selector-based** (`FeatureContext.Lowered` + `BrepQueries`) — semantic
queries that survive regeneration; persistent topological IDs are future work.
Standard features (`ExtrudeSketchFeature`, `HoleFeature`, `FilletRimFeature`, patterns,
`BooleanFeature`) cover simple histories; `Feature.FromFunc` handles one-offs.

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

## Patterns

`shape.PatternLinear(count, step)` and
`shape.PatternCircular(count, axisOrigin, axisDir)` union transformed copies (balanced
tree, all representations). Disjoint results become valid multi-shell solids; a
Difference tool swallowed whole becomes a cavity shell. For hole arrays, keep passing
point lists to `Drill` — that stays the cheaper idiom.

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
against the current Tappex datasheet before production use). Blind holes get flat
bottoms; drill-tip angles are future work.

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
allowed. `ExternalThread` is a threaded rod along +Z (45° lead-in chamfers to the minor
diameter on both ends by default); `ThreadedHole` cuts a tap-drill pilot (via `Drill`,
truncating the internal crests as tapping does) plus a modeled thread void per point.

**Clearance** is the printing fit allowance, applied normal to the flanks (the profile
offsets perpendicular to its own boundary): the external thread *shrinks*, the internal
void *grows*; default 0, typical FDM 0.1–0.25 mm, capped at half the thread depth.

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

`Part.GetMesh(quality)` produces the display mesh on first use and caches it (Shapes
via their best route, B-Reps tessellated, SDFs polygonized, meshes as-is);
`Scene.PreMesh()` does this for every **distinct** part up front so viewers never
tessellate on the render thread. Part names are unique per tab. `Part` is
deliberately a leaf — tabs and assemblies are the containers.

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
- v1 is placement only: **mates/constraints, exploded views, and BOM are future
  work** (mates would solve for the occurrence frames that `Flatten` composes).

## Quality

Bridges and mesh output honor `MeshQuality` (`SegmentsPerCircle`, `CurveSamples` for
tessellation, `SdfResolution` for polygonization); `Scene.Options` carries the same
knobs for everything shown through a scene.

## Future work (todo.md)

Sketch constraint solver (see todo.md), mesh→B-Rep import
(unlock blends → B-Rep), fillets on `Shape` with edge selectors, ellipsoid surfaces for
non-uniformly scaled spheres.
