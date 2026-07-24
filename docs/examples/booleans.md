# Booleans

Union, intersection, and difference compose shapes in every representation — exact
`BrepBoolean` topology surgery in B-Rep, min/max in the implicit engine, and mesh
booleans as the fallback. The operators `|`, `&`, and `-` are shorthand for
`Union`, `Intersect`, and `Subtract`:

```csharp render:booleans
var box = Shape.Box(24, 24, 24);
var ball = Shape.Sphere(15).Translate(0, 0, 12);

var scene = new Scene();
scene.Add(new Part("union", box | ball, Palette.Steel,
    Matrix4d.CreateTranslation((-45, 0, 0))));
scene.Add(new Part("intersection", box & ball, Palette.Brass));
scene.Add(new Part("difference", box - ball, Palette.Coral,
    Matrix4d.CreateTranslation((45, 0, 0))));
```

![Union, intersection, and difference of a box and a sphere](images/booleans.png)

A classic machined part is just a chain of differences:

```csharp render:booleans-machined
var block = Shape.Box(60, 36, 16)
    - Shape.Cylinder(6, 30).Translate(-18, 0, 0)     // through bore
    - Shape.Cylinder(6, 30).Translate(18, 0, 0)
    - Shape.Box(24, 36.2, 10).Translate(0, 0, 8);    // top channel

var scene = new Scene();
scene.Add(new Part("machined block", block, Palette.Sky));
```

![A block with two bores and a milled channel](images/booleans-machined.png)

In the B-Rep lowering the result is **topologically sealed** — it passes `Validate()`
and Euler–Poincaré with the correct genus, so downstream operations (tessellation,
STEP export, further booleans) get a watertight solid. For soft, blended joins use
[smooth booleans](implicit.md) instead.
