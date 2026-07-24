# Exports

EngrCAD exports STEP (exact B-Rep), STL and OBJ (meshes), and PNG renders. The
snippets on this page run against a temp directory (`Scratch`) during the docs build,
so the export paths are exercised on every build — no screenshots needed here.

## STEP (exact geometry)

`StepWriter` emits AP214 `MANIFOLD_SOLID_BREP` with analytic surfaces and curves
(including rational NURBS); `StepReader` imports it back. Round-trip:

```csharp run:step-roundtrip
var solid = (Shape.Box(30, 20, 10) - Shape.Cylinder(4, 30).Translate(8, 0, 0)).ToBrep();

var path = Path.Combine(Scratch, "plate.step");
StepWriter.WriteFile(solid, path, name: "drilled plate");

var result = StepReader.ReadFile(path);
if (result.Solids.Count != 1)
    throw new Exception($"expected 1 solid, got {result.Solids.Count}: "
        + string.Join("; ", result.Diagnostics));
Console.WriteLine($"round-tripped {result.Solids[0].Faces.Count()} faces");
```

Swept surfaces are not exportable yet; `Part.Source` tracks whether a part is
B-Rep-representable for the `--export` switch.

## STL (3D printing)

Binary STL, single mesh or multiple parts merged with their transforms applied:

```csharp run:export-stl
var bracket = Shape.Extrude(Sketch.RoundedRectangle(40, 24, 5), 8)
    .Drill(StandardHoles.Clearance(5), [new(-14, 0), new(14, 0)], depth: 10,
           SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY));

StlWriter.WriteFile(bracket.ToMesh(), Path.Combine(Scratch, "bracket.stl"));

// Multi-part: merge parts with placement transforms, slicer-ready.
StlWriter.WriteFile(
    [(bracket.ToMesh(), Matrix4d.Identity),
     (Shape.Cylinder(3, 12).ToMesh(), Matrix4d.CreateTranslation((0, 30, 0)))],
    Path.Combine(Scratch, "assembly.stl"));

if (new FileInfo(Path.Combine(Scratch, "bracket.stl")).Length < 84)
    throw new Exception("STL came out empty");
```

## OBJ

```csharp run:export-obj
var mesh = Shape.Torus(12, 4).ToMesh();
ObjWriter.WriteFile(mesh, Path.Combine(Scratch, "torus.obj"));
if (!File.ReadLines(Path.Combine(Scratch, "torus.obj")).Any(l => l.StartsWith("v ")))
    throw new Exception("OBJ has no vertices");
```

## From a model program

Any program using `EngrCad.Run` gets headless export and render switches for free —
no code changes, CI-friendly, no window:

```
dotnet run --project MyDesign -- --export bracket.step   # STEP per B-Rep part
dotnet run --project MyDesign -- --export bracket.stl    # merged binary STL
dotnet run --project MyDesign -- --export bracket.obj    # merged OBJ
dotnet run --project MyDesign -- --render bracket.png    # offscreen PNG render
```

`--render` uses the same offscreen renderer that produced every screenshot in these
docs (`EngrCad.RenderToImage`) — see [the viewer](viewer.md).
