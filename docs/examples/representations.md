# The three representations

A `Shape` is an immutable operation graph, not geometry. Lowering it chooses the
engine: `ToBrep()` (exact solids), `ToImplicit()` (signed distance fields),
`ToMesh()` (triangles). Each lowering uses native operations where the target has
them and *bridges* through another representation where it doesn't.

```csharp render:three-reps
var model = Shape.Box(30, 21, 12)
    .SmoothUnion(Shape.Sphere(7.5).Translate(0, 0, 8), blend: 4)
    - Shape.Cylinder(4.5, 40).Translate(8, 0, 0);

// The smooth blend has no B-Rep form, so the B-Rep column drops it:
var brepModel = Shape.Box(30, 21, 12) - Shape.Cylinder(4.5, 40).Translate(8, 0, 0);

var scene = new Scene();
scene.Add(new Part("to B-Rep", Shape.From(brepModel.ToBrep()), Palette.Steel,
    Matrix4d.CreateTranslation((-45, 0, 0))));
scene.Add(new Part("to implicit", Shape.From(model.ToImplicit()), Palette.Teal));
scene.Add(new Part("to mesh", Shape.From(model.ToMesh()), Palette.Coral,
    Matrix4d.CreateTranslation((45, 0, 0))));
```

![The same model lowered to B-Rep, implicit, and mesh](images/three-reps.png)

Everything is convertible **to mesh** — what has no B-Rep form is polygonized from
the SDF path instead, so `ToMesh()` and `Scene.Add` never reject a shape.

## Explain: the honest support report

`Explain(target)` reports the per-node plan — **Native**, **Bridged** (through
another representation; approximate but robust), or **Impossible** — without doing
any work. `CanConvertTo` is the boolean version, and impossible conversions throw
`ShapeConversionException` carrying the same report:

```csharp run:explain
var model = Shape.Box(30, 21, 12)
    .SmoothUnion(Shape.Sphere(7.5).Translate(0, 0, 8), blend: 4);

var report = model.Explain(TargetRep.Brep);
Console.WriteLine(report);            // names the SmoothUnion node as Impossible

if (model.CanConvertTo(TargetRep.Brep))
    throw new Exception("a smooth blend must not be B-Rep convertible");
if (!model.CanConvertTo(TargetRep.Implicit) || !model.CanConvertTo(TargetRep.Mesh))
    throw new Exception("the blend is native in the implicit engine and meshable");

try
{
    model.ToBrep();
    throw new Exception("expected ShapeConversionException");
}
catch (ShapeConversionException ex)
{
    Console.WriteLine($"rejected as expected: {ex.Report.Entries.Count} nodes classified");
}
```

Transforms are never applied to finished geometry when the target can do better: the
lowering **bakes the accumulated matrix into construction inputs** (profiles,
directions, axes), so a rotated-then-drilled B-Rep stays exact. See
`src/EngrCAD.Modeling/README.md` for the full operation-by-target support matrix.

## Dropping down to the engine APIs

`Shape` is a convenience layer, not a cage. Exit to an engine for something the
vocabulary doesn't surface, then re-enter with `Shape.From(...)`:

```csharp render:drop-down
// Exit to the SDF AST for a custom field, re-enter, and keep modeling:
var ripple = Sdf.Blend(Sdf.Torus(12, 4), Sdf.Sphere(9), blendDistance: 6);

var body = Shape.From(ripple)
    .Union(Shape.Cylinder(2.5, 26).Translate(0, 0, 0))
    .ToMesh();  // lower once at the end

var scene = new Scene();
scene.Add(new Part("hybrid", Shape.From(body), Palette.Plum));
```

![A hand-written SDF blended shape composed back into the Shape vocabulary](images/drop-down.png)

The same works in the other directions: `Shape.From(brepSolid)` re-enters an exact
solid after B-Rep surgery (e.g. `Filleting.FilletEdge`), and `Shape.From(mesh)` wraps
scanned or generated meshes (exact mesh SDF in implicit lowerings, direct
participation in mesh booleans).
