# EngrCAD.Web

The web viewer: a Blazor WebAssembly component that renders EngrCAD scenes with WebGL2,
with **the whole kernel running in the browser**. No server, no round trip — a page
loads the geometry kernel, builds the model client-side, and draws it.

`samples/EngrCAD.WebDemo` is the host app.

## The one rule this library exists to keep

**No GLSL lives in JavaScript.** `wwwroot/engrcad-gl.js` owns the GL context, uploads the
buffers C# hands it, and issues draws — nothing else. Shader source comes from
`EngrCAD.Viewer.Core`'s `ViewerShaders`, the *same strings* the desktop window and the
offscreen renderer compile.

That is not tidiness. `RenderCore.cs` exists because the window and offscreen passes had
duplicated ~150 lines and drifted silently — the offscreen pass gained a scene-scaled
frustum the window never got. A WebGL client with its own copy of the shaders would be
that mistake a third time, in a language where nothing would catch it. So this project
references `EngrCAD.Viewer.Core` (UI-free: no Avalonia, no Silk.NET) and **not**
`EngrCAD.Viewer`, which would drag desktop bindings that cannot run in WASM.

The same rule applies to camera framing, section clipping and draw order: those decisions
live in .NET, in code shared with the desktop, and reach JavaScript as a plain
`FrameDescription`. `engrcad-gl.js` contains no policy.

The test is blunt: **if a question about what the scene looks like can be answered by
reading the JavaScript, the rule is broken.** When a frame needs something new, it
becomes data on `FrameDescription`/`DrawCall`, never a branch in the module — which is
why the background gradient's fullscreen triangle arrives as `Geometry = null, Count = 3`
rather than as a `if (isBackground)` in JS.

## `ViewportFrame`: a frame is a value

`ViewportFrame.Build(instances, camera, bounds, aspect, furniture)` is the browser's
counterpart to `ViewportControl.OnOpenGlRender` and `OffscreenRenderer.Draw` — and it is
a **pure function**, so unlike either of them it can be asserted directly. That is not a
stylistic preference: those two drifted precisely because the only way to compare them
was to look at pixels. `EngrCAD.Web.Tests` pins the draw order, the clear colour, the
furniture ranges, the per-instance matrices, and the neutral shader state as values.

Two decisions there are load-bearing and easy to "fix" wrongly:

- **Fills do not cull.** Both desktop passes leave face culling off, because a section
  plane exposes a solid's interior as *backfaces*, which the shared fragment shader
  shades as cut material via `gl_FrontFacing`. Culling would look fine today and break
  the section rung silently.
- **`uSectionCount` is never sent.** It is an `int` uniform, and the interop marshals
  every JSON number through `uniform1f`, which GL rejects on an int. The clip rule
  short-circuits on `uSectionEnabled` and an unset int uniform is already 0, so the
  neutral state must say nothing about it. A test asserts the absence.

## The camera is not forked either

`EngrCadViewport`'s pointer handlers call `CameraMath.DragOrbit`/`DragPan`/`DragZoom`/
`WheelZoom` — the same functions `ViewportControl` calls, moved into
`EngrCAD.Viewer.Core` for exactly this reason. There is one answer to what dragging 100
pixels does, and it is tested once.

The one legitimate difference is unit conversion: a DOM wheel event reports roughly 100
pixels per notch and counts *down* as positive, the opposite of the desktop toolkit's
convention. That is a browser fact, so `WheelNotches` normalizes it in the component and
the feel decision behind it stays in `CameraMath`.

## Geometry crosses the boundary as bytes

Blazor marshals `byte[]` as a binary array; `float[]` goes through JSON. For a mesh of a
few hundred thousand floats that is the difference between a copy and a stall, so
`WebGlContext` packs to bytes on the .NET side and JavaScript reinterprets them as
`Float32Array`/`Uint32Array`. `Vector3d` is doubles and GL wants float32, so the packing
step is also the single narrowing point.

## Measured: what running the kernel in a browser actually costs

A flange with a 6-hole bolt circle and a filleted rim — one boolean, six drilled holes,
a rim feature, so the boolean, face-splitting and trimmed-tessellation paths all run.
Headless Edge on win-arm64, best of three page loads each running best-of-five builds,
from **clean** publishes, with the desktop control **interleaved into the same window** —
this laptop returns 88.7 ms and 153.1 ms from runs of the same Release binary, so a ratio
taken across sittings measures the machine, not the runtime:

| | lower to B-Rep | tessellate | total | payload (brotli) |
| --- | --- | --- | --- | --- |
| Desktop (native) | 36.4 ms | 52.3 ms | **88.7 ms** | — |
| WASM, no AOT | 818.6 ms | 858.6 ms | **1677.3 ms** (18.9×) | **1.9 MB** |
| WASM, AOT | 178.8 ms | 206.4 ms | **385.2 ms** (4.3×) | **4.6 MB** |

Three things worth taking from that:

- **Correctness is not in question.** The browser produced 1 560 triangles, a closed
  mesh, and volume 41 573.0 — the same numbers as the desktop run, to the precision
  displayed. The kernel does not need a WASM-specific code path.
- **AOT is worth 4.4×, and costs 2.4× the payload.** `wasm-tools` plus
  `-p:RunAOTCompilation=true`. Which side of that trade to take is a product decision:
  anything interactive wants AOT; the docs deployment declines it because AOT
  compilation adds minutes to every documentation build and the embedded demo rebuilds
  only on slider release.
- **The kernel is a fifth of the download.** All nine EngrCAD assemblies come to 1.14 MB
  uncompressed and 0.41 MB brotli against a 1.9 MB total; the largest single items are
  `System.Private.CoreLib` (1.53 MB) and `dotnet.native.wasm` (1.43 MB) uncompressed.
  Trimming our own code could win a few hundred kilobytes at most.

Numbers come from the demo's `?report` self-check — see its comment for why in-page
timing has to be beaconed out rather than read from a DOM dump, and why the beacon is an
`<img>` rather than a `fetch`.

### Measure only from a CLEAN publish

Republishing over a previous publish without clearing `obj`, `bin` and the output
directory can ship a runtime that disagrees with the assemblies. It does not fail the
build. It first shows up as a *performance* regression — the no-AOT row measured 2 765 ms
on identical source that measures 1 677 ms clean — and then as a hard abort:

```
MONO interpreter: NIY encountered in method EngrCAD.Core.Vector2d:.cctor ()
[MONO] * Assertion: should not be reached at .../mono/mini/interp/interp.c:4135
```

The named method is a red herring; that cctor contains four `static readonly` struct
fields and nothing else. A clean publish fixes both symptoms. CI is immune (fresh
checkout into an empty workspace), so this is purely a local-iteration hazard — which is
worse, because local iteration is where the numbers come from.

## Deployment: the app is path-portable

`index.html` uses a relative `<base href="./" />`, and every asset reference the build
emits is already relative (`./_framework/...` in the rewritten import map,
`_framework/...` in the script tag). That one tag is the whole difference between an app
pinned to a site root and one that runs from any directory — so the docs site publishes
it straight into `_site/live/` with no `StaticWebAssetBasePath`, no post-publish rewrite,
and no repository name baked into the artifact. `.github/workflows/docs.yml` does it;
`docs/examples/web.md` embeds the result.

`?embed` drops the page heading and footer for that iframe; `?report` runs the timing
self-check described above.

## Proving it drew, headlessly

**A black canvas throws nothing**, so "no errors in the console" is not evidence that
anything rendered. `WebGlContext.CapturePixelsAsync` re-draws the last frame and reads it
back (the context has no `preserveDrawingBuffer`, so a separate interop call would find
the buffer already gone), and `CapturedPixels.CountBrighterThan` turns it into a number
.NET can assert. The demo's `?report` beacon carries that number out.

Headless Edge **does** have WebGL2 even with `--disable-gpu`: it falls back to ANGLE over
the D3D11 WARP renderer (`ANGLE (Microsoft, Microsoft Basic Render Driver, Direct3D11)`),
and `readPixels` works. Measured on the demo's flange at a 736x420 drawing buffer:
**33 961 pixels brighter than 90/255** (the background gradient tops out at 46 and the
ground grid at 74, so everything above that is lit geometry), and **115 081 pixels change**
when the camera is orbited through `CameraMath.DragOrbit` — a viewport that drew once and
then ignored the camera would pass the first check and fail the second. Rendered
side-by-side against `OffscreenRenderer` at the same size, camera and view style, the two
images agree; the desktop one is smoother only because it supersamples 2x.

Two traps in that loop, both already paid for: `--dump-dom` needs `--virtual-time-budget`
to reach the end of the work, and **under virtual time the clock does not advance during
synchronous computation**, so every in-page *timing* reads 0 ms (pixel counts are fine —
they are not clocks). And synthetic input: unlike the desktop toolkit, Blazor **does**
receive `dispatchEvent`-ed `PointerEvent`s, which is verifiable without pixels because
the handler's state reaches the DOM — the canvas cursor switches to `grabbing` on
`pointerdown` and back on `pointerup`.

## Status

Kernel-in-the-browser, the WebGL2 interop layer, the scene-to-frame layer and the orbit
camera are in place, and the demo draws real geometry. Still to build: feature edges,
per-part display modes and the global view style, model tree, picking, section planes and
their isolines, the view cube, and annotations. The parity ladder is in `todo.md`.

Notes for whoever takes the next rung:

- `RenderModes.Resolve` and `SectionClip` are already shared and waiting; the frame
  builder deliberately does **not** half-apply them, because a mode resolved and then
  ignored looks like support and is not. Every instance currently draws shaded.
- There is no ambient-occlusion bake in the browser. `uAmbientOcclusion` is 0, which
  makes the factor exactly 1.0 and *is* the AO-off shading rather than an approximation
  of it — the same property that lets the desktop stream bakes in behind a live scene.
- Frame-constant uniforms ride on `FrameDescription.Shared` so they travel once instead
  of once per instance. For a scene of any size that is most of the interop payload, and
  it is the first place to look if a large assembly feels heavy during a drag.
