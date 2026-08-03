---
title: "In the browser (WebAssembly)"
---

Every other page on this site shows a model rendered ahead of time and committed as a
PNG. This one doesn't. The panel below is the **actual geometry kernel** — B-Rep,
implicit and mesh — compiled to WebAssembly and running in your tab, drawing through
WebGL2. Drag to orbit, shift+drag to pan, scroll to zoom. Move a slider and the model is
rebuilt from scratch: a boolean, six drilled holes and a rim fillet, lowered to an exact
B-Rep, tessellated and rendered, right here.

<!-- The two ../../live/ links below point at the Blazor app, which
     .github/workflows/docs.yml publishes into _site/live/ AFTER the site is built, so no
     such file exists while Astro is running; docs/site/check-links.mjs lists /live/ as
     external for exactly that reason. TWO levels, because this page is served as
     /examples/web/ (a directory) rather than as /examples/web.html -- that is what the
     move from DocFX to Starlight changed, and it is why the link checker resolves
     references the way a browser does instead of trusting them. Do not "fix" this by
     writing an absolute /EngrCAD/live/ path: that bakes the repository name into the
     source and breaks the moment it is renamed or served from a user page. -->

<iframe src="../../live/?embed"
        title="EngrCAD's geometry kernel running in WebAssembly"
        loading="lazy"
        style="width:100%; height:26rem; border:1px solid #3a3d45; border-radius:6px; background:#1d1f24;">
</iframe>

<p style="margin-top:.5rem"><a href="../../live/">Open the demo full-page →</a></p>

If that panel is empty, your browser blocked WebAssembly or the demo hasn't been
deployed yet — see *Running it yourself* below.

## The model

This is the same `Shape` code you would write for the desktop viewer. Nothing about it
is web-specific, and that is the point:

```csharp run:wasm-flange
int holes = 6;
double fillet = 2;

var points = new List<Vector2d>(holes);
for (int i = 0; i < holes; i++)
{
    double angle = 2 * Math.PI * i / holes;
    points.Add(new Vector2d(28 * Math.Cos(angle), 28 * Math.Sin(angle)));
}

var body = Shape.Cylinder(40, 10) - Shape.Cylinder(14, 30);   // bored disc
var top = SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);
var flange = body.Drill(StandardHoles.Clearance(6), points, depth: 14, top)
                 .Fillet(fillet, s => s.PlanarFacesWithNormal(Vector3d.UnitZ));

// The figures this page quotes for the browser, asserted here against the desktop
// kernel: if the two ever stop agreeing, the docs build is what says so.
var mesh = flange.ToMesh();
if (mesh.FaceCount != 1560 || !mesh.IsClosed || Math.Abs(mesh.Volume() - 41573.0) > 0.1)
    throw new Exception($"flange changed: {mesh.FaceCount} tris, closed={mesh.IsClosed}, "
                        + $"volume={mesh.Volume():F1}");
```

A flange rather than a box on purpose: between them the boolean, face-splitting and
trimmed-tessellation paths dominate kernel time, so the timings the panel reports mean
something.

## Run any example on this site

Every other page's screenshot carries a **Run it in your browser** button. It swaps the
picture for this same kernel, building *that page's* example in your tab — the model is
not baked, it is built. 118 of the 132 rendered examples run; the rest are listed with
their reasons in [Writing examples](../writing-examples.md#why-an-example-might-not-run).

The screenshot stays the default and the viewer starts on a click, which is a payload
decision with the numbers below behind it: the runtime is megabytes, so a reader who
never clicks should not pay for it, and one who does gets it cached for every other
example they open. The documentation build compiles each snippet a second time against
exactly the assemblies this app ships and emits it as a **6 KB** assembly the page fetches
on demand.

## What it costs

Headless Edge on **win-x64**, best-of-five builds per page load, from **clean** publishes,
with the desktop control interleaved so all three rows share one machine state (a ratio
taken across sittings would be noise with units):

| | lower to B-Rep | tessellate | total | app payload (brotli) |
| --- | --- | --- | --- | --- |
| Desktop (native) | 107.6 ms | 111.5 ms | **219.1 ms** | — |
| WASM, no AOT | 2294.1 ms | 2326.6 ms | **4620.8 ms** (21.1×) | **2.84 MB** |
| WASM, AOT | 392.4 ms | 420.7 ms | **813.2 ms** (3.7×) | **7.03 MB** |

These supersede an earlier win-arm64 table (88.7 / 1677.3 / 385.2 ms at 1.9 / 4.6 MB) that
was measured on a different machine *and* against a much smaller kernel — the absolute
figures have moved with both, and the ratios have barely moved at all, which is the part
that was ever transferable. Four things worth taking from them:

- **Correctness is not in question.** The browser produces 1 560 triangles, a closed
  mesh and volume 41 573.0 mm³ — the same numbers as the desktop run in the same sitting,
  to the precision displayed. There is no WASM-specific code path in the kernel, and no
  trouble from the `ArrayPool` / `stackalloc` / `Vector<double>` machinery the performance
  mandates rely on. WebAssembly is a **speed tier, not a port**.
- **AOT buys 5.7× for 2.5× the download** (`wasm-tools` plus
  `-p:RunAOTCompilation=true`). Which side of that trade to take is a deployment decision,
  and this page still ships the *non*-AOT build — AOT compilation adds nearly four minutes
  to every documentation deploy, and the interactive examples are things you *orbit* far
  more than things you rebuild: the build happens once per click, the frames after it are
  the same WebGL2 the desktop draws. An interactive editor would choose the other way.
- **Time to a picture is not the row above**, and it is what a reader feels. The build is
  only the first half; the second is meshing, which the viewport does after. Measured
  through the live-example beacon (`?example=<id>&report`) on a warm runtime, from "the
  iframe started" to "a finished frame is on the canvas": an extrusion **369 ms**, a sheet
  metal bracket **461 ms**, a four-bar linkage **604 ms**, a B-Rep thread **1 075 ms**, a
  sectioned housing with SDF isolines **1 093 ms**, a helical gear **6 712 ms**. So the
  button is worth clicking and is worth *not* being automatic.
- **The kernel is now most of our own share of the download, and the runtime is still the
  rest.** The nine EngrCAD assemblies come to 2.87 MB uncompressed / 1.14 MB gzipped
  against a 2.84 MB brotli total, the largest single items being `System.Private.CoreLib`
  (0.51 MB brotli), `dotnet.native.wasm` (0.47 MB) and `EngrCAD.Modeling` (0.34 MB). The
  live examples add **58 KB brotli** to the app — 48.8 of it `System.Net.Http`, which was
  in the assembly list already and trimmed to nothing until something used it, and 7.6 the
  reflection surface in `System.Private.CoreLib` — plus 6 KB per example, fetched on demand.

## No GLSL in JavaScript

The rendering half is `src/EngrCAD.Web`, a Razor component library over WebGL2. Its one
architectural rule is that **`engrcad-gl.js` contains no policy**: it owns the GL
context, uploads the buffers it is handed and issues the draws it is told to. Shader
source comes from `EngrCAD.Viewer.Core`'s `ViewerShaders` — the *same strings* the
desktop window and the offscreen renderer compile — and camera framing, section
clipping and draw order all reach JavaScript as a plain frame description.

That is not tidiness. The desktop viewer already had this exact problem once: the
window and the offscreen renderer duplicated their shader and camera code and drifted
apart silently, which is why the shared render core exists. A WebGL client with its own
copy would be that mistake a third time, in a language where nothing would catch it.
The test of the rule is simple — if a question about what the scene *looks like* can be
answered by reading the JavaScript, the rule has been broken.

Geometry crosses the boundary as `byte[]`, because Blazor marshals that as a binary
array while `float[]` would go through JSON. That packing step is also the single
place doubles narrow to the float32 the GPU wants.

The camera is not forked either: the viewport's pointer handlers call the same
`CameraMath.DragOrbit` / `DragPan` / `DragZoom` / `WheelZoom` the desktop viewport calls,
so there is one answer to what dragging 100 pixels does and it is tested once. The only
legitimate difference is unit conversion — a DOM wheel event reports roughly 100 pixels
per notch and counts *down* as positive, the opposite of the desktop toolkit — which is a
browser fact, normalized at the edge, leaving the feel decision in shared code.

**A frame is a value.** `ViewportFrame.Build(...)` is the browser's counterpart to the
desktop's render callback and the offscreen renderer's draw — but it is a *pure
function*, so unlike either of those it can be asserted directly rather than compared by
eye. That is the point: those two drifted in the first place precisely because looking at
pixels was the only way to compare them.

## Running it yourself

```
dotnet run --project samples/EngrCAD.WebDemo
```

To reproduce what this page embeds — a publish served from a subdirectory:

```
dotnet publish samples/EngrCAD.WebDemo -c Release -o out
```

**Clear `obj`, `bin` and the output directory first.** Republishing a Blazor WASM app
over a previous publish can ship a runtime that disagrees with the assemblies, and it
fails at *run* time with a Mono interpreter abort rather than at build time — the publish
reports success throughout. A fresh CI checkout is immune; local iteration is not.

Then serve `out/wwwroot` from anywhere. The published app is **path-portable**: its
`index.html` uses a relative `<base href="./" />` and every asset reference the build
emits is relative, so it runs from a site root or from `/EngrCAD/live/` with no
rebuild and no repository name compiled into it.

## Status

The viewport draws: shaded geometry with per-part colours, feature edges, the ground grid
and axes, an orbit camera, per-part display modes (shaded / wireframe / translucent), the
global view style and the [matcap shading styles](viewer.md#matcap-shading), the tab
strip and model tree with visibility, click picking with selection sync and hover,
section planes — including multi-plane quarter/octant cuts, honoured by picking and by
the per-plane SDF isolines on each exposed cut face — the view cube with rotate-snap,
3D annotations, the measure tool, exploded views, animation playback, a properties panel
and a BOM view — all through the same shaders, the same `CameraMath`, the same
mode-precedence rule, the same clip rule and the same pose table the desktop window and
the headless renderer use. It also hosts every other page's live example
(`?example=<id>`), including the transport row for the ones an `animate:` fence renders as
a clip. Still to build: construction-tree rows and their rollback previews — the parity
ladder is in `todo.md`. The [Viewer](viewer.md) page describes what the desktop client
already does, which is the target.
