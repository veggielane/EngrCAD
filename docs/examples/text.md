# Text

`Shape.Text` turns a font into real geometry — nameplates, part numbers, logos,
engraved labels. TrueType (`.ttf`) glyph outlines are **lines and quadratic Béziers**
and OpenType/CFF (`.otf`) outlines are **lines and cubic Béziers** — both exactly the
sketch vocabulary, so glyphs become sketches with no flattening: text is native in all
three representations, with exact profiles for B-Rep and crisp tessellation for printing.

## A nameplate

```csharp render:text-nameplate
string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
string fontPath = new[] { "arial.ttf", "segoeui.ttf", "verdana.ttf" }
    .Select(name => Path.Combine(fonts, name)).First(File.Exists);
var font = TrueTypeFont.Load(fontPath);

// Lettering runs along +Y so it reads left-to-right from the default iso view.
var top = SketchPlane.At((0, 0, 2), Vector3d.UnitY, -Vector3d.UnitX);
var lettering = Shape.Text("ENGRCAD", font,
                           size: font.EmSizeForCapHeight(9),   // 9 mm capitals
                           height: 1.2, top,
                           new TextStyle { Align = TextAlign.Center });

var scene = new Scene();
scene.Add(new Part("plate", Shape.Box(22, 70, 4), Palette.Steel));
scene.Add(new Part("lettering", lettering, Palette.Brass));
```

![Raised lettering reading ENGRCAD on a steel plate](images/text-nameplate.png)

## Sizing, origin, and layout

`size` is the **em size** — the typographic "12 point". Drawings usually specify letter
height instead, so `font.EmSizeForCapHeight(9)` converts: capitals come out exactly 9 mm.

The origin is the **baseline** at the start of the first line, x along the writing
direction and y up. `TextStyle` carries alignment (measured per line), tracking, and line
spacing as em multiples; `\n` starts a new line. Advance widths and legacy `kern` pair
kerning are applied automatically — `font.HasKerning` tells you whether a font supplies
them.

A character the font doesn't contain throws, naming the character and the font, rather
than silently dropping it.

## Counters are found, not assumed

The holes in **O**, **A**, and **8** — counters — are detected by containment: contours
are nested by an exact point-in-region test, even depth draws material and odd depth
becomes a hole, and an island inside a counter (the middle of a **䷀**-style glyph, or a
dot inside a ring) becomes its own outline again.

That is deliberate rather than trusting the format. TrueType specifies outer contours
clockwise and counters counter-clockwise, but real fonts routinely violate it, so
orientation is not a reliable signal — containment is.

## Fonts it reads

TrueType outlines: `head`, `maxp`, `cmap` (formats 4 and 12), `loca`, `glyf` (simple
**and** composite glyphs, so accented characters work), `hhea`/`hmtx`, plus optional
`kern` and `OS/2`. The reader is hand-rolled and dependency-free, like the PNG writer.

OpenType/CFF fonts (`.otf`, PostScript Type 2 charstrings — cubic Béziers) load the
same way, including CID-keyed fonts; `font.HasPostScriptOutlines` reports which flavour
you got. A nameplate in an `.otf` face is exactly the code above with a different path —
`TrueTypeFont.Load(@"C:\path\to\SourceSans3-Regular.otf")` — and the geometry is just as
exact, with cubic glyph walls becoming exact NURBS profiles in B-Rep.

TrueType Collections (`.ttc`) and variable-font `CFF2` tables are **rejected with a
clear message** rather than partially misread — support for them is future work.

## Engraving

Cutting lettering *into* a body is a plain subtraction, and it is exact — a whole word
lowers to a single-shell B-Rep whose volume is the plate minus the glyph section times
the pocket depth. Put the sketch plane at the pocket floor and let the tool overshoot
the face, the same rule `Shape.Drill` follows so booleans never meet coplanar faces:

```csharp run:text-engraved
string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
var font = TrueTypeFont.Load(new[] { "arial.ttf", "segoeui.ttf", "verdana.ttf" }
    .Select(name => Path.Combine(fonts, name)).First(File.Exists));
var style = new TextStyle { Align = TextAlign.Center };

var pocket = SketchPlane.At((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);   // face at z = 2
var engraved = Shape.Box(70, 22, 4)
             - Shape.Text("ENGRCAD", font, 12, height: 1.5, pocket, style);   // 1 mm deep

// Exact: the plate minus the glyph section times the 1 mm depth. The only error is
// the tessellator chording the curved glyph outlines.
double section = TextOutlines.Sketches("ENGRCAD", font, 12).Sum(s => s.Area());
var mesh = engraved.ToMesh();
if (!mesh.IsClosed || Math.Abs(mesh.Volume() - (70 * 22 * 4 - section)) > 0.05)
    throw new Exception($"engraving is off: {mesh.Volume()}");
```

Two things to know:

- **Keep the lettering inside the face.** A glyph that runs off an edge makes a cut chain
  crossing the boundary part-way, which the face splitter rejects — loudly.
- **Flush embossing does not fuse.** Text placed exactly on a face is a coplanar pair, so
  the union leaves the body and the glyphs as *touching* shells (right volume, several
  shells). Sink the lettering a fraction into the face — sketch plane 0.1 mm below,
  0.1 mm added to the height — and the boolean fuses into one shell.

Where the exact route refuses, the error message names the fallback:
`Shape.From(result.ToImplicit()).ToMesh(quality)`, which is exact as a field and always
available (raise `SdfResolution` for crisp lettering).
