# 2D views: offset, section, silhouette

Three operations turn a model back into 2D — and one primitive that only exists
because a 2D profile swept in x is the shape you keep reaching for.

## Offsetting a sketch

`Sketch.Offset(delta, join)` grows (positive) or shrinks (negative) a sketch by a
constant distance: clearance fits, wall shells, pocket stock, cutter compensation.
Corners are closed by the join style — `Round`, `Miter` (degrading to a chamfer past
the miter limit) or `Chamfer`:

```csharp render:sketch-offset
var plate = Sketch.Start(-18, -10).LineTo(18, -10).LineTo(18, 4)
                  .LineTo(4, 4).LineTo(4, 10).LineTo(-18, 10).Close();

Shape Extruded(IReadOnlyList<Region2d> regions, double height)
{
    var (outer, holes) = Profile.FromRegion(regions[0]);
    return Shape.Extrude(outer, Vector3d.UnitZ * height, holes);
}

var scene = new Scene();
scene.Add(new Part("as drawn", Extruded(plate.ToRegions(), 2), Palette.Steel));
scene.Add(new Part("grown 3 (round)", Extruded(plate.Offset(3), 1), Palette.Brass,
    Matrix4d.CreateTranslation((0, 0, -3))));
scene.Add(new Part("shrunk 2", Extruded(plate.Offset(-2), 1), Palette.Coral,
    Matrix4d.CreateTranslation((0, 0, 3))));
```

![A stepped plate with a grown outline below it and a shrunk one above](images/sketch-offset.png)

Offsetting is one algorithm, not two: the outward offset is the region unioned with a
slab per edge and a join per corner, and the **inward** offset is that same dilation
applied to the complement. Self-intersection therefore falls out rather than needing a
cleanup pass — shrink a plate through a narrow neck and you get two regions back, or
none at all if it vanishes. That is why `Offset` returns a *list*.

```csharp run:sketch-offset-splits
// A dumbbell: two 10-wide pads joined by a 3-wide neck. Shrink past the neck and the
// one region becomes two; shrink past the pads and nothing is left.
var bar = Sketch.Start(-15, -5).LineTo(-5, -5).LineTo(-5, -1.5).LineTo(5, -1.5)
                .LineTo(5, -5).LineTo(15, -5).LineTo(15, 5).LineTo(5, 5)
                .LineTo(5, 1.5).LineTo(-5, 1.5).LineTo(-5, 5).LineTo(-15, 5).Close();

if (bar.Offset(-1).Count != 1) throw new Exception("still one piece at -1");
if (bar.Offset(-2).Count != 2) throw new Exception("the 3-wide neck should part at -2");
if (bar.Offset(-6).Count != 0) throw new Exception("nothing should survive -6");
```

## Section: `projection(cut = true)`

`Shape.Section(plane)` is the cross-section — the drawing-view section, and OpenSCAD's
`projection(cut = true)`. Cavities become holes automatically, including one with no
opening to the outside at all:

```csharp render:planar-section
// Two through-bores and a SEALED internal cavity: a cylinder short enough that it never
// reaches an end face, so the solid has a void with no opening at all.
var block = Shape.Box(40, 24, 16)
    .Drill(StandardHoles.Clearance(6), [new(-15, 0), new(15, 0)], depth: 20,
           SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY))
    - Shape.Cylinder(4, 20).Rotate(Vector3d.UnitY, Math.PI / 2);

// Slice at z = 0 and re-extrude the section as a thin plate to show what came back.
var slice = block.Section(SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY));

var scene = new Scene();
scene.Add(new Part("block", block, Palette.Slate, Matrix4d.CreateTranslation((0, 26, 0))));
foreach (var (region, i) in slice.Select((r, i) => (r, i)))
{
    var (outer, holes) = Profile.FromRegion(region);
    scene.Add(new Part($"section {i}", Shape.Extrude(outer, Vector3d.UnitZ * 1.5, holes),
        Palette.Brass, Matrix4d.CreateTranslation((0, -14, 0))));
}
```

![A drilled block with a sealed cavity, above the thin plate cut from its mid-plane](images/planar-section.png)

When the shape lowers to B-Rep the section is taken from the **exact** surfaces, so its
fidelity is set by the chord tolerance alone — a bore rim is as round as you ask for,
not as round as the display mesh happens to be. Shapes with no B-Rep form fall back to
the mesh.

A plane flush with a face, or containing an edge, is refused: a section that runs
*along* a face is an area, not a curve. Move the plane a fraction.

## Silhouette: `projection(cut = false)`

`Shape.Silhouette(plane)` is the outline the shape casts along the plane's normal — the
shadow, not the slice. A through hole survives as a hole; a blind pocket does not,
because there is material in front of it:

```csharp render:planar-silhouette
var bracket = Shape.Box(36, 20, 8)
    .Drill(StandardHoles.Clearance(5), [new(-13, 0), new(13, 0)], depth: 12,
           SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY))
    | Shape.Cylinder(7, 18).Translate(0, 0, 9);

var scene = new Scene();
scene.Add(new Part("bracket", bracket, Palette.Sage, Matrix4d.CreateTranslation((0, 34, 0))));

void AddOutline(string name, SketchPlane plane, PartColor color, Vector3d at)
{
    foreach (var (region, i) in bracket.Silhouette(plane).Select((r, i) => (r, i)))
    {
        var (outer, holes) = Profile.FromRegion(region);
        scene.Add(new Part($"{name} {i}", Shape.Extrude(outer, Vector3d.UnitZ * 1, holes),
            color, Matrix4d.CreateTranslation(at)));
    }
}

AddOutline("top", SketchPlane.XY, Palette.Steel, (0, 6, 0));    // bores survive
AddOutline("front", SketchPlane.XZ, Palette.Coral, (0, -18, 0)); // boss appears, bores do not
```

![A bracket with a boss, its top outline keeping both bores as holes, and its front outline a solid T](images/planar-silhouette.png)

The two views say different things, and both are right. Looking **down**, the bores go
all the way through, so they survive as holes and the boss adds nothing (it sits inside
the plate's footprint). Looking from the **front**, the boss becomes the stem of a T —
and the bores vanish, because along that direction there is material in front of them.

A silhouette is the union of every front-facing projected triangle, so it is a *mesh*
result — its fidelity is the mesh's, and a finer mesh costs more union work. The unions
are folded through a balanced tree over Morton-sorted faces, which is not a
micro-optimization: on a 64-segment torus that is 67 ms against 2.4 s for the same tree
unsorted and 259 s accumulated linearly. Merging face 1 with face 900 produces two
disjoint regions and cancels nothing.

## The wedge primitive

`Shape.Wedge(sizeX, sizeY, sizeZ, topX, topOffsetX)` is OCCT's `BRepPrimAPI_MakeWedge`
and the last primitive OpenSCAD reaches for `polyhedron` to build. The base is
`sizeX × sizeY`; the top keeps the same y but is `topX` wide, centred at `topOffsetX`:

```csharp render:wedge
var scene = new Scene();
scene.Add(new Part("chisel", Shape.Wedge(16, 12, 10), Palette.Steel,
    Matrix4d.CreateTranslation((-22, 0, 0))));
scene.Add(new Part("ramp", Shape.Wedge(16, 12, 10, topX: 0, topOffsetX: 8), Palette.Brass));
scene.Add(new Part("dovetail", Shape.Wedge(16, 12, 10, topX: 24), Palette.Sage,
    Matrix4d.CreateTranslation((24, 0, 0))));
```

![A symmetric chisel, a right-triangular ramp, and a dovetail rail](images/wedge.png)

`topX: 0` gives a sharp top edge; moving it over one side with `topOffsetX: ±sizeX/2`
gives the classic ramp (a right triangular prism); a `topX` larger than `sizeX` gives a
dovetail rail. The taper is in x only — a solid tapering in *both* directions is a loft,
not a wedge.

A wedge **is** an extrusion (a trapezoidal cross-section swept along y), so it carries
one internally and every lowering delegates to it. That is why it is native in all three
representations for free, and exact under any affine transform.

## Exactness

Everything on this page except `Section` on a B-Rep shape flattens curves to polylines
first: the 2D arrangement the boolean runs on carries segments, not arcs. So a circle
offset outward by `d` lands just *inside* π(r+d)² — round joins are inscribed arcs,
matching `Sketch.ToRegions`'s one-sided contract, so errors never accumulate in the
unsafe direction. Exact curved 2D booleans are open work.
