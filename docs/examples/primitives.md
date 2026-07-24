# Primitives

`Shape` provides the four solid primitives. All are centered at the origin with their
axis along +Z; position them with [transforms](transforms-patterns.md).

```csharp render:primitives
var scene = new Scene();
scene.Add(new Part("box", Shape.Box(30, 22, 14), Palette.Steel,
    Matrix4d.CreateTranslation((-55, 0, 0))));
scene.Add(new Part("cylinder", Shape.Cylinder(radius: 11, height: 26), Palette.Brass,
    Matrix4d.CreateTranslation((-18, 0, 0))));
scene.Add(new Part("sphere", Shape.Sphere(radius: 13), Palette.Coral,
    Matrix4d.CreateTranslation((18, 0, 0))));
scene.Add(new Part("torus", Shape.Torus(majorRadius: 13, minorRadius: 5), Palette.Teal,
    Matrix4d.CreateTranslation((56, 0, 0))));
```

![Box, cylinder, sphere, and torus primitives](images/primitives.png)

Every primitive is available in **all three representations** — `ToBrep()` produces
exact analytic surfaces (planes, cylinders, spheres, tori), `ToImplicit()` produces
exact signed distance fields, and `ToMesh()` tessellates. A `Box(in Aabb)` overload
builds a box from explicit bounds instead of a centered size.

Rigid transforms and uniform scaling keep every primitive exact; shearing a sphere or
torus has no analytic B-Rep surface and is rejected there (`Explain` names the node) —
see the [support matrix](representations.md).
