---
title: "Smooth blends"
---

`SmoothUnion` / `SmoothIntersect` / `SmoothSubtract` take a blend distance that
rounds the junction — the organic fillet the hard [booleans](booleans.md) can't give.
They come from the implicit engine (signed distance fields) and have no exact B-Rep
form: `ToBrep()` rejects them with a clear report, while `ToMesh()` polygonizes the
field with Surface Nets. They still compose freely with the rest of the `Shape`
vocabulary.

```csharp render:smooth-blend
var stem = Shape.Cylinder(5, 30);
var bulb = Shape.Sphere(11).Translate(0, 0, 18);

var scene = new Scene();
scene.Add(new Part("hard union", stem | bulb, Palette.Steel,
    Matrix4d.CreateTranslation((-30, 0, 15))));
scene.Add(new Part("smooth union", stem.SmoothUnion(bulb, blend: 6), Palette.Teal,
    Matrix4d.CreateTranslation((30, 0, 15))));
```

![A hard union next to a smooth union with a blended neck](images/smooth-blend.png)

A blend of zero or less degrades to the exact hard operator — field *and* bounds —
so a parametric blend that shrinks to nothing does not have to be special-cased at
the call site.

The distance a blended field reports is **correct in sign everywhere and a lower
bound in magnitude near the seam**, which is what meshing and the polygonizer's cull
need; don't read a wall thickness off the blend region itself.

## Related

- [Offset](offset.md) — grow or shrink a solid by a uniform distance
- [Shell](shell.md) — hollow it into a skin
- [Lattices](lattices.md) — periodic infill from the same field arithmetic
- [The SDF vocabulary](sdf-vocabulary.md) — field primitives and domain operations
