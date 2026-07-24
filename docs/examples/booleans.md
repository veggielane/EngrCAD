# Booleans

Union, intersection, and difference compose shapes in every representation — exact
`BrepBoolean` topology surgery in B-Rep, min/max in the implicit engine, and mesh
booleans as the fallback. The operators `|`, `&`, and `-` are shorthand for
`Union`, `Intersect`, and `Subtract`:

```csharp render:booleans
var block = Shape.Box(24, 24, 12);
var post = Shape.Cylinder(radius: 7, height: 28).Translate(4, 4, 0);

var scene = new Scene();
scene.Add(new Part("union", block | post, Palette.Steel,
    Matrix4d.CreateTranslation((-36, 0, 14))));
scene.Add(new Part("intersection", block & post, Palette.Brass,
    Matrix4d.CreateTranslation((0, 0, 14))));
scene.Add(new Part("difference", block - post, Palette.Coral,
    Matrix4d.CreateTranslation((36, 0, 14))));
```

![Union, intersection, and difference of a block and a cylinder](images/booleans.png)

A classic machined part is just a chain of differences:

```csharp render:booleans-machined
var block = Shape.Box(60, 36, 16)
    - Shape.Cylinder(6, 30).Translate(-19, 0, 0)     // through bore
    - Shape.Cylinder(6, 30).Translate(19, 0, 0)
    - Shape.Box(20, 40, 12).Translate(0, 0, 6);      // milled top channel

var scene = new Scene();
scene.Add(new Part("machined block", block, Palette.Sky,
    Matrix4d.CreateTranslation((0, 0, 8))));
```

![A block with two bores and a milled channel](images/booleans-machined.png)

In the B-Rep lowering the result is **topologically sealed** — it passes `Validate()`
and Euler–Poincaré with the correct genus, so downstream operations (tessellation,
STEP export, further booleans) get a watertight solid. For soft, blended joins use
[smooth booleans](implicit.md) instead.
