# DXF & SVG (2D interchange)

Two-dimensional geometry leaves and enters EngrCAD through two writers and one
reader, all dependency-free:

- **DXF** (`DxfDocument`) — the interchange seam with drafting packages and
  laser/plasma/router CAM: LINE, ARC, CIRCLE, LWPOLYLINE, SPLINE and TEXT entities with
  layers, both directions.
- **SVG** (`SvgDrawing`) — drawings for documentation and browsers, with **line-type
  control driven by edge classification** (visible / hidden / section — the thing
  that makes an exported drawing usable rather than a flat soup of curves).

## DXF out, DXF in

A sketch's lines and arcs are written **exactly**: an LWPOLYLINE vertex carries a
"bulge" (tan of a quarter of the arc's sweep), which encodes a circular arc with no
flattening, and a full-circle loop becomes a CIRCLE entity. Cubic béziers flatten at a
stated chord tolerance by default, or become exact SPLINE entities — see
[below](#cubics-exactly-spline-entities). This snippet round-trips on every docs build
and checks the exact area survived:

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

## Cubics, exactly: SPLINE entities

A cubic Bézier **is** a clamped degree-3 B-spline with four control points, so DXF's
SPLINE entity carries one with nothing approximated. `DxfCurveMode.Spline` uses it:

```csharp run:dxf-spline
var wave = Sketch.Start(0, 0)
    .BezierTo((3, 4), (7, -4), (10, 0))
    .LineTo(10, -3).LineTo(0, -3).Close();

var dxf = new DxfDocument();
dxf.Add(wave, layer: "profile", curves: DxfCurveMode.Spline);
dxf.SaveFile(Path.Combine(Scratch, "wave.dxf"));

var back = DxfDocument.LoadFile(Path.Combine(Scratch, "wave.dxf")).ToSketches().Single();

// Exact, not "within a chord tolerance": the area survives to full precision.
double error = Math.Abs(back.Area() - wave.Area());
if (error > 1e-9) throw new Exception($"area drifted by {error:e2}");
```

The cost is structural rather than numerical, and it is worth knowing before you choose:
a loop containing a cubic arrives as a **chain** of entities (LWPOLYLINE runs of lines
and arcs, one SPLINE per cubic) instead of one closed polyline, because DXF's polyline
vocabulary has no cubic vertex. `ToSketches` chains it back by endpoint, so the round
trip is exact either way — but a CAM post that expects one closed polyline per part will
prefer the default. **A sketch with no cubics writes byte-for-byte the same file under
either mode**, so the choice only ever affects Béziers.

Reading is narrower than writing, deliberately. A degree-1 spline is a polyline, and a
non-rational cubic whose interior knots all have multiplicity 3 is *already* a chain of
Bézier segments, so its control points split four at a time with nothing computed — that
covers what this writer emits and what a polybezier exporter emits. A **rational** spline
has no polynomial cubic form, and a general B-spline needs knot-insertion Bézier
decomposition; both are **reported by name** rather than sampled, because a sketch that
silently carried a flattened curve would make every downstream "exact" claim false.

## Units are declared, and honoured

Every file this writer produces states `$INSUNITS` in its header — millimetres by
default, `DxfDocument.Units` to change it. That is the same duty the LTYPE table has:
**a file that does not say what its numbers mean leaves every reader to guess**, and a
laser cutter that guesses inches for millimetres ruins a sheet.

On load the declaration is honoured rather than merely reported: an inch file's
coordinates are scaled into millimetres (`ModelUnits`' convention), the document comes
back labelled millimetres so re-saving it is correct, and a diagnostic names the original
unit and the factor. `Unitless` — the value a great many real files carry — is the file's
honest "no claim" and is never scaled; inventing a factor for it would be exactly the
silent mis-scaling this exists to prevent.

```csharp run:dxf-units
var inches = new DxfDocument { Units = DxfUnits.Inches };
inches.Add(Sketch.Circle((2, 0), 1));                       // a 1 inch radius at x = 2 in
inches.SaveFile(Path.Combine(Scratch, "imperial.dxf"));

var loaded = DxfDocument.LoadFile(Path.Combine(Scratch, "imperial.dxf"));
var circle = (DxfCircle)loaded.Entities.Single();
if (Math.Abs(circle.Radius - 25.4) > 1e-9) throw new Exception($"radius {circle.Radius}");
if (loaded.Units != DxfUnits.Millimetres) throw new Exception("should re-save as mm");
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
