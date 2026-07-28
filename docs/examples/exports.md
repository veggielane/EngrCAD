# Exports

EngrCAD exports STEP (exact B-Rep), STL, OBJ, OFF, 3MF and AMF (meshes), and PNG
renders. The snippets on this page run against a temp directory (`Scratch`) during the
docs build, so the export paths are exercised on every build — no screenshots needed
here.

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

## 3MF and AMF (modern printing formats)

3MF is the format today's slicers prefer: a zip package whose model XML carries
per-object **names and colors**, so a multi-part print arrives as a named object
list rather than one anonymous triangle soup. AMF is its ISO/ASTM predecessor —
plain XML, same idea. Both writers take the same part list: mesh + transform +
name + optional RGB color (transforms are baked into the vertices, mirrored parts
keep outward windings):

```csharp run:export-3mf
var body = Shape.Box(30, 20, 8).ToMesh();
var cap = Shape.Cylinder(6, 4).ToMesh();

var parts = new List<MeshExportPart>
{
    new(body, Matrix4d.Identity, "body", (0.55f, 0.68f, 0.84f)),
    new(cap, Matrix4d.CreateTranslation((0, 0, 6)), "cap", (0.85f, 0.72f, 0.38f)),
};
ThreeMfWriter.WriteFile(parts, Path.Combine(Scratch, "widget.3mf"));
AmfWriter.WriteFile(parts, Path.Combine(Scratch, "widget.amf"));

// The 3MF is a real OPC package: content types, relationships, model XML.
using (var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(Scratch, "widget.3mf")))
{
    if (archive.GetEntry("3D/3dmodel.model") is null)
        throw new Exception("3MF package is missing its model");
}
```

## OFF

The writer twin of the OFF reader — handy for mesh-processing toolchains
(Meshlab, CGAL). N-gon faces are written as-is:

```csharp run:export-off
var mesh = Shape.Cone(8, 3, 10).ToMesh();
OffWriter.WriteFile(mesh, Path.Combine(Scratch, "cone.off"));
var read = MeshReader.ReadFile(Path.Combine(Scratch, "cone.off"));
if (read.Mesh is null || !read.Mesh.IsClosed)
    throw new Exception("OFF did not round-trip closed");
```

## From a model program

Any program using `EngrCad.Run` gets headless export and render switches for free —
no code changes, CI-friendly, no window:

```
dotnet run --project MyDesign -- --export bracket.step   # STEP per B-Rep part
dotnet run --project MyDesign -- --export bracket.stl    # merged binary STL
dotnet run --project MyDesign -- --export bracket.obj    # merged OBJ
dotnet run --project MyDesign -- --export bracket.off    # merged OFF
dotnet run --project MyDesign -- --export bracket.3mf    # 3MF (named, colored objects)
dotnet run --project MyDesign -- --export bracket.amf    # AMF (named, colored objects)
dotnet run --project MyDesign -- --render bracket.png    # offscreen PNG render
```

`--render` uses the same offscreen renderer that produced every screenshot in these
docs (`EngrCad.RenderToImage`) — see [the viewer](viewer.md).
