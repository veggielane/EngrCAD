# Primitives

`Shape` provides the four solid primitives. All are centered at the origin with their
axis along +Z; position them with [transforms](transforms-patterns.md).

```csharp render:primitives
// Primitives are centered at the origin; the part transforms here sit each one
// on the ground plane and spread them out for the camera.
var scene = new Scene();
scene.Add(new Part("box", Shape.Box(24, 18, 12), Palette.Steel,
    Matrix4d.CreateTranslation((-40, 0, 6))));
scene.Add(new Part("cylinder", Shape.Cylinder(radius: 9, height: 20), Palette.Brass,
    Matrix4d.CreateTranslation((-13, 0, 10))));
scene.Add(new Part("sphere", Shape.Sphere(radius: 10.5), Palette.Coral,
    Matrix4d.CreateTranslation((13, 0, 10.5))));
scene.Add(new Part("torus", Shape.Torus(majorRadius: 10, minorRadius: 4), Palette.Teal,
    Matrix4d.CreateTranslation((40, 0, 4))));
```

![Box, cylinder, sphere, and torus primitives](images/primitives.png)

Every primitive is available in **all three representations** — `ToBrep()` produces
exact analytic surfaces (planes, cylinders, spheres, tori), `ToImplicit()` produces
exact signed distance fields, and `ToMesh()` tessellates. A `Box(in Aabb)` overload
builds a box from explicit bounds instead of a centered size.

Rigid transforms and uniform scaling keep every primitive exact; shearing a sphere or
torus has no analytic B-Rep surface and is rejected there (`Explain` names the node) —
see the [support matrix](representations.md).
