# Sketching

2D sketches are the profile language for [extrude, revolve, and sweep](extrude-revolve-sweep.md).
Draw them with the fluent builder — `LineTo`, `ArcTo`, `ArcThrough`, `EllipticalArcTo`,
`BezierTo`, `QuadraticTo` — or start from a primitive, and punch holes with `WithHole`.

## The fluent builder

```csharp render:sketch-plate
var plate = Sketch.Start(-20, -12)
    .LineTo(20, -12)
    .ArcTo(new(20, 12), radius: 14, clockwise: false)   // bulged arc on the right
    .LineTo(-20, 12)
    .BezierTo(new(-34, 7), new(-34, -7), new(-20, -12)) // sculpted bézier on the left
    .Close()
    .WithHole(Sketch.Circle(new(11, 0), 4.5))
    .WithHole(Sketch.Circle(new(-13, 0), 3));

var scene = new Scene();
scene.Add(new Part("sketched plate", Shape.Extrude(plate, 5), Palette.Steel));
```

![A plate extruded from a sketch with an arc, a bézier edge, and two holes](images/sketch-plate.png)

Lines and arcs lower to **exact rational NURBS** profiles for B-Rep, and every sketch
knows its **exact 2D signed distance** (`sketch.ToRegion()`), which makes sketch
extrusions and full revolutions *native* in the implicit engine too — no mesh bridge.
`sketch.Area()` is exact (arc terms analytic, béziers by Gauss quadrature).

## Sketch primitives

`Rectangle`, `RoundedRectangle`, `Circle`, `Polygon`, and `Slot` cover the common
profiles without builder ceremony:

```csharp render:sketch-primitives
var scene = new Scene();
double x = -48;
foreach (var (name, sketch) in new (string, Sketch)[]
{
    ("rectangle", Sketch.Rectangle(18, 12)),
    ("rounded",   Sketch.RoundedRectangle(18, 12, 3)),
    ("circle",    Sketch.Circle(7.5)),
    ("polygon",   Sketch.Polygon([new(-9, -7), new(9, -7), new(0, 9)])),
    ("slot",      Sketch.Slot(length: 18, width: 8)),
})
{
    scene.Add(new Part(name, Shape.Extrude(sketch, 4), Palette.Sky,
        Matrix4d.CreateTranslation((x, 0, 0))));
    x += 24;
}
```

![The five sketch primitives extruded side by side](images/sketch-primitives.png)

## Elliptical arcs

`Sketch.Ellipse` and the builder's `EllipticalArcTo` carry an ellipse **exactly** — the
segment stores the centre and both semi-axis *vectors*, so a rotated ellipse needs no
third parameter and there is no flattened polyline anywhere in the chain. That makes an
elliptical profile native in all three representations, exactly like a circular one:
`πab` from `Area()`, an exact 2D distance field, and an `Ellipse3d` in the B-Rep.

`EllipticalArcTo` is SVG's `A rx ry rotation largeArc sweep` command with the same two
flags and the same meaning, including SVG's out-of-range rule — semi-axes too small to
span the chord are scaled up by the common factor that just reaches, so the ellipse's
**aspect and rotation survive** and you never have to solve for the minimum ellipse
before drawing.

```csharp render:sketch-ellipse
// A full ellipse, a rotated one, and a slotted plate whose ends are elliptical arcs.
var oval = Sketch.Ellipse(semiX: 11, semiY: 6);
var tilted = Sketch.Ellipse(default, semiX: 11, semiY: 6, rotationDegrees: 35);

var plate = Sketch.Start(-9, -6)
    .LineTo(9, -6)
    .EllipticalArcTo(new(9, 6), semiX: 5, semiY: 6)     // right cap: a half-ellipse
    .LineTo(-9, 6)
    .EllipticalArcTo(new(-9, -6), semiX: 5, semiY: 6)   // left cap
    .Close()
    .WithHole(Sketch.Ellipse(default, 6, 2.5, rotationDegrees: -20));

var scene = new Scene();
scene.Add(new Part("ellipse", Shape.Extrude(oval, 4), Palette.Steel,
    Matrix4d.CreateTranslation((-30, 0, 0))));
scene.Add(new Part("rotated", Shape.Extrude(tilted, 4), Palette.Sky));
scene.Add(new Part("elliptical ends", Shape.Extrude(plate, 4), Palette.Brass,
    Matrix4d.CreateTranslation((32, 0, 0))));
```

![A plain ellipse, a rotated ellipse, and a plate with elliptical end caps and a tilted elliptical hole](images/sketch-ellipse.png)

One consequence worth knowing: an ellipse with equal semi-axes is *geometrically* a
circle but stays an `Ellipse2d`, so `BrepQueries.IsCircular` and cylinder promotion will
not claim it. Use `Sketch.Circle` when the shape really is a circle.

Everything else in the sketch vocabulary works on an elliptical arc: it round-trips
through `ToCurves`/`FromCurves` (so it survives feature persistence), exports as an
exact SVG `A` command, and moves with its chord under the constraint solver. What it
does **not** yet have is its own constraint variables — like a bézier, only its endpoint
joints can be constrained, and a tangency to an elliptical arc is not in the vocabulary.

## Sketch constraints

Draw roughly, constrain, solve exact. `sketch.Constrain()` opens the variational
constraint layer: coincident, horizontal/vertical, parallel/perpendicular, tangent,
equal, concentric and fix constraints plus distance/angle/radius/diameter dimensions,
solved by Levenberg–Marquardt from the sketch **as drawn** — the drawing is the seed
and the branch selector (a tangent arc stays on the side it was drawn on). The result
is an ordinary `Sketch` that extrudes, revolves and sweeps like any other.

Below, a rounded rectangle drawn by hand — nothing square, radii disagreeing — next to
the same sketch fully constrained to a 30 × 20 plate with R2 fillets (zero degrees of
freedom remain):

```csharp render:sketch-constraints
// Drawn by hand: nothing is square, the corners are off, the radii disagree.
var drawn = Sketch.Start(12.8, -10.3)
    .ArcTo(new(15.3, -8.1), 2.3, clockwise: false)
    .LineTo(15.1, 7.6)
    .ArcTo(new(12.9, 10.2), 1.8, clockwise: false)
    .LineTo(-12.7, 10.4)
    .ArcTo(new(-15.2, 8.2), 2.1, clockwise: false)
    .LineTo(-14.8, -7.7)
    .ArcTo(new(-13.1, -9.8), 2.0, clockwise: false)
    .Close();

var cs = drawn.Constrain();
cs.Vertical(cs.Line(1)).Horizontal(cs.Line(3)).Vertical(cs.Line(5)).Horizontal(cs.Line(7))
  .Tangent(cs.Line(7), cs.Arc(0)).Tangent(cs.Line(1), cs.Arc(0))
  .Tangent(cs.Line(1), cs.Arc(2)).Tangent(cs.Line(3), cs.Arc(2))
  .Tangent(cs.Line(3), cs.Arc(4)).Tangent(cs.Line(5), cs.Arc(4))
  .Tangent(cs.Line(5), cs.Arc(6)).Tangent(cs.Line(7), cs.Arc(6))
  .EqualRadius(cs.Arc(0), cs.Arc(2))
  .EqualRadius(cs.Arc(0), cs.Arc(4))
  .EqualRadius(cs.Arc(0), cs.Arc(6))
  .Radius(cs.Arc(0), 2)
  .Distance(cs.Point(1), cs.Point(2), 16)   // right edge = height − 2r
  .Distance(cs.Point(3), cs.Point(4), 26)   // top edge = width − 2r
  .Fix(cs.Point(0));

var result = cs.Solve();                    // throws if the constraints contradict
var solved = result.Sketch!;                // IsFullyConstrained: 0 DOF remain

var scene = new Scene();
scene.Add(new Part("drawn", Shape.Extrude(drawn, 4), Palette.Steel));
scene.Add(new Part("solved", Shape.Extrude(solved, 4), Palette.Sky,
    Matrix4d.CreateTranslation((36, 0, 0))));
```

![The sloppy drawn profile beside the solved exact 30 × 20 rounded plate](images/sketch-constraints.png)

The solve report is honest about freedom: under-constrained is *normal* (the
Levenberg–Marquardt step lies in the Jacobian's row space, so geometry no constraint
mentions keeps its drawn proportions, and the remaining degree-of-freedom count is
always reported — rank-revealing, so redundant constraints are not miscounted). A
consistent extra dimension converges and reports the redundancy; a contradictory one
fails **naming the constraints that cannot drop**, and a failed solve produces
nothing — the drawn sketch is never modified:

```
FAILED in 6 iterations; worst residual 1; 1 of 8 DOF constrained (7 free); 1 redundant row
  · 'Distance(outer point 0, outer point 1) = 10' is off by 1
  · 'Distance(outer point 0, outer point 1) = 12' is off by 1
  · no solved sketch was produced — the drawn sketch is unchanged
```

Entities address the sketch's normalized segment order, holes included — a washer is
`cs.Concentric(cs.Arc(0), cs.HoleArc(0, 0))` plus two diameters. Full circles carry
center + radius only (constrain them via `CenterOf`); bézier and elliptical-arc segments
ride along with their endpoints in v1.

## Placing sketches in 3D

Sketches are pure 2D. The modeling operations place them with a `SketchPlane` —
`SketchPlane.XY` / `XZ` / `YZ` presets, or `SketchPlane.At(origin, xAxis, yAxis)` for
arbitrary placement (used by [`Drill`](holes.md) to pick the face being drilled).
Extrusion runs along the plane normal; revolution spins around the plane's y axis.
