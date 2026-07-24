# Transforms & patterns

## Transforms

`Translate`, `RotateX/Y/Z`, `Rotate(axis, angle)`, `Scale`, and the general
`Transform(Matrix4d)` are ordinary graph nodes. Lowerings **bake accumulated
transforms into construction inputs** — a rotated-then-drilled B-Rep stays exact
because the transform moves the profiles and axes, never finished geometry:

```csharp render:transforms
var blank = Shape.Box(30, 14, 6);

var scene = new Scene();
scene.Add(new Part("original", blank, Palette.Steel,
    Matrix4d.CreateTranslation((-45, 0, 0))));
scene.Add(new Part("rotated", blank.RotateZ(Math.PI / 6).RotateY(Math.PI / 8),
    Palette.Brass));
scene.Add(new Part("scaled ×1.4", blank.Scale(1.4), Palette.Coral,
    Matrix4d.CreateTranslation((50, 0, 0))));
```

![A blank, a rotated copy, and a uniformly scaled copy](images/transforms.png)

Uniform scaling keeps everything exact (feature sizes scale with it); shear and
non-uniform scale are exact for boxes, cylinders, and extrusions, and bridge or
reject elsewhere — `Explain` names the offending node
([details](representations.md)).

## Linear patterns

`PatternLinear(count, step)` unions transformed copies (as a balanced tree, in every
representation). Disjoint copies become a valid multi-shell solid:

```csharp render:pattern-linear
var post = Shape.Cylinder(3, 20).Translate(0, 0, 10)
    .SmoothUnion(Shape.Sphere(4.5).Translate(0, 0, 22), 2);

var scene = new Scene();
scene.Add(new Part("fence", post.PatternLinear(6, step: (14, 0, 0)), Palette.Teal));
```

![Six blended posts in a linear pattern](images/pattern-linear.png)

## Circular patterns

`PatternCircular(count, axisOrigin, axisDirection, angleStep?)` revolves copies about
an axis — the classic bolt-circle / carousel layout:

```csharp render:pattern-circular
var carousel = Shape.Cylinder(20, 4)
    | Shape.Cylinder(3, 14).Translate(15, 0, 9)
        .PatternCircular(8, Vector3d.Zero, Vector3d.UnitZ);

var scene = new Scene();
scene.Add(new Part("carousel", carousel, Palette.Brass));
```

![Eight posts patterned around a disc](images/pattern-circular.png)

For arrays of *holes*, keep passing point lists to [`Drill`](holes.md) — one boolean
with many tools is cheaper than patterning a drilled body.
