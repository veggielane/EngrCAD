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
± a radius about its centreline). Feeds and speeds are engineering inputs with stated defaults —
a transcribed chip-load catalogue is filed with the campaign, alongside `G2`/`G3` arcs from the
exact curved-profile tier, climb/conventional selection, helical entry, canned drilling cycles,
rest machining, and the material-removal animation (recorded stock states).
