# DXF & SVG (2D interchange)

Two-dimensional geometry leaves and enters EngrCAD through two writers and one
reader, all dependency-free:

- **DXF** (`DxfDocument`) — the interchange seam with drafting packages and
  laser/plasma/router CAM: LINE, ARC, CIRCLE and LWPOLYLINE entities with layers,
  both directions.
- **SVG** (`SvgDrawing`) — drawings for documentation and browsers, with **line-type
  control driven by edge classification** (visible / hidden / section — the thing
  that makes an exported drawing usable rather than a flat soup of curves).

## DXF out, DXF in

A sketch's lines and arcs are written **exactly**: an LWPOLYLINE vertex carries a
"bulge" (tan of a quarter of the arc's sweep), which encodes a circular arc with no
flattening, and a full-circle loop becomes a CIRCLE entity. Cubic béziers are the one
lossy mapping (DXF polylines have no cubic form) and are flattened at a stated chord
tolerance. This snippet round-trips on every docs build and checks the exact area
survived:

```csharp run:dxf-roundtrip
var plate = Sketch.RoundedRectangle(40, 24, 6);

var dxf = new DxfDocument();
dxf.Add(plate, layer: "outline");
dxf.Add(Sketch.Circle((-12, 0), 3), layer: "holes");
dxf.Add(Sketch.Circle((12, 0), 3), layer: "holes");
dxf.SaveFile(Path.Combine(Scratch, "plate.dxf"));

var loaded = DxfDocument.LoadFile(Path.Combine(Scratch, "plate.dxf"));
var sketches = loaded.ToSketches(out var diagnostics);
if (diagnostics.Count != 0) throw new Exception(string.Join("; ", diagnostics));
if (sketches.Count != 3) throw new Exception($"expected 3 loops, got {sketches.Count}");

// The arc encoding is exact, so the area survives to full precision.
double error = Math.Abs(sketches[0].Area() - plate.Area());
if (error > 1e-9) throw new Exception($"area drifted by {error:e2}");
```

Reading is diagnostic, never throwing: unknown entities are skipped and counted,
loose LINE/ARC entities are chained end-to-end into closed loops at the weld
tolerance, and anything that does not close is *reported*, not invented. An imported
sketch is a first-class `Sketch` — extrude it, revolve it, use it as a hole.

```csharp run:dxf-import-extrude
var dxf = new DxfDocument();
dxf.Add(Sketch.Slot(30, 10), layer: "profile");
dxf.SaveFile(Path.Combine(Scratch, "slot.dxf"));

var profile = DxfDocument.LoadFile(Path.Combine(Scratch, "slot.dxf")).ToSketches().Single();
var solid = Shape.Extrude(profile, 8);
if (!solid.ToMesh().IsClosed) throw new Exception("imported profile should extrude clean");
```

## SVG with drawing conventions

`SvgDrawing` takes the output of [2D views](2d-views.md) — `Shape.Section` (regions
of the cut) and `Shape.Silhouette` (the projected outline) — plus exact sketches
(arcs as SVG `A` commands, cubics as `C`, nothing flattened). Each add states its
**line class**; each (layer, class) pair becomes an SVG group with the ISO 128-style
preset — visible solid, hidden dashed, section dash-dot — that a downstream editor
or CAM can toggle wholesale:

```csharp run:svg-drawing
var housing = Shape.Box(40, 30, 20) - Shape.Cylinder(8, 50);

var top = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
var drawing = new SvgDrawing();
drawing.Add(housing.Silhouette(top), SvgLineClass.Visible, layer: "outline");
drawing.Add(housing.Section(top), SvgLineClass.Section, layer: "cut");
drawing.SaveFile(Path.Combine(Scratch, "housing.svg"));

var text = File.ReadAllText(Path.Combine(Scratch, "housing.svg"));
if (!text.Contains("stroke-dasharray")) throw new Exception("section lines should be dash-dot");
if (!text.Contains("scale(1,-1)")) throw new Exception("model space is y-up; the flip must be there");
```

The drawing is emitted in model millimetres (1 SVG user unit = 1 mm, viewBox sized
from the content), with one root `scale(1,-1)` handling SVG's y-down convention so
every coordinate in the file matches the model's.
