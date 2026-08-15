---
title: "CNC milling (2.5D)"
---

CAM stage 2 — pocketing, profiling and drilling, and like the slicer it is a thin layer over
machinery that already shipped: **pocket clearing is the inward-offset ladder** (`Region2dOffset`
rings one stepover apart, an island's grown boundary ridden like any other loop), **profiling is
one outline offset** by the tool radius (round joins — the path a tool centre physically rolls
around an outside corner), depth arrives in `StepDown` slices with the last clamped to the exact
stated depth, and **drilling ships as expanded peck moves** — plain `G0`/`G1`, so the same twin
decoder reads a drill cycle with nothing new.

**The verification oracle is the morphological opening.** A radius-*r* tool can reach exactly
`grow_r(shrink_r(region))` — internal corners come back rounded — so the union of the passes'
swept footprints (each centreline stroked at the tool diameter: the machined-stock simulation)
must equal the opening, and a rectangular pocket's unreachable corner residue is **closed form**:
`(4 − π)·r²`. The no-gouge claim is exact and point-by-point: every pass point at least the tool
radius from the region boundary.

```csharp run:cam-milling
// The outline comes from the solid itself: a plate sectioned at mid-height.
var plate = Shape.Box(40, 20, 6);
var region = plate.Section(SketchPlane.At(new Vector3d(0, 0, 0), Vector3d.UnitX, Vector3d.UnitY))
    .Single();

var tool = new MillTool(Diameter: 6, FeedRate: 600, StepDown: 2);
var pocket = CncMill.Pocket(region, tool, depth: 4);
var profile = CncMill.Profile(region, tool, depth: 6, ProfileSide.Outside,
    tabs: 3, tabHeight: 2, tabWidth: 8);
var drills = CncMill.Drill([new Vector2d(-15, 0), new Vector2d(15, 0)],
    new MillTool(3), depth: 8, peck: 3);

Console.WriteLine($"pocket: {pocket.Passes.Count} passes, cut {pocket.CutLength:0} mm");
Console.WriteLine($"profile: {profile.Passes.Count} passes (3 holding tabs on the last)");

// The corner residue a round tool cannot reach is closed form: (4 - pi) * r^2.
double r = tool.Radius;
var opening = Region2dBoolean.UnionAll(
    [.. Region2dOffset.Offset(region, -r).SelectMany(s => Region2dOffset.Offset(s, r))]);
Console.WriteLine($"unreachable corner residue: {region.Area - opening.Sum(o => o.Area):0.0000} "
    + $"mm2 vs (4-pi)r2 = {(4 - Math.PI) * r * r:0.0000}");

// One G-code program; the twin decoder recovers the cut length (rapids separated by the
// decoder's own Rapid flag, since feed state persists across G0 and G1 alike).
var decoded = GcodeReader.Read(CncGcodeWriter.Write([pocket, profile, drills]));
double cut = decoded.Moves.Where(m => !m.Rapid && m.XyLength > 0 && m.Feed == tool.FeedRate)
    .Sum(m => m.XyLength);
Console.WriteLine($"decoded cut length {cut:0} mm vs stated "
    + $"{pocket.CutLength + profile.CutLength:0} mm");
```

**Moves mean what their shape says.** Within a pass, an XY move cuts at the feed rate, a
straight-down move plunges at the plunge rate, a straight-up move retracts as a rapid — one
`MillPass` vocabulary carries pockets, tabbed profiles and pecked drills with no per-move
annotations. Holding tabs live on the **final** profile pass only, evenly spaced by arc length (a
stated convention, not rounding luck), each a vertical rise at the tab's own edge — never a
diagonal ramp that would leave the closing stretch part-cut.

`MillTool` carries the process numbers (feeds in mm/min — G-code's `F` verbatim), and its
`Stepover ≤ 0.5` is the value that **provably** covers the whole reachable area (each ring clears
± a radius about its centreline).

## The machined-stock simulation

`CncStock.Simulate` records the stock at N fractions of the total cut length — each state an
ordinary `Shape` a scene can show, export or measure. **The swept volume of a 2.5D pass is
closed form**: a tool at constant z occupies its stroked footprint from the cut level up
through the stock, and a vertical descent bores a disc (an inscribed 32-gon, so a drilled
state's volume is an *exact* prism — polyhedral mesh booleans are exact, which is what makes
the drill check an identity rather than a tolerance). The removal is subtracted as z **bands**
(one level to the next), so successive levels repeating one footprint never hand the boolean
two coincident side walls:

```csharp run:cam-stock
var stock = Shape.Box(30, 22, 8).Translate(0, 0, -4); // stock top at z = 0
var region = new Region2d(
    [new Vector2d(-9, -6), new Vector2d(9, -6), new Vector2d(9, 6), new Vector2d(-9, 6)]);
var tool = new MillTool(Diameter: 4, StepDown: 2);
var ops = new[]
{
    CncMill.Pocket(region, tool, depth: 3),
    CncMill.Drill([new Vector2d(-12, 8), new Vector2d(12, 8)], new MillTool(3), depth: 6),
};

foreach (var state in CncStock.Simulate(stock, ops, states: 5))
    Console.WriteLine($"fraction {state.Fraction:0.00}: cut {state.CutLength,6:0.0} mm, "
        + $"stock {state.Shape.ToMesh().Volume():0.0} mm3");

// The drilled bore is EXACT: an inscribed 32-gon prism, closed form to round-off.
double r = 1.5;
double bore = 32 / 2.0 * r * r * Math.Sin(2 * Math.PI / 32) * 6;
Console.WriteLine($"each bore removes exactly {bore:0.000000} mm3");
```

A state is a still or an export, deliberately not a live clip: a changing-geometry animation
has no matrices-only form (the pose-track contract is that only matrices move), so the states
are recorded data — the same reasoning that kept transient thermal playback off the
deformation uniform. **The TOOL, though, animates as an ordinary pose track**: matrices only,
riding the animation system with nothing new —

```csharp animate:cam-milling-tool frames:36
// The tool riding its own toolpath over the machined stock — matrices only.
var stock = Shape.Box(30, 22, 8).Translate(0, 0, -4);
var region = new Region2d(
    [new Vector2d(-9, -6), new Vector2d(9, -6), new Vector2d(9, 6), new Vector2d(-9, 6)]);
var pocket = CncMill.Pocket(region, new MillTool(Diameter: 4, StepDown: 2), depth: 3);
var machined = CncStock.Simulate(stock, [pocket], states: 2)[^1].Shape;

var scene = new Scene();
scene.Add(new Part("stock", machined));
scene.Add(new Part("tool", Shape.Cylinder(2, 14).Translate(0, 0, 7), Palette.Coral));

// The tool part is modeled with its TIP at the origin, so following the pass points
// puts the tip on the cut; a closed ring appends its own first point to complete.
var waypoints = pocket.Passes
    .SelectMany(p => p.IsClosed ? p.Points.Append(p.Points[0]) : p.Points).ToList();
var animation = new Animation(durationSeconds: 6)
    .With(PathTracks.Follow(scene, "tool", waypoints));
```

![A cutter tracing the pocket's ring ladder over the machined plate](images/cam-milling-tool.png)

`PathTracks.Follow` maps t to **arc length** (the explode-path rule — constant speed through
every corner, however unevenly the waypoints are spaced), hits each waypoint exactly at its own
parameter, and leaves every other instance's matrix untouched bit-for-bit.

Feeds and speeds are engineering inputs with stated defaults — a transcribed chip-load
catalogue is filed with the campaign, alongside `G2`/`G3` arcs from the exact curved-profile
tier, climb/conventional selection, helical entry, canned drilling cycles, rest machining,
and the 3-axis (surfacing) stock simulation — a raster row's swept volume is not a prism, so
`Simulate` refuses it by name today.
