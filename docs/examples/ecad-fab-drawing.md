---
title: "The PCB fabrication drawing"
---

The [Gerbers and the Excellon drill program](ecad-fabrication.md) are what a board house
*machines* from; the **fabrication drawing** is what a board house *reads*. It is the human-
readable sheet that sits beside the fab data: the board outline at a fitted scale, a **drill
map** with a symbol at every drilled feature, a **drill table** grouping the board's holes and
vias by size, a **layer stackup** table, and a **fabrication notes** block — all on the shared
engineering frame a mechanical drawing uses.

## The one rule: it reads the board, it never edits it

A `PcbFabricationSheet` derives everything from the layout's own public read surface — the board
outline, its holes, the placed vias, the stackup — so it cannot disagree with the board it
documents (the ECAD one-declaration rule, applied to a drawing). The drill table is the sharpest
form of that: its rows **partition** the board's holes and vias exactly, so `Σ count` equals the
number of drilled features and adding a hole adds exactly one to its row. That is a closed-form
oracle, not a picture.

## The shared frame — the third consumer

A fabrication drawing is an engineering drawing, so its border and title block come from the same
[`DrawingFrame`](drawings.md) the [mechanical drawing sheet](drawings.md) and the
[schematic sheet](ecad-schematic-sheet.md) use — the three-band `EngineeringTitleBlock` on the
`SheetLayers`. Given the same paper and the same title-block fields, `sheet.Frame().Compute()`
is **byte-identical** to a mechanical `DrawingSheet`'s frame. That is the payoff of one shared
frame: a drawing and its fab drawing of one board cannot draw different furniture.

## A drill map, a stackup and notes

A four-layer board with two mounting holes and a couple of via sizes, drawn on A3:

```csharp svg:ecad-fab-drawing
// A 4-layer board (60 x 40) with two Ø3.2 mounting holes and one Ø0.6 board via.
var board = new PcbBoard(
    [
        new Vector2d(-30, -20), new Vector2d(30, -20),
        new Vector2d(30, 20), new Vector2d(-30, 20),
    ],
    LayerStackup.FourLayer(copper: 0.035, prepreg: 0.2, core: 1.13),
    holes:
    [
        new BoardHole(new Vector2d(-26, -16), 3.2, BoardHoleKind.Mounting),
        new BoardHole(new Vector2d(26, 16), 3.2, BoardHoleKind.Mounting),
        new BoardHole(new Vector2d(0, 15), 0.6, BoardHoleKind.Via),
    ]);

var layout = new PcbLayout(new Schematic("regulator"), board);
// Placed vias in two sizes — they join the drill table with the board's own holes.
layout.AddVia("GND", 8, 6, "Top", "Bottom", drill: 0.3, pad: 0.6);
layout.AddVia("GND", -8, 6, "Top", "Bottom", drill: 0.3, pad: 0.6);
layout.AddVia("GND", 0, -6, "Top", "Bottom", drill: 0.3, pad: 0.6);
layout.AddVia("VIN", 12, -10, "Top", "Bottom", drill: 0.4, pad: 0.8);

// The fabrication drawing — the shared engineering frame plus the fab content.
var title = new TitleBlock
{
    Title = "REGULATOR PCB", DrawingNumber = "PCB-042", Author = "EngrCAD",
    Revision = "A", Company = "ACME ELECTRONICS",
};
var sheet = new PcbFabricationSheet(layout, SheetFormat.A3, title);
var drawing = sheet.Compute();

// The drill table partitions the board's holes and vias by size:
//   Ø0.3 PTH x3, Ø0.4 PTH x1, Ø0.6 PTH x1, Ø3.2 NPTH x2  — seven features, four sizes.
var svg = drawing.ToSvg();   // also drawing.ToDxf(), drawing.ToPdf()
```

![The regulator PCB's fabrication drawing.](images/ecad-fab-drawing.svg)

The drill table's counts and diameters are the board's own — the drawing is a *view* of the
layout's drilled features, so it cannot omit a hole nor invent one. The same `Compute()` feeds
the SVG, DXF and PDF writers, so the three cannot disagree about the drawing.
