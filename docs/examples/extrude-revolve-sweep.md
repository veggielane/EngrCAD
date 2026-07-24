# Extrude, revolve, sweep

The three sketch-consuming operations turn 2D profiles into solids. All are native in
every representation (see [the support matrix](representations.md)).

## Extrude

`Shape.Extrude(sketch, height, plane?)` extrudes along the plane normal (default:
world XY, so height is +Z):

```csharp render:extrude
var bracket = Sketch.Start(0, 0)
    .LineTo(40, 0).LineTo(40, 12).LineTo(12, 12).LineTo(12, 36).LineTo(0, 36)
    .Close();

var scene = new Scene();
scene.Add(new Part("L-bracket", Shape.Extrude(bracket, 16), Palette.Copper));
```

![An L-profile sketch extruded into a bracket](images/extrude.png)

## Revolve

`Shape.Revolve(sketch, angle?, plane?)` spins the sketch about its plane's y axis;
sketch x is the radial direction (must be ≥ 0). The default plane (XZ) puts the axis
on world Z. Sketches may **touch the axis** on full turns — on-axis stretches revolve
to nothing and their endpoints become poles, which is how vases, domes, and spheres
close up in all three representations:

```csharp render:revolve-vase
var vaseProfile = Sketch.Start(0, 0)
    .LineTo(10, 0)                                    // base disc (touches the axis)
    .BezierTo(new(17, 9), new(3.5, 17), new(9, 26))   // curved wall
    .LineTo(0, 26)                                    // rim back to the axis
    .Close();

var scene = new Scene();
scene.Add(new Part("vase", Shape.Revolve(vaseProfile), Palette.Teal));
```

![A vase revolved from an axis-touching profile](images/revolve-vase.png)

Partial revolves (`angle < 2π`) need axis clearance and are rejected at construction
when the sketch touches the axis:

```csharp render:revolve-partial
var section = Sketch.Start(8, 0)
    .LineTo(20, 0).LineTo(20, 6).LineTo(14, 9).LineTo(14, 14).LineTo(20, 17)
    .LineTo(20, 23).LineTo(8, 23)
    .Close();

var scene = new Scene();
scene.Add(new Part("pulley section", Shape.Revolve(section, angle: 1.5 * Math.PI),
    Palette.Steel));
```

![A grooved pulley revolved three quarters of a turn](images/revolve-partial.png)

## Sweep

`Shape.Sweep(sketch, path, plane?)` sweeps the profile along a 3D curve using
rotation-minimizing frames. The sketch plane must sit at the path start,
perpendicular to its tangent:

```csharp render:sweep
// A quadratic NURBS path rising along +Z then bending toward +Y;
// it starts at the origin with tangent +Z, matching the default XY sketch plane.
var path = new NurbsCurve(2,
    [(0, 0, 0), (0, 0, 26), (0, 22, 44)], null,
    [0, 0, 0, 1, 1, 1]);

var scene = new Scene();
scene.Add(new Part("swept tube", Shape.Sweep(Sketch.Circle(5), path), Palette.Sage));
```

![A circular profile swept along a curved path](images/sweep.png)

Profile-based overloads (`Shape.Extrude(Profile, direction, holes?)`,
`Shape.Revolve(Profile, axisOrigin, axisDirection, angle?, holes?)`,
`Shape.Sweep(Profile, path, holes?)`) expose the lower-level B-Rep factory directly —
including sheared extrusions and pipe elbows from closed profiles.
