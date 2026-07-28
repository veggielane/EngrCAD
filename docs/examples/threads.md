# Threads

EngrCAD models **real thread geometry** — helical ridges you can 3D-print and screw
together — not cosmetic thread annotations. `StandardThreads.Metric(size)` supplies
the ISO 261/262 coarse-pitch series **M2–M12** with the **ISO 68-1 basic profile**:
the 60° symmetric V whose dimensions all follow from the nominal diameter d and pitch
P via the fundamental triangle height H = (√3/2)·P — crest flat P/8 at the major
diameter, root flat P/4 at the minor diameter d − (5/4)·H, thread depth 5H/8. A
custom `ThreadSpec(nominalDiameter, pitch)` covers anything outside the catalog;
right-hand threads only.

`StandardThreads.Fine(size)` gives the first-choice ISO 261 **fine** pitch of the same
sizes, and `Metric(size, pitch)` names a second- or third-choice one:

```csharp
var m8 = StandardThreads.Metric(8);      // M8×1.25 coarse, tap drill 6.8
var m8f = StandardThreads.Fine(8);       // M8×1 fine,      tap drill 7.0
var m10 = StandardThreads.Metric(10, 0.75);                 // third-choice fine
var odd = new ThreadSpec(nominalDiameter: 7, pitch: 1.0);   // custom 60° thread

StandardThreads.Pitches(10);             // [1.5, 1.25, 1.0, 0.75] — coarse, then fine
StandardHoles.Tapped(m8f);               // the pilot for any spec, from its own tap drill
```

Fine tap drills are exactly `d − P`; the coarse chart rounds to a stock drill (6.8 for
M8, not 6.75), which is why only the coarse table stores a second column. ⚠ Verify the
pitch and tap-drill values against a current standard before production use.

## External threads

`Shape.ExternalThread(spec, length, clearance, chamferEnds)` builds a threaded stud
along +Z over z ∈ [0, length], with 45° lead-in chamfers down to the minor diameter
on both ends by default. A plain `double` first argument is shorthand for the
metric catalog:

```csharp render:thread-stud
// An M8×1.25 stud welded onto a plain boss. Threads mesh through Surface Nets, so
// give the scene enough SDF resolution for the ridges — the cell size is the longest
// scene axis divided by SdfResolution (about 0.1 mm here, well under the 1.25 pitch).
var stud = Shape.ExternalThread(8, length: 16);                 // z in [0, 16]
var boss = Shape.Cylinder(radius: 8, height: 6).Translate(0, 0, -3);

var scene = new Scene(new MeshQuality { SdfResolution = 220 });
scene.Add(new Part("M8 stud", boss | stud, Palette.Steel,
    Matrix4d.CreateTranslation((0, 0, 6))));
```

![An M8 stud with visible helical thread ridges on a cylindrical boss](images/thread-stud.png)

## Threaded holes

`ThreadedHole(spec, points, depth, plane?, clearance)` taps holes the way a machinist
does: at each 2D point it drills the tap pilot (`ThreadSpec.TapDrillDiameter`, via
[`Drill`](holes.md)) and then subtracts a modeled thread void, both along −normal to
`depth` below the plane. The pilot truncates the internal crests to the tap-drill
diameter — exactly what tapping a drilled hole does. Cutting the block in half shows
the internal thread profile (the viewer's [section mode](viewer.md) does this
interactively):

```csharp render:thread-hole
var top = SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);
var block = Shape.Box(16, 12, 10)   // printable: clearance keeps this on the SDF route
    .ThreadedHole(StandardThreads.Metric(6), [new(0, 0)], depth: 12, top, clearance: 0.15);

// Expose the internal thread by cutting away the front half through the hole axis.
// (A section cut slicing ALONG the thread axis is beyond the B-Rep boolean's
// perpendicular-plane support — the SDF route handles it exactly.)
var sectioned = block - Shape.Box(20, 14, 14).Translate(0, 7, 0);

var scene = new Scene(new MeshQuality { SdfResolution = 220 });
scene.Add(new Part("tapped block", sectioned, Palette.Brass,
    Matrix4d.CreateTranslation((0, 0, 5))));
```

![A half-sectioned block showing the internal thread profile of an M6 tapped hole](images/thread-hole.png)

## Printing clearance

A snug modeled thread pair has zero gap and will not assemble off an FDM printer.
The `clearance` parameter is the fit allowance, applied **normal to the flanks** (the
profile offsets perpendicular to its own boundary, so crests and roots move radially
by the same amount):

- an **external** thread *shrinks* by the clearance,
- an **internal** thread void *grows* by the clearance.

Both are derived from the same ISO 68-1 basic profile, so pairing the **same value**
on the stud and the hole splits the total gap evenly between them. Typical FDM
values are **0.1–0.25 mm** (start around 0.15 mm and tune for your printer); the
default is 0, and values at or beyond half the thread depth are rejected because
they would degenerate the profile:

```csharp run:thread-clearance
var quality = new MeshQuality { SdfResolution = 96 };
double snug = Shape.ExternalThread(8, length: 10).ToMesh(quality).Volume();
double printed = Shape.ExternalThread(8, length: 10, clearance: 0.2).ToMesh(quality).Volume();
if (printed >= snug)
    throw new Exception("clearance must shrink an external thread");

try
{
    // M8×1.25 thread depth is 5H/8 ≈ 0.68 mm; 0.4 exceeds the half-depth cap.
    Shape.ExternalThread(8, length: 10, clearance: 0.4);
    throw new Exception("expected the degenerate-profile guard to throw");
}
catch (ArgumentOutOfRangeException) { }
```

## Representation support (the honest part)

Threads are **implicit-native**: `Sdf.Thread` evaluates the helical profile with an
exact sign (and a documented approximate distance), so thread shapes compose with
every SDF operator, and chamfered or clearance-fitted threads mesh through Surface
Nets polygonization — the 3D printing route (`ToMesh()` → [STL export](exports.md)).

**External threads with the unmodified basic profile — zero clearance and
`chamferEnds: false` — are also B-Rep-native.** The entire lateral boundary is ONE
boolean-free helical sweep (`SolidFactory.MakeThreadedRod`): each facet of the
per-pitch profile — root flat, flank, crest flat, flank — sweeps to a single exact
`HelicalSurface` band wrapping *all* the turns, adjacent bands share exact `Helix3d`
rail edges, and the flat end caps are disks bounded by the spiral arcs the cap planes
cut from the bands. No core cylinder exists for a ridge to weld onto, so no tangent
seams and zero booleans. Any length works (no whole-turn constraint), and such
threads mesh through exact B-Rep tessellation with crisp helical edges:

```csharp render:thread-brep
// A B-Rep-native M8 stud: exact helical surfaces, tessellated with crisp rails —
// no SDF resolution concerns.
var stud = Shape.ExternalThread(8, length: 16, chamferEnds: false);

var scene = new Scene();
scene.Add(new Part("M8 B-Rep stud", stud, Palette.Steel));
```

![A B-Rep-native M8 threaded stud with crisp helical facet edges](images/thread-brep.png)

**Threaded holes are B-Rep-native too** (at zero clearance): the B-Rep path never
drills the pilot separately — the pilot bore wall and the thread tool's root band
would be coaxial (tangent, unsupported boolean input) — and instead subtracts ONE
combined tool per point, the internal thread form clipped at the pilot radius, whose
helical bands cross the drilled plane in exact spiral arcs chaining into a closed
loop:

```csharp run:thread-hole-brep
var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
var tapped = Shape.Box(20, 20, 8)
    .ThreadedHole(StandardThreads.Metric(8), [new(0, 0)], depth: 6, top);

var brep = tapped.ToBrep();                       // B-Rep-native threaded hole
if (!tapped.CanConvertTo(TargetRep.Brep))
    throw new Exception("zero-clearance threaded holes are B-Rep-native");
```

One boundary to know: downstream B-Rep booleans can cut a modeled thread only with
planes **perpendicular to its axis** (the exact spiral-arc case). A cut slicing
*along* the threads — like the sectioned illustration above — fails loudly in the
B-Rep kernel; give the design a clearance (printable parts want one anyway) or drop
to `ToImplicit()` and the SDF route handles it exactly.

`Explain` reports each case truthfully — Native for the basic profile, and a
per-cause Impossible otherwise (45° chamfer cones cutting helical bands are future
surface-intersection work; clearance offsets the profile as a distance field whose
rounded reflex corners have no exact B-Rep counterpart). Helical surfaces are not
STEP-exportable yet (same bucket as swept surfaces):

```csharp run:thread-explain
var plain = Shape.ExternalThread(8, length: 12, chamferEnds: false);
var brep = plain.ToBrep();                          // B-Rep-native: exact helical sweep
brep.Validate();
if (!plain.CanConvertTo(TargetRep.Brep))
    throw new Exception("unchamfered external threads are B-Rep-native");

var chamfered = Shape.ExternalThread(8, length: 12);   // default: 45° lead-in chamfers
Console.WriteLine(chamfered.Explain(TargetRep.Brep));  // names the chamfer as the blocker
if (chamfered.CanConvertTo(TargetRep.Brep))
    throw new Exception("chamfered threads must not silently drop their chamfers");
if (!chamfered.CanConvertTo(TargetRep.Implicit) || !chamfered.CanConvertTo(TargetRep.Mesh))
    throw new Exception("threads are implicit-native and meshable");

try
{
    chamfered.ToBrep();
    throw new Exception("expected ShapeConversionException");
}
catch (ShapeConversionException) { }
```

> [!NOTE]
> Because threads mesh through Surface Nets, ridge fidelity is set by
> `MeshQuality.SdfResolution` — the cell count along the scene's longest axis. Keep
> the cell size well below the pitch (the renders above use ~0.1 mm cells for a
> 1.25/1.0 mm pitch); at the default resolution a small thread in a large scene can
> average away entirely.
