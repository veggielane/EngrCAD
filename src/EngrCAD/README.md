# EngrCAD

The meta-package for the EngrCAD hybrid CAD kernel. Referencing it brings in the whole
kernel as package dependencies:

- **EngrCAD.Core** — tolerant math, AABB/ray, BVH, octree
- **EngrCAD.Mesh** — half-edge mesh engine (booleans, subdivision, decimation)
- **EngrCAD.Implicit** — SDF primitives and operators as an AST
- **EngrCAD.BRep** — parametric curves/surfaces/topology, extrude/revolve/sweep,
  surface intersection, STEP export
- **EngrCAD.Interop** — conversions between representations, B-Rep booleans, and the
  `Scene`/`Part` display model
- **EngrCAD.Query** — LINQ spatial querying over BVH indexes

Add **EngrCAD.Viewer** separately for the desktop viewer (`EngrCad.Show(scene)`); it is
kept out of the meta-package so headless/CI consumers don't pull Avalonia.

```csharp
var scene = new Scene();
var body = SolidFactory.MakeBox(new Aabb((0, 0, 0), (40, 30, 10)));
var bore = SolidFactory.Extrude(Profile.Circle((20, 15, -1), Vector3d.UnitX, Vector3d.UnitY, 4), (0, 0, 12));
scene.Add("bracket", BrepBoolean.Difference(body, bore), Palette.Steel);
EngrCad.Show(scene);   // from EngrCAD.Viewer
```
