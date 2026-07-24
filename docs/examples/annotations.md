# 3D annotations (PMI)

Parts can carry their own manufacturing information — **dimensions, notes, and datum
labels attached to model geometry in 3D space** (model-based definition, instead of
2D drawings). Annotations live on the `Part`, resolve against its geometry, and are
drawn by the viewer with classic dimension graphics: extension lines, arrowheads, and
billboarded screen-constant text. They render in headless/docs output too — the
image below is produced by the same offscreen renderer that made every other picture
on this site.

## Dimensions that measure the model

The important annotations are **selector-based**: instead of storing a number, a
dimension stores a *semantic query* (the `BrepQueries` vocabulary the chamfer/fillet
selectors use) and measures the actual geometry every time it resolves. Change a
parameter, regenerate, and the dimension re-measures — the same topological-naming
story as parametric features.

```csharp render:annotations
var plate = Shape.Box(40, 20, 5)
    .Drill(StandardHoles.Clearance(5), [new(-12, 0), new(12, 0)], depth: 6,
        SketchPlane.At((0, 0, 2.5), Vector3d.UnitX, Vector3d.UnitY));

var part = new Part("plate", plate);

// Auto-measured width: the distance between the two X faces (selector re-runs
// per resolution, so it tracks parameter edits).
part.Annotate(LinearDimension.BetweenFaces(
    s => s.PlanarFacesWithNormal(-Vector3d.UnitX).First(),
    s => s.PlanarFacesWithNormal(Vector3d.UnitX).First()));

// Thickness, pulled to the front with an explicit placement offset.
var thickness = LinearDimension.BetweenFaces(
    s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First(),
    s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First());
thickness.Offset = new Vector3d(0, -16, 0);
part.Annotate(thickness);

// The bore diameter, read from the actual circular edge: "⌀5.5".
part.Annotate(RadialDimension.OnEdge(
    s => s.Faces.SelectMany(f => f.Edges()).Distinct()
        .First(e => e.IsCircular(out var c, out _, out _) && c.X > 0 && c.Z > 2),
    diameter: true));

// A hole callout generated from the same spec that drilled the holes,
// and a datum label.
part.Annotate(HoleCallout.From(StandardHoles.Clearance(5), (-12, -2.75, 2.5), depth: 6));
part.Annotate(new DatumLabel((-20, 8, 2.5), "A"));

var scene = new Scene();
scene.Add(part);
```

![A dimensioned plate: width and thickness dimensions, bore diameter, hole callout, datum label](images/annotations.png)

## The pieces

- **`LinearDimension`** — between two *parallel planar* faces
  (`LinearDimension.BetweenFaces(selectorA, selectorB)`, auto-measured
  plane-to-plane), or between two fixed points (`new LinearDimension(a, b)` — what
  the viewer's interactive **Measure** tool creates from two clicks).
- **`RadialDimension.OnEdge(selector, diameter: …)`** — reads the actual radius of
  an `IsCircular` edge; text `R5` or `⌀10`.
- **`LeaderNote`** / **`DatumLabel`** — free text / a boxed datum letter with a
  leader to a part-local anchor point.
- **Callout generators** — `HoleCallout.From(holeSpec, anchor, depth)` and
  `ThreadCallout.From(threadSpec, anchor, depth?)` produce standard callout text
  ("⌀5.5 ↧6", "M6×1 ↧12") as leader notes, straight from the specs used to cut the
  geometry.
- `Label` overrides the formatted text ("10 REF"); `Offset` places the dimension
  line or leader (leave it zero for a screen-space default).

In the viewer, annotations are on by default whenever a scene carries any (the
**Annot** toolbar toggle hides them), always drawn on top of the model, and posed by
the instance transform — assembly instances show their part's annotations in place.
The **Measure** toggle turns clicks into surface-point picks: two picks create a
transient point-to-point dimension, Escape clears it.
