# Drawings (hidden lines, sheets, dimensions)

A model becomes a **drawing** in three steps, and EngrCAD owns all three:

1. **Hidden-line removal** (`HiddenLineRemoval`) projects a scene's edges into a view
   plane and classifies each piece *visible* or *hidden*.
2. A **sheet** (`DrawingSheet`) places those views on paper with a border and a title
   block, at a standard scale, in first- or third-angle projection.
3. **Export** writes the sheet as SVG or DXF, with every line class on its own layer,
   so the file opens in a drafting package looking the way a drawing should.

The result is a document you can send to a machinist.

## A three-view sheet

`DrawingSheet.StandardLayout` builds the classic set — front, top, right and an
isometric — at one shared scale, chosen as the largest [ISO 5455](https://www.iso.org/standard/11418.html)
ratio that fits the paper. The three orthographic directions come from
`StandardViews`, which is the **same table the viewer's Front/Top/Right buttons read**,
so a drawing's FRONT and the model on screen can never disagree.

```csharp svg:drawing-sheet
var top = SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);
var bracket = Shape.Box(90, 60, 12)
    .Drill(HoleSpec.Simple(9),
        [new Vector2d(-32, -20), new Vector2d(32, -20),
         new Vector2d(-32, 20), new Vector2d(32, 20)], depth: 14, top)
    .Drill(HoleSpec.Counterbore(6.6, 11, 6.8), [new Vector2d(0, 0)], depth: 14, top);

var scene = new Scene();
scene.Add(new Part("bracket", bracket));

var sheet = DrawingSheet.StandardLayout(scene, SheetFormat.A3);
sheet.Title = sheet.Title with
{
    Title = "MOUNTING BRACKET",
    DrawingNumber = "EC-1042",
    Material = "AL 6082-T6",
    Author = "EngrCAD",
    Date = "2026-07-29",
    Revision = "A",
    Company = "ENGRCAD",
};

// Dimensions are placed in the VIEW's own projected coordinates, so they measure the
// part; their arrowheads and lettering are sized in sheet millimetres, so they stay
// printable whatever scale the layout chose.
var front = sheet.Views[0];
front.Annotate(SheetLinearDimension.Horizontal((-45, -6), (45, -6), -14));
front.Annotate(SheetLinearDimension.Vertical((-45, -6), (-45, 6), 14));

// A positive standoff sits to the LEFT of a->b, so the sign says which side.
var plan = sheet.Views[1];
plan.Annotate(SheetLinearDimension.Horizontal((-32, 20), (32, 20), 12));
plan.Annotate(SheetLinearDimension.Vertical((32, -20), (32, 20), -18));
plan.Annotate(SheetRadialDimension.Diameter((-32, 20), 4.5, 135));
plan.Annotate(new SheetNote((0, 0), (18, -34), "M6 CBORE\nFROM THIS FACE"));

var svg = sheet.ToSvg();
```

![A three-view drawing sheet of a mounting bracket](images/drawing-sheet.svg)

Hidden detail comes out dashed automatically — the four bolt holes and the counterbore
are dashed in the front and right views because the probe found material in front of
them, not because anyone said so.

## Section views

A **section view** is an ordinary view with a depth: `SectionThrough` removes
everything nearer the viewer than that point, and the faces the cut lays open are drawn
and hatched.

The cutting plane is perpendicular to the view direction **by construction**, which is
not a limitation but the definition — that is what makes the exposed faces project in
true shape, and therefore worth hatching and dimensioning. (For an oblique cut, take a
view along the oblique normal.)

```csharp svg:drawing-section
var top = SketchPlane.At((0, 0, 9), Vector3d.UnitX, Vector3d.UnitY);
var housing = Shape.Cylinder(30, 18).Translate(0, 0, 9)
    .Drill(HoleSpec.Counterbore(8, 15, 6), [new Vector2d(0, 0)], depth: 20, top);

var part = new Part("housing", housing);
var sheet = new DrawingSheet(SheetFormat.A4);

var plan = new DrawingView(part, StandardViews.DirectionFor("top")!.Value, "TOP")
{
    Scale = 1,
    Center = (90, 140),
};
plan.Annotate(SheetRadialDimension.Diameter((0, 0), 30, 130));
plan.Annotate(SheetRadialDimension.Diameter((0, 0), 7.5, 310));

var section = new DrawingView(part, StandardViews.DirectionFor("front")!.Value, "SECTION A-A")
{
    Scale = 1,
    Center = (215, 140),
    SectionThrough = (0, 0, 0),   // cut on the axis, keep the far half
};
section.Annotate(new SheetLinearDimension((30, 0), (30, 18), -14));
section.Annotate(SheetLinearDimension.Horizontal((-30, 0), (30, 0), -16));

sheet.Add(plan).Add(section);
sheet.Title = sheet.Title with { Title = "BEARING HOUSING", DrawingNumber = "EC-1043" };

var svg = sheet.ToSvg();
```

![A plan view and a hatched section view of a bearing housing](images/drawing-section.svg)

The hatch is clipped **exactly** to the cut regions by an even-odd scan, so it stops at
the bore rather than crossing it, and every cut face on a sheet shares one continuous
45-degree pattern (hatch lines are anchored to the origin, not to each region's own
bounds).

## Exporting a sheet

Both writers consume the same `DrawingSheet.Compute()` result, so they cannot disagree
about what a drawing looks like — they differ only in how a polyline, a dash pattern and
a piece of text are spelled.

| Class | Layer | SVG | DXF |
| --- | --- | --- | --- |
| Visible edge | `visible` | solid, wide | CONTINUOUS |
| Hidden edge | `hidden` | dashed, narrow | HIDDEN |
| Cut boundary | `section` | solid | CENTER |
| Hatch | `hatch` | narrow solid | CONTINUOUS |
| Dimensions | `dimensions` | narrow solid | CONTINUOUS |
| Border, title block | `border`, `titleblock` | narrow solid | CONTINUOUS |

A DXF that *names* a line type must also **define** it, or every reader falls back to
solid lines and the classification is lost in transit — so the writer emits an LTYPE
table for every pattern its layers use. This round-trips the whole sheet through the
landed reader on every docs build:

```csharp run:drawing-dxf
var plate = new Part("plate", Shape.Box(60, 40, 12)
    .Drill(HoleSpec.Simple(10), [new Vector2d(0, 0)], depth: 14,
        SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY)));

var sheet = DrawingSheet.StandardLayout(plate, SheetFormat.A4);
sheet.Title = sheet.Title with { Title = "PLATE", DrawingNumber = "EC-0001" };
sheet.SaveDxf(Path.Combine(Scratch, "plate-drawing.dxf"));

var loaded = DxfDocument.LoadFile(Path.Combine(Scratch, "plate-drawing.dxf"));
if (loaded.Diagnostics.Count != 0)
    throw new Exception(string.Join("; ", loaded.Diagnostics));
if (!loaded.Layers.Contains(SheetLayers.Hidden))
    throw new Exception("hidden detail lost its layer");

var titles = loaded.Entities.OfType<DxfText>().Select(t => t.Value).ToList();
if (!titles.Contains("PLATE") || !titles.Contains("EC-0001"))
    throw new Exception("the title block did not survive the round trip");
```

`sheet.SaveSvg(path)` and `sheet.SaveDxf(path)` write files; `sheet.ToSvg()` and
`sheet.ToDxf()` hand back the document if you would rather post-process it.

## What the projection is honest about

The line work is **exact wherever the kernel has it**: for a B-Rep-backed part the
edges are sampled from the actual edge curves at display resolution, so a bore rim is a
smooth circle however coarse the mesh. Two things are not exact, and the output says so
via `HiddenLineRun.Source`:

- **The outline of a smooth surface** (`EdgeSource.Silhouette`). A cylinder seen from
  the side has no modelled edge along its outline, so one is taken from the display
  mesh's view-dependent silhouette. It is faceted at mesh resolution. True silhouette
  curves on curved surfaces are the known upgrade.
- **Visibility itself.** The question "is there material in front of this point" is
  answered against the display mesh, so a coarser mesh means a coarser dash boundary.
  It is refined by bisection to `HiddenLineOptions.SplitFraction` (1e-5 of the model's
  extent by default), which is far finer than a drawn line is wide.

Everything else is decided exactly. The first stage of the visibility test reads the
point's **own** surface: if every face around it points away from the viewer, it is
buried in its own material and is hidden with no ray cast at all — which settles most
of a solid's edges, for free and without touching a mesh.

```csharp run:drawing-hlr
var part = new Part("box", Shape.Box(40, 20, 10));
var iso = StandardViews.SheetFrame(StandardViews.DirectionFor("iso")!.Value);
var result = HiddenLineRemoval.Project(part, iso);

// Seen from a corner, exactly the three edges meeting at the FAR corner are hidden;
// an axis-parallel edge of length L projects to L*sqrt(2/3) from the iso direction.
double expected = (40 + 20 + 10) * Math.Sqrt(2.0 / 3);
double hidden = result.Hidden.Sum(r => r.Length);
if (Math.Abs(hidden - expected) > 0.2)
    throw new Exception($"expected about {expected:F2} of dashed line, got {hidden:F2}");
if (result.Runs.Any(r => r.Source == EdgeSource.Silhouette))
    throw new Exception("a box has no smooth surface to silhouette");
```

## Options

`HiddenLineOptions` is scale-free: every length is a **fraction of the projected
geometry's extent**, so a drawing of a 4 mm dowel and a drawing of a 4 m beam behave
the same.

| Option | Default | What it does |
| --- | --- | --- |
| `BiasFraction` | 1e-3 | How far a probe steps off the surface before casting. Must exceed the tessellation's chord error; raise it for a deliberately coarse mesh. |
| `SampleFraction` | 1/200 | Spacing of visibility samples along an edge. |
| `SplitFraction` | 1e-5 | How precisely a dash boundary is located by bisection. |
| `MinimumRunFraction` | 2e-3 | Runs shorter than this are absorbed into their neighbour — the "shorter than a pen stroke" rule. |
| `IncludeSilhouette` | true | Draw the mesh-derived outline of smooth surfaces. |
| `IncludeHidden` | true | Emit hidden runs at all. |
| `SectionThrough` | null | Cut everything nearer the viewer than this point. |

Parts follow the same visibility rules an export does: a `Hidden` part contributes
nothing (not even occlusion), a `Ghost` part is left off (a translucent line means
nothing on paper), and a part with `ClippedBySection = false` passes through a section
whole — the drafting convention that shafts, bolts, nuts, keys and pins are drawn
unsectioned.

## Related

- [2D views](2d-views.md) — `Shape.Section` and `Shape.Silhouette`, the region-level
  projections a sheet's cut faces are built from.
- [DXF & SVG](dxf-svg.md) — the 2D interchange layer a sheet writes through.
- [3D annotations (PMI)](annotations.md) — the same dimension anatomy attached to the
  model itself rather than to a sheet.
