# Boolean Operations

## Union

## Subtract

## Intersect

## All

```C#
var cube = new Box {X = 2f, Y = 2f, Z = 2f };
var sphere = new Sphere { Radius = 1.35f };
var cylinderA = new Cylinder { Radius = 0.7f, Height = 3 };
var cylinderB = cylinderA.RotateX(MathHelper.DegreesToRadians(90));
var cylinderC = cylinderA.RotateY(MathHelper.DegreesToRadians(90));
var result = cube.Intersect(sphere).Subtract((cylinderA, cylinderB, cylinderC).Union());
```
Produces:
