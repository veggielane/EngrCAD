# 2D sketch booleans

[Sketches](sketching.md) can be combined in 2D before they ever become a solid.
`Union`, `Intersect`, and `Subtract` turn sketches into **regions** — polygon-with-holes
areas — and the region model works out the nesting for you: a cut that *creates* a hole
produces a hole loop, and loose inner loops are recognised as holes without a single
`WithHole` call.

## Cutting a pocket: the hole is discovered, not declared

```csharp render:sketch-booleans
// A plate and a pocket, drawn as two independent sketches.
var plate  = Sketch.RoundedRectangle(60, 40, 6);
var pocket = Sketch.Slot(30, 14);

// One 2D boolean. The cut CREATES a hole loop neither sketch carried, and the
// region model detects the nesting itself - no WithHole anywhere.
var region = plate.Subtract(pocket)[0];

var (outer, holes) = Profile.FromRegion(region, SketchPlane.XY.Frame);
var scene = new Scene();
scene.Add(new Part("plate", Shape.Extrude(outer, Vector3d.UnitZ * 8, holes)));
```

![A rounded plate with a slot-shaped pocket cut clean through it](images/sketch-booleans.png)

The result is a genus-1 solid: one region, one hole, area 1991.16 against an analytic
1991.16. `Profile.FromRegion` hands back exactly the `(outer, holes)` pair the solid
factories take, so regions are a first-class front door into `Extrude`, `Revolve`, and
`Sweep`.

## Nesting detection

Give the region model a bag of loops and it sorts out which are outers and which are
holes by containment depth — even depth is material, odd depth is a hole, and an island
inside a hole becomes its own region again:

```csharp run:sketch-region-nesting
var outline = Sketch.Rectangle(60, 30);
var boltCircle = Enumerable.Range(0, 4)
    .Select(i => Sketch.Circle(new Vector2d(-21 + 14 * i, 0), 3));

// Four loose circles and an outline: the nesting is DETECTED, not declared.
var nested = Sketch.ToRegions([outline, .. boltCircle]);
if (nested.Count != 1 || nested[0].Holes.Count != 4)
    throw new Exception("expected one region with four holes");
```

## Fidelity: regions are polygonal

A region is a polygon-with-holes, so arcs and béziers are **flattened** to polylines at
a chord tolerance (1e-3 by default) when a sketch enters a boolean. Everything built
from that region inherits the flattening — which is why the bolt-circle areas above come
out a whisker small (inscribed polygons).

A sketch handed **straight** to `Shape.Extrude`/`Revolve`/`Sweep` is untouched: it keeps
its exact arcs and rational NURBS for B-Rep, and its exact 2D signed distance for the
implicit engine. Reach for 2D booleans when you need the region algebra; skip them when
you want exact curves. Exact curved 2D booleans (arcs kept as arcs through the
arrangement) are future work.

Under the hood the booleans run on an exact 2D arrangement: every orientation and
containment decision goes through adaptive-exact predicates, so coincident edges,
touching corners, and vertices lying exactly on other edges are decided correctly
rather than by epsilon.
