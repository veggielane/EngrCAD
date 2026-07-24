# Sketching

2D sketches are the profile language for [extrude, revolve, and sweep](extrude-revolve-sweep.md).
Draw them with the fluent builder — `LineTo`, `ArcTo`, `ArcThrough`, `BezierTo`,
`QuadraticTo` — or start from a primitive, and punch holes with `WithHole`.

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

## Placing sketches in 3D

Sketches are pure 2D. The modeling operations place them with a `SketchPlane` —
`SketchPlane.XY` / `XZ` / `YZ` presets, or `SketchPlane.At(origin, xAxis, yAxis)` for
arbitrary placement (used by [`Drill`](holes.md) to pick the face being drilled).
Extrusion runs along the plane normal; revolution spins around the plane's y axis.
