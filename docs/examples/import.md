# Importing meshes

`Shape.From(path)` imports a mesh file — STL (binary or ASCII, autodetected), OBJ, or
OFF — as a mesh-backed shape. Underneath it is `MeshReader.ReadAndRepair`: dirty files
weld instead of throwing, the repair pipeline runs (crack welding,
degenerate/duplicate removal, consistent outward orientation, T-junction zipping), and
the result composes with everything else in the vocabulary — booleans, transforms,
the implicit route.

This example round-trips through a real file on every docs build: write an STL (which
is an *unindexed facet soup* by design — 12 triangles arrive as 36 unrelated
vertices), import it back, and check the repair actually happened:

```csharp run:import-repair
var path = Path.Combine(Scratch, "imported.stl");
StlWriter.WriteFile(Shape.Box(20, 10, 5).ToMesh(), path);

var imported = Shape.From(path, out var report);
var mesh = imported.ToMesh();

if (!mesh.IsClosed) throw new Exception("import should come back watertight");
if (report.ComponentCount != 1) throw new Exception($"one body expected, got {report.ComponentCount}");
Console.WriteLine($"imported {mesh.FaceCount} faces, volume {mesh.Volume():F1}");
```

An imported shape is a full citizen — here one is drilled and shown beside the
original:

```csharp render:import-drilled
var path = Path.Combine(Scratch, "bracket-import.stl");
StlWriter.WriteFile(Shape.Extrude(Sketch.RoundedRectangle(40, 24, 5), 8).ToMesh(), path);

var imported = Shape.From(path);
var scene = new Scene();
scene.Add(new Part("as imported", imported, Palette.Steel));
scene.Add(new Part("imported, then drilled", imported - Shape.Cylinder(6, 30).Translate(0, 0, 4),
    Palette.Coral, Matrix4d.CreateTranslation((0, 34, 0))));
```

![An imported STL bracket beside a drilled copy of it](images/import-drilled.png)

## What repair does and does not do

- **Always**: exact vertex welding at read (1e-9, or the float32 quantization an STL
  forces), then the soup passes — degenerate and duplicate faces dropped, every
  component re-wound consistently outward, T-junctions zipped.
- **Only when asked**: `Shape.From(path, out var report, fillHolesAndCracks: true)`
  additionally welds boundary cracks pair-wise and fills holes — off by default
  because closing a hole *invents geometry*, which an importer should only do
  deliberately.
- **Never silently**: files whose defects need topological surgery beyond the
  pipeline throw with the defect named; the `out MeshRepairReport` overload reports
  exactly what was repaired (vertices merged, faces rewound, cracks welded, holes
  filled and skipped).

The engine layer is available directly when you want the diagnostics without the
shape wrapper: `MeshReader.ReadFile` returns mesh-or-soup plus warnings and never
throws on dirty geometry; `MeshRepair.Clean`/`AutoRepair` are the pipeline.
An imported mesh has no exact B-Rep — `imported.ToBrep()` honestly refuses
(mesh-to-B-Rep reconstruction is future work); `ToMesh` and `ToImplicit` both work.
