# Saving and loading a document

A `Scene` describes a model; a **`Document`** is that scene in a file. One JSON envelope
carries the whole thing — tabs, parts, colours and poses, each part's `FeatureHistory`,
assemblies and their occurrences, exploded-view offsets, mates, 3D annotations and
simulation results — with a version field on the front.

```csharp
var document = new Document(scene);
document.SaveFile("bracket.json");

var result = Document.LoadFile("bracket.json");
var reloaded = result.Scene;
```

## A document is its construction history, not its geometry

Nothing exact is stored. A part built from a `FeatureHistory` saves that history and
**regenerates** on load, so the reloaded part is still parametric — change a `[Param]`
and it rebuilds, exactly as before it was saved.

```csharp run:document-roundtrip
var history = new FeatureHistory();
history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 40)) { Height = 8 });
history.Add(new HoleFeature(HoleSpec.Simple(6), [new(-20, 0), new(20, 0)]) { Depth = 12 });

var scene = new Scene();
scene.Add(history.ToPart("plate"));

var document = new Document(scene);
string json = document.Save();

// Reload, then edit a parameter and regenerate: the model came back, not a snapshot.
var result = Document.Load(json);
var plate = result.Scene.Tabs[0].Parts[0];

double before = MeshMassProperties.Compute(plate.GetMesh()).Volume;
plate.History!.LoadParameters("""{ "ExtrudeSketchFeature": { "Height": 16 } }""");
plate.Regenerate();
double after = MeshMassProperties.Compute(plate.GetMesh()).Volume;

if (!(after > before * 1.8))
    throw new Exception($"the reloaded history did not rebuild: {before} -> {after}");
```

## Geometry with no recipe: snapshots, named

Some geometry has no construction record to replay — an imported `.stl`, a raw
`HalfEdgeMesh`, an `Sdf`, a `Shape` graph assembled in code. Those parts are **not
silently dropped and not silently pretended parametric**: their display mesh is embedded
in the file as a *snapshot*, and the load reports every part that came back that way.

```csharp run:document-snapshots
var scene = new Scene();
scene.Add(new Part("housing", Shape.Box(40, 30, 10) - Shape.Cylinder(8, 40)));   // a Shape graph
scene.Add(new Part("jig", MeshPrimitives.Box(10, 10, 10)));                       // a raw mesh

var result = Document.Load(new Document(scene).Save());

// Both parts are present and correct; both are honestly labelled non-parametric.
Console.WriteLine($"snapshots: {string.Join(", ", result.Snapshots)}");
if (result.Snapshots.Count != 2)
    throw new Exception("expected both parts to be reported as snapshots");
if (result.Scene.AllParts.Count() != 2)
    throw new Exception("the parts themselves must still load");
```

`DocumentSaveOptions.EmbedGeometry = false` writes a recipe-only file instead: those parts
become an explicit "no geometry" record naming the reason, which loads as a warning and no
part. Either way the file says what it left out.

> **Why embedded rather than a path to a file beside it?** A document that points at its
> neighbours is a manifest, not a document — the reference breaks the first time the file
> is moved, renamed or emailed, and the failure looks like missing geometry rather than a
> missing file. One file that reloads the model is the whole value of the envelope.

## Assemblies, poses and mates

Parts are shared by reference, so a part placed six times is stored **once** and referenced
six times; the same holds for a sub-assembly nested in two parents. Occurrence frames,
explode offsets and the assembly tree all round-trip, and `MateSet`s ride along on the
`Document` (a scene does not own its mates — they are solver input, not structure).

```csharp run:document-assembly
var plate = new Part("plate", Shape.Box(40, 30, 5));
var rig = new Assembly("rig");
var lower = rig.Add(plate);
var upper = rig.Add(plate, Frame3d.FromXY((0, 0, 20), Vector3d.UnitX, Vector3d.UnitY));
upper.ExplodeOffset = (0, 0, 40);

var scene = new Scene();
scene.AddTab("Model").Add(rig);

var document = new Document(scene);
document.Mates.Add(new MateSet(rig)
    .Ground(lower)
    .Add(Mate.Planar(
        MateGeometry.PlanarFace(lower, FaceRef.Top),
        MateGeometry.PlanarFace(upper, FaceRef.Bottom),
        gap: 2, name: "stack")));

var result = Document.Load(document.Save());

// One part, two placements, both poses preserved.
if (result.Scene.AllParts.Count() != 1)
    throw new Exception("the shared part must not be duplicated per placement");
var instances = result.Scene.AllInstances.ToList();
if (instances.Count != 2 || instances[1].World.M34 != 20)
    throw new Exception("occurrence poses did not survive");
if (result.Document.Mates[0].Mates[0].Name != "stack")
    throw new Exception("the mate did not survive");
```

## Loading returns warnings; it does not throw

The convention is `FeatureHistory.LoadParameters`': anything the file describes that this
build cannot rebuild becomes a message in `DocumentLoadResult.Warnings`, never an
exception. Only a structurally invalid file — bad JSON, a missing envelope, a version this
build does not read — throws.

What warns, and why:

| In the file | On load |
| --- | --- |
| A feature with no serialized inputs (`BooleanFeature`, a `FromFunc` lambda, a `ComponentFeature`) | Skipped by name, unless `DocumentLoadOptions.ResolveOpaqueFeature` supplies the instance |
| An annotation that measures through a **selector lambda** (`LinearDimension.BetweenFaces`, `RadialDimension.OnEdge`) | Skipped by name — only its own code can rebuild it |
| A part placed from a catalogue item | Geometry loads; the `HardwareComponent` itself does not (it is a code object) |
| A mate whose query no longer resolves | Loaded from its pinned coordinates, per `MateSet.LoadMates` |

`DocumentLoadResult.Complete` is true when nothing warned.

## The file is a fixed point

`save → load → save` is **byte-identical** for everything that round-trips. That property
is worth more than it sounds: it is what catches a field written but never read, a default
that reloads as a different default, or an ordering that is not purely a function of the
model. A file carrying opaque records is smaller the second time round by exactly those
records — the ones the load already warned about — and a fixed point from there on.

```csharp run:document-fixed-point
var scene = new Scene(new MeshQuality { SegmentsPerCircle = 24 });
var history = new FeatureHistory();
history.Add(new ExtrudeSketchFeature(Sketch.Circle(15)) { Height = 10 });
var boss = history.ToPart("boss", Palette.Brass);
boss.Annotate(new LeaderNote((0, 0, 10), "TOP FACE FLAT WITHIN 0.05"));
scene.Add(boss);

string first = new Document(scene).Save();
string second = Document.Load(first).Document.Save();
if (first != second)
    throw new Exception("save -> load -> save must be byte-identical");
```

## Undo and redo

Editing a document goes through **`DocumentEdit`s** run by an **`UndoStack`**. An edit
captures whatever it is about to overwrite, so undoing it restores the previous state
exactly rather than recomputing it — the `MeshChangeSet` journaling pattern at document
granularity.

```csharp run:document-undo
var history = new FeatureHistory();
history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 40)) { Height = 8 });
history.Add(new HoleFeature(HoleSpec.Simple(6), [new(-20, 0), new(20, 0)]) { Depth = 12 });

var scene = new Scene();
var plate = history.ToPart("plate");
scene.Add(plate);

var undo = new UndoStack();
double original = MeshMassProperties.Compute(plate.GetMesh()).Volume;

// A parameter edit goes through the same JSON seam as SaveParameters and the MCP
// server's set_param, and ends in Part.Regenerate().
undo.Do(DocumentEdits.SetParameter(plate, history.Features[0], "Height", 16.0));
double thicker = MeshMassProperties.Compute(plate.GetMesh()).Volume;

Console.WriteLine($"undo: {undo.UndoDescription}");        // "Set plate.ExtrudeSketchFeature.Height"
undo.Undo();
double back = MeshMassProperties.Compute(plate.GetMesh()).Volume;

if (!(thicker > original) || Math.Abs(back - original) > 1e-9)
    throw new Exception($"{original} -> {thicker} -> {back}");
```

The vocabulary covers what a UI actually does: `SetParameter` / `SetParameters`,
`Suppress`, `AddFeature` / `RemoveFeature`, `Rename` / `SetColor` / `SetTransform` /
`SetDisplayMode` / `SetClippedBySection`, `AddOccurrence` / `RemoveOccurrence` / `Repose` /
`SetExplodeOffset`, `AddMate` / `RemoveMate`, `AddAnnotation` / `RemoveAnnotation`.

### One user-visible step

`UndoStack.Group` collects everything done inside it into a single `CompoundEdit`, so
"place six fasteners" is one Ctrl+Z rather than twelve. Groups nest; only the outermost
becomes a step, and an empty one pushes nothing.

```csharp run:document-undo-group
var scene = new Scene();
var plate = new Part("plate", Shape.Box(60, 40, 8));
scene.Add(plate);

var undo = new UndoStack();
using (undo.Group("Style the plate"))
{
    undo.Do(DocumentEdits.SetColor(plate, Palette.Coral));
    undo.Do(DocumentEdits.SetDisplayMode(plate, DisplayMode.Translucent));
    undo.Do(DocumentEdits.SetClippedBySection(plate, false));
}

if (undo.Undoable.Count != 1)
    throw new Exception("a group is one step");

undo.Undo();                       // all three come back together
if (plate.DisplayMode != DisplayMode.Shaded || !plate.ClippedBySection)
    throw new Exception("the group did not undo as a unit");
```

### A refused edit changes nothing

An edit that cannot be applied leaves the document exactly as it found it — including the
parameter it was about to write. `Part.Regenerate` already keeps the previous complete body
when a rebuild fails; what an edit adds is taking its own bad value back, then reporting
what happened.

```csharp run:document-undo-refusal
var history = new FeatureHistory();
history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(60, 40)) { Height = 8 });

var scene = new Scene();
var plate = history.ToPart("plate");
scene.Add(plate);

var document = new Document(scene);
string before = document.Save();
var undo = new UndoStack();

try
{
    // Below the [Param(Min = 1e-9)] floor: the model will not rebuild.
    undo.Do(DocumentEdits.SetParameter(plate, history.Features[0], "Height", -5.0));
    throw new Exception("that should have been refused");
}
catch (DocumentEditException e)
{
    Console.WriteLine(e.Regeneration);          // names the feature and the violation
}

if (document.Save() != before)
    throw new Exception("a refused edit must leave the document untouched");
if (undo.CanUndo)
    throw new Exception("a refused edit is not history");
```

A group behaves the same way: if any member fails, the ones that already succeeded are
reverted before the exception leaves.

### The stack is session state, not document state

An undo history is not saved. A `Document` is what the model *is*; how it got there belongs
to this editing session, which is also why the stack holds edits (a few captured values)
rather than document snapshots. `UndoStack.Limit` bounds it (200 steps by default, oldest
dropped first), `Clear()` forgets it, and `Changed` fires on every movement so an Edit menu
can repaint from `CanUndo`/`UndoDescription`.

## What the format is not

- **Not a geometry interchange format.** Use STEP for exact B-Reps and STL/3MF/OBJ for
  meshes; a document is for reopening *your own* model in EngrCAD.
- **Not a `Shape`-graph serialization.** A part whose geometry is a `Shape` built in code
  — with no `FeatureHistory` in front of it — saves as a snapshot. Serializing the graph
  itself would make `BooleanFeature` round-trip too, and is filed as future work.
- **Not versioned by guesswork.** `Document.Version` is written into every file and
  `Document.Load` refuses a version it does not know rather than reading it optimistically.
