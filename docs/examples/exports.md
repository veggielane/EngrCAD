# Exports

EngrCAD exports STEP (exact B-Rep), its own lossless `.ecb` B-Rep archive, STL, OBJ, OFF,
3MF, AMF and glTF 2.0 (meshes), VTU (meshes plus simulation results), and PNG renders. The
snippets on this page run against
a temp directory (`Scratch`) during the
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

The exporter covers the full analytic conic family (circles, ellipses, parabolas,
hyperbolas, planar offset curves) plus rational NURBS — including translated NURBS
profile edges, which export exactly by transforming control points rather than being
sampled. The importer maps the same entities back, and also synthesizes foreign
`CONICAL_SURFACE`/`TOROIDAL_SURFACE` entities onto the kernel's revolved-surface
machinery.

### Units

Foreign files declare their length unit, and the importer scales everything to
millimetres — SI prefixes in closed form, `CONVERSION_BASED_UNIT` chains (inches)
multiplied down — reporting the factor as a diagnostic:

```csharp run:step-units
// Simulate a foreign metre-unit file: our own writer always declares millimetres.
var metreCube = SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1)));
string text = StepWriter.Write(metreCube).Replace(".MILLI.,.METRE.", "$,.METRE.");

var imported = StepReader.Read(text);
double size = imported.Solids[0].Vertices.Max(v => v.Position.X);
Console.WriteLine($"1 m cube imported as {size} mm"); // 1000
if (Math.Abs(size - 1000) > 1e-9)
    throw new Exception("metre units were not scaled");
```

## Native B-Rep archive (`.ecb`)

STEP is the interchange format; the `.ecb` archive is the **lossless** one. It round-trips
every curve and surface type the kernel has, including the ones STEP has no entity for —
helical surfaces (modelled threads), lofted surfaces, swept (RMF) surfaces, offset curves,
spiral arcs — plus trimmed edge domains and `CurveSegment` mappings.

It is a **versioned, human-diffable text format**: a numbered entity table where every
reference is `#n`, so shared topology stays shared (an edge used by two faces is written
once and referenced twice), one entity per line, dependencies always defined before use.

```csharp run:brep-archive-roundtrip
// A modelled thread: a HelicalSurface, which STEP cannot represent at all.
// (chamferEnds: false is the B-Rep-native form -- the end cones have no exact B-Rep.)
var rod = Shape.ExternalThread(8, length: 10, chamferEnds: false).ToBrep();

var path = Path.Combine(Scratch, "rod.ecb");
BrepArchive.WriteFile(rod, path, name: "M8 rod");

var result = BrepArchive.ReadFile(path);
var restored = result.Single();
restored.Validate();

Console.WriteLine($"{result.Name}: {restored.Faces.Count()} faces, "
    + $"{restored.Edges.Count()} edges, format v{result.Version}");
if (restored.Faces.Count() != rod.Faces.Count())
    throw new Exception("face count changed across the round trip");

// save -> load -> save is byte-identical: the strong form of "exact".
if (BrepArchive.Write(restored, "M8 rod") != File.ReadAllText(path))
    throw new Exception("the archive is not a fixed point under round-trip");
```

The file reads like this — `Solid` at the end referencing shells, referencing faces,
referencing surfaces and loops all the way down:

```
ENGRCAD-BREP 1
UNITS MM
NAME 'M8 rod'
GENERATOR 'EngrCAD'

#1 = Line((4 0 -0.078125), (4 0 0.078125))
#2 = Helical((0 0 0 1 0 0 0 1 0), (4 -0.078125), (4 0.078125), 1.25, (0 50.26548))
#3 = Vertex((4 0 -0.078125))
...
ROOT #57
```

Two contract points. An **unknown version is refused by name** rather than parsed
hopefully — a newer writer may have added entity forms, and a partial parse of a solid we
cannot build is worse than a clear message. And units are declared and checked, the lesson
the STEP importer paid for.

Reading gives you an ordinary `BrepSolid`, so it feeds straight back into the modelling
API:

```csharp run:brep-archive-reuse
var original = (Shape.Box(30, 20, 10) - Shape.Cylinder(4, 30)).ToBrep();
BrepArchive.WriteFile(original, Path.Combine(Scratch, "plate.ecb"));

var reloaded = BrepArchive.ReadFile(Path.Combine(Scratch, "plate.ecb")).Single();
var further = Shape.From(reloaded) - Shape.Cylinder(3, 30).Translate(10, 0, 0);

var mesh = further.ToMesh();
Console.WriteLine($"reloaded and cut again: {mesh.FaceCount} facets, closed = {mesh.IsClosed}");
if (!mesh.IsClosed)
    throw new Exception("the reloaded solid did not survive a second boolean");
```

### Healing imported files

Foreign writers emit each face's wire separately, so a solid can arrive as a face
soup: duplicated edges, merely-coincident vertices, inconsistent face orientations,
disconnected bodies sharing one shell. `ShapeHealing.Heal` repairs what it can and
reports everything — including what it could not repair — as a return value:

```csharp run:step-healing
var body = SolidFactory.MakeBox(new Aabb((0, 0, 0), (10, 10, 10)));
var boss = SolidFactory.MakeBox(new Aabb((20, 0, 0), (26, 6, 6)));
// A foreign writer's flat face list: two disconnected bodies in ONE shell.
var soup = new BrepSolid([new BrepShell([.. body.Faces, .. boss.Faces])]);

var healed = ShapeHealing.Heal(soup);
Console.WriteLine(healed.Report); // "1 shells split off; result is a closed manifold."
if (healed.Solid.Shells.Count != 2)
    throw new Exception("expected the disconnected components split into two shells");
```

`ShapeHealing.Analyze` is the dry run; `ShapeHealingOptions` switches each pass
(vertex merging, small-edge collapse, sewing, wire re-ordering, shell repair) and
opts into the two passes that adjust geometry or trims (`RefitStraightEdges`,
`RetrimCurvedEdges`).

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

## glTF 2.0 (the web, AR and DCC route)

`GltfWriter` writes binary `.glb` and self-contained `.gltf` (the buffer rides inline
as a data URI, so there is no sidecar `.bin` to lose). It is the one mesh format here
that **keeps the assembly hierarchy** instead of flattening it: glTF has real nodes, so
`GltfScene.Plan` emits a node per tab, per sub-assembly and per occurrence, with **one
mesh per distinct part however many times it is placed**. A fastener placed fifty times
is written once — the same "one product, N occurrences" structure the STEP assembly
writer uses, and the property the baking exporters (STL, OBJ, 3MF) structurally cannot
have.

```csharp run:export-gltf
var bolt = new Part("bolt", Shape.Cylinder(2, 12), Palette.Steel);
var plate = new Part("plate", Shape.Box(60, 40, 6), Palette.Brass);

var stack = new Assembly("stack");
stack.Add(plate);
foreach (var x in new[] { -20.0, 0.0, 20.0 })
    stack.Add(bolt, Frame3d.FromOrthonormal((x, 0, 6), Vector3d.UnitX, Vector3d.UnitY));

var scene = new Scene();
scene.AddTab("Assembly").Add(stack);

var plan = GltfScene.WriteFile(scene, Path.Combine(Scratch, "stack.glb"));

// TWO meshes for four placements: the bolt is written once and instanced.
Console.WriteLine($"{plan.Geometries.Count} meshes, {plan.Roots.Count} root node(s)");
if (plan.Geometries.Count != 2)
    throw new Exception($"expected 2 meshes, got {plan.Geometries.Count}");

var bytes = File.ReadAllBytes(Path.Combine(Scratch, "stack.glb"));
if (System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "glTF")
    throw new Exception("not a GLB");
```

Colours become PBR metallic-roughness materials, translucent parts declare
`alphaMode: BLEND`, and a part carrying a `FieldDisplay` exports its simulation-result
colours as the `COLOR_0` vertex attribute — the *same* colours the viewport draws, since
both come from `FieldRendering.SourceColors`. So an FEA result can be handed to a
browser or a phone as one file.

Three conventions worth knowing:

* **Y-up, metres.** glTF is Y-up and metric; EngrCAD is Z-up millimetres. The conversion
  rides on a single root node built from exact values, so every part transform below it
  stays verbatim. `new GltfOptions { YUp = false, Scale = 1 }` writes model coordinates.
* **Winding is not flipped under mirroring.** The spec requires the *consumer* to reverse
  winding when a node's transform has a negative determinant, so the transform is written
  as-is; flipping here as well would double the correction.
* **Deformation does not travel.** A displacement exaggeration is a viewing parameter and
  glTF has nowhere to record one, so a file carrying 50×-displaced geometry would be
  indistinguishable from a model that really is that shape. The colours go, the
  displacement stays behind.

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
dotnet run --project MyDesign -- --export bracket.ecb    # native lossless B-Rep archive
dotnet run --project MyDesign -- --export bracket.stl    # merged binary STL
dotnet run --project MyDesign -- --export bracket.obj    # merged OBJ
dotnet run --project MyDesign -- --export bracket.off    # merged OFF
dotnet run --project MyDesign -- --export bracket.3mf    # 3MF (named, colored objects)
dotnet run --project MyDesign -- --export bracket.amf    # AMF (named, colored objects)
dotnet run --project MyDesign -- --export bracket.glb    # glTF 2.0 (hierarchy + materials)
dotnet run --project MyDesign -- --export bracket.gltf   # glTF 2.0, self-contained JSON
dotnet run --project MyDesign -- --export bracket.vtu    # VTK unstructured grid + results
dotnet run --project MyDesign -- --render bracket.png    # offscreen PNG render
```

`--render` uses the same offscreen renderer that produced every screenshot in these
docs (`EngrCad.RenderToImage`) — see [the viewer](viewer.md).
