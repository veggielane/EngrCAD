# Chamfer & fillet

`Chamfer(setback, faceSelector)` and `Fillet(radius, faceSelector)` are rim features:
they operate on the closed outer rim of planar faces picked by a **LINQ face
selector** running over the lowered B-Rep solid (`EngrCAD.BRep.BrepQueries` is the
selection vocabulary).

```csharp render:chamfer-fillet
var plate = Shape.Extrude(Sketch.RoundedRectangle(36, 26, 5), 8)
    .Fillet(2.5, s => s.PlanarFacesWithNormal(Vector3d.UnitZ))     // smooth top rim
    .Chamfer(1.5, s => s.PlanarFacesWithNormal(-Vector3d.UnitZ));  // beveled base

var scene = new Scene();
scene.Add(new Part("plate", plate, Palette.Steel));
```

![A rounded-rectangle plate with a filleted top rim and chamfered base](images/chamfer-fillet.png)

Circular rims get exact cone bands (chamfer) and torus bands (fillet):

```csharp render:chamfer-fillet-round
var boss = Shape.Cylinder(16, 10).Translate(0, 0, 5)
    .Fillet(3, s => s.PlanarFacesWithNormal(Vector3d.UnitZ));

var washer = (Shape.Cylinder(16, 6) - Shape.Cylinder(6, 20)).Translate(0, 0, 3)
    .Chamfer(1.5, s => s.PlanarFacesWithNormal(Vector3d.UnitZ));

var scene = new Scene();
scene.Add(new Part("filleted boss", boss, Palette.Brass,
    Matrix4d.CreateTranslation((-22, 0, 0))));
scene.Add(new Part("chamfered washer", washer, Palette.Steel,
    Matrix4d.CreateTranslation((22, 0, 0))));
```

![A cylinder with a filleted top rim and a washer with a chamfered rim](images/chamfer-fillet-round.png)

## Selectors, not IDs

The selector is a function `BrepSolid -> IEnumerable<BrepFace>` — a *semantic* query
(`IsPlanar`, `IsCylindrical`, `IsCircular`, `Length`, adjacency via `face.Edges()` /
`solid.FacesOf(edge)`, sugar like `PlanarFacesWithNormal`) that re-runs on every
regeneration. That is the topological-naming story: references survive upstream edits
because they describe *what* to select, not which stored face.

Because selectors run on the **lowered** solid, upstream transforms are visible and
feature sizes scale with uniform scaling.

## Scope

Both features operate on the closed outer rim of planar faces, and reject unsuitable
input with guidance rather than producing bad geometry:

- **Chamfer** takes straight edges (sharp corners miter exactly with planar strips)
  and full circular rims (exact cone bands).
- **Fillet** needs a tangent-continuous rim — lines + arcs like rounded rectangles,
  slots, and circles. Round sharp sketch corners first
  (`Sketch.RoundedRectangle` instead of `Rectangle`).
- Interior loops (holes) must stay clear of the shrunk boundary.

Both are B-Rep-native; the implicit lowering bridges through tessellation. For fully
general edge fillets, drop down to `Filleting.FilletEdge` on the B-Rep API — see
[dropping down](representations.md#dropping-down-to-the-engine-apis).
