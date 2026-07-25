# Standard components ("smart" hardware)

A catalogue component is more than geometry: **placing it modifies the host model and
assembles itself**. `ComponentAssembly.Place` does both jobs in one call — it cuts what
the component needs out of the host (clearance hole, counterbore, insert pilot, reamed
hole) and adds the component to the assembly at the frame it seats in.

```csharp render:smart-fasteners section:y,0
// A plate, two ISO 4762 cap screws and a Tappex Trisert insert. Sectioned at y = 0 so
// you can see what each Place() cut: the counterbores with the heads seated in them,
// the clearance bores through, and the insert flush in its pilot. The fasteners
// themselves stand whole - hardware is drawn unsectioned, as drafting practice requires.
var top = SketchPlane.At((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);   // Box(70, 44, 12) top

var build = new ComponentAssembly("plate", Shape.Box(70, 44, 12), Palette.Sage);
build.Place(StandardComponents.CapScrew(5, 16), [new(-24, 0), new(24, 0)], top);
build.Place(StandardComponents.TrisertInsert(5), [new(0, 0)], top);

var scene = new Scene();
scene.AddTab("hardware").Add(build.ToAssembly("bracket"));
```

![Two cap screws seated in counterbores and a threaded insert, plate sectioned](images/smart-fasteners.png)

A component carries its own parametric body, a seating convention, and a **host
preparation**. The catalogue is deliberately small and correct rather than broad:

| Component | Host preparation | Body (v1 fidelity) |
| --- | --- | --- |
| `StandardComponents.CapScrew(size, length, seating, fit)` — ISO 4762 | ISO 273 clearance hole plus the DIN 974 counterbore when `ScrewSeating.Counterbored` (the default); as an anchor, the coarse tap-drill pilot plus two pitches of runout | head cylinder (dk, k = d) on a plain shank, one exact revolve — no hex socket, no modeled thread (use `Shape.ExternalThread`) |
| `StandardComponents.TrisertInsert(size)` — Tappex Trisert® | the catalogue pilot bore at `StandardHoles.TrisertMinimumDepth` | plain sleeve bored to the thread's minor diameter |
| `StandardComponents.Dowel(diameter, length, inserted)` — ISO 2338 m6 | reamed hole at the **nominal** diameter (both bodies of a stack) | cylinder with 45° end chamfers |

⚠ ISO 4762 head diameters and the Trisert table are transcribed, not derived — verify
against your supplier's datasheet before production use.

## Preparation is a feature

`Place` appends a `ComponentFeature` to a `FeatureHistory`, so placements regenerate,
cache and suppress like any other step. **Suppressing a placement removes its bore from
the host and its occurrence from the assembly** — one switch, both halves:

```csharp run:smart-fastener-suppression
var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
build.Place(StandardComponents.CapScrew(4, 16, ScrewSeating.OnFace), [new(-20, 0)], top);
var insert = build.Place(StandardComponents.TrisertInsert(4), [new(0, 12)], top);

var withInsert = build.ToAssembly();
double drilled = build.Host!.GetMesh().Volume();

build.Suppress(insert);                        // one switch...
var withoutInsert = build.ToAssembly();
double undrilled = build.Host!.GetMesh().Volume();

// ...removes the occurrence AND the bore it cut.
if (withInsert.Occurrences.Count != 3 || withoutInsert.Occurrences.Count != 2)
    throw new Exception("suppressing a placement must drop its occurrence");
if (undrilled - drilled < 100)
    throw new Exception("suppressing a placement must remove its bore too");
```

Leave the face argument out and the component seats on the body's top face, re-resolved
on every regeneration: change an upstream thickness parameter and the fasteners move
with the face they sit on, their holes re-cut through the new body.

## The full fastener stack

`PlaceThrough` prepares **both** bodies from one call — clearance (and counterbore) in
the near body, the threaded engagement in the far one:

```csharp render:fastener-stack section:y,0
var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
var mateFace  = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);

var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5), Palette.Sage);
var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10), Palette.Slate);

cover.PlaceThrough(StandardComponents.CapScrew(5, 16),
                   [new(-20, 0), new(20, 0)], coverFace, basePlate, mateFace);

var scene = new Scene();
var tab = scene.AddTab("stack");
tab.Add(cover.ToAssembly("cover"));
tab.Add(basePlate.ToAssembly("base"));
```

![Cover bolted to a base: counterbored clearance holes above, blind tapped pilots below](images/fastener-stack.png)

The engagement is geometric, not guessed: the grip is the distance from the screw's
seating datum down to the anchor face, and what is left of its length engages the far
body (plus two pitches of tap runout).

```csharp run:fastener-stack-numbers
var coverFace = SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);
var mateFace  = SketchPlane.At((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
var cover = new ComponentAssembly("cover", Shape.Box(60, 40, 10).Translate(0, 0, 5));
var basePlate = new ComponentAssembly("base", Shape.Box(60, 40, 20).Translate(0, 0, -10));
cover.PlaceThrough(StandardComponents.CapScrew(5, 16), [new(-20, 0)], coverFace, basePlate, mateFace);
cover.ToAssembly();
basePlate.ToAssembly();

// Near body: Ø5.5 clearance under a Ø10 counterbore. Far body: the M5 coarse
// tap-drill pilot, Ø4.2, blind at 13.1 deep.
double clearance = cover.History.Result!.ToImplicit().Evaluate((-20, 0, 4));
double pilot = basePlate.History.Result!.ToImplicit().Evaluate((-20, 0, -2));
if (Math.Abs(clearance - 2.75) > 1e-9 || Math.Abs(pilot - 2.1) > 1e-9)
    throw new Exception($"expected the M5 clearance and tap-drill radii, got {clearance} and {pilot}");
```

Placement points are projected onto the anchor face along the fastener axis, so its 2D
axes need not match; non-parallel faces, an anchor above the seat and a component too
short to reach are rejected with the numbers in the message. The screw is placed
**once**, on the near body. To anchor into an insert rather than a tapped hole, place
the insert on the far body itself and use `Place` on the near one.
