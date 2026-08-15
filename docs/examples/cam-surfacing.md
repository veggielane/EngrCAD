---
title: "CNC: 3-axis surfacing"
---

Stage 3 of the CAM campaign — ball-nose finishing — is where the implicit engine pays
directly. **The cutter-location surface of a ball-nose tool IS the field's r-offset**: a ball
of radius *r* touches the part exactly when its centre is at distance *r* from the surface. So
both strategies read the shape's own SDF (`Shape.ToImplicit()`) instead of approximating an
offset mesh:

- **Raster** (parallel finishing) drops the tool at each sample by a **sphere trace** down the
  vertical ray to the r-isolevel. The field's 1-Lipschitz bound makes that gouge-free *by
  construction* — a step of `sdf − r` can never cross the offset surface, so a stalled trace
  stops **high** (stock left, never a gouge). Rows are serpentine, grid-anchored (the slicer's
  phase rule), and extend a tool radius past the part so the edge is machined.
- **Waterline** (constant-z contouring) is the observation that the CL contour at a centre
  plane **is the SDF's r-isolevel there** — `SdfContours.OnPlane`'s marching squares, chained
  into loops by its exact endpoint equality, then polished onto the isolevel by an *in-plane*
  Newton step (the correction must not leave the plane, or the pass stops being a waterline).
  On the steep walls waterline exists for, the path is exact to round-off.

```csharp run:cam-surfacing
// A dome on a plate, finished with a 4 mm ball-nose.
var part = Shape.Box(24, 24, 6) | Shape.Sphere(6).Translate(0, 0, 3);
var ball = new MillTool(Diameter: 4, StepDown: 2);

var raster = CncSurfacing.Raster(part, ball);
var waterline = CncSurfacing.Waterline(part, ball);
Console.WriteLine($"raster: {raster.Passes.Count} rows, cut length {raster.CutLength:0} mm; "
    + $"waterline: {waterline.Passes.Count} contours on "
    + $"{waterline.Passes.Select(p => p.Points[0].Z).Distinct().Count()} levels");

// The no-gouge claim is the field's own inequality: every ball CENTRE reads at least r.
var sdf = part.ToImplicit();
double worst = raster.Passes.SelectMany(p => p.Points)
    .Min(p => sdf.Evaluate(new Vector3d(p.X, p.Y, p.Z + ball.Radius)));
Console.WriteLine($"worst centre clearance {worst:0.#####} against r = {ball.Radius}");

// The scallop between rows is a chord identity — h = r − sqrt(r² − (s/2)²) — and the
// stepover for a stated finish is its exact inverse.
double s = ball.Stepover * ball.Diameter;
Console.WriteLine($"scallop at a {s} mm stepover: {CncSurfacing.ScallopHeight(ball.Radius, s):0.###} mm; "
    + $"a 0.01 mm finish needs {CncSurfacing.StepoverForScallop(ball.Radius, 0.01):0.###} mm");

// The same CncGcodeWriter carries surfacing passes — a move's meaning is its shape.
string gcode = CncGcodeWriter.Write([raster, waterline], safeZ: 15);
Console.WriteLine($"{gcode.Split('\n').Length} lines of G-code");
```

```csharp render:cam-waterline-rings
// The waterline contours in place: one CL loop per StepDown level, each an exact
// r-isolevel of the part's own field, drawn at its tip height over the translucent part.
var part = Shape.Box(24, 24, 6) | Shape.Sphere(6).Translate(0, 0, 3);
var ball = new MillTool(Diameter: 4, StepDown: 2);
var waterline = CncSurfacing.Waterline(part, ball);

Shape? rings = null;
foreach (var pass in waterline.Passes.Where(p => p.IsClosed))
{
    var pts = pass.Points.Select(p => new Vector2d(p.X, p.Y)).ToList();
    pts.Add(pts[0]);
    foreach (var r in Region2dBoolean.UnionAll([.. Region2dOffset.Stroke(pts, 0.4)]))
    {
        var sketch = Sketch.Polygon(r.Outer);
        foreach (var hole in r.Holes)
            sketch = sketch.WithHole(Sketch.Polygon(hole));
        var ring = Shape.Extrude(sketch, 0.3, SketchPlane.At(
            new Vector3d(0, 0, pass.Points[0].Z), Vector3d.UnitX, Vector3d.UnitY));
        rings = rings is null ? ring : rings | ring;
    }
}

var scene = new Scene();
scene.Add(new Part("part", part, Palette.Steel) { DisplayMode = DisplayMode.Translucent });
scene.Add(new Part("waterlines", rings!, Palette.Coral));
var camera = new CameraState(-Math.PI / 2 + 0.5, 0.55, 52, (0, 0, 1));
```

![Waterline CL contours stacked down the dome and the plate's walls — each ring the field's own r-isolevel at its level](images/cam-waterline-rings.png)

```csharp animate:cam-surfacing-tool frames:36
// The ball-nose riding its raster rows over the dome — the tool part's tip is at its
// origin, so following the pass points puts the tip on the cutter-location surface.
var part = Shape.Box(24, 24, 6) | Shape.Sphere(6).Translate(0, 0, 3);
var ball = new MillTool(Diameter: 4);
var raster = CncSurfacing.Raster(part, ball, sampleStep: 1);

var scene = new Scene();
scene.Add(new Part("part", part, Palette.Steel));
// The shank is slimmer than the ball on purpose: an equal-radius coaxial shank is
// tangent to the sphere along its equator, exactly the coincident curved input the
// B-Rep boolean refuses by name.
scene.Add(new Part("ball",
    Shape.Sphere(2).Translate(0, 0, 2) | Shape.Cylinder(1.4, 9).Translate(0, 0, 6.5),
    Palette.Coral));

// A handful of rows across the dome keeps the motion legible at 36 frames.
var rows = raster.Passes.Skip(raster.Passes.Count / 2 - 3).Take(6);
var waypoints = rows.SelectMany(p => p.Points).ToList();
var animation = new Animation(durationSeconds: 6)
    .With(PathTracks.Follow(scene, "ball", waypoints));
```

![A ball-nose cutter sweeping serpentine raster rows over the dome, its tip riding the field's own r-offset](images/cam-surfacing-tool.png)

The accuracy split is stated rather than averaged: a raster tip is exact to the trace
tolerance everywhere (a flat top reads the face's own height to 1e-6; the dome apex is touched
at its own height, because the global grid anchors a sample at exactly (0, 0)); a waterline
point is exact where the wall is steep — the case waterline is *for* — and honest to the
marching-squares crossing error where the contour crosses a near-horizontal region, where no
in-plane direction changes the field. Where the field is a correct-sign **lower bound** (a CSG
difference near its subtracted tool's fictitious faces), the r-isolevel lies farther from the
part than the true offset — stock left, never a gouge: the conservative direction, inherited
from the field contract rather than arranged.

Passes are in the **shape's own coordinates** (the G-code z word is the tool *tip*, the
machining convention), and the writer is the stage-2 `CncGcodeWriter` unchanged — a move's
meaning is its shape, so an XYZ-combined raster move cuts at the feed rate with nothing new.

**Not in stage 3** (each filed in the campaign): flat and bull-nose cutter-location surfaces
(v1 assumes a ball of the tool's radius), a raster direction other than X, linking rows
without a retract, adaptive stepover from local curvature, holder/shank collision checking,
rest machining, and HSM adaptive clearing (stage 4).
