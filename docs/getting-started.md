---
title: "Getting started"
---

## Install

EngrCAD targets **.NET 10**. The packages are currently published to a *local* NuGet
feed (nuget.org publishing is on the roadmap). From a clone of the repository:

```
dotnet pack EngrCAD.slnx -c Release -o C:\path\to\your\nuget-local
dotnet nuget add source C:\path\to\your\nuget-local --name engrcad-local
```

Then in your model project:

```
dotnet new console -n MyDesign
cd MyDesign
dotnet add package EngrCAD          # the kernel meta-package (Core..Modeling)
dotnet add package EngrCAD.Viewer   # the viewer library (pulls Avalonia; skip for headless use)
```

`EngrCAD` is a meta-package: referencing it brings in `EngrCAD.Core`, `EngrCAD.Mesh`,
`EngrCAD.Implicit`, `EngrCAD.BRep`, `EngrCAD.Interop`, `EngrCAD.Query`, and
`EngrCAD.Modeling`. The viewer is deliberately separate so headless consumers (CI,
export pipelines) do not pull UI dependencies.

## A first model

A design is a console program: build shapes, put them in a `Scene` as named `Part`s,
and hand the scene to the viewer via `EngrCad.Run`:

```csharp
using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;

return EngrCad.Run(args, BuildScene, "my bracket");

static Scene BuildScene()
{
    var plate = Shape.Extrude(Sketch.RoundedRectangle(60, 40, 8), 10)
        .Drill(StandardHoles.Counterbored(5), [new(-20, 0), new(20, 0)], depth: 14,
               SketchPlane.At((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY));

    var scene = new Scene();
    scene.Add(new Part("plate", plate, Palette.Steel));
    return scene;
}
```

`EngrCad.Run` gives every model program standard switches:

| Invocation | Behavior |
| --- | --- |
| *(no args)* | **Live modeling** — shows the scene and hot-reloads edits (see below). |
| `--view` | Static viewer window. |
| `--export part.step` | Headless STEP export (per B-Rep-representable part). |
| `--export part.stl` / `part.obj` | Headless mesh export, parts merged with transforms. |
| `--render out.png` | Headless offscreen render to a PNG — no window, CI-friendly. |
| `--render-style <style>` | View style for `--render`: `points`, `wireframe`, `shaded`, or `shaded-edges` (default). |
| `--section <x\|y\|z> <offset>` | Section plane for `--render`: clip beyond the offset along the axis, cut interiors shaded as cut material — e.g. `--section z 6`. |

### Configuring defaults

`EngrCad.Run` needs no configuration, but when a program wants host-level defaults —
window title, display mesh quality, `--render` image size, or a log sink — the
fluent builder sets them once (a `Scene` that chooses its own `MeshQuality` still
wins over the builder's default):

```csharp
return EngrCad.Configure()
    .WithTitle("my bracket")
    .WithQuality(new MeshQuality { SegmentsPerCircle = 48 })
    .WithRenderSize(1920, 1080)
    .WithViewStyle(ViewStyle.Shaded)          // --render default; CLI switches override
    .WithSection(SectionAxis.Z, 6)            // sectioned headless renders by default
    .Run(args, BuildScene);
```

The builder wraps a plain `EngrCadOptions` POCO, so generic-host apps can bind it as
`IOptions<EngrCadOptions>` and pass it via `EngrCad.Configure(options.Value)`;
`.WithLogger(logger)` / `.WithLoggerFactory(factory)` route status and error
reporting to any `ILogger` (the viewer takes
`Microsoft.Extensions.Logging.Abstractions` — abstractions only, so you still choose
the sink). Unconfigured, it writes to the console; `NullLogger.Instance` silences it.

## The live-modeling loop

The core experience is `dotnet watch` + hot reload — edit the model, save, and watch
the geometry update in place in under a second:

```
dotnet watch --project MyDesign
```

With no arguments `EngrCad.Run` calls `EngrCad.ShowLive`, which registers a metadata
update handler: every time `dotnet watch` patches the running process, the scene
factory is re-invoked and the new scene swapped in **with the camera untouched**. If
the factory throws, the last good scene stays and the error appears in the overlay.
Rude edits (signature changes) restart the process; the camera pose is persisted so
the view survives those too.

## Choosing a representation

Model with the one `Shape` vocabulary, then lower at the end:

```csharp
var body = Shape.Box(40, 30, 10) - Shape.Cylinder(4, 12).Translate(10, 8, 0);

BrepSolid    exact = body.ToBrep();      // exact solid: STEP export, mass properties
Sdf          field = body.ToImplicit();  // signed distance field: blends, lattices
HalfEdgeMesh mesh  = body.ToMesh();      // triangles: rendering, 3D printing
```

Not every operation exists in every engine; `body.Explain(TargetRep.Brep)` reports
node-by-node whether a lowering is *Native*, *Bridged* (through another
representation), or *Impossible* — see
[the three-representation story](examples/representations.md).

## Next steps

- Browse the [examples](examples/primitives.md) — every page's code is executed and rendered
  by the documentation build, so it is guaranteed current.
- Skim the [API reference](api/index.md) for the full surface.
