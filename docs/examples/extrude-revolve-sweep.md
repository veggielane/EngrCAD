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

### Twist and taper

`Shape.Extrude(sketch, height, twist, scale, plane?, slices?)` is OpenSCAD's
`linear_extrude(twist, scale, slices)`: the cross-section at height fraction `t` is
the sketch scaled by `lerp(1, scale, t)` (per axis, about the plane origin) and
rotated by `twist·t` about the plane normal — radians, counter-clockwise (OpenSCAD's
`twist` is the opposite sign). A call with zero twist and unit scale is exactly the
plain extrusion:

```csharp render:extrude-twist
var star = Sketch.Polygon(Enumerable.Range(0, 10)
    .Select(i =>
    {
        double a = Math.PI / 2 + i * Math.PI / 5;
        double r = i % 2 == 0 ? 14.0 : 6.5;
        return new Vector2d(r * Math.Cos(a), r * Math.Sin(a));
    }).ToList());

var scene = new Scene();
scene.Add(new Part("twisted star", Shape.Extrude(star, 40, twist: Math.PI / 2), Palette.Brass));
scene.Add(new Part("tapered boss",
    Shape.Extrude(Sketch.Rectangle(24, 24), 30, twist: 0, scale: 0.4).Translate(40, 0, 0),
    Palette.Steel));
```

![A star profile twisted through a quarter turn beside a tapered square boss](images/extrude-twist.png)

Representation support is honest about what each case is:

- A **pure taper** (`twist: 0`) is **B-Rep-Native** — every straight side sweeps an
  exact plane through the scaling centre, so the solid is a ruled loft between the
  base and the scaled top (a tapered sketch *with holes* is B-Rep-Impossible until
  loft sections support holes; cut the hole after the taper, or use mesh/implicit).
- A **nonzero twist** has no analytic side surface, so it is B-Rep-Impossible: the
  mesh lowering sweeps section rings directly (`slices` controls the ring count;
  omitted, it derives from the twist and the mesh quality), and the implicit lowering
  wraps that mesh in a mesh SDF. `Explain(target)` reports each case.

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
